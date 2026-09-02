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
    //  The failure this closes: create N Recon jobs -> the allocator funds N -> ProvisioningManager
    //  discovers two relied on the same solo Recce -> MoverContended -> wasted re-pack iterations
    //  (and, at the bound, a silently dropped lane). Actor availability must be part of PLANNING —
    //  and it must have a real release / rematch lifecycle, not a single up-front prune.
    //
    //  Lifecycle (ReconActorReservationContext lives for the whole re-pack loop):
    //
    //     job  --Match-->  actor reservation  --(allocator)-->  budget reservation  -->  funding
    //
    //  and on ANY later miss:
    //
    //     budget defer / provisioning invalidation  ->  Release(job) (actor + its concurrency slot)
    //                                                ->  Rematch(remaining live jobs)  ->  re-Pack
    //
    //  Unmatched jobs are NOT deleted from the mission list — they stay as unreserved proposals the
    //  allocator defers (DeferReason.ReconActorUnreserved) and a later Rematch can still bind an
    //  actor freed this same turn. Only genuine ReconJobKey duplicates are removed.
    //
    //  Concurrency room is tracked PER REQUIREMENT CLASS (Observation vs GroundTraversal), seeded
    //  from ReconCapacitySnapshot, so an abundant Refresh can never eat the one free scout a
    //  GroundVisit deficit actually needs (aviation covers Observation, never a physical visit).
    // ===========================================================================================

    // ReconActorReservationContext { ReservedActorIds, ActorToJob, JobToActor } + the per-class
    // concurrency room. Persists across Plan + every Rematch call within one AI turn.
    internal sealed class ReconActorReservationContext
    {
        public readonly HashSet<int> ReservedActorIds = new HashSet<int>();
        public readonly Dictionary<MissionIntentKey, int> JobToActor = new Dictionary<MissionIntentKey, int>();
        public readonly Dictionary<int, MissionIntentKey> ActorToJob = new Dictionary<int, MissionIntentKey>();
        // Non-Recon this-turn claims (raid hosts, defence bodies): never a Recon executor, always
        // excluded, never entered into the job maps.
        public readonly HashSet<int> HardExcluded = new HashSet<int>();

        public int RemainingObservationRoom;
        public int RemainingGroundTraversalRoom;

        public bool IsReservedForAnotherJob(int actorId, MissionIntentKey job) =>
            ActorToJob.TryGetValue(actorId, out MissionIntentKey owner) && !owner.Equals(job);

        public void Reserve(MissionIntentKey job, int actorId, bool ground, bool countsRoom)
        {
            ReservedActorIds.Add(actorId);
            JobToActor[job] = actorId;
            ActorToJob[actorId] = job;
            if (!countsRoom)
                return;
            if (ground)
                RemainingGroundTraversalRoom = Mathf.Max(0, RemainingGroundTraversalRoom - 1);
            else
                RemainingObservationRoom = Mathf.Max(0, RemainingObservationRoom - 1);
        }

        public void Release(MissionIntentKey job, bool ground, bool refundRoom)
        {
            if (!JobToActor.TryGetValue(job, out int actorId))
                return;
            JobToActor.Remove(job);
            ActorToJob.Remove(actorId);
            ReservedActorIds.Remove(actorId);
            if (!refundRoom)
                return;
            if (ground)
                RemainingGroundTraversalRoom++;
            else
                RemainingObservationRoom++;
        }
    }

    internal static class ReconActorReservationPlanner
    {
        private static bool IsGround(MissionProposal m) =>
            m.Target is ScoutMissionTarget t && t.Kind == ScoutTargetKind.Explore;

        // ---- First pass ---------------------------------------------------------------------------
        public static void Plan(ReconActorReservationContext ctxRes, WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, List<MissionProposal> missions, ActorCommitments actorCommitments,
            IReadOnlyList<MissionIntent> activeIntents, IReadOnlyList<ReconObjective> frozenObjectives)
        {
            if (ctxRes == null || missions == null || missions.Count == 0 || snap?.Self?.Armies == null)
                return;

            // 1. Recon proposals only, deduplicated by ReconJobKey (== MissionIntentKey — Requirement
            //    via SubKind + hex + tracked target). Explore(H) and Refresh(H) never merge; two
            //    identical Scout(Explore H) collapse to one job.
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

            // 2. Seed the reservation context. A durable Recon intent's own claimed mover is bound
            //    to ITS OWN job (JobToActor / ActorToJob) — NOT blanket-excluded — so that incumbent
            //    can still be matched to its own actor (the earlier bug: seeding it into a flat
            //    exclude set made ScoutMoverSelector.Rank drop it from its own eligible list). Every
            //    other this-turn claim (raid host, defence body, a recon actor whose intent has no
            //    proposal this turn) is a hard exclusion.
            var reconIntentActorByJob = new Dictionary<MissionIntentKey, int>();
            if (activeIntents != null && actorCommitments != null)
                foreach (MissionIntent i in activeIntents)
                    if (i?.Scout != null && i.PreferredMoverArmyId.HasValue
                        && actorCommitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        reconIntentActorByJob[i.IntentKey] = i.PreferredMoverArmyId.Value;

            var scoutJobKeys = new HashSet<MissionIntentKey>(scoutMissions.Select(MissionIntentKey.For));
            foreach (KeyValuePair<MissionIntentKey, int> kv in reconIntentActorByJob)
            {
                if (scoutJobKeys.Contains(kv.Key))
                    ctxRes.Reserve(kv.Key, kv.Value, ground: false, countsRoom: false); // ground flag irrelevant, no room debit
                else
                    ctxRes.HardExcluded.Add(kv.Value);   // durable recon actor with no proposal this turn
            }
            if (actorCommitments != null)
                foreach (int claimed in actorCommitments.ClaimedArmyIds)
                    if (!ctxRes.ActorToJob.ContainsKey(claimed))
                        ctxRes.HardExcluded.Add(claimed);

            int activeReconExecutions = reconIntentActorByJob.Values.Distinct().Count();

            // 3. Per-class concurrency room from the unified capacity model (Observation vs
            //    GroundTraversal). Recompute here on the post-Phase-A snapshot rather than trusting
            //    a single scalar.
            var obsRunnable = FilterObjectives(frozenObjectives, ground: false);
            var groundRunnable = FilterObjectives(frozenObjectives, ground: true);
            ReconCapacitySnapshot cap = ReconCapacitySnapshot.Build(snap, obsRunnable, groundRunnable,
                activeIntents, actorCommitments, player,
                ReconAirReservationRegistry.ForTurn(player, snap.TurnNumber));

            // Room = how many FRESH lanes of each class to staff this turn = desired concurrency
            // minus the lanes already ACTIVE (a claimed executing scout), minus — for Observation
            // only — the air observation the prepass has pinned. Idle scouts are the SUPPLY that
            // fills this room via assignment below, never a reason to leave it unstaffed (that is
            // the "capacity deficit" question DemandLayer already answered). Air never reduces the
            // GroundTraversal room — a physical visit cannot be flown.
            int airObs = cap.AirborneReconLanes + cap.SpareAirObservationSorties;
            ctxRes.RemainingObservationRoom = Mathf.Max(0,
                cap.DesiredObservationConcurrency - cap.GenericObservationLaneActors.Count - airObs);
            ctxRes.RemainingGroundTraversalRoom = Mathf.Max(0,
                cap.DesiredGroundTraversalConcurrency - cap.GenericGroundLaneActors.Count);

            AssignPass(ctxRes, snap, ctx, player, scoutMissions, "plan");

            AiDebugLog.Write($"[AI][V2][ReconActor] plan — scoutJobs={scoutMissions.Count} "
                + $"activeExec={activeReconExecutions} obsRoom={ctxRes.RemainingObservationRoom} "
                + $"groundRoom={ctxRes.RemainingGroundTraversalRoom} "
                + $"reserved={ctxRes.JobToActor.Count} dedupDropped={dedupDrop.Count} capacity[{cap.Explain}]");
        }

        // ---- Re-pack pass: release + rematch ----------------------------------------------------
        // Called from the bounded re-pack loop. Frees the actor (and its concurrency slot) of every
        // Scout that ended up deferred purely on budget with a bound actor, then re-runs the
        // assignment across every still-unreserved live Scout mission. Returns true when any
        // ReservedMoverArmyId changed — the caller must Pack() again.
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

            // Release actors held by budget-deferred Scouts so a cheaper live job can take them.
            if (deferred != null)
                foreach (DeferredEntry d in deferred)
                {
                    if (d?.Mission == null || d.Mission.Kind != MissionKind.Scout
                        || d.Mission.ReservedMoverArmyId == null)
                        continue;
                    if (d.Reason != DeferReason.InsufficientBudget && d.Reason != DeferReason.InsufficientPhysical)
                        continue;
                    MissionIntentKey job = MissionIntentKey.For(d.Mission);
                    ctxRes.Release(job, IsGround(d.Mission), refundRoom: !d.Mission.FromDurableIntent);
                    AiDebugLog.Write($"[AI][V2][ReconActor] rematch release {StableMissionKey.For(d.Mission)} "
                        + $"— deferred {d.Reason}, freeing #{d.Mission.ReservedMoverArmyId}");
                    d.Mission.ReservedMoverArmyId = null;
                    changed = true;
                }

            if (AssignPass(ctxRes, snap, ctx, player, scoutMissions, "rematch"))
                changed = true;
            return changed;
        }

        // A Scout provisioning miss that frees its actor (satisfied / invalidated elsewhere, no
        // executable step, or contended). Release so the next Rematch / Pack can reuse the actor.
        public static void ReleaseForProvisionFailure(ReconActorReservationContext ctxRes, MissionProposal mission)
        {
            if (ctxRes == null || mission == null || mission.Kind != MissionKind.Scout)
                return;
            MissionIntentKey job = MissionIntentKey.For(mission);
            if (mission.ReservedMoverArmyId != null || ctxRes.JobToActor.ContainsKey(job))
            {
                ctxRes.Release(job, IsGround(mission), refundRoom: !mission.FromDurableIntent);
                AiDebugLog.Write($"[AI][V2][ReconActor] release {StableMissionKey.For(mission)} "
                    + "— provisioning miss, actor returned to the pool");
            }
            mission.ReservedMoverArmyId = null;
        }

        // ---- Shared assignment ----------------------------------------------------------------
        // Binds a concrete scout to every still-unreserved live Scout mission, scarce-first, within
        // the per-class room. Returns true when it bound at least one new actor.
        private static bool AssignPass(ReconActorReservationContext ctxRes, WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, List<MissionProposal> scoutMissions, string phase)
        {
            // Eligible actors per mission: capability + operational + not hard-excluded + not
            // reserved for ANOTHER job + can take a safe first step this turn. The mission's OWN
            // reserved actor (durable incumbent seed) is deliberately kept eligible for it.
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

            // Scarce jobs first: strategic priority (AdmissionRank) DESC, then fewest eligible
            // actors ASC, then stable key.
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

                // Already bound this turn (seeded incumbent, or a prior AssignPass). Confirm the
                // actor is still eligible; if it vanished, release and try to re-bind below.
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
                    ctxRes.Release(job, ground, refundRoom: !incumbent);
                    m.ReservedMoverArmyId = null;
                    AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — held #{held} for {StableMissionKey.For(m)} "
                        + "no longer eligible; released");
                }

                if (m.ReservedMoverArmyId != null)
                    continue;

                // Room check for a FRESH lane (an incumbent always keeps its lane).
                if (!incumbent)
                {
                    int room = ground ? ctxRes.RemainingGroundTraversalRoom : ctxRes.RemainingObservationRoom;
                    if (room <= 0)
                    {
                        AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — {StableMissionKey.For(m)} left unreserved "
                            + $"(no {(ground ? "ground-traversal" : "observation")} concurrency room)");
                        continue;
                    }
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
                ctxRes.Reserve(job, actorId, ground, countsRoom: !incumbent);
                m.ReservedMoverArmyId = actorId;
                m.PreferredMoverArmyId = actorId;
                RepriceForActor(snap, m, pick.Value.Army);
                bound = true;
                AiDebugLog.Write($"[AI][V2][ReconActor] {phase} reserve {StableMissionKey.For(m)} -> #{actorId} "
                    + $"(eligible {eligible[m].Count}, incumbent {(incumbent ? 1 : 0)}, "
                    + $"reqAp {m.Requirements?.ApDesired:0.#})");
            }
            return bound;
        }

        // P1-2 — once a concrete actor is bound, the MissionRequirements envelope is that actor's
        // EXACT cost, not the worst-case MAX across every eligible mover (ScoutPricingWitness's
        // full-beam figure), which could make an executable mission read as InsufficientBudget.
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

        private static List<ReconObjective> FilterObjectives(IReadOnlyList<ReconObjective> objectives, bool ground)
        {
            if (objectives == null)
                return new List<ReconObjective>();
            return objectives
                .Where(o => o != null && o.BaseValue > 0f
                    && ((o.Kind == ReconObjectiveKind.Explore) == ground))
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
        }

        // Mirror of ProvisioningManager.BuildExecutionCandidates' per-actor executability gate.
        private static bool CanExecute(AiTurnContext ctx, PlayerSetupData player, WorldSnapshot snap,
            ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (mover == null)
                return false;
            if (ctx?.Map == null)
                return true; // bare harness — leave the final gate to provisioning

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
