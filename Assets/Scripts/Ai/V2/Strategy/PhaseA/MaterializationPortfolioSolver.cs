using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
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
                PlayerRoot root, PlayerSetupData player, int genAttemptsRemaining)
        {
            var demands = options.Keys.OrderBy(d => d.Ordinal).ToList();
            var best = new Dictionary<DemandState, DemandCandidate>();
            float bestSum = float.NegativeInfinity;
            var acc = new Dictionary<DemandState, DemandCandidate>();

            // round 9 (P0.2) — the physical hand-card / generation-source / AP / H-E-M-T bookkeeping
            // is now the shared MaterializationConsumptionState (same model the reaction closure DFS
            // uses). Behaviour is unchanged: the ceilings (apPool / resPool / genAttemptsRemaining)
            // and the check order are identical to the previous local implementation.
            var consumed = new MaterializationConsumptionState();

            float apPool = root != null
                ? root.ActionPoints - AiConfigV2.housekeepingApReserve : float.MaxValue;
            var resPool = new Dictionary<ResourceType, int>();
            foreach (ResourceType t in ResourceBundle.All)
                resPool[t] = root != null
                    ? Mathf.Max(0, Mathf.FloorToInt(Game.Ai.AiResourceReservation.Available(root, player, t)))
                    : int.MaxValue;

            bool Fits(DemandCandidate c)
            {
                float ap = (c.Plan?.ApCost ?? 0f) + c.FollowupAp;
                if (consumed.ApUsed + ap > apPool + AiConfigV2.allocatorSliceEpsilon)
                    return false;
                if (c.Plan?.Generation != null && consumed.GenerationAttempts + 1 > genAttemptsRemaining)
                    return false;
                ResourceCost rc = c.Plan?.ResCost;
                if (rc != null)
                    foreach (ResourceType t in ResourceBundle.All)
                        if (consumed.ResourceUsed(t) + rc.Get(t) > resPool[t])
                            return false;
                return true;
            }

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
                    if (!consumed.CardsDisjoint(c.Plan))
                        continue;
                    if (!Fits(c))
                        continue;
                    MaterializationConsumptionState.Token token = consumed.Push(c.Plan, c.FollowupAp);

                    acc[d] = c;
                    Rec(i + 1, sum + c.DecisionScore);
                    acc.Remove(d);

                    consumed.Pop(token);
                }
            }
            Rec(0, 0f);
            return best;
        }
    }
}
