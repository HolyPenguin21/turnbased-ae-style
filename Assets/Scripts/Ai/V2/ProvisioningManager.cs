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
    //  PROVISIONING MANAGER  (Strategy V2 build-order step 6a — Explore end to end)
    // ===========================================================================================
    //  ONE entry, ONE exit, ATOMIC. Turns a funded MissionProposal into either a ProvisionedMission
    //  (a concrete mover is assigned AND its first action is executable RIGHT NOW) or a
    //  ProvisionFailure (change nothing). No partial-commit state can exist between the doors.
    //
    //  TWO-STAGE, ONE PASS
    //    PreparePass(funded[])  — batch. Builds the Mission -> MoverArmyId assignment for the whole
    //                             current Pack once: capability-preserving (a scarce stealth scout
    //                             is not spent on an Explore that a plain scout could do), respects
    //                             funded priority, maximises the number of provisionable missions.
    //                             Re-run on every re-Pack over the then-current funded remainder,
    //                             with movers locked by earlier passes excluded.
    //    Provision(session, fe) — per mission, in priority order. Consumes the pre-computed
    //                             assignment: live re-validation -> target still valid ->
    //                             first-step preflight (VisitHexTask.FindNextSafeStep, reused
    //                             as-is) -> AP envelope check -> atomic claim. The AP the mission
    //                             claims is bounded by funded.Tentative (allocator invariant) and
    //                             by the turn's remaining AP net of earlier claims this cycle.
    //
    //  SESSIONS
    //    AllocationSession  — the money side: rejected / locked / fingerprint / repricing floors.
    //    ProvisioningSession — the actor side: the shared WorldSnapshot, running AP claim total,
    //                          army ids locked this turn, the per-pass assignment, successful plans.
    //
    //  SCOPE (6a): Explore only. FocusHex == ExecutionHex. Surveil vantage selection is 6b;
    //  commitment lifetime is step 7; Raid army/card/equipment logic is step 9.
    // ===========================================================================================

    // --- Stage 6 output, SUCCESS branch. Everything TaskExecutor needs, plus the claim the
    //     AllocationSession locks. Holds MoverArmyId (not an ArmyData ref) so the executor
    //     re-resolves the live army before every step — a later mission this same turn may have
    //     changed the roster / position of the actor by then (matters from step 9 on).
    public sealed class ProvisionedMission
    {
        public MissionProposal Mission;
        public StableMissionKey Key;
        public MissionKind Kind;

        public int MoverArmyId;

        public HexCoord FocusHex;       // the objective
        public HexCoord ExecutionHex;   // where the mover actually goes — == FocusHex for Explore (6a)

        public float ClaimedAp;         // activation (+ stealth transition, if reserved); <= funded.Tentative
        public bool StealthApReserved;  // a 1-AP EnterStealth is paid for; the executor decides WHICH step spends it
    }

    // --- Stage 6 output, FAILURE branch. Kind is the reason (telemetry granularity); Disposition
    //     is what the allocator does about it (see ProvisionDisposition). RequiredAp is meaningful
    //     only for EnvelopeTooSmall.
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

    // Turn-scoped, created once in Pipeline.RunTurn and threaded through every pack/provision/
    // re-pack iteration. Carries the ONE strategic snapshot (so provisioning never triggers a
    // fresh WorldAnalysis.Scan), the running AP claim total, the army ids already locked this
    // turn, the per-pass assignment, and the finished plans.
    public sealed class ProvisioningSession
    {
        public readonly WorldSnapshot Snapshot;
        public float ApClaimed { get; private set; }
        public readonly HashSet<int> ClaimedArmyIds = new HashSet<int>();

        private readonly Dictionary<StableMissionKey, ProvisionedMission> _successful =
            new Dictionary<StableMissionKey, ProvisionedMission>();
        private readonly Dictionary<StableMissionKey, int> _assignment =
            new Dictionary<StableMissionKey, int>();

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

        internal void SetAssignment(Dictionary<StableMissionKey, int> a)
        {
            _assignment.Clear();
            foreach (KeyValuePair<StableMissionKey, int> kv in a)
                _assignment[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedMover(StableMissionKey k, out int armyId) =>
            _assignment.TryGetValue(k, out armyId);
    }

    internal static class ProvisioningManager
    {
        // Stealth transition cost — the same 1 AP the gameplay layer charges for a voluntary
        // EnterStealth (AiTurnController.MoveArmyRoutine hardcodes root.SpendActionPoints(1)).
        // Read from the V2-side single source rather than a fresh literal here; TODO: fold both
        // into one shared gameplay constant if StealthSystem ever grows one, so a rules change to
        // the real cost can't silently desync the allocator.
        private static int StealthTransitionApCost => AiConfigV2.scoutOptionalStealthAp;

        // =======================================================================================
        //  BATCH: assign concrete movers to the funded missions of THIS pack.
        // =======================================================================================
        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Scout)
                        continue; // 6a: Scout only — Raid provisioning is step 9
                    if (session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue; // locked by an earlier pass — not re-assigned
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            // Ranked candidate list per open mission — movers locked earlier this turn excluded.
            var cands = new List<List<ScoutMoverCandidate>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                var target = (ScoutMissionTarget)fe.Mission.Target;
                cands.Add(ScoutMoverSelector.Rank(session.Snapshot, target, session.ClaimedArmyIds));
            }

            // Brute-force the best injective assignment (N is maxConcurrentRecon-small). Criteria,
            // most significant first: cover more missions -> cover the higher-priority ones ->
            // don't waste a stealth-capable mover on a non-stealth mission that a plain one could
            // take -> less AP -> shorter ETA -> shorter distance -> lower ArmyId (determinism).
            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            Recurse(0, open, cands, chosen, new HashSet<int>(), ref bestKey, best);

            var map = new Dictionary<StableMissionKey, int>();
            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]].Army.ArmyId;
            session.SetAssignment(map);

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2]   provision prepare — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv => $"{kv.Key}->#{kv.Value}")) + "]");
        }

        private static void Recurse(int i, List<FundedEntry> open, List<List<ScoutMoverCandidate>> cands,
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

            // option: leave mission i unassigned
            chosen[i] = -1;
            Recurse(i + 1, open, cands, chosen, usedArmyIds, ref bestKey, best);

            // option: each still-free candidate mover
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

        private static long[] ScoreAssignment(List<FundedEntry> open, List<List<ScoutMoverCandidate>> cands, int[] chosen)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int wastedStealth = 0;
            long effAp = 0, eta = 0, dist = 0, armyIdSum = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0)
                    continue;
                ScoutMoverCandidate cand = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i; // earlier index == higher priority == bigger bonus

                var target = (ScoutMissionTarget)open[i].Mission.Target;
                bool needStealth = target.Stealth == StealthRequirement.Required;
                bool moverStealthy = cand.Army.IsHidden || cand.Army.CanEnterStealth;
                if (!needStealth && moverStealthy && cands[i].Any(alt => !(alt.Army.IsHidden || alt.Army.CanEnterStealth)))
                    wastedStealth++;

                effAp += cand.EffActivationAp;
                eta += cand.EtaTurns;
                dist += cand.Distance;
                armyIdSum += cand.Army.ArmyId;
            }

            return new[]
            {
                -(long)covered,
                -priorityCoverage,
                (long)wastedStealth,
                effAp,
                eta,
                dist,
                armyIdSum,
            };
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
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("6a provisions Scout missions only"));

            StableMissionKey key = StableMissionKey.For(m);

            // 1. concrete mover from the per-pass assignment.
            if (!session.TryGetAssignedMover(key, out int moverArmyId))
            {
                var raw = ScoutMoverSelector.Rank(session.Snapshot, target, session.ClaimedArmyIds);
                return raw.Count == 0
                    ? ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                        "no eligible solo Recce on the map" + (target.Stealth == StealthRequirement.Required ? " that can go stealth" : "")))
                    : ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"{raw.Count} capable mover(s), all taken by higher-priority missions this cycle"));
            }

            ArmyData army = ResolveArmy(player, moverArmyId);
            if (army == null || army.Owner != player || army.Members.Count == 0
                || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));

            // 2. target still valid (Explore: an unvisited, not-known-occupied frontier hex).
            HexCoord focus = target.FocusHex;
            if (VisionSystem.IsVisited(player, focus))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"focus ({focus.Q},{focus.R}) already visited — nothing left to discover there"));
            if (AiMapMemory.KnownEnemySightingAt(player, focus).HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"focus ({focus.Q},{focus.R}) now holds a known army"));

            // 3. first-step preflight — reuse V1's fog-safe stepper. ExecutionHex == focus in 6a.
            HexCoord executionHex = focus;
            HexCoord? firstStep = VisitHexTask.FindNextSafeStep(ctx.Map, army, executionHex);
            if (firstStep == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({army.Hex.Q},{army.Hex.R}) toward ({executionHex.Q},{executionHex.R})"));

            // 4. AP: the REAL cost of this mover, computed with the same rules execution applies.
            int activationAp = army.HasActivatedThisTurn ? 0 : army.ActivationApCost;
            bool alreadyHidden = army.Members.Any(mem => mem.IsHidden);
            bool reserveStealth = target.Stealth == StealthRequirement.Required
                && !alreadyHidden
                && AiScoutStealthPolicy.MoveWarrantsStealth(player, army, firstStep.Value);
            int stealthAp = reserveStealth ? StealthTransitionApCost : 0;
            float realNeed = activationAp + stealthAp;

            float eps = AiConfigV2.allocatorSliceEpsilon;
            float envelope = funded.Tentative.Ap;
            if (realNeed > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(realNeed,
                    $"mover #{moverArmyId} needs {N(realNeed)} AP, envelope is {N(envelope)}"));

            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (realNeed > turnApLeft + eps)
                // Unreachable in 6a (no commitments -> Σ Tentative <= pool <= ActionPoints), but a
                // real guard once commitments / step 9 can overdraw the pool. Transient contention
                // on the shared AP pool, not a structural dead end.
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: need {N(realNeed)}, {N(turnApLeft)} left after earlier claims"));

            // 5. atomic claim — nothing was mutated above; emit the plan. The caller does
            //    session.RegisterSuccess + AllocationSession.RegisterProvisionSuccess.
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Scout,
                MoverArmyId = moverArmyId,
                FocusHex = focus,
                ExecutionHex = executionHex,
                ClaimedAp = realNeed,
                StealthApReserved = stealthAp > 0,
            });
        }

        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
