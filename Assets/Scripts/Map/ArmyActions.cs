using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // Player-agnostic core of two actions that used to live inline inside UI click handlers
    // (ArmyViewerModalUI.CreateArmy, CardHandUI.DeployUnit) — pulled out so the AI turn
    // controller (see Game.Ai.AiTurnController) can perform the exact same actions a human's
    // click would, with the exact same AP/resource rules, instead of a parallel reimplementation.
    // Both UI call sites now just wrap these and turn a null/false result into their own hint
    // popup — nothing about their behaviour changes.
    public static class ArmyActions
    {
        public const int CreateArmyApCost = 2;

        // Same rule ArmyViewerModalUI.CreateArmy always enforced: a fresh, empty, non-garrison
        // army at `hex`, named from the catalog's own pool, costing CreateArmyApCost. Null if
        // `owner` can't afford it (or an argument is missing) — callers show their own hint.
        public static ArmyData CreateArmy(PlayerSetupData owner, HexCoord hex, FactionCardCatalog catalog, HexSelectionController hexSelectionController)
        {
            if (owner == null || catalog == null)
                return null;

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null || !root.CanSpendActionPoints(CreateArmyApCost))
                return null;
            root.SpendActionPoints(CreateArmyApCost);

            var takenNames = ArmyRegistry.AllForOwner(owner).Select(a => a.Name);
            var army = new ArmyData
            {
                Name = catalog.GetRandomArmyName(takenNames),
                Hex = hex,
                Owner = owner,
                IsGarrison = false,
            };
            ArmyRegistry.Register(army);
            hexSelectionController?.CreateArmyMarker(army);
            return army;
        }

        // UnitAbilities.RapidReaction: "The AP cost to deploy the unit is 0" — overrides the
        // card's own apCost outright rather than needing a spawned UnitData to check against
        // (there isn't one yet at this point). Exposed separately from DeployUnitFromCard so a
        // caller (see Game.Ai.AiTurnController.Decide) can check affordability BEFORE committing
        // to a decision that deploys this card, instead of finding out only after the fact.
        public static int EffectiveDeployApCost(CardDefinition definition)
        {
            if (definition == null)
                return 0;
            return definition.grantedAbilities != null && definition.grantedAbilities.Contains(UnitAbilities.RapidReaction)
                ? 0 : definition.apCost;
        }

        // Instance-aware variant: a Research/Production-created CardData pays activationApCost,
        // not apCost, when it is finally played (its Create attempt already covered the rest).
        // RapidReaction's 0-AP override still wins over both. A null card, or an ordinary
        // (non-produced) one, falls straight back to the CardDefinition rule above.
        public static int EffectiveDeployApCost(CardData card)
        {
            if (card?.Definition == null)
                return 0;
            CardDefinition definition = card.Definition;
            if (definition.grantedAbilities != null && definition.grantedAbilities.Contains(UnitAbilities.RapidReaction))
                return 0;
            return card.ResearchProductionCreated ? definition.activationApCost : definition.apCost;
        }

        // Same rule CardHandUI.DeployUnit always enforced: spend AP/resources, spawn the unit,
        // add it to targetArmy, refresh the hex's marker stack. `failReason` is set (and the
        // call returns false) without spending anything on any of the checked failure paths —
        // callers that want a human-readable hint (CardHandUI) show it; AI callers just log it.
        // sourceCard (optional): the hand CardData this deploy came from. Supplied by the human
        // hand paths so a Research/Production-created card pays activationApCost and skips its
        // (already-paid) ResourceCost. AI callers pass none — every AI card is an ordinary one,
        // so behaviour there is unchanged.
        public static bool DeployUnitFromCard(CardDefinition definition, PlayerSetupData owner, ArmyData targetArmy,
            PlayerRoot root, HexSelectionController hexSelectionController, out string failReason,
            CardDefinition attachedEquipment = null, CardData sourceCard = null)
        {
            failReason = null;
            if (definition == null || owner == null || targetArmy == null || root == null || hexSelectionController == null)
            {
                failReason = "Invalid deploy request.";
                return false;
            }
            if (!AviationRules.CanContain(targetArmy, new UnitData { IsAviation = definition.isAviation }))
            {
                failReason = definition.isAviation
                    ? "Aircraft must be deployed into an airfield or an aviation army."
                    : "Ground units and heroes cannot join an aviation army.";
                return false;
            }
            if (definition.isAviation && !targetArmy.IsAirfield)
            {
                failReason = "Aircraft must be deployed into an owned airfield first.";
                return false;
            }
            if (targetArmy.IsAirfield && targetArmy.Members.Count >= AviationRules.AirfieldCapacityAt(targetArmy.Hex, owner))
            {
                failReason = $"The airfield at {targetArmy.Hex} is full.";
                return false;
            }
            // Capacity must be evaluated against the roster AFTER this card joins. A hero can
            // legitimately turn a full no-hero 2/2 formation into (for example) a legal 3/5
            // formation, while a low-CommandRating hero can also make a previously roomy
            // no-hero garrison too small. Using the old targetArmy.HasRoom check before spawn
            // cannot represent either case and made AI planner feasibility disagree with the
            // canonical gameplay action.
            if (!targetArmy.IsAirfield)
            {
                int projectedCapacity = targetArmy.Capacity;
                if (definition.cardType == CardType.Hero && !targetArmy.Members.Any(m => m.IsHero))
                    projectedCapacity = definition.commandRating;
                if (projectedCapacity < targetArmy.Members.Count + 1)
                {
                    failReason = $"{definition.displayName} would exceed {targetArmy.Name}'s capacity after deployment.";
                    return false;
                }
            }

            bool alreadyPaidResources = sourceCard != null && sourceCard.ResearchProductionCreated;
            int apCost = sourceCard != null ? EffectiveDeployApCost(sourceCard) : EffectiveDeployApCost(definition);

            if (!root.CanSpendActionPoints(apCost))
            {
                failReason = $"Not enough action points to deploy {definition.displayName}.";
                return false;
            }
            if (!alreadyPaidResources && !definition.resourceCost.CanAfford(root))
            {
                failReason = $"Not enough resources to deploy {definition.displayName}.";
                return false;
            }

            root.SpendActionPoints(apCost);
            if (!alreadyPaidResources)
                definition.resourceCost.PayFrom(root);
            bool isHero = definition.cardType == CardType.Hero;
            var spawned = hexSelectionController.SpawnUnit(definition.displayName, owner, definition.moveMax,
                definition.activationApCost, isHero, definition.commandRating, definition.art, definition.grantedAbilities,
                definition.attack, definition.range, definition.hitPoints, definition.initiative, definition.fate,
                definition.defenseRating, definition.resistanceRating, definition.unitTypeTags, definition.detailArt,
                definition.apCost, definition.resourceCost, definition.isAviation, definition.launchEnergyCost,
                definition.turnsWithoutRefuel, definition.antiAirRadius, definition);
            if (spawned == null)
            {
                failReason = $"Could not spawn {definition.displayName}.";
                return false;
            }

            // Equipment attached to this card while it was still in hand (see EquipmentSystem /
            // the attach flow in CardHandUI) rides along onto the spawned unit now — its cost
            // was already paid at attach time, so this only applies the grant.
            if (attachedEquipment != null)
            {
                EquipmentSystem.Apply(attachedEquipment.equipment, spawned);
                spawned.Equipment = attachedEquipment;
            }

            targetArmy.AddMemberSorted(spawned);
            // The unit has no map presence of its own (see Game.Map.ArmyController) — only
            // targetArmy's own marker does, and this may be its first member ever (e.g. a
            // garrison that had zero units until now), so its visibility needs refreshing.
            hexSelectionController.RestackArmiesOn(targetArmy.Hex, null);

            // Stealth trigger B (see Game.Map.StealthSystem): a deploy that adds an r1sX
            // Recce source widens this army's vision — recompute it here (AddMemberSorted
            // alone doesn't) — then check every enemy hidden unit now inside `owner`'s
            // vision, base/citadel hexes included.
            if (AbilityParams.GetBestRecceRadius(spawned) > 0)
                VisionSystem.RecomputeFor(owner);
            StealthSystem.RunChecksForNewVisionSource(owner);
            return true;
        }

        // Same rule ArmyViewerModalUI.TryDropUnit's own drop-on-another-army branch always
        // enforced (pulled out here so Game.Ai.AiManagementPlanner-driven moves — garrison
        // overflow splits, lone-army consolidation — use the exact same rule a human's drag-drop
        // does, rather than a parallel reimplementation): target must have room, and `source`
        // must not be left holding more members than its own (possibly lower, if the moved unit
        // was its commanding hero) capacity can still hold. AP is only charged for the incoming
        // unit's own ActivationApCost, and only if `target` already spent its own
        // ActivationApCost this turn — an army that hasn't moved yet pays for everyone, this
        // unit included, on its own first move order as normal (see the project owner's own
        // report this guards against: reinforcing an already-moved army for free by dragging
        // units in from elsewhere).
        public static bool TransferMember(UnitData unit, ArmyData source, ArmyData target,
            HexSelectionController hexSelectionController, out string failReason)
        {
            failReason = null;
            if (unit == null || source == null || target == null || source == target || target.IsPrison)
            {
                failReason = "Invalid transfer request.";
                return false;
            }
            if (!source.Members.Contains(unit))
            {
                failReason = $"{unit.Name} is not a member of {source.Name}.";
                return false;
            }
            // "Create Army" from an airfield intentionally creates the usual empty field army.
            // Its first aircraft is the authoritative moment it becomes an air army; without
            // this conversion the UI can never form one through its normal drag workflow.
            bool promoteToAirArmy = source.IsAirfield && unit.IsAviation && !AviationRules.IsAirArmy(target)
                && !target.IsGarrison && !target.IsAirfield && target.Members.Count == 0;
            if (!promoteToAirArmy && !AviationRules.CanContain(target, unit))
            {
                failReason = unit.IsAviation
                    ? "Aircraft can only be moved between an airfield and an aviation army."
                    : "Ground units and heroes cannot join aviation.";
                return false;
            }
            if (target.IsAirfield && target.Members.Count >= AviationRules.AirfieldCapacityAt(target.Hex, target.Owner))
            {
                failReason = $"The airfield at {target.Hex} is full.";
                return false;
            }
            if (!target.IsAirfield)
            {
                // Canonical projected-roster capacity check. In particular, a hero joining a
                // currently-full 2/2 no-hero army may raise its capacity and therefore fit; the
                // old target.HasRoom pre-check rejected that legal transition before the hero's
                // CommandRating could be considered.
                var projectedTarget = new List<UnitData>(target.Members) { unit };
                if (ArmyData.ComputeCapacity(projectedTarget, target.IsGarrison) < projectedTarget.Count)
                {
                    failReason = $"{unit.Name} wouldn't fit in {target.Name} after the transfer.";
                    return false;
                }
            }

            if (!source.CanLeaveWithoutOvercrowding(unit))
            {
                failReason = $"Moving {unit.Name} out would leave {source.Name} without room for everyone else.";
                return false;
            }

            PlayerRoot targetRoot = null;
            if (target.HasActivatedThisTurn)
            {
                targetRoot = PlayerRootRegistry.FindFor(target.Owner);
                if (targetRoot == null || !targetRoot.CanSpendActionPoints(unit.ActivationApCost))
                {
                    failReason = $"Not enough action points to add {unit.Name} to {target.Name} "
                        + $"({unit.ActivationApCost} AP needed — it already moved this turn).";
                    return false;
                }
            }

            source.Members.Remove(unit);
            if (promoteToAirArmy)
                target.IsAirArmy = true;
            target.AddMemberSorted(unit);
            targetRoot?.SpendActionPoints(unit.ActivationApCost);
            // Neither army has a marker of its own that follows individual units around (see
            // ArmyController) — only whichever is each owner's visible representative on the
            // shared hex does, and this move can flip either army between empty and non-empty,
            // which changes that. source/target are always on the same hex in every call site
            // today, but restacking both costs nothing extra if that ever stops being true.
            hexSelectionController?.RestackArmiesOn(source.Hex, null);
            if (!target.Hex.Equals(source.Hex))
                hexSelectionController?.RestackArmiesOn(target.Hex, null);
            return true;
        }

        // A direct 1-for-1 exchange between two armies — the same net effect as two TransferMember
        // calls but without either one ever needing a free slot, since a straight swap never
        // changes either army's headcount. TransferMember alone can't express this: it always
        // requires the DESTINATION to already have room, so two armies that are BOTH already full
        // can never trade a single member through it at all (Game.Ai.GarrisonReorgTask's own
        // "garrison full, every field army full too" dead end — project owner's own 2026-08-20 call
        // to add this instead of leaving that a permanent no-op). Capacity is still re-checked on
        // both sides — a swap that drags a hero out (or in) changes that army's own Capacity, so
        // the resulting headcount still needs to actually fit once the trade lands.
        // Read-only preflight for SwapMembers — every check it performs before actually moving
        // anything, with no side effects, so callers deciding WHETHER to propose a swap (see
        // Game.Ai.AiDefencePlanner.TryStrengthenCandidate) can use the exact same feasibility
        // rule execution enforces, instead of a swap that looked useful at candidate-generation
        // time getting rejected here and leaving the AI stuck re-proposing it every turn.
        public static bool CanSwapMembers(UnitData unitA, ArmyData armyA, UnitData unitB, ArmyData armyB,
            out string failReason)
        {
            failReason = null;
            if (unitA == null || armyA == null || unitB == null || armyB == null || armyA == armyB
                || armyA.IsPrison || armyB.IsPrison)
            {
                failReason = "Invalid swap request.";
                return false;
            }
            // Same composition boundary TransferMember already enforces via AviationRules.
            // CanContain (see that method's own check) — SwapMembers never called it, so nothing
            // stopped a ground-army reorg swap from silently mixing an aircraft into a ground
            // army/garrison or a ground unit into an airfield's own stored container/an air army,
            // since none of those callers were ever written with aviation in mind. A flat refusal
            // rather than CanContain's own per-target rules (which assume a single unit joining a
            // STABLE target, not two simultaneous swaps) — nothing in this codebase has a legitimate
            // reason to swap a member between an aviation-composed army and anything else; aviation's
            // own launch/land flow never uses SwapMembers at all (see AviationActions.TryLaunch/
            // AiAviationSupport.LaunchRoutine).
            if (armyA.IsAirfield || AviationRules.IsAirArmy(armyA) || armyB.IsAirfield || AviationRules.IsAirArmy(armyB)
                || unitA.IsAviation || unitB.IsAviation)
            {
                failReason = "Aircraft and ground units/heroes can't be swapped between armies.";
                return false;
            }
            if (!armyA.Members.Contains(unitA))
            {
                failReason = $"{unitA.Name} is not a member of {armyA.Name}.";
                return false;
            }
            if (!armyB.Members.Contains(unitB))
            {
                failReason = $"{unitB.Name} is not a member of {armyB.Name}.";
                return false;
            }

            var remainingA = new List<UnitData>(armyA.Members);
            remainingA.Remove(unitA);
            remainingA.Add(unitB);
            if (ArmyData.ComputeCapacity(remainingA, armyA.IsGarrison) < remainingA.Count)
            {
                failReason = $"{unitB.Name} wouldn't fit in {armyA.Name} once {unitA.Name} leaves.";
                return false;
            }

            var remainingB = new List<UnitData>(armyB.Members);
            remainingB.Remove(unitB);
            remainingB.Add(unitA);
            if (ArmyData.ComputeCapacity(remainingB, armyB.IsGarrison) < remainingB.Count)
            {
                failReason = $"{unitA.Name} wouldn't fit in {armyB.Name} once {unitB.Name} leaves.";
                return false;
            }

            // Same "already-activated armies pay for what joins them" rule TransferMember
            // enforces — but unlike TransferMember (always two DIFFERENT owners' armies) a swap's
            // two armies are routinely the SAME AI player's own (e.g. AiDefencePlanner.
            // TryStrengthenCandidate trading between its own garrison and a field army), so rootA
            // and rootB can be the identical PlayerRoot. Checked and charged as ONE combined
            // requirement against that shared pool when they match — two independent
            // CanSpendActionPoints calls against the SAME starting AP would both pass even when
            // the pool can't actually cover both costs together (e.g. 3 AP, 2+2 needed), and
            // PlayerRoot.SpendActionPoints silently no-ops on an overdraft rather than throwing,
            // so SwapMembers would have gone on to mutate both rosters while only ever actually
            // paying for one side (project owner's own report).
            PlayerRoot rootA = armyA.HasActivatedThisTurn ? PlayerRootRegistry.FindFor(armyA.Owner) : null;
            PlayerRoot rootB = armyB.HasActivatedThisTurn ? PlayerRootRegistry.FindFor(armyB.Owner) : null;
            if (armyA.HasActivatedThisTurn && rootA == null)
            {
                failReason = $"Not enough action points to add {unitB.Name} to {armyA.Name} "
                    + $"({unitB.ActivationApCost} AP needed — it already moved this turn).";
                return false;
            }
            if (armyB.HasActivatedThisTurn && rootB == null)
            {
                failReason = $"Not enough action points to add {unitA.Name} to {armyB.Name} "
                    + $"({unitA.ActivationApCost} AP needed — it already moved this turn).";
                return false;
            }
            if (rootA != null && rootA == rootB)
            {
                int combinedCost = (armyA.HasActivatedThisTurn ? unitB.ActivationApCost : 0)
                    + (armyB.HasActivatedThisTurn ? unitA.ActivationApCost : 0);
                if (!rootA.CanSpendActionPoints(combinedCost))
                {
                    failReason = $"Not enough action points for \"{armyA.Owner.Nickname}\" to swap {unitB.Name} "
                        + $"and {unitA.Name} between {armyA.Name} and {armyB.Name} ({combinedCost} AP needed).";
                    return false;
                }
            }
            else
            {
                if (armyA.HasActivatedThisTurn && !rootA.CanSpendActionPoints(unitB.ActivationApCost))
                {
                    failReason = $"Not enough action points to add {unitB.Name} to {armyA.Name} "
                        + $"({unitB.ActivationApCost} AP needed — it already moved this turn).";
                    return false;
                }
                if (armyB.HasActivatedThisTurn && !rootB.CanSpendActionPoints(unitA.ActivationApCost))
                {
                    failReason = $"Not enough action points to add {unitA.Name} to {armyB.Name} "
                        + $"({unitA.ActivationApCost} AP needed — it already moved this turn).";
                    return false;
                }
            }

            return true;
        }

        public static bool SwapMembers(UnitData unitA, ArmyData armyA, UnitData unitB, ArmyData armyB,
            HexSelectionController hexSelectionController, out string failReason)
        {
            if (!CanSwapMembers(unitA, armyA, unitB, armyB, out failReason))
                return false;

            PlayerRoot rootA = armyA.HasActivatedThisTurn ? PlayerRootRegistry.FindFor(armyA.Owner) : null;
            PlayerRoot rootB = armyB.HasActivatedThisTurn ? PlayerRootRegistry.FindFor(armyB.Owner) : null;

            armyA.Members.Remove(unitA);
            armyB.Members.Remove(unitB);
            armyA.AddMemberSorted(unitB);
            armyB.AddMemberSorted(unitA);
            // Same-owner combined charge — see CanSwapMembers' own comment. Two separate
            // SpendActionPoints calls against the SAME root would double-count the pool's
            // headroom the same way two separate CanSpendActionPoints checks did.
            if (rootA != null && rootA == rootB)
            {
                int combinedCost = (armyA.HasActivatedThisTurn ? unitB.ActivationApCost : 0)
                    + (armyB.HasActivatedThisTurn ? unitA.ActivationApCost : 0);
                rootA.SpendActionPoints(combinedCost);
            }
            else
            {
                rootA?.SpendActionPoints(unitB.ActivationApCost);
                rootB?.SpendActionPoints(unitA.ActivationApCost);
            }

            hexSelectionController?.RestackArmiesOn(armyA.Hex, null);
            if (!armyB.Hex.Equals(armyA.Hex))
                hexSelectionController?.RestackArmiesOn(armyB.Hex, null);
            return true;
        }
    }
}
