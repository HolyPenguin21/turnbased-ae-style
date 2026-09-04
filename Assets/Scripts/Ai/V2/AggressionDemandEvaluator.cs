using System.Collections.Generic;
using System.Linq;
using Game.Players;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AGGRESSION DEMAND EVALUATOR  (AI-MGR-02 round 8 — P1)
    // ===========================================================================================
    //  ONE canonical decision for "does the Aggression axis have a runnable capability shortage
    //  this pass, and if so which objective / which demand(s)". The whole admission contract lives
    //  here — covered-by-committed-raid, allocator cooldown, top-value objective selection,
    //  RaidOperationalReadiness, the ReadyExecutable / NeedsAssembly-DEFER / NeedsHero / NeedsPower
    //  branch, and the exact AxisDemand shapes.
    //
    //  Consumed by BOTH:
    //    · DemandLayer.AggressionDemands  — the real Phase-A pipeline (yields Demands, replays
    //      Diagnostics, attaches trace ids downstream),
    //    · StrategicReactionPass          — the reaction feasibility probe (reads ChosenObjective /
    //      Readiness / Outcome; never mirrors the rules).
    //
    //  Build is a PURE deterministic primitive: no yield, no trace ids, no logging as a side
    //  effect, no registry mutation. It only READS AiAllocatorStateRegistry for cooldowns. Every
    //  diagnostic line the pipeline used to write inline is returned in `Diagnostics` for the
    //  caller to replay verbatim.
    // ===========================================================================================

    public enum AggressionDemandOutcome
    {
        None,             // no self snapshot / no objectives / no runnable capability shortage
        Ready,            // the selected objective is already ReadyExecutable — no demand (unreached here; folded into None)
        AssemblyDeferred, // real shortage is STRUCTURAL — the pipeline DEFERs, buying power would not help
        Demand,           // Demands is non-empty (Hero and/or FieldCombatPower)
    }

    public sealed class AggressionDemandEvaluation
    {
        public AggressionObjective ChosenObjective;
        public RaidOperationalReadiness Readiness;
        public IReadOnlyList<AxisDemand> Demands = System.Array.Empty<AxisDemand>();
        public AggressionDemandOutcome Outcome = AggressionDemandOutcome.None;
        public string Reason = "";
        public int BlockedByCooldown;
        // Every non-covered / non-cooldown discovered-or-not objective whose canonical
        // RaidOperationalReadiness is ReadyExecutable RIGHT NOW, with the ready RaidAssemblyPlan
        // (its BaseArmyId is the canonical executable raid actor). The reaction direct-witness
        // probe reads this instead of a GatePassed filter + cheapest arbitrary pathable army.
        public IReadOnlyList<(AggressionObjective Objective, RaidAssemblyPlan Plan)> ReadyExecutable =
            System.Array.Empty<(AggressionObjective, RaidAssemblyPlan)>();
        // Fully-formatted "[AI][V2][Demand][Aggression] …" lines — the caller replays them through
        // AiDebugLog so Build itself performs no logging.
        public IReadOnlyList<string> Diagnostics = System.Array.Empty<string>();
    }

    public static class AggressionDemandEvaluator
    {
        public static AggressionDemandEvaluation Build(WorldSnapshot snap,
            IReadOnlyList<AggressionObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player)
        {
            var diag = new List<string>();
            var eval = new AggressionDemandEvaluation { Diagnostics = diag };

            if (snap?.Self == null)
            {
                diag.Add("[AI][V2][Demand][Aggression] decision=NONE reason=no_self_snapshot");
                eval.Reason = "no_self_snapshot";
                return eval;
            }
            if (objectives == null || objectives.Count == 0)
            {
                diag.Add("[AI][V2][Demand][Aggression] decision=NONE reason=no_frozen_aggression_objectives");
                eval.Reason = "no_frozen_aggression_objectives";
                return eval;
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
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED targetArmy={i.Raid.TargetArmyId} "
                        + $"reason=covered_by_active_raid actor={i.PreferredMoverArmyId.Value}");
                }

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            AggressionObjective chosen = null;
            RaidOperationalReadiness chosenReadiness = null;
            int blocked = 0;
            var readyList = new List<(AggressionObjective, RaidAssemblyPlan)>();
            // Non-creating read — Build must not register a fresh allocator-state entry as a side
            // effect. null == no state yet == no cooldowns.
            AiAllocatorState cooldownState = AiAllocatorStateRegistry.Peek(player);

            foreach (AggressionObjective o in objectives.OrderByDescending(x => x.BaseValue).ThenBy(x => x.TargetArmyId))
            {
                if (coveredTargets.Contains(o.TargetArmyId))
                    continue;
                StableMissionKey key = RaidKey(o);
                if (cooldownState != null
                    && cooldownState.TryGetCooldown(key, snap.TurnNumber, out MissionCooldownInfo cd))
                {
                    blocked++;
                    diag.Add($"[AI][V2][Demand][Aggression] blocked {key} reason={cd.Reason} "
                        + $"start=t{cd.StartedTurn} until=t{cd.UntilTurn} remaining={cd.RemainingAt(snap.TurnNumber)}");
                    continue;
                }

                RaidOperationalReadiness readiness = RaidOperationalReadiness.Evaluate(
                    snap, o, RaidDefenders(snap, o.TargetArmyId), commitments, inv);
                if (readiness.ReadyExecutable)
                {
                    readyList.Add((o, readiness.ReadyPlan));
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED targetArmy={o.TargetArmyId} "
                        + $"reason=ready_free_army_clears_shared_readiness actor={readiness.ReadyPlan.BaseArmyId} "
                        + $"win={readiness.ReadyPlan.ProjectedWinChance:0.00} "
                        + $"cover={(readiness.ReadyPlan.CoversAllDefenders ? 1 : 0)} "
                        + $"freePower={inv.RaidAvailableFieldPower:0.#} requiredPower={readiness.RequiredPower:0.#} "
                        + $"frozenAsmWin={o.AssemblableWinChance:0.00}");
                    continue;
                }

                // First runnable shortage wins the demand; keep scanning so EVERY ready-executable
                // discovered target is still surfaced for the direct-witness probe.
                if (chosen == null)
                {
                    chosen = o;
                    chosenReadiness = readiness;
                }
            }

            eval.ReadyExecutable = readyList;
            eval.BlockedByCooldown = blocked;

            if (chosen == null || chosenReadiness == null)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED reason=no_runnable_capability_shortage "
                    + $"objectives={objectives.Count} blocked={blocked} freePower={inv.RaidAvailableFieldPower:0.#} "
                    + $"committedPower={inv.CommittedFieldCombatPower:0.#} freeHeroes={inv.AvailableHeroes} "
                    + $"committedHeroes={inv.CommittedHeroes}");
                eval.Reason = "no_runnable_capability_shortage";
                return eval;
            }

            eval.ChosenObjective = chosen;
            eval.Readiness = chosenReadiness;

            if (chosenReadiness.NeedsAssembly)
            {
                // §11 — enough numeric power and a raid-eligible hero exist; the target is not
                // executable only because no legal same-hex formation clears the estimator. That
                // is an organization gap owned by RaidAssembly / Housekeeping / the bounded
                // re-admission — buying more FieldCombatPower would not help.
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER targetArmy={chosen.TargetArmyId} "
                    + $"reason=assembly_gap detail=\"{chosenReadiness.AssemblyReason}\" "
                    + $"freePower={inv.RaidAvailableFieldPower:0.#} requiredPower={chosenReadiness.RequiredPower:0.#} "
                    + $"freeHeroes={inv.AvailableHeroes} committedHeroes={inv.CommittedHeroes} blocked={blocked} "
                    + $"readyDetail=\"{chosenReadiness.ReadyReason}\"");
                eval.Outcome = AggressionDemandOutcome.AssemblyDeferred;
                eval.Reason = "assembly_gap";
                return eval;
            }

            var demands = new List<AxisDemand>();

            if (chosenReadiness.NeedsHero)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE targetArmy={chosen.TargetArmyId} "
                    + $"capability=Hero desired=1 reason=no_free_deployed_hero freeHeroes={inv.AvailableHeroes} "
                    + $"committedHeroes={inv.CommittedHeroes} blocked={blocked} readiness=REJECT "
                    + $"detail=\"{chosenReadiness.ReadyReason}\"");
                demands.Add(new AxisDemand
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
                });
            }

            if (chosenReadiness.NeedsPower)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE targetArmy={chosen.TargetArmyId} "
                    + $"capability=FieldCombatPower desired={chosenReadiness.RequestedPower:0.#} "
                    + $"reason={chosenReadiness.PowerReason} freePower={inv.RaidAvailableFieldPower:0.#} "
                    + $"committedPower={inv.CommittedFieldCombatPower:0.#} requiredPower={chosenReadiness.RequiredPower:0.#} "
                    + $"blocked={blocked} readiness=REJECT detail=\"{chosenReadiness.ReadyReason}\" "
                    + $"frozenReadyWin={chosen.ReadyWinChance:0.00} frozenAsmWin={chosen.AssemblableWinChance:0.00} "
                    + $"frozenCover={(chosen.CanCoverAllDefenders ? 1 : 0)}");
                demands.Add(new AxisDemand
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
                });
            }

            eval.Demands = demands;
            eval.Outcome = demands.Count > 0 ? AggressionDemandOutcome.Demand : AggressionDemandOutcome.None;
            eval.Reason = demands.Count > 0 ? "shortage" : "no_demand_emitted";
            return eval;
        }

        internal static StableMissionKey RaidKey(AggressionObjective o) =>
            new StableMissionKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, o.TargetArmyId, 0, 0);

        internal static IReadOnlyList<WorthIt.DefenderProfile> RaidDefenders(WorldSnapshot snap, int targetArmyId)
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
    }
}
