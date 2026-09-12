using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ReconDemands and its Recon-only private helper (ReconKey).
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
    public static partial class DemandLayer
    {
        private static IEnumerable<AxisDemand> ReconDemands(WorldSnapshot snap,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player, AiTurnContext ctx, PlayerRoot root = null)
        {
            if (snap?.Self?.Armies == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Recon] decision=NONE reason=no_self_army_snapshot");
                yield break;
            }
            if (objectives == null || objectives.Count == 0)
            {
                AiDebugLog.Write("[AI][V2][Demand][Recon] decision=NONE reason=no_frozen_recon_objectives");
                yield break;
            }

            // Spec §1/§10 — concurrency is counted from DISTINCT valid physical scout actors, never
            // from raw MissionIntent rows. Even if continuity is momentarily corrupted (two durable
            // intents pointing at one actor), that scout must still count as exactly one execution,
            // so a legitimately needed replacement scout is not suppressed. The duplicate itself is
            // surfaced as a [CHECK][ERROR] by MissionContinuityLayer.ResolveActive.
            var coveredKeys = new HashSet<MissionIntentKey>();
            var activeReconActors = new HashSet<int>();
            var activeGroundReconActors = new HashSet<int>();
            var ownById = (snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).ToDictionary(a => a.ArmyId, a => a);
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        continue;
                    coveredKeys.Add(i.IntentKey);
                    activeReconActors.Add(i.PreferredMoverArmyId.Value);
                    if (ownById.TryGetValue(i.PreferredMoverArmyId.Value, out ArmySnapshot actor)
                        && !actor.IsAir)
                        activeGroundReconActors.Add(actor.ArmyId);
                }
            int activeReconExecutions = activeReconActors.Count;
            int activeGroundReconExecutions = activeGroundReconActors.Count;

            var uncovered = objectives
                .Where(o => o.BaseValue > 0f && !coveredKeys.Contains(o.IntentKey))
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            if (uncovered.Count == 0)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=SATISFIED reason=all_objectives_covered "
                    + $"objectives={objectives.Count} active={activeReconExecutions}");
                yield break;
            }

            AiAllocatorState cooldownState = AiAllocatorStateRegistry.GetOrCreate(player);
            int turn = snap.TurnNumber;
            var runnable = new List<ReconObjective>(uncovered.Count);
            int blocked = 0;
            foreach (ReconObjective o in uncovered)
            {
                StableMissionKey key = ReconKey(o);
                if (cooldownState.TryGetCooldown(key, turn, out MissionCooldownInfo cd))
                {
                    blocked++;
                    AiDebugLog.Write($"[AI][V2][Demand][Recon] blocked {key} reason={cd.Reason} "
                        + $"start=t{cd.StartedTurn} until=t{cd.UntilTurn} remaining={cd.RemainingAt(turn)}");
                    continue;
                }
                runnable.Add(o);
            }

            AiDebugLog.Write($"[AI][V2][Demand][Recon] jobs raw={objectives.Count} covered={coveredKeys.Count} "
                + $"uncovered={uncovered.Count} blocked={blocked} runnable={runnable.Count} active={activeReconExecutions}");
            if (runnable.Count == 0)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=DEFER reason=all_uncovered_objectives_on_cooldown "
                    + $"uncovered={uncovered.Count} blocked={blocked} runnable=0");
                yield break;
            }

            // AI-RECON-02 — unified recon capacity. Observation lanes (Refresh / Surveil) may be
            // covered by ground scouts, launched wings that can still fly, airborne recon wings, or
            // a launchable hangar aircraft; ground-traversal lanes (Explore — a physical visit) only
            // by ground actors. A new Scout is materialised only when a USABLE, requirement-scoped
            // deficit has persisted (spec §7), never merely because Recon desire is high. Stealth
            // objectives are their own lane — neither aviation nor a generic scout can serve them.
            bool IsStealthObjective(ReconObjective o) =>
                o != null && (o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);

            var observationRunnable = runnable.Where(o => o.Kind != ReconObjectiveKind.Explore).ToList();
            var groundVisitRunnable = runnable.Where(o => o.Kind == ReconObjectiveKind.Explore).ToList();
            var stealthRunnable = runnable.Where(IsStealthObjective).ToList();
            var stealthObsRunnable = stealthRunnable.Where(o => o.Kind != ReconObjectiveKind.Explore).ToList();
            var stealthGroundRunnable = stealthRunnable.Where(o => o.Kind == ReconObjectiveKind.Explore).ToList();

            // RECON-AIR-02 (round 5) — DemandLayer's ONLY calls for Recon capacity, both ground and
            // air, are to ReconAssignmentPlanner (the one canonical Assignment/capacity owner). The
            // air witness is measured first because ReconCapacitySnapshot.Build needs it as an INPUT
            // to size its own Desired/deficit fields (see MeasureAirCapacity's header comment).
            (int airborneWitnessed, int spareLaunchWitnessed) = ReconAssignmentPlanner.MeasureAirCapacity(
                ctx, player, root, snap, objectives, activeIntents, commitments);
            ReconCapacitySnapshot capacity = ReconCapacitySnapshot.Build(
                snap, observationRunnable, groundVisitRunnable, activeIntents, commitments, player,
                airborneWitnessed, spareLaunchWitnessed);
            AiDebugLog.Write($"[AI][V2][Demand][Recon] capacity {capacity.Explain} "
                + $"active={activeReconExecutions} hard={ReconConcurrencyPolicy.HardCap} "
                + $"runnable={runnable.Count} (obs={observationRunnable.Count} groundVisit={groundVisitRunnable.Count} "
                + $"stealth={stealthRunnable.Count}) blocked={blocked}");

            // --- Stealth lane: its own value/coverage estimate vs free stealth-capable movers. Not
            //     persistence-gated (a stealth job with no stealth actor is a real capability gap,
            //     not stage flicker) and not reduced by aviation or generic scouts.
            // §5 — Demand knows only the aggregate ReconAssignmentPlanner reports, never
            // ScoutMoverSelector's own eligibility rule (that rule belongs to Assignment alone).
            // Round 3 (Problem 3) — the stealth free-actor count is now the SAME joint witness
            // MeasureCapacity produces for everything else (see `witness` below), not a second raw
            // ReconAssignmentPlanner.CountEligibleMovers count with no job-matching behind it.
            int desiredStealthLanes = Mathf.Min(stealthRunnable.Count,
                ReconConcurrencyPolicy.DesiredForClass(snap, stealthObsRunnable,
                    ReconConcurrencyPolicy.ReconCoverageClass.Observation)
                + ReconConcurrencyPolicy.DesiredForClass(snap, stealthGroundRunnable,
                    ReconConcurrencyPolicy.ReconCoverageClass.GroundTraversal));

            // --- "Usable capacity" witness. A raw actor COUNT (GroundTraversalSupply/
            //     ObservationSupply) is not proof of executable work: an idle solo Recce can still be
            //     unable to reach any runnable objective (blocked path, no reachable Surveil vantage),
            //     which only ReconAssignmentPlanner.CanExecute actually knows via
            //     SafeStepPathing / SurveilVantageSelector. A durable lane actor is re-validated
            //     against its OWN current committed target (its path was only proven valid when the
            //     lane started — it may since have spent its MP/AP or lost the path). An idle,
            //     uncommitted actor only counts if a single JOINT bipartite matching across BOTH
            //     Ground and Observation runnable jobs (ReconAssignmentPlanner.MeasureCapacity) can
            //     assign it a DISTINCT reachable job — matching Ground and Observation independently
            //     would double-count any
            //     idle ground scout reachable to jobs of both classes as capacity for both at once,
            //     when physically it can only ever serve one.
            //
            //     This witnessed count — not the raw one — is what both the persistence-streak
            //     registry and the Rule-1/Rule-2 math below are computed against: a raw non-zero
            //     supply that is actually unreachable must produce the same EFFECTIVE deficit as if
            //     the actor did not exist at all, or a scout that already exists on paper but can
            //     never physically act would silently zero out the very deficit this gate exists to
            //     detect (regression: 1 unreachable existing scout, desired=1 => raw deficit reads 0,
            //     nothing would ever be created without this).
            // GENERIC only (mirrors ReconCapacitySnapshot's own obsGeneric/groundGeneric filtering) —
            // a stealth-required job must never be satisfiable by matching a plain, non-stealth actor
            // against it just because CanExecute proves a path exists; CanExecute checks reachability,
            // not the mover's stealth capability, so an unfiltered list would let a stealth-only
            // requirement quietly count as covered by generic capacity.
            var groundVisitGeneric = groundVisitRunnable.Where(o => !IsStealthObjective(o)).ToList();
            var observationGeneric = observationRunnable.Where(o => !IsStealthObjective(o)).ToList();
            // §5/§9 — the ONE read-only aggregate query Demand is allowed to ask Assignment. Demand
            // does not know (and must not know) HOW the actor<->job matching behind this number was
            // produced — see ReconAssignmentPlanner.MeasureCapacity.
            ReconCapacityMeasurement witness = ReconAssignmentPlanner.MeasureCapacity(ctx, player, snap, capacity,
                activeIntents, commitments, groundVisitGeneric, observationGeneric,
                stealthGroundRunnable, stealthObsRunnable);
            int groundWitnessedSupply = witness.GroundLaneWitnessed + witness.GroundIdleWitnessed;
            int obsWitnessedSupply = witness.ObsLaneWitnessed
                + capacity.AirborneReconLanes + capacity.SpareAirObservationSorties + witness.ObsIdleWitnessed;
            int groundEffectiveDeficit =
                Mathf.Max(0, capacity.DesiredGroundTraversalConcurrency - groundWitnessedSupply);
            int obsEffectiveDeficit =
                Mathf.Max(0, capacity.DesiredObservationConcurrency - obsWitnessedSupply);

            int stealthFree = witness.StealthGroundWitnessed + witness.StealthObsWitnessed;
            int missStealth = Mathf.Max(0, desiredStealthLanes - stealthFree);

            // --- Generic (non-stealth) capacity deficits, persistence-gated. Fed the EFFECTIVE
            //     (witnessed) deficit, not the raw one — see above.
            bool obsPersist = ReconCapacityDeficitRegistry.RegisterAndCheck(
                player, turn, ReconDeficitKind.Observation, obsEffectiveDeficit, out int obsStreak);
            bool groundPersist = ReconCapacityDeficitRegistry.RegisterAndCheck(
                player, turn, ReconDeficitKind.GroundTraversal, groundEffectiveDeficit, out int groundStreak);

            // --- Rule 1 (persistence-gate spec) — Zero-Capacity Bootstrap. A real runnable
            //     opportunity that NO usable actor of the required class can currently serve at all
            //     must get at least its first unit of capacity right away — persistence exists to
            //     stop churny re-materialization of ADDITIONAL capacity, not to starve an axis that
            //     has nothing usable whatsoever. Scoped per class: Observation counts air supply too
            //     (an idle helicopter means Observation is not zero-capacity even with 0 ground
            //     actors), GroundTraversal never does (aviation cannot substitute a physical visit).
            int groundBootstrap = groundWitnessedSupply == 0 && groundVisitRunnable.Count > 0
                ? Mathf.Min(1, groundEffectiveDeficit) : 0;
            int obsBootstrap = obsWitnessedSupply == 0 && observationRunnable.Count > 0
                ? Mathf.Min(1, obsEffectiveDeficit) : 0;

            int obsNew = obsBootstrap + (obsPersist ? Mathf.Max(0, obsEffectiveDeficit - obsBootstrap) : 0);
            int groundNew = groundBootstrap + (groundPersist ? Mathf.Max(0, groundEffectiveDeficit - groundBootstrap) : 0);
            if (groundBootstrap > 0)
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=PROMOTE previousGate=persistence "
                    + $"reason=zero_capacity_bootstrap class=GroundTraversal runnable={groundVisitRunnable.Count} "
                    + $"rawSupply={capacity.GroundTraversalSupply} witnessedSupply={groundWitnessedSupply} "
                    + $"rawDeficit={capacity.GroundTraversalDeficit} effectiveDeficit={groundEffectiveDeficit} "
                    + $"bootstrapped={groundBootstrap}");
            if (obsBootstrap > 0)
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=PROMOTE previousGate=persistence "
                    + $"reason=zero_capacity_bootstrap class=Observation runnable={observationRunnable.Count} "
                    + $"rawSupply={capacity.ObservationSupply} witnessedSupply={obsWitnessedSupply} "
                    + $"rawDeficit={capacity.ObservationDeficit} effectiveDeficit={obsEffectiveDeficit} "
                    + $"bootstrapped={obsBootstrap}");

            // --- Shared room. HardCap bounds concurrent GROUND scouts; the scarcer stealth need is
            //     served first.
            int roomForNew = Mathf.Max(0, ReconConcurrencyPolicy.HardCap - activeGroundReconExecutions);
            int stealthNew = Mathf.Min(missStealth, roomForNew);

            // A persisted GroundTraversal deficit is a HARD FLOOR: aviation can never substitute for
            // a physical visit, so it must produce scouts no matter how much air observation
            // capacity exists. Only the OBSERVATION portion is trimmed by the global useful-generic-
            // concurrency ceiling, and that ceiling is measured against GROUND capacity already in
            // hand only — air is not interchangeable with a ground lane (review round 4, P0).
            int usefulGenericRoom = Mathf.Max(0,
                capacity.CombinedDesiredConcurrency - capacity.ExistingGroundUsableCapacity);
            int groundPart = groundNew;
            int obsPart = Mathf.Min(obsNew, Mathf.Max(0, usefulGenericRoom - groundPart));
            int genericNew = Mathf.Min(groundPart + obsPart, Mathf.Max(0, roomForNew - stealthNew));
            // Split the materialised count back onto its two requirement classes (ground floor
            // first) so each emitted demand carries a TargetHex / ScoutContext / Value that
            // actually matches the deficit it is being created for (review round 5).
            int matGround = Mathf.Min(groundPart, genericNew);
            int matObs = genericNew - matGround;

            // --- Rule 2 (persistence-gate spec) escape candidates. The part of each class's raw
            //     deficit that is real (beyond the Rule-1 bootstrap unit) but has not yet persisted
            //     long enough is not simply dropped: it is carried forward as a PERSISTENCE-DEFERRED
            //     demand, room-bounded exactly like a normal materialisation would be, so
            //     StrategicPhaseA's reconciliation pass can still promote it later THIS turn if it
            //     turns out there is no other actionable work worth preferring over it.
            int roomLeftForDeferred = Mathf.Max(0, roomForNew - stealthNew - genericNew);
            int groundResidualUnpersisted = groundPersist ? 0
                : Mathf.Max(0, groundEffectiveDeficit - groundBootstrap);
            int obsResidualUnpersisted = obsPersist ? 0
                : Mathf.Max(0, obsEffectiveDeficit - obsBootstrap);
            int groundDeferred = Mathf.Min(groundResidualUnpersisted, roomLeftForDeferred);
            int obsDeferred = Mathf.Min(obsResidualUnpersisted, Mathf.Max(0, roomLeftForDeferred - groundDeferred));

            const float reconFixedOverheadAp = 0f;

            if (stealthNew <= 0 && genericNew <= 0)
            {
                string reason =
                    missStealth > 0 || obsEffectiveDeficit > 0 || groundEffectiveDeficit > 0
                        ? (obsNew + groundNew == 0 && missStealth == 0
                            ? "capacity_deficit_not_yet_persistent"
                            : "concurrency_hard_cap_or_useful_ceiling_reached")
                        : "usable_capacity_covers_all_lanes";
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=DEFER reason={reason} "
                    + $"obsDeficit(effective)={obsEffectiveDeficit}(raw={capacity.ObservationDeficit} "
                    + $"persist={(obsPersist ? 1 : 0)} streak={obsStreak}) "
                    + $"groundTraversalDeficit(effective)={groundEffectiveDeficit}(raw={capacity.GroundTraversalDeficit} "
                    + $"persist={(groundPersist ? 1 : 0)} streak={groundStreak}) "
                    + $"missStealth={missStealth} stealthFree={stealthFree} active={activeReconExecutions} "
                    + $"activeGround={activeGroundReconExecutions} "
                    + $"hard={ReconConcurrencyPolicy.HardCap} combinedCeiling={capacity.CombinedDesiredConcurrency} "
                    + $"existingGroundUsable={capacity.ExistingGroundUsableCapacity} usefulGenericRoom={usefulGenericRoom} "
                    + $"groundFloor={groundNew} roomForNew={roomForNew} blocked={blocked} "
                    + $"deferredCandidates=(ground={groundDeferred},obs={obsDeferred})");
            }

            if (groundDeferred > 0)
            {
                ReconObjective best = groundVisitRunnable.FirstOrDefault(o => !IsStealthObjective(o))
                    ?? groundVisitRunnable.FirstOrDefault() ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=DEFER reason=capacity_deficit_not_yet_persistent "
                    + $"class=GroundTraversal persistenceDeferredEmitted=true desired={groundDeferred} "
                    + $"runnable={groundVisitRunnable.Count} witnessedSupply={groundWitnessedSupply} "
                    + $"streak={groundStreak}");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = groundDeferred,
                    RequiredTraits = TraitPreference.None,
                    PreferredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    IsPersistenceDeferred = true,
                    Explain = $"GroundTraversal effective deficit {groundEffectiveDeficit} not yet persistent "
                        + $"(streak {groundStreak}); {groundVisitRunnable.Count} runnable job(s); "
                        + "deferred pending no-alternative-work reconciliation",
                };
            }

            if (obsDeferred > 0)
            {
                ReconObjective best = observationRunnable.FirstOrDefault(o => !IsStealthObjective(o))
                    ?? observationRunnable.FirstOrDefault() ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=DEFER reason=capacity_deficit_not_yet_persistent "
                    + $"class=Observation persistenceDeferredEmitted=true desired={obsDeferred} "
                    + $"runnable={observationRunnable.Count} witnessedSupply={obsWitnessedSupply} "
                    + $"streak={obsStreak}");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = obsDeferred,
                    RequiredTraits = TraitPreference.None,
                    PreferredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    IsPersistenceDeferred = true,
                    Explain = $"Observation effective deficit {obsEffectiveDeficit} not yet persistent "
                        + $"(streak {obsStreak}); {observationRunnable.Count} runnable job(s); "
                        + "deferred pending no-alternative-work reconciliation",
                };
            }

            if (stealthNew > 0)
            {
                ReconObjective best = stealthRunnable.FirstOrDefault() ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=stealth desired={stealthNew} reason=insufficient_free_stealth_scouts "
                    + $"jobs={stealthRunnable.Count} desiredLanes={desiredStealthLanes} free={stealthFree} "
                    + $"runnable={runnable.Count} blocked={blocked} target=({best.FocusHex.Q},{best.FocusHex.R})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = stealthNew,
                    RequiredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    Explain = $"{stealthRunnable.Count} stealth job(s), {desiredStealthLanes} wanted, "
                        + $"{stealthFree} stealth scout(s) free, miss {stealthNew}; blocked {blocked}",
                };
            }

            if (matGround > 0)
            {
                ReconObjective best = groundVisitRunnable.FirstOrDefault(o => !IsStealthObjective(o))
                    ?? groundVisitRunnable.FirstOrDefault() ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=generic-ground desired={matGround} reason=persistent_ground_traversal_deficit "
                    + $"groundTraversalDeficit(effective)={groundEffectiveDeficit}(streak={groundStreak}) "
                    + $"combinedCeiling={capacity.CombinedDesiredConcurrency} existingGroundUsable={capacity.ExistingGroundUsableCapacity} "
                    + $"matGround={matGround} matObs={matObs} runnable={runnable.Count} blocked={blocked} "
                    + $"target=({best.FocusHex.Q},{best.FocusHex.R})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = matGround,
                    RequiredTraits = TraitPreference.None,
                    PreferredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    Explain = $"persistent GroundTraversal effective deficit {groundEffectiveDeficit} "
                        + $"(aviation cannot substitute a physical visit); want {matGround}; blocked {blocked}",
                };
            }

            if (matObs > 0)
            {
                ReconObjective best = observationRunnable.FirstOrDefault(o => !IsStealthObjective(o))
                    ?? observationRunnable.FirstOrDefault() ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=generic-observation desired={matObs} reason=persistent_observation_deficit "
                    + $"obsDeficit(effective)={obsEffectiveDeficit}(streak={obsStreak}) "
                    + $"airborneAir={capacity.AirborneReconLanes} spareAir={capacity.SpareAirObservationSorties} "
                    + $"combinedCeiling={capacity.CombinedDesiredConcurrency} existingGroundUsable={capacity.ExistingGroundUsableCapacity} "
                    + $"matGround={matGround} matObs={matObs} runnable={runnable.Count} blocked={blocked} "
                    + $"target=({best.FocusHex.Q},{best.FocusHex.R})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = matObs,
                    RequiredTraits = TraitPreference.None,
                    PreferredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    Explain = $"persistent Observation effective deficit {obsEffectiveDeficit} "
                        + $"(net of airborne {capacity.AirborneReconLanes} + spare air {capacity.SpareAirObservationSorties}); "
                        + $"want {matObs}; blocked {blocked}",
                };
            }
        }

        // Round 8 (P1) — thin wrapper over the canonical AggressionDemandEvaluator. The whole
        // admission / selection / shortage contract now lives in ONE primitive shared with
        // StrategicReactionPass, so the reaction probe can never disagree with the real pipeline.
        // This wrapper only replays the evaluator's diagnostics and yields its demands into the
        // pipeline stream (where trace ids are attached).
        private static StableMissionKey ReconKey(ReconObjective o) =>
            new StableMissionKey(MissionKind.Scout,
                o.Kind == ReconObjectiveKind.Surveil ? (int)ScoutTargetKind.Surveil : (int)ScoutTargetKind.Explore,
                o.Kind == ReconObjectiveKind.Surveil ? o.ContactArmyId : 0,
                o.FocusHex.Q, o.FocusHex.R);
    }
}
