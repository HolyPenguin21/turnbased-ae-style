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
    //  Build is a deterministic primitive: no yield, no trace ids, no logging as a side effect.
    //  It only READS AiAllocatorStateRegistry for cooldowns. Every diagnostic line the pipeline
    //  used to write inline is returned in `Diagnostics` for the caller to replay verbatim.
    //
    //  AGG-RAID P1#1 — Build has NO exception to "no mutation": it is a pure snapshot read even
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

            // An empty frozen objective list means only "no fresh target survived discovery this
            // pass". Durable Raid intents are independent continuity state and must still be
            // re-tested below: otherwise a weakened active Raid gets a reinforcement demand when
            // any unrelated fresh target exists, but loses the exact same demand when the unrelated
            // target disappears. That makes continuity depend on an unrelated map objective.
            objectives ??= System.Array.Empty<AggressionObjective>();

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);

            // ===================================================================================
            //  AGG-RAID §6 — THE MAIN FIX. An active Raid intent is NOT automatically "covered".
            //  A claimed actor only proves an army is bound to the operation, not that it can
            //  still WIN the next fight. The old code marked the target covered on that claim
            //  alone, so a primary weakened by the previous battle produced no demand, was
            //  re-proposed as incumbent anyway, was rejected by Provisioning as AssemblyInfeasible,
            //  and the intent hung until stall/reap. Here the primary is re-tested against the
            //  CURRENT (already re-oriented) target through the SAME gate Provisioning will use.
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
                    if (ri.Phase == RaidMissionPhase.Return || ri.Phase == RaidMissionPhase.SupportReturn)
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

                    IReadOnlyList<WorthIt.DefenderProfile> defenders = RaidDefenders(snap, ri.Target);
                    GroundCombatAssemblyPlan primaryPlan = GroundCombatAssemblyPlanner.PlanForArmyAt(
                        snap, defenders, primaryId, AiConfigV2.raidMinViableWinChance);
                    if (primaryPlan.Feasible)
                    {
                        diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                            + $"target={ri.Target.DiagnosticLabel} primary={primaryId} "
                            + $"win={primaryPlan.ProjectedWinChance:0.00} "
                            + "reason=primary_clears_worthit_against_current_target");
                        continue;
                    }

                    if (ri.SupportArmyId.HasValue)
                    {
                        diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                            + $"target={ri.Target.DiagnosticLabel} primary={primaryId} support={ri.SupportArmyId.Value} "
                            + "reason=reinforcement_already_assigned_or_en_route");
                        continue;
                    }

                    if (ri.ReinforcementRequestedTurn == snap.TurnNumber)
                    {
                        diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                            + "reason=reinforcement_already_requested_this_turn");
                        continue;
                    }

                    // AGG-RAID P0#1 — an EXISTING free army may already be able to serve as
                    // reinforcement (no materialization needed at all). Only request a NEW
                    // IndependentFieldArmy when no such existing candidate is available; Missions /
                    // Provisioning pick the concrete actor through the normal ground-combat batch
                    // solve once this evaluation reports the target still uncovered.
                    List<int> existingSupportCandidates = GroundCombatAssemblyPlanner
                        .ReinforcementSupportCandidates(snap, primaryId, defenders, commitments?.ClaimedArmyIdSet);
                    if (existingSupportCandidates.Count > 0)
                    {
                        diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                            + $"target={ri.Target.DiagnosticLabel} primary={primaryId} "
                            + $"candidates={existingSupportCandidates.Count} "
                            + "reason=existing_free_army_available_as_support");
                        continue;
                    }

                    ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(a => a != null && a.ArmyId == primaryId);
                    float targetPower = AiPower.EffectiveArmyPowerFromProfiles(defenders);
                    float required = UnityEngine.Mathf.Max(1f, targetPower * AiConfigV2.raidCombatPowerMargin);
                    float deficit = UnityEngine.Mathf.Max(1f, required - (primary?.EffectiveArmyPower ?? 0f));

                    // §6 — if the power exists numerically but cannot PHYSICALLY be delivered as a
                    // separate army (no free field army, no reusable shell, no deployable unit
                    // card), emit a bounded DEFER instead of a phantom +1 power claim.
                    if (!CanDeliverIndependentFieldArmy(snap, inv))
                    {
                        diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER intent={i.IntentKey} "
                            + $"target={ri.Target.DiagnosticLabel} primary={primaryId} "
                            + $"reason=no_independent_field_army_deliverable deficit={deficit:0.#} "
                            + $"freePower={inv.RaidAvailableFieldPower:0.#} shells={inv.ReusableEmptyArmies.Count}");
                        continue;
                    }

                    // AGG-RAID P1#1 — Build is a pure snapshot read; it never mutates the real
                    // RaidIntent. The Assault -> Reinforcement phase transition is
                    // MissionContinuityLayer.AdvanceRaidPhase's job (it independently re-verifies
                    // the primary's state every reconciliation pass); the ReinforcementRequestedTurn
                    // dedup stamp is written only once a materialization for this exact
                    // ConsumerIntentKey is actually accepted/funded
                    // (CapabilityDeliveryEvaluator.TryHandoffRaidSupport). Build is called from both
                    // the main Phase-A pass and the bounded reaction probe — a diagnostic evaluation
                    // must never be able to commit the real mission to state it may never fund.
                    float reinforcementValue = objectives
                        .Where(o => o != null && o.Target.Equals(ri.Target))
                        .Select(o => o.BaseValue)
                        .DefaultIfEmpty(0f)
                        .Max();
                    diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE intent={i.IntentKey} "
                        + $"target={ri.Target.DiagnosticLabel} capability=FieldCombatPower "
                        + $"shape=IndependentFieldArmy desired={deficit:0.#} primary={primaryId} "
                        + $"required={required:0.#} have={(primary?.EffectiveArmyPower ?? 0f):0.#} "
                        + $"task={reinforcementValue:0.##} "
                        + $"rendezvous=({(primary?.Hex.Q ?? 0)},{(primary?.Hex.R ?? 0)}) "
                        + "reason=weakened_primary_needs_separate_support_army");
                    reinforcementDemands.Add(new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Aggression,
                        Capability = CapabilityKind.FieldCombatPower,
                        DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                        ConsumerIntentKey = i.IntentKey,
                        DesiredAmount = deficit,
                        RequiredCapabilityPower = deficit,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = primary?.Hex,
                        // This is still the same world objective, so carry its canonical intrinsic
                        // value. The active Raid's Hard commitment owns continuity separately; a
                        // synthetic legacy 90 here would max Phase-A urgency and reintroduce the
                        // retired Raid-local score scale into cross-demand arbitration.
                        Value = reinforcementValue,
                        Explain = $"raid {ri.Target.DiagnosticLabel}: primary #{primaryId} no longer clears WorthIt "
                            + $"({(primary?.EffectiveArmyPower ?? 0f):0.#} of {required:0.#} needed); "
                            + $"deliver ~{deficit:0.#} field power as a SEPARATE support army to "
                            + $"({(primary?.Hex.Q ?? 0)},{(primary?.Hex.R ?? 0)}); task={reinforcementValue:0.##}",
                    });
                }
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
                    snap, o, RaidDefenders(snap, o.Target), commitments, inv);
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
                    Value = chosen.BaseValue,
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
                    Value = chosen.BaseValue,
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

        internal static IReadOnlyList<WorthIt.DefenderProfile> RaidDefenders(WorldSnapshot snap, RaidTargetRef target) =>
            AiV2Util.KnownDefenders(snap, target);
    }
}