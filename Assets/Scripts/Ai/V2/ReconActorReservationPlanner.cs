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
    //  Actor availability is part of PLANNING, before funding. The reservation context lives for
    //  the whole re-pack loop, but it NEVER inherits an admission verdict from a previous Pack():
    //
    //    · The ONLY permanent (this-turn) block is a real provisioning STRUCTURAL rejection
    //      (TargetSatisfied / TargetInvalidated / NoObservationVantage / AssemblyInfeasible).
    //    · InsufficientBudget / ExecutionCapacity / MissionConflict / CommitmentPoolExhausted are
    //      transient portfolio outcomes. Rematch() does NOT block them — it releases EVERY
    //      non-structurally-rejected, not-yet-provisioned reservation, rebuilds the room from the
    //      current snapshot, and re-assigns from scratch with a BUDGET-FEASIBLE greedy: scarce-
    //      first, but a job whose activation cost would exceed the remaining Recon AP budget is
    //      skipped so a cheaper lower-priority job can take the freed scout. That, not a sticky
    //      block, is what stops the "pricey Surveil re-grabs the only scout every pass" loop, and
    //      it lets a job funded once the portfolio changes (a sibling died and freed AP) get its
    //      turn on the very next Pack.
    //
    //  ROOM is per requirement class AND per stealth tier, with a shared global ceiling:
    //     Generic{Observation,GroundTraversal}   — aviation covers Observation only
    //     Stealth{Observation,GroundTraversal}   — aviation covers neither
    //     GlobalGroundActor = min(HardCap, CombinedDesiredConcurrency) - active executions
    //  Rejected incumbents are subtracted from the active count so their execution slot is freed.
    // ===========================================================================================

    internal enum ReconJobBlock { None, StructuralRejectedThisTurn }

    internal sealed class ReconActorReservationContext
    {
        public readonly HashSet<int> ReservedActorIds = new HashSet<int>();
        public readonly Dictionary<MissionIntentKey, int> JobToActor = new Dictionary<MissionIntentKey, int>();
        public readonly Dictionary<int, MissionIntentKey> ActorToJob = new Dictionary<int, MissionIntentKey>();
        // Non-Recon this-turn claims (raid hosts, defence bodies): never a Recon executor.
        public readonly HashSet<int> HardExcluded = new HashSet<int>();
        public readonly Dictionary<MissionIntentKey, ReconJobBlock> JobBlock =
            new Dictionary<MissionIntentKey, ReconJobBlock>();
        // Incumbent jobs whose provisioning structurally failed this turn — their execution slot is
        // no longer occupied, so the global room recomputation must not keep counting them.
        public readonly HashSet<MissionIntentKey> StructurallyRejectedIncumbents = new HashSet<MissionIntentKey>();

        // Static-for-the-turn inputs, stashed by Plan so Rematch can rebuild room without re-plumbing.
        public WorldSnapshot Snapshot;
        public AiTurnContext Ctx;
        public PlayerSetupData Player;
        public IReadOnlyList<MissionIntent> ActiveIntents;
        public ActorCommitments ActorCommitments;
        public IReadOnlyList<ReconObjective> FrozenObjectives;
        public float ReconApBudget;              // AxisBudgetLedger.Balance(Recon) at plan time
        public int ActiveReconExecutions;

        public int RemainingGenericObservationRoom;
        public int RemainingGenericGroundRoom;
        public int RemainingStealthObservationRoom;
        public int RemainingStealthGroundRoom;
        public int RemainingGlobalGroundActorRoom;

        public bool IsReservedForAnotherJob(int actorId, MissionIntentKey job) =>
            ActorToJob.TryGetValue(actorId, out MissionIntentKey owner) && !owner.Equals(job);

        public ReconJobBlock BlockOf(MissionIntentKey job) =>
            JobBlock.TryGetValue(job, out ReconJobBlock b) ? b : ReconJobBlock.None;

        public void MarkStructural(MissionIntentKey job, bool incumbent)
        {
            JobBlock[job] = ReconJobBlock.StructuralRejectedThisTurn;
            if (incumbent)
                StructurallyRejectedIncumbents.Add(job);
        }

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
            IReadOnlyList<MissionIntent> activeIntents, IReadOnlyList<ReconObjective> frozenObjectives,
            AxisBudgetLedger apLedger)
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
            ctxRes.ReconApBudget = apLedger != null ? Mathf.Max(0f, apLedger.Balance(DesireAxis.Recon)) : float.MaxValue;

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
                if (scoutJobKeys.Contains(kv.Key))
                    ctxRes.Reserve(kv.Key, kv.Value, stealth: false, ground: false, countsRoom: false);
                else
                    ctxRes.HardExcluded.Add(kv.Value);
            }
            if (actorCommitments != null)
                foreach (int claimed in actorCommitments.ClaimedArmyIds)
                    if (!ctxRes.ActorToJob.ContainsKey(claimed))
                        ctxRes.HardExcluded.Add(claimed);

            ctxRes.ActiveReconExecutions = reconIntentActorByJob.Values.Distinct().Count();

            RebuildRoom(ctxRes, null);
            AssignPass(ctxRes, scoutMissions, null, "plan");

            AiDebugLog.Write($"[AI][V2][ReconActor] plan — scoutJobs={scoutMissions.Count} "
                + $"activeExec={ctxRes.ActiveReconExecutions} reconApBudget={ctxRes.ReconApBudget:0.#} "
                + $"room[genObs={ctxRes.RemainingGenericObservationRoom} genGround={ctxRes.RemainingGenericGroundRoom} "
                + $"stObs={ctxRes.RemainingStealthObservationRoom} stGround={ctxRes.RemainingStealthGroundRoom} "
                + $"global={ctxRes.RemainingGlobalGroundActorRoom}] reserved={ctxRes.JobToActor.Count} "
                + $"dedupDropped={dedupDrop.Count}");
        }

        // ---- Re-pack pass ---------------------------------------------------------------------
        // Release everything not structurally rejected and not already provisioned, rebuild the
        // room from scratch, and re-assign with the budget-feasible greedy. Returns true when any
        // ReservedMoverArmyId changed — the caller must Pack() again.
        public static bool Rematch(ReconActorReservationContext ctxRes, List<MissionProposal> missions,
            ProvisioningSession provSession)
        {
            if (ctxRes == null || missions == null || ctxRes.Snapshot == null)
                return false;

            var scoutMissions = missions.Where(m => m != null && m.Kind == MissionKind.Scout
                && m.Target is ScoutMissionTarget).ToList();
            if (scoutMissions.Count == 0)
                return false;

            var before = scoutMissions.ToDictionary(MissionIntentKey.For, m => m.ReservedMoverArmyId);

            foreach (MissionProposal m in scoutMissions)
            {
                MissionIntentKey job = MissionIntentKey.For(m);
                if (ctxRes.BlockOf(job) == ReconJobBlock.StructuralRejectedThisTurn)
                    continue;
                if (provSession != null && provSession.AlreadyProvisioned(StableMissionKey.For(m)))
                    continue;   // locked — its actor is claimed, keep the reservation
                ctxRes.Release(job, IsStealthJob(m), IsGround(m), refundRoom: false); // room is fully rebuilt next
                m.ReservedMoverArmyId = null;
            }

            RebuildRoom(ctxRes, provSession);
            AssignPass(ctxRes, scoutMissions, provSession, "rematch");

            bool changed = false;
            foreach (MissionProposal m in scoutMissions)
            {
                before.TryGetValue(MissionIntentKey.For(m), out int? prev);
                if (prev != m.ReservedMoverArmyId)
                    changed = true;
            }
            return changed;
        }

        // A Scout provisioning miss. STRUCTURAL kinds (the objective is gone / unservable) block the
        // job permanently this turn; the rest (actor contention / no step / no mover) just release
        // the actor so a later Rematch can rebind it — they are NOT permanent.
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

            if (structural)
            {
                ctxRes.MarkStructural(job, mission.FromDurableIntent);
                AiDebugLog.Write($"[AI][V2][ReconActor] {StableMissionKey.For(mission)} — {kind} structural; "
                    + "job blocked this turn, actor + execution slot released");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2][ReconActor] {StableMissionKey.For(mission)} — {kind}; "
                    + "actor released for rematch (not a permanent block)");
            }
        }

        // ---- Room ---------------------------------------------------------------------------------
        // Room = how many MORE fresh lanes of each class to staff = desired − already-active durable
        // lanes − fresh lanes already PROVISIONED this turn. AssignPass then decrements as it binds
        // new fresh reservations. Called with provSession == null from Plan (nothing provisioned
        // yet) and with the live session from every Rematch (rebuilt from scratch — no verdict is
        // inherited from a prior Pack).
        private static void RebuildRoom(ReconActorReservationContext ctxRes, ProvisioningSession provSession)
        {
            WorldSnapshot snap = ctxRes.Snapshot;
            var obsRunnable = FilterObjectives(ctxRes.FrozenObjectives, ground: false, stealth: null);
            var groundRunnable = FilterObjectives(ctxRes.FrozenObjectives, ground: true, stealth: null);
            ReconCapacitySnapshot cap = ReconCapacitySnapshot.Build(snap, obsRunnable, groundRunnable,
                ctxRes.ActiveIntents, ctxRes.ActorCommitments, ctxRes.Player,
                ReconAirReservationRegistry.ForTurn(ctxRes.Player, snap.TurnNumber));

            // Fresh (non-incumbent) Scout lanes already provisioned this turn, by class.
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

            int airObs = cap.AirborneReconLanes + cap.SpareAirObservationSorties;
            ctxRes.RemainingGenericObservationRoom = Mathf.Max(0,
                cap.DesiredObservationConcurrency - cap.GenericObservationLaneActors.Count - airObs - pGenObs);
            ctxRes.RemainingGenericGroundRoom = Mathf.Max(0,
                cap.DesiredGroundTraversalConcurrency - cap.GenericGroundLaneActors.Count - pGenGround);

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
                    ReconConcurrencyPolicy.ReconCoverageClass.Observation) - activeStealthObs - pStObs);
            ctxRes.RemainingStealthGroundRoom = Mathf.Max(0,
                ReconConcurrencyPolicy.DesiredForClass(snap,
                    FilterObjectives(ctxRes.FrozenObjectives, ground: true, stealth: true),
                    ReconConcurrencyPolicy.ReconCoverageClass.GroundTraversal) - activeStealthGround - pStGround);

            // Global = min(HardCap, CombinedDesiredConcurrency) − active durable lanes − fresh lanes
            // already provisioned. Structurally rejected incumbents no longer occupy a slot
            // (invalidation frees execution capacity, RECON-01 §7). Never staff past the useful
            // combined concurrency even though the hard cap alone would allow it (RECON-02).
            int ceiling = Mathf.Min(ReconConcurrencyPolicy.HardCap, Mathf.Max(1, cap.CombinedDesiredConcurrency));
            int effectiveActive = Mathf.Max(0,
                ctxRes.ActiveReconExecutions - ctxRes.StructurallyRejectedIncumbents.Count);
            ctxRes.RemainingGlobalGroundActorRoom = Mathf.Max(0, ceiling - effectiveActive - pFreshTotal);
        }

        // ---- Shared assignment (budget-feasible greedy) --------------------------------------
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

            // Durable incumbents first so they lock their carried scout before any fresh job can
            // out-order them on the scarce-first tie-break; then strategic priority (AdmissionRank)
            // DESC, then fewest eligible actors ASC, then stable key.
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

            float cumulativeReconAp = 0f;
            float eps = AiConfigV2.allocatorSliceEpsilon;

            foreach (MissionProposal m in scoutMissions)
            {
                MissionIntentKey job = MissionIntentKey.For(m);
                bool incumbent = m.FromDurableIntent;
                bool ground = IsGround(m);
                bool stealth = IsStealthJob(m);

                if (ctxRes.BlockOf(job) == ReconJobBlock.StructuralRejectedThisTurn)
                {
                    m.ReservedMoverArmyId = null;
                    continue;
                }
                if (provSession != null && provSession.AlreadyProvisioned(StableMissionKey.For(m)))
                {
                    // Already executed this turn — count its spend, keep whatever the ledger holds.
                    if (ctxRes.JobToActor.TryGetValue(job, out int done))
                        cumulativeReconAp += ActorCost(snap, m, Snapshot(snap, done));
                    continue;
                }

                // Confirm an already-held actor (seeded incumbent) or (re)bind one.
                int? held = ctxRes.JobToActor.TryGetValue(job, out int h) ? h : (int?)null;
                if (held.HasValue && !eligible[m].Any(c => c.Army.ArmyId == held.Value))
                {
                    ctxRes.Release(job, stealth, ground, refundRoom: !incumbent);
                    m.ReservedMoverArmyId = null;
                    held = null;
                }

                ScoutMoverCandidate? pick = null;
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

                float cost = ActorCost(snap, m, pick.Value.Army);

                // Budget-feasible greedy: never hold a scout for a job the Recon axis budget cannot
                // fund this turn — a cheaper job takes the actor instead. This applies to a durable
                // incumbent too: an over-budget commitment (the allocator would defer it
                // CommitmentPoolExhausted) must NOT keep its carried scout pinned; its INTENT
                // survives via continuity, it just does not execute this turn. Incumbents are only
                // released, never structurally blocked.
                if (cumulativeReconAp + cost > ctxRes.ReconApBudget + eps)
                {
                    if (held.HasValue || ctxRes.JobToActor.ContainsKey(job))
                    {
                        ctxRes.Release(job, stealth, ground, refundRoom: !incumbent);
                        m.ReservedMoverArmyId = null;
                    }
                    AiDebugLog.Write($"[AI][V2][ReconActor] {phase} — {StableMissionKey.For(m)} unreserved "
                        + $"(cost {cost:0.#} + used {cumulativeReconAp:0.#} > recon budget {ctxRes.ReconApBudget:0.#}"
                        + $"{(incumbent ? "; incumbent, intent kept" : "")})");
                    continue;
                }

                if (!held.HasValue)
                    ctxRes.Reserve(job, pick.Value.Army.ArmyId, stealth, ground, countsRoom: !incumbent);
                m.ReservedMoverArmyId = pick.Value.Army.ArmyId;
                m.PreferredMoverArmyId = pick.Value.Army.ArmyId;
                ApplyReprice(snap, m, pick.Value.Army);
                cumulativeReconAp += cost;

                AiDebugLog.Write($"[AI][V2][ReconActor] {phase} reserve {StableMissionKey.For(m)} -> #{pick.Value.Army.ArmyId} "
                    + $"(eligible {eligible[m].Count}, incumbent {(incumbent ? 1 : 0)}, stealth {(stealth ? 1 : 0)}, "
                    + $"cost {cost:0.#}, usedNow {cumulativeReconAp:0.#}/{ctxRes.ReconApBudget:0.#})");
            }
        }

        private static float ActorCost(WorldSnapshot snap, MissionProposal m, ArmySnapshot mover)
        {
            if (mover == null || !(m.Target is ScoutMissionTarget target))
                return m.Requirements?.ApDesired ?? 0f;
            HexCoord costHex = ExecutionHexFor(snap, mover, target);
            return ScoutCostModel.PairCost(snap, mover, costHex, target.Stealth == StealthRequirement.Required).RequiredAp;
        }

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

        private static ArmySnapshot Snapshot(WorldSnapshot snap, int armyId) =>
            snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == armyId);

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
