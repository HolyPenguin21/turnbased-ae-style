using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ---- tempo candidate model ------------------------------------------------------------
    internal enum TempoKind { PlayMat, PlayNonCombat, Draw, MaintenanceSpend, PressureSpend, Hold, EndTurn }

    internal sealed class TempoCandidate
    {
        public TempoKind Kind;
        public string ActionKey;
        public float Utility;
        public float ApCost;              // spec §6 — must fit SPENDABLE (not raw) AP
        public ResourceCost ResCost;      // spec §6 — full persistent-resource cost vector (null = none)
        public bool ConsumesGeneration;   // spec §P0 — shared maxGenerationActionsPerTurn budget
        public bool CountsAsTerminalDraw;    // spec §P0 — maxTerminalDrawsPerTurn sub-cap
        public string Label;
        public string DrawDiag;   // Draw only — preformatted valuation breakdown for the log
        public MatSurplusDecision Mat;
        public NonCombatCardPlayer.NonCombatPlay Nc;
        public StrategicPressurePlan Pressure;
        public StrategicSpendCandidate Spend;   // non-card strategic spend — executed verbatim
    }

    // AI-MGR-01 review-r4 finding 9a — the materialization-surplus lane's per-iteration decision.
    internal struct MatSurplusDecision
    {
        public bool Admissible;
        public MaterializationPlan Plan;
        public float Utility;
        public AxisDemand Residual;      // non-null => operational strategic residual
        public CapabilityInventory Inv;
     }

    // ARCH-02 §8/§39/§42 — the Phase-B tempo candidate provider. Builds the ONE comparable
    // candidate space (PlayCard mat / PlayCard non-combat / Draw / non-card strategic spend /
    // pressure advance / Hold / EndTurn) for the arbiter loop in StrategicPhaseB. It scores each
    // candidate with the canonical owners (StrategicCardEvaluator via the builders, HoldEvaluator,
    // the Draw expected-deck-value model) and applies only STRUCTURAL admission guards; it does not
    // select or execute. Extracted verbatim from StrategicManager.
    internal static class TempoCandidateProvider
    {
        internal static bool IsSpend(TempoKind k) =>
            k == TempoKind.PlayMat || k == TempoKind.PlayNonCombat || k == TempoKind.Draw
            || k == TempoKind.MaintenanceSpend || k == TempoKind.PressureSpend;

        internal static List<TempoCandidate> BuildTempoCandidates(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            StrategicPhaseResult result, IReadOnlyList<ReconObjective> reconObjectives, float spendableAp,
            StrategicTempoBudget budget, bool verbose)
        {
            var list = new List<TempoCandidate>();

            // AI-MGR — the ONE shared Phase-B owner-witnessed AP workload for this tempo iteration,
            // from the REAL feasible candidate universe across BOTH lanes. The same scalar goes to
            // the materialization lane (RankedSurplus/ScoreSurplus) and the non-combat lane
            // (NonCombatCardPlayer/ScoreNonCombat), so an ApBonus Unit/Hero and an ApBonus
            // Base/Facility are priced off one number. null unless a recurring-resource carrier is
            // reachable this turn (=> both lanes keep the discounted structural fallback).
            float? phaseBWitnessedApDemand = MaterializationCandidateBuilder.PhaseBWitnessedApWorkload(
                snap, player, root, hand, ctx,
                CapabilityInventory.Build(snap, player, commitments), commitments, result.Reservation);

            // PlayCard — materialization lane. Utility = StrategicCardEvaluator decision score, verbatim.
            foreach (MatSurplusDecision mat in ComputeMatDecisions(snap, player, root, hand, ctx,
                         commitments, result, reconObjectives, phaseBWitnessedApDemand, verbose))
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PlayMat, Mat = mat, Utility = mat.Utility,
                    ApCost = mat.Plan.ApCost, ResCost = mat.Plan.ResCost,
                    ConsumesGeneration = mat.Plan.Generation != null,
                    ActionKey = "mat:" + mat.Plan.StableKey,
                    Label = $"{mat.Plan.Kind} {AiCardLog.Plan(mat.Plan)}"
                        + (mat.Residual != null ? $" (residual {mat.Residual.Capability})" : ""),
                });

            // Every legal non-combat play enters the same arbitration set. Selecting a lane winner
            // before reservation/cap/parking checks used to hide a cheaper legal fallback.
            var nonCombatBlocked = new List<string>();
            foreach (NonCombatCardPlayer.NonCombatPlay nc in NonCombatCardPlayer.EnumeratePlays(
                         snap, player, root, hand, ctx, nonCombatBlocked, result.Reservation,
                         phaseBWitnessedApDemand))
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PlayNonCombat, Nc = nc, Utility = nc.Score,
                    ApCost = nc.ApCost, ResCost = nc.ResCost,
                    ConsumesGeneration = nc.Generation != null,
                    ActionKey = "nc:" + nc.StableKey,
                    Label = $"{nc.Kind} {nc.Explain}",
                });
            if (verbose)
                foreach (string reason in nonCombatBlocked
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(System.StringComparer.Ordinal)
                    .OrderBy(x => x, System.StringComparer.Ordinal))
                    AiDebugLog.WriteVerbose($"[AI][V2]     cand PlayNonCombat BLOCKED: {reason}");

            // §P0.1 — only card alternatives actually selectable under the shared generation
            // budget and live spendable pools suppress Draw. Structurally blocked cards do not.
            bool CardSelectableNow(TempoCandidate c) => c != null
                && (!c.ConsumesGeneration || !budget.GenerationCapHit)
                && c.ApCost <= spendableAp + AiConfigV2.allocatorSliceEpsilon
                && StrategicSpendability.FitsSpendableResources(player, root, ctx, c.ResCost);
            TempoCandidate bestSelectablePlayCandidate = list
                .Where(c => (c.Kind == TempoKind.PlayMat || c.Kind == TempoKind.PlayNonCombat)
                    && CardSelectableNow(c))
                .OrderByDescending(c => c.Utility)
                .ThenBy(c => c.ActionKey, System.StringComparer.Ordinal)
                .FirstOrDefault();
            float bestSelectablePlay = bestSelectablePlayCandidate?.Utility ?? 0f;

            // T5 regression: with one card and exactly DrawCost AP, the one-step arbiter preferred
            // a modest 1-AP play, emptied the hand and stranded the final AP. Preserve that option
            // value only when the winning card play actually consumes the last hand card AND would
            // make a follow-up draw unaffordable. With enough AP to play then draw, no bonus applies.
            bool lastCardPlayWouldStrandDraw = hand.Hand.Count == 1
                && bestSelectablePlayCandidate != null
                && ConsumesHandCard(bestSelectablePlayCandidate)
                && spendableAp - bestSelectablePlayCandidate.ApCost
                    + AiConfigV2.allocatorSliceEpsilon < ctx.DrawApCost;

            // DrawCard — a real scored peer (spec §1), NOT penalised for holding H/E/M/T (costs AP only).
            bool canCycle = CardDrawExecutor.CanCycle(root, hand, ctx);
            bool fitsSpendableDrawAp =
                spendableAp + AiConfigV2.allocatorSliceEpsilon >= ctx.DrawApCost;
            if (AiConfigV2.surplusAllowDraw && canCycle && fitsSpendableDrawAp)
            {
                float drawU = DrawCandidateUtility(snap, hand, ctx, bestSelectablePlay,
                    lastCardPlayWouldStrandDraw, out string drawDiag);
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.Draw, Utility = drawU, ApCost = ctx.DrawApCost, ActionKey = "draw",
                    CountsAsTerminalDraw = true, DrawDiag = drawDiag,
                    Label = $"cycle 1 card ({ctx.DrawApCost} AP), hand {hand.Hand.Count}/{ctx.HandCapacity}",
                });
            }
            else if (AiConfigV2.surplusAllowDraw && hand.HasFreeSlot && hand.HasCardsLeftToDraw)
            {
                // A structurally absent candidate used to vanish from the shortlist. Log only the
                // dynamic AP refusal (full hand / empty deck are already explicit in the FRAME and
                // tempo headers), including whether raw AP or a reservation caused it.
                string reason = !root.CanSpendActionPoints(ctx.DrawApCost)
                    ? $"raw AP {root.ActionPoints} < cost {ctx.DrawApCost}"
                    : $"spendable AP {F(spendableAp)} < cost {ctx.DrawApCost}";
                AiDebugLog.Write($"[AI][V2]     cand Draw BLOCKED: {reason}; hand "
                    + $"{hand.Hand.Count}/{ctx.HandCapacity} deck {hand.RemainingDeckCount}");
            }

            // ExistingStrategicSpendAction — genuinely NON-CARD strategic actions only (Base/Citadel
            // slot-capacity upgrade). Facility / Equipment / generation are ordinary PlayCard
            // candidates above (one StrategicCardEvaluator, spec §5). Every eligible non-card spend
            // is its own candidate — no hidden category priority chain (spec §3).
            foreach (StrategicSpendCandidate sp in StrategicMaintenancePolicy.EnumerateCandidates(
                player, root, hand, ctx, snap, phaseBWitnessedApDemand))
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.MaintenanceSpend, Utility = sp.Utility, ApCost = sp.ApCost,
                    ResCost = sp.ResCost, Spend = sp,
                    ActionKey = "maint:" + sp.StableKey, Label = sp.Label,
                });

            StrategicPressurePlan pressure = StrategicPressureAdvance.BuildPlan(player, root, hand, ctx, commitments);
            if (pressure != null && pressure.Army != null)
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PressureSpend, Pressure = pressure,
                    Utility = AiConfigV2.tempoPressureAdvanceValue,
                    ApCost = pressure.Army.HasActivatedThisTurn ? 0f : pressure.Army.ActivationApCost,
                    ActionKey = "pressure:" + pressure.Army.Id,
                    Label = $"advance army #{pressure.Army.Id} toward known enemy Citadel "
                        + $"({pressure.TargetHex.Q},{pressure.TargetHex.R})",
                });

            // HoldResources — the value of NOT spending. AP is lost at EndTurn so holding it is ~0;
            // the loose persistent-resource pool is worth holding only when the economy is fragile.
            // (Per-card hold value is already inside every PlayCard NetScore — spec §5.)
            list.Add(new TempoCandidate
            {
                Kind = TempoKind.Hold, ActionKey = "hold",
                Utility = HoldEvaluator.HoldResourcesUtility(root, snap),
                Label = "keep unspent resources for future turns",
            });
            list.Add(new TempoCandidate
            {
                Kind = TempoKind.EndTurn, ActionKey = "endturn", Utility = 0f, Label = "end the turn",
            });
            return list;
        }

        // spec §1/§P0.1/§P1.6 — expected value of converting stranded AP into a fresh card option,
        // in the same [~0..5] band the PlayCard candidates use. Terms:
        //   · expectedDeckValue  = floor + normalised mean remaining-deck STRATEGIC card value
        //                          (combat power + generic role coverage + equipment/infra profile),
        //                          tapered when the deck is nearly empty;
        //   · fill factor         (softened — a single free slot is still a legal, ~0.70 draw);
        //   · last-slot block risk (softened to a small penalty);
        //   · AP opportunity cost;
        //   · handQualityPenalty  = weight * the best play SELECTABLE RIGHT NOW (0 if every card
        //                          alternative is blocked by budget / affordability / placement).
        private static float DrawCandidateUtility(WorldSnapshot snap, AiHandData hand, AiTurnContext ctx,
            float bestSelectablePlay, bool lastCardPlayWouldStrandDraw, out string diag)
        {
            int freeSlots = Mathf.Max(0, ctx.HandCapacity - hand.Hand.Count);
            float fill = Mathf.Clamp(AiConfigV2.tempoDrawFillFloor
                + AiConfigV2.tempoDrawFillPerSlot * freeSlots, 0f, 1f);

            var deck = hand.RemainingDeck?.Where(d => d != null).ToList();
            float deckMean = 0f;
            if (deck != null && deck.Count > 0)
            {
                float sum = 0f;
                foreach (CardDefinition d in deck)
                    sum += GenericStrategicCardValue(d);
                deckMean = sum / deck.Count;
            }
            float deckValue = Mathf.Clamp01(deckMean / Mathf.Max(1f, AiConfigV2.tempoDrawDeckValueNorm));
            float thinTaper = deck == null ? 0f
                : Mathf.Clamp01(deck.Count / Mathf.Max(1f, AiConfigV2.tempoDrawThinDeckTaperCards));
            float expectedDeckValue =
                (AiConfigV2.tempoDrawBaseValue + AiConfigV2.tempoDrawDeckValueWeight * deckValue) * thinTaper;

            float blockRisk = freeSlots <= 1 ? AiConfigV2.tempoDrawFutureBlockPenalty : 0f;
            float apOpp = AiConfigV2.tempoDrawApOpportunityWeight * ctx.DrawApCost;
            float handQualityPenalty = AiConfigV2.tempoDrawHandActionableWeight * Mathf.Max(0f, bestSelectablePlay);
            float continuityBonus = lastCardPlayWouldStrandDraw
                ? AiConfigV2.tempoDrawLastCardContinuityBonus : 0f;

            float u = expectedDeckValue * fill - blockRisk - apOpp - handQualityPenalty
                + continuityBonus;
            diag = $"expDeckVal {F(expectedDeckValue)} (mean {F(deckMean)} taper {F(thinTaper)}) freeSlots {freeSlots} "
                + $"fill {F(fill)} blockRisk {F(blockRisk)} apOpp {F(apOpp)} handQualPen {F(handQualityPenalty)} "
                + $"continuity +{F(continuityBonus)} (selectablePlay {F(bestSelectablePlay)}) => draw {F(u)}";
            return u;
        }

        // spec §P1.6 — a lightweight GENERIC strategic value for an unseen deck card (the concrete
        // card is not drawn yet, so this is not a second StrategicCardEvaluator). Combat body power
        // + one bump per generic strategic role the card's granted abilities cover (AoE / Regen /
        // Aura / Summon / … via StrategicEffectRegistry), plus a flat profile for the non-combat
        // card families.
        private static float GenericStrategicCardValue(CardDefinition d)
        {
            if (d == null) return 0f;
            switch (d.cardType)
            {
                case CardType.Equipment:
                    return AiConfigV2.tempoDrawEquipmentValue;
                case CardType.Base:
                case CardType.Facility:
                    return AiConfigV2.tempoDrawInfraValue;
                case CardType.Unit:
                case CardType.Hero:
                default:
                {
                    float v = Mathf.Max(0f, AiPower.ToPowerUnit(d).BasePower);
                    if (d.grantedAbilities != null && d.grantedAbilities.Count > 0)
                    {
                        int roles = StrategicEffectRegistry
                            .Roles(d.grantedAbilities, Mathf.Max(1, d.moveMax)).Distinct().Count();
                        v += roles * AiConfigV2.tempoDrawEffectRoleValue;
                    }
                    return v;
                }
            }
        }

        private static bool ConsumesHandCard(TempoCandidate c)
        {
            if (c == null) return false;
            if (c.Kind == TempoKind.PlayNonCombat)
                return c.Nc?.Card != null;
            if (c.Kind == TempoKind.PlayMat)
                return c.Mat.Plan?.BaseCardInHand != null || c.Mat.Plan?.EquipmentInHand != null;
            return false;
        }

        private static bool PlanBaseIsHeroCard(MaterializationPlan plan)
        {
            CardDefinition def = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            return def != null && def.cardType == CardType.Hero;
        }

        // §P1 — multiplier on the surplus-admission threshold for a generic garrison deposit when
        // the garrison is already a strong defensive stack (>= a fraction of BestStackPotential)
        // and no asset is threatened. 1f otherwise.
        private static bool GarrisonSaturated(WorldSnapshot snap, MaterializationPlan plan,
            AxisDemand residual)
        {
            if (residual != null || plan == null || plan.Deploy.Kind != DeploymentKind.Garrison
                || snap?.Self == null)
                return false;
            bool assetThreat = snap.Threat?.Threats != null && snap.Threat.Threats.Count > 0;
            if (assetThreat)
                return false;
            float reserve = AiConfigV2.garrisonSaturatedReserveFractionOfBestStack
                * Mathf.Max(0f, snap.Self.BestStackPotential);
            return reserve > 0f && snap.Self.GarrisonPower >= reserve;
        }

        // §P1 — generic surplus must not add a scout beyond the physical IsSoloRecce portfolio
        // cap, across EVERY chain kind (RankedSurplus treats a recce card as ScoutCapability and
        // will build NewArmy / ReusableShell / Attach / Generate placements for it — the Recon
        // DemandLayer portfolio cap never sees those). Primary bound is the CURRENT desired
        // concurrency + a warm spare; ReconConcurrencyPolicy.HardCap is the absolute ceiling.
        private static bool ScoutSurplusPortfolioSaturated(PlayerSetupData player, MaterializationPlan plan,
            WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives)
        {
            if (plan == null || plan.FinalCapability != CapabilityKind.ScoutCapability)
                return false;
            int solo = ArmyRegistry.AllForOwner(player).Count(a => a != null && AiArmyRoles.IsSoloRecce(a));
            if (solo >= ReconConcurrencyPolicy.HardCap)
                return true;
            if (reconObjectives == null)
                return false;
            var runnable = reconObjectives
                .Where(o => o != null && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            int desired = ReconConcurrencyPolicy.DesiredTotal(snap, runnable);
            return solo >= desired + AiConfigV2.scoutSurplusWarmSpare;
        }

        private static List<MatSurplusDecision> ComputeMatDecisions(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            ActorCommitments commitments, StrategicPhaseResult result,
            IReadOnlyList<ReconObjective> reconObjectives, float? witnessedUsefulApDemand,
            bool verbose)
        {
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            List<(MaterializationPlan plan, float utility)> ranked =
                MaterializationCandidateBuilder.RankedSurplus(
                    snap, player, root, hand, ctx, inv, commitments, result.Reservation,
                    witnessedUsefulApDemand);
            var admitted = new List<MatSurplusDecision>();
            foreach ((MaterializationPlan plan, float utility) in ranked)
            {
                AxisDemand matchedResidual = result.Reservation.BestUnresolvedDemandFor(plan);
                AxisDemand residual = matchedResidual != null
                    && MaterializationDeliveryPolicy.CanDeliverDemandOperationally(plan, matchedResidual)
                        ? matchedResidual : null;

                if (matchedResidual != null && residual == null
                    && matchedResidual.Capability == CapabilityKind.Hero && PlanBaseIsHeroCard(plan))
                {
                    if (verbose)
                        AiDebugLog.Write($"[AI][V2]   strat.B — hold {plan.StableKey}: hero card matches "
                            + $"unresolved {matchedResidual} but no placement delivers it");
                    continue;
                }

                if (GarrisonSaturated(snap, plan, residual)
                    && plan.Score < AiConfigV2.garrisonSaturatedMinUtility)
                {
                    if (verbose)
                        AiDebugLog.Write($"[AI][V2]   strat.B — defer {plan.StableKey} {AiCardLog.Plan(plan)} "
                            + $"score {F(plan.Score)} < garrison-saturated bar "
                            + $"{F(AiConfigV2.garrisonSaturatedMinUtility)}");
                    continue;
                }
                if (residual == null
                    && ScoutSurplusPortfolioSaturated(player, plan, snap, reconObjectives))
                {
                    if (verbose)
                        AiDebugLog.Write($"[AI][V2]   strat.B — hold {plan.StableKey} {AiCardLog.Plan(plan)}: "
                            + "generic surplus would add a scout beyond the physical portfolio");
                    continue;
                }

                admitted.Add(new MatSurplusDecision
                {
                    Admissible = true,
                    Plan = plan,
                    Utility = utility,
                    Residual = residual,
                    Inv = inv,
                });
            }
            return admitted;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
