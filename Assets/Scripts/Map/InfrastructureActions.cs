using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Map
{
    // ===========================================================================================
    //  INFRASTRUCTURE ACTIONS  (shared authoritative infrastructure transaction)
    // ===========================================================================================
    //  The SINGLE owner of "found a Base" / "place a Facility" / "build an extraction site" as an
    //  all-or-nothing transaction, shared by the human UI (CardHandUI) and AI Strategy V2
    //  (BuildingPlayExecutor). Legality is defined once (CanFoundBase / CanPlaceFacility); Try* is
    //  that check plus the mutation.
    //
    //  TRANSACTION MODEL. AP + resources are spent FIRST (a pure, fully-reversible numeric change)
    //  and the world-mutating primitive (SpawnBuilding / TryBuildExtractionFacility) is the LAST
    //  step and the commit point:
    //    · primitive reports failure  -> AP + resources are refunded to the exact pre-transaction
    //      value; the primitive's own guard means it mutated nothing. FULLY ATOMIC.
    //    · primitive THROWS            -> AP + resources are refunded, and any half-applied world
    //      state the primitive left is rolled back best-effort: a building it registered over an
    //      existing resource site is un-registered and the site re-registered; a garrison it
    //      spawned (Barracks base) that did not exist before is removed. A hard error is logged.
    //      The one thing that cannot be undone is StealthSystem's fog-of-war reveal — that is
    //      information, not reservation/army-limbo state, and this path only happens when the
    //      scene itself is misconfigured (null prefab, etc.).
    //    · primitive succeeds         -> the only remaining steps are infallible array writes.
    //
    //  These methods do NOT touch any hand: the caller removes its own card representation
    //  (CardHandUI.RemoveCard for the human, AiHandData.Hand for the AI) ONLY when Ok is true.
    // ===========================================================================================
    public readonly struct InfrastructureBuildOutcome
    {
        public readonly bool Ok;
        public readonly BuildingData Building;
        public readonly int SlotIndex;      // facility slot filled (-1 for a Base)
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

    public static class InfrastructureActions
    {
        private static readonly ResourceType[] Res =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        // A dragged Base card may land on a hero-built resource SITE (HasTieredUnlock == false) as
        // long as the fresh Base's slot capacity can fit every Facility already there — they carry
        // over. Anything else (a citadel, a real Base, another player's building) blocks it.
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

        // ================================================================= Base =====
        public static bool CanFoundBase(CardDefinition definition, HexCoord hex, PlayerSetupData owner,
            int apCost, ResourceCost resourceCost, out string reason)
        {
            reason = null;
            if (definition == null || definition.cardType != CardType.Base || owner == null)
            { reason = "not a Base card"; return false; }
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null) { reason = "no player root"; return false; }

            BuildingData existing = BuildingRegistry.FindAt(hex);
            if (existing != null && (existing.Owner != owner || !CanMergeIntoResourceSite(existing)))
            { reason = "hex already has a building"; return false; }
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, owner))
            { reason = "needs one of your hero-led armies on the hex"; return false; }
            if (BattleInitiator.FindEnemyAt(hex, owner) != null)
            { reason = "an enemy army holds this hex"; return false; }
            if (apCost < 0 || !root.CanSpendActionPoints(apCost))
            { reason = $"not enough action points ({apCost})"; return false; }
            if (resourceCost != null && !resourceCost.CanAfford(root))
            { reason = "not enough resources"; return false; }
            return true;
        }

        public static InfrastructureBuildOutcome TryFoundBase(HexSelectionController hexSelection,
            CardDefinition definition, HexCoord hex, PlayerSetupData owner, int apCost, ResourceCost resourceCost)
        {
            if (hexSelection == null)
                return InfrastructureBuildOutcome.Fail("no hex controller");
            if (!CanFoundBase(definition, hex, owner, apCost, resourceCost, out string reason))
                return InfrastructureBuildOutcome.Fail(reason);

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            BuildingData existing = BuildingRegistry.FindAt(hex);
            FacilityData[] carriedOver = existing?.FacilitySlots;
            MapObjectVisual oldVisual = existing?.Visual;
            bool ownerGarrisonExistedBefore = ArmyRegistry.AllAt(hex).Any(a => a != null && a.IsGarrison && a.Owner == owner);

            // --- reversible spend FIRST ---
            int apBefore = root.ActionPoints;
            root.SpendActionPoints(apCost);
            resourceCost?.PayFrom(root);

            // --- commit: SpawnBuilding is the point of no return ---
            BuildingData building = null;
            bool threw = false;
            try
            {
                building = hexSelection.SpawnBuilding(definition, hex, owner);
            }
            catch (Exception e)
            {
                threw = true;
                Debug.LogError($"[Infra] SpawnBuilding threw ({e.Message}); rolling back the transaction");
            }

            if (building == null)
            {
                if (threw)
                    RollbackPartialSpawn(hexSelection, hex, owner, existing, ownerGarrisonExistedBefore);
                root.ActionPoints = apBefore;
                Refund(root, resourceCost);
                return InfrastructureBuildOutcome.Fail(threw
                    ? "SpawnBuilding threw; transaction rolled back"
                    : "SpawnBuilding refused (missing scene config); rolled back");
            }

            // --- infallible finalize: merge a carried-over resource site ---
            if (oldVisual != null)
                UnityEngine.Object.Destroy(oldVisual.gameObject);
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
            }
            return InfrastructureBuildOutcome.Success(building, -1, apBefore - root.ActionPoints);
        }

        // Best-effort undo of a SpawnBuilding that partially mutated the world before throwing.
        private static void RollbackPartialSpawn(HexSelectionController hexSelection, HexCoord hex,
            PlayerSetupData owner, BuildingData siteBefore, bool ownerGarrisonExistedBefore)
        {
            BuildingData now = BuildingRegistry.FindAt(hex);
            if (now != null && now != siteBefore)
            {
                if (now.Visual != null)
                    UnityEngine.Object.Destroy(now.Visual.gameObject);
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
                        UnityEngine.Object.Destroy(orphan.Controller.gameObject);
                    ArmyRegistry.Unregister(orphan);
                }
            }
            hexSelection?.RestackArmiesOn(hex, null);
        }

        // ============================================================= Facility =====
        public static bool CanPlaceFacility(CardDefinition definition, HexCoord baseHex, PlayerSetupData owner,
            int apCost, ResourceCost resourceCost, out string reason)
        {
            reason = null;
            if (definition == null || definition.cardType != CardType.Facility || owner == null)
            { reason = "not a Facility card"; return false; }
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null) { reason = "no player root"; return false; }

            BuildingData building = BuildingRegistry.FindAt(baseHex);
            if (building == null || building.Owner != owner || !building.IsBase)
            { reason = "no owned Base at the hex"; return false; }
            if (building.FindFirstAvailableFacilitySlot() < 0)
            { reason = "Base has no free Facility slot"; return false; }
            if (apCost < 0 || !root.CanSpendActionPoints(apCost))
            { reason = $"not enough action points ({apCost})"; return false; }
            if (resourceCost != null && !resourceCost.CanAfford(root))
            { reason = "not enough resources"; return false; }
            return true;
        }

        public static InfrastructureBuildOutcome TryPlaceFacility(CardDefinition definition, HexCoord baseHex,
            PlayerSetupData owner, int apCost, ResourceCost resourceCost)
        {
            if (!CanPlaceFacility(definition, baseHex, owner, apCost, resourceCost, out string reason))
                return InfrastructureBuildOutcome.Fail(reason);

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            BuildingData building = BuildingRegistry.FindAt(baseHex);
            int slotIndex = building.FindFirstAvailableFacilitySlot();

            // Fallible step (malformed grantedAbilities) FIRST, before any spend.
            FacilityData facility;
            try
            {
                facility = FacilityData.FromDefinition(definition);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Infra] FacilityData.FromDefinition threw: {e.Message}");
                return InfrastructureBuildOutcome.Fail("facility build threw");
            }

            int apBefore = root.ActionPoints;
            root.SpendActionPoints(apCost);
            resourceCost?.PayFrom(root);
            building.FacilitySlots[slotIndex] = facility;   // infallible commit
            return InfrastructureBuildOutcome.Success(building, slotIndex, apBefore - root.ActionPoints);
        }

        // ===================================================== extraction site =====
        //  HexSelectionController.TryBuildExtractionFacility builds its FacilityData before the
        //  spend, but AFTER the spend it does several more world mutations: the facility-slot
        //  write, zeroing the acting hero-army's move points, and (for a brand-new site) the
        //  marker + BuildingRegistry.Register + RestackArmiesOn. Its own affordability checks all
        //  run before the spend, so a plain `false` return with nothing spent means nothing was
        //  mutated. This wrapper captures every piece of post-spend state up front and, on a THROW
        //  or on a false-return-that-nevertheless-spent, restores ALL of it -- AP, resources,
        //  facility slots, hero move points, and a half-registered new site -- so a failed build
        //  leaves the game state exactly as before (same contract as TryFoundBase).
        public static InfrastructureBuildOutcome TryBuildExtractionSite(HexSelectionController hexSelection,
            CardDefinition facilityDefinition, HexCoord hex, PlayerSetupData owner)
        {
            if (hexSelection == null || facilityDefinition == null || owner == null)
                return InfrastructureBuildOutcome.Fail("bad args");
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, owner))
                return InfrastructureBuildOutcome.Fail("no hero-led army on the resource hex");
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null)
                return InfrastructureBuildOutcome.Fail("no player root");

            int apBefore = root.ActionPoints;
            int[] resBefore = SnapshotResources(root);

            // Everything TryBuildExtractionFacility can mutate after its (pre-spend) checks,
            // captured so a throw or a spent-but-false return can be fully undone.
            BuildingData siteBefore = BuildingRegistry.FindAt(hex);
            bool wasNewSite = siteBefore == null;
            FacilityData[] slotsBefore = siteBefore != null
                ? (FacilityData[])siteBefore.FacilitySlots.Clone()
                : null;
            List<(UnitData Member, int Move)> moveBefore = SnapshotHeroArmyMovement(hex, owner);

            bool ok;
            bool threw = false;
            try
            {
                ok = hexSelection.TryBuildExtractionFacility(facilityDefinition, hex, owner);
            }
            catch (Exception e)
            {
                ok = false;
                threw = true;
                Debug.LogError($"[Infra] TryBuildExtractionFacility threw: {e.Message}");
            }

            int apSpent = apBefore - root.ActionPoints;
            if (!ok)
            {
                // A throw can land after the (infallible-in-practice) post-spend mutations even
                // when apCost / resourceCost are both zero, so restore on ANY throw, not only on a
                // measured spend. A plain false return only ever happens before the spend.
                if (threw || apSpent != 0 || ResourcesMoved(resBefore, root))
                {
                    root.ActionPoints = apBefore;
                    RestoreResources(root, resBefore);
                    RestoreHeroArmyMovement(moveBefore);
                    if (wasNewSite)
                        RollbackPartialExtractionSite(hexSelection, hex, siteBefore);
                    else
                        RestoreFacilitySlots(siteBefore, slotsBefore);
                }
                return InfrastructureBuildOutcome.Fail(threw
                    ? "TryBuildExtractionFacility threw; transaction rolled back"
                    : "TryBuildExtractionFacility rejected the hex");
            }
            return InfrastructureBuildOutcome.Success(BuildingRegistry.FindAt(hex), -1, apSpent);
        }

        // Best-effort undo of a new resource SITE that TryBuildExtractionFacility registered before
        // it threw: destroy the fresh marker, drop the registry entry, re-resolve the hex layout.
        private static void RollbackPartialExtractionSite(HexSelectionController hexSelection,
            HexCoord hex, BuildingData siteBefore)
        {
            BuildingData now = BuildingRegistry.FindAt(hex);
            if (now != null && now != siteBefore)
            {
                if (now.Visual != null)
                    UnityEngine.Object.Destroy(now.Visual.gameObject);
                BuildingRegistry.Unregister(hex);
            }
            hexSelection?.RestackArmiesOn(hex, null);
        }

        private static void RestoreFacilitySlots(BuildingData building, FacilityData[] before)
        {
            if (building == null || before == null) return;
            int n = Math.Min(building.FacilitySlots.Length, before.Length);
            for (int i = 0; i < n; i++)
                building.FacilitySlots[i] = before[i];
        }

        // Move points of every member of every one of `owner`'s hero-led armies on `hex` -- the
        // superset of what TryBuildExtractionFacility zeroes (it charges the whole build to one
        // acting army's remaining movement). Restore is idempotent for the armies it left alone.
        private static List<(UnitData Member, int Move)> SnapshotHeroArmyMovement(HexCoord hex, PlayerSetupData owner)
        {
            var snap = new List<(UnitData, int)>();
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army == null || army.Owner != owner || !army.Members.Exists(m => m.IsHero))
                    continue;
                foreach (UnitData member in army.Members)
                    snap.Add((member, member.MoveCurrent));
            }
            return snap;
        }

        private static void RestoreHeroArmyMovement(List<(UnitData Member, int Move)> before)
        {
            if (before == null) return;
            foreach ((UnitData member, int move) in before)
                if (member != null)
                    member.MoveCurrent = move;
        }

        // -------------------------------------------------------------- helpers ----
        private static void Refund(PlayerRoot root, ResourceCost cost)
        {
            if (root == null || cost == null) return;
            foreach (ResourceType t in Res)
            {
                int amount = cost.Get(t);
                if (amount > 0)
                    root.AddResource(t, amount);
            }
        }

        private static int[] SnapshotResources(PlayerRoot root)
        {
            var v = new int[Res.Length];
            for (int i = 0; i < Res.Length; i++)
                v[i] = root.GetResource(Res[i]);
            return v;
        }

        private static bool ResourcesMoved(int[] before, PlayerRoot root)
        {
            for (int i = 0; i < Res.Length; i++)
                if (before[i] != root.GetResource(Res[i])) return true;
            return false;
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
