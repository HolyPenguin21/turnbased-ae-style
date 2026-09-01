using System;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Map
{
    // ===========================================================================================
    //  INFRASTRUCTURE ACTIONS  (shared authoritative domain transaction)
    // ===========================================================================================
    //  The SINGLE owner of "found a Base" / "place a Facility" as an all-or-nothing transaction.
    //  Both the human UI (CardHandUI.TryBuildBase / TryDeployFacilityToHex) and AI Strategy V2
    //  (BuildingPlayExecutor) call these — neither one assembles the spend/create/refund sequence
    //  itself any more. Legality is defined ONCE here (CanFoundBase / CanPlaceFacility); Try* is
    //  exactly that check plus the mutation.
    //
    //  ATOMICITY by ORDERING, not by after-the-fact rollback: the ONLY fallible step (SpawnBuilding
    //  / FacilityData.FromDefinition) runs BEFORE any AP/resource is spent. If it fails, nothing
    //  was spent and nothing was created. Everything after the spend — the spend itself
    //  (SpendActionPoints / PayFrom on amounts already checked with CanSpendActionPoints /
    //  CanAfford) and the facility-slot assignment / merge carry-over — is pure arithmetic / array
    //  writes that cannot fail. A defensive catch still un-registers + destroys a just-created
    //  building and refunds, so an unexpected throw can never leave a half-applied state.
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

        // ------------------------------------------------------------------- Base ----
        //  `apCost` / `resourceCost` are the card INSTANCE's effective play cost (a
        //  Research/Production card pays activationApCost and no ResourceCost) — the caller passes
        //  them so this stays agnostic to card-instance semantics.

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

            // --- fallible step FIRST, before any spend ---
            BuildingData building;
            try
            {
                building = hexSelection.SpawnBuilding(definition, hex, owner);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Infra] SpawnBuilding threw: {e.Message}");
                return InfrastructureBuildOutcome.Fail("SpawnBuilding threw");
            }
            if (building == null)
                return InfrastructureBuildOutcome.Fail("SpawnBuilding refused (missing scene config)");

            int apBefore = root.ActionPoints;
            try
            {
                root.SpendActionPoints(apCost);
                resourceCost?.PayFrom(root);

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
            }
            catch (Exception e)
            {
                // Unreachable in practice (spends were pre-validated); full rollback anyway.
                Debug.LogError($"[Infra] TryFoundBase post-spawn threw: {e.Message}; rolling back");
                BuildingRegistry.Unregister(hex);
                if (building.Visual != null)
                    UnityEngine.Object.Destroy(building.Visual.gameObject);
                root.ActionPoints = apBefore;
                Refund(root, resourceCost);
                return InfrastructureBuildOutcome.Fail("exception during build; rolled back");
            }

            return InfrastructureBuildOutcome.Success(building, -1, apBefore - root.ActionPoints);
        }

        // ---------------------------------------------------------------- Facility ----
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

            // --- fallible step FIRST, before any spend ---
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
            building.FacilitySlots[slotIndex] = facility;
            return InfrastructureBuildOutcome.Success(building, slotIndex, apBefore - root.ActionPoints);
        }

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
    }
}
