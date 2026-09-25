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
            if (owner == null || catalog == null)
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
            if (owner == null || definition == null
                || string.IsNullOrEmpty(definition.requiredBuildingAbility))
                return false;
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner == owner
                && building.HasAbility(definition.requiredBuildingAbility);
        }

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
                && destination.Members.Count >= AviationRules.AirfieldCapacityAt(deploymentHex, owner))
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
            if (attachedEquipment != null)
            {
                EquipmentSystem.Apply(attachedEquipment.equipment, spawned);
                spawned.Equipment = attachedEquipment;
            }

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
            if (target.IsAirfield && target.Members.Count >= AviationRules.AirfieldCapacityAt(target.Hex, target.Owner))
            {
                failReason = $"The airfield at {target.Hex} is full.";
                return false;
            }
            if (!target.IsAirfield)
            {
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
            bool requiresCharge = target.RequiresActivationCharge(unit);
            int energyCost = requiresCharge && unit.IsAviation ? unit.LaunchEnergyCost : 0;
            if (requiresCharge)
            {
                targetRoot = PlayerRootRegistry.FindFor(target.Owner);
                if (targetRoot == null || !targetRoot.CanSpendActionPoints(unit.ActivationApCost)
                    || targetRoot.GetResource(ResourceType.Energy) < energyCost)
                {
                    failReason = $"Not enough action points to add {unit.Name} to {target.Name} "
                        + $"({unit.ActivationApCost} AP, {energyCost} Energy needed — it already moved this turn).";
                    return false;
                }
            }

            source.Members.Remove(unit);
            if (promoteToAirArmy)
            {
                target.IsAirArmy = true;
                hexSelectionController?.RefreshArmyAirLook(target);
            }
            target.AddMemberSorted(unit);
            if (requiresCharge)
            {
                targetRoot?.SpendActionPoints(unit.ActivationApCost);
                if (energyCost > 0)
                    targetRoot?.AddResource(ResourceType.Energy, -energyCost);
            }
            target.MarkUnitActivationPaid(unit);
            hexSelectionController?.RestackArmiesOn(source.Hex, null);
            if (!target.Hex.Equals(source.Hex))
                hexSelectionController?.RestackArmiesOn(target.Hex, null);
            PublishRosterChange(source, target, AbilityParams.GetBestRecceRadius(unit) > 0);
            return true;
        }

        public static bool CanTransferMembers(IReadOnlyList<UnitData> units, ArmyData source,
            ArmyData target, out string failReason)
            => CanTransferMembers(units, source, target, null, out _, out _, out _, out failReason);

        public static int TransferMembersApCost(IEnumerable<UnitData> units, ArmyData target)
        {
            if (units == null || target == null)
                return 0;
            return units.Where(u => u != null && target.RequiresActivationCharge(u))
                .Distinct().Sum(u => u.ActivationApCost);
        }

        // `promoteToCommander` — a hero of the batch that takes command of `target` on arrival
        // (placed first, so the target's capacity is judged under ITS Command).
        private static bool CanTransferMembers(IReadOnlyList<UnitData> units, ArmyData source,
            ArmyData target, UnitData promoteToCommander, out PlayerRoot targetRoot,
            out int totalApCost, out int totalEnergyCost, out string failReason)
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

            var projectedSource = source.Members.Where(u => !distinct.Contains(u)).ToList();
            if (ArmyData.ComputeCapacity(projectedSource, source.IsGarrison) < projectedSource.Count)
            {
                failReason = $"The batch would leave {source.Name} without room for everyone else.";
                return false;
            }
            var projectedTarget = new List<UnitData>(target.Members);
            foreach (UnitData unit in distinct)
            {
                int index = unit == promoteToCommander ? 0
                    : unit.IsHero ? projectedTarget.Count(u => u.IsHero) : projectedTarget.Count;
                projectedTarget.Insert(index, unit);
            }
            if (!target.IsAirfield
                && ArmyData.ComputeCapacity(projectedTarget, target.IsGarrison) < projectedTarget.Count)
            {
                failReason = $"The batch wouldn't fit in {target.Name}.";
                return false;
            }
            if (target.IsAirfield
                && projectedTarget.Count > AviationRules.AirfieldCapacityAt(target.Hex, target.Owner))
            {
                failReason = $"The airfield at {target.Hex} is full.";
                return false;
            }

            var chargeable = distinct.Where(target.RequiresActivationCharge).ToList();
            if (chargeable.Count > 0)
            {
                totalApCost = TransferMembersApCost(chargeable, target);
                totalEnergyCost = chargeable.Where(u => u.IsAviation).Sum(u => u.LaunchEnergyCost);
                targetRoot = PlayerRootRegistry.FindFor(target.Owner);
                if (targetRoot == null || !targetRoot.CanSpendActionPoints(totalApCost)
                    || targetRoot.GetResource(ResourceType.Energy) < totalEnergyCost)
                {
                    failReason = $"Not enough action points to add the batch to {target.Name} "
                        + $"({totalApCost} AP, {totalEnergyCost} Energy needed — it already moved this turn).";
                    return false;
                }
            }
            return true;
        }

        // `promoteToCommander` (optional) — a hero of the batch that takes command of `target` in
        // the same atomic operation (the zero-AP TryReorderCommander a player could do right
        // after the move); capacity is checked under its Command.
        public static bool TransferMembersAtomic(IReadOnlyList<UnitData> units, ArmyData source,
            ArmyData target, HexSelectionController hexSelectionController, out string failReason,
            UnitData promoteToCommander = null)
        {
            if (promoteToCommander != null
                && (!promoteToCommander.IsHero || units == null || !units.Contains(promoteToCommander)))
            {
                failReason = "The promoted commander must be a hero of the batch.";
                return false;
            }
            if (!CanTransferMembers(units, source, target, promoteToCommander,
                    out PlayerRoot targetRoot, out int totalApCost, out int totalEnergyCost, out failReason))
                return false;

            foreach (UnitData unit in units)
                source.Members.Remove(unit);
            foreach (UnitData unit in units)
                target.AddMemberSorted(unit);
            if (promoteToCommander != null && target.Members.IndexOf(promoteToCommander) > 0)
                target.TryReorderCommander(promoteToCommander, out _);
            targetRoot?.SpendActionPoints(totalApCost);
            if (totalEnergyCost > 0)
                targetRoot?.AddResource(ResourceType.Energy, -totalEnergyCost);
            foreach (UnitData unit in units)
                target.MarkUnitActivationPaid(unit);

            hexSelectionController?.RestackArmiesOn(source.Hex, null);
            if (!target.Hex.Equals(source.Hex))
                hexSelectionController?.RestackArmiesOn(target.Hex, null);
            PublishRosterChange(source, target, units.Any(u => AbilityParams.GetBestRecceRadius(u) > 0));
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
