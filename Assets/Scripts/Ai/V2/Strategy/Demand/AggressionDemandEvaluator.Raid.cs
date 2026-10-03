using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class AggressionDemandEvaluator
    {
        private static AggressionDemandEvaluation BuildRaid(WorldSnapshot snap,
            IReadOnlyList<RaidObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
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

            // An empty frozen objective list means only "no fresh target survived discovery this
            // pass". Durable Raid intents are independent continuity state and must still be
            // re-tested below: otherwise a weakened active Raid gets a reinforcement demand when
            // any unrelated fresh target exists, but loses the exact same demand when the unrelated
            // target disappears. That makes continuity depend on an unrelated map objective.
            objectives ??= System.Array.Empty<RaidObjective>();

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);

            // ===================================================================================
            //  An active Raid intent is NOT automatically "covered". A claimed actor only proves
            //  an army is bound to the operation, not that it can still WIN the next fight; a
            //  primary weakened by the previous battle must produce a demand instead of being
            //  re-proposed and rejected by Provisioning as AssemblyInfeasible. The primary is
            //  re-tested against the CURRENT (already re-oriented) target through the SAME gate
            //  Provisioning will use.
            // ===================================================================================
            var coveredTargets = new HashSet<RaidTargetRef>();
            var reinforcementDemands = new List<AxisDemand>();
            if (activeIntents != null)
                foreach (MissionIntent i in activeIntents)
                {
                    RaidIntent ri = i?.Kind == MissionKind.Raid ? i.Raid : null;
                    if (ri == null)
                        continue;

                    // A Return/SupportReturn leg consumes no target and needs no combat capability
                    // at all — the target (if any) is already handled or irrelevant to this leg.
                    if (ri.Phase == RaidMissionPhase.Return || ri.Phase == RaidMissionPhase.SupportReturn
                        || ri.Phase == RaidMissionPhase.RecoveryReturn)
                    {
                        if (ri.Target.HasValue) coveredTargets.Add(ri.Target);
                        diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                            + $"reason=raid_in_{ri.Phase.ToString().ToLowerInvariant()}_phase");
                        continue;
                    }

                    if (!ri.PrimaryArmyId.HasValue || commitments == null || !commitments.IsArmyClaimed(ri.PrimaryArmyId.Value))
                        continue;   // no bound primary yet -> ordinary fresh-objective handling below
                    int primaryId = ri.PrimaryArmyId.Value;

                    if (ri.Target.HasValue) coveredTargets.Add(ri.Target);

                    // Build is a pure snapshot read; it never mutates the real RaidIntent. The
                    // Assault -> Reinforcement transition is MissionContinuityLayer.AdvanceRaidPhase's
                    // job; the ReinforcementRequestedTurn dedup stamp is written only once a
                    // materialization for this exact ConsumerIntentKey is accepted/funded
                    // (CapabilityDeliveryEvaluator.TryHandoffRaidSupport). Build also serves the
                    // bounded reaction probe, which must never commit the real mission to anything.
                    AxisDemand raidShortage = GroundCombatDemandPolicy.BoundPrimaryShortage(snap, inv, commitments, i,
                        MissionKind.Raid, ri.Target.DiagnosticLabel, primaryId, ri.SupportArmyId,
                        ri.ReinforcementRequestedTurn, AiV2Util.KnownOpposition(snap, ri.Target),
                        AiV2Util.KnownRaidDefenceBonus(snap, ri.Target),
                        GroundCombatAdmissionPolicy.RaidPrimaryGate(ri),
                        // The same world objective, so its canonical intrinsic TaskScore.
                        () => objectives
                            .Where(o => o != null && o.Target.Equals(ri.Target))
                            .OrderByDescending(o => o.BaseValue)
                            .FirstOrDefault()?.TaskScore ?? default,
                        diag);
                    if (raidShortage != null)
                        reinforcementDemands.Add(raidShortage);
                }
            // ATK §41 — the Attack lane's proven shortages join the SAME demand list, through the
            // same rules, in AggressionDemandEvaluator.Attack.cs.
            AppendAttackDemands(snap, activeIntents, commitments, inv, diag, reinforcementDemands);
            AppendHeldBaseGarrisonDemands(snap, diag, reinforcementDemands);

            RaidObjective chosen = null;
            RaidOperationalReadiness chosenReadiness = null;
            int blocked = 0;
            var readyList = new List<(RaidObjective, GroundCombatAssemblyPlan)>();
            // Non-creating read — Build must not register a fresh allocator-state entry as a side
            // effect. null == no state yet == no cooldowns.
            AiAllocatorState cooldownState = AiAllocatorStateRegistry.Peek(player);

            foreach (RaidObjective o in objectives.OrderByDescending(x => x.BaseValue).ThenBy(x => x.Target.DiagnosticLabel))
            {
                if (coveredTargets.Contains(o.Target))
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
                    snap, o, AiV2Util.KnownOpposition(snap, o.Target), commitments, inv);
                if (readiness.ReadyExecutable)
                {
                    readyList.Add((o, readiness.ReadyPlan));
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED target={o.Target.DiagnosticLabel} "
                        + $"reason=ready_free_army_clears_shared_readiness actor={readiness.ReadyPlan.BaseArmyId} "
                        + $"win={readiness.ReadyPlan.ProjectedWinChance:0.00} "
                        + $"cover={(readiness.ReadyPlan.CoversAllDefenders ? 1 : 0)} "
                        + $"freePower={inv.RaidAvailableFieldPower:0.#} requiredPower={readiness.RequiredPower:0.#} "
                        + $"frozenAsmWin={o.AssemblableWinChance:0.00}");
                    continue;
                }

                // T06 — a proven-unreachable target asks for nothing and does not shadow a
                // lower-value target Phase A can actually serve. It stays in honest knowledge and is
                // re-read from the next snapshot (new card/output/equipment, changed defence).
                if (readiness.ProvenUnreachableWithinKnownPool)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP target={o.Target.DiagnosticLabel} "
                        + $"reason=proven_unreachable_within_known_pool detail=\"{readiness.UnreachableReason}\"");
                    continue;
                }

                // Preserve the first (highest-value) assembly gap only as a fallback. An assembly
                // gap cannot be fulfilled by Phase A; it must not hide a lower-value objective
                // whose missing Hero/FieldCombatPower Phase A can actually deliver this pass.
                // Among actionable shortages, the original value ordering is unchanged.
                if (chosen == null || (chosenReadiness.NeedsAssembly
                    && (readiness.NeedsHero || readiness.NeedsPower)))
                {
                    chosen = o;
                    chosenReadiness = readiness;
                }
            }

            eval.ReadyExecutable = readyList;
            eval.BlockedByCooldown = blocked;

            if (chosen == null || chosenReadiness == null)
            {
                if (reinforcementDemands.Count > 0)
                {
                    eval.Demands = reinforcementDemands;
                    eval.Outcome = AggressionDemandOutcome.Demand;
                    eval.Reason = "raid_reinforcement";
                    return eval;
                }
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED reason=no_runnable_capability_shortage "
                    + $"objectives={objectives.Count} blocked={blocked} freePower={inv.RaidAvailableFieldPower:0.#} "
                    + $"committedPower={inv.CommittedFieldCombatPower:0.#} freeHeroes={inv.AvailableHeroes} "
                    + $"committedHeroes={inv.CommittedHeroes}");
                eval.Reason = "no_runnable_capability_shortage";
                return eval;
            }

            eval.ChosenObjective = chosen;
            eval.Readiness = chosenReadiness;

            if (chosenReadiness.NeedsAssembly && reinforcementDemands.Count == 0)
            {
                // §11 — enough numeric power and a raid-eligible hero exist; the target is not
                // executable only because no legal same-hex formation clears the estimator. That
                // is an organization gap owned by RaidAssembly / Housekeeping / the bounded
                // re-admission — buying more FieldCombatPower would not help.
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER target={chosen.Target.DiagnosticLabel} "
                    + $"reason=assembly_gap detail=\"{chosenReadiness.AssemblyReason}\" "
                    + $"freePower={inv.RaidAvailableFieldPower:0.#} requiredPower={chosenReadiness.RequiredPower:0.#} "
                    + $"freeHeroes={inv.AvailableHeroes} committedHeroes={inv.CommittedHeroes} blocked={blocked} "
                    + $"readyDetail=\"{chosenReadiness.ReadyReason}\"");
                eval.Outcome = AggressionDemandOutcome.AssemblyDeferred;
                eval.Reason = "assembly_gap";
                return eval;
            }

            // Reinforcement demands are real, target-specific and always carried through: they are
            // never replaced by the fresh-objective shortage below.
            var demands = new List<AxisDemand>(reinforcementDemands);

            if (chosenReadiness.NeedsHero)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE target={chosen.Target.DiagnosticLabel} "
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
                    WorldTaskScore = chosen.TaskScore,
                    Value = chosen.TaskScore.Value,
                    Explain = $"raid {chosen.Target.DiagnosticLabel} needs a free deployed hero; free {inv.AvailableHeroes}, "
                        + $"committed {inv.CommittedHeroes}; blocked targets {blocked}; {chosenReadiness.ReadyReason}",
                });
            }

            if (chosenReadiness.NeedsPower)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE target={chosen.Target.DiagnosticLabel} "
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
                    WorldTaskScore = chosen.TaskScore,
                    Value = chosen.TaskScore.Value,
                    Explain = $"raid {chosen.Target.DiagnosticLabel} needs ~{chosenReadiness.RequestedPower:0.#} more free field capability "
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
        // Gates cooldowns for a fresh/incumbent ASSAULT attempt on this target. Delegates to
        // StableMissionKey.ForRaidAssault, the single owner of this encoding — never hand-built here.
        internal static StableMissionKey RaidKey(RaidObjective o) =>
            StableMissionKey.ForRaidAssault(o.Target);

    }
}
