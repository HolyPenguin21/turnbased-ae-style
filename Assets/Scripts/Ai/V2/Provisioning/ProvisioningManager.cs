using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

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
        public EconomyMissionTarget EconomyTarget;
        public string ReservationOwner;
        public MissionIntentKey? EconomyLoanSource;
        public ResourceVector ClaimedPhysical;
        public float ClaimedAp;
        // RECON-AIR-01 — the REAL Energy this mission's bound actor needs to activate (0 for Ground,
        // which never spends Energy to activate). Folded into ClaimedPhysical.Energy so it flows
        // through the SAME generic ResourceAllocator accounting AP already uses (RegisterProvisionSuccess).
        public float ClaimedEnergy;
        public bool StealthApReserved;
        // State version after provisioning completed. In the current batch adapter several
        // provisioned missions may coexist; the future selected-only loop executes immediately
        // and can require this version to still be current before issuing a gameplay command.
        public int PlannedAtStateVersion = -1;
        // AI-RECON-02 — this Scout mission's requirement is a stealthy one (StealthRequirement.
        // Required OR a non-zero DetectionRisk). Flows Requirement -> Mission -> Intent so the
        // durable ScoutIntent knows an active lane is a stealth lane a generic scout can't cover.
        public bool RequiresStealth;
        // Round 4 — which executor this Scout mission is bound to. Ground (default) is executed by
        // ReconGroundExecutor through TaskExecutor, exactly as before. AirExisting/AirLaunch are
        // executed by ReconAirExecutor (the orchestrator routes provisioned Scout missions to the
        // right executor by this tag BEFORE calling TaskExecutor.Execute — see AiStrategyV2Pipeline).
        public ScoutExecutorKind ExecutorKind = ScoutExecutorKind.Ground;
        public HexCoord AirfieldHex;                       // AirLaunch only
        public System.Collections.Generic.List<Game.Units.UnitData> LaunchSubset; // AirLaunch only
    }

    public readonly struct ProvisionFailure
    {
        public readonly ProvisionFailureKind Kind;
        public readonly ProvisionDisposition Disposition;
        // Round 7 (Problem 3) — the GENERIC multi-resource envelope this failure reports as needed.
        // Only meaningful for EnvelopeTooSmall/RepriceThisTurn; every other constructor leaves it at
        // ProvisionRequirement.Zero. RequiredAp is kept as a read-only convenience projection for
        // existing AP-only call sites/log lines — it is never the underlying storage any more.
        public readonly ProvisionRequirement Requirement;
        public readonly string Detail;

        public float RequiredAp => Requirement.Ap;

        public ProvisionFailure(ProvisionFailureKind kind, ProvisionDisposition disposition, ProvisionRequirement requirement, string detail)
        {
            Kind = kind;
            Disposition = disposition;
            Requirement = requirement;
            Detail = detail;
        }

        public static ProvisionFailure MoverContended(string d) =>
            new ProvisionFailure(ProvisionFailureKind.MoverContended, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure NoMoverExists(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoMoverExists, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure NoObservationVantage(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoObservationVantage, ProvisionDisposition.RejectWithCooldown, ProvisionRequirement.Zero, d);
        // AP-only convenience overload — every existing caller (Ground Scout, Raid, and any future
        // Aggression/Defence/Economy/Development provisioner) that has no physical-resource shortfall
        // keeps calling this exactly as before; Physical is Zero, so the allocator's component-wise
        // max reduces to the pre-round-7 float-floor behaviour for them.
        public static ProvisionFailure EnvelopeTooSmall(float requiredAp, string d) =>
            EnvelopeTooSmall(ProvisionRequirement.ApOnly(requiredAp), d);
        public static ProvisionFailure EnvelopeTooSmall(ProvisionRequirement requirement, string d) =>
            new ProvisionFailure(ProvisionFailureKind.EnvelopeTooSmall, ProvisionDisposition.RepriceThisTurn, requirement, d);
        public static ProvisionFailure NoExecutableStep(string d) =>
            new ProvisionFailure(ProvisionFailureKind.NoExecutableStep, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure TargetSatisfied(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetSatisfied, ProvisionDisposition.DropThisTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure TargetInvalidated(string d) =>
            new ProvisionFailure(ProvisionFailureKind.TargetInvalidated, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
        public static ProvisionFailure AssemblyInfeasible(string d) =>
            new ProvisionFailure(ProvisionFailureKind.AssemblyInfeasible, ProvisionDisposition.RejectWithCooldown, ProvisionRequirement.Zero, d);
        // Air recon only — hard feasibility passed (CanAffordLaunch + funded envelope + live AP/Energy),
        // but the staged strategic reservation decision declined to protect Energy/AP for the sortie
        // this turn. No cooldown: it is re-evaluated from a fresh snapshot every turn.
        public static ProvisionFailure SortieNotWorthwhile(string d) =>
            new ProvisionFailure(ProvisionFailureKind.SortieNotWorthwhile, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, d);
    }

    public sealed class ProvisioningResult
    {
        public bool Success;
        public ProvisionedMission Provisioned;
        public ProvisionFailure Failure;
        // Provisioning is normally pure binding. Raid assembly and Economy's same-hex builder
        // lightening are the transactional exceptions: successful member transfers are versioned once only
        // after the whole transaction commits. A complete rollback reports no mutation.
        public bool StateChanged;
        public int StateVersionAfter = -1;
        public int TransferredMemberCount;

        public static ProvisioningResult Ok(ProvisionedMission m, int transferredMemberCount = 0)
        {
            bool changed = transferredMemberCount > 0;
            int version = changed ? V2StateVersion.Bump() : V2StateVersion.Current;
            if (m != null) m.PlannedAtStateVersion = version;
            return new ProvisioningResult
            {
                Success = true,
                Provisioned = m,
                StateChanged = changed,
                StateVersionAfter = version,
                TransferredMemberCount = transferredMemberCount,
            };
        }

        public static ProvisioningResult Fail(ProvisionFailure f,
            bool stateChanged = false, int transferredMemberCount = 0)
        {
            int version = stateChanged ? V2StateVersion.Bump() : V2StateVersion.Current;
            return new ProvisioningResult
            {
                Success = false,
                Failure = f,
                StateChanged = stateChanged,
                StateVersionAfter = version,
                TransferredMemberCount = transferredMemberCount,
            };
        }
    }

    public sealed class ProvisioningSession
    {
        public readonly WorldSnapshot Snapshot;
        public float ApClaimed { get; private set; }
        // RECON-AIR-03 — the cumulative real Energy every AirLaunch mission provisioned so far
        // THIS pass has claimed. Mirrors ApClaimed's role for AP: without this, two separate
        // AirLaunch missions provisioned sequentially within the same pass each check affordability
        // against the SAME unmutated root Energy stock independently, so both can pass even though
        // launching both together would exceed it (ProvisioningManager.ProvisionAir checks against
        // this before accepting a launch).
        public float EnergyClaimed { get; private set; }
        public readonly HashSet<int> ClaimedArmyIds = new HashSet<int>();

        private readonly Dictionary<StableMissionKey, ProvisionedMission> _successful =
            new Dictionary<StableMissionKey, ProvisionedMission>();
        private readonly Dictionary<StableMissionKey, ScoutExecutionCandidate> _assignment =
            new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
        // Round 3 (Problem 2) — the rejection reason ReconAssignmentPlanner.AssignFunded already
        // computed for every Scout mission that got no actor this pass. ClassifyNoAssignment below
        // is now a pure translation of this into a ProvisionFailure — it never re-derives it.
        private readonly Dictionary<StableMissionKey, ScoutAssignmentFailureReason> _assignmentRejections =
            new Dictionary<StableMissionKey, ScoutAssignmentFailureReason>();
        private readonly Dictionary<StableMissionKey, int> _raidAssignment =
            new Dictionary<StableMissionKey, int>();

        public ProvisioningSession(WorldSnapshot snapshot) { Snapshot = snapshot; }
        public IReadOnlyDictionary<StableMissionKey, ProvisionedMission> Successful => _successful;
        public bool AlreadyProvisioned(StableMissionKey k) => _successful.ContainsKey(k);

        public void RegisterSuccess(StableMissionKey k, ProvisionedMission m)
        {
            _successful[k] = m;
            ApClaimed += m.ClaimedAp;
            EnergyClaimed += m.ClaimedEnergy;
            ClaimedArmyIds.Add(m.MoverArmyId);
        }

        internal void SetAssignment(ReconAssignmentResult result)
        {
            _assignment.Clear();
            _assignmentRejections.Clear();
            if (result == null) return;
            foreach (KeyValuePair<StableMissionKey, ScoutExecutionCandidate> kv in result.Assigned)
                _assignment[kv.Key] = kv.Value;
            foreach (KeyValuePair<StableMissionKey, ScoutAssignmentFailureReason> kv in result.Rejected)
                _assignmentRejections[kv.Key] = kv.Value;
        }

        internal bool TryGetAssignedExecution(StableMissionKey k, out ScoutExecutionCandidate exec) =>
            _assignment.TryGetValue(k, out exec);

        internal bool TryGetAssignmentRejection(StableMissionKey k, out ScoutAssignmentFailureReason reason) =>
            _assignmentRejections.TryGetValue(k, out reason);

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

        // Live structural revalidation uses the same canonical army-role predicate Analysis
        // froze into ArmySnapshot.IsMobileEconomyBuilder.
        private static bool IsMobileEconomyHero(ArmyData army, PlayerSetupData player) =>
            army != null && army.Owner == player && AiArmyRoles.IsHeroLed(army);

        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation,
            ActorCommitments durableCommitments = null)
        {
            PrepareScoutAssignments(player, root, ctx, session, allocation, durableCommitments);
            PrepareRaidAssignments(session, allocation);
        }

        // Delegates the actual actor<->job matching to ReconAssignmentPlanner (the ONE canonical
        // Assignment owner, spec Level 5) — ProvisioningManager only collects the open FUNDED Scout
        // missions, hands them over, and stores the result. Only funded missions ever reach here:
        // there is no pre-funding reservation state to reconcile against any more. Round 4 — `root`
        // is now threaded through so AssignFunded can size/probe the air-actor pool (Energy/AP gates)
        // the same way ReconAirReservationPrepass already does for capacity sizing.
        private static void PrepareScoutAssignments(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation,
            ActorCommitments durableCommitments)
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

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFunded(
                session.Snapshot, ctx, player, open, session.ClaimedArmyIds, root,
                session.Successful.Values.ToList(), durableCommitments?.ClaimedArmyIdSet);
            session.SetAssignment(result);
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

            if (m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget economy)
                return ProvisionEconomy(player, root, hand, ctx, session, funded, economy);

            if (m.Kind != MissionKind.Scout || !(m.Target is ScoutMissionTarget target))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible("unsupported mission kind"));

            StableMissionKey key = StableMissionKey.For(m);
            bool surveil = target.Kind == ScoutTargetKind.Surveil;
            bool refresh = ReconScoutKinds.IsRefresh(target.Kind);

            if (!session.TryGetAssignedExecution(key, out ScoutExecutionCandidate exec))
                return ClassifyNoAssignment(session, key, target);

            if (exec.ExecutorKind != ScoutExecutorKind.Ground)
                return ProvisionAir(player, root, ctx, session, funded, exec, target, key);

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

            HexCoord? firstStep = SafeStepPathing.FindNextSafeStep(ctx.Map, army, executionHex);
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
                ClaimedPhysical = funded.PhysicalDraw,
                StealthApReserved = stealthAp > 0,
                RequiresStealth = target.Stealth == StealthRequirement.Required || target.DetectionRisk > 0f,
            });
        }

        internal static bool IsEligibleEconomyRecoveryActor(
            MissionProposal mission, ArmySnapshot actor)
        {
            if (mission == null || actor == null || mission.Kind != MissionKind.Economy
                || !(mission.Target is EconomyMissionTarget target)
                || target.Kind != EconomyTaskKind.ReturnBuilder
                || !mission.PreferredMoverArmyId.HasValue)
                return false;
            return actor.ArmyId == mission.PreferredMoverArmyId.Value
                && (!target.BuilderArmyId.HasValue
                    || actor.ArmyId == target.BuilderArmyId.Value)
                && actor.HasHero && !actor.IsPrison && !actor.IsAir && !actor.IsAirfield;
        }

        private static ProvisioningResult ProvisionEconomy(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target)
        {
            MissionProposal m = funded.Mission;
            StableMissionKey key = StableMissionKey.For(m);
            if (target.Kind == EconomyTaskKind.ReturnBuilder)
                return ProvisionEconomyRecovery(player, root, ctx, session, funded, target, key);
            if (MissionOutcomeLedger.EconomyObjectiveSatisfied(player, target))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied("economy target already built"));
            if (target.Kind == EconomyTaskKind.FoundBase
                && (target.BuildCard == null || hand?.Hand == null || !hand.Hand.Contains(target.BuildCard)))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated("founding card no longer in hand"));

            List<MissionIntent> standingIntents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            MissionIntentKey currentIntentKey = MissionIntentKey.For(m);
            ActorCommitments actorCommitments = ActorCommitments.FromIntents(
                standingIntents, session.Snapshot, null);
            IReadOnlyList<DemandLayer.EconomyBuilderChoice> rankedBuilders =
                DemandLayer.RankEconomyBuilders(session.Snapshot, target.TargetHex,
                    target.BuilderRoutes, standingIntents, actorCommitments,
                    target.BuildValue, target.BuildApCost,
                    includeReturn: target.Kind == EconomyTaskKind.BuildExtraction);
            IEnumerable<DemandLayer.EconomyBuilderChoice> eligibleBuilders = rankedBuilders;
            if (m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
                eligibleBuilders = eligibleBuilders.Where(
                    x => x.Route.ArmyId == m.PreferredMoverArmyId.Value);
            ArmyData hero = eligibleBuilders
                .OrderBy(x => m.PreferredMoverArmyId == x.Route.ArmyId ? 0 : 1)
                .Select(x => ResolveArmy(player, x.Route.ArmyId))
                .FirstOrDefault(a => a != null && IsMobileEconomyHero(a, player)
                    && !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex)
                    && !session.ClaimedArmyIds.Contains(a.Id)
                    && !standingIntents.Any(i => i.PreferredMoverArmyId == a.Id
                        && !i.IntentKey.Equals(currentIntentKey)
                        && !DemandLayer.EconomyDonorStructurallyEligible(i))
                    && (a.Hex.Equals(target.TargetHex)
                        || (a.CurrentMovement > 0
                            && SafeStepPathing.FindNextSafeStep(
                                ctx.Map, a, target.TargetHex).HasValue)));
            if (hero == null)
            {
                if (m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"committed economy builder #{m.PreferredMoverArmyId.Value} cannot advance this turn"));
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "no free hero can advance toward economy site"));
            }

            MissionIntent donor = standingIntents.FirstOrDefault(i => i != null
                && i.Kind != MissionKind.Economy && i.PreferredMoverArmyId == hero.Id
                && DemandLayer.EconomyDonorStructurallyEligible(i));
            int distance = SafeStepPathing.FindSafePathCost(ctx.Map, hero, target.TargetHex);
            if (distance == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep("no safe economy route"));
            if (donor != null)
            {
                if (!DemandLayer.EconomyLoanAllowed(donor, target.BuildValue, distance,
                        hero.CurrentMovement, out float loanNet))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }

            bool travelNeeded = !hero.Hex.Equals(target.TargetHex);
            bool completionThisTurn = distance <= hero.CurrentMovement;
            ResourceCost stageCost = completionThisTurn ? target.BuildResourceCost : null;
            List<UnitData> lighteningPlan = PlanEconomyArmyLightening(
                player, hero, target.TargetHex, session.Snapshot, ctx, out ArmyData garrison);
            float realAp = EconomyMissionClaimedAp(hero, target.BuildApCost,
                target.MinimumFollowupAp, lighteningPlan, travelNeeded, completionThisTurn);
            if (realAp > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(realAp,
                    $"economy hero #{hero.Id} needs {realAp:0.##} AP for "
                    + (completionThisTurn ? "delivery + completion" : "this travel stage")));
            if (realAp > root.ActionPoints - session.ApClaimed + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended("economy AP no longer available"));

            string owner = EconomyMissionPlanner.OwnerKey(key);
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx,
                    stageCost, owner))
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, CostVector(stageCost)),
                    "economy completion resources no longer spendable"));
            int unloadedMembers = ApplyEconomyArmyLightening(
                hero, garrison, lighteningPlan, ctx);
            // Atomic transfer failure leaves the original roster intact, so claim its real live AP
            // rather than the projected lighter value.
            realAp = EconomyMissionClaimedAp(hero, target.BuildApCost,
                target.MinimumFollowupAp, null, travelNeeded, completionThisTurn);
            if (realAp > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(realAp,
                    "economy army could not be lightened within the funded AP envelope"));
            if (completionThisTurn)
                InfrastructureFulfillment.ReserveEconomyCost(player, ctx.TurnNumber, owner,
                    target.BuildResourceCost, target.BuildApCost);

            MissionIntent loan = donor;
            if (loan != null)
            {
                loan.Status = IntentStatus.Suspended;
                loan.Suspended = SuspendReason.EconomyLoan;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] borrow actor=#{hero.Id} from={loan.IntentKey} to={key}");
            }

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = hero.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                ClaimedAp = realAp, ClaimedPhysical = CostVector(stageCost),
                ReservationOwner = owner,
                EconomyLoanSource = loan?.IntentKey,
            }, unloadedMembers);
        }

        // Economy-specific, same-hex preparation belongs here because the target and its route are
        // already selected. The canonical atomic batch transfer guarantees an all-or-nothing
        // roster change; a failed preflight simply leaves the original army usable.
        internal static int TryLightenEconomyArmy(PlayerSetupData player, ArmyData builder,
            HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx)
        {
            List<UnitData> plan = PlanEconomyArmyLightening(
                player, builder, target, snapshot, ctx, out ArmyData garrison);
            return ApplyEconomyArmyLightening(builder, garrison, plan, ctx);
        }

        private static List<UnitData> PlanEconomyArmyLightening(PlayerSetupData player,
            ArmyData builder, HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx,
            out ArmyData garrison)
        {
            garrison = null;
            if (player == null || builder == null || ctx == null || builder.IsGarrison
                || builder.IsPrison || builder.IsAirfield || builder.IsAirArmy
                || builder.Hex.Equals(target) || !builder.Members.Any(u => u != null && u.IsHero))
                return new List<UnitData>();
            // A loan may temporarily redirect an Explore/early-Raid actor, but it must not also
            // rewrite that durable mission's roster behind Continuity's back. Hard Defence,
            // Surveil and started Raid are already excluded at assignment; this preserves the
            // remaining loanable obligations as well.
            if (MissionIntentRegistry.GetOrCreate(player).All.Any(i => i != null
                && i.Status == IntentStatus.Active && i.Kind != MissionKind.Economy
                && i.PreferredMoverArmyId == builder.Id))
                return new List<UnitData>();
            BuildingData home = BuildingRegistry.FindAt(builder.Hex);
            bool isCitadel = player.CitadelHexQ == builder.Hex.Q
                && player.CitadelHexR == builder.Hex.R;
            if ((home == null || home.Owner != player || (!home.IsBase && !isCitadel)))
                return new List<UnitData>();
            garrison = ArmyRegistry.FindGarrisonAt(builder.Hex, player);
            if (garrison == null || garrison == builder || garrison.HasActivatedThisTurn)
                return new List<UnitData>();

            List<UnitData> bodies = builder.Members
                .Where(u => u != null && !u.IsHero && !u.IsAviation)
                .OrderByDescending(u => AiPower.ToPowerUnit(u).BasePower)
                .ThenBy(u => u.Name).ToList();
            IReadOnlyList<UnitData> retained = SelectEconomyEscort(
                builder, bodies, EconomyRouteThreats(snapshot, builder.Hex, target));
            // Null means a corridor contact exists but the remembered roster is too incomplete
            // for WorthIt's skill-aware coverage/win evaluation. Conservatively keep everything.
            if (retained == null)
                return new List<UnitData>();
            var keep = new HashSet<UnitData>(retained);

            List<UnitData> extras = bodies.Where(u => !keep.Contains(u))
                .OrderByDescending(u => u.ActivationApCost)
                .ThenBy(u => AiPower.ToPowerUnit(u).BasePower)
                .ThenBy(u => u.Name).ToList();
            if (extras.Count == 0)
                return extras;
            while (extras.Count > 0
                   && !ArmyActions.CanTransferMembers(extras, builder, garrison, out _))
                extras.RemoveAt(extras.Count - 1);
            return extras;
        }

        private static int ApplyEconomyArmyLightening(ArmyData builder, ArmyData garrison,
            IReadOnlyList<UnitData> extras, AiTurnContext ctx)
        {
            if (builder == null || garrison == null || extras == null || extras.Count == 0)
                return 0;
            if (!ArmyActions.TransferMembersAtomic(
                    extras, builder, garrison, ctx.HexSelection, out string why))
            {
                if (!string.IsNullOrEmpty(why))
                    AiDebugLog.WriteVerbose($"[AI][V2][Economy] builder lighten skipped: {why}");
                return 0;
            }
            AiDebugLog.Write($"[AI][V2][Economy] builder #{builder.Id} left {extras.Count} escort(s) "
                + $"in garrison #{garrison.Id}; retainedPower={AiPower.EffectiveArmyPower(builder.Members):0.##}");
            return extras.Count;
        }

        private static IReadOnlyList<AiMapMemory.KnownEnemySighting> EconomyRouteThreats(
            WorldSnapshot snapshot,
            HexCoord from, HexCoord target)
        {
            int direct = HexGridMath.Distance(from, target);
            return (snapshot?.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(enemy => HexGridMath.Distance(from, enemy.Hex)
                    + HexGridMath.Distance(enemy.Hex, target)
                    <= direct + 2)
                .ToList();
        }

        // Composition selection only; combat truth remains WorthIt (coverage + full-roster Monte
        // Carlo) and tie quality remains AiPower. The smallest safe body count wins. If the memory
        // lacks per-unit profiles, null deliberately refuses lightening rather than inventing a
        // third Economy strength surrogate.
        internal static IReadOnlyList<UnitData> SelectEconomyEscort(ArmyData builder,
            IReadOnlyList<UnitData> bodies,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats)
        {
            if (builder == null)
                return null;
            if (threats == null || threats.Count == 0)
                return System.Array.Empty<UnitData>();
            if (threats.Any(t => t.Defenders == null || t.Defenders.Count == 0))
                return null;

            List<UnitData> pool = (bodies ?? System.Array.Empty<UnitData>())
                .Where(u => u != null).Distinct().ToList();
            for (int count = 0; count <= pool.Count; count++)
            {
                List<UnitData> best = null;
                float bestPower = float.MinValue;
                foreach (List<UnitData> subset in Combinations(pool, count))
                {
                    // Heroes travel with the builder but do not participate in ground combat;
                    // WorthIt's ArmyData overloads apply the same non-hero boundary.
                    var roster = subset.Select(WorthIt.FromLiveUnit).ToList();
                    bool safe = threats.All(t => WorthIt.CanDamageAll(roster, t.Defenders)
                        && WorthIt.WinChance(roster, t.Defenders, 0f)
                            >= AiConfig.defenceActiveWinChance);
                    if (!safe)
                        continue;
                    float power = AiPower.EffectiveArmyPower(subset);
                    if (best == null || power > bestPower + AiConfigV2.allocatorSliceEpsilon)
                    {
                        best = subset;
                        bestPower = power;
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
        }

        private static IEnumerable<List<UnitData>> Combinations(
            IReadOnlyList<UnitData> source, int count, int start = 0,
            List<UnitData> prefix = null)
        {
            prefix ??= new List<UnitData>();
            if (prefix.Count == count)
            {
                yield return new List<UnitData>(prefix);
                yield break;
            }
            for (int i = start; i <= source.Count - (count - prefix.Count); i++)
            {
                prefix.Add(source[i]);
                foreach (List<UnitData> result in Combinations(source, count, i + 1, prefix))
                    yield return result;
                prefix.RemoveAt(prefix.Count - 1);
            }
        }

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded) =>
            EconomyMissionClaimedAp(builder, buildApCost, minimumFollowupAp, unloaded,
                travelNeeded: true, completionThisTurn: true);

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded,
            bool travelNeeded, bool completionThisTurn)
        {
            float activation = 0f;
            if (travelNeeded && builder != null && !builder.HasActivatedThisTurn)
                activation = builder.Members
                    .Where(u => u != null && (unloaded == null || !unloaded.Contains(u)))
                    .Sum(u => u.ActivationApCost);
            float completion = completionThisTurn
                ? Mathf.Max(buildApCost, minimumFollowupAp) : 0f;
            return activation + completion;
        }

        private static ProvisioningResult ProvisionEconomyRecovery(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target, StableMissionKey key)
        {
            MissionProposal mission = funded.Mission;
            int? preferredId = mission.PreferredMoverArmyId ?? target.BuilderArmyId;
            if (!preferredId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "return-builder mission has no preferred actor"));

            ArmySnapshot actorSnapshot = session.Snapshot?.Self?.Armies?
                .FirstOrDefault(a => a != null && a.ArmyId == preferredId.Value);
            if (!IsEligibleEconomyRecoveryActor(mission, actorSnapshot))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"preferred return builder #{preferredId.Value} is no longer eligible"));

            ArmyData actor = ResolveArmy(player, preferredId.Value);
            if (actor == null || actor.Owner != player
                || !actor.Members.Any(u => u != null && u.IsHero))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"preferred return builder #{preferredId.Value} no longer exists"));
            BuildingData shelter = BuildingRegistry.FindAt(target.TargetHex);
            bool isOwnCitadel = player.CitadelHexQ == target.TargetHex.Q
                && player.CitadelHexR == target.TargetHex.R;
            if (shelter == null || shelter.Owner != player
                || (!shelter.IsBase && !isOwnCitadel))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"recovery shelter ({target.TargetHex.Q},{target.TargetHex.R}) is no longer protected"));
            if (actor.Hex.Equals(target.TargetHex))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"return builder #{actor.Id} already protected"));
            if (actor.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"return builder #{actor.Id} has no movement"));
            if (!SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.TargetHex).HasValue
                || SafeStepPathing.FindSafePathCost(ctx.Map, actor, target.TargetHex) == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe recovery route for builder #{actor.Id}"));

            float activation = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
            if (activation > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activation,
                    $"return builder #{actor.Id} needs {activation:0.##} AP"));
            if (activation > root.ActionPoints - session.ApClaimed
                + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "recovery activation AP no longer available"));

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = actor.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                ClaimedAp = activation, ClaimedPhysical = ResourceVector.Zero,
                ReservationOwner = null,
            });
        }

        private static ResourceVector CostVector(ResourceCost cost) => cost == null
            ? ResourceVector.Zero
            : new ResourceVector(0f, cost.Get(ResourceType.Human), cost.Get(ResourceType.Energy),
                cost.Get(ResourceType.Materials), cost.Get(ResourceType.Tech));

        // RECON-AIR-01 (round 5) — claim the air actor/subset Assignment already picked, THROUGH
        // THE SAME generic funding/provisioning accounting Ground uses: the real AP/Energy Assignment
        // resolved for this exact actor/subset (ScoutExecutionCandidate.RequiredAp/RequiredEnergy —
        // see ReconAssignmentPlanner.AppendAirCandidates) is checked against the envelope Funding
        // granted (funded.Tentative.Ap / funded.PhysicalDraw.Energy) and, if it fits, claimed for
        // real — ClaimedAp/ClaimedEnergy are no longer hard-coded 0. If it does not fit, this returns
        // the ordinary EnvelopeTooSmall failure and lets the existing repack/reprice loop
        // (ResourceAllocator.RegisterProvisionFailure) handle it exactly like ground already does —
        // no separate air ledger. The terminal air execution stage still re-checks LIVE HARD gates
        // (AiAirSortiePlanner.CanAffordLaunch / CanIssueMoveNow / AA / safe return) against the
        // post-ground-movement world state before actually spending anything — but it no longer
        // re-runs any strategic hand/deck/income economics: that decision is made once, here, by
        // AirSortieReservationAdmission.
        private static ProvisioningResult ProvisionAir(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded, ScoutExecutionCandidate exec,
            ScoutMissionTarget target, StableMissionKey key)
        {
            MissionProposal m = funded.Mission;
            bool surveil = target.Kind == ScoutTargetKind.Surveil;
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
            }
            else
            {
                // Air only serves Refresh in this round's scope (see AppendAirCandidates).
                if (ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, focus))
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"refresh focus ({focus.Q},{focus.R}) is already visible again"));
            }

            int moverArmyId;
            HexCoord airfieldHex = default;
            List<UnitData> launchSubset = null;

            if (exec.ExecutorKind == ScoutExecutorKind.AirExisting)
            {
                ArmyData wing = ResolveArmy(player, exec.Army.ArmyId);
                if (wing == null || wing.Owner != player || !AviationRules.IsValidAirArmy(wing)
                    || wing.CurrentMovement <= 0)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned air actor #{exec.Army.ArmyId} is no longer a usable air wing"));

                // Round 8 (Problem 1) — ProvisionAir validates the SAME two air-actor states the pool
                // ReconAssignmentPlanner.AssignFunded now offers (detail.AirborneWings first, then
                // ready spares); the old code accepted only the first and rejected every continuing
                // wing an incumbent ScoutIntent had just re-won a FRESH funded mission for, so the
                // continuation architecture was wired end to end but never executable. The two states
                // are EXPLICITLY MUTUALLY EXCLUSIVE — computed once from one live-sortie lookup, not
                // an airfield check in one branch and a registry check in the other:
                //   · ReadyAirExisting      = own airfield  + NO live sortie: about to start one.
                //   · ContinuingAirExisting = airborne + a LIVE Recon sortie + a durable
                //     ReconPatrolState + a non-null projected sortie state whose phase is not
                //     Return/Hold. It is mid-sortie by definition, so the ready-idle-wing shape is not
                //     demanded of it; it is rejected only when forced into recovery this turn (that
                //     lifecycle is Mandatory Flight Recovery's — ReconAirExecutor flies it
                //     unconditionally, outside funding — never strategic Recon progress). A null
                //     projected state is NOT a silent pass: no valid live Recon sortie => reject.
                bool onOwnAirfield = AviationRules.IsOwnedAirfieldAt(wing.Hex, player);
                AirSortie liveSortie = AirSortieRegistry.ForArmy(player, wing);
                bool hasPatrolState = ReconPatrolStateRegistry.TryGet(player, wing.Id, out _);

                bool ready = onOwnAirfield && liveSortie == null;
                bool continuing = !onOwnAirfield
                    && wing.Controller != null
                    && liveSortie != null
                    && liveSortie.Kind == AirSortieKind.Recon
                    && hasPatrolState;

                if (continuing)
                {
                    ReconAirSortieState projected = ReconAirReservationPrepass.ProjectScoringSortie(player, ctx, wing);
                    if (projected == null)
                        return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                            $"continuing air actor #{wing.Id} has no valid live Recon sortie state"));
                    if (projected.Phase == ReconAirPhase.Return || projected.Phase == ReconAirPhase.Hold)
                        return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                            $"continuing air actor #{wing.Id} is Return/Hold-bound this turn (recovery, not fresh Recon progress)"));
                }
                else if (!ready)
                {
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned air actor #{wing.Id} is neither a ready standalone wing nor a valid continuing Recon sortie"));
                }
                moverArmyId = wing.Id;
            }
            else // AirLaunch
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(exec.AirfieldHex, player);
                if (airfield == null || exec.LaunchSubset == null || exec.LaunchSubset.Count == 0
                    || !AiAirSortiePlanner.CanAffordLaunch(root, player, exec.LaunchSubset))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"assigned launch airfield ({exec.AirfieldHex.Q},{exec.AirfieldHex.R}) no longer has an affordable subset"));
                moverArmyId = exec.ActorKey;
                airfieldHex = exec.AirfieldHex;
                launchSubset = new List<UnitData>(exec.LaunchSubset);
            }

            // RECON-AIR-01 — the real, actor-specific cost Assignment already resolved for THIS
            // exact candidate (see AppendAirCandidates: a live Pick/PickFromStorage against the
            // bound mission target, not a generic "some useful step exists" probe). Compare against
            // the envelope Funding granted; claim for real only if it fits.
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float realAp = exec.RequiredAp;
            float realEnergy = exec.RequiredEnergy;
            float apEnvelope = funded.Tentative.Ap;
            float energyEnvelope = funded.PhysicalDraw.Energy;
            if (realAp > apEnvelope + eps || realEnergy > energyEnvelope + eps)
                // Round 7 (Problem 3) — report BOTH the real AP and the real Energy this exact air
                // actor needs, not AP alone: an air launch's Energy shortfall must raise an Energy
                // floor too, so ResourceAllocator's next Pack() can actually fund it, instead of
                // repricing only the AP dimension and looping on the same Energy-starved envelope.
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, new ResourceVector(0f, 0f, realEnergy, 0f, 0f)),
                    $"air actor #{moverArmyId} needs {N(realAp)} AP / {N(realEnergy)} Energy, "
                    + $"envelope is {N(apEnvelope)} AP / {N(energyEnvelope)} Energy"));

            float turnApLeft = root.ActionPoints - session.ApClaimed;
            if (realAp > turnApLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn AP exhausted: air actor #{moverArmyId} needs {N(realAp)}, {N(turnApLeft)} left after earlier claims"));

            // RECON-AIR-03 — the cumulative, SEQUENTIAL check the funded-envelope comparison above
            // cannot provide on its own: two separate AirLaunch (or AirExisting) missions provisioned
            // one after another THIS pass both see the SAME unmutated root.Energy (Provisioning never
            // mutates world resources — only Execution does), so each could pass its OWN envelope
            // check independently while jointly exceeding the real stockpile. session.EnergyClaimed
            // accumulates every earlier real claim this pass, mirroring session.ApClaimed for AP.
            float liveEnergyLeft = root.GetResource(Game.Economy.ResourceType.Energy) - session.EnergyClaimed;
            if (realEnergy > liveEnergyLeft + eps)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"turn Energy exhausted: air actor #{moverArmyId} needs {N(realEnergy)}, "
                    + $"{N(liveEnergyLeft)} left after earlier claims this pass"));

            // AI-MGR — the SINGLE strategic sortie-reservation admission. Everything above
            // (CanAffordLaunch, the funded-envelope comparison, the cumulative live AP/Energy checks)
            // is HARD feasibility: "enough resources exist right now". This is the one strategic
            // question — "is THIS exact sortie worth paying for this turn versus keeping the Energy
            // for hand/deck card pressure?" — and it is answered HERE and nowhere else. The canonical
            // staged decision (Resource Outlook -> Hand/Deck Energy Pressure -> Recon Value ->
            // Reserve) is AviationSortieReservationEvaluator, fed the exact per-actor AP/Energy and
            // the mission-specific route score Assignment already resolved (ScoutExecutionCandidate),
            // never a fresh air-route re-probe. Recomputed every turn: an idle aircraft never yields
            // a standing reservation. Tactical layers below MUST NOT re-run this economics — they are
            // limited to live hard/safety gates (CanAffordLaunch / CanIssueMoveNow / AA / safe return).
            ProvisionFailure? sortieDeclined = AirSortieReservationAdmission(
                player, root, ctx, session, exec, moverArmyId, airfieldHex, realAp, realEnergy);
            if (sortieDeclined.HasValue)
                return ProvisioningResult.Fail(sortieDeclined.Value);

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
                ClaimedAp = realAp,
                ClaimedEnergy = realEnergy,
                ClaimedPhysical = new ResourceVector(0f, 0f, realEnergy, 0f, 0f),
                StealthApReserved = false,
                RequiresStealth = false,
                ExecutorKind = exec.ExecutorKind,
                AirfieldHex = airfieldHex,
                LaunchSubset = launchSubset,
            });
        }

        // The ONE strategic admission for an air-recon sortie whose HARD feasibility already passed
        // inside ProvisionAir. Returns null to proceed with the reservation, or a SortieNotWorthwhile
        // failure (RetryNextTurn, no cooldown) when the canonical evaluator declines this turn.
        //
        // No generic air-route re-probe happens here: Assignment (ReconAssignmentPlanner.
        // AppendAirCandidates) already picked this exact actor/airfield AND proved a mission-specific
        // route — the resulting AIR-01 route score and the exact per-actor AP/Energy ride in on the
        // ScoutExecutionCandidate. This method feeds those figures, plus what earlier missions this
        // pass have already claimed (session.ApClaimed/EnergyClaimed), straight into the canonical
        // AviationSortieReservationEvaluator (Resource Outlook -> Hand/Deck Energy Pressure -> Recon
        // Value -> Reserve), which reads live hand/deck/income Energy pressure itself. No new
        // evaluator, no per-turn reservation registry — recomputed every turn.
        private static ProvisionFailure? AirSortieReservationAdmission(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, ProvisioningSession session,
            ScoutExecutionCandidate exec, int moverArmyId, HexCoord airfieldHex, float realAp, float realEnergy)
        {
            // Bare harness / no world to reason about — hard gates already passed, leave behaviour unchanged.
            if (ctx?.Map == null || session?.Snapshot == null)
                return null;

            bool existing = exec.ExecutorKind == ScoutExecutorKind.AirExisting;
            string label = existing ? $"actor=#{moverArmyId}" : $"airfield=({airfieldHex.Q},{airfieldHex.R})";

            AviationReservationDecision decision = AviationSortieReservationEvaluator.EvaluateRecon(
                player, root, ctx.Map,
                Mathf.CeilToInt(Mathf.Max(0f, realAp)),
                Mathf.CeilToInt(Mathf.Max(0f, realEnergy)),
                exec.RouteScore,
                existing ? moverArmyId : -1,
                Mathf.CeilToInt(Mathf.Max(0f, session.ApClaimed)),
                Mathf.CeilToInt(Mathf.Max(0f, session.EnergyClaimed)),
                // Actors already in session.EnergyClaimed (a continuing wing provisioned earlier
                // this pass) — the evaluator's live scan must not re-count their owed Energy.
                session.ClaimedArmyIds);
            AiDebugLog.Write(decision.ToLog(label));

            return decision.ShouldReserve
                ? (ProvisionFailure?)null
                : ProvisionFailure.SortieNotWorthwhile(
                    $"air actor #{moverArmyId}: sortie not worth reserving this turn ({decision.Reason})");
        }

        // Round 3 (Problem 2) — PURE translation of ReconAssignmentPlanner.AssignFunded's already-
        // computed rejection reason into a ProvisionFailure. No eligibility / route / vantage
        // re-derivation happens here any more — "why couldn't this job be assigned" has exactly ONE
        // owner, ReconAssignmentPlanner, and this is just its vocabulary mapped onto Provisioning's.
        private static ProvisioningResult ClassifyNoAssignment(ProvisioningSession session,
            StableMissionKey key, ScoutMissionTarget target)
        {
            bool needStealth = target.Stealth == StealthRequirement.Required;
            if (!session.TryGetAssignmentRejection(key, out ScoutAssignmentFailureReason reason))
                // Should not happen — AssignFunded rejects or accepts every mission it is handed.
                // Fail safe rather than re-deriving anything ourselves.
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"{key} had no assignment result this pass (assignment/provisioning desync)"));

            switch (reason)
            {
                case ScoutAssignmentFailureReason.NoMoverExists:
                    return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                        "no solo Recce" + (needStealth ? " with stealth capability" : "") + " on the map"));
                case ScoutAssignmentFailureReason.NoObservationVantage:
                    return ProvisioningResult.Fail(ProvisionFailure.NoObservationVantage(
                        $"no on-map vantage within any scout's vision of ({target.FocusHex.Q},{target.FocusHex.R})"));
                case ScoutAssignmentFailureReason.NoExecutableStep:
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        $"eligible scout(s)/vantage exist but none reachable this turn toward "
                        + $"({target.FocusHex.Q},{target.FocusHex.R})"));
                default:
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "a capable solo Recce exists but is spent / activated / claimed this cycle"));
            }
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

            if (SafeStepPathing.FindNextSafeStep(ctx.Map, host, targetHex) == null)
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
                    int transfersStillApplied = applied.Count(x => x?.Unit != null && host.Members.Contains(x.Unit));
                    bool rollbackChangedWorld = transfersStillApplied > 0;
                    AiDebugLog.Write($"[AI][V2]   raid provision [{m.AttemptId}] {key} — assembly transaction failed on "
                        + $"{t.Unit.Name} from #{t.DonorArmyId}: {why}; rollback={(rollbackOk ? "OK" : "FAILED")}; "
                        + $"remainingTransfers={transfersStillApplied}");
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            rollbackOk ? $"atomic raid assembly rejected: {why}" : $"raid assembly failed and rollback was incomplete: {why}"),
                        rollbackChangedWorld, transfersStillApplied);
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
            }, applied.Count);
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
            if (!WorthIt.CanDamageAll(attackers, defenders, 0f))
            {
                win = 0f;
                return false;
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
