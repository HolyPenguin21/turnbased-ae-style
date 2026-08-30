using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DEMAND LAYER  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  Converts the turn's FROZEN strategic evaluation into concrete AxisDemand[] — capability
    //  SHORTAGES, never card requests. Axes say WHAT is missing; StrategicManager decides HOW.
    //
    //  Recon and Aggression/Raid are wired. Defence / Economy / Development remain explicit no-op
    //  hooks until their own V2 objectives land — V2 still owns the AI turn while those directions
    //  are intentionally absent.
    //
    //  Recon: sizes required Scout capacity from the ONE Recon-objective enumeration
    //  (ReconObjectiveEvaluator), NOT a private duplicate scout-target estimator. Objectives
    //  already covered by a valid active Recon intent do NOT need new capacity, and a solo Recce
    //  claimed by an active operation is "existing", not "available".
    // ===========================================================================================
    public static class DemandLayer
    {
        public static List<AxisDemand> Generate(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<ReconObjective> objectives, IReadOnlyList<AggressionObjective> aggressionObjectives,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player)
        {
            var demands = new List<AxisDemand>();
            demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player));
            demands.AddRange(AggressionDemands(snap, breakdown, aggressionObjectives, activeIntents, commitments, player));
            demands.AddRange(DefenceDemands(snap, breakdown));
            demands.AddRange(EconomyDemands(snap, breakdown));
            demands.AddRange(DevelopmentDemands(snap, breakdown));

            foreach (AxisDemand d in demands)
                AiDebugLog.Write($"[AI][V2]   demand — {d} | {d.Explain}");
            return demands;
        }

        // --------------------------------------------------------------------------- Recon ----
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

            // An objective is only COVERED when a live intent tracks it AND that intent's committed
            // actor is still STRUCTURALLY capable of running it. ActorCommitments already encodes
            // exactly that test (it only claims a mover whose actor is a solo Recce still capable
            // of the intent's real stealth requirement), so "is this objective covered" reduces to
            // "is the intent's mover claimed" — one source of truth, no second capability check.
            var coveredKeys = new HashSet<MissionIntentKey>();
            int activeReconExecutions = 0;
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        continue;
                    coveredKeys.Add(i.IntentKey);
                    activeReconExecutions++;
                }

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

            int remainingConcurrency = Mathf.Max(0, AiConfigV2.maxConcurrentReconExecutions - activeReconExecutions);
            if (remainingConcurrency == 0)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=DEFER reason=execution_lane_capacity "
                    + $"active={activeReconExecutions} max={AiConfigV2.maxConcurrentReconExecutions} uncovered={uncovered.Count}");
                yield break;
            }

            int requiredNewExecutions = Mathf.Min(remainingConcurrency, uncovered.Count);
            List<ReconObjective> topN = uncovered.Take(requiredNewExecutions).ToList();

            // Split by capability PROFILE — a stealth-Required objective needs a stealth scout; a
            // plain Explore is fine with any scout. Emitting one blanket stealth demand would make
            // the AI build stealth scouts to cover plain jobs an ordinary existing scout already
            // handles.
            int stealthNeeded = topN.Count(o =>
                o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);
            int genericNeeded = requiredNewExecutions - stealthNeeded;

            var claimed = commitments?.ClaimedArmyIdSet;
            int stealthSupply = ScoutMoverSelector.Eligible(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.Required }, claimed).Count;
            int anySupply = ScoutMoverSelector.Eligible(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.None }, claimed).Count;

            // Stealth jobs consume stealth scouts first; whatever stealth scouts are left plus the
            // non-stealth eligible scouts cover the generic jobs.
            int missStealth = Mathf.Max(0, stealthNeeded - stealthSupply);
            int stealthLeftover = Mathf.Max(0, stealthSupply - stealthNeeded);
            int genericSupply = Mathf.Max(0, anySupply - stealthSupply) + stealthLeftover;
            int missGeneric = Mathf.Max(0, genericNeeded - genericSupply);

            // FIXED mission overhead only (actor-independent). Recon has none — the deployed
            // scout's own activation AP and the stealth surcharge are added per candidate by
            // StrategicManager from the card + RequiredTraits.
            const float reconFixedOverheadAp = 0f;

            if (missStealth > 0)
            {
                ReconObjective best = topN.FirstOrDefault(o =>
                    o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f) ?? uncovered[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=stealth desired={missStealth} reason=insufficient_free_stealth_scouts "
                    + $"jobs={stealthNeeded} free={stealthSupply} target=({best.FocusHex.Q},{best.FocusHex.R})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = missStealth,
                    RequiredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    Explain = $"{stealthNeeded} stealth job(s), {stealthSupply} stealth scout(s) free, miss {missStealth}",
                };
            }

            if (missGeneric > 0)
            {
                ReconObjective best = topN.FirstOrDefault(o =>
                    o.Stealth != StealthRequirement.Required && !(o.DetectionRisk > 0f)) ?? uncovered[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=generic desired={missGeneric} reason=insufficient_free_scouts jobs={genericNeeded} "
                    + $"free={genericSupply} anyFree={anySupply} stealthFree={stealthSupply} "
                    + $"target=({best.FocusHex.Q},{best.FocusHex.R})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = missGeneric,
                    RequiredTraits = TraitPreference.None,
                    PreferredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    Explain = $"{genericNeeded} generic job(s), {genericSupply} scout(s) free "
                        + $"(any {anySupply}, stealth {stealthSupply}), miss {missGeneric}",
                };
            }

            if (missStealth == 0 && missGeneric == 0)
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=SATISFIED reason=free_scout_supply_covers_open_slots "
                    + $"jobs={requiredNewExecutions} anyFree={anySupply} stealthFree={stealthSupply}");
        }

        // --------------------------------------------------------------------- Aggression ----
        //  Reads the SAME frozen AggressionObjective[] AggressionMissionLayer will read — no second
        //  target scan. Strategic feasibility stored on the frozen objective is only the starting
        //  point: once durable intents are resolved, capability supply must be recalculated from
        //  FREE actors. A field army/hero committed to an active mission is not spare Raid supply,
        //  and a Hero card in hand is not a deployed leader until StrategicManager materialises it.
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
            bool chosenNeedsPower = false;
            bool chosenNeedsHero = false;
            float chosenPowerDeficit = 0f;
            float chosenRequiredPower = 0f;
            string chosenPowerReason = null;

            foreach (AggressionObjective o in objectives
                .OrderByDescending(x => x.BaseValue)
                .ThenBy(x => x.TargetArmyId))
            {
                if (coveredTargets.Contains(o.TargetArmyId))
                    continue;

                float requiredPower = Mathf.Max(1f, o.TargetPower * AiConfigV2.raidCombatPowerMargin);
                float numericDeficit = Mathf.Max(0f, requiredPower - inv.RaidAvailableFieldPower);
                bool coverageMissing = !o.CanCoverAllDefenders;
                bool projectedWinMissing = o.AssemblableWinChance < AiConfigV2.raidMinViableWinChance;
                bool needsPower = coverageMissing || projectedWinMissing
                    || numericDeficit > AiConfigV2.allocatorSliceEpsilon;

                // A ready single stack that already clears the shared estimator may raid without a
                // hero. Otherwise a fresh/strengthened raid needs a DEPLOYED free hero. The
                // strategic report's HeroAvailable also counts cards in hand; CapabilityInventory
                // intentionally does not, so this is the point that creates the Phase-A Hero demand.
                bool readyCanRaidWithoutHero = o.CanCoverAllDefenders
                    && o.ReadyWinChance >= AiConfigV2.raidMinViableWinChance;
                bool needsHero = inv.AvailableHeroes <= 0 && !readyCanRaidWithoutHero;

                if (!needsPower && !needsHero)
                {
                    AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=SATISFIED targetArmy={o.TargetArmyId} "
                        + $"reason=free_capability_sufficient freePower={inv.RaidAvailableFieldPower:0.#} "
                        + $"requiredPower={requiredPower:0.#} freeHeroes={inv.AvailableHeroes} "
                        + $"readyWin={o.ReadyWinChance:0.00} asmWin={o.AssemblableWinChance:0.00} "
                        + $"cover={(o.CanCoverAllDefenders ? 1 : 0)}");
                    continue;
                }

                chosen = o;
                chosenNeedsPower = needsPower;
                chosenNeedsHero = needsHero;
                chosenRequiredPower = requiredPower;
                chosenPowerDeficit = needsPower ? Mathf.Max(1f, numericDeficit) : 0f;
                chosenPowerReason = coverageMissing ? "defender_coverage_missing"
                    : projectedWinMissing ? "projected_win_below_threshold"
                    : "free_field_power_below_requirement";
                break;
            }

            if (chosen == null)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=SATISFIED reason=no_uncovered_capability_shortage "
                    + $"freePower={inv.RaidAvailableFieldPower:0.#} committedPower={inv.CommittedFieldCombatPower:0.#} "
                    + $"freeHeroes={inv.AvailableHeroes} committedHeroes={inv.CommittedHeroes}");
                yield break;
            }

            if (chosenNeedsHero)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=CREATE targetArmy={chosen.TargetArmyId} "
                    + $"capability=Hero desired=1 reason=no_free_deployed_hero freeHeroes={inv.AvailableHeroes} "
                    + $"committedHeroes={inv.CommittedHeroes} readyWin={chosen.ReadyWinChance:0.00}");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.Hero,
                    DesiredAmount = 1,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = chosen.LastKnownHex,
                    Value = chosen.BaseValue,
                    Explain = $"raid #{chosen.TargetArmyId} needs a free deployed hero; "
                        + $"free {inv.AvailableHeroes}, committed {inv.CommittedHeroes}",
                };
            }

            if (chosenNeedsPower)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Aggression] decision=CREATE targetArmy={chosen.TargetArmyId} "
                    + $"capability=FieldCombatPower desired={chosenPowerDeficit:0.#} reason={chosenPowerReason} "
                    + $"freePower={inv.RaidAvailableFieldPower:0.#} committedPower={inv.CommittedFieldCombatPower:0.#} "
                    + $"requiredPower={chosenRequiredPower:0.#} readyWin={chosen.ReadyWinChance:0.00} "
                    + $"asmWin={chosen.AssemblableWinChance:0.00} cover={(chosen.CanCoverAllDefenders ? 1 : 0)}");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DesiredAmount = chosenPowerDeficit,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = chosen.LastKnownHex,
                    Value = chosen.BaseValue,
                    Explain = $"raid #{chosen.TargetArmyId} needs ~{chosenPowerDeficit:0.#} more free field power "
                        + $"({chosenPowerReason}; free {inv.RaidAvailableFieldPower:0.#}, "
                        + $"committed {inv.CommittedFieldCombatPower:0.#}, required {chosenRequiredPower:0.#})",
                };
            }
        }

        // ------------------------------------------------------- extensible axis hooks ----
        //  Kept as explicit no-op methods so the wiring point for each future axis is visible.
        private static IEnumerable<AxisDemand> DefenceDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
        private static IEnumerable<AxisDemand> EconomyDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
    }
}
