using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // Shared player-agnostic gameplay transactions for creating armies, deploying cards and
    // transferring members. Both human and AI callers use these exact mutations and costs.
    public static class ArmyActions
    {
        public const int CreateArmyApCost = 2;

        public static ArmyData CreateArmy(PlayerSetupData owner, HexCoord hex, FactionCardCatalog catalog, HexSelectionController hexSelectionController)
        {
            if (owner == null || catalog == null || hexSelectionController?.Map == null
                || !hexSelectionController.Map.CanEnter(hex))
                return null;

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null || !root.CanSpendActionPoints(CreateArmyApCost))
                return null;
            root.SpendActionPoints(CreateArmyApCost);
            return RegisterNewArmy(owner, hex, catalog, hexSelectionController);
        }

        // Atomic form of creating an army with its first member: validate the final roster
        // before spending AP, then publish the populated army after the transfer completes.
        public static ArmyData CreateArmyWithMember(PlayerSetupData owner, HexCoord hex,
            FactionCardCatalog catalog, ArmyData source, UnitData member,
            HexSelectionController hexSelectionController, out string failReason)
        {
            failReason = null;
            if (hexSelectionController?.Map == null || !hexSelectionController.Map.CanEnter(hex))
            { failReason = "Terrain cannot host a ground army."; return null; }
            if (owner == null || catalog == null || source == null || member == null
                || source.Owner != owner || source.IsPrison || member.IsAviation
                || !source.Hex.Equals(hex))
            {
                failReason = "Invalid create-army-with-member request.";
                return null;
            }

            ArmyData prospective = ArmyData.CreateVisualSnapshot();
            prospective.Hex = hex;
            prospective.Owner = owner;
            prospective.IsGarrison = false;
            if (!CanTransferMembers(new[] { member }, source, prospective, out failReason))
                return null;

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null || !root.CanSpendActionPoints(CreateArmyApCost))
            {
                failReason = $"Not enough action points to create an army ({CreateArmyApCost} AP needed).";
                return null;
            }

            root.SpendActionPoints(CreateArmyApCost);
            source.Members.Remove(member);
            ArmyData army = RegisterNewArmy(owner, hex, catalog, hexSelectionController, member);
            army.MarkUnitActivationPaid(member);
            hexSelectionController?.RestackArmiesOn(source.Hex, null);
            if (AbilityParams.GetBestRecceRadius(member) > 0)
                VisionSystem.RecomputeFor(owner);
            // RegisterNewArmy notified while the shell was still empty. The finalized
            // transfer must be observed as well; the old event cannot represent this roster.
            VisionSystem.NotifyContentChanged(hex);
            return army;
        }

        private static ArmyData RegisterNewArmy(PlayerSetupData owner, HexCoord hex,
            FactionCardCatalog catalog, HexSelectionController hexSelectionController,
            UnitData firstMember = null)
        {
            var takenNames = ArmyRegistry.AllForOwner(owner).Select(a => a.Name);
            var army = new ArmyData
            {
                Name = catalog.GetRandomArmyName(takenNames),
                Hex = hex,
                Owner = owner,
                IsGarrison = false,
            };
            // When the caller is creating a populated army, publish the FINAL roster to the
            // registry. Observers must never see a transient empty shell for one logical action.
            if (firstMember != null)
                army.AddMemberSorted(firstMember);
            ArmyRegistry.Register(army);
            hexSelectionController?.CreateArmyMarker(army);
            return army;
        }

        // THE activation gate and debit for an army's next action (move order or stationary air
        // strike), shared by the human order path, the route arrow and the AI. Costs come from
        // ArmyData.PendingActivationApCost/EnergyCost: a ground army once per turn, an air army
        // once per sortie (members already flagged SortieLaunchPaid are free). Nothing is spent
        // unless the whole cost is affordable.
        public static bool CanAffordActivation(ArmyData army, PlayerRoot root)
        {
            if (army == null)
                return false;
            if (!army.RequiresActivationPayment)
                return true;
            return root != null && root.CanSpendActionPoints(army.PendingActivationApCost)
                && root.GetResource(ResourceType.Energy) >= army.PendingActivationEnergyCost;
        }

        public static bool TryPayActivation(ArmyData army, PlayerRoot root)
        {
            if (!CanAffordActivation(army, root))
                return false;
            if (army.RequiresActivationPayment)
            {
                int ap = army.PendingActivationApCost;
                int energy = army.PendingActivationEnergyCost;
                if (ap > 0)
                    root.SpendActionPoints(ap);
                if (energy > 0)
                    root.AddResource(ResourceType.Energy, -energy);
            }
            if (AviationRules.IsAirArmy(army))
                foreach (UnitData member in army.Members)
                    member.SortieLaunchPaid = true;
            army.MarkActivated();
            return true;
        }

        public static int EffectiveDeployApCost(CardDefinition definition)
        {
            if (definition == null)
                return 0;
            return definition.grantedAbilities != null && definition.grantedAbilities.Contains(UnitAbilities.RapidReaction)
                ? 0 : definition.apCost;
        }

        public static int EffectiveDeployApCost(CardData card)
        {
            if (card?.Definition == null)
                return 0;
            CardDefinition definition = card.Definition;
            if (definition.grantedAbilities != null && definition.grantedAbilities.Contains(UnitAbilities.RapidReaction))
                return 0;
            return card.ResearchProductionCreated ? definition.activationApCost : definition.apCost;
        }

        // Pure gameplay legality primitive shared by human preview, AI planning and the physical
        // deployment transaction. Ground Unit/Hero deployment requires an owned building at the
        // destination carrying the card's declared required ability; an empty requirement is not
        // a wildcard. Aviation has its separate owned-airfield rule.
        public static bool HasRequiredGroundDeploymentBuilding(PlayerSetupData owner, HexCoord hex,
            CardDefinition definition)
        {
            return HasRequiredGroundDeploymentBuilding(owner, BuildingRegistry.FindAt(hex), definition);
        }

        // A projected infrastructure transaction uses the same deployment law as the live one.
        internal static bool HasRequiredGroundDeploymentBuilding(PlayerSetupData owner,
            BuildingData building, CardDefinition definition) =>
            owner != null && definition != null
            && !string.IsNullOrEmpty(definition.requiredBuildingAbility)
            && building != null && building.Owner == owner
            && building.HasAbility(definition.requiredBuildingAbility);

        // Existing-army deployment compatibility surface. All legality/payment/spawn logic lives
        // in DeployUnitFromCardCore below; UI and aviation keep their current call shape.
        public static bool DeployUnitFromCard(CardDefinition definition, PlayerSetupData owner, ArmyData targetArmy,
            PlayerRoot root, HexSelectionController hexSelectionController, out string failReason,
            CardDefinition attachedEquipment = null, CardData sourceCard = null)
        {
            return DeployUnitFromCardCore(definition, owner, targetArmy,
                targetArmy != null ? targetArmy.Hex : default, null, root,
                hexSelectionController, out _, out failReason, attachedEquipment, sourceCard);
        }

        // Atomic "create a fresh field army + deploy its first card" form. The same core validates
        // the complete final roster and complete AP/resource cost BEFORE anything is published or
        // charged. A failed operation therefore cannot leave a paid empty shell behind.
        public static bool DeployUnitFromCardToNewArmy(CardDefinition definition, PlayerSetupData owner,
            HexCoord hex, FactionCardCatalog catalog, PlayerRoot root,
            HexSelectionController hexSelectionController, out ArmyData createdArmy, out string failReason,
            CardDefinition attachedEquipment = null, CardData sourceCard = null)
        {
            return DeployUnitFromCardCore(definition, owner, null, hex, catalog,
                root, hexSelectionController, out createdArmy, out failReason,
                attachedEquipment, sourceCard);
        }

        private static bool DeployUnitFromCardCore(CardDefinition definition, PlayerSetupData owner,
            ArmyData targetArmy, HexCoord deploymentHex, FactionCardCatalog newArmyCatalog,
            PlayerRoot root, HexSelectionController hexSelectionController, out ArmyData resultingArmy,
            out string failReason, CardDefinition attachedEquipment, CardData sourceCard)
        {
            resultingArmy = targetArmy;
            failReason = null;
            bool creatingArmy = targetArmy == null;

            if (definition == null || owner == null || root == null || hexSelectionController == null
                || (creatingArmy && newArmyCatalog == null))
            {
                failReason = "Invalid deploy request.";
                return false;
            }
            // One authoritative mutation boundary for human, V1, V2 and aviation. A new field
            // army is only a valid ground Unit/Hero destination; aviation must still use Airfield.
            if ((definition.cardType != CardType.Unit && definition.cardType != CardType.Hero)
                || root.Setup != owner
                || !object.ReferenceEquals(root, PlayerRootRegistry.FindFor(owner))
                || (sourceCard != null && !object.ReferenceEquals(sourceCard.Definition, definition))
                || (!creatingArmy && (targetArmy.Owner != owner || targetArmy.IsPrison))
                || (creatingArmy && definition.isAviation))
            {
                failReason = "Card, owner, target army or resource owner is invalid for deployment.";
                return false;
            }

            // A prospective army is validation-only and never enters a registry. It lets the SAME
            // container/capacity rules below validate a fresh final roster without a special AI
            // approximation or a temporary empty shell.
            ArmyData destination = targetArmy;
            if (creatingArmy)
            {
                destination = ArmyData.CreateVisualSnapshot();
                destination.Owner = owner;
                destination.Hex = deploymentHex;
                destination.IsGarrison = false;
            }
            else
            {
                deploymentHex = targetArmy.Hex;
            }

            if (hexSelectionController.Map == null || !hexSelectionController.Map.CanEnter(deploymentHex))
            { failReason = "Terrain cannot host deployment."; return false; }

            // Ground deployment requires an OWN building with the card's declared ability.
            // Aviation remains Airfield-only.
            if (definition.isAviation)
            {
                if (!destination.IsAirfield || !AviationRules.IsOwnedAirfieldAt(deploymentHex, owner))
                {
                    failReason = "Aircraft must be deployed into an owned airfield first.";
                    return false;
                }
            }
            else if (!HasRequiredGroundDeploymentBuilding(owner, deploymentHex, definition))
            {
                failReason = $"{definition.displayName} requires your building with '{definition.requiredBuildingAbility}' at this hex.";
                return false;
            }

            var prospectiveMember = new UnitData { IsAviation = definition.isAviation };
            if (!AviationRules.CanContain(destination, prospectiveMember))
            {
                failReason = definition.isAviation
                    ? "Aircraft must be deployed into an airfield or an aviation army."
                    : "Ground units and heroes cannot join an aviation army.";
                return false;
            }
            if (definition.isAviation && !destination.IsAirfield)
            {
                failReason = "Aircraft must be deployed into an owned airfield first.";
                return false;
            }
            if (destination.IsAirfield
                && AviationRules.FreeAirfieldCapacity(deploymentHex, owner) < 1)
            {
                failReason = $"The airfield at {deploymentHex} is full.";
                return false;
            }
            if (!destination.IsAirfield && !destination.CanFitAdditionalCard(definition))
            {
                string targetName = creatingArmy ? "a fresh army" : destination.Name;
                failReason = $"{definition.displayName} would exceed {targetName}'s capacity after deployment.";
                return false;
            }

            bool alreadyPaidResources = sourceCard != null && sourceCard.ResearchProductionCreated;
            int deployAp = sourceCard != null ? EffectiveDeployApCost(sourceCard) : EffectiveDeployApCost(definition);
            int totalAp = deployAp + (creatingArmy ? CreateArmyApCost : 0);
            if (!root.CanSpendActionPoints(totalAp))
            {
                failReason = creatingArmy
                    ? $"Not enough action points to create an army and deploy {definition.displayName} ({totalAp} AP needed)."
                    : $"Not enough action points to deploy {definition.displayName}.";
                return false;
            }
            if (!alreadyPaidResources && definition.resourceCost != null
                && !definition.resourceCost.CanAfford(root))
            {
                failReason = $"Not enough resources to deploy {definition.displayName}.";
                return false;
            }

            // SpawnUnit only materializes UnitData; it does not publish it to ArmyRegistry. Do it
            // before the irreversible debit so an unexpected spawn refusal cannot consume AP or
            // resources. Equipment mutates this unpublished body for the same reason.
            bool isHero = definition.cardType == CardType.Hero;
            UnitData spawned = hexSelectionController.SpawnUnit(definition.displayName, owner, definition.moveMax,
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
            EquipmentSystem.ApplyAttachments(spawned,
                sourceCard != null ? sourceCard.Equipment : attachedEquipment, sourceCard?.Mutator);

            root.SpendActionPoints(totalAp);
            if (!alreadyPaidResources && definition.resourceCost != null)
                definition.resourceCost.PayFrom(root);

            if (creatingArmy)
            {
                resultingArmy = RegisterNewArmy(owner, deploymentHex, newArmyCatalog,
                    hexSelectionController, spawned);
            }
            else
            {
                destination.AddMemberSorted(spawned);
                resultingArmy = destination;
            }

            hexSelectionController.RestackArmiesOn(deploymentHex, null);
            if (AbilityParams.GetBestRecceRadius(spawned) > 0)
                VisionSystem.RecomputeFor(owner);
            StealthSystem.RunChecksForNewVisionSource(resultingArmy, spawned);
            VisionSystem.NotifyContentChanged(deploymentHex);
            return true;
        }

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
            bool promoteToAirArmy = source.IsAirfield && unit.IsAviation && !AviationRules.IsAirArmy(target)
                && !target.IsGarrison && !target.IsAirfield && target.Members.Count == 0;
            if (!promoteToAirArmy && !AviationRules.CanContain(target, unit))
            {
                failReason = unit.IsAviation
                    ? "Aircraft can only be moved between an airfield and an aviation army."
                    : "Ground units and heroes cannot join aviation.";
                return false;
            }
            if (target.IsAirfield && unit.IsAviation && !source.Hex.Equals(target.Hex))
            {
                failReason = $"{unit.Name} must be at the airfield to land there.";
                return false;
            }
            if (target.IsAirfield && AviationRules.FreeAirfieldCapacity(target.Hex, target.Owner, source) < 1)
            {
                failReason = $"The airfield at {target.Hex} is full.";
                return false;
            }
            if (!target.IsAirfield)
            {
                // Judged on the roster AddMemberSorted will actually produce (garrison: normalized).
                if (!ArmyData.RosterFits(ArmyData.ProjectAdd(target.Members, unit, target.IsGarrison),
                        target.IsGarrison))
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

            // Aviation never pays on a transfer: an aircraft that has not yet paid its sortie
            // launch (UnitData.SortieLaunchPaid) is charged by its stack's next flight action
            // (TryPayActivation), and storing an aircraft in its airfield is a landing.
            PlayerRoot targetRoot = null;
            bool requiresCharge = !unit.IsAviation && target.RequiresActivationCharge(unit);
            if (requiresCharge)
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
            source.NormalizeGarrisonOrder();
            target.AddMemberSorted(unit);
            if (target.IsAirfield)
                AviationRules.ResetAfterLanding(unit);
            AviationRules.SyncAirArmyShell(source, hexSelectionController);
            AviationRules.SyncAirArmyShell(target, hexSelectionController);
            if (requiresCharge)
                targetRoot?.SpendActionPoints(unit.ActivationApCost);
            target.MarkUnitActivationPaid(unit);
            hexSelectionController?.RestackArmiesOn(source.Hex, null);
            if (!target.Hex.Equals(source.Hex))
                hexSelectionController?.RestackArmiesOn(target.Hex, null);
            PublishRosterChange(source, target, AbilityParams.GetBestRecceRadius(unit) > 0);
            return true;
        }

        public static bool CanTransferMembers(IReadOnlyList<UnitData> units, ArmyData source,
            ArmyData target, out string failReason)
            => CanTransferMembers(units, source, target, null, null, out _, out _, out _, out failReason);

        // The exact check TransferMembersAtomic(units, source, target, …, promoteToCommander,
        // displaced) will run — for a caller that plans the exchange before committing to it.
        // `requireChargeNow` false: the same membership / container / capacity legality without the
        // current turn's activation charge — for a read-only projection of an exchange that will
        // happen on a later turn (the charge is then priced, not paid from today's AP).
        public static bool CanExchangeMembers(IReadOnlyList<UnitData> units, ArmyData source,
            ArmyData target, UnitData promoteToCommander, IReadOnlyList<UnitData> displaced,
            out string failReason, bool requireChargeNow = true)
            => CanTransferMembers(units, source, target, promoteToCommander, displaced,
                out _, out _, out _, out failReason, requireChargeNow);

        public static int TransferMembersApCost(IEnumerable<UnitData> units, ArmyData target)
        {
            if (units == null || target == null)
                return 0;
            return units.Where(u => u != null && target.RequiresActivationCharge(u))
                .Distinct().Sum(u => u.ActivationApCost);
        }

        // `promoteToCommander` — a hero of the batch that takes command of `target` on arrival
        // (placed first, so the target's capacity is judged under ITS Command).
        // `displaced` — members of `target` that move to `source` in the same operation (an
        // exchange); both sides are judged on their final rosters.
        private static bool CanTransferMembers(IReadOnlyList<UnitData> units, ArmyData source,
            ArmyData target, UnitData promoteToCommander, IReadOnlyList<UnitData> displaced,
            out PlayerRoot targetRoot, out int totalApCost, out int totalEnergyCost, out string failReason,
            bool requireChargeNow = true)
        {
            failReason = null;
            targetRoot = null;
            totalApCost = 0;
            totalEnergyCost = 0;
            if (units == null || units.Count == 0 || source == null || target == null
                || source == target || source.IsPrison || target.IsPrison)
            {
                failReason = "Invalid batch transfer request.";
                return false;
            }

            var distinct = units.Where(u => u != null).Distinct().ToList();
            if (distinct.Count != units.Count)
            {
                failReason = "Batch contains a null or duplicate member.";
                return false;
            }
            foreach (UnitData unit in distinct)
            {
                if (!source.Members.Contains(unit))
                {
                    failReason = $"{unit.Name} is not a member of {source.Name}.";
                    return false;
                }
                if (!AviationRules.CanContain(target, unit))
                {
                    failReason = "A batch transfer cannot mix incompatible aviation/ground containers.";
                    return false;
                }
            }

            var back = (displaced ?? System.Array.Empty<UnitData>()).Where(u => u != null).Distinct().ToList();
            foreach (UnitData unit in back)
            {
                if (!target.Members.Contains(unit) || distinct.Contains(unit)
                    || !AviationRules.CanContain(source, unit))
                {
                    failReason = $"{unit.Name} cannot be exchanged back from {target.Name}.";
                    return false;
                }
            }

            // Both final rosters are built by the same ProjectAdd the commit uses (a garrison is
            // normalized by CommandRating), so preview and commit cannot disagree.
            var projectedSource = source.Members.Where(u => !distinct.Contains(u)).ToList();
            foreach (UnitData unit in back)
                projectedSource = ArmyData.ProjectAdd(projectedSource, unit, source.IsGarrison);
            if (!ArmyData.RosterFits(projectedSource, source.IsGarrison))
            {
                failReason = $"The batch would leave {source.Name} without room for everyone else.";
                return false;
            }
            var projectedTarget = target.Members.Where(u => !back.Contains(u)).ToList();
            foreach (UnitData unit in distinct)
                projectedTarget = ArmyData.ProjectAdd(projectedTarget, unit, target.IsGarrison);
            // An explicit promotion is a field-army choice; a garrison keeps its CommandRating order.
            if (promoteToCommander != null && !target.IsGarrison && projectedTarget.Remove(promoteToCommander))
                projectedTarget.Insert(0, promoteToCommander);
            if (!target.IsAirfield && !ArmyData.RosterFits(projectedTarget, target.IsGarrison))
            {
                failReason = $"The batch wouldn't fit in {target.Name}.";
                return false;
            }
            if (target.IsAirfield && distinct.Any(u => u.IsAviation) && !source.Hex.Equals(target.Hex))
            {
                failReason = $"Aircraft must be at the airfield at {target.Hex} to land there.";
                return false;
            }
            if (target.IsAirfield
                && distinct.Count - back.Count > AviationRules.FreeAirfieldCapacity(target.Hex, target.Owner, source))
            {
                failReason = $"The airfield at {target.Hex} is full.";
                return false;
            }

            if (!requireChargeNow)
                return true;
            // Aviation is never charged on a transfer (see TransferMember): its launch is paid by
            // the stack's next flight action through TryPayActivation.
            var chargeable = distinct.Where(u => !u.IsAviation && target.RequiresActivationCharge(u)).ToList();
            var chargeableBack = back.Where(u => !u.IsAviation && source.RequiresActivationCharge(u)).ToList();
            if (chargeable.Count > 0 || chargeableBack.Count > 0)
            {
                totalApCost = TransferMembersApCost(chargeable, target)
                    + TransferMembersApCost(chargeableBack, source);
                targetRoot = PlayerRootRegistry.FindFor(target.Owner);
                if (targetRoot == null || !targetRoot.CanSpendActionPoints(totalApCost))
                {
                    failReason = $"Not enough action points to add the batch to {target.Name} "
                        + $"({totalApCost} AP needed — it already moved this turn).";
                    return false;
                }
            }
            return true;
        }

        // `promoteToCommander` (optional) — a hero of the batch that takes command of `target` in
        // the same atomic operation (the zero-AP TryReorderCommander a player could do right
        // after the move); capacity is checked under its Command.
        // `displaced` (optional) — members of `target` that go to `source` in the same operation,
        // an exchange of any size (SwapMembers is the one-for-one case); each side is checked on
        // its final roster and every newcomer to an already-activated army pays its activation.
        public static bool TransferMembersAtomic(IReadOnlyList<UnitData> units, ArmyData source,
            ArmyData target, HexSelectionController hexSelectionController, out string failReason,
            UnitData promoteToCommander = null, IReadOnlyList<UnitData> displaced = null)
        {
            if (promoteToCommander != null
                && (!promoteToCommander.IsHero || units == null || !units.Contains(promoteToCommander)))
            {
                failReason = "The promoted commander must be a hero of the batch.";
                return false;
            }
            if (!CanTransferMembers(units, source, target, promoteToCommander, displaced,
                    out PlayerRoot targetRoot, out int totalApCost, out int totalEnergyCost, out failReason))
                return false;

            List<UnitData> back = (displaced ?? System.Array.Empty<UnitData>())
                .Where(u => u != null).Distinct().ToList();
            foreach (UnitData unit in units)
                source.Members.Remove(unit);
            foreach (UnitData unit in back)
                target.Members.Remove(unit);
            foreach (UnitData unit in units)
                target.AddMemberSorted(unit);
            foreach (UnitData unit in back)
                source.AddMemberSorted(unit);
            source.NormalizeGarrisonOrder();
            if (promoteToCommander != null && !target.IsGarrison
                && target.Members.IndexOf(promoteToCommander) > 0)
                target.TryReorderCommander(promoteToCommander, out _);
            if (target.IsAirfield)
                foreach (UnitData unit in units)
                    AviationRules.ResetAfterLanding(unit);
            if (source.IsAirfield)
                foreach (UnitData unit in back)
                    AviationRules.ResetAfterLanding(unit);
            AviationRules.SyncAirArmyShell(source, hexSelectionController);
            AviationRules.SyncAirArmyShell(target, hexSelectionController);
            targetRoot?.SpendActionPoints(totalApCost);
            foreach (UnitData unit in units)
                target.MarkUnitActivationPaid(unit);
            foreach (UnitData unit in back)
                source.MarkUnitActivationPaid(unit);

            hexSelectionController?.RestackArmiesOn(source.Hex, null);
            if (!target.Hex.Equals(source.Hex))
                hexSelectionController?.RestackArmiesOn(target.Hex, null);
            PublishRosterChange(source, target,
                units.Concat(back).Any(u => AbilityParams.GetBestRecceRadius(u) > 0));
            return true;
        }

        // Aviation preparation is one container transaction, never a paid transfer followed by
        // a best-effort rollback. Zero-cost preparation declines an activated garrison join that
        // would owe AP; its caller can form a new wing without disturbing the ground army.
        public static bool TryUnloadAndBoardAircraft(ArmyData ground, ArmyData garrison,
            ArmyData airfield, IReadOnlyList<UnitData> aircraft,
            HexSelectionController hexSelection, out string failReason)
        {
            failReason = "Invalid air-wing preparation.";
            if (ground == null || garrison == null || airfield == null || aircraft == null
                || aircraft.Count == 0 || ground == garrison || ground == airfield
                || !garrison.IsGarrison || !airfield.IsAirfield || ground.IsGarrison
                || ground.IsPrison || ground.IsAirfield || AviationRules.IsAirArmy(ground)
                || ground.Owner != garrison.Owner || ground.Owner != airfield.Owner
                || !ground.Hex.Equals(garrison.Hex) || !ground.Hex.Equals(airfield.Hex)
                || aircraft.Any(u => u == null || !u.IsAviation || !airfield.Members.Contains(u))
                || aircraft.Distinct().Count() != aircraft.Count
                || ground.Members.Any(u => u == null || u.IsAviation || u.IsPrisoner))
                return false;
            var roster = ground.Members.ToList();
            if (!CanTransferMembers(roster, ground, garrison, null, null,
                    out _, out int ap, out _, out failReason))
                return false;
            if (ap != 0 || ArmyData.ComputeCapacity(aircraft, false) < aircraft.Count)
            {
                failReason = ap != 0 ? "Preparing this wing would charge ground activation."
                    : "The aircraft do not fit in the emptied shell.";
                return false;
            }
            ground.Members.Clear();
            foreach (UnitData unit in roster)
            {
                garrison.AddMemberSorted(unit);
                garrison.MarkUnitActivationPaid(unit);
            }
            foreach (UnitData unit in aircraft)
            {
                airfield.Members.Remove(unit);
                ground.AddMemberSorted(unit);
                ground.MarkUnitActivationPaid(unit);
            }
            AviationRules.SyncAirArmyShell(ground, hexSelection);
            hexSelection?.RestackArmiesOn(ground.Hex, null);
            PublishRosterChange(ground, garrison,
                roster.Concat(aircraft).Any(u => AbilityParams.GetBestRecceRadius(u) > 0));
            // The airfield also changed, on the same already-published hex.
            failReason = null;
            return true;
        }

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
            remainingA = ArmyData.ProjectAdd(remainingA, unitB, armyA.IsGarrison);
            if (!ArmyData.RosterFits(remainingA, armyA.IsGarrison))
            {
                failReason = $"{unitB.Name} wouldn't fit in {armyA.Name} once {unitA.Name} leaves.";
                return false;
            }
            var remainingB = new List<UnitData>(armyB.Members);
            remainingB.Remove(unitB);
            remainingB = ArmyData.ProjectAdd(remainingB, unitA, armyB.IsGarrison);
            if (!ArmyData.RosterFits(remainingB, armyB.IsGarrison))
            {
                failReason = $"{unitA.Name} wouldn't fit in {armyB.Name} once {unitB.Name} leaves.";
                return false;
            }

            bool chargeAIncoming = armyA.RequiresActivationCharge(unitB);
            bool chargeBIncoming = armyB.RequiresActivationCharge(unitA);
            PlayerRoot rootA = chargeAIncoming ? PlayerRootRegistry.FindFor(armyA.Owner) : null;
            PlayerRoot rootB = chargeBIncoming ? PlayerRootRegistry.FindFor(armyB.Owner) : null;
            if (chargeAIncoming && rootA == null)
            {
                failReason = $"Not enough action points to add {unitB.Name} to {armyA.Name} "
                    + $"({unitB.ActivationApCost} AP needed — it already moved this turn).";
                return false;
            }
            if (chargeBIncoming && rootB == null)
            {
                failReason = $"Not enough action points to add {unitA.Name} to {armyB.Name} "
                    + $"({unitA.ActivationApCost} AP needed — it already moved this turn).";
                return false;
            }
            if (rootA != null && rootA == rootB)
            {
                int combinedCost = (chargeAIncoming ? unitB.ActivationApCost : 0)
                    + (chargeBIncoming ? unitA.ActivationApCost : 0);
                if (!rootA.CanSpendActionPoints(combinedCost))
                {
                    failReason = $"Not enough action points for \"{armyA.Owner.Nickname}\" to swap {unitB.Name} "
                        + $"and {unitA.Name} between {armyA.Name} and {armyB.Name} ({combinedCost} AP needed).";
                    return false;
                }
            }
            else
            {
                if (chargeAIncoming && !rootA.CanSpendActionPoints(unitB.ActivationApCost))
                {
                    failReason = $"Not enough action points to add {unitB.Name} to {armyA.Name} "
                        + $"({unitB.ActivationApCost} AP needed — it already moved this turn).";
                    return false;
                }
                if (chargeBIncoming && !rootB.CanSpendActionPoints(unitA.ActivationApCost))
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

            bool chargeAIncoming = armyA.RequiresActivationCharge(unitB);
            bool chargeBIncoming = armyB.RequiresActivationCharge(unitA);
            PlayerRoot rootA = chargeAIncoming ? PlayerRootRegistry.FindFor(armyA.Owner) : null;
            PlayerRoot rootB = chargeBIncoming ? PlayerRootRegistry.FindFor(armyB.Owner) : null;

            armyA.Members.Remove(unitA);
            armyB.Members.Remove(unitB);
            armyA.AddMemberSorted(unitB);
            armyB.AddMemberSorted(unitA);
            if (rootA != null && rootA == rootB)
            {
                int combinedCost = (chargeAIncoming ? unitB.ActivationApCost : 0)
                    + (chargeBIncoming ? unitA.ActivationApCost : 0);
                rootA.SpendActionPoints(combinedCost);
            }
            else
            {
                rootA?.SpendActionPoints(unitB.ActivationApCost);
                rootB?.SpendActionPoints(unitA.ActivationApCost);
            }
            armyA.MarkUnitActivationPaid(unitB);
            armyB.MarkUnitActivationPaid(unitA);
            hexSelectionController?.RestackArmiesOn(armyA.Hex, null);
            if (!armyB.Hex.Equals(armyA.Hex))
                hexSelectionController?.RestackArmiesOn(armyB.Hex, null);
            PublishRosterChange(armyA, armyB,
                AbilityParams.GetBestRecceRadius(unitA) > 0 || AbilityParams.GetBestRecceRadius(unitB) > 0);
            return true;
        }

        // Only completed roster transactions publish visibility. One content notification
        // per distinct hex, not one per transferred member; a Recce member additionally
        // changes the owner's vision footprint. This is gameplay ownership, not AI policy.
        private static void PublishRosterChange(ArmyData source, ArmyData target, bool recceTransferred)
        {
            if (recceTransferred)
            {
                VisionSystem.RecomputeFor(source.Owner);
                if (source.Owner != target.Owner)
                    VisionSystem.RecomputeFor(target.Owner);
            }
            VisionSystem.NotifyContentChanged(source.Hex);
            if (!target.Hex.Equals(source.Hex))
                VisionSystem.NotifyContentChanged(target.Hex);
        }
    }
}
