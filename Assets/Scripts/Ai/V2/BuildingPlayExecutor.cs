using System;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  BUILDING PLAY EXECUTOR  (Strategy V2 — infrastructure card execution)
    // ===========================================================================================
    //  A THIN AI-side wrapper over the shared authoritative domain transaction
    //  Game.Map.InfrastructureActions (the SAME entry point the human UI uses). This class no
    //  longer assembles any spend/create/refund sequence itself — it only:
    //    · resolves the card instance's effective play cost,
    //    · calls InfrastructureActions.TryFoundBase / TryPlaceFacility,
    //    · removes the AI's own card from AiHandData.Hand ONLY on Ok.
    //  The hero-built extraction facility path stays on HexSelectionController
    //  .TryBuildExtractionFacility, which is itself already fully atomic.
    // ===========================================================================================
    public sealed class BuildingPlayResult
    {
        public bool Built;
        public float ApSpent;
        public bool StateChanged;
        public bool CardConsumed;
        public string FailReason;

        public static BuildingPlayResult Fail(string why) => new BuildingPlayResult { FailReason = why };
    }

    public static class BuildingPlayExecutor
    {
        // Non-mutating "could this Base card be founded here right now" — the SAME rule
        // InfrastructureActions.TryFoundBase enforces. Used by InfrastructureFulfillment's hex scan.
        public static bool CanFoundBaseAt(PlayerSetupData player, AiHandData hand, AiTurnContext ctx,
            CardData card, HexCoord hex, out string reason)
        {
            reason = null;
            if (player == null || hand == null || ctx?.HexSelection == null || card?.Definition == null)
            { reason = "missing args"; return false; }
            if (!hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            return InfrastructureActions.CanFoundBase(card.Definition, hex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost, out reason);
        }

        public static bool CanPlaceFacilityAt(PlayerSetupData player, AiHandData hand, AiTurnContext ctx,
            CardData card, HexCoord baseHex, out string reason)
        {
            reason = null;
            if (player == null || hand == null || card?.Definition == null)
            { reason = "missing args"; return false; }
            if (!hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            return InfrastructureActions.CanPlaceFacility(card.Definition, baseHex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost, out reason);
        }

        // -------------------------------------------------------------------- Base ----
        public static BuildingPlayResult PlayBaseCard(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, CardData card, HexCoord hex)
        {
            if (player == null || hand == null || ctx?.HexSelection == null || card?.Definition == null)
                return BuildingPlayResult.Fail("missing args");
            if (!hand.Hand.Contains(card))
                return BuildingPlayResult.Fail("card not in hand");

            InfrastructureBuildOutcome outcome = InfrastructureActions.TryFoundBase(
                ctx.HexSelection, card.Definition, hex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost);
            if (!outcome.Ok)
                return new BuildingPlayResult { Built = false, FailReason = outcome.FailReason };

            hand.Hand.Remove(card);   // caller-owned hand, only on success
            return new BuildingPlayResult
            {
                Built = true, CardConsumed = true, StateChanged = true, ApSpent = outcome.ApSpent,
            };
        }

        // ---------------------------------------------------------------- Facility ----
        public static BuildingPlayResult PlayFacilityCard(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, CardData card, HexCoord baseHex)
        {
            if (player == null || hand == null || card?.Definition == null)
                return BuildingPlayResult.Fail("missing args");
            if (!hand.Hand.Contains(card))
                return BuildingPlayResult.Fail("card not in hand");

            InfrastructureBuildOutcome outcome = InfrastructureActions.TryPlaceFacility(
                card.Definition, baseHex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost);
            if (!outcome.Ok)
                return new BuildingPlayResult { Built = false, FailReason = outcome.FailReason };

            hand.Hand.Remove(card);
            return new BuildingPlayResult
            {
                Built = true, CardConsumed = true, StateChanged = true, ApSpent = outcome.ApSpent,
            };
        }

        // -------------------------------------------------------- extraction site ----
        //  TryBuildExtractionFacility owns the whole transaction (hero-on-hex, AP, resources,
        //  hero move) — no hand card is involved.
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
