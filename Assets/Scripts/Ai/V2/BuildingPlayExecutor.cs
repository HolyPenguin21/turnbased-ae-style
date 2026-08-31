using System;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  BUILDING PLAY EXECUTOR  (Strategy V2 — infrastructure card execution)
    // ===========================================================================================
    //  The single V2 path for INFRASTRUCTURE (Base / Facility / hero-built extraction site). It
    //  drives the SAME domain primitives the human UI drives — the game has no single atomic
    //  "play an infrastructure card" transaction, so CardHandUI itself assembles the identical
    //  sequence (see CardHandUI.TryBuildBase / TryDeployFacilityToHex):
    //
    //    · CardType.Base      -> HexSelectionController.SpawnBuilding
    //    · CardType.Facility  -> BuildingData.FacilitySlots[i] = FacilityData.FromDefinition(def)
    //    · extraction site    -> HexSelectionController.TryBuildExtractionFacility  (already fully
    //                            atomic — it validates hero presence + AP + resources and spends
    //                            them itself)
    //
    //  ATOMICITY. Every observable mutation is fenced behind an EXHAUSTIVE preflight (card in hand,
    //  AP affordable, resources affordable, target legal, free slot). Once preflight passes, the
    //  mutation steps cannot fail — SpendActionPoints / PayFrom / the primitive / Hand.Remove all
    //  succeed on already-validated inputs. The whole sequence is wrapped so that an unexpected
    //  throw is reported as a failure rather than leaving a half-built state, and cost figures are
    //  the card instance's EFFECTIVE play cost (parity with the human path — a
    //  Research/Production card pays activationApCost and no ResourceCost).
    // ===========================================================================================
    public sealed class BuildingPlayResult
    {
        public bool Built;
        public float ApSpent;          // real PlayerRoot AP delta
        public bool StateChanged;
        public bool CardConsumed;
        public string FailReason;

        public static BuildingPlayResult Fail(string why) => new BuildingPlayResult { FailReason = why };
    }

    public static class BuildingPlayExecutor
    {
        // ---------------------------------------------------------------------- Base ----
        public static bool PreflightBaseCard(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, CardData card, HexCoord hex, out string reason)
        {
            reason = null;
            if (player == null || root == null || hand == null || ctx?.HexSelection == null || card == null)
            { reason = "missing args"; return false; }
            CardDefinition def = card.Definition;
            if (def == null || def.cardType != CardType.Base)
            { reason = $"card type {def?.cardType} is not a Base"; return false; }
            if (!hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, player))
            { reason = "founding a Base needs one of your hero-led armies on the hex"; return false; }
            if (ArmyRegistry.AllAt(hex).Any(a => a != null && a.Owner != null && a.Owner != player))
            { reason = "hex is contested"; return false; }
            if (BuildingRegistry.FindAt(hex) != null)
            { reason = "hex already has a building"; return false; }
            if (!root.CanSpendActionPoints(card.EffectivePlayApCost))
            { reason = $"need {card.EffectivePlayApCost} AP"; return false; }
            ResourceCost cost = card.EffectivePlayResourceCost;
            if (cost != null && !cost.CanAfford(root))
            { reason = "resource cost unaffordable"; return false; }
            if (!string.IsNullOrEmpty(def.requiredBuildingAbility)
                && !PlacementRules.HasRequiredBuilding(player, hex, def))
            { reason = $"no owned '{def.requiredBuildingAbility}' building at the hex"; return false; }
            return true;
        }

        public static BuildingPlayResult PlayBaseCard(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, CardData card, HexCoord hex)
        {
            if (!PreflightBaseCard(player, root, hand, ctx, card, hex, out string reason))
                return BuildingPlayResult.Fail(reason);

            int apStart = root.ActionPoints;
            try
            {
                // SpawnBuilding refuses (returns null) BEFORE any mutation on a missing-config
                // argument and otherwise always succeeds; it touches no AP/resource/hand itself.
                BuildingData building = ctx.HexSelection.SpawnBuilding(card.Definition, hex, player);
                if (building == null)
                    return BuildingPlayResult.Fail("SpawnBuilding refused (missing scene config)");

                root.SpendActionPoints(card.EffectivePlayApCost);
                card.EffectivePlayResourceCost?.PayFrom(root);
                hand.Hand.Remove(card);

                return new BuildingPlayResult
                {
                    Built = true, CardConsumed = true, StateChanged = true,
                    ApSpent = apStart - root.ActionPoints,
                };
            }
            catch (Exception e)
            {
                AiDebugLog.Write($"[AI][V2][ERROR] BuildingPlayExecutor.PlayBaseCard threw after preflight: {e.Message}");
                return new BuildingPlayResult
                {
                    Built = false, StateChanged = true,
                    ApSpent = apStart - root.ActionPoints,
                    FailReason = "exception during build sequence",
                };
            }
        }

        // ------------------------------------------------------------------ Facility ----
        //  A CardType.Facility card placed into a free slot of one of the player's own Bases —
        //  the SAME operation CardHandUI.TryDeployFacilityToHex performs. This is the path that
        //  actually creates a Research/Production capability (WorldAnalysis.HasDevFacility reads
        //  BuildingData.HasFacilityWithAbility, i.e. a filled slot, not a building ability).
        public static bool PreflightFacilityCard(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, CardData card, HexCoord baseHex, out string reason, out BuildingData building)
        {
            reason = null;
            building = null;
            if (player == null || root == null || hand == null || card == null)
            { reason = "missing args"; return false; }
            CardDefinition def = card.Definition;
            if (def == null || def.cardType != CardType.Facility)
            { reason = $"card type {def?.cardType} is not a Facility"; return false; }
            if (!hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            building = BuildingRegistry.FindAt(baseHex);
            if (building == null || building.Owner != player || !building.IsBase)
            { reason = "no owned Base at the hex"; return false; }
            if (building.FindFirstAvailableFacilitySlot() < 0)
            { reason = "Base has no free Facility slot"; return false; }
            if (!root.CanSpendActionPoints(card.EffectivePlayApCost))
            { reason = $"need {card.EffectivePlayApCost} AP"; return false; }
            ResourceCost cost = card.EffectivePlayResourceCost;
            if (cost != null && !cost.CanAfford(root))
            { reason = "resource cost unaffordable"; return false; }
            return true;
        }

        public static BuildingPlayResult PlayFacilityCard(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, CardData card, HexCoord baseHex)
        {
            if (!PreflightFacilityCard(player, root, hand, ctx, card, baseHex, out string reason, out BuildingData building))
                return BuildingPlayResult.Fail(reason);

            int apStart = root.ActionPoints;
            try
            {
                int slotIndex = building.FindFirstAvailableFacilitySlot();
                if (slotIndex < 0)
                    return BuildingPlayResult.Fail("Base free slot vanished between preflight and play");

                root.SpendActionPoints(card.EffectivePlayApCost);
                card.EffectivePlayResourceCost?.PayFrom(root);
                building.FacilitySlots[slotIndex] = FacilityData.FromDefinition(card.Definition);
                hand.Hand.Remove(card);

                return new BuildingPlayResult
                {
                    Built = true, CardConsumed = true, StateChanged = true,
                    ApSpent = apStart - root.ActionPoints,
                };
            }
            catch (Exception e)
            {
                AiDebugLog.Write($"[AI][V2][ERROR] BuildingPlayExecutor.PlayFacilityCard threw after preflight: {e.Message}");
                return new BuildingPlayResult
                {
                    Built = false, StateChanged = true,
                    ApSpent = apStart - root.ActionPoints,
                    FailReason = "exception during facility deploy",
                };
            }
        }

        // -------------------------------------------------------- extraction site ----
        //  TryBuildExtractionFacility owns the whole transaction (hero-on-hex, AP, resources,
        //  hero move). These cards are never in hand — no hand boundary here.
        public static BuildingPlayResult BuildExtractionFacility(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, CardDefinition facilityDef, HexCoord hex)
        {
            if (player == null || root == null || ctx?.HexSelection == null || facilityDef == null)
                return BuildingPlayResult.Fail("missing args");
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, player))
                return BuildingPlayResult.Fail("no hero-led army on the resource hex");

            int apStart = root.ActionPoints;
            bool ok;
            try
            {
                ok = ctx.HexSelection.TryBuildExtractionFacility(facilityDef, hex, player);
            }
            catch (Exception e)
            {
                AiDebugLog.Write($"[AI][V2][ERROR] TryBuildExtractionFacility threw: {e.Message}");
                ok = false;
            }
            float apSpent = apStart - root.ActionPoints;
            return new BuildingPlayResult
            {
                Built = ok,
                StateChanged = ok || apSpent > 0f,
                ApSpent = apSpent,
                FailReason = ok ? null : "TryBuildExtractionFacility rejected the hex",
            };
        }
    }
}
