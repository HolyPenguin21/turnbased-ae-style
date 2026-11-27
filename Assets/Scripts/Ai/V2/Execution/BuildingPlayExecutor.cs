using System.Collections.Generic;
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
    public sealed class BuildingPlayResult
    {
        public bool Built;
        public float ApSpent;
        public bool StateChanged;
        public int StateVersionAfter = -1;
        public int AdditionalCardsConsumed;   // a synchronous founding defender, if any
        // The Base level bought for the slot stays bought even when the Facility placement then fails.
        public bool BaseLevelBought;
        public string FailReason;

        public static BuildingPlayResult Fail(string why) => new BuildingPlayResult { FailReason = why };
    }

    public static class BuildingPlayExecutor
    {
        // Non-mutating "could this Base card be founded here right now" — the SAME rule
        // InfrastructureActions.TryFoundBase enforces. Used by InfrastructureFulfillment's hex scan.
        public static bool CanFoundBaseAt(PlayerSetupData player, AiHandData hand, AiTurnContext ctx,
            CardData card, HexCoord hex, out string reason, bool requireCardInHand = true, bool resourceForecast = false)
        {
            reason = null;
            if (player == null || hand == null || ctx?.HexSelection == null || card?.Definition == null)
            { reason = "missing args"; return false; }
            if (requireCardInHand && !hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            return InfrastructureActions.CanFoundBase(card.Definition, hex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost, out reason, ctx.Map, requireCurrentResources: !resourceForecast);
        }

        public static bool CanPlaceFacilityAt(PlayerSetupData player, AiHandData hand, AiTurnContext ctx,
            CardData card, HexCoord baseHex, out string reason, bool requireCardInHand = true,
            bool requireCurrentAp = true, bool resourceForecast = false)
        {
            reason = null;
            if (player == null || hand == null || card?.Definition == null)
            { reason = "missing args"; return false; }
            if (requireCardInHand && !hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            return InfrastructureActions.CanPlaceFacility(card.Definition, baseHex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost, out reason, requireCurrentAp, !resourceForecast);
        }

        // -------------------------------------------------------------------- Base ----
        public static BuildingPlayResult PlayBaseCard(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, CardData card, HexCoord hex,
            int? builderArmyId = null, CardData immediateGarrisonCard = null)
        {
            if (player == null || hand == null || ctx?.HexSelection == null || card?.Definition == null)
                return BuildingPlayResult.Fail("missing args");
            if (!hand.Hand.Contains(card))
                return BuildingPlayResult.Fail("card not in hand");

            if (!PlanBaseGarrison(player, root, hand, ctx, card, hex, builderArmyId,
                    out CardData defender, out string guardReason, pinnedDefender: immediateGarrisonCard)
                || defender != immediateGarrisonCard)
                return BuildingPlayResult.Fail(guardReason ?? "garrison continuation changed");
            ResourceCost totalCost = BaseAndGarrisonCost(card, defender);
            int totalAp = card.EffectivePlayApCost + (defender == null ? 0 : CardCostRules.PlayAp(defender));
            if (!root.CanSpendActionPoints(totalAp) || !totalCost.CanAfford(root))
                return BuildingPlayResult.Fail("base and immediate garrison are not jointly affordable");

            // On a site the known enemy reaches the whole escort stays as the new garrison (the
            // builder took its whole roster for that, EconomyBuilderSelection); elsewhere one body.
            bool threatenedSite = AiArmyRoles.FoundingSiteThreat(player, hex, out _) > AiConfigV2.allocatorSliceEpsilon;
            using var mutation = WorldDeltaLifecycle.BeginTransaction();
            InfrastructureBuildOutcome outcome = InfrastructureActions.TryFoundBase(
                ctx.HexSelection, card.Definition, hex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost, completeBeforeCommit: _ =>
                {
                    if (defender == null)
                        LeaveGarrisonBody(player, ctx, hex, builderArmyId);
                    else
                    {
                        ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex, player);
                        CardPlayResult deployed = CardPlayExecutor.Play(player, root, hand, ctx,
                            CardPlayPlan.Into(defender, hex, DeploymentKind.Garrison, garrison), consumeCard: false);
                        if (!deployed.Deployed) return false;
                    }
                    if (threatenedSite)
                        LeaveEscortAsGarrison(player, ctx, hex, builderArmyId);
                    return ArmyRegistry.FindGarrisonAt(hex, player)?.Members
                        .Any(AiArmyRoles.IsGroundBattleBody) == true;
                });
            if (!outcome.Ok)
                return new BuildingPlayResult { FailReason = outcome.FailReason,
                    StateVersionAfter = WorldDeltaLifecycle.Current };

            // Gameplay is committed. A hand observer throwing after RemoveCard's mutation
            // must not leave the other paid card playable or report a failed founding.
            foreach (CardData consumed in new[] { defender, card })
            {
                if (consumed == null) continue;
                try { hand.RemoveCard(consumed); }
                catch (System.Exception e) { UnityEngine.Debug.LogException(e); }
            }
            return new BuildingPlayResult
            {
                Built = true, AdditionalCardsConsumed = defender == null ? 0 : 1,
                StateChanged = true, ApSpent = outcome.ApSpent,
                StateVersionAfter = mutation.Commit(player, ctx.TurnNumber,
                    new WorldDelta(true, StrategicInvalidationReason.None)),
            };
        }

        // Immediate, deterministic continuation only: a legal free transfer from the selected
        // builder, an already present garrison, or a held card deployed in this same call.
        // Never a later Phase B purchase, random draw, Challenge or forecast income.
        internal static bool PlanBaseGarrison(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, CardData baseCard, HexCoord hex, int? builderArmyId,
            out CardData defenderCard, out string reason,
            System.Func<CardData, bool> admitDefender = null, CardData pinnedDefender = null, bool resourceForecast = false, SpendAuthority authority = default)
        {
            defenderCard = null;
            reason = "no guaranteed ground garrison";
            if (player == null || root == null || hand == null || baseCard?.Definition == null)
                return false;
            if (!object.ReferenceEquals(root, PlayerRootRegistry.FindFor(player))) return false;
            ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex, player);
            if (garrison?.Members.Any(AiArmyRoles.IsGroundBattleBody) == true)
            { reason = null; return true; }
            if (garrison == null && baseCard.Definition.grantedAbilities?.Contains(UnitAbilities.Barracks) != true)
                return false;
            ArmyData projected = garrison ?? ArmyData.CreateVisualSnapshot();
            if (garrison == null)
            { projected.Owner = player; projected.Hex = hex; projected.IsGarrison = true; }
            foreach (ArmyData builder in ArmyRegistry.AllAt(hex).Where(a => a != null
                && a.Owner == player && !a.IsGarrison && AiArmyRoles.IsHeroLed(a)
                && (!builderArmyId.HasValue || a.Id == builderArmyId.Value)))
                if (builder.Members.Where(u => !u.IsHero && AiArmyRoles.IsGroundBattleBody(u))
                    .Any(u => (u.ActivationApCost <= 0 || !projected.RequiresActivationCharge(u))
                        && ArmyActions.CanTransferMembers(new[] { u }, builder, projected, out _)))
                { reason = null; return true; }
            var building = new BuildingData { Owner = player, Hex = hex, IsBase = true };
            foreach (string ability in (System.Collections.Generic.IEnumerable<string>)baseCard.Definition.grantedAbilities
                ?? System.Array.Empty<string>())
                building.Abilities.Add(ability);
            foreach (CardData candidate in hand.Hand.Where(c => c?.Definition != null
                && c.Definition.cardType == CardType.Unit && !c.Definition.isAviation
                && (pinnedDefender == null || c == pinnedDefender)))
            {
                if (!AiArmyRoles.IsGroundBattleBody(AiPower.ToDefenderProfile(candidate.Definition)))
                    continue;
                var plan = CardPlayPlan.Into(candidate, hex, DeploymentKind.Garrison, projected);
                if (!CardPlayExecutor.Preflight(player, root, hand, ctx, plan, out _,
                    resourceForecast: true, projectedBuilding: building))
                    continue;
                ResourceCost cost = BaseAndGarrisonCost(baseCard, candidate);
                if (!root.CanSpendActionPoints(baseCard.EffectivePlayApCost + plan.TotalApCost)
                    || !(resourceForecast ? TradeFunding.CanFund(player, root, ctx, cost, authority) : cost.CanAfford(root))
                    || (admitDefender != null && !admitDefender(candidate))) continue;
                defenderCard = candidate;
                reason = null;
                return true;
            }
            return false;
        }

        internal static ResourceCost BaseAndGarrisonCost(CardData baseCard, CardData defender)
        {
            ResourceCost a = baseCard?.EffectivePlayResourceCost;
            ResourceCost b = defender?.EffectivePlayResourceCost;
            return new ResourceCost {
                human = (a?.human ?? 0) + (b?.human ?? 0),
                energy = (a?.energy ?? 0) + (b?.energy ?? 0),
                materials = (a?.materials ?? 0) + (b?.materials ?? 0),
                tech = (a?.tech ?? 0) + (b?.tech ?? 0),
            };
        }

        // A threatened new Base keeps the builder's whole escort: every ground body that fits the
        // new garrison and joins it free stays; the hero walks on alone. Same builder and
        // free-transfer rule as LeaveGarrisonBody.
        private static void LeaveEscortAsGarrison(PlayerSetupData player, AiTurnContext ctx, HexCoord hex,
            int? builderArmyId)
        {
            ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex, player);
            if (garrison == null)
                return;
            foreach (ArmyData builder in ArmyRegistry.AllAt(hex).Where(a => a != null && !a.IsGarrison
                && a.Owner == player && !a.IsAirfield && !a.IsAirArmy && !a.IsPrison
                && a.Members.Any(u => u != null && u.IsHero)
                && (!builderArmyId.HasValue || a.Id == builderArmyId.Value)).OrderBy(a => a.Id).ToList())
            {
                var stayed = new List<string>();
                foreach (UnitData body in builder.Members
                             .Where(u => u != null && !u.IsHero && AiArmyRoles.IsGroundBattleBody(u))
                             .OrderByDescending(u => AiPower.ToPowerUnit(u).BasePower).ToList())
                {
                    if ((body.ActivationApCost > 0 && garrison.RequiresActivationCharge(body))
                        || !ArmyActions.CanTransferMembers(new[] { body }, builder, garrison, out _))
                        continue;
                    if (ArmyActions.TransferMember(body, builder, garrison, ctx.HexSelection, out _))
                        stayed.Add(body.Name);
                }
                if (stayed.Count > 0)
                    AiDebugLog.Write($"[AI][V2][Economy] {player.Nickname}: threatened new base at ({hex.Q},{hex.R}) "
                        + $"keeps the escort [{string.Join(",", stayed)}] as its garrison; builder #{builder.Id} "
                        + $"keeps {builder.Members.Count} member(s)");
            }
        }

        // A freshly founded Base is not left empty: one ground body of the builder stays as its free
        // garrison - the one that costs the builder most to carry (highest activation AP, then lowest
        // power). Nothing stays when the builder brought no body or the base already has a ground
        // defender.
        private static void LeaveGarrisonBody(PlayerSetupData player, AiTurnContext ctx, HexCoord hex,
            int? builderArmyId = null)
        {
            ArmyData garrison = ArmyRegistry.AllAt(hex).FirstOrDefault(a => a != null && a.IsGarrison
                && a.Owner == player);
            if (garrison == null || garrison.Members.Any(u => u != null && AiArmyRoles.IsGroundBattleBody(u)))
                return;
            // Only a HERO-LED army can have founded the base (InfrastructureActions.CanFoundBase), so
            // an unrelated hero-less army standing there is never raided for its body; when the
            // caller knows the builder, only that army is considered.
            foreach (ArmyData builder in ArmyRegistry.AllAt(hex).Where(a => a != null && !a.IsGarrison
                && a.Owner == player && !a.IsAirfield && !a.IsAirArmy && !a.IsPrison
                && a.Members.Any(u => u != null && u.IsHero)
                && (!builderArmyId.HasValue || a.Id == builderArmyId.Value)).OrderBy(a => a.Id))
            {
                UnitData body = builder.Members
                    .Where(u => u != null && !u.IsHero && AiArmyRoles.IsGroundBattleBody(u))
                    .OrderByDescending(u => u.ActivationApCost)
                    .ThenBy(u => AiPower.ToPowerUnit(u).BasePower)
                    .FirstOrDefault(u => (u.ActivationApCost <= 0 || !garrison.RequiresActivationCharge(u))
                        && ArmyActions.CanTransferMembers(new[] { u }, builder, garrison, out _));
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

            if (!CanPlaceFacilityAt(player, hand, ctx, card, baseHex, out string fundingReason, resourceForecast: true))
                return BuildingPlayResult.Fail(fundingReason);
            int rateBeforeFunding = root.TradeRate;
            if (!TradeFunding.Fund(player, root, ctx, card.EffectivePlayResourceCost))
                return new BuildingPlayResult { FailReason = "facility funding unavailable",
                    StateChanged = root.TradeRate != rateBeforeFunding,
                    StateVersionAfter = root.TradeRate != rateBeforeFunding ? WorldDeltaLifecycle.Current : -1 };

            InfrastructureBuildOutcome outcome = InfrastructureActions.TryPlaceFacility(
                card.Definition, baseHex, player,
                card.EffectivePlayApCost, card.EffectivePlayResourceCost);
            if (!outcome.Ok)
                return new BuildingPlayResult { Built = false, FailReason = outcome.FailReason,
                    StateChanged = root.TradeRate != rateBeforeFunding,
                    StateVersionAfter = root.TradeRate != rateBeforeFunding ? WorldDeltaLifecycle.Current : -1 };

            hand.RemoveCard(card);
            return new BuildingPlayResult
            {
                Built = true, StateChanged = true, ApSpent = outcome.ApSpent,
                StateVersionAfter = WorldDeltaLifecycle.CommitMutation(),
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

            InfrastructureBuildOutcome outcome = ctx.HexSelection.BuildExtractionSite(facilityDef, hex, player);
            return new BuildingPlayResult
            {
                Built = outcome.Ok,
                StateChanged = outcome.Ok,
                ApSpent = outcome.ApSpent,
                StateVersionAfter = outcome.Ok ? WorldDeltaLifecycle.CommitMutation() : -1,
                FailReason = outcome.Ok ? null : outcome.FailReason,
            };
        }
    }
}

