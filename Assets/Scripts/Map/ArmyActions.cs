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
        public static event System.Action<ArmyData, UnitData> UnitDeployed;
        public const int CreateArmyApCost = 2;

        public static void DisbandEmptyArmiesOf(PlayerSetupData owner)
        {
            if (owner == null) return;
            using var mutation = VisionSystem.BeginMutation();
            foreach (ArmyData army in ArmyRegistry.AllForOwner(owner).Where(a => a.Members.Count == 0 && !a.IsPrison))
                DeleteArmyIfEmptied(army);
        }

        // Elimination releases prisoners under the existing capacity/no-activation-charge rule.
        // Ordinary roster transfer never releases a prisoner or changes its ownership.
        internal static void ReleasePrisoners(PlayerSetupData defeatedOwner, HexCoord citadelHex)
        {
            ArmyData prison = ArmyRegistry.At(citadelHex).FirstOrDefault(a => a.IsPrison && a.Owner == defeatedOwner);
            if (prison == null) return;
            using var mutation = VisionSystem.BeginMutation();
            foreach (UnitData hero in prison.Members.ToArray())
            {
                PlayerSetupData originalOwner = hero.CapturedFrom;
                ArmyData garrison = originalOwner?.CitadelHexQ.HasValue == true && originalOwner.CitadelHexR.HasValue
                    ? ArmyRegistry.FindGarrisonAt(new HexCoord(originalOwner.CitadelHexQ.Value, originalOwner.CitadelHexR.Value), originalOwner)
                    : null;
                if (garrison == null)
                {
                    RemoveMember(prison, hero);
                    continue;
                }
                if (!ArmyData.RosterFits(ArmyData.ProjectAdd(garrison.Members, hero, garrison.IsGarrison), garrison.IsGarrison))
                    continue;
                prison.Members.Remove(hero);
                hero.Owner = originalOwner; hero.IsPrisoner = false; hero.CapturedFrom = null;
                garrison.AddMemberSorted(hero);
                PublishRosterChange(prison, garrison, true);
            }
        }

        // Battle summons deliberately bypass normal capacity, as before. The command owns
        // canonical membership/reconciliation; the engine owns the tactical grid and summon rule.
        internal static bool AddBattleSummon(ArmyData army, UnitData unit)
        {
            if (army == null || army.Id < 0 || unit == null || unit.RuntimeId <= 0 || !unit.IsSummoned
                || unit.Owner != army.Owner || army.IsPrison || army.Members.Contains(unit)
                || ArmyRegistry.AllArmies().Any(a => a.Members.Any(m => m.RuntimeId == unit.RuntimeId))) return false;
            using var mutation = VisionSystem.BeginMutation();
            army.AddMemberSorted(unit);
            PublishRosterChange(army, army, true);
            return true;
        }

        public static void DeleteArmyIfEmptied(ArmyData army, bool force = false)
        {
            if (army == null || army.Members.Count > 0) return;
            using var mutation = VisionSystem.BeginMutation();
            if (!force && (army.IsCarrierContainer || army.IsPrison)) return;
            ArmyController controller = army.Controller;
            var actualHex = controller != null ? controller.CurrentHex : army.Hex;
            BuildingData building = BuildingRegistry.FindAt(army.Hex);
            if (!force && !army.WasCarrierWing && (controller == null || !controller.IsMoving)
                && building != null && building.Owner == army.Owner
                && (building.HasAbility(UnitAbilities.Barracks)
                    || army.IsAirfield && AviationRules.IsAirfieldBuilding(building, army.Owner)))
            { VisionSystem.NotifyContentChanged(army.Hex); return; }
            if (controller != null) controller.CancelMovement();
            army.Controller = null; army.PendingAirStrikePolicy = null;
            ArmyRegistry.Unregister(army, actualHex);
            // A composed operation may compensate this removal before publishing. Keep the
            // old marker available until commit; discarded notifications must not destroy it.
            if (controller != null)
                VisionSystem.PublishAfterMutation(() => Game.Core.UnityObjectLifetime.Destroy(controller.gameObject));
        }
        public static bool RemoveMember(ArmyData army, UnitData unit, bool cleanup = true, bool publish = true)
            => RemoveMembers(army, new[] { unit }, cleanup, publish) > 0;
        public static int RemoveMembers(ArmyData army, IEnumerable<UnitData> units, bool cleanup = true, bool publish = true)
        {
            if (army == null || units == null) return 0;
            using var mutation = VisionSystem.BeginMutation();
            var removed = units.Where(u => u != null && army.Members.Contains(u)).Distinct().ToArray();
            foreach (UnitData unit in removed) army.Members.Remove(unit);
            if (removed.Length == 0) return 0;
            army.NormalizeGarrisonOrder(); army.RefreshTransport();
            if (cleanup) foreach (UnitData unit in removed)
                StealthSystem.OnUnitRemoved(unit, deferNotifications: army.Controller != null && army.Controller.IsMoving);
            AviationActions.RefreshCarrierContainers(army);
            if (publish)
            {
                VisionSystem.RecomputeFor(army.Owner);
                // A moving army is indexed at its origin until relocation finishes. Publishing
                // that origin now would expose distant casualties as a false local sighting.
                if (army.Controller == null || !army.Controller.IsMoving)
                    VisionSystem.NotifyContentChanged(army.Hex);
            }
            return removed.Length;
        }
        public static void ClearMembers(ArmyData army, bool cleanup = true) => RemoveMembers(army, army?.Members.ToArray(), cleanup);
        internal static void ImprisonMember(UnitData hero, ArmyData source, ArmyData prison)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (hero == null || source == null || prison == null || !prison.IsPrison
                || !source.Members.Contains(hero) || prison.Members.Contains(hero)) return;
            source.Members.Remove(hero);
            hero.CapturedFrom = hero.Owner; hero.Owner = prison.Owner; hero.IsPrisoner = true;
            prison.Members.Add(hero); PublishRosterChange(source, prison, true);
        }
        public static void ChangeOwner(ArmyData army, PlayerSetupData owner)
        {
            if (army == null || army.Owner == owner) return;
            using var mutation = VisionSystem.BeginMutation();
            PlayerSetupData previous = army.Owner; army.SetOwner(owner);
            foreach (UnitData unit in army.Members)
            {
                unit.Owner = owner;
                foreach (ArmyData container in new[] { unit.CarrierAirfield, unit.CarrierFlightDeck })
                    if (container != null) ChangeOwner(container, owner);
            }
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            army.RefreshTransport(); AviationActions.RefreshCarrierContainers(army);
            VisionSystem.RecomputeFor(previous); VisionSystem.RecomputeFor(owner); VisionSystem.NotifyContentChanged(army.Hex);
        }
        public static bool CanReorderMembers(ArmyData army, IReadOnlyList<UnitData> order, out string failReason)
        {
            failReason = "Cannot reorder this army.";
            if (army == null || army.Id < 0 || army.IsPrison || order == null || order.Count != army.Members.Count
                || order.Distinct().Count() != order.Count || order.Any(u => !army.Members.Contains(u))) return false;
            return ValidateProjectedRoster(army, order, out failReason);
        }
        public static bool ReorderMembers(ArmyData army, IReadOnlyList<UnitData> order, out string failReason)
        {
            failReason = "This army is no longer available.";
            if (!ArmyRegistry.IsRegistered(army) || !CanReorderMembers(army, order, out failReason)) return false;
            if (army.Members.SequenceEqual(order)) return true;
            using var mutation = VisionSystem.BeginMutation();
            var final = order.ToArray(); army.Members.Clear(); army.Members.AddRange(final);
            PublishRosterChange(army, army, false); return true;
        }
        private static bool ValidateProjectedRoster(ArmyData army, IReadOnlyList<UnitData> roster, out string failure)
        {
            failure = "Invalid projected roster.";
            if (army == null || army.IsPrison || roster == null || roster.Any(u => u == null || u.IsPrisoner || u.Owner != army.Owner)
                || roster.Distinct().Count() != roster.Count) return false;
            bool regularSeen = false;
            foreach (UnitData unit in roster)
            {
                if (unit.IsHero && regularSeen) { failure = "Heroes must precede regular units."; return false; }
                if (!unit.IsHero) regularSeen = true;
            }
            if (army.IsGarrison && !roster.SequenceEqual(ArmyData.NormalizeRoster(roster)))
            { failure = "Garrison heroes are ordered by command rating."; return false; }
            if (!army.IsAirfield && !army.IsCarrierFlightDeck && !ArmyData.RosterFits(roster, army.IsGarrison))
            { failure = $"Not enough capacity in {army.Name}."; return false; }
            if (roster.Any(u => u.IsAviation) && (army.IsGarrison || roster.Any(u => !u.IsAviation))
                || (army.IsAirfield || army.IsCarrierFlightDeck) && roster.Any(u => !u.IsAviation))
            { failure = "Aircraft and ground units cannot share this container."; return false; }
            failure = null; return true;
        }

        public static ArmyData CreateArmy(PlayerSetupData owner, HexCoord hex, FactionCardCatalog catalog, HexSelectionController hexSelectionController)
            => CreateArmy(owner, hex, catalog, hexSelectionController, out _);

        public static ArmyData CreateArmy(PlayerSetupData owner, HexCoord hex, FactionCardCatalog catalog,
            HexSelectionController hexSelectionController, out string failReason)
        {
            using var mutation = VisionSystem.BeginMutation();
            failReason = null;
            if (owner == null || catalog == null || hexSelectionController?.Map == null)
            { failReason = "Cannot create an army here."; return null; }
            if (!hexSelectionController.Map.CanEnter(hex))
            { failReason = "Ground armies cannot occupy this terrain."; return null; }

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null)
            { failReason = "Your resources are unavailable. Try again."; return null; }
            if (!root.CanSpendActionPoints(CreateArmyApCost))
            { failReason = $"Need {CreateArmyApCost} AP to create an army."; return null; }
            root.SpendActionPoints(CreateArmyApCost);
            return RegisterNewArmy(owner, hex, catalog, hexSelectionController);
        }

        // Atomic form of creating an army with its first member: validate the final roster
        // before spending AP, then publish the populated army after the transfer completes.
        public static ArmyData CreateArmyWithMember(PlayerSetupData owner, HexCoord hex,
            FactionCardCatalog catalog, ArmyData source, UnitData member,
            HexSelectionController hexSelectionController, out string failReason)
            => CreateArmyWithMembers(owner, hex, catalog, source, new[] { member },
                hexSelectionController, out failReason);

        // One player-agnostic transaction: validate the complete roster before charging or
        // transferring; publish only the final populated army. Also owns single-member creation.
        public static ArmyData CreateArmyWithMembers(PlayerSetupData owner, HexCoord hex,
            FactionCardCatalog catalog, ArmyData source, IReadOnlyList<UnitData> members,
            HexSelectionController hexSelectionController, out string failReason)
        {
            using var mutation = VisionSystem.BeginMutation();
            failReason = null;
            if (hexSelectionController?.Map == null || !hexSelectionController.Map.CanEnter(hex))
            { failReason = "Ground armies cannot occupy this terrain."; return null; }
            if (owner == null || catalog == null || source == null || members == null || members.Count == 0
                || members.Any(u => u == null || u.IsAviation) || source.Owner != owner || source.IsPrison
                || !ArmyRegistry.IsRegistered(source) || !source.Hex.Equals(hex))
            { failReason = "Cannot create an army with these units."; return null; }
            ArmyData prospective = ArmyData.CreateVisualSnapshot();
            prospective.Owner = owner; prospective.Hex = hex;
            if (!CanTransferMembers(members, source, prospective, out failReason)) return null;
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null || !root.CanSpendActionPoints(CreateArmyApCost))
            { failReason = $"Need {CreateArmyApCost} AP to create an army."; return null; }
            members = members.ToArray();
            root.SpendActionPoints(CreateArmyApCost);
            foreach (UnitData unit in members) source.Members.Remove(unit);
            ArmyData army = RegisterNewArmy(owner, hex, catalog, hexSelectionController, members);
            source.RefreshTransport(); army.RefreshTransport();
            foreach (UnitData unit in members) army.MarkUnitActivationPaid(unit);
            hexSelectionController?.RestackArmiesOn(source.Hex, null);
            if (members.Any(u => AbilityParams.GetBestRecceRadius(u) > 0)) VisionSystem.RecomputeFor(owner);
            VisionSystem.NotifyContentChanged(hex);
            return army;
        }

        private static ArmyData RegisterNewArmy(PlayerSetupData owner, HexCoord hex,
            FactionCardCatalog catalog, HexSelectionController hexSelectionController,
            IReadOnlyList<UnitData> firstMembers = null)
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
            if (firstMembers != null)
                foreach (UnitData unit in firstMembers) army.AddMemberSorted(unit);
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
            using var mutation = VisionSystem.BeginMutation();
            if (!ArmyRegistry.IsRegistered(army) || !CanAffordActivation(army, root))
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
            {
                AviationActions.DetachCarrierWing(army);
                foreach (UnitData member in army.Members)
                    member.SortieLaunchPaid = true;
            }
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
            => EffectiveDeployApCost(card, null);

        // Physical deployment and a materialization preview share the final ability state.
        // Attaching RapidReaction before deployment has the same price as a native ability.
        public static int EffectiveDeployApCost(CardData card, IReadOnlyList<string> projectedAbilities)
        {
            if (card?.Definition == null)
                return 0;
            IReadOnlyList<string> abilities = projectedAbilities ?? EquipmentSystem.EffectiveAbilities(card);
            if (abilities.Contains(UnitAbilities.RapidReaction))
                return 0;
            return card.EffectivePlayApCost;
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

        // Read-only access to the exact validation used by deployment, including produced-card
        // costs, RapidReaction, capacity and airfield rules. No registry writes or resource spend.
        public static bool CanDeployUnitFromCard(CardData card, PlayerSetupData owner, ArmyData targetArmy,
            PlayerRoot root, HexSelectionController hexSelectionController)
        {
            return targetArmy != null && ValidateDeployUnitFromCard(card?.Definition, owner, targetArmy,
                targetArmy.Hex, null, root, hexSelectionController, card, out _, out _, out _);
        }

        // Domain entrypoint used by hand commands; presentation is optional.
        internal static bool TryDeployCard(CardData card, PlayerSetupData owner, ArmyData army,
            PlayerRoot root, HexMap map, HexSelectionController presentation, out string reason)
            => DeployUnitFromCardCore(card?.Definition, owner, army, army != null ? army.Hex : default,
                null, root, presentation, out _, out reason, null, card, map);

        internal static bool CanDeployCard(CardData card, PlayerSetupData owner, ArmyData army,
            PlayerRoot root, HexMap map, out string reason)
        {
            reason = "This army is no longer available.";
            return army != null && ArmyRegistry.IsRegistered(army)
                && ValidateDeployUnitFromCard(card?.Definition, owner, army, army.Hex, null, root,
                    null, card, out _, out _, out reason, map);
        }

        private static bool ValidateDeployUnitFromCard(CardDefinition definition, PlayerSetupData owner,
            ArmyData targetArmy, HexCoord deploymentHex, FactionCardCatalog newArmyCatalog,
            PlayerRoot root, HexSelectionController hexSelectionController, CardData sourceCard,
            out ArmyData destination, out int totalAp, out string failReason, HexMap map = null)
        {
            destination = targetArmy;
            totalAp = 0;
            failReason = null;
            bool creatingArmy = targetArmy == null;

            if (definition == null || owner == null || root == null
                || (creatingArmy && newArmyCatalog == null))
            {
                failReason = "Cannot deploy this card here.";
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
                failReason = root.Setup != owner || !object.ReferenceEquals(root, PlayerRootRegistry.FindFor(owner))
                    ? "Your resources are unavailable. Try again." : "Cannot deploy this card to this army.";
                return false;
            }

            // A prospective army is validation-only and never enters a registry. It lets the SAME
            // container/capacity rules below validate a fresh final roster without a special AI
            // approximation or a temporary empty shell.
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

            map = map ?? hexSelectionController?.Map;
            if (map == null || !map.CanEnter(deploymentHex))
            { failReason = "Units cannot deploy on this terrain."; return false; }

            // Ground deployment requires an OWN building with the card's declared ability.
            // Aviation remains Airfield-only.
            if (definition.isAviation)
            {
                if (!destination.IsAirfield || !AviationRules.IsOwnedAirfieldAt(deploymentHex, owner))
                {
                    failReason = "Deploy aircraft to your airfield first.";
                    return false;
                }
            }
            else if (!HasRequiredGroundDeploymentBuilding(owner, deploymentHex, definition))
            {
                failReason = $"Requires your building with {definition.requiredBuildingAbility} on this hex.";
                return false;
            }

            var prospectiveMember = UnitData.CreateProjection();
            prospectiveMember.IsAviation = definition.isAviation;
            if (!AviationRules.CanContain(destination, prospectiveMember))
            {
                failReason = definition.isAviation
                    ? "Aircraft require an airfield or air wing."
                    : "Ground units and heroes cannot join an air wing.";
                return false;
            }
            if (definition.isAviation && !destination.IsAirfield)
            {
                failReason = "Deploy aircraft to your airfield first.";
                return false;
            }
            if (destination.IsAirfield
                && AviationRules.FreeContainerCapacity(destination) < 1)
            {
                failReason = $"Airfield at {deploymentHex} is full.";
                return false;
            }
            if (!destination.IsAirfield && !destination.CanFitAdditionalCard(definition, sourceCard?.Equipment, sourceCard?.Mutator))
            {
                string targetName = creatingArmy ? "a fresh army" : destination.Name;
                failReason = $"Not enough capacity in {targetName} for {definition.displayName}.";
                return false;
            }

            bool alreadyPaidResources = sourceCard != null && sourceCard.ResearchProductionCreated;
            int deployAp = sourceCard != null ? EffectiveDeployApCost(sourceCard) : EffectiveDeployApCost(definition);
            totalAp = deployAp + (creatingArmy ? CreateArmyApCost : 0);
            if (!root.CanSpendActionPoints(totalAp))
            {
                failReason = creatingArmy
                    ? $"Need {totalAp} AP to create an army and deploy {definition.displayName}."
                    : $"Not enough AP to deploy {definition.displayName}.";
                return false;
            }
            if (!alreadyPaidResources && definition.resourceCost != null
                && !definition.resourceCost.CanAfford(root))
            {
                failReason = $"Not enough resources to deploy {definition.displayName}.";
                return false;
            }

            if (!new PaymentQuote(totalAp, alreadyPaidResources ? null : definition.resourceCost).CanDebit(root, out _))
            { failReason = "Invalid or unaffordable deployment cost."; return false; }
            return true;
        }

        private static bool DeployUnitFromCardCore(CardDefinition definition, PlayerSetupData owner,
            ArmyData targetArmy, HexCoord deploymentHex, FactionCardCatalog newArmyCatalog,
            PlayerRoot root, HexSelectionController hexSelectionController, out ArmyData resultingArmy,
            out string failReason, CardDefinition attachedEquipment, CardData sourceCard, HexMap map = null)
        {
            using var mutation = VisionSystem.BeginMutation();
            resultingArmy = targetArmy;
            if (targetArmy != null && (targetArmy.Id < 0 || !ArmyRegistry.IsRegistered(targetArmy)))
            { failReason = "Cannot deploy into a display snapshot."; return false; }
            if (!ValidateDeployUnitFromCard(definition, owner, targetArmy, deploymentHex, newArmyCatalog,
                    root, hexSelectionController, sourceCard, out ArmyData destination, out int totalAp, out failReason, map))
                return false;
            bool creatingArmy = targetArmy == null;
            deploymentHex = destination.Hex;
            bool alreadyPaidResources = sourceCard != null && sourceCard.ResearchProductionCreated;

            // SpawnUnit only materializes UnitData; it does not publish it to ArmyRegistry. Do it
            // before the irreversible debit so an unexpected spawn refusal cannot consume AP or
            // resources. Equipment mutates this unpublished body for the same reason.
            UnitData spawned = UnitData.FromDefinition(definition, owner,
                sourceCard != null ? sourceCard.Equipment : attachedEquipment, sourceCard?.Mutator);
            if (spawned == null)
            { failReason = $"Could not spawn {definition.displayName}."; return false; }
            var quote = new PaymentQuote(totalAp, alreadyPaidResources ? null : definition.resourceCost);
            if (!quote.TryDebit(root, out _, out _))
            { failReason = "The deployment cost is no longer affordable."; return false; }

            if (creatingArmy)
            {
                resultingArmy = RegisterNewArmy(owner, deploymentHex, newArmyCatalog,
                    hexSelectionController, new[] { spawned });
            }
            else
            {
                destination.AddMemberSorted(spawned);
                resultingArmy = destination;
            }

            resultingArmy.RefreshTransport();
            AviationActions.RefreshCarrierContainers(resultingArmy);
            // Visual failures cannot turn a committed deployment into a rejected hand play.
            VisionSystem.PublishAfterMutation(() => hexSelectionController?.RestackArmiesOn(deploymentHex, null));
            if (AbilityParams.GetBestRecceRadius(spawned) > 0)
                VisionSystem.RecomputeFor(owner);
            StealthSystem.RunChecksForNewVisionSource(resultingArmy, spawned);
            VisionSystem.NotifyContentChanged(deploymentHex);
            ArmyData deployedArmy = resultingArmy;
            VisionSystem.PublishAfterMutation(UnitDeployed, deployedArmy, spawned);
            return true;
        }

        public static bool TransferMember(UnitData unit, ArmyData source, ArmyData target,
            HexSelectionController hexSelectionController, out string failReason)
        {
            return TransferMembersAtomic(new[] { unit }, source, target, hexSelectionController, out failReason);
        }
        public static bool CanTransferMember(UnitData unit, ArmyData source, ArmyData target, out string failReason)
            => CanTransferMembers(new[] { unit }, source, target, out failReason);

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
                failReason = "Cannot transfer these units between these armies.";
                return false;
            }

            if (source.Owner != target.Owner
                || units.Any(u => u == null || u.IsPrisoner || u.Owner != source.Owner)
                || promoteToCommander != null && (!promoteToCommander.IsHero || !units.Contains(promoteToCommander)))
            { failReason = "Units must belong to the same owner and an editable army."; return false; }
            var distinct = units.Where(u => u != null).Distinct().ToList();
            if ((source.IsCarrierContainer || target.IsCarrierContainer) && !source.Hex.Equals(target.Hex))
            {
                failReason = "Aircraft must share the carrier's hex to board or leave.";
                return false;
            }
            if (distinct.Count != units.Count)
            {
                failReason = "Select each available unit only once.";
                return false;
            }
            foreach (UnitData unit in distinct)
            {
                if (!source.Members.Contains(unit) || target.Members.Contains(unit))
                {
                    failReason = $"{unit.Name} is not a member of {source.Name}.";
                    return false;
                }
                if (!AviationRules.CanContain(target, unit))
                {
                    failReason = "Aircraft and ground units cannot share an army.";
                    return false;
                }
            }

            var back = (displaced ?? System.Array.Empty<UnitData>()).ToList();
            if (back.Any(u => u == null || u.IsPrisoner || u.Owner != target.Owner) || back.Distinct().Count() != back.Count)
            { failReason = "Select each available unit only once."; return false; }
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
            if (!ValidateProjectedRoster(source, projectedSource, out failReason))
            {
                failReason = $"This transfer would leave {source.Name} over capacity.";
                return false;
            }
            var projectedTarget = target.Members.Where(u => !back.Contains(u)).ToList();
            foreach (UnitData unit in distinct)
                projectedTarget = ArmyData.ProjectAdd(projectedTarget, unit, target.IsGarrison);
            // An explicit promotion is a field-army choice; a garrison keeps its CommandRating order.
            if (promoteToCommander != null && !target.IsGarrison && projectedTarget.Remove(promoteToCommander))
                projectedTarget.Insert(0, promoteToCommander);
            if (!ValidateProjectedRoster(target, projectedTarget, out failReason))
            {
                failReason = $"Not enough capacity in {target.Name} for these units.";
                return false;
            }
            if (target.IsAirfield && distinct.Any(u => u.IsAviation) && !source.Hex.Equals(target.Hex))
            {
                failReason = $"Aircraft must be at the airfield at {target.Hex} to land there.";
                return false;
            }
            if ((target.IsAirfield || target.IsCarrierFlightDeck)
                && distinct.Count - back.Count > AviationRules.FreeContainerCapacity(target, source))
            {
                failReason = $"Airfield at {target.Hex} is full.";
                return false;
            }

            if ((source.IsAirfield || source.IsCarrierFlightDeck)
                && back.Count - distinct.Count > AviationRules.FreeContainerCapacity(source, target))
            { failReason = $"Airfield at {source.Hex} is full."; return false; }
            if (!requireChargeNow) return true;
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
                    failReason = targetRoot == null ? "Your resources are unavailable. Try again."
                        : $"Need {totalApCost} AP to transfer units into already activated armies.";
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
            using var mutation = VisionSystem.BeginMutation();
            if (!ArmyRegistry.IsRegistered(source) || !ArmyRegistry.IsRegistered(target))
            { failReason = "The source or destination army is no longer available."; return false; }
            if (promoteToCommander != null
                && (!promoteToCommander.IsHero || units == null || !units.Contains(promoteToCommander)))
            {
                failReason = "The new commander must be a hero among the selected units.";
                return false;
            }
            if (!CanTransferMembers(units, source, target, promoteToCommander, displaced,
                    out PlayerRoot targetRoot, out int totalApCost, out int totalEnergyCost, out failReason))
                return false;

            units = units.ToArray();
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
            if (target.IsCarrierFlightDeck && target.Controller == null)
                hexSelectionController?.CreateArmyMarker(target);
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
            if (target.IsAirfield) AviationActions.NotifyLandingCompleted(source, target, units[0]);
            if (source.IsAirfield && back.Count > 0) AviationActions.NotifyLandingCompleted(target, source, back[0]);
            return true;
        }

        // Aviation preparation is one container transaction, never a paid transfer followed by
        // a best-effort rollback. Zero-cost preparation declines an activated garrison join that
        // would owe AP; its caller can form a new wing without disturbing the ground army.
        public static bool TryUnloadAndBoardAircraft(ArmyData ground, ArmyData garrison,
            ArmyData airfield, IReadOnlyList<UnitData> aircraft,
            HexSelectionController hexSelection, out string failReason)
        {
            using var mutation = VisionSystem.BeginMutation();
            failReason = "Invalid air-wing preparation.";
            if (ground == null || garrison == null || airfield == null || aircraft == null
                || aircraft.Count == 0 || ground == garrison || ground == airfield
                || !ArmyRegistry.IsRegistered(ground) || !ArmyRegistry.IsRegistered(garrison) || !ArmyRegistry.IsRegistered(airfield)
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
            aircraft = aircraft.ToArray();
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
            if (unitA == null || unitB == null || unitA.IsAviation || unitB.IsAviation
                || armyA == null || armyB == null || armyA.IsAirfield || armyB.IsAirfield
                || AviationRules.IsAirArmy(armyA) || AviationRules.IsAirArmy(armyB))
            { failReason = "Aircraft cannot swap with ground units or heroes."; return false; }
            return CanExchangeMembers(new[] { unitA }, armyA, armyB, null, new[] { unitB }, out failReason);
        }
        public static bool SwapMembers(UnitData unitA, ArmyData armyA, UnitData unitB, ArmyData armyB,
            HexSelectionController hexSelectionController, out string failReason)
        {
            if (!CanSwapMembers(unitA, armyA, unitB, armyB, out failReason)) return false;
            return TransferMembersAtomic(new[] { unitA }, armyA, armyB, hexSelectionController,
                out failReason, displaced: new[] { unitB });
        }

        // Only completed roster transactions publish visibility. One content notification
        // per distinct hex, not one per transferred member; a Recce member additionally
        // changes the owner's vision footprint. This is gameplay ownership, not AI policy.
        private static void PublishRosterChange(ArmyData source, ArmyData target, bool recceTransferred)
        {
            using var mutation = VisionSystem.BeginMutation();
            source.RefreshTransport();
            target.RefreshTransport();
            AviationActions.RefreshCarrierContainers(source);
            AviationActions.RefreshCarrierContainers(target);
            // Empty/non-empty transitions add or remove vision even without Recce.
            VisionSystem.RecomputeFor(source.Owner);
            if (source.Owner != target.Owner) VisionSystem.RecomputeFor(target.Owner);
            VisionSystem.NotifyContentChanged(source.Hex);
            if (!target.Hex.Equals(source.Hex))
                VisionSystem.NotifyContentChanged(target.Hex);
        }
    }
}
