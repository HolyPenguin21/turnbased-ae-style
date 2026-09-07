using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // One feasible (DemandState -> DemandCandidate) chain assignment, ranked by the evaluator's
    // opportunity-adjusted DecisionScore.
    internal readonly struct PhaseACandidate
    {
        public readonly DemandState State;
        public readonly DemandCandidate Cand;

        public PhaseACandidate(DemandState state, DemandCandidate cand)
        {
            State = state;
            Cand = cand;
        }

        public MaterializationPlan Plan => Cand.Plan;
        public float FollowupAp => Cand.FollowupAp;
        public float DecisionScore => Cand.DecisionScore;
    }

    // ARCH-02 §18 — the constrained portfolio solver. Given per-demand candidate chains plus the
    // shared AP / H-E-M-T / generation-attempt / physical-card pools, it returns the best JOINTLY
    // feasible collision-free set. It does not execute, does not change score and adds no capability
    // priority: pure feasibility + optimisation over the canonical score. Extracted verbatim from
    // StrategicManager.
    internal static class MaterializationPortfolioSolver
    {
        // AI-MGR-01 review-r3 — cross-demand arbitration ranks purely on the opportunity-adjusted
        // DecisionScore (Play - Hold + urgency), computed once in the builder. demand.Value is NOT
        // re-multiplied here — its weight already entered DecisionScore through UrgencyBonus.
        internal static float ArbitrationScore(PhaseACandidate c) => c.DecisionScore;

        // ===================================================================================
        //  SHARED JOINT-FEASIBILITY BOOKKEEPING  (AI-MGR — one machine, three consumers)
        // ===================================================================================
        //  The cumulative AP / H-E-M-T / generation-attempt / physical-card / recipient-capacity
        //  draw of a multi-chain portfolio. BestInjectiveAssignment, EstimateLegalApWorkload and
        //  CountJointlyLegalFillersForRecipient all push/pop against ONE instance of this, so they
        //  can never disagree about what a set of chains physically consumes. The AP pool nets the
        //  HousekeepingManager reserve; the resource pool nets the owner-aware reservation ledger
        //  (StrategicSpendability.SpendableAmount), exactly as the former inline `Fits` did.
        private sealed class JointFeasibility
        {
            private readonly MaterializationConsumptionState _consumed = new MaterializationConsumptionState();
            private readonly ProjectedPhysicalState _physical = new ProjectedPhysicalState();
            private readonly Dictionary<ResourceType, int> _resPool = new Dictionary<ResourceType, int>();
            private readonly float _apPool;
            private readonly int _genAttemptsRemaining;
            // AI-MGR — AP is the variable whose marginal utility the workload measurement measures, so
            // it must NOT double as a ceiling on the measured demand: an 8-AP player with 12 AP of
            // real work should read workload 12, not 8. Authoritative selection (BestInjectiveAssignment)
            // keeps AP enforced; the EstimateLegalApWorkload measurement passes turn it OFF. Every
            // OTHER constraint (H/E/M/T, the one generation attempt, card-disjointness, physical
            // recipient capacity) stays enforced in both modes.
            private readonly bool _enforceApPool;

            public JointFeasibility(PlayerRoot root, PlayerSetupData player, AiTurnContext ctx,
                AiHandData hand, int genAttemptsRemaining, IEnumerable<MaterializationPlan> recipientSeedPlans,
                bool enforceApPool)
            {
                _genAttemptsRemaining = genAttemptsRemaining;
                _enforceApPool = enforceApPool;
                _physical.SeedHandSlots(hand != null ? Mathf.Max(0, hand.Capacity - hand.Hand.Count) : int.MaxValue);
                if (recipientSeedPlans != null)
                    foreach (MaterializationPlan p in recipientSeedPlans)
                    {
                        ArmyData tgt = p?.Deploy.Army;
                        if (tgt == null) continue;
                        _physical.SeedRecipient(ProjectedPhysicalState.RecipientKey(p),
                            tgt.IsGarrison,
                            tgt.Members.Count(u => u != null && !u.IsHero),
                            tgt.Members.Any(u => u != null && u.IsHero),
                            tgt.Capacity);
                    }

                _apPool = root != null
                    ? root.ActionPoints - AiConfigV2.housekeepingApReserve : float.MaxValue;
                foreach (ResourceType t in ResourceBundle.All)
                    _resPool[t] = root != null
                        ? Mathf.Max(0, Mathf.FloorToInt(StrategicSpendability.SpendableAmount(player, root, ctx, t)))
                        : int.MaxValue;
            }

            // Physical disjointness only — the hand card / generation source is not already taken.
            public bool CardsDisjoint(MaterializationPlan p) => _consumed.CardsDisjoint(p);

            // Would this chain (+ its follow-up AP) still fit on top of everything already pushed?
            public bool Fits(MaterializationPlan plan, float followupAp)
            {
                float ap = (plan?.ApCost ?? 0f) + followupAp;
                if (_enforceApPool && _consumed.ApUsed + ap > _apPool + AiConfigV2.allocatorSliceEpsilon)
                    return false;
                if (plan?.Generation != null && _consumed.GenerationAttempts + 1 > _genAttemptsRemaining)
                    return false;
                ResourceCost rc = plan?.ResCost;
                if (rc != null)
                    foreach (ResourceType t in ResourceBundle.All)
                        if (_consumed.ResourceUsed(t) + rc.Get(t) > _resPool[t])
                            return false;
                if (!_physical.CanAdd(plan))
                    return false;
                return true;
            }

            public readonly struct Token
            {
                public readonly MaterializationConsumptionState.Token Consumed;
                public readonly ProjectedPhysicalState.Token Physical;

                public Token(MaterializationConsumptionState.Token consumed, ProjectedPhysicalState.Token physical)
                {
                    Consumed = consumed;
                    Physical = physical;
                }
            }

            public Token Push(MaterializationPlan plan, float followupAp)
            {
                MaterializationConsumptionState.Token c = _consumed.Push(plan, followupAp);
                ProjectedPhysicalState.Token p = _physical.Add(plan);
                return new Token(c, p);
            }

            public void Pop(in Token t)
            {
                _physical.Remove(t.Physical);
                _consumed.Pop(t.Consumed);
            }
        }

        // Bounded max-total injective assignment over the active demands (<= maxDemandFulfillment
        // ActionsPerTurn, each with <= phaseATopK options): choose one Worthwhile chain per demand
        // (or none) so no hand card / generation source is used twice, maximising the total
        // DecisionScore. Branching factor (K+1)^demandCount — trivial at K=3, count<=3.
        //
        // AI-MGR-01 review-r4 finding 3 — the chosen portfolio must be JOINTLY feasible, not just
        // card-disjoint: the ONE per-turn generation attempt and the shared AP / H-E-M-T pools are
        // consumed by the whole accepted set. Two chains that are each individually affordable can be
        // un-runnable together (both want the last Tech; both want the single Challenge with
        // different CardKeys). Without this the search returns a phantom portfolio and the downstream
        // pick has to paper over it — which is exactly the hidden capability-priority layer finding 1
        // removes.
        internal static Dictionary<DemandState, DemandCandidate>
            BestInjectiveAssignment(
                Dictionary<DemandState, List<DemandCandidate>> options,
                PlayerRoot root, PlayerSetupData player, AiTurnContext ctx, AiHandData hand,
                int genAttemptsRemaining)
        {
            var demands = options.Keys.OrderBy(d => d.Ordinal).ToList();
            var best = new Dictionary<DemandState, DemandCandidate>();
            float bestSum = float.NegativeInfinity;
            var acc = new Dictionary<DemandState, DemandCandidate>();

            // round 9 (P0.2) / ARCH-02 §15/§57 — the shared JointFeasibility owns the physical
            // hand-card / generation-source / AP / H-E-M-T bookkeeping AND the recipient/hero/hand-
            // slot side, so two individually-legal chains into ONE recipient with one free slot can
            // no longer both land in a "jointly feasible" assignment.
            var jf = new JointFeasibility(root, player, ctx, hand, genAttemptsRemaining,
                options.Values.SelectMany(v => v).Select(c => c.Plan), enforceApPool: true);

            void Rec(int i, float sum)
            {
                if (i == demands.Count)
                {
                    if (sum > bestSum || (sum == bestSum && acc.Count > best.Count))
                    {
                        bestSum = sum;
                        best = new Dictionary<DemandState, DemandCandidate>(acc);
                    }
                    return;
                }
                DemandState d = demands[i];
                Rec(i + 1, sum); // skip this demand
                foreach (DemandCandidate c in options[d])
                {
                    if (!c.Worthwhile)
                        continue;
                    if (!jf.CardsDisjoint(c.Plan))
                        continue;
                    if (!jf.Fits(c.Plan, c.FollowupAp))
                        continue;
                    JointFeasibility.Token token = jf.Push(c.Plan, c.FollowupAp);

                    acc[d] = c;
                    Rec(i + 1, sum + c.DecisionScore);
                    acc.Remove(d);

                    jf.Pop(token);
                }
            }
            Rec(0, 0f);
            return best;
        }

        // ===================================================================================
        //  AI-MGR — OWNER-WITNESSED LEGAL CARD-AP WORKLOAD  (StrategicManager Phase A measurement)
        // ===================================================================================
        //  "How much AP could the AI still spend on a JOINTLY feasible collision-free set of card
        //  materializations this turn?" — the card component of the owner-witnessed AP workload the
        //  recurring-AP effect (ApBonus) is priced against.
        //
        //  MEASUREMENT-ONLY. It ranks on ACTUAL AP cost (plan.ApCost + follow-up), NOT DecisionScore
        //  / Worthwhile: feeding a score-derived figure back into the AP-utility that scores those
        //  same cards would be circular (AP utility -> card score -> workload -> AP utility). It
        //  reuses the SAME JointFeasibility machinery BestInjectiveAssignment runs on — with the AP
        //  pool admission DISABLED (enforceApPool:false). AP is the quantity whose marginal utility
        //  is being measured, so it cannot also cap the measured demand: an 8-AP player with 12 AP of
        //  jointly-feasible work must read 12, not 8. Every other constraint (H/E/M/T, the one
        //  generation attempt, card-disjointness, physical recipient capacity) still applies, so a
        //  phantom "both chains want the last Tech / the one Challenge" pair can never inflate it.
        //
        //  `options` MUST be every FEASIBLE plan for each demand (one representative per physical
        //  consumption signature — MaterializationCandidateBuilder.AllFeasiblePlansForDemand),
        //  BEFORE any score-based Top-K prune: a good ApBonus carrier must not be dropped by a
        //  fallback-scored pruning pass before it is priced against the real workload.
        internal static float EstimateLegalApWorkload(
            Dictionary<DemandState, List<(MaterializationPlan plan, float followupAp)>> options,
            PlayerRoot root, PlayerSetupData player, AiTurnContext ctx, AiHandData hand,
            int genAttemptsRemaining)
        {
            if (options == null || options.Count == 0)
                return 0f;

            var demands = options.Keys.OrderBy(d => d.Ordinal).ToList();
            var jf = new JointFeasibility(root, player, ctx, hand, genAttemptsRemaining,
                options.Values.SelectMany(v => v).Select(c => c.plan), enforceApPool: false);

            float best = 0f;

            void Rec(int i, float apSum)
            {
                if (i == demands.Count)
                {
                    if (apSum > best) best = apSum;
                    return;
                }
                DemandState d = demands[i];
                Rec(i + 1, apSum); // this demand spends no card AP
                foreach ((MaterializationPlan plan, float followupAp) c in options[d])
                {
                    if (c.plan == null)
                        continue;
                    if (!jf.CardsDisjoint(c.plan))
                        continue;
                    if (!jf.Fits(c.plan, c.followupAp))
                        continue;
                    JointFeasibility.Token token = jf.Push(c.plan, c.followupAp);
                    Rec(i + 1, apSum + Mathf.Max(0f, c.plan.ApCost) + Mathf.Max(0f, c.followupAp));
                    jf.Pop(token);
                }
            }
            Rec(0, 0f);
            return best;
        }

        // FLAT overload — Phase B (surplus) / any lane that has ONE undivided pool of feasible plans
        // rather than a per-demand partition. Take-or-skip subset search, joint feasibility (H/E/M/T /
        // the one generation / card-disjointness / physical recipient capacity) enforced by the SAME
        // JointFeasibility with AP admission DISABLED — AP is the measured quantity, never a ceiling.
        // `plans` MUST already be deduped to one representative per physical consumption signature (so
        // the pool is small — bounded by hand size). Iteration is AP-DESCENDING so the search sees
        // the expensive AP opportunities that PROVE AP pressure first, and a hand pathological enough
        // to exceed apWorkloadExactSearchMax falls back to an AP-descending greedy admission rather
        // than dropping the costly plans.
        internal static float EstimateLegalApWorkload(
            IReadOnlyList<(MaterializationPlan plan, float followupAp)> plans,
            PlayerRoot root, PlayerSetupData player, AiTurnContext ctx, AiHandData hand,
            int genAttemptsRemaining)
        {
            if (plans == null || plans.Count == 0)
                return 0f;

            var pool = plans
                .Where(p => p.plan != null)
                .OrderByDescending(p => p.plan.ApCost + p.followupAp)
                .ThenBy(p => p.plan.StableKey, System.StringComparer.Ordinal)
                .ToList();
            if (pool.Count == 0)
                return 0f;

            var jf = new JointFeasibility(root, player, ctx, hand, genAttemptsRemaining,
                pool.Select(p => p.plan), enforceApPool: false);

            float ApOf((MaterializationPlan plan, float followupAp) c)
                => Mathf.Max(0f, c.plan.ApCost) + Mathf.Max(0f, c.followupAp);

            // Pool too large for an exact 2^N sweep — AP-descending greedy admission (each accepted
            // plan consumes its real H/E/M/T / generation / slot, so the next Fits sees the truth).
            if (pool.Count > AiConfigV2.apWorkloadExactSearchMax)
            {
                float greedy = 0f;
                foreach ((MaterializationPlan plan, float followupAp) c in pool)
                    if (jf.CardsDisjoint(c.plan) && jf.Fits(c.plan, c.followupAp))
                    {
                        jf.Push(c.plan, c.followupAp);
                        greedy += ApOf(c);
                    }
                return greedy;
            }

            float best = 0f;

            void Rec(int i, float apSum)
            {
                if (apSum > best) best = apSum;
                if (i == pool.Count)
                    return;
                (MaterializationPlan plan, float followupAp) c = pool[i];
                if (jf.CardsDisjoint(c.plan) && jf.Fits(c.plan, c.followupAp))
                {
                    JointFeasibility.Token token = jf.Push(c.plan, c.followupAp);
                    Rec(i + 1, apSum + ApOf(c));
                    jf.Pop(token);
                }
                Rec(i + 1, apSum); // skip plan i
            }
            Rec(0, 0f);
            return best;
        }

        // ===================================================================================
        //  AI-MGR — JOINTLY-LEGAL DESTINATION FILLERS  (HeroCommandMarginalValue, Command 6 vs 7)
        // ===================================================================================
        //  How many extra NON-HERO bodies could ALSO legally land in `heroPlan`'s recipient this
        //  turn, on top of the hero itself — the count that decides whether a hero's higher Command
        //  actually unlocks a usable slot or an empty one. Uses the SAME JointFeasibility (AP /
        //  H-E-M-T / generation / physical / recipient capacity), seeded with the hero plan already
        //  pushed, so two individually-legal Unit plans can never both claim the last free slot.
        //
        //  `candidateFillers` is the CROSS-demand feasible plan universe (Phase A's measurement pass)
        //  — the Unit fillers live in OTHER demands' sets, not this Hero demand's. Only Unit plans
        //  into the SAME recipient are considered; Hero plans and plans reusing a card/source the
        //  hero plan already consumes are excluded. Result is capped at heroCommandMarginalMaxSlots.
        internal static int CountJointlyLegalFillersForRecipient(
            MaterializationPlan heroPlan,
            IEnumerable<(MaterializationPlan plan, float followupAp)> candidateFillers,
            PlayerRoot root, PlayerSetupData player, AiTurnContext ctx, AiHandData hand,
            int genAttemptsRemaining)
        {
            if (heroPlan == null)
                return 0;

            string recipientKey = ProjectedPhysicalState.RecipientKey(heroPlan);
            var fillers = new List<(MaterializationPlan plan, float followupAp)>();
            if (candidateFillers != null)
                foreach ((MaterializationPlan plan, float followupAp) c in candidateFillers)
                {
                    MaterializationPlan p = c.plan;
                    if (p == null || ReferenceEquals(p, heroPlan))
                        continue;
                    CardDefinition d = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                    if (d == null || d.cardType != CardType.Unit)
                        continue;
                    if (ProjectedPhysicalState.RecipientKey(p) != recipientKey)
                        continue;
                    fillers.Add((p, c.followupAp));
                }
            if (fillers.Count == 0)
                return 0;

            var seedPlans = new List<MaterializationPlan> { heroPlan };
            seedPlans.AddRange(fillers.Select(f => f.plan));
            var jf = new JointFeasibility(root, player, ctx, hand, genAttemptsRemaining, seedPlans,
                enforceApPool: true);

            // The hero body is already committed to the recipient.
            if (!jf.Fits(heroPlan, 0f))
                return 0;
            jf.Push(heroPlan, 0f);

            fillers = fillers
                .OrderBy(f => f.plan.ApCost + f.followupAp)
                .ThenBy(f => f.plan.StableKey, System.StringComparer.Ordinal)
                .ToList();

            // TRUE maximum joint-feasible filler count, not a greedy one: with multi-dimensional
            // resources (AP + H/E/M/T + the one generation + slots) a cheapest-first greedy can pick
            // one plan that blocks two others (A=1AP/2H beats B,C=2AP/1H each when only 2H is free —
            // greedy takes A for count 1, B+C would be count 2). Bounded DFS, each branch pushes the
            // plan's real consumption; early-out the moment the cap is reached or can't be beaten.
            int cap = Mathf.Max(0, AiConfigV2.heroCommandMarginalMaxSlots);
            int best = 0;

            void Rec(int i, int count)
            {
                if (count > best) best = count;
                if (best >= cap || i == fillers.Count || count + (fillers.Count - i) <= best)
                    return;
                (MaterializationPlan plan, float followupAp) f = fillers[i];
                if (jf.CardsDisjoint(f.plan) && jf.Fits(f.plan, f.followupAp))
                {
                    JointFeasibility.Token token = jf.Push(f.plan, f.followupAp);
                    Rec(i + 1, count + 1);
                    jf.Pop(token);
                }
                Rec(i + 1, count);
            }
            Rec(0, 0);
            return Mathf.Min(best, cap);
        }
    }
}
