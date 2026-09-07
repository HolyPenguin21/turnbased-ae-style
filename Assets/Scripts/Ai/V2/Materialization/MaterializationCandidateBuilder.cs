using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    public readonly struct PlacementOption
    {
        public readonly HexCoord Hex;
        public readonly DeploymentKind Kind;
        public readonly ArmyData Army;

        public PlacementOption(HexCoord hex, DeploymentKind kind, ArmyData army)
        {
            Hex = hex;
            Kind = kind;
            Army = army;
        }

        public CardPlayPlan Bind(CardData card) =>
            Kind == DeploymentKind.NewArmy
                ? CardPlayPlan.NewArmyAt(card, Hex)
                : CardPlayPlan.Into(card, Hex, Kind, Army);

        public string Key => $"{(int)Kind}:{Hex.Q},{Hex.R}:{(Army != null ? Army.Id : -1)}";
    }

    internal static class PlacementSelector
    {
        public static List<PlacementOption> BuildOptions(WorldSnapshot snap, PlayerSetupData player,
            CardDefinition def, ActorCommitments commitments, bool soloOnly)
        {
            var opts = new List<PlacementOption>();
            if (def == null || snap?.Self?.BaseHexes == null || player == null)
                return opts;

            bool isUnit = def.cardType == CardType.Unit;
            bool isHero = def.cardType == CardType.Hero;
            List<ArmyData> own = ArmyRegistry.AllForOwner(player).Where(a => a != null).OrderBy(a => a.Id).ToList();

            foreach (HexCoord hex in snap.Self.BaseHexes)
            {
                if (!PlacementRules.HasRequiredBuilding(player, hex, def))
                    continue;

                ArmyData shell = ReusableArmySelector.FindReusableAt(player, hex, commitments);
                if (shell != null)
                    opts.Add(new PlacementOption(hex, DeploymentKind.ReusableShell, shell));
                opts.Add(new PlacementOption(hex, DeploymentKind.NewArmy, null));

                if (soloOnly)
                    continue;

                foreach (ArmyData a in own)
                {
                    if (!a.Hex.Equals(hex) || a.IsPrison)
                        continue;
                    if (a.IsGarrison)
                    {
                        if (PlacementRules.CanDepositIntoGarrison(a)
                            && CardPlayExecutor.CanFitAfterDeploy(a, def))
                            opts.Add(new PlacementOption(hex, DeploymentKind.Garrison, a));
                        continue;
                    }
                    // Projected capacity, not pre-join HasRoom: a first hero may legally turn a
                    // full 2/2 body formation into 3/N and is exactly the placement a live Hero
                    // strategic demand needs. CardPlayExecutor/ArmyActions enforce the same rule.
                    if (a.Members.Count == 0 || !CardPlayExecutor.CanFitAfterDeploy(a, def))
                        continue;
                    // IsPlainReserveArmy intentionally means "heroless AND currently has room" for
                    // generic placement callers. A Hero card is the one exception here: it may lead
                    // a full heroless non-Recce ground formation when its projected CommandRating
                    // creates the needed slot. Keep that exception local to prospective Hero-card
                    // placement instead of weakening the global reserve-army predicate for Units.
                    bool heroCanLeadFullFormation = isHero
                        && !a.IsAirfield && !a.IsAirArmy
                        && !AbilityParams.ArmyHasAnyRecce(a)
                        && a.Members.All(u => u != null && !u.IsHero && !u.IsAviation);
                    bool ok = AiArmyRoles.IsPlainReserveArmy(a)
                        || heroCanLeadFullFormation
                        || (isUnit && AiArmyRoles.IsHeroLedCombatArmy(a));
                    if (ok)
                        opts.Add(new PlacementOption(hex, DeploymentKind.ExistingArmy, a));
                }
            }
            return opts;
        }
    }

    public sealed class MaterializationReservation
    {
        public readonly HashSet<string> ClaimedGeneratorUses = new HashSet<string>();
        public readonly HashSet<string> TriedGeneratorCards = new HashSet<string>();
        public readonly List<AxisDemand> UnresolvedDemands = new List<AxisDemand>();
        public int GenerationAttemptsUsed;

        public bool CanGenerateMore => GenerationAttemptsUsed < AiConfigV2.maxGenerationActionsPerTurn;

        public void RecordGenerationAttempt(GenerationStep g, MaterializationResult r)
        {
            if (g == null) return;
            GenerationAttemptsUsed++;
            if (!string.IsNullOrEmpty(g.UseKey)) ClaimedGeneratorUses.Add(g.UseKey);
            if (!string.IsNullOrEmpty(g.CardKey)) TriedGeneratorCards.Add(g.CardKey);
            if (r != null && !string.IsNullOrEmpty(r.AttemptedGenerationUseKey))
                ClaimedGeneratorUses.Add(r.AttemptedGenerationUseKey);
        }

        public AxisDemand BestUnresolvedDemandFor(MaterializationPlan plan)
        {
            if (plan == null) return null;
            // A demand still IsPersistenceDeferred did NOT clear the persistence gate this pass — it
            // is a valid, unresolved, but NOT actionable-yet demand (spec "Persistence Gate:
            // Bootstrap & No-Alternative-Work Escape"). It must never grant Phase B's residual
            // urgency bonus or the residual-only safeguard bypasses (GenericSurplusWouldChurn /
            // ScoutSurplusPortfolioSaturated) — that would let ordinary Phase-B surplus scoring do an
            // end-run around the very gate that just kept this demand from playing in Phase A.
            // TryPromotePersistenceDeferred clears the flag on the SAME AxisDemand instance the
            // moment it actually promotes it, so a promoted demand is indistinguishable from a
            // normal one here — exactly the desired behaviour.
            return UnresolvedDemands
                .Where(d => d != null && !d.IsPersistenceDeferred && d.DesiredAmount > 0f
                    && d.Capability == plan.FinalCapability
                    && (plan.ExpectedTraits & d.RequiredTraits) == d.RequiredTraits)
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis)
                .FirstOrDefault();
        }
    }

    // AI-MGR-01 review-r3 — one demand's scored, opportunity-adjusted chain. DecisionScore
    // (Play - Hold + urgency) is computed ONCE here and carried all the way to the cross-demand
    // arbitration, so the manager never re-ranks on the raw play score again.
    public readonly struct DemandCandidate
    {
        public readonly MaterializationPlan Plan;
        public readonly float FollowupAp;
        public readonly float PlayScore;
        public readonly float HoldValue;
        public readonly float DecisionScore;

        public DemandCandidate(MaterializationPlan plan, float followupAp, float playScore,
            float holdValue, float decisionScore)
        {
            Plan = plan;
            FollowupAp = followupAp;
            PlayScore = playScore;
            HoldValue = holdValue;
            DecisionScore = decisionScore;
        }

        // Worth playing at all: holding the card (+ any urgency) does not beat it.
        public bool Worthwhile => DecisionScore > AiConfigV2.allocatorSliceEpsilon;
    }

    internal static class MaterializationCandidateBuilder
    {
        // AI-MGR-01 P1.3 — excludeCards / excludeGenKeys let the Phase A instance assignment ask
        // for the chains that AVOID a hand card / generation source another demand has claimed, so
        // two demands never both count one physical card as available capacity.
        public static List<DemandCandidate> TopForDemand(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            AxisBudgetLedger ledger, ActorCommitments commitments, float reservedFollowupAp,
            MaterializationReservation reservation, CapabilityInventory inv, bool hasCompetingHeroDemand,
            int k = 3,
            System.Collections.Generic.ISet<CardData> excludeCards = null,
            System.Collections.Generic.ISet<string> excludeGenKeys = null,
            float? witnessedUsefulApDemand = null,
            IReadOnlyList<(MaterializationPlan plan, float followupAp)> fillerUniverse = null)
        {
            var raw = MaterializationChainEnumerator.EnumerateForDemand(
                snap, player, root, hand, ctx, demand, commitments, reservation, excludeCards, excludeGenKeys);
            var candidates = MaterializationFeasibility.FilterForDemand(
                raw, player, root, hand, ctx, demand, ledger, reservedFollowupAp);

            if (candidates.Count == 0) return new List<DemandCandidate>();

            int referenceMoveMax = 0;
            if (demand.Capability == CapabilityKind.ScoutCapability)
            {
                var reference = candidates
                    .OrderBy(c => c.plan.ApCost + c.followupAp)
                    .ThenBy(c => ResourceCostSum(c.plan.ResCost))
                    .ThenBy(c => (int)c.plan.Kind)
                    .ThenBy(c => c.plan.StableKey, System.StringComparer.Ordinal)
                    .First();
                referenceMoveMax = CapabilityQualityEvaluator.ProjectedMoveMax(reference.plan);
            }

            // AI-MGR §11 / Command 6-vs-7 — for a HERO plan, the marginal value of its Command must
            // be measured against how many extra bodies could ALSO legally land in the same recipient
            // this turn (a slot the hero's Command unlocks but nothing can fill is worth nothing).
            // The fillers live in OTHER demands' candidate sets (a FieldCombatPower demand's Unit
            // plans, not this Hero demand's), so the count is taken over `fillerUniverse` — the
            // cross-demand feasible set Phase A assembled in its measurement pass — through the
            // shared portfolio solver's joint-feasibility. Null (persistence reconcile, isolated
            // callers) falls back to this demand's own set: safe, just usually degenerate.
            int genRemaining = Mathf.Max(0,
                AiConfigV2.maxGenerationActionsPerTurn - (reservation?.GenerationAttemptsUsed ?? 0));
            IReadOnlyList<(MaterializationPlan plan, float followupAp)> fillerPlans =
                fillerUniverse ?? candidates.Select(x => (x.plan, x.followupAp)).ToList();

            foreach (var c in candidates)
            {
                CardDefinition cdef = c.plan.BaseCardInHand?.Definition ?? c.plan.GeneratedBaseDef;
                int projectedLegalFillers = cdef != null && cdef.cardType == CardType.Hero
                    ? MaterializationPortfolioSolver.CountJointlyLegalFillersForRecipient(
                        c.plan, fillerPlans, root, player, ctx, hand, genRemaining)
                    : 0;
                c.plan.Score = ScorePlanA(c.plan, demand, c.proj, inv, referenceMoveMax,
                    hasCompetingHeroDemand, snap, witnessedUsefulApDemand, projectedLegalFillers);
            }

            // AI-MGR-01 P0 review-r3 — DecisionScore = Play - Hold + urgency, computed ONCE here.
            // Urgency (a function of demand.Value) is folded in so a real threat lifts every net
            // value; the cross-demand arbitration in StrategicManager ranks purely on DecisionScore
            // and never re-applies demand.Value or re-reads the raw play score.
            float urgency = UrgencyBonus(demand.Value);
            float Decide(MaterializationPlan p) =>
                p.Score - (p.UseBreakdown?.HoldValue ?? 0f) + urgency;

            var ranked = candidates
                .OrderByDescending(c => Decide(c.plan))
                .ThenByDescending(c => c.plan.Score)
                .ThenBy(c => c.plan.StableKey, System.StringComparer.Ordinal)
                .ToList();

            // AI-MGR-01 review-r4 finding 2 — the top-K cut is taken over unique CONSUMPTION
            // SIGNATURES (base card instance + equipment card instance + generation source), NOT raw
            // MaterializationPlans. One physical card yields many plans (A@army1, A@army2,
            // A@garrison, …); without this dedup those clones eat every K slot and a fallback card B
            // is lost before the injective assignment ever sees it (D1{A,B}+D2{A} would collapse to
            // D1{A,A,A}). `ranked` is best-first, so the first plan seen per signature is its best
            // placement.
            var dedup = new List<(MaterializationPlan plan, float followupAp, TraitPreference proj)>();
            var seenSig = new HashSet<string>();
            foreach (var c in ranked)
                if (seenSig.Add(ConsumptionSignature(c.plan)))
                    dedup.Add(c);

            LogQualityChoice(demand, dedup);
            MaterializationPlan best = dedup[0].plan;
            if (best.UseBreakdown != null)
                AiDebugLog.Write($"[AI][V2]   strat.eval A — {demand.Capability} via {best.StableKey} "
                    + $"role={best.UseRole} play {best.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"hold {(best.UseBreakdown?.HoldValue ?? 0f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"urgency {urgency.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"decision {Decide(best).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"[{best.UseBreakdown.ToCompact()}]");

            var outList = new List<DemandCandidate>(Mathf.Max(1, k));
            foreach (var c in dedup.Take(Mathf.Max(1, k)))
            {
                float hold = c.plan.UseBreakdown?.HoldValue ?? 0f;
                outList.Add(new DemandCandidate(c.plan, c.followupAp, c.plan.Score, hold, Decide(c.plan)));
            }
            return outList;
        }

        // AI-MGR-01 review-r4 finding 2 — the physical resources a chain consumes, WITHOUT the
        // deployment target. It is StableKey minus its trailing `Deploy.Key` segment: the leading
        // segments already encode chain kind, capability, base-card hand index, equipment hand index
        // and generation CardKey. Two placements of the same card share it; a different card copy or
        // a different generation source does not.
        private static string ConsumptionSignature(MaterializationPlan p)
        {
            string k = p?.StableKey ?? "";
            int lastBar = k.LastIndexOf('|');
            return lastBar >= 0 ? k.Substring(0, lastBar) : k;
        }

        private static void LogQualityChoice(AxisDemand demand,
            List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> ranked)
        {
            if (demand.Capability != CapabilityKind.ScoutCapability || ranked.Count == 0) return;
            MaterializationPlan win = ranked[0].plan;
            (MaterializationPlan plan, float followupAp, TraitPreference proj)? runner = null;
            foreach (var c in ranked.Skip(1))
            {
                bool differentBody = c.plan.Kind != win.Kind
                    || !ReferenceEquals(c.plan.BaseCardInHand, win.BaseCardInHand)
                    || c.plan.GeneratedBaseDef != win.GeneratedBaseDef;
                bool close = win.Score - c.plan.Score <= AiConfigV2.scoutQualityLogRunnerUpMargin;
                if (differentBody || close) { runner = c; break; }
            }
            if (runner == null) return;

            string Name(MaterializationPlan p) =>
                (p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef)?.displayName ?? "?";
            string bdW = win.QualityBreakdown != null ? win.QualityBreakdown.ToCompact() : "-";
            string bdR = runner.Value.plan.QualityBreakdown != null
                ? runner.Value.plan.QualityBreakdown.ToCompact() : "-";
            AiDebugLog.Write($"[AI][V2]   strat.A quality {demand.Capability} — "
                + $"{Name(win)} score {win.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                + $"[{bdW}] > {Name(runner.Value.plan)} "
                + $"{runner.Value.plan.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} [{bdR}]");
        }

        // AI-MGR-02 round 6 — the full set of PREFLIGHTED surplus materialization plans (each one
        // already passed CardPlayExecutor.Preflight + ReservesOkAfterChain). BestSurplus picks the
        // highest-DecisionScore among these; the reaction feasibility probe needs the WHOLE set so
        // it can find the genuinely CHEAPEST feasible plan, not just the best-scored one.

        public static (MaterializationPlan plan, float utility)? BestSurplus(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            CapabilityInventory inv, ActorCommitments commitments, MaterializationReservation reservation,
            float? witnessedUsefulApDemand = null)
        {
            List<MaterializationPlan> raw = MaterializationChainEnumerator.EnumerateSurplusPlans(
                snap, player, root, hand, ctx, inv, commitments, reservation);
            List<MaterializationPlan> candidates = MaterializationFeasibility.FilterSurplus(
                raw, player, root, hand, ctx, reservation);
            if (candidates.Count == 0) return null;

            // `witnessedUsefulApDemand` is the SHARED Phase-B owner-witnessed AP workload
            // (PhaseBWitnessedApWorkload) — the SAME scalar the non-combat lane gets, so a Unit/Hero
            // ApBonus carrier and a Base/Facility ApBonus carrier are priced off one number. Null =>
            // the evaluator falls back to the discounted structural estimate.

            // Scoring is SEPARATE from enumeration (DoD): the enumerator returns score-free plans;
            // here every surviving plan gets the canonical StrategicCardEvaluator NetScore. The
            // reaction feasibility probe consumes the same enumerator output and never reads .Score.
            foreach (MaterializationPlan p in candidates)
            {
                bool recce = AbilityParams.AbilitiesHaveAnyRecce(p.ProjectedAbilities);
                CardDefinition bd = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                bool hero = bd != null && bd.cardType == CardType.Hero;
                p.Score = SurplusUtility(snap, p, inv, recce, hero, hand, p.ProjectedAbilities,
                    witnessedUsefulApDemand);
            }

            // final closure follow-up §P1 — GLOBAL highest-score arbitration, no residual bucket
            // ordering. Each candidate's Phase-B decision score is its NetScore plus the urgency of
            // the unresolved demand it would OPERATIONALLY deliver (the same UrgencyBonus ramp Phase
            // A folds into its DecisionScore). A residual candidate no longer skips ahead of a
            // higher-scored normal one — it just carries the weight its demand.Value earns. The
            // returned utility IS this decision score, so StrategicManager compares it directly with
            // the non-combat lane and never re-adds urgency.
            float DecisionScore(MaterializationPlan p)
            {
                AxisDemand d = reservation?.BestUnresolvedDemandFor(p);
                float urgency = d != null && CanDeliverDemandOperationally(p, d)
                    ? UrgencyBonus(d.Value) : 0f;
                return p.Score + urgency;
            }

            MaterializationPlan bestPlan = candidates
                .OrderByDescending(DecisionScore)
                .ThenByDescending(p => p.Score)
                .ThenBy(p => p.StableKey, System.StringComparer.Ordinal)
                .First();
            float bestDecision = DecisionScore(bestPlan);
            if (bestPlan.UseBreakdown != null)
                AiDebugLog.Write($"[AI][V2]   strat.eval B — {bestPlan.StableKey} role={bestPlan.UseRole} "
                    + $"net {bestPlan.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"decision {bestDecision.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"[{bestPlan.UseBreakdown.ToCompact()}]");
            return (bestPlan, bestDecision);
        }





        // §2 — the best still-unresolved strategic demand this surplus card would be relevant to,
        // or null. A non-null result means Phase B must not spend the card on a placement that
        // cannot operationally deliver `cap`; it is held in hand instead.

        // ARCH-02 §16 — moved to the canonical MaterializationDeliveryPolicy. This forwarder keeps
        // the widely-used name for the builder's own gates and its external callers.
        internal static bool CanDeliverDemandOperationally(MaterializationPlan p, AxisDemand demand)
            => MaterializationDeliveryPolicy.CanDeliverDemandOperationally(p, demand);

        // ARCH-02 §45 — route through the one owner-aware spendability seam so a bounded reaction
        // envelope (or any other explicit reservation) is respected here too, not just the legacy
        // recon-air pool.

        // AI-MGR-01 — Phase A scoring is now the shared StrategicCardEvaluator (Card x IntendedUse,
        // BaselineForceReadiness, no flat Hero bonus). This wrapper keeps the call signature and
        // still carries the Scout capability-quality breakdown + the new use breakdown for logging.
        private static float ScorePlanA(MaterializationPlan p, AxisDemand demand, TraitPreference projected,
            CapabilityInventory inv, int referenceMoveMax, bool hasCompetingHeroDemand, WorldSnapshot snap,
            float? witnessedUsefulApDemand, int projectedLegalFillers)
        {
            StrategicCardUseCandidate cand = StrategicCardEvaluator.ScoreForDemand(
                p, demand, projected, inv, referenceMoveMax, hasCompetingHeroDemand, snap,
                witnessedUsefulApDemand, projectedLegalFillers);
            p.QualityBreakdown = cand.QualityBreakdown;
            p.UseBreakdown = cand.Breakdown;
            p.UseRole = cand.IntendedRole;
            return cand.TotalUseScore;
        }

        // AI-MGR — MEASUREMENT-ONLY feasible set for a demand: every chain that passed enumeration +
        // resource feasibility, ONE representative (cheapest AP) per physical consumption signature,
        // with NO scoring / DecisionScore / Worthwhile filter. StrategicManager Phase A sums these
        // across the active demands (MaterializationPortfolioSolver.EstimateLegalApWorkload) to get
        // the owner-witnessed legal card-AP workload BEFORE the AP-dependent scoring pass — so a good
        // ApBonus carrier is never dropped by fallback-scored Top-K pruning before it is priced
        // against the real workload.
        public static List<(MaterializationPlan plan, float followupAp)> AllFeasiblePlansForDemand(
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            AxisDemand demand, AxisBudgetLedger ledger, ActorCommitments commitments, float reservedFollowupAp,
            MaterializationReservation reservation)
        {
            var raw = MaterializationChainEnumerator.EnumerateForDemand(
                snap, player, root, hand, ctx, demand, commitments, reservation, null, null);
            var candidates = MaterializationFeasibility.FilterForDemand(
                raw, player, root, hand, ctx, demand, ledger, reservedFollowupAp);

            var bySig = new Dictionary<string, (MaterializationPlan plan, float followupAp)>();
            foreach (var c in candidates)
            {
                if (c.plan == null) continue;
                string sig = ConsumptionSignature(c.plan);
                float ap = c.plan.ApCost + c.followupAp;
                if (!bySig.TryGetValue(sig, out var cur) || ap < cur.plan.ApCost + cur.followupAp)
                    bySig[sig] = (c.plan, c.followupAp);
            }
            return bySig.Values.ToList();
        }

        private static float ResourceCostSum(ResourceCost c) => c == null
            ? 0f : c.human + c.energy + c.materials + c.tech;

        // AI-MGR-01 P0.2 review-r2 — urgency enters the Play-vs-Hold equation (not a hard switch):
        // a demand's Value ramps a bonus added to every candidate's net decision value, so a real
        // threat / raid gap keeps materialising even against a card with a high HoldValue, while a
        // soft baseline demand adds ~nothing and can genuinely lose to Hold.
        // Shared Play-vs-Hold / Phase-B urgency ramp off a demand's Value. Used by Phase A's
        // DecisionScore and (final closure follow-up §P1) by BestSurplus's global decision score so
        // an operational residual competes on score instead of a hard boolean priority.
        private static float UrgencyBonus(float demandValue)
        {
            float t = Mathf.Clamp01((demandValue - AiConfigV2.stratHoldUrgencyRampLo)
                / Mathf.Max(0.01f, AiConfigV2.stratHoldUrgencyRampHi - AiConfigV2.stratHoldUrgencyRampLo));
            return t * AiConfigV2.stratHoldUrgencyMax;
        }

        // AI-MGR-01 — Phase B surplus scoring is the shared StrategicCardEvaluator too. It builds a
        // Card x IntendedRole candidate set and returns the best NetScore (play value minus the
        // separately scored HoldValue).
        private static float SurplusUtility(WorldSnapshot snap, MaterializationPlan p, CapabilityInventory inv,
            bool recce, bool hero, AiHandData hand, IReadOnlyList<string> projected,
            float? witnessedUsefulApDemand = null)
        {
            StrategicCardUseCandidate cand = StrategicCardEvaluator.ScoreSurplus(
                p, inv, recce, hero, hand, projected, snap, witnessedUsefulApDemand);
            p.UseBreakdown = cand.Breakdown;
            p.UseRole = cand.IntendedRole;
            return cand.NetScore;
        }

        // AI-MGR — the SHARED Phase-B owner-witnessed AP workload: how much AP the AI could still
        // spend this end-of-turn on a jointly-feasible set of card plays, across BOTH Phase-B lanes
        // (Unit/Hero surplus + non-combat Base/Facility/Aviation/Equipment). One scalar, computed
        // once per tempo iteration from the REAL feasible candidate universe, handed to both
        // BestSurplus/ScoreSurplus and NonCombatCardPlayer/ScoreNonCombat so an ApBonus Unit and an
        // ApBonus Base are priced off the same number (spec §4). AP is NOT a ceiling here
        // (EstimateLegalApWorkload runs with AP admission off) — it is the measured quantity. No
        // committed-mover term: by end of turn the missions have run, the surplus universe IS the
        // workload. Returns null (=> evaluators keep the discounted structural fallback) unless a
        // PlayerGlobal recurring-resource carrier is reachable through hand or deck.
        internal static float? PhaseBWitnessedApWorkload(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, CapabilityInventory inv,
            ActorCommitments commitments, MaterializationReservation reservation)
        {
            System.Collections.Generic.IEnumerable<CardDefinition> handDeckDefs =
                (snap?.Self?.Hand ?? System.Array.Empty<CardData>()).Select(c => c?.Definition)
                    .Concat(snap?.Self?.Deck ?? System.Array.Empty<CardDefinition>());
            if (!StrategicEffectRegistry.AnyGlobalRecurringCarrier(handDeckDefs))
                return null;

            int genRemaining = Mathf.Max(0,
                AiConfigV2.maxGenerationActionsPerTurn - (reservation?.GenerationAttemptsUsed ?? 0));

            // Unit/Hero surplus lane — jointly-feasible, one representative per consumption signature.
            List<MaterializationPlan> surplusRaw = MaterializationChainEnumerator.EnumerateSurplusPlans(
                snap, player, root, hand, ctx, inv, commitments, reservation);
            List<MaterializationPlan> surplus = MaterializationFeasibility.FilterSurplus(
                surplusRaw, player, root, hand, ctx, reservation);
            var bySig = new Dictionary<string, (MaterializationPlan plan, float followupAp)>();
            foreach (MaterializationPlan p in surplus)
            {
                if (p == null) continue;
                string sig = ConsumptionSignature(p);
                if (!bySig.TryGetValue(sig, out var cur) || p.ApCost < cur.plan.ApCost)
                    bySig[sig] = (p, 0f);
            }
            float total = MaterializationPortfolioSolver.EstimateLegalApWorkload(
                bySig.Values.ToList(), root, player, ctx, hand, genRemaining);

            // Non-combat lane — one feasible play per card; its AP is added straight (AP is the
            // measured quantity, not a ceiling). A mild over-count on a rare double-generated hand is
            // harmless; the failure mode this whole rework fixes is UNDER-counting.
            var seenNc = new HashSet<CardData>();
            foreach (NonCombatCardPlayer.NonCombatPlay ncp in NonCombatCardPlayer.EnumeratePlays(
                         snap, player, root, hand, ctx, new List<string>(), reservation))
                if (ncp?.Card != null && seenNc.Add(ncp.Card))
                    total += Mathf.Max(0f, ncp.Card.EffectivePlayApCost);

            return total;
        }






    }
}
