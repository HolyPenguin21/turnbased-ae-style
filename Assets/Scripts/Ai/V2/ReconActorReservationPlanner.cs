using System.Collections.Generic;
using System.Linq;
using Game.Players;
using Game.Map;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-RECON-01 — ACTOR-AWARE RECON PLANNING & RESERVATION
    // ===========================================================================================
    //  The failure this closes: create 3 Recon jobs -> the allocator funds 3 -> ProvisioningManager
    //  discovers two of them relied on the same solo Recce -> MoverContended -> a wasted re-pack
    //  iteration (and, at the iteration bound, a silently dropped lane). Actor availability must be
    //  part of PLANNING, before funding — not a provisioning-time discovery.
    //
    //  This stage runs AFTER MissionLayer.Propose (proposals priced by ScoutPricingWitness) and
    //  BEFORE MissionContinuityLayer.BindFunding / ResourceAllocator:
    //
    //    Generate -> Deduplicate -> Determine eligible actors -> Match actor -> Reserve actor
    //    -> (allocator reserves budget -> funds -> ProvisioningManager provisions an ALREADY
    //        actor-bound mission)
    //
    //  It never spends a resource and never mutates game state. It only (a) drops Recon proposals
    //  that have no distinct eligible scout or that exceed the still-unmet concurrency, and
    //  (b) stamps MissionProposal.ReservedMoverArmyId so ProvisioningManager restricts that
    //  mission's assignment to the reserved actor. ProvisioningManager.PrepareScoutAssignments
    //  stays the authoritative N-way injective solver; if the reserved actor has become unusable
    //  by provisioning time it rematches across free scouts and MoverContended remains as the
    //  defensive runtime outcome for genuine unexpected state change (spec §7).
    // ===========================================================================================
    internal static class ReconActorReservationPlanner
    {
        public static void Plan(WorldSnapshot snap, AiTurnContext ctx, PlayerSetupData player,
            List<MissionProposal> missions, ActorCommitments actorCommitments,
            IReadOnlyList<MissionIntent> activeIntents, IReadOnlyList<ReconObjective> frozenObjectives)
        {
            if (missions == null || missions.Count == 0 || snap?.Self?.Armies == null)
                return;

            // 1. Recon proposals only, deduplicated by ReconJobKey. MissionIntentKey already encodes
            //    (Requirement/IntentType via SubKind) + (ObjectiveHexOrRegion via Q,R) + (TargetId
            //    via ObjectiveId), so Explore(H) and Refresh(H) are never merged and two identical
            //    Scout(Explore H) collapse to one job.
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

            // 2. Planning reservation context. Seed reserved actor ids with everything already hard-
            //    committed this turn: mission-continuity movers + actors claimed by any other lane
            //    (raid hosts, defence bodies, …). ActorCommitments is that normalized view.
            var reserved = new HashSet<int>(actorCommitments?.ClaimedArmyIds ?? System.Array.Empty<int>());

            // Distinct scouts already executing a durable Recon role — they count toward concurrency
            // but are not re-reserved here (their mover is already in `reserved`).
            var activeReconActors = new HashSet<int>();
            if (activeIntents != null && actorCommitments != null)
                foreach (MissionIntent i in activeIntents)
                    if (i?.Scout != null && i.PreferredMoverArmyId.HasValue
                        && actorCommitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        activeReconActors.Add(i.PreferredMoverArmyId.Value);
            int activeReconExecutions = activeReconActors.Count;

            // 3. A real candidate list per job: capability fits AND operational AND not reserved AND
            //    can actually take a safe first step toward the objective this turn AND its
            //    activation AP is spendable. ScoutMoverSelector.Rank already applies the eligibility
            //    filter (solo Recce, not spent, stealth-capable when required) and the deterministic
            //    activation-AP -> ETA -> distance -> id ranking; add the first-step executability
            //    gate ProvisioningManager also enforces so an impossible Refresh cannot reserve a
            //    scout that would then fail NoExecutableStep.
            var eligible = new Dictionary<MissionProposal, List<ScoutMoverCandidate>>();
            foreach (MissionProposal m in scoutMissions)
            {
                var target = (ScoutMissionTarget)m.Target;
                eligible[m] = ScoutMoverSelector.Rank(snap, target, reserved)
                    .Where(c => CanExecute(ctx, player, snap, c.Army, target))
                    .ToList();
            }

            // 4. Assign scarce jobs first: strategic priority (AdmissionRank — the planner-local
            //    LocalAdmissionScore + step-7 retarget hysteresis) DESC, then fewest eligible actors
            //    ASC (a specialist-only job takes its actor before an abundant one), then stable key.
            scoutMissions.Sort((a, b) =>
            {
                int c = MissionAdmissionPolicy.AdmissionRank(b).CompareTo(MissionAdmissionPolicy.AdmissionRank(a));
                if (c != 0) return c;
                c = eligible[a].Count.CompareTo(eligible[b].Count);
                if (c != 0) return c;
                return StableMissionKey.For(a).CompareTo(StableMissionKey.For(b));
            });

            // 6. Recompute concurrency room as actors are assigned. Fresh (non-incumbent) lanes are
            //    only worth reserving up to the still-unmet desired concurrency; a durable incumbent
            //    is always kept (continuity owns its retirement).
            int desiredTotal = DesiredReconConcurrency(snap, frozenObjectives);
            int freshRoom = Mathf.Max(0, desiredTotal - activeReconExecutions);
            int freshReserved = 0;

            var drop = new List<MissionProposal>();
            foreach (MissionProposal m in scoutMissions)
            {
                bool incumbent = m.FromDurableIntent;

                ScoutMoverCandidate? pick = null;
                foreach (ScoutMoverCandidate c in eligible[m])
                {
                    if (reserved.Contains(c.Army.ArmyId))
                        continue;
                    // Honour an already-preferred mover (durable incumbent's carried scout, or the
                    // pricing witness's soft pick) when it is still free and eligible.
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
                    if (incumbent)
                    {
                        AiDebugLog.Write($"[AI][V2][ReconActor] {StableMissionKey.For(m)} incumbent kept unreserved "
                            + "— no free eligible scout; provisioning resolves it or reports MoverContended");
                        continue;
                    }
                    drop.Add(m);
                    AiDebugLog.Write($"[AI][V2][ReconActor] drop {StableMissionKey.For(m)} — no eligible unreserved scout "
                        + $"(reserved {reserved.Count})");
                    continue;
                }

                if (!incumbent && freshReserved >= freshRoom)
                {
                    drop.Add(m);
                    AiDebugLog.Write($"[AI][V2][ReconActor] drop {StableMissionKey.For(m)} — recon concurrency already met "
                        + $"(desired {desiredTotal}, active {activeReconExecutions}, freshReserved {freshReserved})");
                    continue;
                }

                int actorId = pick.Value.Army.ArmyId;
                reserved.Add(actorId);
                m.ReservedMoverArmyId = actorId;
                m.PreferredMoverArmyId = actorId;
                if (!incumbent)
                    freshReserved++;
                AiDebugLog.Write($"[AI][V2][ReconActor] reserve {StableMissionKey.For(m)} -> #{actorId} "
                    + $"(eligible {eligible[m].Count}, incumbent {(incumbent ? 1 : 0)}, "
                    + $"las {MissionAdmissionPolicy.AdmissionRank(m):0.00})");
            }

            foreach (MissionProposal m in drop)
                missions.Remove(m);

            AiDebugLog.Write($"[AI][V2][ReconActor] plan — scoutJobs={scoutMissions.Count} activeExec={activeReconExecutions} "
                + $"desired={desiredTotal} reserved={freshReserved} dropped={drop.Count}");
        }

        // Mirror of ProvisioningManager.BuildExecutionCandidates' per-actor executability gate: for
        // Explore/Refresh the mover must be able to take a safe first step toward the focus this
        // turn; for Surveil it must have an on-map vantage within vision that it can start toward.
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

        private static int DesiredReconConcurrency(WorldSnapshot snap, IReadOnlyList<ReconObjective> frozenObjectives)
        {
            if (frozenObjectives == null || frozenObjectives.Count == 0)
                return Mathf.Max(1, ReconConcurrencyPolicy.HardCap);
            var runnable = frozenObjectives
                .Where(o => o != null && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            return Mathf.Max(1, ReconConcurrencyPolicy.DesiredTotal(snap, runnable));
        }

        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);
    }
}
