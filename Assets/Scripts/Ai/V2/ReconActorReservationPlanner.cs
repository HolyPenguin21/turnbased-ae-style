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
    //  Actor availability is part of PLANNING, before funding. The planner does NOT re-implement a
    //  simplified AP allocator — the authoritative ResourceAllocator (commitments may overdraft an
    //  axis slice against the global pool, fresh missions have a fungible spillover pass, locked
    //  claims, protected air AP) is the ONLY budget authority. The planner just:
    //
    //    1. Actor-matches scarce-first, bounded by per-class + global execution room.
    //    2. Lets the real Pack() decide fundability.
    //    3. On a real Pack deferral for budget/capacity reasons, moves that job into a TRANSIENT
    //       per-wave skip set so a cheaper alternative takes the freed scout — and clears that set
    //       the moment the portfolio materially changes (a provisioning success or failure freed
    //       resources), so a job that becomes fundable when a sibling dies is re-evaluated.
    //    4. On a real provisioning failure, blocks the job for the turn — matching the allocator's
    //       own _rejectedThisTurn, so the planner never pins an actor to a mission the allocator
    //       will not fund again this turn. Structural kinds also free the execution slot.
    // ===========================================================================================

    internal enum ReconJobBlock { None, RejectedThisTurn }

    internal sealed class ReconActorReservationContext
    {
        public readonly HashSet<int> ReservedActorIds = new HashSet<int>();
        public readonly Dictionary<MissionIntentKey, int> JobToActor = new Dictionary<MissionIntentKey, int>();
        public readonly Dictionary<int, MissionIntentKey> ActorToJob = new Dictionary<int, MissionIntentKey>();
        // Non-Recon this-turn claims (raid hosts, defence bodies): never a Recon executor.
        public readonly HashSet<int> HardExcluded = new HashSet<int>();
        // Provisioning-rejected this turn — matches AllocationSession._rejectedThisTurn. Permanent
        // for the turn (the allocator will not fund the mission again); retried fresh next turn.
        public readonly Dictionary<MissionIntentKey, ReconJobBlock> JobBlock =
            new Dictionary<MissionIntentKey, ReconJobBlock>();
        // TRANSIENT: jobs the real Pack just deferred on budget / capacity. Skipped for the current
        // rematch wave only; cleared when the portfolio materially changes.
        public readonly HashSet<MissionIntentKey> CurrentWaveBudgetDeferred = new HashSet<MissionIntentKey>();

        // Static-for-the-turn inputs stashed by Plan so Rematch can rebuild room without re-plumbing.
        public WorldSnapshot Snapshot;
        public AiTurnContext Ctx;
        public PlayerSetupData Player;
        public IReadOnlyList<MissionIntent> ActiveIntents;
        public ActorCommitments ActorCommitments;
        public IReadOnlyList<ReconObjective> FrozenObjectives;
        public int TurnNumber;
        public int ActiveReconExecutions;

        // A job is out of the running for the WHOLE turn (matches the allocator): provisioning-
        // rejected (JobBlock) OR on a cross-turn cooldown. Such a job never holds an actor and its
        // incumbent no longer occupies a current-turn execution / class lane.
        public bool UnfundableThisTurn(MissionIntentKey job) =>
            BlockOf(job) != ReconJobBlock.None;

        public int RemainingGenericObservationRoom;
        public int RemainingGenericGroundRoom;
        public int RemainingStealthObservationRoom;
        public int RemainingStealthGroundRoom;
        public int RemainingGlobalGroundActorRoom;

        public bool IsReservedForAnotherJob(int actorId, MissionIntentKey job) =>
            ActorToJob.TryGetValue(actorId, out MissionIntentKey owner) && !owner.Equals(job);

        public ReconJobBlock BlockOf(MissionIntentKey job) =>
            JobBlock.TryGetValue(job, out ReconJobBlock b) ? b : ReconJobBlock.None;

        public void MarkRejected(MissionIntentKey job) => JobBlock[job] = ReconJobBlock.RejectedThisTurn;

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

            ctxRes.Snapshot = snap;
            ctxRes.Ctx = ctx;
            ctxRes.Player = player;
            ctxRes.ActiveIntents = activeIntents;
            ctxRes.ActorCommitments = actorCommitments;
            ctxRes.FrozenObjectives = frozenObjectives;
            ctxRes.TurnNumber = snap.TurnNumber;

            // 1b. Objectives on a cross-turn cooldown are categorically unfundable this turn — the
            //     allocator will always defer them OnCooldown. MissionLayer.Propose works off the
            //     frozen objective list and does not check cooldown, so a cooldown'd proposal can
            //     otherwise re-grab the only scout every rematch wave and starve a valid backup.
            //     Block them up front so actor matching never considers them.
            AiAllocatorState allocState = AiAllocatorStateRegistry.GetOrCreate(player);
            foreach (MissionProposal m in scoutMissions)
            {
                if (!allocState.OnCooldown(StableMissionKey.For(m), snap.TurnNumber))
                    continue;
                MissionIntentKey job = MissionIntentKey.For(m);
                ctxRes.MarkRejected(job);
                m.ReservedMoverArmyId = null;
                AiDebugLog.Write($"[AI][V2][ReconActor] {StableMissionKey.For(m)} — on cooldown, "
                    + "excluded from actor matching this turn");
            }

            // 2. Seed the context. A durable Recon intent's own claimed mover is bound to ITS OWN
            //    job (not blanket-excluded) so the incumbent can still be matched to it.
            var reconIntentActorByJob = new Dictionary<MissionIntentKey, int>();
            if (activeIntents != null && actorCommitments != null)
                foreach (MissionIntent i in activeIntents)
                    if (i?.Scout != null && i.PreferredMoverArmyId.HasValue
                        && actorCommitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        reconIntentActorByJob[i.IntentKey] = i.PreferredMoverArmyId.Value;

            var scoutJobKeys = new HashSet<MissionIntentKey>(scoutMissions.Select(MissionIntentKey.For));
            foreach (KeyValuePair<MissionIntentKey, int> kv in reconIntentActorByJob)
            {
                bool hasProposal = scoutJobKeys.Contains(kv.Key);
                if (hasProposal && !ctxRes.UnfundableThisTurn(kv.Key))
                    ctxRes.Reserve(kv.Key, kv.Value, stealth: false, ground: false, countsRoom: false);
                else if (!hasProposal)
                    ctxRes.HardExcluded.Add(kv.Value);   // durable recon actor with no proposal this turn
                // hasProposal && cooldown'd: neither seed nor hard-exclude — its scout is FREE for
                // another job this turn, and the incumbent's lane is freed in RebuildRoom.
            }
            if (actorCommitments != null)
                foreach (int claimed in actorCommitments.ClaimedArmyIds)
                    if (!ctxRes.ActorToJob.ContainsKey(claimed))
                        ctxRes.HardExcluded.Add(claimed);

            ctxRes.ActiveReconExecutions = reconIntentActorByJob.Values.Distinct().Count();

            RebuildRoom(ctxRes, null);
            AssignPass(ctxRes, scoutMissions, null, "plan");

            AiDebugLog.Write($"[AI][V2][ReconActor] plan — scoutJobs={scoutMissions.Count} "
                + $"activeExec={ctxRes.ActiveReconExecutions} "
                + $"room[genObs={ctxRes.RemainingGenericObservationRoom} genGround={ctxRes.RemainingGenericGroundRoom} "
                + $"stObs={ctxRes.RemainingStealthObservationRoom} stGround={ctxRes.RemainingStealthGroundRoom} "
                + $"global={ctxRes.RemainingGlobalGroundActorRoom}] reserved={ctxRes.JobToActor.Count} "
                + $"dedupDropped={dedupDrop.Count}");
        }

        // ---- Re-pack pass -------------------------------------------------------------------------
        // portfolioChanged — a provisioning success or failure this iteration freed AP / actors, so
        // budget/capacity deferrals from the LAST (now stale) Pack must be re-evaluated: clear the
        // transient wave-deferred set and do NOT re-read the stale deferrals. When the portfolio did
        // NOT change, the last Pack's budget/capacity verdict still stands — fold those jobs into
        // the wave-deferred set so a cheaper alternative can take their freed scout this wave.
        public static bool Rematch(ReconActorReservationContext ctxRes, List<MissionProposal> missions,
            ProvisioningSession provSession, TentativeAllocation allocation, bool portfolioChanged)
        {
            if (ctxRes == null || missions == null || ctxRes.Snapshot == null)
                return false;

            var scoutMissions = missions.Where(m => m != null && m.Kind == MissionKind.Scout
                && m.Target is ScoutMissionTarget).ToList();
            if (scoutMissions.Count == 0)
                return false;

            var before = new Dictionary<MissionIntentKey, int?>();
            foreach (MissionProposal m in scoutMissions)
                before[MissionIntentKey.For(m)] = m.ReservedMoverArmyId;

            if (portfolioChanged)
            {
                ctxRes.CurrentWaveBudgetDeferred.Clear();
            }
            else if (allocation?.Deferred != null)
            {
                foreach (DeferredEntry d in allocation.Deferred)
                {
                    if (d?.Mission == null || d.Mission.Kind != MissionKind.Scout)
                        continue;
                    if (d.Reason == DeferReason.InsufficientBudget
                        || d.Reason == DeferReason.InsufficientPhysical
                        || d.Reason == DeferReason.CommitmentPoolExhausted
                        || d.Reason == DeferReason.ExecutionCapacity
                        || d.Reason == DeferReason.MissionConflict)
                        ctxRes.CurrentWaveBudgetDeferred.Add(MissionIntentKey.For(d.Mission));
                }
            }

            foreach (MissionProposal m in scoutMissions)
            {
                MissionIntentKey job = MissionIntentKey.For(m);
                if (ctxRes.BlockOf(job) != ReconJobBlock.None)
                    continue;   // provisioning-rejected this turn — stays released
                if (provSession != null && provSession.AlreadyProvisioned(StableMissionKey.For(m)))
                    continue;   // locked — its actor is claimed, keep the reservation
                ctxRes.Release(job, IsStealthJob(m), IsGround(m), refundRoom: false); // room fully rebuilt next
                m.ReservedMoverArmyId = null;
            }

            RebuildRoom(ctxRes, provSession);
            AssignPass(ctxRes, scoutMissions, provSession, "rematch");

            foreach (MissionProposal m in scoutMissions)
            {
                before.TryGetValue(MissionIntentKey.For(m), out int? prev);
                if (prev != m.ReservedMoverArmyId)
                    return true;
            }
            return false;
        }

        // A Scout provisioning miss. The allocator has already put the mission in _rejectedThisTurn
        // (every provisioning failure disposition except RepriceThisTurn does), so the planner must
        // NOT keep — or later re-acquire — an actor for it this turn. Structural kinds additionally
        // free the execution slot; the rest retry fresh next turn.
        public static void RecordProvisionFailure(ReconActorReservationContext ctxRes, MissionProposal mission,
            ProvisionFailureKind kind)
        {
            if (ctxRes == null || mission == null || mission.Kind != MissionKind.Scout)
                return;
            MissionIntentKey job = MissionIntentKey.For(mission);
            bool structural =
                kind == ProvisionFailureKind.TargetSatisfied
                || kind == ProvisionFailureKind.TargetInvalidated
                || kind == ProvisionFailureKind.NoObservationVantage
                || kind == ProvisionFailureKind.AssemblyInfeasible;

            if (mission.ReservedMoverArmyId != null || ctxRes.JobToActor.ContainsKey(job))
                ctxRes.Release(job, IsStealthJob(mission), IsGround(mission), refundRoom: false);
            mission.ReservedMoverArmyId = null;
            ctxRes.MarkRejected(job);

            AiDebugLog.Write($"[AI][V2][ReconActor] {StableMissionKey.For(mission)} — {kind} "
                + $"{(structural ? "structural" : "transient")}; actor released, job blocked this turn "
                + "(matches allocator _rejectedThisTurn)");
        }

        // ---- Room ------------------------------------------------------------------------------
        // Room = how many MORE fresh lanes of each class to staff = desired − already-active durable
        // lanes − fresh lanes already PROVISIONED this turn. Rebuilt from scratch every call.
        private static void RebuildRoom(ReconActorReservationContext ctxRes, ProvisioningSession provSession)
        {
            WorldSnapshot snap = ctxRes.Snapshot;
            var obsRunnable = FilterObjectives(ctxRes.FrozenObjectives, ground: false, stealth: null);
            var groundRunnable = FilterObjectives(ctxRes.FrozenObjectives, ground: true, stealth: null);
            ReconCapacitySnapshot cap = ReconCapacitySnapshot.Build(snap, obsRunnable, groundRunnable,
                ctxRes.ActiveIntents, ctxRes.ActorCommitments, ctxRes.Player,
                ReconAirReservationRegistry.ForTurn(ctxRes.Player, snap.TurnNumber));

            int pGenObs = 0, pGenGround = 0, pStObs = 0, pStGround = 0;
            if (provSession != null)
                foreach (ProvisionedMission pm in provSession.Successful.Values)
                {
                    if (pm.Kind != MissionKind.Scout || (pm.Mission?.FromDurableIntent ?? false))
                        continue;
                    bool g = pm.ScoutKind == ScoutTargetKind.Explore;
                    bool st = pm.Mission != null && IsStealthJob(pm.Mission);
                    if (st && g) pStGround++;
                    else if (st) pStObs++;
                    else if (g) pGenGround++;
                    else pGenObs++;
                }
            int pFreshTotal = pGenObs + pGenGround + pStObs + pStGround;

            // Incumbents taken OUT of the running this turn — provisioning-rejected, on cooldown, OR
            // budget/capacity-deferred by the real Pack for the current wave — no longer occupy a
            // current-turn execution / class lane (durable intent survives for next turn ≠ executes
            // this turn — RECON-01 §7). ReconCapacitySnapshot and the stealth counts below are built
            // from the FROZEN activeIntents and still include them, so subtract them back out per
            // class so a valid backup can take the freed lane + scout.
            int sideGenObs = 0, sideGenGround = 0, sideStObs = 0, sideStGround = 0;
            var sidelinedMovers = new HashSet<int>();
            if (ctxRes.ActiveIntents != null && ctxRes.ActorCommitments != null)
                foreach (MissionIntent i in ctxRes.ActiveIntents)
                {
                    if (i?.Scout == null || !i.PreferredMoverArmyId.HasValue
                        || !ctxRes.ActorCommitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        continue;
                    if (!ctxRes.UnfundableThisTurn(i.IntentKey)
                        && !ctxRes.CurrentWaveBudgetDeferred.Contains(i.IntentKey))
                        continue;
                    sidelinedMovers.Add(i.PreferredMoverArmyId.Value);
                    bool g = i.Scout.Kind == ScoutTargetKind.Explore;
                    bool st = i.Scout.RequiresStealth;
                    if (st && g) sideStGround++;
                    else if (st) sideStObs++;
                    else if (g) sideGenGround++;
                    else sideGenObs++;
                }

            int airObs = cap.AirborneReconLanes + cap.SpareAirObservationSorties;
            ctxRes.RemainingGenericObservationRoom = Mathf.Max(0,
                cap.DesiredObservationConcurrency
                - Mathf.Max(0, cap.GenericObservationLaneActors.Count - sideGenObs) - airObs - pGenObs);
            ctxRes.RemainingGenericGroundRoom = Mathf.Max(0,
                cap.DesiredGroundTraversalConcurrency
                - Mathf.Max(0, cap.GenericGroundLaneActors.Count - sideGenGround) - pGenGround);

            int activeStealthObs = 0, activeStealthGround = 0;
            if (ctxRes.ActiveIntents != null && ctxRes.ActorCommitments != null)
                foreach (MissionIntent i in ctxRes.ActiveIntents)
                    if (i?.Scout != null && i.Scout.RequiresStealth && i.PreferredMoverArmyId.HasValue
                        && ctxRes.ActorCommitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                    {
                        if (i.Scout.Kind == ScoutTargetKind.Explore) activeStealthGround++;
                        else activeStealthObs++;
                    }
            ctxRes.RemainingStealthObservationRoom = Mathf.Max(0,
                ReconConcurrencyPolicy.DesiredForClass(snap,
                    FilterObjectives(ctxRes.FrozenObjectives, ground: false, stealth: true),
                    ReconConcurrencyPolicy.ReconCoverageClass.Observation)
                - Mathf.Max(0, activeStealthObs - sideStObs) - pStObs);
            ctxRes.RemainingStealthGroundRoom = Mathf.Max(0,
                ReconConcurrencyPolicy.DesiredForClass(snap,
                    FilterObjectives(ctxRes.FrozenObjectives, ground: true, stealth: true),
                    ReconConcurrencyPolicy.ReconCoverageClass.GroundTraversal)
                - Mathf.Max(0, activeStealthGround - sideStGround) - pStGround);

            // Global ceiling = min(HardCap, desired concurrency over ALL runnable Recon objectives
            // — generic AND stealth). cap.CombinedDesiredConcurrency is generic-only and would
            // under-cap a mixed / all-stealth portfolio.
            var allRunnable = (ctxRes.FrozenObjectives ?? System.Array.Empty<ReconObjective>())
                .Where(o => o != null && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue).ThenBy(o => o.IntentKey).ToList();
            int combinedAllDesired = ReconConcurrencyPolicy.DesiredForClass(snap, allRunnable,
                ReconConcurrencyPolicy.ReconCoverageClass.Combined);
            int ceiling = Mathf.Min(ReconConcurrencyPolicy.HardCap, Mathf.Max(1, combinedAllDesired));
            int effectiveActive = Mathf.Max(0, ctxRes.ActiveReconExecutions - sidelinedMovers.Count);
            ctxRes.RemainingGlobalGroundActorRoom = Mathf.Max(0, ceiling - effectiveActive - pFreshTotal);
        }

        // ---- Shared assignment (scarce-first, room-limited — NO private AP model) -------------
        private static void AssignPass(ReconActorReservationContext ctxRes, List<MissionProposal> scoutMissions,
            ProvisioningSession provSession, string phase)
        {
            WorldSnapshot snap = ctxRes.Snapshot;
            AiTurnContext ctx = ctxRes.Ctx;
            PlayerSetupData player = ctxRes.Player;

            var provisionedExclude = new HashSet<int>(ctxRes.HardExcluded);
            if (provSession != null)
                foreach (int id in provSession.ClaimedArmyIds)
                    provisionedExclude.Add(id);

            var eligible = new Dictionary<MissionProposal, List<ScoutMoverCandidate>>();
            foreach (MissionProposal m in scoutMissions)
            {
                MissionIntentKey job = MissionIntentKey.For(m);
                var exclude = new HashSet<int>(provisionedExclude);
                foreach (int rid in ctxRes.ReservedActorIds)
                    if (ctxRes.IsReservedForAnotherJob(rid, job))
                        exclude.Add(rid);
                var target = (ScoutMissionTarget)m.Target;
                eligible[m] = ScoutMoverSelector.Rank(snap, target, exclude)
                    .Where(c => CanExecute(ctx, player, snap, c.Army, target))
                    .ToList();
            }

            // Durable incumbents first (lock their carried scout before any fresh job's tie-break),
            // then strategic priority (AdmissionRank) DESC, then fewest eligible actors ASC, then key.
            scoutMissions.Sort((a, b) =>
            {
                int c = (a.FromDurableIntent ? 0 : 1).CompareTo(b.FromDurableIntent ? 0 : 1);
                if (c != 0) return c;
                c = MissionAdmissionPolicy.AdmissionRank(b).CompareTo(MissionAdmissionPolicy.AdmissionRank(a));
                if (c != 0) return c;
                c = eligible[a].Count.CompareTo(eligible[b].Count);
                if (c != 0) return c;
                return StableMissionKey.For(a).CompareTo(StableMissionKey.For(b));
            });

            foreach (MissionProposal m in scoutMissions)
            {
                MissionIntentKey job = MissionIntentKey.For(m);
                bool incumbent = m.FromDurableIntent;
                bool ground = IsGround(m);
                bool stealth = IsStealthJob(m);

                if (ctxRes.BlockOf(job) != ReconJobBlock.None
                    || ctxRes.CurrentWaveBudgetDeferred.Contains(job))
                {
                    m.ReservedMoverArmyId = null;
                    continue;
                }
                if (provSession != null && provSession.AlreadyProvisioned(StableMissionKey.For(m)))
                    continue;   // executed this turn — keep whatever the ledger holds

                int? held = ctxRes.JobToActor.TryGetValue(job, out int h) ? h : (int?)null;
                if (held.HasValue && !eligible[m].Any(c => c.Army.ArmyId == held.Value))
                {
                    ctxRes.Release(job, stealth, ground, refundRoom: !incumbent);
                    m.ReservedMoverArmyId = null;
                    held = null;
                }

                ScoutMoverCandidate? pick;
                if (held.HasValue)
                {
                    pick = eligible[m].First(c => c.Army.ArmyId == held.Value);
                }
                else
                {
                    if (!incumbent
                        && (ctxRes.ClassRoom(stealth, ground) <= 0 || ctxRes.RemainingGlobalGroundActorRoom <= 0))
                    {
                        AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — {StableMissionKey.For(m)} unreserved "
                            + $"(no {(stealth ? "stealth-" : "")}{(ground ? "ground" : "obs")} room / global {ctxRes.RemainingGlobalGroundActorRoom})");
                        continue;
                    }
                    pick = null;
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
                        AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — {StableMissionKey.For(m)} unreserved "
                            + $"(no eligible free scout; eligible {eligible[m].Count})");
                        continue;
                    }
                }

                if (!held.HasValue)
                    ctxRes.Reserve(job, pick.Value.Army.ArmyId, stealth, ground, countsRoom: !incumbent);
                m.ReservedMoverArmyId = pick.Value.Army.ArmyId;
                m.PreferredMoverArmyId = pick.Value.Army.ArmyId;
                ApplyReprice(snap, m, pick.Value.Army);

                AiDebugLog.Write($"[AI][V2][ReconActor] {phase} reserve {StableMissionKey.For(m)} -> #{pick.Value.Army.ArmyId} "
                    + $"(eligible {eligible[m].Count}, incumbent {(incumbent ? 1 : 0)}, stealth {(stealth ? 1 : 0)})");
            }
        }

        // Once a concrete actor is bound, price the envelope at THAT actor's exact cost.
        private static void ApplyReprice(WorldSnapshot snap, MissionProposal m, ArmySnapshot mover)
        {
            if (mover == null || m?.Requirements == null || !(m.Target is ScoutMissionTarget target))
                return;
            HexCoord costHex = ExecutionHexFor(snap, mover, target);
            ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, costHex, target.Stealth == StealthRequirement.Required);
            MissionRequirements r = m.Requirements;
            r.MoverKnown = true;
            r.ApMinimum = r.ApDesired = r.ApMaximum = pc.RequiredAp;
            if (target.Kind != ScoutTargetKind.Surveil)
            {
                r.EtaTurns = pc.EtaTurns;
                r.EstimatedDistance = pc.Distance;
            }
        }

        private static HexCoord ExecutionHexFor(WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (target.Kind != ScoutTargetKind.Surveil)
                return target.FocusHex;
            var vantages = SurveilVantageSelector.Rank(snap, mover, target).ToList();
            return vantages.Count > 0 ? vantages[0].ExecutionHex : target.FocusHex;
        }

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
