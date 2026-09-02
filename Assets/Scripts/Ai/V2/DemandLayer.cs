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
            ActorCommitments commitments, PlayerSetupData player)
        {
            var demands = new List<AxisDemand>();
            // §17 — decay the resource-starvation feedback once per turn before it is read.
            if (player != null && snap != null)
                ResourceStarvationRegistry.DecayOncePerTurn(player, snap.TurnNumber);
            demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player));
            demands.AddRange(AggressionDemands(snap, breakdown, aggressionObjectives, activeIntents, commitments, player));
            demands.AddRange(DefenceDemands(snap, breakdown));
            demands.AddRange(EconomyDemands(snap, breakdown, player));
            demands.AddRange(DevelopmentDemands(snap, breakdown));
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

        private static IEnumerable<AxisDemand> ReconDemands(WorldSnapshot snap,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player)
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
                snap, observationRunnable, groundVisitRunnable, activeIntents, commitments, player);
            AiDebugLog.Write($"[AI][V2][Demand][Recon] capacity {capacity.Explain} "
                + $"active={activeReconExecutions} hard={ReconConcurrencyPolicy.HardCap} "
                + $"runnable={runnable.Count} (obs={observationRunnable.Count} groundVisit={groundVisitRunnable.Count} "
                + $"stealth={stealthRunnable.Count}) blocked={blocked}");

            // --- Stealth lane: its own value/coverage estimate vs free stealth-capable movers. Not
            //     persistence-gated (a stealth job with no stealth actor is a real capability gap,
            //     not stage flicker) and not reduced by aviation or generic scouts.
            var claimed = commitments?.ClaimedArmyIdSet;
            int stealthFree = ScoutMoverSelector.Eligible(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.Required }, claimed).Count;
            int desiredStealthLanes = Mathf.Min(stealthRunnable.Count,
                ReconConcurrencyPolicy.DesiredForClass(snap, stealthObsRunnable,
                    ReconConcurrencyPolicy.ReconCoverageClass.Observation)
                + ReconConcurrencyPolicy.DesiredForClass(snap, stealthGroundRunnable,
                    ReconConcurrencyPolicy.ReconCoverageClass.GroundTraversal));
            int missStealth = Mathf.Max(0, desiredStealthLanes - stealthFree);

            // --- Generic (non-stealth) capacity deficits, persistence-gated.
            bool obsPersist = ReconCapacityDeficitRegistry.RegisterAndCheck(
                player, turn, ReconDeficitKind.Observation, capacity.ObservationDeficit, out int obsStreak);
            bool groundPersist = ReconCapacityDeficitRegistry.RegisterAndCheck(
                player, turn, ReconDeficitKind.GroundTraversal, capacity.GroundTraversalDeficit, out int groundStreak);
            int obsNew = obsPersist ? capacity.ObservationDeficit : 0;
            int groundNew = groundPersist ? capacity.GroundTraversalDeficit : 0;

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

            const float reconFixedOverheadAp = 0f;

            if (stealthNew <= 0 && genericNew <= 0)
            {
                string reason =
                    missStealth > 0 || capacity.ObservationDeficit > 0 || capacity.GroundTraversalDeficit > 0
                        ? (obsNew + groundNew == 0 && missStealth == 0
                            ? "capacity_deficit_not_yet_persistent"
                            : "concurrency_hard_cap_or_useful_ceiling_reached")
                        : "usable_capacity_covers_all_lanes";
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=DEFER reason={reason} "
                    + $"obsDeficit={capacity.ObservationDeficit}(persist={(obsPersist ? 1 : 0)} streak={obsStreak}) "
                    + $"groundTraversalDeficit={capacity.GroundTraversalDeficit}(persist={(groundPersist ? 1 : 0)} streak={groundStreak}) "
                    + $"missStealth={missStealth} stealthFree={stealthFree} active={activeReconExecutions} "
                    + $"hard={ReconConcurrencyPolicy.HardCap} combinedCeiling={capacity.CombinedDesiredConcurrency} "
                    + $"existingGroundUsable={capacity.ExistingGroundUsableCapacity} usefulGenericRoom={usefulGenericRoom} "
                    + $"groundFloor={groundNew} roomForNew={roomForNew} blocked={blocked}");
                yield break;
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

            if (genericNew > 0)
            {
                var genericPool = groundPart > 0 ? groundVisitRunnable : observationRunnable;
                ReconObjective best = genericPool.FirstOrDefault(o => !IsStealthObjective(o))
                    ?? runnable.FirstOrDefault(o => !IsStealthObjective(o)) ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=generic desired={genericNew} reason=persistent_usable_capacity_deficit "
                    + $"obsDeficit={capacity.ObservationDeficit}(streak={obsStreak}) "
                    + $"groundTraversalDeficit={capacity.GroundTraversalDeficit}(streak={groundStreak}) "
                    + $"airborneAir={capacity.AirborneReconLanes} spareAir={capacity.SpareAirObservationSorties} "
                    + $"combinedCeiling={capacity.CombinedDesiredConcurrency} existingGroundUsable={capacity.ExistingGroundUsableCapacity} "
                    + $"groundPart={groundPart} obsPart={obsPart} runnable={runnable.Count} blocked={blocked} "
                    + $"target=({best.FocusHex.Q},{best.FocusHex.R})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = genericNew,
                    RequiredTraits = TraitPreference.None,
                    PreferredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    Explain = $"persistent usable-capacity deficit (obs {capacity.ObservationDeficit}, "
                        + $"groundTraversal {capacity.GroundTraversalDeficit}; airborneAir "
                        + $"{capacity.AirborneReconLanes}, spareAir {capacity.SpareAirObservationSorties}); "
                        + $"want {genericNew}; blocked {blocked}",
                };
            }
        }

        private static IEnumerable<AxisDemand> AggressionDemands(WorldSnapshot snap, DesireBreakdown b,
            IReadOnlyList<AggressionObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player)
        {
            if (snap?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Aggression] decision=NONE reason=no_self_snapshot");
                yield break;
            }
            if (objectives == null || objectives.Count == 0)
            {
                AiDebugLog.Write("[AI][V2][Demand][Aggression] decision=NONE reason=no_frozen_aggression_objectives");
                yield break;
            }

            var coveredTargets = new HashSet<int>();
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i?.Kind != MissionKind.Raid || i.Raid == null || i.PreferredMoverArmyId == null)
                        continue;
                    if (!commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        continue;
                    coveredTargets.Add(i.Raid.TargetArmyId);
                    AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=SATISFIED targetArmy={i.Raid.TargetArmyId} "
                        + $"reason=covered_by_active_raid actor={i.PreferredMoverArmyId.Value}");
                }

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            AggressionObjective chosen = null;
            RaidOperationalReadiness chosenReadiness = null;
            int blocked = 0;
            AiAllocatorState cooldownState = AiAllocatorStateRegistry.GetOrCreate(player);

            foreach (AggressionObjective o in objectives.OrderByDescending(x => x.BaseValue).ThenBy(x => x.TargetArmyId))
            {
                if (coveredTargets.Contains(o.TargetArmyId))
                    continue;
                StableMissionKey key = RaidKey(o);
                if (cooldownState.TryGetCooldown(key, snap.TurnNumber, out MissionCooldownInfo cd))
                {
                    blocked++;
                    AiDebugLog.Write($"[AI][V2][Demand][Aggression] blocked {key} reason={cd.Reason} "
                        + $"start=t{cd.StartedTurn} until=t{cd.UntilTurn} remaining={cd.RemainingAt(snap.TurnNumber)}");
                    continue;
                }

                RaidOperationalReadiness readiness = RaidOperationalReadiness.Evaluate(
                    snap, o, RaidDefenders(snap, o.TargetArmyId), commitments, inv);
                if (readiness.ReadyExecutable)
                {
                    AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=SATISFIED targetArmy={o.TargetArmyId} "
                        + $"reason=ready_free_army_clears_shared_readiness actor={readiness.ReadyPlan.BaseArmyId} "
                        + $"win={readiness.ReadyPlan.ProjectedWinChance:0.00} "
                        + $"cover={(readiness.ReadyPlan.CoversAllDefenders ? 1 : 0)} "
                        + $"freePower={inv.RaidAvailableFieldPower:0.#} requiredPower={readiness.RequiredPower:0.#} "
                        + $"frozenAsmWin={o.AssemblableWinChance:0.00}");
                    continue;
                }

                chosen = o;
                chosenReadiness = readiness;
                break;
            }

            if (chosen == null || chosenReadiness == null)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=SATISFIED reason=no_runnable_capability_shortage "
                    + $"objectives={objectives.Count} blocked={blocked} freePower={inv.RaidAvailableFieldPower:0.#} "
                    + $"committedPower={inv.CommittedFieldCombatPower:0.#} freeHeroes={inv.AvailableHeroes} "
                    + $"committedHeroes={inv.CommittedHeroes}");
                yield break;
            }

            if (chosenReadiness.NeedsAssembly)
            {
                // §11 — enough numeric power and a raid-eligible hero exist; the target is not
                // executable only because no legal same-hex formation clears the estimator. That
                // is an organization gap owned by RaidAssembly / Housekeeping / the bounded
                // re-admission — buying more FieldCombatPower would not help.
                AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=DEFER targetArmy={chosen.TargetArmyId} "
                    + $"reason=assembly_gap detail=\"{chosenReadiness.AssemblyReason}\" "
                    + $"freePower={inv.RaidAvailableFieldPower:0.#} requiredPower={chosenReadiness.RequiredPower:0.#} "
                    + $"freeHeroes={inv.AvailableHeroes} committedHeroes={inv.CommittedHeroes} blocked={blocked} "
                    + $"readyDetail=\"{chosenReadiness.ReadyReason}\"");
                yield break;
            }

            if (chosenReadiness.NeedsHero)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=CREATE targetArmy={chosen.TargetArmyId} "
                    + $"capability=Hero desired=1 reason=no_free_deployed_hero freeHeroes={inv.AvailableHeroes} "
                    + $"committedHeroes={inv.CommittedHeroes} blocked={blocked} readiness=REJECT "
                    + $"detail=\"{chosenReadiness.ReadyReason}\"");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.Hero,
                    DesiredAmount = 1,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = chosen.LastKnownHex,
                    Value = chosen.BaseValue,
                    Explain = $"raid #{chosen.TargetArmyId} needs a free deployed hero; free {inv.AvailableHeroes}, "
                        + $"committed {inv.CommittedHeroes}; blocked targets {blocked}; {chosenReadiness.ReadyReason}",
                };
            }

            if (chosenReadiness.NeedsPower)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=CREATE targetArmy={chosen.TargetArmyId} "
                    + $"capability=FieldCombatPower desired={chosenReadiness.RequestedPower:0.#} "
                    + $"reason={chosenReadiness.PowerReason} freePower={inv.RaidAvailableFieldPower:0.#} "
                    + $"committedPower={inv.CommittedFieldCombatPower:0.#} requiredPower={chosenReadiness.RequiredPower:0.#} "
                    + $"blocked={blocked} readiness=REJECT detail=\"{chosenReadiness.ReadyReason}\" "
                    + $"frozenReadyWin={chosen.ReadyWinChance:0.00} frozenAsmWin={chosen.AssemblableWinChance:0.00} "
                    + $"frozenCover={(chosen.CanCoverAllDefenders ? 1 : 0)}");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DesiredAmount = chosenReadiness.RequestedPower,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = chosen.LastKnownHex,
                    Value = chosen.BaseValue,
                    Explain = $"raid #{chosen.TargetArmyId} needs ~{chosenReadiness.RequestedPower:0.#} more free field capability "
                        + $"({chosenReadiness.PowerReason}; free {inv.RaidAvailableFieldPower:0.#}, committed "
                        + $"{inv.CommittedFieldCombatPower:0.#}, required {chosenReadiness.RequiredPower:0.#}; "
                        + $"blocked targets {blocked}; {chosenReadiness.ReadyReason})",
                };
            }
        }

        private static StableMissionKey ReconKey(ReconObjective o) =>
            new StableMissionKey(MissionKind.Scout,
                o.Kind == ReconObjectiveKind.Surveil ? (int)ScoutTargetKind.Surveil : (int)ScoutTargetKind.Explore,
                o.Kind == ReconObjectiveKind.Surveil ? o.ContactArmyId : 0,
                o.FocusHex.Q, o.FocusHex.R);

        private static StableMissionKey RaidKey(AggressionObjective o) =>
            new StableMissionKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, o.TargetArmyId, 0, 0);

        private static IReadOnlyList<WorthIt.DefenderProfile> RaidDefenders(WorldSnapshot snap, int targetArmyId)
        {
            if (snap?.Known == null || targetArmyId == 0)
                return System.Array.Empty<WorthIt.DefenderProfile>();
            IEnumerable<AiMapMemory.KnownEnemySighting> sightings =
                (snap.Known.EnemySightings ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>());
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                if (s.ArmyId == targetArmyId)
                    return s.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            return System.Array.Empty<WorthIt.DefenderProfile>();
        }

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
