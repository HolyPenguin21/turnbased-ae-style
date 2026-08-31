using System.Collections.Generic;
using System.Linq;
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
            demands.AddRange(ReconDemands(snap, objectives, activeIntents, commitments, player));
            demands.AddRange(AggressionDemands(snap, breakdown, aggressionObjectives, activeIntents, commitments, player));
            demands.AddRange(DefenceDemands(snap, breakdown));
            demands.AddRange(EconomyDemands(snap, breakdown));
            demands.AddRange(DevelopmentDemands(snap, breakdown));
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

            int desiredConcurrency = ReconConcurrencyPolicy.DesiredTotal(snap, runnable);
            int remainingConcurrency = Mathf.Max(0, desiredConcurrency - activeReconExecutions);
            AiDebugLog.Write($"[AI][V2][Demand][Recon] concurrency {ReconConcurrencyPolicy.Explain(snap, runnable)} "
                + $"active={activeReconExecutions} remaining={remainingConcurrency}");
            if (remainingConcurrency == 0)
            {
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=DEFER reason=desired_concurrency_satisfied "
                    + $"active={activeReconExecutions} desired={desiredConcurrency} "
                    + $"hard={AiConfigV2.maxConcurrentReconExecutions} runnable={runnable.Count} blocked={blocked}");
                yield break;
            }

            int requiredNewExecutions = Mathf.Min(remainingConcurrency, runnable.Count);
            List<ReconObjective> topN = runnable.Take(requiredNewExecutions).ToList();
            int stealthNeeded = topN.Count(o => o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);
            int genericNeeded = requiredNewExecutions - stealthNeeded;
            var claimed = commitments?.ClaimedArmyIdSet;
            int stealthSupply = ScoutMoverSelector.Eligible(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.Required }, claimed).Count;
            int anySupply = ScoutMoverSelector.Eligible(snap,
                new ScoutMissionTarget { Stealth = StealthRequirement.None }, claimed).Count;
            int missStealth = Mathf.Max(0, stealthNeeded - stealthSupply);
            int stealthLeftover = Mathf.Max(0, stealthSupply - stealthNeeded);
            int genericSupply = Mathf.Max(0, anySupply - stealthSupply) + stealthLeftover;
            int missGeneric = Mathf.Max(0, genericNeeded - genericSupply);
            const float reconFixedOverheadAp = 0f;

            if (missStealth > 0)
            {
                ReconObjective best = topN.FirstOrDefault(o =>
                    o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f) ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=stealth desired={missStealth} reason=insufficient_free_stealth_scouts "
                    + $"jobs={stealthNeeded} free={stealthSupply} runnable={runnable.Count} blocked={blocked} "
                    + $"target=({best.FocusHex.Q},{best.FocusHex.R})");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Recon,
                    Capability = CapabilityKind.ScoutCapability,
                    DesiredAmount = missStealth,
                    RequiredTraits = TraitPreference.Stealth,
                    MinimumFollowupAp = reconFixedOverheadAp,
                    TargetHex = best.FocusHex,
                    Value = best.BaseValue,
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    Explain = $"{stealthNeeded} runnable stealth job(s), {stealthSupply} stealth scout(s) free, miss {missStealth}; blocked {blocked}",
                };
            }

            if (missGeneric > 0)
            {
                ReconObjective best = topN.FirstOrDefault(o =>
                    o.Stealth != StealthRequirement.Required && !(o.DetectionRisk > 0f)) ?? runnable[0];
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=CREATE capability=ScoutCapability "
                    + $"profile=generic desired={missGeneric} reason=insufficient_free_scouts jobs={genericNeeded} "
                    + $"free={genericSupply} anyFree={anySupply} stealthFree={stealthSupply} "
                    + $"runnable={runnable.Count} blocked={blocked} target=({best.FocusHex.Q},{best.FocusHex.R})");
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
                    ScoutContext = ScoutCapabilityContext.FromReconObjective(best, snap),
                    Explain = $"{genericNeeded} runnable generic job(s), {genericSupply} scout(s) free "
                        + $"(any {anySupply}, stealth {stealthSupply}), miss {missGeneric}; blocked {blocked}",
                };
            }

            if (missStealth == 0 && missGeneric == 0)
                AiDebugLog.Write($"[AI][V2][Demand][Recon] decision=SATISFIED reason=free_scout_supply_covers_open_slots "
                    + $"jobs={requiredNewExecutions} runnable={runnable.Count} blocked={blocked} "
                    + $"anyFree={anySupply} stealthFree={stealthSupply}");
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

        private static IEnumerable<AxisDemand> DefenceDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
        private static IEnumerable<AxisDemand> EconomyDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b) =>
            Enumerable.Empty<AxisDemand>();
    }
}
