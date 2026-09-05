using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON ASSIGNMENT / PROVISIONING  (AI V2 architecture — Level 5)
    // ===========================================================================================
    //  Single responsibility: given a funded Recon mission, which concrete actor executes it — the
    //  ONE canonical owner of actor<->Recon job matching. This class answers exactly three kinds of
    //  question, and nothing about strategic priority, axis funding, or a strategic objective:
    //
    //    A. EvaluateCandidate — "can THIS actor execute THIS job at all" (route/vantage/stealth
    //       feasibility). The canonical actor/job feasibility definition.
    //    B/D. BuildCandidates / AssignFunded — given a batch of FUNDED missions, the best one-to-one
    //       actor assignment across all of them at once (job <= 1 actor, actor <= 1 job).
    //    C. MeasureCapacity — a READ-ONLY aggregate query Demand uses to ask "how much of this is
    //       there ANY usable actor for" without Demand itself knowing how matching works.
    //
    //  This is deliberately NOT a pre-funding reservation subsystem: it owns no cross-call mutable
    //  room/ledger state, mirrors no allocator deferrals, and never runs before AxisBudgetLedger /
    //  ResourceAllocator have funded a mission. Assignment begins for real only after generic
    //  funding (ResourceAllocator.Pack) has picked a mission — see ProvisioningManager.
    // ===========================================================================================

    public enum ReconAssignmentBlockReason
    {
        None,
        ActorMissing,
        NoRoute,
        NoReachableVantage,
    }

    public readonly struct ReconAssignmentCandidateResult
    {
        public readonly bool Feasible;
        public readonly ReconAssignmentBlockReason BlockReason;

        public ReconAssignmentCandidateResult(bool feasible, ReconAssignmentBlockReason reason)
        {
            Feasible = feasible;
            BlockReason = reason;
        }

        public static readonly ReconAssignmentCandidateResult Ok =
            new ReconAssignmentCandidateResult(true, ReconAssignmentBlockReason.None);

        public static ReconAssignmentCandidateResult Blocked(ReconAssignmentBlockReason reason) =>
            new ReconAssignmentCandidateResult(false, reason);
    }

    // Read-only aggregate result of MeasureCapacity — witnessed (proven-executable) actor counts,
    // split by requirement class. NOT the same as a raw actor COUNT (e.g. capacity.Generic*
    // LaneActors.Count / IdleGroundScouts.Count) — those are unverified; this is the result of an
    // actual joint actor<->job matching (one actor <= one job, one job <= one actor).
    public readonly struct ReconCapacityMeasurement
    {
        public readonly int GroundLaneWitnessed;
        public readonly int ObsLaneWitnessed;
        public readonly int GroundIdleWitnessed;
        public readonly int ObsIdleWitnessed;

        public ReconCapacityMeasurement(int groundLane, int obsLane, int groundIdle, int obsIdle)
        {
            GroundLaneWitnessed = groundLane;
            ObsLaneWitnessed = obsLane;
            GroundIdleWitnessed = groundIdle;
            ObsIdleWitnessed = obsIdle;
        }
    }

    internal static class ReconAssignmentPlanner
    {
        // =======================================================================================
        //  A. EvaluateCandidate — the ONLY canonical actor/job feasibility definition. Answers
        //     "can this one actor execute this one job", ignoring everyone else (actor uniqueness /
        //     concrete-job uniqueness / class-quota bookkeeping is the CALLER's job — BuildCandidates/
        //     AssignFunded for real assignment, MeasureCapacity's flow network for the Demand
        //     witness). Must NOT evaluate axis funding — that is Generic Funding's job, not
        //     Assignment's, and combining the two is exactly the FundedActionableNow-style composite
        //     concept this architecture forbids.
        // =======================================================================================
        public static ReconAssignmentCandidateResult EvaluateCandidate(AiTurnContext ctx,
            PlayerSetupData player, WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (mover == null)
                return ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.ActorMissing);
            if (ctx?.Map == null)
                return ReconAssignmentCandidateResult.Ok;

            ArmyData live = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == mover.ArmyId);
            if (live == null)
                return ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.ActorMissing);

            if (target.Kind != ScoutTargetKind.Surveil)
                return SafeStepPathing.FindNextSafeStep(ctx.Map, live, target.FocusHex) != null
                    ? ReconAssignmentCandidateResult.Ok
                    : ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.NoRoute);

            foreach (SurveilVantageCandidate v in SurveilVantageSelector.Rank(snap, mover, target))
                if (SafeStepPathing.FindNextSafeStep(ctx.Map, live, v.ExecutionHex) != null)
                    return ReconAssignmentCandidateResult.Ok;
            return ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.NoReachableVantage);
        }

        // Thin bool convenience over EvaluateCandidate — kept because most callers only need the
        // yes/no answer (Demand's witness matching, the assignment solver's pairwise feasibility
        // filter). A trivial local alias inside Assignment, not a second facade.
        internal static bool CanExecute(AiTurnContext ctx, PlayerSetupData player, WorldSnapshot snap,
            ArmySnapshot mover, ScoutMissionTarget target) =>
            EvaluateCandidate(ctx, player, snap, mover, target).Feasible;

        // =======================================================================================
        //  E. ResolveExecutionHex — Explore/Refresh execute AT the target; Surveil executes from the
        //     best currently-reachable vantage.
        // =======================================================================================
        internal static HexCoord ResolveExecutionHex(WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (target.Kind != ScoutTargetKind.Surveil)
                return target.FocusHex;
            var vantages = SurveilVantageSelector.Rank(snap, mover, target).ToList();
            return vantages.Count > 0 ? vantages[0].ExecutionHex : target.FocusHex;
        }

        // =======================================================================================
        //  B/D. BuildCandidates / AssignFunded — real Provisioning-time assignment. Only FUNDED
        //     missions participate (moved here verbatim from ProvisioningManager, spec §14 — the
        //     one-to-one solver + its scoring stay behaviourally identical, only the owner moves).
        // =======================================================================================
        internal static List<ScoutExecutionCandidate> BuildCandidates(WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, ScoutMissionTarget target, ISet<int> excludeArmyIds)
        {
            var list = new List<ScoutExecutionCandidate>();
            bool stealthRequired = target.Stealth == StealthRequirement.Required;
            bool surveil = target.Kind == ScoutTargetKind.Surveil;

            List<ArmySnapshot> movers = ScoutMoverSelector.Eligible(snap, target, excludeArmyIds);
            foreach (ArmySnapshot mover in movers)
            {
                if (!surveil)
                {
                    if (ctx?.Map != null)
                    {
                        ArmyData liveMover = ResolveArmy(player, mover.ArmyId);
                        if (liveMover == null
                            || SafeStepPathing.FindNextSafeStep(ctx.Map, liveMover, target.FocusHex) == null)
                            continue;
                    }
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, target.FocusHex, stealthRequired);
                    list.Add(new ScoutExecutionCandidate(mover, target.FocusHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, 0f, 0, pc.AlreadyHidden, pc.RequiredAp));
                    continue;
                }

                ArmyData live = ResolveArmy(player, mover.ArmyId);
                if (live == null) continue;
                foreach (SurveilVantageCandidate v in SurveilVantageSelector.Rank(snap, mover, target))
                {
                    if (SafeStepPathing.FindNextSafeStep(ctx?.Map, live, v.ExecutionHex) == null)
                        continue;
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, v.ExecutionHex, stealthRequired: true);
                    list.Add(new ScoutExecutionCandidate(mover, v.ExecutionHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, v.DetectionRisk, v.StandOff, pc.AlreadyHidden, pc.RequiredAp));
                    break;
                }
            }
            return list;
        }

        // AssignFunded — best one-to-one actor/execution-candidate assignment across every OPEN
        // funded Scout mission at once (bounded exhaustive search + lexicographic scoring; the
        // portfolio is always small — a handful of funded Recon missions per pass). Returns the
        // chosen ScoutExecutionCandidate per mission key; a mission absent from the result got no
        // actor this pass (Provisioning reports the generic failure).
        public static Dictionary<StableMissionKey, ScoutExecutionCandidate> AssignFunded(
            WorldSnapshot snap, AiTurnContext ctx, PlayerSetupData player,
            List<FundedEntry> open, ISet<int> alreadyClaimedArmyIds)
        {
            var map = new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
            if (open == null || open.Count == 0)
                return map;

            var cands = new List<List<ScoutExecutionCandidate>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                var target = (ScoutMissionTarget)fe.Mission.Target;
                cands.Add(BuildCandidates(snap, ctx, player, target, alreadyClaimedArmyIds));
            }

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            RecurseScout(0, open, cands, chosen, new HashSet<int>(), ref bestKey, best);

            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2][Recon][Assignment] assignFunded — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv =>
                        $"{kv.Key}->#{kv.Value.Army.ArmyId}@({kv.Value.ExecutionHex.Q},{kv.Value.ExecutionHex.R})")) + "]");
            return map;
        }

        private static void RecurseScout(int i, List<FundedEntry> open, List<List<ScoutExecutionCandidate>> cands,
            int[] chosen, HashSet<int> usedArmyIds, ref long[] bestKey, int[] best)
        {
            if (i == open.Count)
            {
                long[] key = ScoreScoutAssignment(open, cands, chosen);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    System.Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            RecurseScout(i + 1, open, cands, chosen, usedArmyIds, ref bestKey, best);
            for (int c = 0; c < cands[i].Count; c++)
            {
                int aid = cands[i][c].Army.ArmyId;
                if (usedArmyIds.Contains(aid)) continue;
                usedArmyIds.Add(aid);
                chosen[i] = c;
                RecurseScout(i + 1, open, cands, chosen, usedArmyIds, ref bestKey, best);
                usedArmyIds.Remove(aid);
            }
            chosen[i] = -1;
        }

        private static long[] ScoreScoutAssignment(List<FundedEntry> open,
            List<List<ScoutExecutionCandidate>> cands, int[] chosen)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int actorDiscontinuity = 0;
            int wastedStealth = 0;
            long risk = 0, standOff = 0, requiredAp = 0, eta = 0, dist = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0) continue;
                ScoutExecutionCandidate cand = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i;

                int? preferred = open[i].Mission.PreferredMoverArmyId;
                if (preferred.HasValue && cand.Army.ArmyId != preferred.Value
                    && cands[i].Any(alt => alt.Army.ArmyId == preferred.Value))
                    actorDiscontinuity++;

                var target = (ScoutMissionTarget)open[i].Mission.Target;
                bool needStealth = target.Stealth == StealthRequirement.Required;
                if (!needStealth && cand.IsStealthCapableMover
                    && cands[i].Any(alt => !alt.IsStealthCapableMover))
                    wastedStealth++;

                risk += Mathf.RoundToInt(cand.DetectionRisk * 1_000_000f);
                standOff += cand.StandOff;
                requiredAp += Mathf.RoundToInt(cand.RequiredAp);
                eta += cand.EtaTurns;
                dist += cand.Distance;
            }

            var key = new long[9 + 3 * n];
            key[0] = -covered;
            key[1] = -priorityCoverage;
            key[2] = actorDiscontinuity;
            key[3] = wastedStealth;
            key[4] = risk;
            key[5] = -standOff;
            key[6] = requiredAp;
            key[7] = eta;
            key[8] = dist;
            for (int i = 0; i < n; i++)
            {
                int b = 9 + 3 * i;
                if (chosen[i] < 0)
                    key[b] = key[b + 1] = key[b + 2] = long.MaxValue;
                else
                {
                    ScoutExecutionCandidate cand = cands[i][chosen[i]];
                    key[b] = cand.Army.ArmyId;
                    key[b + 1] = cand.ExecutionHex.Q;
                    key[b + 2] = cand.ExecutionHex.R;
                }
            }
            return key;
        }

        private static int Lex(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0) return c;
            }
            return 0;
        }

        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        // =======================================================================================
        //  C. MeasureCapacity — Demand's ONE read-only aggregate query. Moved verbatim from
        //     DemandLayer.ComputeReconWitness/SolveReconFlow (spec §5/§9 checklist) — Demand must
        //     not know HOW the matching is produced, only the resulting witnessed counts.
        //
        //     A small max-flow network, not a bipartite matching — see the historical note kept
        //     below (unchanged reasoning, only the owner moved):
        //       1. Matching actor<->individual-objective maximises JOB COUNT, not CLASS COVERAGE.
        //       2. Anonymous per-class quota slots fix #1 but throw away JOB uniqueness.
        //     A flow network keeps BOTH constraints (job <= 1 actor, actor <= 1 job) AND the
        //     remaining per-class quota as one problem, solved breadth-then-depth so every
        //     outstanding class gets at least one unit before any class gets a second.
        // =======================================================================================
        public static ReconCapacityMeasurement MeasureCapacity(AiTurnContext ctx, PlayerSetupData player,
            WorldSnapshot snap, ReconCapacitySnapshot capacity, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, IReadOnlyList<ReconObjective> groundVisitRunnable,
            IReadOnlyList<ReconObjective> observationRunnable)
        {
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>();
            ArmySnapshot Resolve(int id) => armies.FirstOrDefault(a => a != null && a.ArmyId == id);

            var laneTarget = new Dictionary<int, ScoutMissionTarget>();
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i?.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value) || i.Scout.RequiresStealth)
                        continue;
                    laneTarget[i.PreferredMoverArmyId.Value] = new ScoutMissionTarget
                    {
                        FocusHex = i.Scout.FocusHex,
                        Kind = i.Scout.Kind,
                        Stealth = StealthRequirement.None,
                        DetectionRisk = 0f,
                    };
                }

            var laneActorClaimed = new HashSet<int>();
            int RevalidateLane(IEnumerable<int> ids)
            {
                int n = 0;
                foreach (int id in ids)
                {
                    if (laneActorClaimed.Contains(id))
                        continue;
                    ArmySnapshot a = Resolve(id);
                    if (a == null || !laneTarget.TryGetValue(id, out ScoutMissionTarget t))
                        continue;
                    if (CanExecute(ctx, player, snap, a, t))
                    {
                        laneActorClaimed.Add(id);
                        n++;
                    }
                }
                return n;
            }

            int groundLaneWitnessed = RevalidateLane(capacity.GenericGroundLaneActors);
            int obsLaneWitnessed = RevalidateLane(capacity.GenericObservationLaneActors);

            var idleActors = armies.Where(a => a != null && capacity.IdleGroundScouts.Contains(a.ArmyId)).ToList();
            int remainingGroundSlots = Mathf.Max(0, capacity.DesiredGroundTraversalConcurrency - groundLaneWitnessed);
            int remainingObsSlots = Mathf.Max(0, capacity.DesiredObservationConcurrency - obsLaneWitnessed
                - capacity.AirborneReconLanes - capacity.SpareAirObservationSorties);

            (int groundP1, int obsP1, var usedActors, var usedGroundIdx, var usedObsIdx) = SolveReconFlow(
                ctx, player, snap, idleActors, groundVisitRunnable, observationRunnable,
                Mathf.Min(remainingGroundSlots, 1), Mathf.Min(remainingObsSlots, 1));

            var leftoverActors = idleActors.Where(a => !usedActors.Contains(a.ArmyId)).ToList();
            var leftoverGround = groundVisitRunnable == null ? new List<ReconObjective>()
                : groundVisitRunnable.Where((o, idx) => !usedGroundIdx.Contains(idx)).ToList();
            var leftoverObs = observationRunnable == null ? new List<ReconObjective>()
                : observationRunnable.Where((o, idx) => !usedObsIdx.Contains(idx)).ToList();

            (int groundP2, int obsP2, _, _, _) = SolveReconFlow(
                ctx, player, snap, leftoverActors, leftoverGround, leftoverObs,
                remainingGroundSlots - groundP1, remainingObsSlots - obsP1);

            int groundIdleWitnessed = groundP1 + groundP2;
            int obsIdleWitnessed = obsP1 + obsP2;

            return new ReconCapacityMeasurement(groundLaneWitnessed, obsLaneWitnessed, groundIdleWitnessed, obsIdleWitnessed);
        }

        // One max-flow solve: source -> each actor (cap 1) -> each individual job it can reach
        // (cap 1) -> that job's class-aggregator (cap 1, job uniqueness) -> sink (cap = groundCap /
        // obsCap). Returns the flow used per class, plus WHICH actors (by ArmyId) and WHICH job
        // indices carried flow, so a caller running a follow-up phase can exclude them.
        private static (int GroundUsed, int ObsUsed, HashSet<int> UsedActorIds, HashSet<int> UsedGroundJobIndex,
            HashSet<int> UsedObsJobIndex) SolveReconFlow(AiTurnContext ctx, PlayerSetupData player, WorldSnapshot snap,
            IReadOnlyList<ArmySnapshot> actors, IReadOnlyList<ReconObjective> groundJobs,
            IReadOnlyList<ReconObjective> obsJobs, int groundCap, int obsCap)
        {
            var usedActorIds = new HashSet<int>();
            var usedGroundIdx = new HashSet<int>();
            var usedObsIdx = new HashSet<int>();
            int groundJobCount = groundJobs?.Count ?? 0;
            int obsJobCount = obsJobs?.Count ?? 0;
            groundCap = Mathf.Max(0, groundCap);
            obsCap = Mathf.Max(0, obsCap);
            if (actors == null || actors.Count == 0 || (groundCap <= 0 && obsCap <= 0)
                || (groundJobCount == 0 && obsJobCount == 0))
                return (0, 0, usedActorIds, usedGroundIdx, usedObsIdx);

            int actorBase = 1;
            int groundJobBase = actorBase + actors.Count;
            int obsJobBase = groundJobBase + groundJobCount;
            int groundAgg = obsJobBase + obsJobCount;
            int obsAgg = groundAgg + 1;
            int sink = obsAgg + 1;
            int nodeCount = sink + 1;
            var graph = new List<FlowEdge>[nodeCount];
            for (int n = 0; n < nodeCount; n++) graph[n] = new List<FlowEdge>();

            for (int i = 0; i < actors.Count; i++)
                AddFlowEdge(graph, 0, actorBase + i, 1);
            for (int j = 0; j < groundJobCount; j++)
                AddFlowEdge(graph, groundJobBase + j, groundAgg, 1);
            for (int j = 0; j < obsJobCount; j++)
                AddFlowEdge(graph, obsJobBase + j, obsAgg, 1);
            if (groundCap > 0)
                AddFlowEdge(graph, groundAgg, sink, groundCap);
            if (obsCap > 0)
                AddFlowEdge(graph, obsAgg, sink, obsCap);
            for (int i = 0; i < actors.Count; i++)
            {
                ArmySnapshot a = actors[i];
                if (groundCap > 0)
                    for (int j = 0; j < groundJobCount; j++)
                        if (CanExecute(ctx, player, snap, a, groundJobs[j].ToTarget()))
                            AddFlowEdge(graph, actorBase + i, groundJobBase + j, 1);
                if (obsCap > 0)
                    for (int j = 0; j < obsJobCount; j++)
                        if (CanExecute(ctx, player, snap, a, obsJobs[j].ToTarget()))
                            AddFlowEdge(graph, actorBase + i, obsJobBase + j, 1);
            }

            MaxFlow(graph, 0, sink);

            for (int i = 0; i < actors.Count; i++)
                if (UsedCapacity(graph, 0, actorBase + i) > 0)
                    usedActorIds.Add(actors[i].ArmyId);
            for (int j = 0; j < groundJobCount; j++)
                if (UsedCapacity(graph, groundJobBase + j, groundAgg) > 0)
                    usedGroundIdx.Add(j);
            for (int j = 0; j < obsJobCount; j++)
                if (UsedCapacity(graph, obsJobBase + j, obsAgg) > 0)
                    usedObsIdx.Add(j);

            int groundUsed = groundCap > 0 ? UsedCapacity(graph, groundAgg, sink) : 0;
            int obsUsed = obsCap > 0 ? UsedCapacity(graph, obsAgg, sink) : 0;
            return (groundUsed, obsUsed, usedActorIds, usedGroundIdx, usedObsIdx);
        }

        private sealed class FlowEdge
        {
            public int To;
            public int Capacity;
            public int Reverse;
        }

        private static void AddFlowEdge(List<FlowEdge>[] graph, int from, int to, int capacity)
        {
            graph[from].Add(new FlowEdge { To = to, Capacity = capacity, Reverse = graph[to].Count });
            graph[to].Add(new FlowEdge { To = from, Capacity = 0, Reverse = graph[from].Count - 1 });
        }

        private static int UsedCapacity(List<FlowEdge>[] graph, int from, int to)
        {
            foreach (FlowEdge e in graph[from])
                if (e.To == to)
                    return graph[to][e.Reverse].Capacity;
            return 0;
        }

        private static int MaxFlow(List<FlowEdge>[] graph, int source, int sink)
        {
            int flow = 0;
            int n = graph.Length;
            while (true)
            {
                var parentNode = new int[n];
                var parentEdge = new int[n];
                for (int i = 0; i < n; i++) { parentNode[i] = -1; parentEdge[i] = -1; }
                parentNode[source] = source;
                var queue = new Queue<int>();
                queue.Enqueue(source);
                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    if (u == sink) break;
                    for (int e = 0; e < graph[u].Count; e++)
                    {
                        FlowEdge edge = graph[u][e];
                        if (edge.Capacity > 0 && parentNode[edge.To] < 0)
                        {
                            parentNode[edge.To] = u;
                            parentEdge[edge.To] = e;
                            queue.Enqueue(edge.To);
                        }
                    }
                }
                if (parentNode[sink] < 0)
                    break;

                int aug = int.MaxValue;
                for (int v = sink; v != source; v = parentNode[v])
                    aug = Mathf.Min(aug, graph[parentNode[v]][parentEdge[v]].Capacity);
                for (int v = sink; v != source; v = parentNode[v])
                {
                    FlowEdge edge = graph[parentNode[v]][parentEdge[v]];
                    edge.Capacity -= aug;
                    graph[v][edge.Reverse].Capacity += aug;
                }
                flow += aug;
            }
            return flow;
        }
    }
}
