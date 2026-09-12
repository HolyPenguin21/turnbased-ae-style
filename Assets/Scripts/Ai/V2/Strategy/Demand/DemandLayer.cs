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
    public static class DemandLayer
    {
        public static List<AxisDemand> Generate(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<AggressionObjective> aggressionObjectives,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player, AiTurnContext ctx = null,
            PlayerRoot root = null, IReadOnlyList<DevelopmentOpportunity> devOpportunities = null,
            Radar radar = null, ISet<DesireAxis> dirtyAxes = null)
        {
            var demands = new List<AxisDemand>();
            bool GenerateAxis(DesireAxis axis) => dirtyAxes == null || dirtyAxes.Contains(axis);
            // §17 — decay the resource-starvation feedback once per turn before it is read.
            if (player != null && snap != null)
                ResourceStarvationRegistry.DecayOncePerTurn(player, snap.TurnNumber);
            if (GenerateAxis(DesireAxis.Recon))
                demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player, ctx, root));
            if (GenerateAxis(DesireAxis.Aggression))
                demands.AddRange(AggressionDemands(snap, breakdown, aggressionObjectives, activeIntents, commitments, player));
            if (GenerateAxis(DesireAxis.Defence))
                demands.AddRange(DefenceDemands(snap, breakdown));
            if (GenerateAxis(DesireAxis.Economy))
                demands.AddRange(EconomyDemands(snap, breakdown, player, ctx, root,
                    activeIntents, commitments));
            if (GenerateAxis(DesireAxis.Development))
                demands.AddRange(DevelopmentDemands(snap, breakdown, devOpportunities, radar,
                    demands, activeIntents, player));
            // AI-MGR-01 — radar-independent standing-force pull. Emitted LAST so it can see whether
            // an Aggression / Defence combat demand already covers the same ground this pass.
            if (GenerateAxis(DesireAxis.Defence))
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
            int rejectedNoBuilder = 0;
            int rejectedPayback = 0;
            int rejectedStrategicValue = 0;
            int rejectedDeliveryValue = 0;
            foreach (EconomyExtractionOpportunity site in s.Economy.ExtractionOpportunities
                ?? System.Array.Empty<EconomyExtractionOpportunity>())
            {
                if (!standings.TryGetValue(site.ResourceType, out EconomyResourceStanding rs))
                    continue;
                float resourcePriority = EconomyResourcePriority(rs);
                CardDefinition def = ExtractionDefinition(ctx, site.ResourceType);
                if (ctx?.GameConfig != null && def == null)
                    continue;
                float starvation = Mathf.Max(rs.StarvationPressure,
                    ResourceStarvationRegistry.Pressure(player, site.ResourceType));
                resourcePriority = Mathf.Max(resourcePriority, starvation);
                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                float resourceCost = ResourceCostSum(def?.resourceCost);
                float preliminaryPayback = EconomyPaybackTurns(
                    gain, resourceCost, def?.apCost ?? 0f);
                float preliminaryValue = ScoreEconomySite(
                    resourcePriority, gain,
                    site.BaseNetworkSynergy, site.NearbyResourceClusterValue,
                    0f, 0f, 0f, resourceCost, def?.apCost ?? 0f,
                    preliminaryPayback);
                EconomyBuilderChoice builder = SelectEconomyBuilder(
                    s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                    preliminaryValue, def?.apCost ?? 0f, includeReturn: true);
                float travel = builder?.Route.TravelCost
                    ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                float exposure = ThreatExposure(s, site.Hex);
                float opportunity = EconomyMissionOpportunityCost(builder, activeIntents);
                float assignmentAp = builder?.TotalAssignmentApCost ?? (def?.apCost ?? 0f);
                float payback = EconomyPaybackTurns(gain, resourceCost, assignmentAp);
                if (payback > AiConfigV2.economyExtractionMaxPaybackTurns)
                {
                    rejectedPayback++;
                    continue;
                }
                float strategicValue = ScoreEconomySite(
                    resourcePriority, gain,
                    site.BaseNetworkSynergy, site.NearbyResourceClusterValue,
                    0f, exposure, 0f, resourceCost, def?.apCost ?? 0f,
                    preliminaryPayback);
                float deliveryApCost = Mathf.Max(0f,
                    assignmentAp - (def?.apCost ?? 0f));
                float value = strategicValue
                    - AiConfigV2.economyBuildApPenalty * deliveryApCost
                    - AiConfigV2.economySiteTravelPenalty * Mathf.Max(0f, travel)
                    - Mathf.Max(0f, opportunity);
                if (strategicValue <= AiConfigV2.allocatorSliceEpsilon)
                {
                    rejectedStrategicValue++;
                    continue;
                }
                if (value <= AiConfigV2.allocatorSliceEpsilon)
                {
                    if (builder == null) rejectedNoBuilder++;
                    else rejectedDeliveryValue++;
                    continue;
                }
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
                    EconomySiteValue = strategicValue,
                    EconomyTravelCost = travel,
                    EconomyThreatExposure = exposure,
                    EconomyHeroOpportunityCost = opportunity,
                    EconomyAssignmentApCost = assignmentAp,
                    EconomyPaybackTurns = payback,
                    EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                    EconomyProjectedActivationApCost = builder?.ProjectedActivationApCost ?? 0,
                    EconomyProjectedMaxMovement = builder?.ProjectedMaxMovement ?? 0,
                    EconomyBuilderRoutes = site.BuilderRoutes,
                    Value = value,
                    Explain = $"{site.ResourceType} deficit={rs.DeficitScore:0.##} "
                        + $"resourcePriority={resourcePriority:0.##} marginalGain={gain:0.#} effectiveYield={site.EffectiveYield} "
                        + $"alreadyCollected={site.CurrentBuildingCollection} "
                        + $"network={site.BaseNetworkSynergy:0.##} "
                        + $"cluster={site.NearbyResourceClusterValue:0.##} "
                        + $"site={strategicValue:0.##} delivery={value:0.##} "
                        + $"travel={travel:0.#} exposure={exposure:0.##} "
                        + $"heroCost={opportunity:0.##}",
                });
            }

            string baseSummary = AddBaseCandidates(
                s, candidates, player, ctx, activeIntents, commitments,
                out int baseNoBuilder, out int baseStrategicValue,
                out int baseDeliveryValue, out int baseThreshold);
            // Resource need is a strategic decision; builder convenience chooses a site only
            // after a resource has survived feasibility/payback filtering. This prevents a scout
            // standing on a low-priority resource from silently replacing the hand bottleneck.
            IOrderedEnumerable<AxisDemand> extractionRanked = candidates
                .Where(x => x.Capability == CapabilityKind.EconomicInfrastructure
                    && x.EconomyResourceType.HasValue
                    // A FoundBase intent already owns this hex — extraction must not propose a
                    // competing build on the same target.
                    && !HasActiveEconomyIntentAtHexOfKind(
                        activeIntents, x.TargetHex, EconomyTaskKind.FoundBase))
                // A builder already committed and en route (or standing) on this target must not
                // lose its slot to .Take(N) just because some other resource's priority ticked up
                // this pass — mirrors baseRanked's IsActiveBaseCommitment precedence below.
                .OrderByDescending(x => HasActiveEconomyBuildIntent(activeIntents, x) ? 1 : 0)
                .ThenByDescending(x => standings.TryGetValue(
                        x.EconomyResourceType.Value, out EconomyResourceStanding rs)
                    ? Mathf.Max(EconomyResourcePriority(rs),
                        ResourceStarvationRegistry.Pressure(
                            player, x.EconomyResourceType.Value))
                    : 0f)
                .ThenByDescending(x => x.EconomySiteValue)
                .ThenByDescending(x => x.EconomyExpectedIncomeGain)
                .ThenBy(x => x.EconomyTravelCost)
                .ThenBy(x => x.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(x => x.TargetHex?.R ?? int.MaxValue);
            IOrderedEnumerable<AxisDemand> baseRanked = candidates
                .Where(x => x.Capability == CapabilityKind.EconomicExpansionBase
                    // An active BuildExtraction intent already owns this hex — a fresh Base
                    // candidate must not propose converting/competing for the same target while
                    // that extraction is still in flight.
                    && !HasActiveEconomyIntentAtHexOfKind(
                        activeIntents, x.TargetHex, EconomyTaskKind.BuildExtraction))
                .OrderByDescending(x => IsActiveBaseCommitment(
                    activeIntents, x.TargetHex, x.EconomyBuildCard))
                .ThenByDescending(x => x.EconomySiteValue)
                .ThenByDescending(x => x.EconomyExpectedIncomeGain)
                .ThenBy(x => x.EconomyTravelCost)
                .ThenBy(x => x.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(x => x.TargetHex?.R ?? int.MaxValue);
            List<AxisDemand> selected = extractionRanked
                .Take(Mathf.Max(0, AiConfigV2.economyMaxInfrastructureDemandsPerTurn))
                .Concat(baseRanked.Take(
                    Mathf.Max(0, AiConfigV2.economyMaxExpansionBaseDemandsPerTurn)))
                .ToList();
            foreach (AxisDemand demand in selected)
            {
                // A Phase-A Hero handoff already has one concrete actor and target owned by
                // Continuity. If that actor is temporarily composition-ineligible, its durable
                // mission must retry/defer; requesting another Hero for the same operation would
                // grow the roster every settled pass and create a second owner for one need.
                if (!demand.EconomyPreferredBuilderArmyId.HasValue
                    && HasActiveEconomyBuildIntent(activeIntents, demand))
                {
                    AiDebugLog.Write($"[AI][V2][Economy][Demand] selected=continuity "
                        + $"target=({demand.TargetHex?.Q},{demand.TargetHex?.R}) "
                        + "reason=existing_target_specific_actor");
                    continue;
                }
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
            int rejectionTotal = rejectedNoBuilder + baseNoBuilder + rejectedPayback
                + rejectedStrategicValue + baseStrategicValue
                + rejectedDeliveryValue + baseDeliveryValue + baseThreshold;
            AiDebugLog.Write($"[AI][V2][Economy][Rejections] no_builder={rejectedNoBuilder + baseNoBuilder} "
                + $"payback={rejectedPayback} strategic_value={rejectedStrategicValue + baseStrategicValue} "
                + $"delivery_value={rejectedDeliveryValue + baseDeliveryValue} threshold={baseThreshold}");
            if (selected.Count == 0)
                AiDebugLog.Write($"[AI][V2][Economy][Demand] selected=none rejected={rejectionTotal} "
                    + "reason=no_legal_valuable_site_or_base");
        }

        internal static AxisDemand EconomyHeroPrerequisite(AxisDemand source) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.Hero,
            DesiredAmount = 1f,
            TargetHex = source.TargetHex,
            // Preserve the exact operation through Hero materialization. H/E/M/T stay free until
            // a builder route exists because deferred reservations never admit Hero capability.
            EconomyResourceType = source.EconomyResourceType,
            EconomyBuildCard = source.EconomyBuildCard,
            EconomyBuildResourceCost = source.EconomyBuildResourceCost,
            EconomyBuildApCost = source.EconomyBuildApCost,
            MinimumFollowupAp = source.MinimumFollowupAp,
            EconomyExpectedIncomeGain = source.EconomyExpectedIncomeGain,
            EconomySiteValue = source.EconomySiteValue,
            EconomyTravelCost = source.EconomyTravelCost,
            EconomyThreatExposure = source.EconomyThreatExposure,
            EconomyHeroOpportunityCost = source.EconomyHeroOpportunityCost,
            EconomyAssignmentApCost = source.EconomyAssignmentApCost,
            EconomyPaybackTurns = source.EconomyPaybackTurns,
            EconomyStrategicUrgency = source.EconomyStrategicUrgency,
            EconomyPreferredBuilderArmyId = source.EconomyPreferredBuilderArmyId,
            EconomyProjectedActivationApCost = source.EconomyProjectedActivationApCost,
            EconomyProjectedMaxMovement = source.EconomyProjectedMaxMovement,
            EconomyBuilderRoutes = source.EconomyBuilderRoutes,
            Value = source.Value,
            Explain = source.Explain + "; prerequisite=mobile_hero",
        };

        internal sealed class EconomyBuilderChoice
        {
            public EconomyBuilderRouteSnapshot Route;
            public ArmySnapshot Army;
            public float TotalAssignmentApCost;
            public EconomyArmySuitability Suitability;
            public int MinimumEscortCount;
            public int ProjectedActivationApCost;
            public int ProjectedMaxMovement;
        }

        internal enum EconomyArmySuitability
        {
            Ready,
            LightenAtBase,
            ReinforceAtBase,
            Ineligible,
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
                .Select(x => AssessEconomyArmy(snap, target, x.route, x.army,
                    buildApCost, includeReturn))
                .Where(x => x.Suitability != EconomyArmySuitability.Ineligible)
                .OrderByDescending(x => x.Route.HasActiveEconomyCommitment
                    || ActiveAssignment(activeIntents, x.Route.ArmyId)?.Kind == MissionKind.Economy)
                .ThenBy(x => x.Suitability == EconomyArmySuitability.Ready ? 0
                    : x.Suitability == EconomyArmySuitability.LightenAtBase ? 1 : 2)
                .ThenBy(x => x.TotalAssignmentApCost)
                .ThenBy(x => x.Route.EffectiveArmyPower)
                .ThenBy(x => x.Route.ArmySize)
                .ThenBy(x => x.Route.TravelCost + (includeReturn ? x.Route.ReturnTravelCost : 0))
                .ThenBy(x => x.Route.ArmyId)
                .ToList();
        }

        private static EconomyBuilderChoice AssessEconomyArmy(WorldSnapshot snap,
            HexCoord target, EconomyBuilderRouteSnapshot route, ArmySnapshot army,
            float buildApCost, bool includeReturn)
        {
            var choice = new EconomyBuilderChoice
            {
                Route = route,
                Army = army,
                TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                    route, buildApCost, includeReturn),
                Suitability = EconomyArmySuitability.Ineligible,
            };
            if (army == null)
                return choice;

            List<AiMapMemory.KnownEnemySighting> threats = EconomyRouteThreats(
                snap, army.Hex, target);
            bool atBase = snap?.Self?.BaseHexes?.Contains(army.Hex) == true;
            // EconomyRouteThreats already scans the whole corridor (direct + detour buffer) against
            // honestly-witnessed sightings — a clean route reported here is not a proximity guess,
            // it is the fog-honest answer. No separate base-adjacency requirement on top of it.
            bool safeRear = threats.Count == 0;
            int minimumEscort = safeRear ? 0 : 1;
            choice.MinimumEscortCount = minimumEscort;

            List<int> currentIndices = Enumerable.Range(0, army.Members?.Count ?? 0)
                .Where(i => i >= (army.NonHeroIsAviation?.Count ?? 0)
                    || !army.NonHeroIsAviation[i]).ToList();
            List<WorthIt.DefenderProfile> current = currentIndices
                .Select(i => army.Members[i]).ToList();
            if (EconomyRosterSafe(current, threats, minimumEscort))
            {
                List<int> retained = MinimumSafeEconomyEscortIndices(
                    army, threats, minimumEscort);
                // Field composition is immutable for Economy: a suitable field army travels as
                // one actor and must be priced whole. Only a Base/Citadel candidate may project
                // the minimum retained subset that Provisioning can actually unload atomically.
                if (!atBase)
                    retained = currentIndices;
                int smallest = retained?.Count ?? current.Count;
                choice.MinimumEscortCount = smallest;
                int knownBodyAp = army.NonHeroActivationApCosts?.Sum() ?? 0;
                int heroAp = army.HeroActivationApCost > 0
                    ? army.HeroActivationApCost
                    : Mathf.Max(0, route.ActivationApCost - knownBodyAp);
                int heroMove = army.HeroMoveMax > 0
                    ? army.HeroMoveMax : route.MaxMovement;
                choice.ProjectedActivationApCost = heroAp
                    + retained.Sum(i => i < army.NonHeroActivationApCosts.Count
                        ? army.NonHeroActivationApCosts[i] : 0);
                choice.ProjectedMaxMovement = retained.Count == 0
                    ? heroMove
                    : Mathf.Min(heroMove,
                        retained.Min(i => i < army.NonHeroMoveMax.Count
                            ? army.NonHeroMoveMax[i] : army.MaxMovement));
                EconomyBuilderRouteSnapshot projectedRoute = route;
                projectedRoute.ActivationApCost = choice.ProjectedActivationApCost;
                projectedRoute.MaxMovement = Mathf.Max(1, choice.ProjectedMaxMovement);
                choice.Route = projectedRoute;
                choice.TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                    projectedRoute, buildApCost, includeReturn);
                choice.Suitability = atBase && current.Count > smallest
                    ? EconomyArmySuitability.LightenAtBase
                    : EconomyArmySuitability.Ready;
                return choice;
            }

            // Field rosters are immutable for Economy. A deficient field army is rejected here,
            // before its AP reaches Allocation. Only a Base/Citadel garrison may supply the exact
            // minimum missing escort.
            if (!atBase || snap?.Self?.Armies == null)
                return choice;
            ArmySnapshot garrison = snap.Self.Armies.FirstOrDefault(a => a != null
                && a.IsGarrison && a.Hex.Equals(army.Hex));
            // Mirror ProvisioningManager.PlanEconomyArmyLightening's hard gate here: a garrison
            // already activated this turn cannot actually hand over an escort, so do not score
            // ReinforceAtBase as viable and let Provisioning discover that as AssemblyInfeasible
            // (which also burns a 2-turn structural cooldown on the whole delivery for nothing).
            if (garrison == null || garrison == army || garrison.HasActivatedThisTurn)
                return choice;
            List<WorthIt.DefenderProfile> reserve =
                garrison?.Members?.ToList() ?? new List<WorthIt.DefenderProfile>();
            List<int> reserveIndices = Enumerable.Range(0, reserve.Count)
                .Where(i => i >= (garrison.NonHeroIsAviation?.Count ?? 0)
                    || !garrison.NonHeroIsAviation[i]).ToList();
            for (int add = 1; add <= reserve.Count; add++)
            {
                List<int> best = null;
                int bestAp = int.MaxValue;
                int bestMove = int.MinValue;
                foreach (List<int> subset in Combinations(reserveIndices, add))
                {
                    var projected = new List<WorthIt.DefenderProfile>(current);
                    projected.AddRange(subset.Select(i => reserve[i]));
                    if (!EconomyRosterSafe(projected, threats, minimumEscort))
                        continue;
                    int addedAp = subset.Sum(i => i < garrison.NonHeroActivationApCosts.Count
                        ? garrison.NonHeroActivationApCosts[i] : 0);
                    int addedMove = subset.Min(i => i < garrison.NonHeroMoveMax.Count
                        ? garrison.NonHeroMoveMax[i] : garrison.MaxMovement);
                    if (best == null || addedAp < bestAp
                        || (addedAp == bestAp && addedMove > bestMove))
                    {
                        best = subset;
                        bestAp = addedAp;
                        bestMove = addedMove;
                    }
                }
                if (best != null)
                {
                    choice.MinimumEscortCount = current.Count + best.Count;
                    choice.Suitability = EconomyArmySuitability.ReinforceAtBase;
                    choice.ProjectedActivationApCost = route.ActivationApCost + bestAp;
                    choice.ProjectedMaxMovement = Mathf.Min(route.MaxMovement,
                        bestMove > 0 ? bestMove : route.MaxMovement);
                    EconomyBuilderRouteSnapshot projectedRoute = route;
                    projectedRoute.ActivationApCost = choice.ProjectedActivationApCost;
                    projectedRoute.MaxMovement = Mathf.Max(1, choice.ProjectedMaxMovement);
                    choice.Route = projectedRoute;
                    choice.TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                        projectedRoute, buildApCost, includeReturn);
                    return choice;
                }
            }
            return choice;
        }

        internal static List<AiMapMemory.KnownEnemySighting> EconomyRouteThreats(
            WorldSnapshot snapshot, HexCoord from, HexCoord target)
        {
            int direct = HexGridMath.Distance(from, target);
            return (snapshot?.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snapshot?.Known?.NeutralSightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(enemy => HexGridMath.Distance(from, enemy.Hex)
                    + HexGridMath.Distance(enemy.Hex, target) <= direct + 2)
                .ToList();
        }

        internal static bool EconomyRosterSafe(
            IReadOnlyList<WorthIt.DefenderProfile> roster,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            if ((roster?.Count ?? 0) < minimumEscort)
                return false;
            if (threats == null || threats.Count == 0)
                return true;
            if (threats.Any(t => t.Defenders == null || t.Defenders.Count == 0))
                return false;
            return threats.All(t => WorthIt.CanDamageAll(roster, t.Defenders)
                && WorthIt.WinChance(roster, t.Defenders, 0f)
                    >= AiConfig.defenceActiveWinChance);
        }

        internal static int MinimumSafeEconomyEscortCount(
            IReadOnlyList<WorthIt.DefenderProfile> roster,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            List<WorthIt.DefenderProfile> pool = roster?.ToList()
                ?? new List<WorthIt.DefenderProfile>();
            for (int count = Mathf.Max(0, minimumEscort); count <= pool.Count; count++)
                if (Combinations(pool, count).Any(x =>
                        EconomyRosterSafe(x, threats, minimumEscort)))
                    return count;
            return int.MaxValue;
        }

        private static List<int> MinimumSafeEconomyEscortIndices(ArmySnapshot army,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            IReadOnlyList<WorthIt.DefenderProfile> pool = army?.Members
                ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<int> indices = Enumerable.Range(0, pool.Count)
                .Where(i => i >= (army.NonHeroIsAviation?.Count ?? 0)
                    || !army.NonHeroIsAviation[i]).ToList();
            for (int count = Mathf.Max(0, minimumEscort); count <= indices.Count; count++)
            {
                List<int> best = null;
                int bestAp = int.MaxValue;
                int bestMove = int.MinValue;
                foreach (List<int> subset in Combinations(indices, count))
                {
                    List<WorthIt.DefenderProfile> roster = subset.Select(i => pool[i]).ToList();
                    if (!EconomyRosterSafe(roster, threats, minimumEscort))
                        continue;
                    int ap = subset.Sum(i => i < army.NonHeroActivationApCosts.Count
                        ? army.NonHeroActivationApCosts[i] : 0);
                    int move = subset.Count == 0 ? army.HeroMoveMax
                        : subset.Min(i => i < army.NonHeroMoveMax.Count
                            ? army.NonHeroMoveMax[i] : army.MaxMovement);
                    if (best == null || ap < bestAp || (ap == bestAp && move > bestMove))
                    {
                        best = subset;
                        bestAp = ap;
                        bestMove = move;
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
        }

        private static IEnumerable<List<T>> Combinations<T>(IReadOnlyList<T> source,
            int count, int start = 0, List<T> prefix = null)
        {
            prefix ??= new List<T>();
            if (prefix.Count == count)
            {
                yield return new List<T>(prefix);
                yield break;
            }
            for (int i = start; i <= source.Count - (count - prefix.Count); i++)
            {
                prefix.Add(source[i]);
                foreach (List<T> result in Combinations(source, count, i + 1, prefix))
                    yield return result;
                prefix.RemoveAt(prefix.Count - 1);
            }
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

        // Economy borrows an actor, not its entire combat value. Idle/current-Economy builders
        // lose no active mission; a permitted Recon/Raid loan pays the existing continuation loss.
        private static float EconomyMissionOpportunityCost(EconomyBuilderChoice builder,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            if (builder?.Army == null)
                return 0f;
            MissionIntent assignment = ActiveAssignment(activeIntents, builder.Army.ArmyId);
            return assignment == null || assignment.Kind == MissionKind.Economy
                ? 0f
                : AiConfigV2.economyLoanContinuationLoss;
        }

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
                : Mathf.Max(0f, resourceCost) / expectedIncomeGain;

        private static float EconomyResourcePriority(EconomyResourceStanding standing)
        {
            float handShortfall = standing.HandResourceNeed <= AiConfigV2.allocatorSliceEpsilon
                ? 0f
                : Mathf.Clamp01((standing.HandResourceNeed - standing.SpendableStockpile)
                    / standing.HandResourceNeed);
            float operationalShortfall =
                standing.ReservedOperationalNeed <= AiConfigV2.allocatorSliceEpsilon
                    ? 0f
                    : Mathf.Clamp01((standing.ReservedOperationalNeed
                            - standing.SpendableStockpile)
                        / standing.ReservedOperationalNeed);
            return Mathf.Max(standing.DeficitScore, standing.StarvationPressure,
                handShortfall, operationalShortfall);
        }

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
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            out int noBuilder, out int strategicValueRejected,
            out int deliveryValueRejected, out int thresholdRejected)
        {
            noBuilder = 0;
            strategicValueRejected = 0;
            deliveryValueRejected = 0;
            thresholdRejected = 0;
            List<CardData> baseCards = (s.Self.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition?.cardType == CardType.Base)
                .OrderBy(c => c.Definition.authoredKey ?? c.Definition.displayName)
                .ToList();
            if (baseCards.Count == 0 || s.Economy?.BaseOpportunities == null)
            {
                MissionIntentRegistry.GetOrCreate(player)
                    .MarkBaseExpansionCandidate(s.TurnNumber, null, null,
                        structurallyEligible: false);
                return "considered=0 kept=0 reason=no_base_card_or_opportunity";
            }

            int considered = 0;
            int kept = 0;
            AxisDemand best = null;
            var meaningfulDemands = new List<AxisDemand>();

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
                        + AiConfigV2.economyBaseForwardProgressValue * site.ForwardProgressValue
                        + AiConfigV2.economyBaseCorridorAlignmentValue * site.CorridorAlignmentValue
                        + AiConfigV2.economyBaseGlobalEffectValue * global;
                    bool meaningful = reasonValue > AiConfigV2.allocatorSliceEpsilon || committed;
                    if (!meaningful)
                    {
                        strategicValueRejected++;
                        continue;
                    }

                    EconomyBuilderChoice builder = SelectEconomyBuilder(
                        s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                        reasonValue, card.EffectivePlayApCost, includeReturn: false);
                    bool structuralRoute = HasStructuralEconomyBuilderRoute(
                        s, site.Hex, site.BuilderRoutes);
                    if (!structuralRoute)
                    {
                        noBuilder++;
                        continue;
                    }

                    float travel = builder?.Route.TravelCost
                        ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                    float exposure = ThreatExposure(s, site.Hex);
                    float heroCost = EconomyMissionOpportunityCost(builder, activeIntents);
                    float assignmentAp = builder?.TotalAssignmentApCost
                        ?? card.EffectivePlayApCost;
                    float intrinsicBuildCost = card.EffectivePlayApCost
                            * AiConfigV2.economyBuildApPenalty
                        + ResourceCostSum(card.EffectivePlayResourceCost)
                            * AiConfigV2.economyBuildResourcePenalty;
                    float deliveryApCost = Mathf.Max(0f,
                            assignmentAp - card.EffectivePlayApCost)
                        * AiConfigV2.economyBuildApPenalty;
                    float strategicValue = reasonValue - intrinsicBuildCost
                        - AiConfigV2.economySiteThreatPenalty * exposure;
                    float value = strategicValue - deliveryApCost
                        - AiConfigV2.economySiteTravelPenalty * travel
                        - Mathf.Max(0f, heroCost);
                    AiDebugLog.WriteVerbose($"[AI][V2][Economy][BaseCandidate] "
                        + $"card={card.Definition.displayName} target=({site.Hex.Q},{site.Hex.R}) "
                        + $"reason={reasonValue:0.##} buildCost={intrinsicBuildCost:0.##} "
                        + $"deliveryApCost={deliveryApCost:0.##} site={strategicValue:0.##} "
                        + $"delivery={value:0.##} committed={committed} decision=stage");

                    meaningfulDemands.Add(new AxisDemand
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
                        EconomySiteValue = strategicValue,
                        EconomyTravelCost = travel,
                        EconomyThreatExposure = exposure,
                        EconomyHeroOpportunityCost = heroCost,
                        EconomyAssignmentApCost = assignmentAp,
                        EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                        EconomyProjectedActivationApCost = builder?.ProjectedActivationApCost ?? 0,
                        EconomyProjectedMaxMovement = builder?.ProjectedMaxMovement ?? 0,
                        EconomyBuilderRoutes = site.BuilderRoutes,
                        Value = value,
                        Explain = $"Base reason={reasonValue:0.##} capacity={site.CapacityValue:0.##} "
                            + $"yield={hexYield:0.##} cluster={site.NearbyResourceClusterValue:0.##} "
                            + $"network={site.NetworkExpansionValue:0.##} "
                            + $"pressure={site.InfrastructurePressure:0.##} airfield={airfield:0.##} "
                            + $"logistics={site.LogisticsValue:0.##} forward={site.ForwardProgressValue:0.##} "
                            + $"corridor={site.CorridorAlignmentValue:0.##} global={global:0.##} "
                            + $"buildCost={intrinsicBuildCost:0.##} deliveryApCost={deliveryApCost:0.##}",
                    });
                }

            // Stage the best meaningful, legal and safely-routable Base before value admission.
            // This is what lets the existing continuity urgency accumulate from a negative score.
            AxisDemand stagedBase = meaningfulDemands
                .Where(d => d.EconomyPreferredBuilderArmyId.HasValue)
                .OrderByDescending(d => IsActiveBaseCommitment(
                    activeIntents, d.TargetHex, d.EconomyBuildCard) ? 1 : 0)
                .ThenByDescending(d => d.Value)
                .ThenByDescending(d => d.EconomySiteValue)
                .ThenBy(d => d.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(d => d.TargetHex?.R ?? int.MaxValue)
                .FirstOrDefault();
            bool urgencyEligible = stagedBase?.TargetHex != null
                && HasStructuralEconomyBuilderRoute(
                    s, stagedBase.TargetHex.Value, stagedBase.EconomyBuilderRoutes);
            float urgency = MissionIntentRegistry.GetOrCreate(player)
                .MarkBaseExpansionCandidate(s.TurnNumber, stagedBase?.EconomyBuildCard,
                    stagedBase?.TargetHex, urgencyEligible);

            foreach (AxisDemand demand in meaningfulDemands)
            {
                bool staged = stagedBase != null
                    && demand.EconomyBuildCard == stagedBase.EconomyBuildCard
                    && demand.TargetHex.Equals(stagedBase.TargetHex);
                float candidateUrgency = staged ? urgency : 0f;
                demand.EconomyStrategicUrgency = candidateUrgency;
                if (candidateUrgency > 0f)
                    demand.Explain += $" urgency={candidateUrgency:0.##}";

                bool committed = IsActiveBaseCommitment(
                    activeIntents, demand.TargetHex, demand.EconomyBuildCard);
                bool admitted = committed || demand.Value + candidateUrgency
                    >= AiConfigV2.economyBaseDemandMinValue;
                AiDebugLog.WriteVerbose($"[AI][V2][Economy][BaseAdmission] "
                    + $"card={demand.EconomyBuildCard?.Definition?.displayName} "
                    + $"target=({demand.TargetHex?.Q},{demand.TargetHex?.R}) "
                    + $"value={demand.Value:0.##} urgency={candidateUrgency:0.##} "
                    + $"committed={committed} decision={(admitted ? "keep" : "defer")}");
                if (!admitted)
                {
                    if (!demand.EconomyPreferredBuilderArmyId.HasValue)
                        noBuilder++;
                    else if (demand.EconomySiteValue <= AiConfigV2.allocatorSliceEpsilon)
                        strategicValueRejected++;
                    else if (demand.Value <= AiConfigV2.allocatorSliceEpsilon)
                        deliveryValueRejected++;
                    else
                        thresholdRejected++;
                    continue;
                }

                output.Add(demand);
                kept++;
                if (best == null || demand.Value + demand.EconomyStrategicUrgency
                    > best.Value + best.EconomyStrategicUrgency)
                    best = demand;
            }

            return best == null
                ? $"considered={considered} kept={kept} best=none "
                    + $"wait={MissionIntentRegistry.GetOrCreate(player).BaseExpansionWaitTurns} urgency={urgency:0.##}"
                : $"considered={considered} kept={kept} best={best.EconomyBuildCard.Definition.displayName} "
                    + $"target=({best.TargetHex?.Q},{best.TargetHex?.R}) value={best.EconomySiteValue:0.##} "
                    + $"wait={MissionIntentRegistry.GetOrCreate(player).BaseExpansionWaitTurns} urgency={urgency:0.##}";
        }

        // Cross-family guard: extraction and base candidates are ranked/selected independently
        // (see EconomyDemands), so nothing else stops a fresh candidate of one family from
        // targeting a hex already owned by an active intent of the OTHER family. This is the
        // only place that checks across EconomyTaskKind.
        private static bool HasActiveEconomyIntentAtHexOfKind(IReadOnlyList<MissionIntent> intents,
            HexCoord? target, EconomyTaskKind kind)
        {
            if (!target.HasValue || intents == null)
                return false;
            return intents.Any(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.Economy && i.Economy?.Kind == kind
                && i.Economy.TargetHex.Equals(target.Value));
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

        private static bool HasActiveEconomyBuildIntent(
            IReadOnlyList<MissionIntent> intents, AxisDemand demand)
        {
            if (demand?.TargetHex == null || intents == null)
                return false;
            EconomyTaskKind kind = demand.Capability == CapabilityKind.EconomicExpansionBase
                ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
            return intents.Any(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId.HasValue
                && i.Economy?.Kind == kind
                && i.Economy.TargetHex.Equals(demand.TargetHex.Value)
                && (kind == EconomyTaskKind.FoundBase
                    || i.Economy.ResourceType == demand.EconomyResourceType));
        }

        private static bool HasStructuralEconomyBuilderRoute(WorldSnapshot snap, HexCoord target,
            IReadOnlyList<EconomyBuilderRouteSnapshot> routes)
        {
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = routes
                ?? SnapshotFallbackRoutes(snap, target);
            return witnessed.Any(route => route.TravelCost < int.MaxValue
                && (snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>()).Any(army =>
                    army != null && army.ArmyId == route.ArmyId
                    && (army.IsMobileEconomyBuilder
                        || (route.IsOnTarget && army.IsGarrison && army.HasHero))));
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
            IReadOnlyList<DevelopmentOpportunity> devOpportunities, Radar radar,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player)
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
                    if (!HasSupportedDevelopmentAxisDemand(
                        op, formedDemands, activeIntents, player))
                    {
                        AiDebugLog.Write($"[AI][V2][Demand][Development] decision=REJECT "
                            + $"card={op.Card?.displayName ?? "?"} recipient={op.RecipientLabel ?? "?"} "
                            + "reason=no_supported_axis_demand");
                        continue;
                    }
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


        // Production amplifies an already-owned need; it never originates one. In the current
        // scope only a real Recon capability delta or the exact builder of an Economy obligation
        // is a valid witness. Attack/Defence matching remains with WorthIt when those axes return.
        internal static bool HasSupportedDevelopmentAxisDemand(DevelopmentOpportunity op,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player)
        {
            if (op == null)
                return false;

            bool hasReconDemand = formedDemands?.Any(d => d != null
                && d.RequestingAxis == DesireAxis.Recon
                && d.Capability == CapabilityKind.ScoutCapability) == true;
            if (op.RecipientKind == DevRecipientKind.HandCard)
                return hasReconDemand && ImprovesReconCapability(op);

            if (op.RecipientUnit == null || player == null)
                return false;
            ArmyData army = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => a?.Members != null && a.Members.Contains(op.RecipientUnit));
            if (army == null)
                return false;

            bool economyWitness = formedDemands?.Any(d => d != null
                    && d.RequestingAxis == DesireAxis.Economy
                    && d.EconomyPreferredBuilderArmyId == army.Id) == true
                || activeIntents?.Any(i => i != null && i.Status == IntentStatus.Active
                    && i.Kind == MissionKind.Economy
                    // A builder already walking home (ReturnBuilder) has no outstanding build
                    // obligation left — it cannot justify a fresh Production/CardUpgrade demand.
                    && (i.Economy?.Kind == EconomyTaskKind.BuildExtraction
                        || i.Economy?.Kind == EconomyTaskKind.FoundBase)
                    && (i.PreferredMoverArmyId == army.Id
                        || i.Economy?.BuilderArmyId == army.Id)) == true;
            if (economyWitness)
                return true;

            bool reconWitness = activeIntents?.Any(i => i != null
                && i.Status == IntentStatus.Active && i.Kind == MissionKind.Scout
                && i.PreferredMoverArmyId == army.Id) == true;
            return reconWitness && ImprovesReconCapability(op);
        }

        private static bool ImprovesReconCapability(DevelopmentOpportunity op)
        {
            EquipmentGrant grant = op?.Card?.equipment;
            if (grant == null)
                return false;

            IEnumerable<string> beforeAbilities;
            int beforeMove;
            int beforeActivation;
            if (op.RecipientKind == DevRecipientKind.HandCard)
            {
                CardDefinition host = op.RecipientCard?.Definition;
                if (host == null)
                    return false;
                beforeAbilities = EquipmentSystem.EffectiveAbilities(
                    host.grantedAbilities, op.RecipientCard.Equipment?.equipment);
                beforeMove = host.moveMax;
                beforeActivation = host.activationApCost;
            }
            else
            {
                if (op.RecipientUnit == null)
                    return false;
                beforeAbilities = op.RecipientUnit.Abilities;
                beforeMove = op.RecipientUnit.MoveMax;
                beforeActivation = op.RecipientUnit.ActivationApCost;
            }

            var beforeStats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.MoveMax] = beforeMove,
                [EquipmentStat.ActivationApCost] = beforeActivation,
            };
            PredictedEquipmentState after = EquipmentSystem.Predict(
                grant, beforeStats, beforeAbilities);
            int afterMove = after.Stats.TryGetValue(EquipmentStat.MoveMax, out int move)
                ? move : beforeMove;
            int afterActivation = after.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int activation)
                ? activation : beforeActivation;
            return AbilityParams.GetBestRecceRadius(after.Abilities)
                    > AbilityParams.GetBestRecceRadius(beforeAbilities)
                || AbilityParams.GetBestRecceSpotStrength(after.Abilities)
                    > AbilityParams.GetBestRecceSpotStrength(beforeAbilities)
                || BestStealthLevel(after.Abilities) > BestStealthLevel(beforeAbilities)
                || afterMove > beforeMove
                || afterActivation < beforeActivation;
        }

        private static int BestStealthLevel(IEnumerable<string> abilities)
        {
            int best = 0;
            if (abilities == null)
                return best;
            foreach (string ability in abilities)
                if (AbilityParams.TryGetStealthLevel(ability, out int level))
                    best = System.Math.Max(best, level);
            return best;
        }

    }
}
