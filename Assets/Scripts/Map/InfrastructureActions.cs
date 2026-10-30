using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Units;
using Game.Terrain;
using UnityEngine;

namespace Game.Map
{
    // Shared transaction result for human and AI infrastructure actions.
    public readonly struct InfrastructureBuildOutcome
    {
        public readonly bool Ok;
        public readonly BuildingData Building;
        public readonly int SlotIndex;
        public readonly int ApSpent;
        public readonly string FailReason;

        private InfrastructureBuildOutcome(bool ok, BuildingData b, int slot, int apSpent, string fail)
        {
            Ok = ok; Building = b; SlotIndex = slot; ApSpent = apSpent; FailReason = fail;
        }

        public static InfrastructureBuildOutcome Success(BuildingData b, int slot, int apSpent) =>
            new InfrastructureBuildOutcome(true, b, slot, apSpent, null);
        public static InfrastructureBuildOutcome Fail(string why) =>
            new InfrastructureBuildOutcome(false, null, -1, 0, why);
    }

    // Result of buying ONE Base level (InfrastructureActions.TryUpgradeBase).
    public readonly struct BaseUpgradeOutcome
    {
        public readonly bool Ok;
        public readonly BuildingData Building;
        public readonly BaseUpgradeTier Tier;
        public readonly int LevelBefore, LevelAfter;
        public readonly int UnlockedSlotsAfter;
        public readonly int ApSpent;
        public readonly ResourceCost ResourcesSpent;
        public readonly string FailReason;

        private BaseUpgradeOutcome(bool ok, BuildingData b, BaseUpgradeTier tier, int before, int after,
            int unlocked, int ap, ResourceCost spent, string fail)
        {
            Ok = ok; Building = b; Tier = tier; LevelBefore = before; LevelAfter = after;
            UnlockedSlotsAfter = unlocked; ApSpent = ap; ResourcesSpent = spent; FailReason = fail;
        }

        internal static BaseUpgradeOutcome Success(BuildingData b, BaseUpgradeTier tier, int before, int ap) =>
            new BaseUpgradeOutcome(true, b, tier, before, b.Level, b.UnlockedFacilitySlots, ap,
                tier.cost ?? new ResourceCost(), null);
        internal static BaseUpgradeOutcome Fail(string why) =>
            new BaseUpgradeOutcome(false, null, null, 0, 0, 0, 0, null, why);
    }

    // Canonical legality and atomic gameplay mutation for Base/Facility/extraction actions.
    // Changes to already-visible hex contents are published here, after the final commit.
    public static class InfrastructureActions
    {
        public static event Action<BuildingData> BaseFounded;
        internal const string NoFreeFacilitySlotReason = "No free facility slots in this base.";
        private static readonly ResourceType[] Res =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        public static bool CanMergeIntoResourceSite(BuildingData existing)
        {
            if (existing == null || existing.HasTieredUnlock)
                return false;
            int occupied = 0;
            foreach (FacilityData facility in existing.FacilitySlots)
                if (facility != null)
                    occupied++;
            return occupied <= BuildingData.DefaultTotalFacilitySlots;
        }

