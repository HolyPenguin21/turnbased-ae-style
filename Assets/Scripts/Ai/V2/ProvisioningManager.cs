using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  PROVISIONING MANAGER  (Strategy V2 build-order step 6 — Explore 6a + Surveil 6b)
    // ===========================================================================================
    //  ONE entry, ONE exit, ATOMIC. Turns a funded MissionProposal into either a ProvisionedMission
    //  (a concrete mover + ExecutionHex, with its first action executable RIGHT NOW) or a
    //  ProvisionFailure (change nothing). No partial-commit state can exist between the doors.
    //
    //  TWO-STAGE, ONE PASS
    //    PreparePass(funded[]) — batch. Builds, per funded mission, the ranked list of executable
    //                            (mover, ExecutionHex) pairs (ScoutExecutionCandidate), then a
    //                            global injective assignment across missions: cover more missions
    //                            -> cover higher-priority ones -> preserve a scarce stealth mover
    //                            -> lower surveillance risk -> greater stand-off -> less AP -> ETA
    //                            -> distance -> deterministic ids/coords. Re-run every re-Pack over
    //                            the funded remainder, with earlier-locked movers excluded.
    //    Provision(session, fe) — per mission, in priority order. Consumes the assigned pair:
    //                            live re-validation -> objective still open -> first-step preflight
    //                            (VisitHexTask.FindNextSafeStep) -> Surveil: vantage still sees the
    //                            focus -> AP envelope check -> atomic claim.
    //
    //  EXPLORE vs SURVEIL
    //    Explore  — ExecutionHex == FocusHex. "Done" = the frontier hex was reached / visited.
    //    Surveil  — ExecutionHex is a safe vantage; the scout NEVER steps onto FocusHex. "Done" is
    //               INFORMATION: FocusHex re-observed, or TrackedArmyId re-sighted anywhere fresher
    //               than BaselineObservedTurn. Reaching the vantage is only the means.
    //
    //  SESSIONS
    //    AllocationSession  — money: rejected / locked / fingerprint / repricing floors.
    //    ProvisioningSession — actors: the shared WorldSnapshot, running AP claim, ids locked this
    //                          turn, the per-pass assignment, the finished plans.
    // ===========================================================================================

    public sealed class ProvisionedMission
    {
        public MissionProposal Mission;
        public StableMissionKey Key;
        public MissionKind Kind;
        public ScoutTargetKind ScoutKind;   // Explore | Surveil

        public int MoverArmyId;             // re-resolved to live ArmyData before every executor step

        public HexCoord FocusHex;           // the information objective
        public HexCoord ExecutionHex;       // where the mover goes — == FocusHex for Explore

        // Surveil only (TrackedArmyId null / BaselineObservedTurn 0 for Explore). Completion =
        // a sighting of TrackedArmyId with SeenTurn > BaselineObservedTurn, OR FocusHex visible.
        public int? TrackedArmyId;
        public int BaselineObservedTurn;

        public float ClaimedAp;
        public bool StealthApReserved;
    }

    public readonly struct ProvisionFailure
    {
        public readonly ProvisionFailureKind Kind;
        public readonly ProvisionDisposition Disposition;
        public readonly float RequiredAp;
        public readonly string Detail;

        public ProvisionFailure(ProvisionFailureKind kind, ProvisionDisposition disposition, float requiredAp, string detail)
        {
            Kind = kind;
            Disposition = disposition;
            RequiredAp = requiredAp;
            Detail = detail;
        }

        public static ProvisionFailure MoverContended(string d) =>
            new ProvisionFailure(ProvisionFailureKind.MoverContended, ProvisionDisposition.RetryNextTurn, 0f, d);
        public static ProvisionFailure NoMoverExists(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoMoverExists, ProvisionDisposition.RejectWithCooldown, 0f, d);
        public static ProvisionFailure NoObservationVantage(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoObservationVantage, ProvisionDisposition.RejectWithCooldown, 0f, d);
        public static ProvisionFailure EnvelopeTooSmall(float requiredAp, string d) =>
            new ProvisionFailure(ProvisionFailureKind.EnvelopeTooSmall, ProvisionDisposition.RepriceThisTurn, requiredAp, d);
        public static ProvisionFailure NoExecutableStep(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoExecutableStep, ProvisionDisposition.RetryNextTurn, 0f, d);
        public static ProvisionFailure TargetSatisfied(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetSatisfied, ProvisionDisposition.DropThisTurn, 0f, d);
        public static ProvisionFailure TargetInvalidated(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetInvalidated, ProvisionDisposition.RetryNextTurn, 0f, d);
        public static ProvisionFailure AssemblyInfeasible(string d) =>
            new ProvisionFailure(ProvisionFailureKind.AssemblyInfeasible, ProvisionDisposition.RejectWithCooldown, 0f, d);
    }

    public sealed class ProvisioningResult
    {
        public bool Success;
        public ProvisionedMission Provisioned;   // non-null iff Success
        public ProvisionFailure Failure;         // valid iff !Success

        public static ProvisioningResult Ok(ProvisionedMission m) =>
            new ProvisioningResult { Success = true, Provisioned = m };
        public static ProvisioningResult Fail(ProvisionFailure f) =>
            new ProvisioningResult { Success = false, Failure = f };
    }

    public sealed class ProvisioningSession
    {
        public readonly WorldSnapshot Snapshot;
        public float ApClaimed { get; private set; }
        public readonly HashSet<int> ClaimedArmyIds = new HashSet<int>();

        private readonly Dictionary<StableMissionKey, ProvisionedMission> _successful =
            new Dictionary<StableMissionKey, ProvisionedMission>();
        private readonly Dictionary<StableMissionKey, ScoutExecutionCandidate> _assignment =
            new Dictionary<StableMissionKey, ScoutExecutionCandidate>();

        public ProvisioningSession(WorldSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public IReadOnlyDictionary<StableMissionKey, ProvisionedMission> Successful => _successful;
        public bool AlreadyProvisioned(StableMissionKey k) => _successful.ContainsKey(k);

        public void RegisterSuccess(StableMissionKey k, ProvisionedMission m)
        {
            _successful[k] = m;
            ApClaimed += m.ClaimedAp;
            ClaimedArmyIds.Add(m.MoverArmyId);
        }

        internal void SetAssignment(Dictionary<StableMissionKey, ScoutExecutionCandidate> a)
        {
            _assignment.Clear();
            foreach (KeyValuePair<StableMissionKey, ScoutExecutionCandidate> kv in a)
                _assignment[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedExecution(StableMissionKey k, out ScoutExecutionCandidate exec) =>
            _assignment.TryGetValue(k, out exec);
    }

    internal static class ProvisioningManager
    {
        private static int StealthTransitionApCost => AiConfigV2.scoutOptionalStealthAp;

        // =======================================================================================
        //  BATCH: build (mover, ExecutionHex) candidates and assign them to the funded missions.
        // =======================================================================================
        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Scout)
                        continue; // Scout only — Raid provisioning is step 9
                    if (!(fe.Mission.Target is ScoutMissionTarget))
                        continue;
                    if (session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue; // locked by an earlier pass — not re-assigned
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            var cands = new List<List<ScoutExecutionCandidate>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                var target = (ScoutMissionTarget)fe.Mission.Target;
                cands.Add(BuildExecutionCandidates(session.Snapshot, ctx, player, target, session.ClaimedArmyIds));
            }

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            Recurse(0, open, cands, chosen, new HashSet<int>(), ref bestKey, best);

            var map = new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];
            session.SetAssignment(map);

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2]   provision prepare — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv =>
                        $"{kv.Key}->#{kv.Value.Army.ArmyId}@({kv.Value.ExecutionHex.Q},{kv.Value.ExecutionHex.R})")) + "]");
        }

        // One ScoutExecutionCandidate per ELIGIBLE mover. Explore -> (mover, FocusHex). Surveil ->
        // (mover, first CURRENTLY-EXECUTABLE vantage from SurveilVantageSelector's ranking). A
        // mover whose every geometric vantage is unreachable this turn contributes nothing here.
        private static List<ScoutExecutionCandidate> BuildExecutionCandidates(WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, ScoutMissionTarget target, ISet<int> excludeArmyIds)
        {
            var list = new List<ScoutExecutionCandidate>();
            bool stealthRequired = target.Stealth == StealthRequirement.Required;
            bool surveil = target.Kind == ScoutTargetKind.Surveil;

            foreach (ArmySnapshot mover in ScoutMoverSelector.Eligible(snap, target, excludeArmyIds))
            {
                if (!surveil)
                {
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, target.FocusHex, stealthRequired);
                    list.Add(new ScoutExecutionCandidate(mover, target.FocusHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, 0f, 0, pc.AlreadyHidden, pc.RequiredAp));
                    continue;
                }

                ArmyData live = ResolveArmy(player, mover.ArmyId);
                if (live == null)
                    continue;
                foreach (SurveilVantageCandidate v in SurveilVantageSelector.Rank(snap, mover, target))
                {
                    if (VisitHexTask.FindNextSafeStep(ctx?.Map, live, v.ExecutionHex) == null)
                        continue; // not executable this turn — try the next-safest vantage
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, v.ExecutionHex, stealthRequired: true);
                    list.Add(new ScoutExecutionCandidate(mover, v.ExecutionHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, v.DetectionRisk, v.StandOff, pc.AlreadyHidden, pc.RequiredAp));
                    break; // ONE candidate per mover
                }
            }
            return list;
        }

        private static void Recurse(int i, List<FundedEntry> open, List<List<ScoutExecutionCandidate>> cands,
            int[] chosen, HashSet<int> usedArmyIds, ref long[] bestKey, int[] best)
        {
            if (i == open.Count)
            {
                long[] key = ScoreAssignment(open, cands, chosen);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            Recurse(i + 1, open, cands, chosen, usedArmyIds, ref bestKey, best);

            for (int c = 0; c < cands[i].Count; c++)
            {
                int aid = cands[i][c].Army.ArmyId;
                if (usedArmyIds.Contains(aid))
                    continue;
                usedArmyIds.Add(aid);
                chosen[i] = c;
                Recurse(i + 1, open, cands, chosen, usedArmyIds, ref bestKey, best);
                usedArmyIds.Remove(aid);
            }
            chosen[i] = -1;
        }

        // Lex key, most significant first:
        //   coverage -> mission priority -> preserve scarce stealth -> surveillance risk ->
        //   stand-off (bigger safer) -> AP -> ETA -> distance -> deterministic (armyId,Q,R) tuple.
        private static long[] ScoreAssignment(List<FundedEntry> open, List<List<ScoutExecutionCandidate>> cands, int[] chosen)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int wastedStealth = 0;
            long risk = 0, standOff = 0, requiredAp = 0, eta = 0, dist = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0)
                    continue;
                ScoutExecutionCandidate cand = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i;

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

            var key = new long[8 + 3 * n];
            key[0] = -(long)covered;
            key[1] = -priorityCoverage;
            key[2] = wastedStealth;
            key[3] = risk;
            key[4] = -standOff;
            key[5] = requiredAp;
            key[6] = eta;
            key[7] = dist;
            for (int i = 0; i < n; i++)
            {
                int b = 8 + 3 * i;
                if (chosen[i] < 0)
                {
                    key[b] = key[b + 1] = key[b + 2] = long.MaxValue;
                }
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

        // =======================================================================================
        //  THE DOOR: provision one funded mission, atomically.
        // =======================================================================================
        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded?.Mission;
            if (m == null || ctx?.Map == null || root == null)
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("no mission / map / root"));
            if (m.Kind != MissionKind.Scout || !(m.Target is ScoutMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("provisions Scout missions only"));

            StableMissionKey key = StableMissionKey.For(m);
            bool surveil = target.Kind == ScoutTargetKind.Surveil;

            // 1. assigned (mover, ExecutionHex) pair.
            if (!session.TryGetAssignedExecution(key, out ScoutExecutionCandidate exec))
                return ClassifyNoAssignment(session, target, surveil);

            int moverArmyId = exec.Army.ArmyId;
            ArmyData army = ResolveArmy(player, moverArmyId);
            if (army == null || army.Owner != player || army.Members.Count == 0
                || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));

            HexCoord focus = target.FocusHex;
            HexCoord executionHex = exec.ExecutionHex;

            // 2. objective still open?
            if (surveil)
            {
                int trackedId = target.Contact?.Army?.ArmyId ?? -1;
                if (trackedId < 0 || target.Contact.Source != ContactSource.Honest
                    || target.Contact.Knowledge != ContactKnowledge.LastKnown)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        "surveil target is no longer an honest last-known contact"));
                int baseline = target.Contact.LastObservedTurn;
                if (VisionSystem.IsVisible(player, focus) || HasFresherSighting(player, trackedId, baseline))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"tracked #{trackedId} already re-observed (focus ({focus.Q},{focus.R}), baseline turn {baseline})"));
                if (executionHex.Equals(focus))
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        "surveil ExecutionHex == FocusHex — invariant violation"));
                if (HexGridMath.Distance(executionHex, focus) > exec.Army.EffectiveVisionRadius)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"mover #{moverArmyId} vision {exec.Army.EffectiveVisionRadius} no longer covers focus from vantage"));
            }
            else
            {
                if (VisionSystem.IsVisited(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"focus ({focus.Q},{focus.R}) already visited — nothing left to discover there"));
                if (AiMapMemory.KnownEnemySightingAt(player, focus).HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"focus ({focus.Q},{focus.R}) now holds a known army"));
            }

            // 3. first-step preflight toward ExecutionHex (V1's fog-safe stepper, reused).
            HexCoord? firstStep = VisitHexTask.FindNextSafeStep(ctx.Map, army, executionHex);
            if (firstStep == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({army.Hex.Q},{army.Hex.R}) toward ({executionHex.Q},{executionHex.R})"));

            // 4. AP: real cost, same rules execution applies. A stealth-Required mission whose
            //    mover is not already hidden ALWAYS reserves the 1 AP and enters stealth up front.
            int activationAp = army.HasActivatedThisTurn ? 0 : army.ActivationApCost;
            bool alreadyHidden = army.Members.Any(mem => mem.IsHidden);
            bool reserveStealth = target.Stealth == StealthRequirement.Required && !alreadyHidden;
            int stealthAp = reserveStealth ? StealthTransitionApCost : 0;
            float realNeed = activationAp + stealthAp;

            float eps = AiConfigV2.allocatorSliceEpsilon;
            float envelope = funded.Tentative.Ap;
            if (realNeed > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(realNeed,
                    $"mover #{moverArmyId} needs {N(realNeed)} AP, envelope is {N(envelope)}"));

            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (realNeed > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: need {N(realNeed)}, {N(turnApLeft)} left after earlier claims"));

            // 5. atomic claim.
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Scout,
                ScoutKind = target.Kind,
                MoverArmyId = moverArmyId,
                FocusHex = focus,
                ExecutionHex = executionHex,
                TrackedArmyId = surveil ? target.Contact.Army.ArmyId : (int?)null,
                BaselineObservedTurn = surveil ? target.Contact.LastObservedTurn : 0,
                ClaimedAp = realNeed,
                StealthApReserved = stealthAp > 0,
            });
        }

        // No assignment for this mission this pass. Explore keeps the 6a two-way split. Surveil
        // adds NoObservationVantage between "no scout at all" and "scouts busy": a capable scout
        // exists but no on-map hex within ANY structural scout's vision can observe the focus.
        // "Vantage exists but no safe route to it today" stays transient NoExecutableStep, NOT a
        // structural cooldown.
        private static ProvisioningResult ClassifyNoAssignment(ProvisioningSession session,
            ScoutMissionTarget target, bool surveil)
        {
            WorldSnapshot snap = session.Snapshot;
            bool needStealth = target.Stealth == StealthRequirement.Required;

            if (!ScoutMoverSelector.HasStructuralCandidate(snap, target))
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "no solo Recce" + (needStealth ? " with stealth capability" : "") + " on the map"));

            if (!surveil)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "a capable solo Recce exists but is spent / activated / taken this cycle"));

            bool anyStructuralVantage = ScoutMoverSelector.StructuralCandidates(snap, target)
                .Any(mv => SurveilVantageSelector.Rank(snap, mv, target).Count > 0);
            if (!anyStructuralVantage)
                return ProvisioningResult.Fail(ProvisionFailure.NoObservationVantage(
                    $"no on-map vantage within any scout's vision of ({target.FocusHex.Q},{target.FocusHex.R})"));

            bool anyEligibleHasVantage = ScoutMoverSelector.Eligible(snap, target, session.ClaimedArmyIds)
                .Any(mv => SurveilVantageSelector.Rank(snap, mv, target).Count > 0);
            return anyEligibleHasVantage
                ? ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    "a vantage exists but no safe first step to it this turn"))
                : ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "mover+vantage exist but every such scout is spent / claimed this cycle"));
        }

        private static bool HasFresherSighting(PlayerSetupData player, int trackedArmyId, int baselineTurn)
        {
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == trackedArmyId && s.SeenTurn > baselineTurn)
                    return true;
            return false;
        }

        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
