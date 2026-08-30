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

        public static void PreparePass(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, TentativeAllocation allocation)
        {
            var open = new List<FundedEntry>();
            if (allocation?.Funded != null)
                foreach (FundedEntry fe in allocation.Funded)
                {
                    if (fe?.Mission == null || fe.Mission.Kind != MissionKind.Scout)
                        continue;
                    if (!(fe.Mission.Target is ScoutMissionTarget))
                        continue;
                    if (session.AlreadyProvisioned(StableMissionKey.For(fe.Mission)))
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
                        continue;
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, v.ExecutionHex, stealthRequired: true);
                    list.Add(new ScoutExecutionCandidate(mover, v.ExecutionHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, v.DetectionRisk, v.StandOff, pc.AlreadyHidden, pc.RequiredAp));
                    break;
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

        private static long[] ScoreAssignment(List<FundedEntry> open, List<List<ScoutExecutionCandidate>> cands, int[] chosen)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int actorDiscontinuity = 0;
            int wastedStealth = 0;
            long risk = 0, standOff = 0, requiredAp = 0, eta = 0, dist = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0)
                    continue;
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
            key[0] = -(long)covered;
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
                    $"raid target #{target.TargetArmyId} has no current honest sighting"));
            }
            if (sighting.Value.Owner != null && !sighting.Value.Owner.IsNeutral && sighting.Value.Owner.Equals(player))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"raid target #{target.TargetArmyId} is now ours"));

            HexCoord targetHex = sighting.Value.Hex;
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                sighting.Value.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();

            RaidAssemblyPlan plan = RaidAssemblyPlanner.Plan(snap, target, defenders, session.ClaimedArmyIds);
            if (!plan.Feasible)
            {
                // Critical distinction: the target/force is NOT structurally infeasible when the
                // same frozen world has a ready actor that clears the shared estimator and the only
                // reason it disappeared is a claim by an earlier mission this cycle. Re-run the
                // pure solver without same-turn claims to classify the failure. This is the Raid
                // analogue of Scout MoverContended and must never poison the target with cooldown.
                RaidAssemblyPlan unrestricted = RaidAssemblyPlanner.Plan(snap, target, defenders, null);
                if (unrestricted.Feasible)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"raid target #{target.TargetArmyId} has a ready actor (#{unrestricted.BaseArmyId}) "
                        + $"without same-turn claims, but all clearing actors are already claimed/spent; {plan.Reason}"));
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

            var transfers = new List<KeyValuePair<UnitData, ArmyData>>();
            var claimedDonors = new List<int>();
            if (plan.NeedsAssembly)
            {
                var projected = new List<UnitData>(host.Members);
                foreach (int donorId in plan.MergeArmyIds)
                {
                    ArmyData donor = ResolveArmy(player, donorId);
                    if (donor == null || !donor.Hex.Equals(host.Hex))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{donorId} is gone or no longer co-located"));
                    if (session.ClaimedArmyIds.Contains(donorId))
                        return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                            $"raid donor #{donorId} was claimed by an earlier mission this cycle"));
                    if (donor.IsPrison || donor.IsAirfield || AviationRules.IsAirArmy(donor) || AiArmyRoles.IsSoloRecce(donor))
                        return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                            $"raid donor #{donorId} is not a legal ground donor (prison / aviation / dedicated Recce)"));
                    claimedDonors.Add(donorId);
                    foreach (UnitData u in donor.Members.Where(x => !x.IsHero && !x.IsAviation).ToList())
                    {
                        var withU = new List<UnitData>(projected) { u };
                        if (ArmyData.ComputeCapacity(withU, host.IsGarrison) < withU.Count)
                            continue;
                        if (!donor.CanLeaveWithoutOvercrowding(u))
                            continue;
                        if (!AiArmyRoles.CanSpareGarrisonMember(player, donor, u))
                            continue;
                        transfers.Add(new KeyValuePair<UnitData, ArmyData>(u, donor));
                        projected.Add(u);
                    }
                }
                if (transfers.Count == 0)
                    return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                        "no legal same-hex body could be added to the raid host"));
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

            foreach (KeyValuePair<UnitData, ArmyData> t in transfers)
            {
                if (!ArmyActions.TransferMember(t.Key, t.Value, host, ctx.HexSelection, out string why))
                    AiDebugLog.Write($"[AI][V2]   raid provision {key} — WARN transfer of a body from #{t.Value.Id} failed: {why}");
            }

            foreach (int d in claimedDonors)
                session.ClaimedArmyIds.Add(d);

            AiDebugLog.Write($"[AI][V2]   raid provision {key} — OK host #{host.Id} "
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
