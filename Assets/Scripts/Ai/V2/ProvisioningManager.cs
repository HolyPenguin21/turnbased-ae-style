using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    public sealed class ProvisionedMission
    {
        public MissionProposal Mission;
        public StableMissionKey Key;
        public MissionKind Kind;
        public ScoutTargetKind ScoutKind;
        public int MoverArmyId;
        public HexCoord FocusHex;
        public HexCoord ExecutionHex;
        public int? TrackedArmyId;
        public int BaselineObservedTurn;
        public int RaidTargetArmyId;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        public ResourceVector ClaimedPhysical;
        public float ClaimedAp;
        public bool StealthApReserved;
        public bool IsReplacement;
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
            new ProvisionFailure(ProvisionFailureKind.NoMoverExists, ProvisionDisposition.RetryNextTurn, 0f, d);
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
        public ProvisionedMission Provisioned;
        public ProvisionFailure Failure;

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
        private readonly Dictionary<StableMissionKey, int> _raidAssignment =
            new Dictionary<StableMissionKey, int>();

        public ProvisioningSession(WorldSnapshot snapshot) { Snapshot = snapshot; }
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

        internal void SetRaidAssignment(Dictionary<StableMissionKey, int> a)
        {
            _raidAssignment.Clear();
            foreach (KeyValuePair<StableMissionKey, int> kv in a)
                _raidAssignment[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedRaidActor(StableMissionKey k, out int armyId) =>
            _raidAssignment.TryGetValue(k, out armyId);
    }

    internal static class ProvisioningManager
    {
        private static int StealthTransitionApCost => AiConfigV2.scoutOptionalStealthAp;

        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation)
        {
            PrepareScoutAssignments(player, ctx, session, allocation);
            PrepareRaidAssignments(session, allocation);
        }

        private static void PrepareScoutAssignments(PlayerSetupData player, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Scout
                        || !(fe.Mission.Target is ScoutMissionTarget)
                        || session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue;
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
            RecurseScout(0, open, cands, chosen, new HashSet<int>(), ref bestKey, best);

            var map = new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];
            session.SetAssignment(map);

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2]   provision prepare scout — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv =>
                        $"{kv.Key}->#{kv.Value.Army.ArmyId}@({kv.Value.ExecutionHex.Q},{kv.Value.ExecutionHex.R})")) + "]");
        }

        private static void PrepareRaidAssignments(ProvisioningSession session, TentativeAllocation allocation)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Raid
                        || session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
                        continue;
                    open.Add(fe);
                }
            open.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            var cands = new List<List<int>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                var ids = new List<int>();
                if (RaidAdmissionRegistry.TryGet(fe.Mission, out HashSet<int> eligible))
                    ids.AddRange(eligible.Where(id => !session.ClaimedArmyIds.Contains(id))
                        .OrderBy(id => RaidActorActivation(session.Snapshot, id))
                        .ThenBy(id => RaidActorPower(session.Snapshot, id))
                        .ThenBy(id => id));
                cands.Add(ids);
            }

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            RecurseRaid(0, open, cands, chosen, new HashSet<int>(), session.Snapshot, ref bestKey, best);

            var map = new Dictionary<StableMissionKey, int>();
            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    map[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];
            session.SetRaidAssignment(map);

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2]   provision prepare raid — {open.Count} open, assigned ["
                    + string.Join(" ", map.Select(kv => $"{kv.Key}->#{kv.Value}")) + "]");
        }

        private static int RaidActorActivation(WorldSnapshot snap, int id)
        {
            ArmySnapshot a = snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
            return a == null || a.HasActivatedThisTurn ? 0 : a.ActivationApCost;
        }

        private static float RaidActorPower(WorldSnapshot snap, int id) =>
            snap?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id)?.EffectiveArmyPower ?? float.MaxValue;

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
                    // Spec §3 — an (objective, actor) pair only enters the assignment solve if THIS
                    // actor can currently take a safe first step toward the objective. Without this
                    // the solver reserves a physical scout for a Refresh/Explore that provisioning
                    // already knows cannot move (NoExecutableStep), starving an executable Explore
                    // incumbent with a false MoverContended. Executability is actor-specific: a
                    // different eligible mover with a real route still yields its own candidate, and
                    // the objective is never globally dropped here.
                    if (ctx?.Map != null)
                    {
                        ArmyData liveMover = ResolveArmy(player, mover.ArmyId);
                        if (liveMover == null
                            || VisitHexTask.FindNextSafeStep(ctx.Map, liveMover, target.FocusHex) == null)
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
                    if (VisitHexTask.FindNextSafeStep(ctx?.Map, live, v.ExecutionHex) == null)
                        continue;
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, v.ExecutionHex, stealthRequired: true);
                    list.Add(new ScoutExecutionCandidate(mover, v.ExecutionHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, v.DetectionRisk, v.StandOff, pc.AlreadyHidden, pc.RequiredAp));
                    break;
                }
            }
            return list;
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
                    Array.Copy(chosen, best, chosen.Length);
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

        private static void RecurseRaid(int i, List<FundedEntry> open, List<List<int>> cands,
            int[] chosen, HashSet<int> usedArmyIds, WorldSnapshot snap, ref long[] bestKey, int[] best)
        {
            if (i == open.Count)
            {
                long[] key = ScoreRaidAssignment(open, cands, chosen, snap);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            RecurseRaid(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
            for (int c = 0; c < cands[i].Count; c++)
            {
                int aid = cands[i][c];
                if (usedArmyIds.Contains(aid)) continue;
                usedArmyIds.Add(aid);
                chosen[i] = c;
                RecurseRaid(i + 1, open, cands, chosen, usedArmyIds, snap, ref bestKey, best);
                usedArmyIds.Remove(aid);
            }
            chosen[i] = -1;
        }

        private static long[] ScoreRaidAssignment(List<FundedEntry> open, List<List<int>> cands,
            int[] chosen, WorldSnapshot snap)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int actorDiscontinuity = 0;
            long activation = 0;
            long overkillPower = 0;
            long actorIdSum = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0) continue;
                int actorId = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i;
                activation += RaidActorActivation(snap, actorId);
                overkillPower += Mathf.RoundToInt(RaidActorPower(snap, actorId) * 100f);
                actorIdSum += actorId;

                int? preferred = open[i].Mission.PreferredMoverArmyId;
                if (preferred.HasValue && actorId != preferred.Value && cands[i].Contains(preferred.Value))
                    actorDiscontinuity++;
            }

            var key = new long[6 + n];
            key[0] = -covered;
            key[1] = -priorityCoverage;
            key[2] = actorDiscontinuity;
            key[3] = activation;
            key[4] = overkillPower;
            key[5] = actorIdSum;
            for (int i = 0; i < n; i++)
                key[6 + i] = chosen[i] < 0 ? long.MaxValue : cands[i][chosen[i]];
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

        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded?.Mission;
            if (m == null || ctx?.Map == null || root == null)
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("no mission / map / root"));

            if (m.Kind == MissionKind.Raid)
                return RaidProvisioner.Provision(player, root, ctx, session, funded);

            if (m.Kind != MissionKind.Scout || !(m.Target is ScoutMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("provisions Scout / Raid missions only"));

            StableMissionKey key = StableMissionKey.For(m);
            bool surveil = target.Kind == ScoutTargetKind.Surveil;
            bool refresh = ReconScoutKinds.IsRefresh(target.Kind);

            if (!session.TryGetAssignedExecution(key, out ScoutExecutionCandidate exec))
                return ClassifyNoAssignment(session, player, ctx, target, surveil);

            int moverArmyId = exec.Army.ArmyId;
            ArmyData army = ResolveArmy(player, moverArmyId);
            if (army == null || army.Owner != player || army.Members.Count == 0
                || !AiArmyRoles.IsSoloRecce(army) || army.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"assigned mover #{moverArmyId} is no longer a usable solo Recce"));

            HexCoord focus = target.FocusHex;
            HexCoord executionHex = exec.ExecutionHex;

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
                if (ScoutExecutionSafety.VantageBlockedNow(player, executionHex, ctx.TurnNumber))
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"vantage ({executionHex.Q},{executionHex.R}) is now occupied by a current force / foreign building"));
            }
            else if (refresh)
            {
                // A Refresh target was selected because frozen IntelAge was stale. Previously
                // Visited ground remains valid; only a NEW current observation completes it.
                if (ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"refresh focus ({focus.Q},{focus.R}) is already visible again"));
                if (AiMapMemory.KnownEnemySightingAt(player, focus).HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                        $"refresh focus ({focus.Q},{focus.R}) now holds a known army"));
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

            HexCoord? firstStep = VisitHexTask.FindNextSafeStep(ctx.Map, army, executionHex);
            if (firstStep == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({army.Hex.Q},{army.Hex.R}) toward ({executionHex.Q},{executionHex.R})"));

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

        private static ProvisioningResult ClassifyNoAssignment(ProvisioningSession session,
            PlayerSetupData player, AiTurnContext ctx, ScoutMissionTarget target, bool surveil)
        {
            WorldSnapshot snap = session.Snapshot;
            bool needStealth = target.Stealth == StealthRequirement.Required;

            if (!ScoutMoverSelector.HasStructuralCandidate(snap, target))
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "no solo Recce" + (needStealth ? " with stealth capability" : "") + " on the map"));

            if (!surveil)
            {
                // Spec §3/§10 — an unassigned ground Explore/Refresh is only MoverContended if a
                // capable UNCLAIMED scout that could actually take a safe first step toward the
                // focus was preferred elsewhere. If unclaimed eligible scouts exist but NONE can
                // reach the focus this turn, that is NoExecutableStep, not contention — so a stuck
                // impossible Refresh never reads as if it stole an executable Explore's mover.
                var freeEligible = ScoutMoverSelector.Eligible(snap, target, session.ClaimedArmyIds).ToList();
                if (freeEligible.Count > 0 && ctx?.Map != null)
                {
                    bool anyReachable = freeEligible.Any(mv =>
                    {
                        ArmyData live = ResolveArmy(player, mv.ArmyId);
                        return live != null
                            && VisitHexTask.FindNextSafeStep(ctx.Map, live, target.FocusHex) != null;
                    });
                    if (!anyReachable)
                        return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                            $"eligible scout(s) exist but none can take a safe first step toward "
                            + $"({target.FocusHex.Q},{target.FocusHex.R}) this turn"));
                }
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "a capable solo Recce exists but is spent / activated / taken this cycle"));
            }

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

    internal static class RaidProvisioner
    {
        public static ProvisioningResult Provision(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded)
        {
            MissionProposal m = funded.Mission;
            if (!(m.Target is RaidMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("raid mission has no RaidMissionTarget"));

            StableMissionKey key = StableMissionKey.For(m);
            WorldSnapshot snap = session.Snapshot;
            float eps = AiConfigV2.allocatorSliceEpsilon;

            AiMapMemory.KnownEnemySighting? sighting = FindLiveSighting(player, target.TargetArmyId);
            if (sighting == null)
            {
                if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, target.TargetArmyId))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"raid target #{target.TargetArmyId} no longer exists (destroyed / captured)"));
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"raid target #{target.TargetArmyId} has no current honest sighting; absence is not proof of destruction"));
            }
            if (sighting.Value.Owner != null && !sighting.Value.Owner.IsNeutral && sighting.Value.Owner.Equals(player))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"raid target #{target.TargetArmyId} is now ours"));

            HexCoord targetHex = sighting.Value.Hex;
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                sighting.Value.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();

            RaidAssemblyPlan plan = null;
            if (session.TryGetAssignedRaidActor(key, out int assignedActor)
                && !session.ClaimedArmyIds.Contains(assignedActor))
            {
                RaidAssemblyPlan assigned = RaidAssemblyPlanner.PlanForArmy(snap, target, defenders, assignedActor);
                if (assigned.Feasible) plan = assigned;
            }
            if (plan == null)
                plan = RaidAssemblyPlanner.Plan(snap, target, defenders, session.ClaimedArmyIds);

            if (!plan.Feasible)
            {
                RaidAssemblyPlan unrestricted = RaidAssemblyPlanner.Plan(snap, target, defenders, null);
                if (unrestricted.Feasible)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"raid target #{target.TargetArmyId} has an executable force but its host/donor is already claimed; {plan.Reason}"));
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(plan.Reason));
            }

            ArmyData host = ResolveArmy(player, plan.BaseArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0
                || host.IsPrison || host.IsAirfield || AviationRules.IsAirArmy(host)
                || AiArmyRoles.IsSoloRecce(host) || AiArmyRoles.IsSoloHeroAwaitingEscort(host))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid host #{plan.BaseArmyId} is no longer a usable ground combat army"));
            if (host.Owner != player || session.ClaimedArmyIds.Contains(host.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"raid host #{plan.BaseArmyId} was claimed by an earlier mission this cycle"));

            var transfers = new List<RaidAssemblyTransfer>();
            var claimedDonors = new HashSet<int>();
            var projectedUnits = new List<UnitData>(host.Members);
            if (plan.NeedsAssembly)
            {
                int heroTransfers = 0;
                foreach (RaidAssemblyTransfer t in plan.Transfers)
                {
                    if (t?.Unit == null)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("raid assembly contains a null unit"));
                    ArmyData donor = ResolveArmy(player, t.DonorArmyId);
                    if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{t.DonorArmyId} is gone, moved, or would be emptied"));
                    if (session.ClaimedArmyIds.Contains(donor.Id))
                        return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                            $"raid donor #{donor.Id} was claimed by an earlier mission this cycle"));
                    bool unitIsHero = t.Unit.IsHero;
                    if (unitIsHero && (++heroTransfers > 1 || projectedUnits.Any(u => u != null && u.IsHero)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid host #{host.Id} may take at most one hero and only when heroless"));
                    if (donor.IsPrison || donor.IsAirfield || AviationRules.IsAirArmy(donor)
                        || AiArmyRoles.IsSoloRecce(donor) || !donor.Members.Contains(t.Unit)
                        || t.Unit.IsAviation)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{donor.Id} / unit {t.Unit.Name} is no longer legal"));
                    if (!donor.CanLeaveWithoutOvercrowding(t.Unit)
                        || (donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, donor, t.Unit)))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{donor.Id} can no longer spare {t.Unit.Name}"));
                    if (host.HasActivatedThisTurn && t.Unit.ActivationApCost > 0)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"adding {t.Unit.Name} to activated raid host would spend unbudgeted AP"));

                    var withU = new List<UnitData>(projectedUnits) { t.Unit };
                    if (ArmyData.ComputeCapacity(withU, host.IsGarrison) < withU.Count)
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid host #{host.Id} no longer has capacity for planned assembly"));
                    projectedUnits.Add(t.Unit);
                    transfers.Add(t);
                    claimedDonors.Add(donor.Id);
                }

                List<WorthIt.DefenderProfile> projectedProfiles = projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
                if (!Clears(projectedProfiles, defenders, out float projectedWin))
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        "planned same-hex roster no longer clears the shared WorthIt estimator"));
                plan.ProjectedWinChance = projectedWin;
            }

            if (VisitHexTask.FindNextSafeStep(ctx.Map, host, targetHex) == null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe first step from ({host.Hex.Q},{host.Hex.R}) toward raid target ({targetHex.Q},{targetHex.R})"));

            int activationAp = host.HasActivatedThisTurn ? 0 : host.ActivationApCost;
            float envelope = funded.Tentative.Ap;
            if (activationAp > envelope + eps)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activationAp,
                    $"raid host #{host.Id} needs {N(activationAp)} AP, envelope is {N(envelope)}"));
            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (activationAp > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: raid needs {N(activationAp)}, {N(turnApLeft)} left"));

            var applied = new List<RaidAssemblyTransfer>();
            foreach (RaidAssemblyTransfer t in transfers)
            {
                ArmyData donor = ResolveArmy(player, t.DonorArmyId);
                string why = donor == null ? "donor missing" : null;
                if (donor == null || !ArmyActions.TransferMember(t.Unit, donor, host, ctx.HexSelection, out why))
                {
                    bool rollbackOk = RollbackAssembly(player, host, applied, ctx);
                    AiDebugLog.Write($"[AI][V2]   raid provision [{m.AttemptId}] {key} — assembly transaction failed on "
                        + $"{t.Unit.Name} from #{t.DonorArmyId}: {why}; rollback={(rollbackOk ? "OK" : "FAILED")}");
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        rollbackOk ? $"atomic raid assembly rejected: {why}" : $"raid assembly failed and rollback was incomplete: {why}"));
                }
                applied.Add(t);
            }

            foreach (int d in claimedDonors)
                session.ClaimedArmyIds.Add(d);

            AiDebugLog.Write($"[AI][V2]   raid provision [{m.AttemptId}] {key} — OK host #{host.Id} "
                + $"{(plan.NeedsAssembly ? $"(+{transfers.Count} body from {claimedDonors.Count} donor) " : "")}" 
                + $"win~{plan.ProjectedWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"ap {N(activationAp)} -> ({targetHex.Q},{targetHex.R})");

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m,
                Key = key,
                Kind = MissionKind.Raid,
                MoverArmyId = host.Id,
                FocusHex = targetHex,
                ExecutionHex = targetHex,
                RaidTargetArmyId = target.TargetArmyId,
                RaidLastKnownHex = targetHex,
                RaidTargetIsNeutral = sighting.Value.Owner != null && sighting.Value.Owner.IsNeutral,
                ClaimedPhysical = funded.PhysicalDraw,
                ClaimedAp = activationAp,
                StealthApReserved = false,
            });
        }

        private static bool RollbackAssembly(PlayerSetupData player, ArmyData host,
            List<RaidAssemblyTransfer> applied, AiTurnContext ctx)
        {
            bool ok = true;
            for (int i = applied.Count - 1; i >= 0; i--)
            {
                RaidAssemblyTransfer t = applied[i];
                ArmyData donor = ResolveArmy(player, t.DonorArmyId);
                string why = donor == null ? "donor missing" : !host.Members.Contains(t.Unit) ? "unit no longer in host" : null;
                if (donor == null || !host.Members.Contains(t.Unit)
                    || !ArmyActions.TransferMember(t.Unit, host, donor, ctx.HexSelection, out why))
                {
                    ok = false;
                    AiDebugLog.Write($"[AI][V2]   raid assembly rollback — FAILED {t.Unit?.Name} "
                        + $"host #{host.Id}->donor #{t.DonorArmyId}: {why}");
                }
            }
            return ok;
        }

        private static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out float win)
        {
            foreach (WorthIt.DefenderProfile def in defenders)
            {
                bool covered = attackers.Any(atk => WorthIt.CanDamage(atk.Attack, def, 0f));
                if (!covered) { win = 0f; return false; }
            }
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            return win >= AiConfigV2.raidMinViableWinChance;
        }

        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static AiMapMemory.KnownEnemySighting? FindLiveSighting(PlayerSetupData player, int armyId)
        {
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == armyId) return s;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownNeutralSightings(player))
                if (s.ArmyId == armyId) return s;
            return null;
        }
    }
}
