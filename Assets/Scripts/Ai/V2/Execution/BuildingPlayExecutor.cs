using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  BUILDING PLAY EXECUTOR  (Strategy V2 — infrastructure card execution)
    // ===========================================================================================
    //  A THIN AI-side wrapper over the shared authoritative domain transaction
    //  Game.Map.InfrastructureActions (the SAME entry point the human UI uses). This class no
    //  longer assembles any spend/create/refund sequence itself — it only:
    //    · resolves the card instance's effective play cost,
    //    · calls InfrastructureActions.TryFoundBase / TryPlaceFacility / TryBuildExtractionSite,
    //    · removes the AI's own card from AiHandData.Hand ONLY on Ok.
    //  The hero-built extraction path also goes through InfrastructureActions.TryBuildExtractionSite
    //  (same as the human UI): the world primitive HexSelectionController.TryBuildExtractionFacility
    //  is NOT atomic on its own — the wrapper captures and restores the full pre-transaction state
    //  (AP, resources, facility slot, hero move points, a half-registered new site) on a throw.
    // ===========================================================================================
    public sealed class BuildingPlayResult : IV2ActionResult
    {
        public bool Built;
        public float ApSpent;
        public ResourceCost ResourcesSpent;   // the founding card's resource cost on a success (null = none / n/a)
        public bool StateChanged;
        public int StateVersionAfter = -1;
        public bool CardConsumed;
        public string FailReason;

        public static BuildingPlayResult Fail(string why) => new BuildingPlayResult { FailReason = why };

        public V2ActionOutcome Outcome => new V2ActionOutcome(
            succeeded: Built, stateChanged: StateChanged, apSpent: ApSpent, resourcesSpent: ResourcesSpent,
            played: CardConsumed, generated: false, attached: false, moved: false, created: Built,
            needsReplan: false, stateVersionAfter: StateVersionAfter, failReason: Built ? null : FailReason);
    }

    public static class BuildingPlayExecutor
    {
        // Non-mutating "could this Base card be founded here right now" — the SAME rule
        // InfrastructureActions.TryFoundBase enforces. Used by InfrastructureFulfillment's hex scan.
        public static bool CanFoundBaseAt(PlayerSetupData player, AiHandData hand, AiTurnContext ctx,
            CardData card, HexCoord hex, out string reason, bool requireCardInHand = true)
        {
            reason = null;
            if (player == null || hand == null || ctx?.HexSelection == null || card?.Definition == null)
            { reason = "missing args"; return false; }
            if (requireCardInHand && !hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            return InfrastructureActions.CanFoundBase(card.Definition, hex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost, out reason, ctx.Map);
        }

        public static bool CanPlaceFacilityAt(PlayerSetupData player, AiHandData hand, AiTurnContext ctx,
            CardData card, HexCoord baseHex, out string reason, bool requireCardInHand = true)
        {
            reason = null;
            if (player == null || hand == null || card?.Definition == null)
            { reason = "missing args"; return false; }
            if (requireCardInHand && !hand.Hand.Contains(card))
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

            hand.RemoveCard(card);   // caller-owned hand, only on success
            LeaveGarrisonBody(player, ctx, hex);
            return new BuildingPlayResult
            {
                Built = true, CardConsumed = true, StateChanged = true, ApSpent = outcome.ApSpent,
                ResourcesSpent = card.EffectivePlayResourceCost,
                StateVersionAfter = V2StateVersion.Bump(),
            };
        }

        // Project owner, 2026-10-02: a freshly founded Base is not left empty. The builder's
        // army (the hero and whatever it brought) stands on the hex; one ground body stays as the
        // new garrison so the base is not taken on the same or the next turn by anything that walks
        // past. The builder keeps the rest and may stay or go on. Best effort and free (the
        // garrison never activates): nothing happens when the builder brought no body, the base
        // already has a ground defender, or the hero would be left with nothing to travel with.
        // The body that costs the builder most to carry (highest activation AP, then lowest power)
        // is the one that stays.
        internal static void LeaveGarrisonBody(PlayerSetupData player, AiTurnContext ctx, HexCoord hex)
        {
            ArmyData garrison = ArmyRegistry.AllAt(hex).FirstOrDefault(a => a != null && a.IsGarrison
                && a.Owner == player);
            if (garrison == null || garrison.Members.Any(u => u != null && AiArmyRoles.IsGroundBattleBody(u)))
                return;
            foreach (ArmyData builder in ArmyRegistry.AllAt(hex).Where(a => a != null && !a.IsGarrison
                && a.Owner == player && !a.IsAirfield && !a.IsAirArmy && !a.IsPrison)
                .OrderByDescending(a => a.Members.Any(u => u != null && u.IsHero)).ThenBy(a => a.Id))
            {
                UnitData body = builder.Members
                    .Where(u => u != null && !u.IsHero && AiArmyRoles.IsGroundBattleBody(u))
                    .OrderByDescending(u => u.ActivationApCost)
                    .ThenBy(u => AiPower.ToPowerUnit(u).BasePower)
                    .FirstOrDefault();
                if (body == null)
                    continue;
                if (ArmyActions.TransferMember(body, builder, garrison, ctx.HexSelection, out string why))
                {
                    AiDebugLog.Write($"[AI][V2][Economy] {player.Nickname}: \"{body.Name}\" stays as the garrison of "
                        + $"the new base at ({hex.Q},{hex.R}); builder #{builder.Id} keeps {builder.Members.Count} member(s)");
                    return;
                }
                AiDebugLog.Write($"[AI][V2][Economy] new base at ({hex.Q},{hex.R}): could not leave "
                    + $"{body.Name} as garrison: {why}");
            }
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

            hand.RemoveCard(card);
            return new BuildingPlayResult
            {
                Built = true, CardConsumed = true, StateChanged = true, ApSpent = outcome.ApSpent,
                ResourcesSpent = card.EffectivePlayResourceCost,
                StateVersionAfter = V2StateVersion.Bump(),
            };
        }

        // -------------------------------------------------------- extraction site ----
        //  Delegates to the shared transaction (measures + refunds the AP/resource delta on a
        //  throw or a false-but-spent outcome) — no hand card is involved.
        public static BuildingPlayResult BuildExtractionFacility(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, CardDefinition facilityDef, HexCoord hex)
        {
            if (player == null || root == null || ctx?.HexSelection == null || facilityDef == null)
                return BuildingPlayResult.Fail("missing args");

            InfrastructureBuildOutcome outcome = InfrastructureActions.TryBuildExtractionSite(
                ctx.HexSelection, facilityDef, hex, player);
            return new BuildingPlayResult
            {
                Built = outcome.Ok,
                StateChanged = outcome.Ok,
                ApSpent = outcome.ApSpent,
                // The site charges the facility definition's resourceCost (same figure
                // InfrastructureFulfillment admits the build against). No hand card is consumed.
                ResourcesSpent = outcome.Ok ? facilityDef.resourceCost : null,
                StateVersionAfter = outcome.Ok ? V2StateVersion.Bump() : -1,
                FailReason = outcome.Ok ? null : outcome.FailReason,
            };
        }
    }
}
