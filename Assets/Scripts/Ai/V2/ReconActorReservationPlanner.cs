using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-RECON-01 — ACTOR-AWARE RECON PLANNING & RESERVATION  (ground scout lane)
    // ===========================================================================================
    //  Actor availability is part of PLANNING, before funding, with a real release / rematch
    //  lifecycle:
    //
    //     job  --Match-->  actor reservation  --(allocator)-->  budget reservation  -->  funding
    //
    //  On any later miss the job is BOTH released (actor + concurrency slot returned) AND marked
    //  with why it failed, so a rematch in the same re-pack loop hands the freed actor to a job
    //  that CAN still be admitted instead of re-reserving the same infeasible / rejected one:
    //
    //     budget defer            -> Release + Block(BudgetInfeasibleThisTurn)
    //     lane full / conflict    -> Release + Block(RejectedThisTurn)
    //     provisioning invalidated-> Release + Block(RejectedThisTurn)   (allocator also rejected it)
    //
    //  A blocked job stays as an unreserved proposal (allocator DeferReason.ReconActorUnreserved);
    //  it is never deleted and continuity keeps its intent.
    //
    //  ROOM is tracked per requirement class AND per stealth tier, with a shared global ceiling:
    //     GenericObservation / GenericGroundTraversal   (aviation covers Observation only)
    //     StealthObservation / StealthGroundTraversal   (aviation covers neither; DemandLayer's
    //                                                    dedicated stealth path sizes these)
    //     GlobalGroundActor = ReconConcurrencyPolicy.HardCap - active executions
    //  so a fresh stealth Surveil is never starved by a zero GENERIC observation room, and the
    //  planner never reserves more ground scouts than the execution lane can ever run.
    // ===========================================================================================

    internal enum ReconJobBlock { None, BudgetInfeasibleThisTurn, RejectedThisTurn }

    internal sealed class ReconActorReservationContext
    {
        public readonly HashSet<int> ReservedActorIds = new HashSet<int>();
        public readonly Dictionary<MissionIntentKey, int> JobToActor = new Dictionary<MissionIntentKey, int>();
        public readonly Dictionary<int, MissionIntentKey> ActorToJob = new Dictionary<int, MissionIntentKey>();
        // Non-Recon this-turn claims (raid hosts, defence bodies): never a Recon executor.
        public readonly HashSet<int> HardExcluded = new HashSet<int>();
        // Why a job may not re-acquire an actor this turn.
        public readonly Dictionary<MissionIntentKey, ReconJobBlock> JobBlock =
            new Dictionary<MissionIntentKey, ReconJobBlock>();

        public int RemainingGenericObservationRoom;
        public int RemainingGenericGroundRoom;
        public int RemainingStealthObservationRoom;
        public int RemainingStealthGroundRoom;
        public int RemainingGlobalGroundActorRoom;

        public bool IsReservedForAnotherJob(int actorId, MissionIntentKey job) =>
            ActorToJob.TryGetValue(actorId, out MissionIntentKey owner) && !owner.Equals(job);

        public ReconJobBlock BlockOf(MissionIntentKey job) =>
            JobBlock.TryGetValue(job, out ReconJobBlock b) ? b : ReconJobBlock.None;

        public void Block(MissionIntentKey job, ReconJobBlock b)
        {
            if (b != ReconJobBlock.None)
                JobBlock[job] = b;
        }

        // countsRoom: a FRESH lane consumes both its class room and the global ground-actor ceiling.
        // A durable incumbent is already active and consumes neither.
        public void Reserve(MissionIntentKey job, int actorId, bool stealth, bool ground, bool countsRoom)
        {
            ReservedActorIds.Add(actorId);
            JobToActor[job] = actorId;
            ActorToJob[actorId] = job;
            if (!countsRoom)
                return;
            RemainingGlobalGroundActorRoom = Mathf.Max(0, RemainingGlobalGroundActorRoom - 1);
            AdjustClassRoom(stealth, ground, -1);
        }

        public void Release(MissionIntentKey job, bool stealth, bool ground, bool refundRoom)
        {
            if (!JobToActor.TryGetValue(job, out int actorId))
                return;
            JobToActor.Remove(job);
            ActorToJob.Remove(actorId);
            ReservedActorIds.Remove(actorId);
            if (!refundRoom)
                return;
            RemainingGlobalGroundActorRoom++;
            AdjustClassRoom(stealth, ground, +1);
        }

        public int ClassRoom(bool stealth, bool ground) =>
            stealth
                ? (ground ? RemainingStealthGroundRoom : RemainingStealthObservationRoom)
                : (ground ? RemainingGenericGroundRoom : RemainingGenericObservationRoom);

        private void AdjustClassRoom(bool stealth, bool ground, int delta)
        {
            if (stealth && ground) RemainingStealthGroundRoom = Mathf.Max(0, RemainingStealthGroundRoom + delta);
            else if (stealth) RemainingStealthObservationRoom = Mathf.Max(0, RemainingStealthObservationRoom + delta);
            else if (ground) RemainingGenericGroundRoom = Mathf.Max(0, RemainingGenericGroundRoom + delta);
            else RemainingGenericObservationRoom = Mathf.Max(0, RemainingGenericObservationRoom + delta);
        }
    }

    internal static class ReconActorReservationPlanner
    {
        private static bool IsGround(MissionProposal m) =>
            m.Target is ScoutMissionTarget t && t.Kind == ScoutTargetKind.Explore;

        private static bool IsStealthJob(MissionProposal m) =>
            m.Target is ScoutMissionTarget t
            && (t.Stealth == StealthRequirement.Required || t.DetectionRisk > 0f);

        private static bool IsStealthObjective(ReconObjective o) =>
            o != null && (o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);

        // ---- First pass ---------------------------------------------------------------------------
        public static void Plan(ReconActorReservationContext ctxRes, WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, List<MissionProposal> missions, ActorCommitments actorCommitments,
            IReadOnlyList<MissionIntent> activeIntents, IReadOnlyList<ReconObjective> frozenObjectives)
        {
            if (ctxRes == null || missions == null || missions.Count == 0 || snap?.Self?.Armies == null)
                return;

            // 1. Recon proposals only, deduplicated by ReconJobKey (== MissionIntentKey).
            var scoutMissions = new List<MissionProposal>();
            var seenJobs = new HashSet<MissionIntentKey>();
            var dedupDrop = new List<MissionProposal>();
            foreach (MissionProposal m in missions)
            {
                if (m == null || m.Kind != MissionKind.Scout || !(m.Target is ScoutMissionTarget))
                    continue;
                MissionIntentKey job = MissionIntentKey.For(m);
                if (!seenJobs.Add(job))
                {
                    AiDebugLog.Write($"[AI][V2][ReconActor] dedup — dropped duplicate recon job {job}");
                    dedupDrop.Add(m);
                    continue;
                }
                scoutMissions.Add(m);
            }
            foreach (MissionProposal m in dedupDrop)
                missions.Remove(m);
            if (scoutMissions.Count == 0)
                return;

            // 2. Seed the reservation context. A durable Recon intent's own claimed mover is bound to
            //    ITS OWN job (not blanket-excluded) so that incumbent can still be matched to it.
            var reconIntentActorByJob = new Dictionary<MissionIntentKey, int>();
            var activeStealthObsActors = new HashSet<int>();
            var activeStealthGroundActors = new HashSet<int>();
            if (activeIntents != null && actorCommitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i?.Scout == null || i.PreferredMoverArmyId == null
                        || !actorCommitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        continue;
                    int id = i.PreferredMoverArmyId.Value;
                    reconIntentActorByJob[i.IntentKey] = id;
                    if (i.Scout.RequiresStealth)
                    {
                        if (i.Scout.Kind == ScoutTargetKind.Explore) activeStealthGroundActors.Add(id);
                        else activeStealthObsActors.Add(id);
                    }
                }

            var scoutJobKeys = new HashSet<MissionIntentKey>(scoutMissions.Select(MissionIntentKey.For));
            foreach (KeyValuePair<MissionIntentKey, int> kv in reconIntentActorByJob)
            {
                if (scoutJobKeys.Contains(kv.Key))
                    ctxRes.Reserve(kv.Key, kv.Value, stealth: false, ground: false, countsRoom: false);
                else
                    ctxRes.HardExcluded.Add(kv.Value);
            }
            if (actorCommitments != null)
                foreach (int claimed in actorCommitments.ClaimedArmyIds)
                    if (!ctxRes.ActorToJob.ContainsKey(claimed))
                        ctxRes.HardExcluded.Add(claimed);

            int activeReconExecutions = reconIntentActorByJob.Values.Distinct().Count();

            // 3. Room. Generic per-class from the unified capacity model (post-Phase-A snapshot);
            //    stealth per-class sized directly from the stealth-filtered runnable objectives
            //    (aviation never helps a stealth lane); global ceiling from ReconConcurrencyPolicy.
            var obsRunnable = FilterObjectives(frozenObjectives, ground: false, stealth: null);
            var groundRunnable = FilterObjectives(frozenObjectives, ground: true, stealth: null);
            ReconCapacitySnapshot cap = ReconCapacitySnapshot.Build(snap, obsRunnable, groundRunnable,
                activeIntents, actorCommitments, player,
                ReconAirReservationRegistry.ForTurn(player, snap.TurnNumber));

            int airObs = cap.AirborneReconLanes + cap.SpareAirObservationSorties;
            ctxRes.RemainingGenericObservationRoom = Mathf.Max(0,
                cap.DesiredObservationConcurrency - cap.GenericObservationLaneActors.Count - airObs);
            ctxRes.RemainingGenericGroundRoom = Mathf.Max(0,
                cap.DesiredGroundTraversalConcurrency - cap.GenericGroundLaneActors.Count);

            var stealthObs = FilterObjectives(frozenObjectives, ground: false, stealth: true);
            var stealthGround = FilterObjectives(frozenObjectives, ground: true, stealth: true);
            ctxRes.RemainingStealthObservationRoom = Mathf.Max(0,
                ReconConcurrencyPolicy.DesiredForClass(snap, stealthObs,
                    ReconConcurrencyPolicy.ReconCoverageClass.Observation)
                - activeStealthObsActors.Count);
            ctxRes.RemainingStealthGroundRoom = Mathf.Max(0,
                ReconConcurrencyPolicy.DesiredForClass(snap, stealthGround,
                    ReconConcurrencyPolicy.ReconCoverageClass.GroundTraversal)
                - activeStealthGroundActors.Count);

            ctxRes.RemainingGlobalGroundActorRoom = Mathf.Max(0,
                ReconConcurrencyPolicy.HardCap - activeReconExecutions);

            AssignPass(ctxRes, snap, ctx, player, scoutMissions, "plan");

            AiDebugLog.Write($"[AI][V2][ReconActor] plan — scoutJobs={scoutMissions.Count} "
                + $"activeExec={activeReconExecutions} room[genObs={ctxRes.RemainingGenericObservationRoom} "
                + $"genGround={ctxRes.RemainingGenericGroundRoom} stObs={ctxRes.RemainingStealthObservationRoom} "
                + $"stGround={ctxRes.RemainingStealthGroundRoom} global={ctxRes.RemainingGlobalGroundActorRoom}] "
                + $"reserved={ctxRes.JobToActor.Count} dedupDropped={dedupDrop.Count} capacity[{cap.Explain}]");
        }

        // ---- Re-pack pass: release + block + rematch ------------------------------------------
        public static bool Rematch(ReconActorReservationContext ctxRes, WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, List<MissionProposal> missions, IReadOnlyList<DeferredEntry> deferred)
        {
            if (ctxRes == null || missions == null)
                return false;

            var scoutMissions = missions.Where(m => m != null && m.Kind == MissionKind.Scout
                && m.Target is ScoutMissionTarget).ToList();
            if (scoutMissions.Count == 0)
                return false;

            bool changed = false;
            if (deferred != null)
                foreach (DeferredEntry d in deferred)
                {
                    if (d?.Mission == null || d.Mission.Kind != MissionKind.Scout
                        || d.Mission.ReservedMoverArmyId == null)
                        continue;

                    ReconJobBlock block;
                    if (d.Reason == DeferReason.InsufficientBudget || d.Reason == DeferReason.InsufficientPhysical)
                        block = ReconJobBlock.BudgetInfeasibleThisTurn;
                    else if (d.Reason == DeferReason.ExecutionCapacity || d.Reason == DeferReason.MissionConflict)
                        block = ReconJobBlock.RejectedThisTurn;
                    else
                        continue;   // ReconActorUnreserved / RejectedThisTurn / OnCooldown — nothing new to free

                    MissionIntentKey job = MissionIntentKey.For(d.Mission);
                    AiDebugLog.Write($"[AI][V2][ReconActor] rematch release {StableMissionKey.For(d.Mission)} "
                        + $"— deferred {d.Reason}, freeing #{d.Mission.ReservedMoverArmyId}, block={block}");
                    ctxRes.Release(job, IsStealthJob(d.Mission), IsGround(d.Mission),
                        refundRoom: !d.Mission.FromDurableIntent);
                    ctxRes.Block(job, block);
                    d.Mission.ReservedMoverArmyId = null;
                    changed = true;
                }

            if (AssignPass(ctxRes, snap, ctx, player, scoutMissions, "rematch"))
                changed = true;
            return changed;
        }

        // A Scout provisioning miss. The allocator has already put the mission in _rejectedThisTurn,
        // so it will not be re-funded this turn — free its actor AND block re-acquisition so a
        // rematch cannot pin a scout to a dead job.
        public static void ReleaseForProvisionFailure(ReconActorReservationContext ctxRes, MissionProposal mission)
        {
            if (ctxRes == null || mission == null || mission.Kind != MissionKind.Scout)
                return;
            MissionIntentKey job = MissionIntentKey.For(mission);
            if (mission.ReservedMoverArmyId != null || ctxRes.JobToActor.ContainsKey(job))
            {
                ctxRes.Release(job, IsStealthJob(mission), IsGround(mission),
                    refundRoom: !mission.FromDurableIntent);
                AiDebugLog.Write($"[AI][V2][ReconActor] release {StableMissionKey.For(mission)} "
                    + "— provisioning miss, actor returned + job blocked this turn");
            }
            ctxRes.Block(job, ReconJobBlock.RejectedThisTurn);
            mission.ReservedMoverArmyId = null;
        }

        // ---- Shared assignment ----------------------------------------------------------------
        private static bool AssignPass(ReconActorReservationContext ctxRes, WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, List<MissionProposal> scoutMissions, string phase)
        {
            var eligible = new Dictionary<MissionProposal, List<ScoutMoverCandidate>>();
            foreach (MissionProposal m in scoutMissions)
            {
                MissionIntentKey job = MissionIntentKey.For(m);
                var exclude = new HashSet<int>(ctxRes.HardExcluded);
                foreach (int rid in ctxRes.ReservedActorIds)
                    if (ctxRes.IsReservedForAnotherJob(rid, job))
                        exclude.Add(rid);
                var target = (ScoutMissionTarget)m.Target;
                eligible[m] = ScoutMoverSelector.Rank(snap, target, exclude)
                    .Where(c => CanExecute(ctx, player, snap, c.Army, target))
                    .ToList();
            }

            scoutMissions.Sort((a, b) =>
            {
                int c = MissionAdmissionPolicy.AdmissionRank(b).CompareTo(MissionAdmissionPolicy.AdmissionRank(a));
                if (c != 0) return c;
                c = eligible[a].Count.CompareTo(eligible[b].Count);
                if (c != 0) return c;
                return StableMissionKey.For(a).CompareTo(StableMissionKey.For(b));
            });

            bool bound = false;
            foreach (MissionProposal m in scoutMissions)
            {
                MissionIntentKey job = MissionIntentKey.For(m);
                bool incumbent = m.FromDurableIntent;
                bool ground = IsGround(m);
                bool stealth = IsStealthJob(m);

                // A job blocked this turn (budget-infeasible / rejected) must not re-acquire an
                // actor — that is the loop the previous review found.
                if (ctxRes.BlockOf(job) != ReconJobBlock.None)
                {
                    m.ReservedMoverArmyId = null;
                    continue;
                }

                // Confirm an already-held actor (seeded incumbent, or a prior AssignPass).
                if (ctxRes.JobToActor.TryGetValue(job, out int held))
                {
                    if (eligible[m].Any(c => c.Army.ArmyId == held))
                    {
                        if (m.ReservedMoverArmyId != held)
                        {
                            m.ReservedMoverArmyId = held;
                            m.PreferredMoverArmyId = held;
                            RepriceForActor(snap, m, Snapshot(snap, held));
                            bound = true;
                        }
                        continue;
                    }
                    ctxRes.Release(job, stealth, ground, refundRoom: !incumbent);
                    m.ReservedMoverArmyId = null;
                    AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — held #{held} for {StableMissionKey.For(m)} "
                        + "no longer eligible; released");
                }

                if (m.ReservedMoverArmyId != null)
                    continue;

                if (!incumbent
                    && (ctxRes.ClassRoom(stealth, ground) <= 0 || ctxRes.RemainingGlobalGroundActorRoom <= 0))
                {
                    AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — {StableMissionKey.For(m)} left unreserved "
                        + $"(no {(stealth ? "stealth-" : "")}{(ground ? "ground-traversal" : "observation")} room / "
                        + $"global {ctxRes.RemainingGlobalGroundActorRoom})");
                    continue;
                }

                ScoutMoverCandidate? pick = null;
                foreach (ScoutMoverCandidate c in eligible[m])
                {
                    if (ctxRes.ReservedActorIds.Contains(c.Army.ArmyId))
                        continue;
                    if (m.PreferredMoverArmyId.HasValue && c.Army.ArmyId == m.PreferredMoverArmyId.Value)
                    {
                        pick = c;
                        break;
                    }
                    if (pick == null)
                        pick = c;
                }

                if (pick == null)
                {
                    AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — {StableMissionKey.For(m)} left unreserved "
                        + $"(no eligible free scout; eligible {eligible[m].Count})");
                    continue;
                }

                int actorId = pick.Value.Army.ArmyId;
                ctxRes.Reserve(job, actorId, stealth, ground, countsRoom: !incumbent);
                m.ReservedMoverArmyId = actorId;
                m.PreferredMoverArmyId = actorId;
                RepriceForActor(snap, m, pick.Value.Army);
                bound = true;
                AiDebugLog.Write($"[AI][V2][ReconActor] {phase} reserve {StableMissionKey.For(m)} -> #{actorId} "
                    + $"(eligible {eligible[m].Count}, incumbent {(incumbent ? 1 : 0)}, "
                    + $"stealth {(stealth ? 1 : 0)}, reqAp {m.Requirements?.ApDesired:0.#})");
            }
            return bound;
        }

        // P1-2 — once a concrete actor is bound, price the envelope at THAT actor's exact cost.
        private static void RepriceForActor(WorldSnapshot snap, MissionProposal m, ArmySnapshot mover)
        {
            if (mover == null || m?.Requirements == null || !(m.Target is ScoutMissionTarget target))
                return;
            bool stealthRequired = target.Stealth == StealthRequirement.Required;

            HexCoord costHex = target.FocusHex;
            if (target.Kind == ScoutTargetKind.Surveil)
            {
                var vantages = SurveilVantageSelector.Rank(snap, mover, target).ToList();
                if (vantages.Count > 0)
                    costHex = vantages[0].ExecutionHex;
            }

            ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, costHex, stealthRequired);
            MissionRequirements r = m.Requirements;
            r.MoverKnown = true;
            r.ApMinimum = r.ApDesired = r.ApMaximum = pc.RequiredAp;
            if (target.Kind != ScoutTargetKind.Surveil)
            {
                r.EtaTurns = pc.EtaTurns;
                r.EstimatedDistance = pc.Distance;
            }
        }

        private static ArmySnapshot Snapshot(WorldSnapshot snap, int armyId) =>
            snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == armyId);

        // stealth: null = any, true = stealth only, false = generic only.
        private static List<ReconObjective> FilterObjectives(IReadOnlyList<ReconObjective> objectives,
            bool ground, bool? stealth)
        {
            if (objectives == null)
                return new List<ReconObjective>();
            return objectives
                .Where(o => o != null && o.BaseValue > 0f
                    && ((o.Kind == ReconObjectiveKind.Explore) == ground)
                    && (stealth == null || IsStealthObjective(o) == stealth.Value))
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
        }

        private static bool CanExecute(AiTurnContext ctx, PlayerSetupData player, WorldSnapshot snap,
            ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (mover == null)
                return false;
            if (ctx?.Map == null)
                return true;

            ArmyData live = ResolveArmy(player, mover.ArmyId);
            if (live == null)
                return false;

            if (target.Kind != ScoutTargetKind.Surveil)
                return VisitHexTask.FindNextSafeStep(ctx.Map, live, target.FocusHex) != null;

            foreach (SurveilVantageCandidate v in SurveilVantageSelector.Rank(snap, mover, target))
                if (VisitHexTask.FindNextSafeStep(ctx.Map, live, v.ExecutionHex) != null)
                    return true;
            return false;
        }

        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);
    }
}