        public static bool CanFoundBase(CardDefinition definition, HexCoord hex, PlayerSetupData owner,
            int apCost, ResourceCost resourceCost, out string reason, HexMap map = null)
        {
            reason = null;
            if (map != null && !map.CanEnter(hex))
            { reason = "Buildings cannot be placed on this terrain."; return false; }
            if (definition == null || definition.cardType != CardType.Base || owner == null)
            { reason = "Select a base card."; return false; }
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null) { reason = "Your resources are unavailable. Try again."; return false; }
            BuildingData existing = BuildingRegistry.FindAt(hex);
            if (existing != null && (existing.Owner != owner || !CanMergeIntoResourceSite(existing)))
            { reason = "This hex already has a building."; return false; }
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, owner))
            { reason = "Move one of your armies with a hero onto this hex."; return false; }
            if (BattleInitiator.FindEnemyAt(hex, owner) != null)
            { reason = "An enemy army controls this hex."; return false; }
            if (apCost < 0 || !root.CanSpendActionPoints(apCost))
            { reason = apCost < 0 ? "This card has an invalid AP cost." : $"Need {apCost} AP to build here."; return false; }
            if (resourceCost != null && !resourceCost.CanAfford(root))
            { reason = "Not enough resources."; return false; }
            return true;
        }

        public static InfrastructureBuildOutcome TryFoundBase(HexSelectionController hexSelection,
            CardDefinition definition, HexCoord hex, PlayerSetupData owner, int apCost, ResourceCost resourceCost,
            Func<BuildingData, bool> completeBeforeCommit = null)
            => TryFoundBaseOnMap(hexSelection?.Map, definition, hex, owner, apCost, resourceCost,
                hexSelection, completeBeforeCommit);

        public static InfrastructureBuildOutcome TryFoundBaseOnMap(HexMap map,
            CardDefinition definition, HexCoord hex, PlayerSetupData owner, int apCost, ResourceCost resourceCost,
            HexSelectionController hexSelection = null, Func<BuildingData, bool> completeBeforeCommit = null)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (map == null || !map.CanEnter(hex))
                return InfrastructureBuildOutcome.Fail("Buildings cannot be placed on this terrain.");
            if (!CanFoundBase(definition, hex, owner, apCost, resourceCost, out string reason, map))
                return InfrastructureBuildOutcome.Fail(reason);

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            BuildingData existing = BuildingRegistry.FindAt(hex);
            FacilityData[] carriedOver = existing?.FacilitySlots;
            MapObjectVisual oldVisual = existing?.Visual;
            bool ownerGarrisonExistedBefore = ArmyRegistry.AllAt(hex).Any(a => a != null && a.IsGarrison && a.Owner == owner);
            // The building's own container only: a carrier hangar on the hex is not rolled back.
            bool ownerAirfieldExistedBefore = AviationRules.AirfieldsAt(hex, owner).Any(a => !a.IsCarrierContainer);

            int[] resourcesBefore = completeBeforeCommit == null ? null : SnapshotResources(root);
            var rostersBefore = completeBeforeCommit == null ? null : ArmyRegistry.AllForOwner(owner)
                .Where(a => a != null)
                .ToDictionary(a => a, a => a.Members.ToArray());
            var placementBefore = rostersBefore?.Keys.ToDictionary(a => a,
                a => (a.Hex, a.IsAirArmy, a.AirWingLanded, a.Controller, a.PendingAirStrikePolicy));
            var armiesBefore = completeBeforeCommit == null ? null : ArmyRegistry.AllArmies().ToHashSet();
            UnitData[] unitsBefore = rostersBefore?.Values.SelectMany(u => u).Distinct().ToArray();
            var sortiesBefore = unitsBefore?.Where(u => u.IsAviation).ToDictionary(u => u,
                u => (u.SortieLaunchPaid, u.ConsecutiveUnlandedEnds, u.HasEmergencyFlightPenalty));
            var transportBefore = unitsBefore?.ToDictionary(u => u, u => (Carrier: u.AssignedTransportCarrier, Army: u.TransportArmy));
            var coverageBefore = rostersBefore?.Keys.ToDictionary(a => a,
                a => unitsBefore.Where(a.HasActivationCoverageFor).ToHashSet());
            Action restoreDetection = completeBeforeCommit == null ? null : StealthSystem.CaptureDetectionRollback();
            int apBefore = root.ActionPoints;
            if (!new PaymentQuote(apCost, resourceCost).TryDebit(root, out var payment, out _))
                return InfrastructureBuildOutcome.Fail("The building cost is no longer affordable.");
            BuildingData building = null;
            bool threw = false;
            try
            {
                building = SpawnBaseDefinition(definition, hex, owner, map, hexSelection, false);
            }
            catch (Exception e)
            {
                threw = true;
                Debug.LogError($"[Infra] SpawnBuilding threw ({e.Message}); rolling back the transaction");
            }

            if (building == null)
            {
                if (threw || !ReferenceEquals(BuildingRegistry.FindAt(hex), existing))
                    RollbackPartialSpawn(hexSelection, hex, owner, existing,
                        ownerGarrisonExistedBefore, ownerAirfieldExistedBefore);
                payment.TryCompensate();
                mutation.Discard();
                return InfrastructureBuildOutcome.Fail("Could not build the base. Costs refunded.");
            }

            if (carriedOver != null)
            {
                int slot = 0;
                foreach (FacilityData facility in carriedOver)
                {
                    if (facility == null) continue;
                    while (slot < building.FacilitySlots.Length && building.FacilitySlots[slot] != null)
                        slot++;
                    if (slot >= building.FacilitySlots.Length) break;
                    building.FacilitySlots[slot] = facility;
                    slot++;
                }
                // Registration is batched; publish the completed inherited slots together.
                if (slot > 0)
                    VisionSystem.NotifyContentChanged(hex);
            }
            // Optional deterministic completion participates in the existing transaction. The
            // enclosing transaction compensates a partial canonical transfer/deploy as well as
            // its own payment. A failed completion must not publish an empty Base
            // that the mission ledger could mistake for a completed Economy project next pass.
            bool completionAccepted = true;
            try { completionAccepted = completeBeforeCommit == null || completeBeforeCommit(building); }
            catch (Exception e)
            {
                completionAccepted = false;
                Debug.LogError($"[Infra] Founding completion threw ({e.Message}); rolling back");
            }
            if (!completionAccepted)
            {
                // Success is still unpublished. Restore every canonical source/recipient,
                // including same-owner transfers arriving from another indexed hex.
                foreach (ArmyData army in rostersBefore.Keys.Concat(ArmyRegistry.AllForOwner(owner)).Distinct().ToArray())
                {
                    foreach (UnitData unit in unitsBefore)
                        army.MarkUnitActivationPaid(unit,
                            coverageBefore.TryGetValue(army, out var covered) && covered.Contains(unit));
                    army.Members.Clear();
                    if (rostersBefore.TryGetValue(army, out UnitData[] members))
                        army.Members.AddRange(members);
                    army.RefreshTransport();
                }
                using (Game.Setup.MatchRestoreScope.Begin())
                    foreach (ArmyData added in ArmyRegistry.AllArmies().Where(a => !armiesBefore.Contains(a)).ToArray())
                    {
                        added.ClearTransport();
                        if (added.CarrierUnit != null)
                        {
                            if (added.CarrierUnit.CarrierAirfield == added) added.CarrierUnit.CarrierAirfield = null;
                            if (added.CarrierUnit.CarrierFlightDeck == added) added.CarrierUnit.CarrierFlightDeck = null;
                            added.CarrierUnit = null;
                        }
                        ArmyRegistry.Unregister(added);
                        if (added.Controller != null) Game.Core.UnityObjectLifetime.Destroy(added.Controller.gameObject);
                        added.Controller = null;
                    }
                // Transfer reconciliation can relocate an existing carrier's containers, and a
                // landing closes a sortie. Restoring lists alone would leave both inconsistent.
                using (Game.Setup.MatchRestoreScope.Begin())
                    foreach (var entry in placementBefore)
                    {
                        if (ArmyRegistry.IsRegistered(entry.Key))
                            ArmyRegistry.MoveArmy(entry.Key, entry.Value.Hex);
                        else
                        {
                            // Full carrier-sortie landing retires its empty shell. Restoring
                            // only Members would strand the aircraft outside the world index.
                            entry.Key.SetHex(entry.Value.Hex);
                            ArmyRegistry.Register(entry.Key);
                        }
                        entry.Key.Controller = entry.Value.Controller;
                        entry.Key.PendingAirStrikePolicy = entry.Value.PendingAirStrikePolicy;
                        entry.Key.IsAirArmy = entry.Value.IsAirArmy;
                        entry.Key.AirWingLanded = entry.Value.AirWingLanded;
                    }
                foreach (var entry in sortiesBefore)
                {
                    entry.Key.SortieLaunchPaid = entry.Value.SortieLaunchPaid;
                    entry.Key.ConsecutiveUnlandedEnds = entry.Value.ConsecutiveUnlandedEnds;
                    entry.Key.HasEmergencyFlightPenalty = entry.Value.HasEmergencyFlightPenalty;
                }
                foreach (var entry in transportBefore) entry.Key.SetTransportCarrier(entry.Value.Carrier, entry.Value.Army);
                foreach (ArmyData original in rostersBefore.Keys) original.RebindRestoredTransportRecipients();
                root.ActionPoints = apBefore;
                RestoreResources(root, resourcesBefore);
                RollbackPartialSpawn(hexSelection, hex, owner, existing,
                    ownerGarrisonExistedBefore, ownerAirfieldExistedBefore);
                restoreDetection?.Invoke();
                mutation.Discard();
                return InfrastructureBuildOutcome.Fail("Could not finish building the base. Costs refunded.");
            }
            if (oldVisual != null)
                Game.Core.UnityObjectLifetime.Destroy(oldVisual.gameObject);
            StealthSystem.RunChecksForNewVisionSource(building);
            VisionSystem.PublishAfterMutation(BaseFounded, building);
            return InfrastructureBuildOutcome.Success(building, -1, apBefore - root.ActionPoints);
        }

        private static void RollbackPartialSpawn(HexSelectionController hexSelection, HexCoord hex,
            PlayerSetupData owner, BuildingData siteBefore, bool ownerGarrisonExistedBefore,
            bool ownerAirfieldExistedBefore)
        {
            using var restore = Game.Setup.MatchRestoreScope.Begin();
            BuildingData now = BuildingRegistry.FindAt(hex);
            if (now != null && now != siteBefore)
            {
                if (now.Visual != null)
                    Game.Core.UnityObjectLifetime.Destroy(now.Visual.gameObject);
                BuildingRegistry.Unregister(hex);
            }
            if (siteBefore != null && BuildingRegistry.FindAt(hex) == null)
                BuildingRegistry.Register(hex, siteBefore);

            if (!ownerGarrisonExistedBefore)
            {
                ArmyData orphan = ArmyRegistry.AllAt(hex)
                    .FirstOrDefault(a => a != null && a.IsGarrison && a.Owner == owner && a.Members.Count == 0);
                if (orphan != null)
                {
                    if (orphan.Controller != null)
                        Game.Core.UnityObjectLifetime.Destroy(orphan.Controller.gameObject);
                    ArmyRegistry.Unregister(orphan);
                }
            }
            if (!ownerAirfieldExistedBefore)
            {
                ArmyData orphan = ArmyRegistry.AllAt(hex)
                    .FirstOrDefault(a => a != null && a.IsAirfield && !a.IsCarrierContainer
                        && a.Owner == owner && a.Members.Count == 0);
                if (orphan != null)
                {
                    if (orphan.Controller != null)
                        Game.Core.UnityObjectLifetime.Destroy(orphan.Controller.gameObject);
                    ArmyRegistry.Unregister(orphan);
                }
            }
            hexSelection?.RestackArmiesOn(hex, null);
        }

        // ONE Base level. The next tier is the one at index Level-1; `expectedLevel` (optional) lets an
        // AI revalidation refuse a plan made for a Base that has moved on. Raw legality only: whether
        // the AI ought to buy it (witness, gate, bank) is decided by its own admission, never here, so
        // the human UpgradeBase button keeps buying any legal next level.
        public static bool CanUpgradeBase(BuildingData building, BaseUpgradeTier[] tiers, int? expectedLevel,
            out BaseUpgradeTier tier, out string reason)
        {
            tier = null;
            reason = null;
            if (building == null || building.Owner == null || !ReferenceEquals(BuildingRegistry.FindAt(building.Hex), building))
            { reason = "This base is no longer available."; return false; }
            // Only a tiered Base/Citadel has levels; a hero-built resource site is never upgraded.
            if (!building.IsBase || !building.HasTieredUnlock)
            { reason = "This building cannot be upgraded."; return false; }
            if (expectedLevel.HasValue && building.Level != expectedLevel.Value)
            { reason = "Base level changed. Reopen the base window."; return false; }
            int tierIndex = building.Level - 1;
            if (tiers == null || tierIndex < 0 || tierIndex >= tiers.Length || tiers[tierIndex] == null)
            { reason = "This base is fully upgraded."; return false; }
            PlayerRoot root = PlayerRootRegistry.FindFor(building.Owner);
            if (root == null) { reason = "Your resources are unavailable. Try again."; return false; }
            BaseUpgradeTier next = tiers[tierIndex];
            if (!root.CanSpendActionPoints(next.apCost))
            { reason = $"Need {next.apCost} AP to upgrade this base."; return false; }
            if (next.cost != null && !next.cost.CanAfford(root))
            { reason = "Not enough resources."; return false; }
            if (!new PaymentQuote(next.apCost, next.cost).CanDebit(root, out _))
            { reason = "Invalid or unaffordable upgrade cost."; return false; }
            tier = next;
            return true;
        }

        // Synchronous: every check precedes the single payment + mutation, a refusal changes nothing.
        public static BaseUpgradeOutcome TryUpgradeBase(BuildingData building, BaseUpgradeTier[] tiers,
            int? expectedLevel = null)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!CanUpgradeBase(building, tiers, expectedLevel, out BaseUpgradeTier tier, out string reason))
                return BaseUpgradeOutcome.Fail(reason);
            PlayerRoot root = PlayerRootRegistry.FindFor(building.Owner);
            int levelBefore = building.Level;
            int apBefore = root.ActionPoints;
            if (!new PaymentQuote(tier.apCost, tier.cost).TryDebit(root, out _, out _))
                return BaseUpgradeOutcome.Fail("Invalid or unaffordable upgrade cost.");
            building.Level++;
            building.Defense += tier.defenseGain;
            // Level changes UnlockedFacilitySlots while the hex and its owner's vision stay the same:
            // publish the completed building state once.
            VisionSystem.NotifyContentChanged(building.Hex);
            return BaseUpgradeOutcome.Success(building, tier, levelBefore, apBefore - root.ActionPoints);
        }

        public static bool CanPlaceFacility(CardDefinition definition, HexCoord baseHex, PlayerSetupData owner,
            int apCost, ResourceCost resourceCost, out string reason, bool requireCurrentAp = true)
        {
            reason = null;
            if (definition == null || definition.cardType != CardType.Facility || owner == null)
            { reason = "Select a facility card."; return false; }
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null) { reason = "Your resources are unavailable. Try again."; return false; }
            BuildingData building = BuildingRegistry.FindAt(baseHex);
            if (building == null || building.Owner != owner || !building.IsBase)
            { reason = "Facilities require one of your bases on this hex."; return false; }
            if (apCost < 0 || (requireCurrentAp && !root.CanSpendActionPoints(apCost)))
            { reason = apCost < 0 ? "This card has an invalid AP cost." : $"Need {apCost} AP to build here."; return false; }
            if (resourceCost != null && !resourceCost.CanAfford(root))
            { reason = "Not enough resources."; return false; }
            if (building.FindFirstAvailableFacilitySlot() < 0)
            { reason = NoFreeFacilitySlotReason; return false; }
            return true;
        }

        public static InfrastructureBuildOutcome TryPlaceFacility(CardDefinition definition, HexCoord baseHex,
            PlayerSetupData owner, int apCost, ResourceCost resourceCost)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!CanPlaceFacility(definition, baseHex, owner, apCost, resourceCost, out string reason))
                return InfrastructureBuildOutcome.Fail(reason);

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            BuildingData building = BuildingRegistry.FindAt(baseHex);
            int slotIndex = building.FindFirstAvailableFacilitySlot();
            FacilityData facility;
            try
            {
                facility = FacilityData.FromDefinition(definition);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Infra] FacilityData.FromDefinition threw: {e.Message}");
                return InfrastructureBuildOutcome.Fail("Could not build this facility. Try again.");
            }

            int apBefore = root.ActionPoints;
            if (!new PaymentQuote(apCost, resourceCost).TryDebit(root, out _, out _))
                return InfrastructureBuildOutcome.Fail("Invalid or unaffordable facility cost.");
            building.FacilitySlots[slotIndex] = facility;
            // The building stays registered at the same visible hex; neither Register
            // nor RecomputeFor fires. Notify once after the slot and its income change.
            VisionSystem.NotifyContentChanged(baseHex);
            return InfrastructureBuildOutcome.Success(building, slotIndex, apBefore - root.ActionPoints);
        }

        // Scripted spawn by definition is separate from playing an exact hand instance.
        internal static BuildingData SpawnBaseDefinition(CardDefinition definition, HexCoord hex,
            PlayerSetupData owner, HexMap map, HexSelectionController presentation, bool runVisionChecks)
        {
            if (map == null || !map.CanEnter(hex) || owner == null || definition == null) return null;
            using var mutation = VisionSystem.BeginMutation();
            var building = BuildingData.FromDefinition(definition, hex, owner);
            presentation?.BindBaseVisual(building);
            BuildingRegistry.Register(hex, building);
            BuildingRegistry.EnsureGarrisonForBuilding(building, presentation);
            if (building.AirfieldCapacity > 0) AviationActions.EnsureAirfield(presentation, owner, hex);
            presentation?.RestackArmiesOn(hex, null);
            if (runVisionChecks) StealthSystem.RunChecksForNewVisionSource(building);
            return building;
        }

        public static InfrastructureBuildOutcome TryBuildExtractionSite(HexSelectionController hexSelection,
            CardDefinition facilityDefinition, HexCoord hex, PlayerSetupData owner)
            => TryBuildExtractionSiteOnMap(hexSelection?.Map, hexSelection?.ExtractionConfig,
                facilityDefinition, hex, owner, hexSelection);

        public static bool CanBuildExtractionSite(HexMap map, GameConfig config, CardDefinition definition,
            HexCoord hex, PlayerSetupData owner, out PaymentQuote quote, out string reason,
            bool requireCurrentBudget = true)
            => PrepareExtraction(map, config, definition, hex, owner, out _, out _, out _, out quote, out reason, requireCurrentBudget);

        private static bool PrepareExtraction(HexMap map, GameConfig config, CardDefinition definition,
            HexCoord hex, PlayerSetupData owner, out BuildingData building, out int slot,
            out ArmyData actor, out PaymentQuote quote, out string reason, bool requireCurrentBudget = true)
        {
            building = null; slot = -1; actor = null;
            quote = new PaymentQuote(definition?.apCost ?? 0, definition?.resourceCost);
            reason = "Cannot build this resource facility here.";
            if (map == null || config == null || definition == null || definition.cardType != CardType.Facility
                || owner == null || !map.CanEnter(hex) || !HexSelectionController.HasOwnHeroArmyAt(hex, owner)) return false;
            building = BuildingRegistry.FindAt(hex);
            if (building != null && building.Owner != owner) return false;
            building = building ?? new BuildingData(totalFacilitySlots: 4)
            {
                Name = "Resource Site", Hex = hex, Owner = owner,
                StructurePointsMax = config.resourceSiteStructurePoints, StructurePointsCurrent = config.resourceSiteStructurePoints,
                Defense = config.resourceSiteDefense, Fate = config.resourceSiteFate, HasTieredUnlock = false,
            };
            string ability = definition.grantedAbilities?.Find(a => Array.IndexOf(UnitAbilities.CollectAbilities, a) >= 0);
            int resourceIndex = Array.IndexOf(UnitAbilities.CollectAbilities, ability);
            if (resourceIndex < 0) return false;
            if (building.HasFacilityWithAbility(ability))
            { reason = $"{building.Name} already has a {definition.displayName}."; return false; }
            if (!map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain)) return false;
            var resource = (ResourceType)resourceIndex;
            int collectors = ArmyRegistry.At(hex).Where(a => a.Owner == owner)
                .Sum(a => a.Members.Count(m => m != null && m.HasAbility(ability)));
            int marginal = IncomeProjection.MarginalOwnerCollectionAtHex(
                HexResourceBonusRegistry.EffectiveYield(terrain, hex).Get(resource),
                building.CollectedAmount(resource), 1, collectors, BattleInitiator.FindEnemyAt(hex, owner) == null);
            if (marginal <= 0)
            { reason = $"{definition.displayName} adds no {resource} income here."; return false; }
            slot = building.FindFirstAvailableFacilitySlot();
            if (slot < 0) { reason = $"No free facility slots in {building.Name}."; return false; }
            if (requireCurrentBudget && !quote.CanDebit(PlayerRootRegistry.FindFor(owner), out PaymentFailure failure))
            { reason = failure == PaymentFailure.ActionPoints ? $"Not enough AP to build {definition.displayName}." : $"Not enough resources to build {definition.displayName}."; return false; }
            actor = ArmyRegistry.At(hex).Where(a => a.Owner == owner && a.Members.Any(m => m != null && m.IsHero))
                .OrderBy(a => a.CurrentMovement).ThenBy(a => a.Id).FirstOrDefault();
            reason = null; return true;
        }

        public static InfrastructureBuildOutcome TryBuildExtractionSiteOnMap(HexMap map, GameConfig config,
            CardDefinition definition, HexCoord hex, PlayerSetupData owner, HexSelectionController presentation = null)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!PrepareExtraction(map, config, definition, hex, owner, out var building, out int slot,
                    out var actor, out var quote, out string reason)) return InfrastructureBuildOutcome.Fail(reason);
            FacilityData facility;
            try { facility = FacilityData.FromDefinition(definition); }
            catch (Exception e) { return InfrastructureBuildOutcome.Fail("Could not build this facility: " + e.Message); }
            bool newSite = BuildingRegistry.FindAt(hex) == null;
            // Prepare optional visuals before debit; a presentation failure cannot charge a failed action.
            if (newSite)
            {
                try { presentation?.BindExtractionVisual(building); }
                catch (Exception e) { return InfrastructureBuildOutcome.Fail("Could not display this resource site: " + e.Message); }
            }
            if (!quote.TryDebit(PlayerRootRegistry.FindFor(owner), out _, out _))
                return InfrastructureBuildOutcome.Fail("The resource facility cost is no longer affordable.");
            building.FacilitySlots[slot] = facility;
            if (newSite) BuildingRegistry.Register(hex, building);
            HexSelectionController.ApplyExtractionBuilderConsequences(actor);
            StealthSystem.RunChecksForNewVisionSource(building, facility);
            VisionSystem.NotifyContentChanged(hex);
            VisionSystem.PublishAfterMutation(() => presentation?.RestackArmiesOn(hex, null));
            return InfrastructureBuildOutcome.Success(building, slot, quote.Ap);
        }

        private static int[] SnapshotResources(PlayerRoot root)
        {
            var v = new int[Res.Length];
            for (int i = 0; i < Res.Length; i++)
                v[i] = root.GetResource(Res[i]);
            return v;
        }

        private static void RestoreResources(PlayerRoot root, int[] before)
        {
            for (int i = 0; i < Res.Length; i++)
            {
                int delta = before[i] - root.GetResource(Res[i]);
                if (delta != 0)
                    root.AddResource(Res[i], delta);
            }
        }
    }
}
