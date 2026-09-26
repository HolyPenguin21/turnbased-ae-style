using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AGGRESSION DEMAND EVALUATOR
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
    //  Build is a deterministic primitive: no yield, no trace ids, no logging as a side effect.
    //  It only READS AiAllocatorStateRegistry for cooldowns. Every diagnostic line is returned in
    //  `Diagnostics` for the caller to replay verbatim.
    //
    //  Build has NO exception to "no mutation": it is a pure snapshot read even
    //  for the weakened-primary reinforcement case. The Assault -> Reinforcement phase transition
    //  belongs to MissionContinuityLayer.AdvanceRaidPhase (it already independently re-verifies the
    //  primary's state every reconciliation pass); the RaidIntent.ReinforcementRequestedTurn dedup
    //  stamp — the "exactly one support intent per weakened primary" invariant — is written only
    //  once a materialization for this exact ConsumerIntentKey is actually accepted/funded
    //  (CapabilityDeliveryEvaluator.TryHandoffRaidSupport). Build only READS that stamp to decide
    //  whether a demand it is about to (re-)propose was already requested this turn.
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
        // RaidOperationalReadiness is ReadyExecutable RIGHT NOW, with the ready GroundCombatAssemblyPlan
        // (its BaseArmyId is the canonical executable raid actor). The reaction direct-witness
        // probe reads this instead of a GatePassed filter + cheapest arbitrary pathable army.
        public IReadOnlyList<(AggressionObjective Objective, GroundCombatAssemblyPlan Plan)> ReadyExecutable =
            System.Array.Empty<(AggressionObjective, GroundCombatAssemblyPlan)>();
        // Fully-formatted "[AI][V2][Demand][Aggression] …" lines — the caller replays them through
        // AiDebugLog so Build itself performs no logging.
        public IReadOnlyList<string> Diagnostics = System.Array.Empty<string>();
    }

    public static partial class AggressionDemandEvaluator
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

            // An empty frozen objective list means only "no fresh target survived discovery this
            // pass". Durable Raid intents are independent continuity state and must still be
            // re-tested below: otherwise a weakened active Raid gets a reinforcement demand when
            // any unrelated fresh target exists, but loses the exact same demand when the unrelated
            // target disappears. That makes continuity depend on an unrelated map objective.
            objectives ??= System.Array.Empty<AggressionObjective>();

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
                    AxisDemand raidShortage = BoundPrimaryShortage(snap, inv, commitments, i,
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

            AggressionObjective chosen = null;
            RaidOperationalReadiness chosenReadiness = null;
            int blocked = 0;
            var readyList = new List<(AggressionObjective, GroundCombatAssemblyPlan)>();
            // Non-creating read — Build must not register a fresh allocator-state entry as a side
            // effect. null == no state yet == no cooldowns.
            AiAllocatorState cooldownState = AiAllocatorStateRegistry.Peek(player);

            foreach (AggressionObjective o in objectives.OrderByDescending(x => x.BaseValue).ThenBy(x => x.Target.DiagnosticLabel))
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

        // §41 — the ONE "does a bound ground-combat primary need a NEW support army" chain, shared
        // by Raid and Attack. It is NOT a shortage while the primary still clears its gate, a
        // support is already bound or was requested this turn, or an EXISTING free army could join
        // it (Missions/Provisioning bind that one through the batch solve). Power that exists but
        // cannot physically be delivered as a separate army is a bounded DEFER, never a phantom
        // claim. Otherwise: one IndependentFieldArmy demand sized to the deficit, carrying the
        // objective's canonical TaskScore (no synthetic lifecycle value). Returns null when no
        // demand is due; every decision is written to `diag`.
        // A demand is sized with the SAME hex defence its gate was evaluated with
        // (GroundCombatFeasibility.RequiredPower owns the formula): sized without it, it
        // under-asks exactly on fortified targets, where the estimator rejected the fight.
        private static float RequiredSitePower(IReadOnlyList<WorthIt.DefendingArmy> opposition,
            float hexBonus) =>
            GroundCombatFeasibility.RequiredPower(WorthIt.UnitsOf(opposition), hexBonus);

        private static AxisDemand BoundPrimaryShortage(WorldSnapshot snap, CapabilityInventory inv,
            ActorCommitments commitments, MissionIntent intent, MissionKind consumerKind,
            string targetLabel, int primaryId, int? supportArmyId, int reinforcementRequestedTurn,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, float gate,
            System.Func<TaskScore> objectiveScore, List<string> diag)
        {
            string at = $"intent={intent.IntentKey} target={targetLabel} primary={primaryId}";
            GroundCombatAssemblyPlan primaryPlan = GroundCombatAssemblyPlanner.PlanForArmyAt(
                snap, opposition, primaryId, gate, hexBonus);
            if (primaryPlan.Feasible)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + $"win={primaryPlan.ProjectedWinChance:0.00} reason=primary_clears_its_gate");
                return null;
            }
            if (supportArmyId.HasValue)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + $"support={supportArmyId.Value} reason=reinforcement_already_assigned_or_en_route");
                return null;
            }
            if (reinforcementRequestedTurn == snap.TurnNumber)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + "reason=reinforcement_already_requested_this_turn");
                return null;
            }
            List<int> existing = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, primaryId, opposition, commitments?.ClaimedArmyIdSet, hexBonus,
                allowCommandHandover: consumerKind == MissionKind.Attack);
            if (existing.Count > 0)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + $"candidates={existing.Count} reason=existing_free_army_available_as_support");
                return null;
            }

            ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(a => a != null && a.ArmyId == primaryId);
            float have = primary?.EffectiveArmyPower ?? 0f;
            float required = RequiredSitePower(opposition, hexBonus);
            // §11 — enough numeric power that still misses the estimator's gate is a composition
            // gap more power cannot close; it never becomes a phantom +1 FieldCombatPower.
            if (required - have <= AiConfigV2.allocatorSliceEpsilon)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER {at} "
                    + $"reason=primary_power_suffices_gate_missed required={required:0.#} have={have:0.#} "
                    + $"hexDef={hexBonus:0.#}");
                return null;
            }
            float deficit = required - have;
            if (!CanDeliverIndependentFieldArmy(snap, inv))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER {at} "
                    + $"reason=no_independent_field_army_deliverable deficit={deficit:0.#} "
                    + $"freePower={inv.RaidAvailableFieldPower:0.#} shells={inv.ReusableEmptyArmies.Count}");
                return null;
            }

            TaskScore score = objectiveScore();
            string lane = consumerKind.ToString().ToLowerInvariant();
            string rendezvous = $"({(primary?.Hex.Q ?? 0)},{(primary?.Hex.R ?? 0)})";
            diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE {at} capability=FieldCombatPower "
                + $"shape=IndependentFieldArmy desired={deficit:0.#} required={required:0.#} "
                + $"have={have:0.#} hexDef={hexBonus:0.#} task={score.Value:0.##} rendezvous={rendezvous} "
                + "reason=bound_primary_needs_separate_support_army");
            return new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                ConsumerIntentKey = intent.IntentKey,
                ConsumerMissionKind = consumerKind,
                DesiredAmount = deficit,
                RequiredCapabilityPower = deficit,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = primary?.Hex,
                WorldTaskScore = score,
                Value = score.Value,
                Explain = $"{lane} {targetLabel}: primary #{primaryId} no longer clears its gate "
                    + $"({have:0.#} of {required:0.#} needed, hex defence {hexBonus:0.#}); deliver "
                    + $"~{deficit:0.#} field power as a SEPARATE support army to {rendezvous}; "
                    + $"task={score.Value:0.##}",
            };
        }

        // ActiveDefence shares Aggression's one capability-demand owner. The mission happy path
        // remains in AggressionMissionPlanner; this method only translates a proven STRUCTURAL
        // response shortage into the same FieldCombatPower contract Phase A already materializes.
        // A force hidden merely by commitments, a spent/AP-exhausted actor, or enough physical
        // power split across incompatible same-hex packages is deliberately DEFER, not production.
        internal static IReadOnlyList<AxisDemand> BuildActiveDefenceDemands(WorldSnapshot snap,
            IReadOnlyList<ActiveDefenceObjective> objectives,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            PlayerSetupData player, out IReadOnlyList<string> diagnostics)
        {
            var demands = new List<AxisDemand>();
            var diag = new List<string>();
            diagnostics = diag;
            if (snap?.Self?.Armies == null)
                return demands;

            objectives ??= ActiveDefenceObjectiveEvaluator.Enumerate(snap);
            foreach (ActiveDefenceObjective objective in objectives
                .Where(o => o != null)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Target.EnemyArmyId))
            {
                EnemyContactSnapshot contact = snap.Threat?.Contacts?.FirstOrDefault(c =>
                    c?.Army != null && c.Army.ArmyId == objective.Target.EnemyArmyId
                    && c.Position.HasValue);
                if (contact?.Army == null)
                    continue;

                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.ActiveDefence
                    && i.ActiveDefence?.EnemyArmyId == objective.Target.EnemyArmyId);
                int? pinnedActor = incumbent?.ActiveDefence?.PrimaryArmyId;
                var excluded = commitments?.ClaimedArmyIdSet ?? new HashSet<int>();
                if (pinnedActor.HasValue)
                    excluded.Remove(pinnedActor.Value);

                var request = new GroundCombatAssemblyRequest
                {
                    Opposition = new[] { new WorthIt.DefendingArmy(contact.Army.Members, contact.Army.Commander) },
                    WinChanceGate = GroundCombatAdmissionPolicy.PinnedOrFreshGate(pinnedActor.HasValue),
                    PreferredPrimaryArmyId = pinnedActor,
                    PinToPreferred = pinnedActor.HasValue,
                    ExcludedArmyIds = excluded,
                };
                GroundCombatAssemblyPlan ready = GroundCombatAssemblyPlanner.Plan(snap, request);
                if (ready.Feasible)
                {
                    diag.Add($"[AI][V2][ActiveDefence][Demand] enemy={objective.Target.EnemyArmyId} "
                        + $"asset={objective.Target.ProtectedAssetKind}@({objective.Target.ProtectedAssetHex.Q},"
                        + $"{objective.Target.ProtectedAssetHex.R}) decision=SATISFIED reason=direct_response actor=#{ready.BaseArmyId} "
                        + $"win={ready.ProjectedWinChance:0.00}");
                    continue;
                }

                // Existing armies can be ASSEMBLED into the response: the same shared cross-hex
                // gather the Mission layer proposes (AggressionMissionPlanner
                // .TryAppendActiveDefenceReinforcement), with the same claims and gate. Assembly,
                // not production, closes this gap.
                GroundCombatGatherPlan assembly = GroundCombatAssemblyPlanner.PlanGather(snap,
                    request.Opposition, 0f, objective.Target.LastKnownHex, excluded,
                    request.WinChanceGate, pinnedActor);
                if (assembly.Feasible)
                {
                    diag.Add($"[AI][V2][ActiveDefence][Demand] enemy={objective.Target.EnemyArmyId} "
                        + $"decision=ASSEMBLY planned host=#{assembly.HostArmyId} "
                        + $"supports=[{string.Join(",", assembly.SupportArmyIds)}] "
                        + $"win={assembly.ProjectedWinChance:0.00}");
                    continue;
                }

                // If the same physical force can respond or be assembled when contention is
                // ignored — every claim released and spent MP restored (it moves next turn) — the
                // gap is temporary. Buying another army would be phantom.
                float freshGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate;
                GroundCombatAssemblyPlan physical = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Opposition = request.Opposition,
                        WinChanceGate = freshGate,
                        ExcludedArmyIds = new HashSet<int>(),
                    });
                if (!physical.Feasible)
                    physical = snap.Self.Armies
                        .Where(a => a != null && a.IsStructuralRaidActor)
                        .Select(a => GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(snap,
                            request.Opposition, a.ArmyId, freshGate))
                        .FirstOrDefault(p => p.Feasible) ?? physical;
                GroundCombatGatherPlan physicalAssembly = physical.Feasible ? null
                    : GroundCombatAssemblyPlanner.PlanGather(snap, request.Opposition, 0f,
                        objective.Target.LastKnownHex, new HashSet<int>(), freshGate,
                        requireMovementNow: false);
                if (physical.Feasible || physicalAssembly.Feasible)
                {
                    diag.Add($"[AI][V2][ActiveDefence][Demand] enemy={objective.Target.EnemyArmyId} "
                        + "decision=DEFER reason=mover_contended "
                        + (physical.Feasible ? $"physicalActor=#{physical.BaseArmyId}"
                            : $"physicalHost=#{physicalAssembly.HostArmyId} "
                                + $"supports=[{string.Join(",", physicalAssembly.SupportArmyIds)}]"));
                    continue;
                }

                // A real capability shortage: no legal response and no assembly path, even with
                // every claim released. Sized by the one ground-combat requirement owner.
                float required = GroundCombatFeasibility.RequiredPower(
                    WorthIt.UnitsOf(request.Opposition), 0f);
                List<ArmySnapshot> structural = snap.Self.Armies
                    .Where(a => a != null && a.IsStructuralRaidActor).ToList();
                float physicalPower = structural.Sum(a => Mathf.Max(0f, a.EffectiveArmyPower));
                // Aggregate power that cannot be assembled (capacity, no sparable bodies, no path)
                // is not capability: once the total already reaches `required`, the new army is
                // sized against the strongest single force it would stand beside instead.
                float strongest = structural.Count == 0 ? 0f
                    : structural.Max(a => Mathf.Max(0f, a.EffectiveArmyPower));
                float deficit = Mathf.Max(1f, physicalPower + AiConfigV2.allocatorSliceEpsilon < required
                    ? required - physicalPower : required - strongest);
                MissionIntentKey consumer = MissionIntentKey.ForActiveDefence(
                    objective.Target.EnemyArmyId);
                diag.Add($"[AI][V2][ActiveDefence][Demand] enemy={objective.Target.EnemyArmyId} "
                    + $"asset={objective.Target.ProtectedAssetKind}@({objective.Target.ProtectedAssetHex.Q},"
                    + $"{objective.Target.ProtectedAssetHex.R}) decision=CREATE "
                    + $"capability=FieldCombatPower required={required:0.#} physical={physicalPower:0.#} "
                    + $"deficit={deficit:0.#} reason=no_feasible_response_force detail=\"{assembly.Reason}\"");
                demands.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                    ConsumerIntentKey = consumer,
                    ConsumerMissionKind = MissionKind.ActiveDefence,
                    DesiredAmount = deficit,
                    RequiredCapabilityPower = deficit,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = objective.Target.ProtectedAssetHex,
                    WorldTaskScore = objective.TaskScore,
                    Value = objective.TaskScore.Value,
                    Explain = $"ActiveDefence enemy #{objective.Target.EnemyArmyId} threatening "
                        + $"{objective.Target.ProtectedAssetKind}@({objective.Target.ProtectedAssetHex.Q},"
                        + $"{objective.Target.ProtectedAssetHex.R}) needs ~{deficit:0.#} field power "
                        + $"({physicalPower:0.#}/{required:0.#}); consumer={consumer}",
                });
            }
            return demands;
        }

        // §6 — "is there, in principle, a way to physically field a SEPARATE support army": a free
        // ready field army, a reusable empty shell, or a deployable unit/hero card in hand. Pure
        // read; it never claims anything. When this is false the caller DEFERS (bounded) instead of
        // inventing a phantom power request nothing could ever satisfy.
        internal static bool CanDeliverIndependentFieldArmy(WorldSnapshot snap, CapabilityInventory inv)
        {
            if (inv != null && inv.RaidAvailableFieldPower > AiConfigV2.allocatorSliceEpsilon)
                return true;
            if (inv != null && inv.ReusableEmptyArmies != null && inv.ReusableEmptyArmies.Count > 0)
                return true;
            foreach (Game.Cards.CardData c in snap?.Self?.Hand
                ?? (IReadOnlyList<Game.Cards.CardData>)System.Array.Empty<Game.Cards.CardData>())
            {
                Game.Cards.CardType? t = c?.Definition?.cardType;
                if (t == Game.Cards.CardType.Unit || t == Game.Cards.CardType.Hero)
                    return true;
            }
            return false;
        }

        // Gates cooldowns for a fresh/incumbent ASSAULT attempt on this target. Delegates to
        // StableMissionKey.ForRaidAssault, the single owner of this encoding — never hand-built here.
        internal static StableMissionKey RaidKey(AggressionObjective o) =>
            StableMissionKey.ForRaidAssault(o.Target);

    }
}
