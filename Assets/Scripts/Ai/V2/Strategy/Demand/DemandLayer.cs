using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public static class DemandLayer
    {
        public static List<AxisDemand> Generate(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<AggressionObjective> aggressionObjectives,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player, AiTurnContext ctx = null,
            PlayerRoot root = null, IReadOnlyList<DevelopmentOpportunity> devOpportunities = null,
            Radar radar = null)
        {
            var demands = new List<AxisDemand>();
            // §17 — decay the resource-starvation feedback once per turn before it is read.
            if (player != null && snap != null)
                ResourceStarvationRegistry.DecayOncePerTurn(player, snap.TurnNumber);
            demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player, ctx, root));
            demands.AddRange(AggressionDemands(snap, breakdown, aggressionObjectives, activeIntents, commitments, player));
            demands.AddRange(DefenceDemands(snap, breakdown));
            demands.AddRange(EconomyDemands(snap, breakdown, player, ctx, root,
                activeIntents, commitments));
            demands.AddRange(DevelopmentDemands(snap, breakdown, devOpportunities, radar));
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
        //  ECO — a known extraction site with positive marginal income and acceptable payback.
        //  Deficit/starvation remain urgency multipliers, but are not admission prerequisites.
        //  Extraction and Base expansion each expose at most one demand to the shared allocator.
        // ---------------------------------------------------------------------------------------
        internal static IEnumerable<AxisDemand> EconomyDemands(WorldSnapshot s, DesireBreakdown b,
            PlayerSetupData player, AiTurnContext ctx, PlayerRoot root,
            IReadOnlyList<MissionIntent> activeIntents = null, ActorCommitments commitments = null)
        {
            if (s?.Self == null || s.Economy?.PerType == null)
            {
                AiDebugLog.Write("[AI][V2][Economy][Demand] selected=none reason=no_economy_snapshot");
                yield break;
            }

            var standings = s.Economy.PerType.ToDictionary(x => x.Type, x => x);
            var candidates = new List<AxisDemand>();
            foreach (EconomyExtractionOpportunity site in s.Economy.ExtractionOpportunities
                ?? System.Array.Empty<EconomyExtractionOpportunity>())
            {
                if (!standings.TryGetValue(site.ResourceType, out EconomyResourceStanding rs))
                    continue;
                CardDefinition def = ExtractionDefinition(ctx, site.ResourceType);
                if (ctx?.GameConfig != null && def == null)
                    continue;
                float starvation = Mathf.Max(rs.StarvationPressure,
                    ResourceStarvationRegistry.Pressure(player, site.ResourceType));
                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                float resourceCost = ResourceCostSum(def?.resourceCost);
                float preliminaryPayback = EconomyPaybackTurns(
                    gain, resourceCost, def?.apCost ?? 0f);
                float preliminaryValue = ScoreEconomySite(
                    Mathf.Max(rs.DeficitScore, starvation), gain,
                    site.BaseNetworkSynergy, site.NearbyResourceClusterValue,
                    0f, 0f, 0f, resourceCost, def?.apCost ?? 0f,
                    preliminaryPayback);
                EconomyBuilderChoice builder = SelectEconomyBuilder(
                    s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                    preliminaryValue, def?.apCost ?? 0f, includeReturn: true);
                float travel = builder?.Route.TravelCost
                    ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                float exposure = ThreatExposure(s, site.Hex);
                float opportunity = builder?.Route.EffectiveArmyPower ?? 0f;
                float assignmentAp = builder?.TotalAssignmentApCost ?? (def?.apCost ?? 0f);
                float payback = EconomyPaybackTurns(gain, resourceCost, assignmentAp);
                if (payback > AiConfigV2.economyExtractionMaxPaybackTurns)
                    continue;
                float value = ScoreEconomySite(Mathf.Max(rs.DeficitScore, starvation), gain,
                    site.BaseNetworkSynergy, site.NearbyResourceClusterValue,
                    travel, exposure, opportunity, resourceCost, assignmentAp, payback);
                if (value <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                candidates.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = CapabilityKind.EconomicInfrastructure,
                    DesiredAmount = 1f,
                    TargetHex = site.Hex,
                    EconomyResourceType = site.ResourceType,
                    EconomyBuildResourceCost = def?.resourceCost,
                    EconomyBuildApCost = def?.apCost ?? 0,
                    MinimumFollowupAp = def?.apCost ?? 0,
                    EconomyExpectedIncomeGain = gain,
                    EconomySiteValue = value,
                    EconomyTravelCost = travel,
                    EconomyThreatExposure = exposure,
                    EconomyHeroOpportunityCost = opportunity,
                    EconomyAssignmentApCost = assignmentAp,
                    EconomyPaybackTurns = payback,
                    EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                    EconomyBuilderRoutes = site.BuilderRoutes,
                    Value = value,
                    Explain = $"{site.ResourceType} deficit={rs.DeficitScore:0.##} "
                        + $"marginalGain={gain:0.#} effectiveYield={site.EffectiveYield} "
                        + $"alreadyCollected={site.CurrentBuildingCollection} "
                        + $"network={site.BaseNetworkSynergy:0.##} "
                        + $"cluster={site.NearbyResourceClusterValue:0.##} travel={travel:0.#} "
                        + $"exposure={exposure:0.##} heroCost={opportunity:0.##}",
                });
            }

            string baseSummary = AddBaseCandidates(
                s, candidates, player, ctx, activeIntents, commitments);
            IOrderedEnumerable<AxisDemand> ranked = candidates
                .OrderByDescending(x => IsActiveBaseCommitment(
                    activeIntents, x.TargetHex, x.EconomyBuildCard))
                .ThenByDescending(x => x.EconomySiteValue)
                .ThenByDescending(x => x.EconomyExpectedIncomeGain)
                .ThenBy(x => x.EconomyTravelCost)
                .ThenBy(x => x.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(x => x.TargetHex?.R ?? int.MaxValue);
            List<AxisDemand> selected = ranked
                .Where(x => x.Capability == CapabilityKind.EconomicInfrastructure)
                .Take(Mathf.Max(0, AiConfigV2.economyMaxInfrastructureDemandsPerTurn))
                .Concat(ranked.Where(x => x.Capability == CapabilityKind.EconomicExpansionBase)
                    .Take(Mathf.Max(0, AiConfigV2.economyMaxExpansionBaseDemandsPerTurn)))
                .ToList();
            foreach (AxisDemand demand in selected)
            {
                AxisDemand emitted = demand.EconomyPreferredBuilderArmyId.HasValue
                    ? demand
                    : EconomyHeroPrerequisite(demand);
                AiDebugLog.Write($"[AI][V2][Economy][Demand] selected={emitted.Capability} "
                    + $"resource={emitted.EconomyResourceType?.ToString() ?? "none"} "
                    + $"target=({emitted.TargetHex?.Q},{emitted.TargetHex?.R}) value={emitted.Value:0.##} "
                    + $"rejected={Mathf.Max(0, candidates.Count - selected.Count)}");
                yield return emitted;
            }
            AiDebugLog.Write($"[AI][V2][Economy][BaseCandidates] {baseSummary}");
            if (selected.Count == 0)
                AiDebugLog.Write($"[AI][V2][Economy][Demand] selected=none rejected={candidates.Count} "
                    + "reason=no_legal_valuable_site_or_base");
        }

        internal static AxisDemand EconomyHeroPrerequisite(AxisDemand source) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.Hero,
            DesiredAmount = 1f,
            TargetHex = source.TargetHex,
            // Preserve only the exact card instance as a staged commitment. H/E/M/T stay free
            // until the builder enters the one-turn completion horizon.
            EconomyResourceType = source.EconomyResourceType,
            EconomyBuildCard = source.EconomyBuildCard,
            MinimumFollowupAp = source.MinimumFollowupAp,
            EconomyExpectedIncomeGain = source.EconomyExpectedIncomeGain,
            EconomySiteValue = source.EconomySiteValue,
            EconomyTravelCost = source.EconomyTravelCost,
            EconomyThreatExposure = source.EconomyThreatExposure,
            EconomyHeroOpportunityCost = source.EconomyHeroOpportunityCost,
            EconomyAssignmentApCost = source.EconomyAssignmentApCost,
            EconomyPaybackTurns = source.EconomyPaybackTurns,
            EconomyPreferredBuilderArmyId = source.EconomyPreferredBuilderArmyId,
            EconomyBuilderRoutes = source.EconomyBuilderRoutes,
            Value = source.Value,
            Explain = source.Explain + "; prerequisite=mobile_hero",
        };

        internal sealed class EconomyBuilderChoice
        {
            public EconomyBuilderRouteSnapshot Route;
            public ArmySnapshot Army;
            public float TotalAssignmentApCost;
        }

        internal static EconomyBuilderChoice SelectEconomyBuilder(WorldSnapshot snap,
            HexCoord target, IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            float buildValue, float buildApCost, bool includeReturn)
        {
            return RankEconomyBuilders(snap, target, routes, activeIntents, commitments,
                buildValue, buildApCost, includeReturn).FirstOrDefault();
        }

        internal static IReadOnlyList<EconomyBuilderChoice> RankEconomyBuilders(WorldSnapshot snap,
            HexCoord target, IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            float buildValue, float buildApCost, bool includeReturn)
        {
            return EconomyBuilderCandidates(snap, target, routes, activeIntents, commitments)
                .Where(x => x.route.IsOnTarget || ActiveAssignment(activeIntents, x.army.ArmyId) == null
                    || ActiveAssignment(activeIntents, x.army.ArmyId).Kind == MissionKind.Economy
                    || EconomyLoanAllowed(ActiveAssignment(activeIntents, x.army.ArmyId), buildValue,
                        x.route.TravelCost, x.army.CurrentMovement, out _))
                .Select(x => new EconomyBuilderChoice
                {
                    Route = x.route,
                    Army = x.army,
                    TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                        x.route, buildApCost, includeReturn),
                })
                .OrderByDescending(x => x.Route.HasActiveEconomyCommitment
                    || ActiveAssignment(activeIntents, x.Route.ArmyId)?.Kind == MissionKind.Economy)
                .ThenBy(x => x.TotalAssignmentApCost)
                .ThenBy(x => x.Route.EffectiveArmyPower)
                .ThenBy(x => x.Route.ArmySize)
                .ThenBy(x => x.Route.TravelCost + (includeReturn ? x.Route.ReturnTravelCost : 0))
                .ThenBy(x => x.Route.ArmyId)
                .ToList();
        }

        internal static float EstimateEconomyAssignmentAp(EconomyBuilderRouteSnapshot route,
            float buildApCost, bool includeReturn)
        {
            int move = Mathf.Max(1, route.MaxMovement);
            int outboundTurns = route.TravelCost <= 0 ? 0
                : route.CurrentMovement > 0
                    ? 1 + Mathf.CeilToInt(Mathf.Max(0,
                        route.TravelCost - route.CurrentMovement) / (float)move)
                    : Mathf.CeilToInt(route.TravelCost / (float)move);
            int paidOutboundActivations = Mathf.Max(0,
                outboundTurns - (route.HasActivatedThisTurn && outboundTurns > 0 ? 1 : 0));
            if (route.IsOnTarget) paidOutboundActivations = 0;
            int returnTurns = includeReturn && route.ReturnTravelCost > 0
                ? Mathf.CeilToInt(route.ReturnTravelCost / (float)move) : 0;
            return Mathf.Max(0f, buildApCost)
                + (paidOutboundActivations + returnTurns) * Mathf.Max(0, route.ActivationApCost);
        }

        private static IEnumerable<(EconomyBuilderRouteSnapshot route, ArmySnapshot army)>
            EconomyBuilderCandidates(WorldSnapshot snap, HexCoord target,
                IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
                IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments)
        {
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = routes
                ?? SnapshotFallbackRoutes(snap, target);
            foreach (EconomyBuilderRouteSnapshot route in witnessed)
            {
                ArmySnapshot army = snap?.Self?.Armies?.FirstOrDefault(
                    a => a != null && a.ArmyId == route.ArmyId);
                if (army == null)
                    continue;
                if (route.IsOnTarget && army.IsGarrison && army.HasHero)
                {
                    yield return (route, army);
                    continue;
                }
                if (!army.IsMobileEconomyBuilder)
                    continue;

                MissionIntent assignment = ActiveAssignment(activeIntents, army.ArmyId);
                bool claimed = commitments != null && commitments.IsArmyClaimed(army.ArmyId);
                if (assignment != null)
                {
                    if (assignment.Kind == MissionKind.Economy)
                    {
                        if (assignment.Economy == null
                            || !assignment.Economy.TargetHex.Equals(target))
                            continue;
                    }
                    else if (!EconomyDonorStructurallyEligible(assignment))
                    {
                        continue;
                    }
                }
                if (claimed && assignment == null)
                    continue;
                if (EconomyBuilderUnderImmediateThreat(snap, army.Hex))
                    continue;
                yield return (route, army);
            }
        }

        // Tests and snapshot-only simulations may construct opportunities without the production
        // Analysis route list. Preserve their structural semantics without any live-registry read.
        private static IReadOnlyList<EconomyBuilderRouteSnapshot> SnapshotFallbackRoutes(
            WorldSnapshot snap, HexCoord target)
        {
            var result = new List<EconomyBuilderRouteSnapshot>();
            foreach (ArmySnapshot army in snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
            {
                if (army == null)
                    continue;
                if (army.IsGarrison)
                {
                    if (army.HasHero && army.Hex.Equals(target))
                        result.Add(new EconomyBuilderRouteSnapshot
                        {
                            ArmyId = army.ArmyId, TravelCost = 0, ReturnTravelCost = 0,
                            CurrentMovement = army.CurrentMovement, MaxMovement = army.MaxMovement,
                            ActivationApCost = army.ActivationApCost, ArmySize = army.MemberCount,
                            HasActivatedThisTurn = army.HasActivatedThisTurn,
                            EffectiveArmyPower = army.EffectiveArmyPower, IsOnTarget = true,
                        });
                    continue;
                }
                if (!army.IsMobileEconomyBuilder)
                    continue;
                result.Add(new EconomyBuilderRouteSnapshot
                {
                    ArmyId = army.ArmyId,
                    TravelCost = HexGridMath.Distance(army.Hex, target),
                    ReturnTravelCost = snap?.Self?.BaseHexes?.Count > 0
                        ? snap.Self.BaseHexes.Min(h => HexGridMath.Distance(target, h)) : 0,
                    CurrentMovement = army.CurrentMovement,
                    MaxMovement = army.MaxMovement,
                    ActivationApCost = army.ActivationApCost,
                    HasActivatedThisTurn = army.HasActivatedThisTurn,
                    ArmySize = army.MemberCount,
                    EffectiveArmyPower = army.EffectiveArmyPower,
                    IsOnTarget = army.Hex.Equals(target),
                });
            }
            return result;
        }

        private static MissionIntent ActiveAssignment(
            IReadOnlyList<MissionIntent> activeIntents, int armyId) =>
            activeIntents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                && i.PreferredMoverArmyId == armyId);

        internal static bool EconomyDonorStructurallyEligible(MissionIntent donor)
        {
            if (donor == null || (donor.Funding != CommitmentTier.None
                && donor.Funding != CommitmentTier.Soft))
                return false;
            if (donor.Kind == MissionKind.Scout)
                return donor.Scout != null && donor.Scout.Kind != ScoutTargetKind.Surveil;
            if (donor.Kind == MissionKind.Raid)
                return donor.Raid != null && !donor.Raid.OperationStarted;
            return false;
        }

        internal static bool EconomyLoanAllowed(MissionIntent donor, float buildValue,
            int routeCost, int movementAvailable, out float netValue)
        {
            netValue = buildValue - AiConfigV2.economyLoanContinuationLoss
                - Mathf.Max(0, routeCost) * AiConfigV2.economySiteTravelPenalty;
            return EconomyDonorStructurallyEligible(donor)
                && routeCost <= movementAvailable
                && netValue >= AiConfigV2.economyLoanHysteresisThreshold;
        }

        internal static bool EconomyBuilderUnderImmediateThreat(
            WorldSnapshot snap, HexCoord hex) =>
            snap?.Threat?.Threats != null && snap.Threat.Threats.Any(t => t?.Asset != null
                && t.Asset.Hex.Equals(hex) && t.Severity >= AiConfigV2.defenceSeverityTrigger
                && (!t.EnemyEta.HasValue || t.EnemyEta.Value <= 1));

        internal static float EconomyRecoveryThreatExposure(WorldSnapshot snap, HexCoord hex) =>
            ThreatExposure(snap, hex);

        internal static float EconomyPaybackTurns(float expectedIncomeGain,
            float resourceCost, float assignmentApCost) => expectedIncomeGain <= 0f
                ? float.PositiveInfinity
                : (Mathf.Max(0f, resourceCost) + Mathf.Max(0f, assignmentApCost))
                    / expectedIncomeGain;

        internal static float ScoreEconomySite(float deficit, float expectedIncomeGain,
            float baseNetworkSynergy, float nearbyResourceClusterValue, float travelCost,
            float threatExposure, float heroOpportunityCost, float resourceCost,
            float assignmentApCost, float paybackTurns) =>
            AiConfigV2.economySiteDeficitValue * Mathf.Clamp01(deficit)
            + AiConfigV2.economySiteIncomeGainValue * Mathf.Max(0f, expectedIncomeGain)
            + AiConfigV2.economySiteBaseSynergyValue * Mathf.Clamp01(baseNetworkSynergy)
            + AiConfigV2.economySiteClusterValue * Mathf.Max(0f, nearbyResourceClusterValue)
            + AiConfigV2.economyExtractionPaybackValue
                * Mathf.Clamp01(1f - paybackTurns / AiConfigV2.economyExtractionMaxPaybackTurns)
            - AiConfigV2.economyBuildResourcePenalty * Mathf.Max(0f, resourceCost)
            - AiConfigV2.economyBuildApPenalty * Mathf.Max(0f, assignmentApCost)
            - AiConfigV2.economySiteTravelPenalty * Mathf.Max(0f, travelCost)
            - AiConfigV2.economySiteThreatPenalty * Mathf.Clamp01(threatExposure)
            - AiConfigV2.economySiteHeroOpportunityPenalty * Mathf.Max(0f, heroOpportunityCost);

        private static string AddBaseCandidates(WorldSnapshot s, List<AxisDemand> output,
            PlayerSetupData player, AiTurnContext ctx,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments)
        {
            List<CardData> baseCards = (s.Self.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition?.cardType == CardType.Base)
                .OrderBy(c => c.Definition.authoredKey ?? c.Definition.displayName)
                .ToList();
            if (baseCards.Count == 0 || s.Economy?.BaseOpportunities == null)
                return "considered=0 kept=0 reason=no_base_card_or_opportunity";

            int considered = 0;
            int kept = 0;
            AxisDemand best = null;

            foreach (EconomyBaseOpportunity site in s.Economy.BaseOpportunities)
                foreach (CardData card in baseCards)
                {
                    considered++;
                    bool committed = IsActiveBaseCommitment(activeIntents, site.Hex, card);
                    float hexYield = BaseHexYieldValue(s, site.HexYield);
                    float global = BaseGlobalEffectValue(s, card.Definition);
                    float airfield = BaseAirfieldValue(s, card.Definition, site.Hex);
                    float reasonValue = AiConfigV2.economyBaseCapacityValue * site.CapacityValue
                        + AiConfigV2.economyBaseHexYieldValue * hexYield
                        + AiConfigV2.economyBaseClusterValue * site.NearbyResourceClusterValue
                        + AiConfigV2.economyBaseNetworkExpansionValue * site.NetworkExpansionValue
                        + AiConfigV2.economyBaseInfrastructurePressureValue * site.InfrastructurePressure
                        + AiConfigV2.economyBaseAirfieldValue * airfield
                        + AiConfigV2.economyBaseLogisticsValue * site.LogisticsValue
                        + AiConfigV2.economyBaseGlobalEffectValue * global;
                    if (site.NearbyResourceClusterValue <= 0f
                        && site.NetworkExpansionValue <= 0f && hexYield <= 0f
                        && site.InfrastructurePressure <= 0f && airfield <= 0f
                        && !site.ConvertsOwnedExtractionSite && global <= 0f && !committed)
                        continue;
                    EconomyBuilderChoice builder = SelectEconomyBuilder(
                        s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                        reasonValue, card.EffectivePlayApCost, includeReturn: false);
                    float travel = builder?.Route.TravelCost
                        ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                    float exposure = ThreatExposure(s, site.Hex);
                    float heroCost = builder?.Route.EffectiveArmyPower ?? 0f;
                    float assignmentAp = builder?.TotalAssignmentApCost
                        ?? card.EffectivePlayApCost;
                    float buildCost = assignmentAp * AiConfigV2.economyBuildApPenalty
                        + ResourceCostSum(card.EffectivePlayResourceCost)
                            * AiConfigV2.economyBuildResourcePenalty;
                    float value = reasonValue - buildCost
                        - AiConfigV2.economySiteTravelPenalty * travel
                        - AiConfigV2.economySiteThreatPenalty * exposure
                        - AiConfigV2.economySiteHeroOpportunityPenalty * heroCost;
                    string decision = value < AiConfigV2.economyBaseDemandMinValue && !committed
                        ? "below_min" : "keep";
                    AiDebugLog.WriteVerbose($"[AI][V2][Economy][BaseCandidate] "
                        + $"card={card.Definition.displayName} target=({site.Hex.Q},{site.Hex.R}) "
                        + $"yield={hexYield:0.##} cluster={site.NearbyResourceClusterValue:0.##} "
                        + $"network={site.NetworkExpansionValue:0.##} pressure={site.InfrastructurePressure:0.##} "
                        + $"airfield={airfield:0.##} global={global:0.##} cost={buildCost:0.##} "
                        + $"value={value:0.##} committed={committed} decision={decision}");
                    if (decision != "keep")
                        continue;
                    var demand = new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Economy,
                        Capability = CapabilityKind.EconomicExpansionBase,
                        DesiredAmount = 1f,
                        TargetHex = site.Hex,
                        EconomyBuildCard = card,
                        EconomyBuildResourceCost = card.EffectivePlayResourceCost,
                        EconomyBuildApCost = card.EffectivePlayApCost,
                        MinimumFollowupAp = card.EffectivePlayApCost,
                        EconomyExpectedIncomeGain = site.HexYield.Sum,
                        EconomySiteValue = value,
                        EconomyTravelCost = travel,
                        EconomyThreatExposure = exposure,
                        EconomyHeroOpportunityCost = heroCost,
                        EconomyAssignmentApCost = assignmentAp,
                        EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                        EconomyBuilderRoutes = site.BuilderRoutes,
                        Value = value,
                        Explain = $"Base capacity={site.CapacityValue:0.##} yield={hexYield:0.##} "
                            + $"cluster={site.NearbyResourceClusterValue:0.##} "
                            + $"network={site.NetworkExpansionValue:0.##} "
                            + $"pressure={site.InfrastructurePressure:0.##} airfield={airfield:0.##} "
                            + $"logistics={site.LogisticsValue:0.##} global={global:0.##} "
                            + $"cost={buildCost:0.##}",
                    };
                    output.Add(demand);
                    kept++;
                    if (best == null || demand.EconomySiteValue > best.EconomySiteValue)
                        best = demand;
                }
            return best == null
                ? $"considered={considered} kept={kept} best=none"
                : $"considered={considered} kept={kept} best={best.EconomyBuildCard.Definition.displayName} "
                    + $"target=({best.TargetHex?.Q},{best.TargetHex?.R}) value={best.EconomySiteValue:0.##}";
        }

        private static bool IsActiveBaseCommitment(IReadOnlyList<MissionIntent> intents,
            HexCoord? target, CardData card)
        {
            if (!target.HasValue || intents == null)
                return false;
            return intents.Any(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.Economy && i.Economy?.Kind == EconomyTaskKind.FoundBase
                && i.Economy.TargetHex.Equals(target.Value)
                && (i.Economy.BuildCard == null || i.Economy.BuildCard == card));
        }

        private static float BaseHexYieldValue(WorldSnapshot s, ResourceBundle yield)
        {
            if (s?.Economy?.PerType == null)
                return 0f;
            var standings = s.Economy.PerType.ToDictionary(x => x.Type, x => x);
            float value = 0f;
            foreach (ResourceType type in ResourceBundle.All)
                if (standings.TryGetValue(type, out EconomyResourceStanding standing))
                    value += yield.Get(type) * Mathf.Max(0.25f, standing.DeficitScore);
            return value;
        }

        private static float BaseGlobalEffectValue(WorldSnapshot s, CardDefinition definition)
        {
            if (definition?.grantedAbilities == null)
                return 0f;
            EffectContribution contribution = StrategicEffectRegistry.Contributions(
                IntendedRole.Economy, definition.grantedAbilities, 0,
                new EffectEvaluationContext(s));
            return contribution.GlobalRoleFit + contribution.GlobalImmediateTempo
                + contribution.GlobalThreatResponse + contribution.GlobalCapabilityGap
                + contribution.GlobalForceGrowth + contribution.GlobalSynergy;
        }

        private static float BaseAirfieldValue(WorldSnapshot s, CardDefinition definition,
            HexCoord target)
        {
            if (definition == null || definition.airfieldCapacity <= 0 || s?.Self == null)
                return 0f;
            bool aviationRelevant = (s.Self.Hand ?? System.Array.Empty<CardData>())
                    .Any(c => c?.Definition?.isAviation == true)
                || (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>()).Any(a => a != null && a.IsAir);
            if (!aviationRelevant)
                return 0f;
            List<ArmySnapshot> airfields = (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.IsAirfield).ToList();
            if (airfields.Count == 0)
                return 1f;
            int distance = airfields.Min(a => HexGridMath.Distance(a.Hex, target));
            return Mathf.Clamp01(distance / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
        }

        private static CardDefinition ExtractionDefinition(AiTurnContext ctx, ResourceType type)
        {
            CardDefinition[] cards = ctx?.GameConfig?.extractionFacilityCards;
            int index = (int)type;
            return cards != null && index >= 0 && index < cards.Length ? cards[index] : null;
        }

        private static float ThreatExposure(WorldSnapshot s, HexCoord target)
        {
            if (s?.Known?.EnemySightings == null)
                return 0f;
            float exposure = 0f;
            foreach (AiMapMemory.KnownEnemySighting enemy in s.Known.EnemySightings)
            {
                int distance = HexGridMath.Distance(target, enemy.Hex);
                if (distance <= 3)
                    exposure = Mathf.Max(exposure, 1f - distance / 4f);
            }
            return exposure;
        }

        private static float ResourceCostSum(ResourceCost cost) => cost == null ? 0f
            : ResourceBundle.All.Sum(t => Mathf.Max(0, cost.Get(t)));

        // ---------------------------------------------------------------------------------------
        //  DEV — three staged shapes (the radar no longer gates on facility+hero; the demand layer
        //  bootstraps each missing prerequisite the way Recon bootstraps a scout):
        //    · no Research/Production facility yet -> ONE DevelopmentInfrastructure gap demand.
        //    · facility built but UNSTAFFED -> ONE DevelopmentOperator demand @the facility hex
        //      (play a Research/Production hero card onto it). No offerings exist without an
        //      operator, so CardUpgrade is not emitted this turn.
        //    · facility staffed -> ONE CardUpgrade demand PER scored DevelopmentOpportunity, each
        //      carrying its opportunity handle. Phase A runs the carried opportunity verbatim.
        // ---------------------------------------------------------------------------------------
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b,
            IReadOnlyList<DevelopmentOpportunity> devOpportunities, Radar radar)
        {
            float devScale = radar != null ? RadarValueScale.For(radar, DesireAxis.Development) : 1f;
            if (s?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            if (!s.Self.HasDevFacility)
            {
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
                yield break;
            }

            // Facility built but no qualifying operator hero -> stage the hero. PER MODE: Research and
            // Production need DIFFERENT hero abilities (Researcher vs Assembler), so a staffed b_Lab
            // must NOT suppress the b_Factory operator demand — each unstaffed mode gets its own.
            DevelopmentReadiness rd = s.Development;
            int operatorPrerequisites = 0;
            if (rd != null && rd.Facilities != null)
            {
                foreach (ResearchProductionMode mode in new[]
                    { ResearchProductionMode.Research, ResearchProductionMode.Production })
                {
                    bool modeStaffed = false, modeHasOpenFacility = false;
                    HexCoord at = default;
                    foreach (DevelopmentFacility f in rd.Facilities)
                    {
                        if (f.Mode != mode) continue;
                        if (f.HasHero) { modeStaffed = true; break; }
                        if (!f.Contested && !modeHasOpenFacility)
                        {
                            modeHasOpenFacility = true;
                            at = f.Hex;
                        }
                    }
                    if (modeStaffed || !modeHasOpenFacility)
                        continue;

                    bool haveCard = mode == ResearchProductionMode.Research
                        ? rd.ResearcherCardInHand
                        : rd.AssemblerCardInHand;
                    operatorPrerequisites++;
                    AiDebugLog.Write($"[AI][V2][Demand][Development] decision=CREATE anchor=({at.Q},{at.R}) "
                        + $"capability=DevelopmentOperator mode={mode} desired=1 "
                        + $"reason={(haveCard ? "unstaffed_facility_operator_card_in_hand" : "unstaffed_facility_no_operator_card_yet")}");
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Development,
                        Capability = CapabilityKind.DevelopmentOperator,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = at,
                        DevelopmentOperatorMode = mode,
                        Value = 45f * devScale,
                        Explain = $"facility @({at.Q},{at.R}) has no {mode} operator — "
                            + "Development axis cannot run a Challenge until a qualifying hero stands on it",
                    };
                }
            }
            // An unstaffed mode must not suppress real opportunities from another ready mode.
            int emitted = 0;
            if (devOpportunities != null)
                foreach (DevelopmentOpportunity op in devOpportunities)
                {
                    if (op == null || op.BaseValue <= 0f) continue;
                    emitted++;
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Development,
                        Capability = CapabilityKind.CardUpgrade,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = op.FacilityHex,
                        Value = op.BaseValue * devScale,   // radar model #1a — Development weight scales merit
                        DevOpportunity = op,
                        Explain = op.Explain,
                    };
                }

            if (emitted > 0)
                AiDebugLog.Write($"[AI][V2][Demand][Development] decision=UPGRADE count={emitted} "
                    + "reason=facility_ready_scored_opportunities");
            else if (operatorPrerequisites == 0)
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=SATISFIED "
                    + "reason=facility_ready_no_worthwhile_upgrade");
        }

    }
}
