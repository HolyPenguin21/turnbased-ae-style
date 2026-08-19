using System.Collections.Generic;
using System.Linq;
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

        // Same rule CardHandUI.DeployUnit always enforced: spend AP/resources, spawn the unit,
        // add it to targetArmy, refresh the hex's marker stack. `failReason` is set (and the
        // call returns false) without spending anything on any of the checked failure paths —
        // callers that want a human-readable hint (CardHandUI) show it; AI callers just log it.
        public static bool DeployUnitFromCard(CardDefinition definition, PlayerSetupData owner, ArmyData targetArmy,
            PlayerRoot root, HexSelectionController hexSelectionController, out string failReason)
        {
            failReason = null;
            if (definition == null || owner == null || targetArmy == null || root == null || hexSelectionController == null)
            {
                failReason = "Invalid deploy request.";
                return false;
            }

            int apCost = EffectiveDeployApCost(definition);

            if (!root.CanSpendActionPoints(apCost))
            {
                failReason = $"Not enough action points to deploy {definition.displayName}.";
                return false;
            }
            if (!definition.resourceCost.CanAfford(root))
            {
                failReason = $"Not enough resources to deploy {definition.displayName}.";
                return false;
            }

            root.SpendActionPoints(apCost);
            definition.resourceCost.PayFrom(root);
            bool isHero = definition.cardType == CardType.Hero;
            var spawned = hexSelectionController.SpawnUnit(definition.displayName, owner, definition.moveMax,
                definition.activationApCost, isHero, definition.commandRating, definition.art, definition.grantedAbilities,
                definition.attack, definition.range, definition.hitPoints, definition.initiative, definition.fate,
                definition.defenseRating, definition.resistanceRating, definition.unitTypeTags, definition.detailArt,
                definition.apCost, definition.resourceCost);
            if (spawned == null)
            {
                failReason = $"Could not spawn {definition.displayName}.";
                return false;
            }

            targetArmy.AddMemberSorted(spawned);
            // The unit has no map presence of its own (see Game.Map.ArmyController) — only
            // targetArmy's own marker does, and this may be its first member ever (e.g. a
            // garrison that had zero units until now), so its visibility needs refreshing.
            hexSelectionController.RestackArmiesOn(targetArmy.Hex, null);
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
            if (!target.HasRoom)
            {
                failReason = $"{target.Name} is full — can't move {unit.Name} in.";
                return false;
            }

            var remaining = new List<UnitData>(source.Members);
            remaining.Remove(unit);
            if (ArmyData.ComputeCapacity(remaining, source.IsGarrison) < remaining.Count)
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
    }
}
