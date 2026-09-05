using System.Collections.Generic;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public static class DemandLayer
    {
        public static List<AxisDemand> Generate(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<AggressionObjective> aggressionObjectives,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player, AiTurnContext ctx = null)
        {
            var demands = new List<AxisDemand>();
            // §17 — decay the resource-starvation feedback once per turn before it is read.
            if (player != null && snap != null)
                ResourceStarvationRegistry.DecayOncePerTurn(player, snap.TurnNumber);
            demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player, ctx));
            demands.AddRange(AggressionDemands(snap, breakdown, aggressionObjectives, activeIntents, commitments, player));
            demands.AddRange(DefenceDemands(snap, breakdown));
            demands.AddRange(EconomyDemands(snap, breakdown, player));
            demands.AddRange(DevelopmentDemands(snap, breakdown));
            // AI-MGR-01 — radar-independent standing-force pull. Emitted LAST so it can see whether
            // an Aggression / Defence combat demand already covers the same ground this pass.
            demands.AddRange(BaselineForceReadinessDemands(snap, player, commitments, demands));
            // Correlation: one DemandTraceId per demand for this pass, in deterministic list order
            // (AiV2Trace scope was opened by the orchestrator). Rides on AxisDemand.TraceId /
            // ToString from here — into Phase A and every [CHECK] line raised for the demand.
            V2TraceScope scope = AiV2Trace.CurrentScope(player);
            foreach (AxisDemand d in demands)
                if (d != null && string.IsNullOrEmpty(d.TraceId))
                    d.TraceId = scope?.NextDemandId() ?? "?";
            foreach (AxisDemand d in demands)
                AiDebugLog.Write($"[AI][V2]   demand — {d} | {d.Explain}");
            return demands;
        }

        // ---------------------------------------------------------------------------------------
        //  AI-MGR-01 — BaselineForceReadiness (spec §4). NOT a threat response and NOT a radar
        //  desire: the AI must continuously keep a reasonable standing potential for future tasks.
        //  Emits at most ONE low-priority FieldCombatPower demand so an ordinary combat unit gets
        //  Phase-A pull instead of only Phase-B surplus. Charged to the Defence entitlement (a
        //  standing field force is latent defence). Suppressed whenever an Aggression/Defence
        //  combat demand already exists this pass, when Need is below the threshold, or when the
        //  AI already fields enough free field power and combat actors. It only decides a card is
        //  worth materialising — never which army/garrison it joins (that stays Housekeeping's).
        // ---------------------------------------------------------------------------------------
        private static IEnumerable<AxisDemand> BaselineForceReadinessDemands(WorldSnapshot snap,
            PlayerSetupData player, ActorCommitments commitments, IReadOnlyList<AxisDemand> already)
        {
            if (snap?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Baseline] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            bool combatDemandExists = already != null && already.Any(d => d != null
                && (d.Capability == CapabilityKind.FieldCombatPower || d.Capability == CapabilityKind.Hero
                    || d.Capability == CapabilityKind.GarrisonCombatPower));
            if (combatDemandExists)
            {
                AiDebugLog.Write("[AI][V2][Demand][Baseline] decision=SATISFIED reason=combat_demand_already_raised");
                yield break;
            }

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            BaselineForceReadiness r = BaselineForceReadiness.Evaluate(snap, inv, snap.Self?.Hand);

            if (r.Need < AiConfigV2.baselineReadinessDemandMinNeed)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Baseline] decision=SATISFIED reason=need_below_threshold "
                    + $"need={r.Need:0.00} min={AiConfigV2.baselineReadinessDemandMinNeed:0.00} "
                    + $"actors={r.CombatActors} freeFieldPower={r.FreeFieldPower:0.#} "
                    + $"hasBody={(r.HasFieldBody ? 1 : 0)} hasHero={(r.HasHero ? 1 : 0)}");
                yield break;
            }

            if (r.FreeFieldPower >= AiConfigV2.baselineReadinessSatisfiedPower
                && r.CombatActors >= AiConfigV2.baselineReadinessTargetActors)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Baseline] decision=SATISFIED reason=standing_force_sufficient "
                    + $"need={r.Need:0.00} actors={r.CombatActors}/{AiConfigV2.baselineReadinessTargetActors} "
                    + $"freeFieldPower={r.FreeFieldPower:0.#}/{AiConfigV2.baselineReadinessSatisfiedPower:0.#}");
                yield break;
            }

            // P1.7 — FLAT low Value. The Need >= min gate already decides the demand exists; Need
            // is priced once, downstream, in the evaluator's ForceGrowthValue. Scaling Value by
            // Need too (then Value x Plan.Score in arbitration) triple-counted the same signal.
            float value = AiConfigV2.baselineReadinessDemandValue;
            AiDebugLog.Write($"[AI][V2][Demand][Baseline] decision=CREATE capability=FieldCombatPower desired=1 "
                + $"need={r.Need:0.00} value={value:0.#} actors={r.CombatActors} freeFieldPower={r.FreeFieldPower:0.#} "
                + $"hasBody={(r.HasFieldBody ? 1 : 0)} hasHero={(r.HasHero ? 1 : 0)}");
            yield return new AxisDemand
            {
                RequestingAxis = DesireAxis.Defence,
                Capability = CapabilityKind.FieldCombatPower,
                DesiredAmount = 1,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = null,
                Value = value,
                Explain = $"baseline force readiness: need {r.Need:0.00} (actors {r.CombatActors}, "
                    + $"free field power {r.FreeFieldPower:0.#}, body {(r.HasFieldBody ? 1 : 0)}, "
                    + $"hero {(r.HasHero ? 1 : 0)}); maintain standing potential for future tasks",
            };
        }

        private static IEnumerable<AxisDemand> ReconDemands(WorldSnapshot snap,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player, AiTurnContext ctx)
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
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        continue;
                    coveredKeys.Add(i.IntentKey);
                    activeReconActors.Add(i.PreferredMoverArmyId.Value);
                }
            int activeReconExecutions = activeReconActors.Count;

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

            ReconCapacitySnapshot capacity = ReconCapacitySnapshot.Build(
                snap, observationRunnable, groundVisitRunnable, activeIntents, commitments, player,
                ReconAirReservationRegistry.ForTurn(player, snap.TurnNumber));
            AiDebugLog.Write($"[AI][V2][Demand][Recon] capacity {capacity.Explain} "
                + $"active={activeReconExecutions} hard={ReconConcurrencyPolicy.HardCap} "
                + $"runnable={runnable.Count} (obs={observationRunnable.Count} groundVisit={groundVisitRunnable.Count} "
                + $"stealth={stealthRunnable.Count}) blocked={blocked}");

            // --- Stealth lane: its own value/coverage estimate vs free stealth-capable movers. Not
            //     persistence-gated (a stealth job with no stealth actor is a real capability gap,
            //     not stage flicker) and not reduced by aviation or generic scouts.
            // §5 — Demand knows only the aggregate ReconAssignmentPlanner reports, never
            // ScoutMoverSelector's own eligibility rule (that rule belongs to Assignment alone).
            var claimed = commitments?.ClaimedArmyIdSet;
            int stealthFree = ReconAssignmentPlanner.CountEligibleMovers(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.Required }, claimed);
            int desiredStealthLanes = Mathf.Min(stealthRunnable.Count,
                ReconConcurrencyPolicy.DesiredForClass(snap, stealthObsRunnable,
                    ReconConcurrencyPolicy.ReconCoverageClass.Observation)
                + ReconConcurrencyPolicy.DesiredForClass(snap, stealthGroundRunnable,
                    ReconConcurrencyPolicy.ReconCoverageClass.GroundTraversal));
            int missStealth = Mathf.Max(0, desiredStealthLanes - stealthFree);

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
                activeIntents, commitments, groundVisitGeneric, observationGeneric);
            int groundWitnessedSupply = witness.GroundLaneWitnessed + witness.GroundIdleWitnessed;
            int obsWitnessedSupply = witness.ObsLaneWitnessed
                + capacity.AirborneReconLanes + capacity.SpareAirObservationSorties + witness.ObsIdleWitnessed;
            int groundEffectiveDeficit =
                Mathf.Max(0, capacity.DesiredGroundTraversalConcurrency - groundWitnessedSupply);
            int obsEffectiveDeficit =
                Mathf.Max(0, capacity.DesiredObservationConcurrency - obsWitnessedSupply);

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
            int roomForNew = Mathf.Max(0, ReconConcurrencyPolicy.HardCap - activeReconExecutions);
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
        private static IEnumerable<AxisDemand> AggressionDemands(WorldSnapshot snap, DesireBreakdown b,
            IReadOnlyList<AggressionObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player)
        {
            AggressionDemandEvaluation eval = AggressionDemandEvaluator.Build(
                snap, objectives, activeIntents, commitments, player);
            foreach (string line in eval.Diagnostics)
                AiDebugLog.Write(line);
            foreach (AxisDemand d in eval.Demands)
                yield return d;
        }

        private static StableMissionKey ReconKey(ReconObjective o) =>
            new StableMissionKey(MissionKind.Scout,
                o.Kind == ReconObjectiveKind.Surveil ? (int)ScoutTargetKind.Surveil : (int)ScoutTargetKind.Explore,
                o.Kind == ReconObjectiveKind.Surveil ? o.ContactArmyId : 0,
                o.FocusHex.Q, o.FocusHex.R);

        // ---------------------------------------------------------------------------------------
        //  DEF — a threatened Citadel/Base whose committed defence is below requirement. NEVER
        //  fires just because resources are free: it needs a real AssetThreatSnapshot above the
        //  severity trigger AND a saturation deficit. Existing garrison + own field armies already
        //  standing on the asset + defence bodies already requested earlier in THIS same call are
        //  all subtracted before a new demand is raised (spec §5).
        // ---------------------------------------------------------------------------------------
        private static IEnumerable<AxisDemand> DefenceDemands(WorldSnapshot s, DesireBreakdown b)
        {
            IReadOnlyList<AssetThreatSnapshot> threats = s?.Threat?.Threats;
            if (threats == null || threats.Count == 0 || s.Self?.Armies == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Defence] decision=NONE reason=no_asset_threats");
                yield break;
            }

            // Highest-severity threat per defended asset hex.
            var worst = new Dictionary<HexCoord, AssetThreatSnapshot>();
            foreach (AssetThreatSnapshot t in threats)
            {
                if (t?.Asset == null || t.Contact == null)
                    continue;
                if (t.Asset.Kind != AssetKind.Citadel && t.Asset.Kind != AssetKind.Base)
                    continue;
                if (t.Severity < AiConfigV2.defenceSeverityTrigger)
                    continue;
                if (!worst.TryGetValue(t.Asset.Hex, out AssetThreatSnapshot cur) || t.Severity > cur.Severity)
                    worst[t.Asset.Hex] = t;
            }
            if (worst.Count == 0)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Defence] decision=SATISFIED reason=no_threat_above_severity_trigger "
                    + $"trigger={AiConfigV2.defenceSeverityTrigger:0.##} threats={threats.Count}");
                yield break;
            }

            int emitted = 0;
            var plannedByHex = new Dictionary<HexCoord, float>();
            foreach (AssetThreatSnapshot t in worst.Values
                .OrderByDescending(x => x.Severity).ThenBy(x => x.Asset.Hex.Q).ThenBy(x => x.Asset.Hex.R))
            {
                if (emitted >= AiConfigV2.defenceMaxDemandsPerTurn)
                    break;

                HexCoord hex = t.Asset.Hex;
                float threateningPower = t.Contact.Army?.EffectiveArmyPower ?? 0f;
                float required = threateningPower * AiConfigV2.defenceReserveMargin;

                float existingGarrison = 0f, assignedField = 0f;
                foreach (ArmySnapshot a in s.Self.Armies)
                {
                    if (a == null || !a.Hex.Equals(hex)) continue;
                    if (a.IsGarrison) existingGarrison += a.EffectiveArmyPower;
                    else if (!a.IsAir && !a.IsPrison) assignedField += a.EffectiveArmyPower;
                }
                plannedByHex.TryGetValue(hex, out float planned);
                float available = existingGarrison + assignedField + planned;

                if (available + AiConfigV2.allocatorSliceEpsilon >= required)
                {
                    AiDebugLog.Write($"[AI][V2][Demand][Defence] decision=SATISFIED asset=({hex.Q},{hex.R}) "
                        + $"kind={t.Asset.Kind} severity={t.Severity:0.##} required={required:0.#} "
                        + $"available={available:0.#} (garrison={existingGarrison:0.#} field={assignedField:0.#} "
                        + $"planned={planned:0.#}) — saturated");
                    continue;
                }

                float deficit = required - available;
                int bodies = Mathf.Clamp(
                    Mathf.CeilToInt(deficit / Mathf.Max(1f, AiConfigV2.defencePerBodyPowerEstimate)),
                    1, AiConfigV2.defenceMaxBodiesPerAsset);
                plannedByHex[hex] = planned + bodies * AiConfigV2.defencePerBodyPowerEstimate;
                emitted++;

                AiDebugLog.Write($"[AI][V2][Demand][Defence] decision=CREATE asset=({hex.Q},{hex.R}) "
                    + $"kind={t.Asset.Kind} capability=GarrisonCombatPower desired={bodies} "
                    + $"severity={t.Severity:0.##} required={required:0.#} available={available:0.#} "
                    + $"deficit={deficit:0.#} (garrison={existingGarrison:0.#} field={assignedField:0.#})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Defence,
                    Capability = CapabilityKind.GarrisonCombatPower,
                    DesiredAmount = bodies,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = hex,
                    RequiredCapabilityPower = required,
                    Value = Mathf.Clamp01(t.Severity) * 100f,
                    Explain = $"{t.Asset.Kind} @({hex.Q},{hex.R}) under threat sev {t.Severity:0.##}: "
                        + $"need ~{required:0.#} defence, have {available:0.#} "
                        + $"(garrison {existingGarrison:0.#} + field {assignedField:0.#}); request {bodies} body(s)",
                };
            }

            if (emitted == 0)
                AiDebugLog.Write("[AI][V2][Demand][Defence] decision=SATISFIED reason=all_threatened_assets_saturated");
        }

        // ---------------------------------------------------------------------------------------
        //  ECO — a resource type whose recurring income is BELOW the sustainable target AND a
        //  known unbuilt resource hex exists for it. The target combines own deck/card cadence
        //  with opponent income; this layer only emits work when a concrete site is actionable.
        //  One demand at a time.
        // ---------------------------------------------------------------------------------------
        private static IEnumerable<AxisDemand> EconomyDemands(WorldSnapshot s, DesireBreakdown b, PlayerSetupData player)
        {
            IReadOnlyList<KeyValuePair<HexCoord, ResourceType>> resourceHexes = s?.Known?.ResourceHexes;
            if (resourceHexes == null || resourceHexes.Count == 0 || s.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Economy] decision=NONE reason=no_known_resource_hexes");
                yield break;
            }

            var knownBuilt = new HashSet<HexCoord>();
            if (s.Known.Buildings != null)
                foreach (AiMapMemory.KnownBuilding kb in s.Known.Buildings)
                    knownBuilt.Add(kb.Hex);

            int emitted = 0;
            var seenTypes = new HashSet<ResourceType>();
            foreach (KeyValuePair<HexCoord, ResourceType> rh in resourceHexes
                .OrderBy(x => x.Key.Q).ThenBy(x => x.Key.R))
            {
                if (emitted >= AiConfigV2.economyMaxDemandsPerTurn)
                    break;
                if (knownBuilt.Contains(rh.Key) || seenTypes.Contains(rh.Value))
                    continue;

                bool hasIncome = HasIncomeFor(s, rh.Value);
                if (hasIncome)
                    continue;
                seenTypes.Add(rh.Value);
                emitted++;

                AiDebugLog.Write($"[AI][V2][Demand][Economy] decision=CREATE hex=({rh.Key.Q},{rh.Key.R}) "
                    + $"resource={rh.Value} capability=EconomicInfrastructure desired=1 reason=income_below_target");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = CapabilityKind.EconomicInfrastructure,
                    DesiredAmount = 1,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = rh.Key,
                    EconomyResourceType = rh.Value,
                    Value = 55f,
                    Explain = $"{rh.Value} income {s.Self.PerTurnIncome.Get(rh.Value):0.##} below target "
                        + $"{s.Economy.IncomeTarget.Get(rh.Value):0.##}; known unbuilt site @({rh.Key.Q},{rh.Key.R})",
                };
            }

            if (emitted == 0)
            {
                // §17 — even with income targets covered, if AGG/RCN chains keep stalling for an
                // empty resource stock, value ONE known unbuilt extraction site for that resource.
                foreach (KeyValuePair<HexCoord, ResourceType> rh in resourceHexes
                    .OrderBy(x => x.Key.Q).ThenBy(x => x.Key.R))
                {
                    if (knownBuilt.Contains(rh.Key))
                        continue;
                    float pressure = ResourceStarvationRegistry.Pressure(player, rh.Value);
                    if (pressure < AiConfigV2.starvationEconomyTrigger)
                        continue;
                    float value = 40f + AiConfigV2.starvationEconomyValueBonus * Mathf.Clamp01(pressure);
                    AiDebugLog.Write($"[AI][V2][Demand][Economy] decision=CREATE hex=({rh.Key.Q},{rh.Key.R}) "
                        + $"resource={rh.Value} capability=EconomicInfrastructure desired=1 "
                        + $"reason=repeated_strategic_starvation pressure={pressure:0.##} value={value:0.#}");
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Economy,
                        Capability = CapabilityKind.EconomicInfrastructure,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = rh.Key,
                        EconomyResourceType = rh.Value,
                        Value = value,
                        Explain = $"{rh.Value} stock repeatedly starved strategic chains "
                            + $"(pressure {pressure:0.##}); known unbuilt site @({rh.Key.Q},{rh.Key.R})",
                    };
                    emitted++;
                    break;
                }
            }

            if (emitted == 0)
            {
                bool hasIncomeGap = ResourceBundle.All.Any(t => !HasIncomeFor(s, t));
                AiDebugLog.Write(hasIncomeGap
                    ? "[AI][V2][Demand][Economy] decision=NONE reason=income_gap_but_no_actionable_known_site"
                    : "[AI][V2][Demand][Economy] decision=SATISFIED reason=known_income_targets_covered_or_sites_built");
            }
        }

        // ---------------------------------------------------------------------------------------
        //  DEV — no Research/Production facility yet. A capability gap that blocks the whole
        //  Development axis downstream. One demand at a time.
        // ---------------------------------------------------------------------------------------
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b)
        {
            if (s?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_self_snapshot");
                yield break;
            }
            if (s.Self.HasDevFacility)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=SATISFIED reason=development_facility_exists");
                yield break;
            }
            if (s.Self.BaseHexes == null || s.Self.BaseHexes.Count == 0)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_base_to_expand");
                yield break;
            }

            HexCoord anchor = s.Self.BaseHexes[0];
            AiDebugLog.Write($"[AI][V2][Demand][Development] decision=CREATE anchor=({anchor.Q},{anchor.R}) "
                + "capability=DevelopmentInfrastructure desired=1 reason=no_research_production_facility");
            yield return new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.DevelopmentInfrastructure,
                DesiredAmount = 1,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = anchor,
                Value = 45f,
                Explain = "no Research/Production facility — Development axis has no operator base",
            };
        }

        private static bool HasIncomeFor(WorldSnapshot s, ResourceType type)
        {
            if (s?.Self == null || s.Economy == null)
                return false;
            float target = Mathf.Max(0f, s.Economy.IncomeTarget.Get(type));
            if (target <= AiConfigV2.allocatorSliceEpsilon)
                return true;
            return s.Self.PerTurnIncome.Get(type) + AiConfigV2.allocatorSliceEpsilon >= target;
        }
    }
}
