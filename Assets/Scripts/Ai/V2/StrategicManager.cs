using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public sealed class StrategicPhaseResult
    {
        public bool StateChanged;
        public int CardsPlayed;
        public int CardsDrawn;
        public readonly Dictionary<DesireAxis, float> ApDebited = new Dictionary<DesireAxis, float>();
        public MaterializationReservation Reservation;

        public void AddDebit(DesireAxis a, float ap)
        {
            ApDebited.TryGetValue(a, out float cur);
            ApDebited[a] = cur + ap;
        }
    }

    // The single owner of V2 strategic Unit/Hero/Recce materialization. Phase A closes explicit
    // capability demands against the shared axis ledger; Phase B spends only genuinely remaining
    // capacity. Physical resources are read from the real V2 PlayerRoot pool, never protected by
    // speculative fixed H/E/M/T floors.
    public static class StrategicManager
    {
        private sealed class DemandState
        {
            public AxisDemand Demand;
            public float Remaining;
            public int Ordinal;
            public bool Blocked;
        }

        private readonly struct PhaseACandidate
        {
            public readonly DemandState State;
            public readonly MaterializationPlan Plan;
            public readonly float FollowupAp;

            public PhaseACandidate(DemandState state, MaterializationPlan plan, float followupAp)
            {
                State = state;
                Plan = plan;
                FollowupAp = followupAp;
            }
        }

        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments)
        {
            // This method is the first V2 mutating boundary. Capture before checking whether there
            // are any demands so even a pure-surplus / housekeeping-only turn gets an exact start
            // state for the final AP/H/E/M/T delta line.
            if (player != null && root != null && ctx != null)
                TurnResourceTelemetry.CaptureStart(player, root, ctx.TurnNumber);

            var result = new StrategicPhaseResult { Reservation = new MaterializationReservation() };
            if (demands == null || demands.Count == 0 || player == null || root == null || hand == null || ledger == null)
                return result;

            AiDebugLog.Write($"[AI][V2]   strat.A — {player.Nickname} hand {AiCardLog.Hand(hand)}");

            var states = demands
                .Select((d, i) => new DemandState
                {
                    Demand = d,
                    Remaining = d != null ? Mathf.Max(0f, d.DesiredAmount) : 0f,
                    Ordinal = i,
                })
                .Where(s => s.Demand != null && s.Remaining > 0f)
                .ToList();
            if (states.Count == 0)
                return result;

            int chainAttempts = 0;
            while (chainAttempts < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
            {
                List<DemandState> active = states.Where(s => !s.Blocked && s.Remaining > 0f).ToList();
                if (active.Count == 0)
                    break;

                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                var feasible = new List<PhaseACandidate>();

                foreach (DemandState state in active)
                {
                    AxisDemand demand = state.Demand;
                    float reserved = ledger.ReservedFollowup(demand.RequestingAxis);
                    // Hero opportunity is real only when another still-active strategic shortage
                    // in THIS Phase-A portfolio actually needs a Hero. "No deployed free hero" by
                    // itself is not a reason to preserve a Hero scout on turn one.
                    bool competingHeroDemand = demand.Capability == CapabilityKind.ScoutCapability
                        && active.Any(other => !ReferenceEquals(other, state)
                            && other.Remaining > AiConfigV2.allocatorSliceEpsilon
                            && other.Demand.Capability == CapabilityKind.Hero);
                    (MaterializationPlan plan, float followupAp)? pick = MaterializationCandidateBuilder.BestForDemand(
                        snap, player, root, hand, ctx, demand, ledger, commitments, reserved,
                        result.Reservation, inv, competingHeroDemand);
                    if (pick != null)
                        feasible.Add(new PhaseACandidate(state, pick.Value.plan, pick.Value.followupAp));
                }

                if (feasible.Count == 0)
                {
                    foreach (DemandState state in active)
                    {
                        AxisDemand d = state.Demand;
                        float reserved = ledger.ReservedFollowup(d.RequestingAxis);
                        string diag = MaterializationDiagnostics.ExplainNoChain(
                            snap, player, root, hand, ctx, d, ledger, commitments, reserved);
                        AiDebugLog.Write($"[AI][V2]   strat.A — {d}: no feasible useful chain "
                            + $"({DesireAxes.Abbrev(d.RequestingAxis)} entitlement "
                            + $"{F(ledger.Balance(d.RequestingAxis))}, discrete "
                            + $"{F(ledger.DiscreteAdmissionBudget(d.RequestingAxis))}, followup reserved {F(reserved)}); {diag}");
                    }
                    break;
                }

                PhaseACandidate selected = feasible
                    .OrderBy(c => ConsumesResourceNeededByHigherPriorityDemand(c, feasible, root) ? 1 : 0)
                    .ThenBy(c => ConsumesTraitRequiredByOtherFeasibleDemand(c, feasible) ? 1 : 0)
                    .ThenByDescending(ArbitrationScore)
                    .ThenByDescending(c => c.State.Demand.Value)
                    .ThenBy(c => (int)c.State.Demand.RequestingAxis)
                    .ThenBy(c => c.State.Ordinal)
                    .ThenBy(c => c.Plan.StableKey, System.StringComparer.Ordinal)
                    .First();

                AxisDemand chosenDemand = selected.State.Demand;
                MaterializationPlan plan = selected.Plan;
                MaterializationResult play = MaterializationExecutor.Execute(
                    snap, player, root, hand, ctx, plan, commitments);
                chainAttempts++;

                if (plan.Generation != null)
                    result.Reservation.RecordGenerationAttempt(plan.Generation, play);
                if (play.StateChanged)
                    result.StateChanged = true;
                if (play.ApSpent > 0f)
                {
                    ledger.Debit(chosenDemand.RequestingAxis, play.ApSpent);
                    result.AddDebit(chosenDemand.RequestingAxis, play.ApSpent);
                }

                if (!play.Deployed)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.A — {chosenDemand}: {plan.Kind} {AiCardLog.Plan(plan)} "
                        + $"chain did not deploy ({play.FailReason}); gen={(play.Generated ? 1 : 0)} "
                        + $"att={(play.Attached ? 1 : 0)}");
                    if (play.StateChanged)
                        snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    if (!play.StateChanged && plan.Generation == null)
                        selected.State.Blocked = true;
                    continue;
                }

                // Capability delivery is a LIVE state delta, not the printed BasePower of the card.
                // In particular, a unit deposited into garrison is useful reserve power but it has
                // delivered 0 mobile FieldCombatPower until a real Raid-eligible field actor exists.
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                CapabilityInventory afterInv = CapabilityInventory.Build(snap, player, commitments);
                float delivered = DeliveredCapabilityAmount(chosenDemand, inv, afterInv);
                bool operationallyDelivered = delivered > AiConfigV2.allocatorSliceEpsilon;

                float borrowed = 0f;
                if (operationallyDelivered)
                {
                    float alreadyReserved = ledger.ReservedFollowup(chosenDemand.RequestingAxis);
                    borrowed = ledger.CommitDiscreteFollowupBorrow(chosenDemand.RequestingAxis,
                        alreadyReserved + selected.FollowupAp);
                    ledger.ReserveFollowup(chosenDemand.RequestingAxis, selected.FollowupAp);
                    selected.State.Remaining = Mathf.Max(0f, selected.State.Remaining - delivered);
                }
                else
                {
                    // Do not repeatedly consume the whole hand on reserve-only placements for one
                    // operational shortage in this Phase-A pass. The unresolved demand is carried
                    // into Phase B/next turn and will be re-evaluated from the refreshed live state.
                    selected.State.Blocked = true;
                    AiDebugLog.Write($"[AI][V2]   strat.A — {chosenDemand}: deployment changed state but delivered "
                        + $"0 operational {chosenDemand.Capability}; reserve/potential only, residual unchanged");
                }
                result.CardsPlayed++;

                AiDebugLog.Write($"[AI][V2]   strat.A — {chosenDemand}: {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"@{plan.Deploy.Hex.Q},{plan.Deploy.Hex.R} "
                    + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(chosenDemand.RequestingAxis)}, {plan.Deploy.Kind}, "
                    + $"delivered {F(delivered)}, followup {(operationallyDelivered ? F(selected.FollowupAp) : "0")}ap reserved"
                    + (borrowed > AiConfigV2.allocatorSliceEpsilon ? $", discreteBorrow {F(borrowed)}ap" : "")
                    + $", {plan.StableKey})");
            }

            // Carry only the still-missing quantity into late-turn preparation. This is deliberately
            // a snapshot copy: Phase B may consume it without mutating the DemandLayer's frozen
            // strategic output or accidentally recreating quantities already delivered in Phase A.
            result.Reservation.UnresolvedDemands.Clear();
            foreach (DemandState state in states.Where(s => s.Remaining > 0f))
                result.Reservation.UnresolvedDemands.Add(CloneResidualDemand(state));

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — {result.CardsPlayed} chain(s), ledger now " + ledger.DebugLine());
            if (result.Reservation.UnresolvedDemands.Count > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — residual demands "
                    + string.Join(" | ", result.Reservation.UnresolvedDemands.Select(d => d.ToString())));
            return result;
        }

        private static AxisDemand CloneResidualDemand(DemandState state)
        {
            AxisDemand d = state.Demand;
            return new AxisDemand
            {
                RequestingAxis = d.RequestingAxis,
                Value = d.Value,
                TargetHex = d.TargetHex,
                Capability = d.Capability,
                DesiredAmount = Mathf.Max(0f, state.Remaining),
                RequiredTraits = d.RequiredTraits,
                PreferredTraits = d.PreferredTraits,
                MinimumFollowupAp = d.MinimumFollowupAp,
                ScoutContext = d.ScoutContext,
                Explain = d.Explain,
            };
        }

        private static bool ConsumesTraitRequiredByOtherFeasibleDemand(
            PhaseACandidate candidate, IReadOnlyList<PhaseACandidate> feasible)
        {
            TraitPreference spareTraits = candidate.Plan.ExpectedTraits & ~candidate.State.Demand.RequiredTraits;
            if (spareTraits == TraitPreference.None)
                return false;

            foreach (PhaseACandidate other in feasible)
            {
                if (ReferenceEquals(other.State, candidate.State))
                    continue;
                if ((other.State.Demand.RequiredTraits & spareTraits) != TraitPreference.None)
                    return true;
            }
            return false;
        }

        // Preserve a physically scarce resource for a harder capability gate before spending it on
        // a more fungible one. This is intentionally asymmetric: a Hero shortage is a binary raid
        // gate, while generic FieldCombatPower can usually be supplied by many bodies. Without this
        // portfolio guard the first infantry candidate could consume the last Human and make the
        // simultaneously feasible Hero demand impossible in the very next Phase-A iteration.
        private static bool ConsumesResourceNeededByHigherPriorityDemand(PhaseACandidate candidate,
            IReadOnlyList<PhaseACandidate> feasible, PlayerRoot root)
        {
            if (root == null || candidate.State?.Demand == null)
                return false;

            int candidatePriority = CapabilityResourcePriority(candidate.State.Demand.Capability);
            foreach (PhaseACandidate other in feasible)
            {
                if (ReferenceEquals(other.State, candidate.State) || other.State?.Demand == null)
                    continue;
                if (CapabilityResourcePriority(other.State.Demand.Capability) <= candidatePriority)
                    continue;

                foreach (ResourceType type in ResourceBundle.All)
                {
                    int spend = ResourceSpend(candidate.Plan, type);
                    int otherNeed = ResourceSpend(other.Plan, type);
                    if (spend <= 0 || otherNeed <= 0)
                        continue;
                    if (root.GetResource(type) - spend < otherNeed)
                        return true;
                }
            }
            return false;
        }

        private static int CapabilityResourcePriority(CapabilityKind capability)
        {
            switch (capability)
            {
                case CapabilityKind.Hero: return 3;
                case CapabilityKind.ScoutCapability: return 2;
                case CapabilityKind.FieldCombatPower: return 1;
                case CapabilityKind.GarrisonCombatPower: return 0;
                default: return 0;
            }
        }

        private static int ResourceSpend(MaterializationPlan plan, ResourceType type)
        {
            ResourceCost cost = plan?.ResCost;
            if (cost == null)
                return 0;
            switch (type)
            {
                case ResourceType.Human: return Mathf.Max(0, cost.human);
                case ResourceType.Energy: return Mathf.Max(0, cost.energy);
                case ResourceType.Materials: return Mathf.Max(0, cost.materials);
                case ResourceType.Tech: return Mathf.Max(0, cost.tech);
                default: return 0;
            }
        }

        private static float DeliveredCapabilityAmount(AxisDemand demand,
            CapabilityInventory before, CapabilityInventory after)
        {
            if (demand == null || before == null || after == null)
                return 0f;

            switch (demand.Capability)
            {
                case CapabilityKind.FieldCombatPower:
                    return Mathf.Max(0f, after.RaidAvailableFieldPower - before.RaidAvailableFieldPower);
                case CapabilityKind.GarrisonCombatPower:
                    return Mathf.Max(0f, after.GarrisonCombatPower - before.GarrisonCombatPower);
                case CapabilityKind.Hero:
                    return Mathf.Max(0, after.AvailableHeroes - before.AvailableHeroes);
                case CapabilityKind.ScoutCapability:
                    if ((demand.RequiredTraits & TraitPreference.Stealth) != 0)
                        return Mathf.Max(0, after.StealthScouts - before.StealthScouts);
                    return Mathf.Max(0, after.ReadyScouts - before.ReadyScouts);
                default:
                    return 0f;
            }
        }

        // Phase B may let an unresolved strategic demand outrank the generic surplus threshold, but
        // only when this placement can operationally deliver that demand. Matching CapabilityKind is
        // not enough: a FieldCombatPower card sent to garrison is reserve/potential, not a mobile
        // raid actor, and must be evaluated as ordinary surplus instead of receiving a residual pass.
        private static bool CanDeliverResidualOperationally(MaterializationPlan plan, AxisDemand demand)
        {
            if (plan == null || demand == null)
                return false;

            switch (demand.Capability)
            {
                case CapabilityKind.ScoutCapability:
                    return true;
                case CapabilityKind.GarrisonCombatPower:
                    return plan.Deploy.Kind == DeploymentKind.Garrison;
                case CapabilityKind.Hero:
                    return plan.Deploy.Kind == DeploymentKind.ExistingArmy
                        && plan.Deploy.Army != null
                        && plan.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation);
                case CapabilityKind.FieldCombatPower:
                {
                    if (plan.Deploy.Kind == DeploymentKind.Garrison)
                        return false;
                    CardDefinition def = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
                    bool hero = def != null && def.cardType == CardType.Hero;
                    if (!hero)
                        return true;
                    return plan.Deploy.Kind == DeploymentKind.ExistingArmy
                        && plan.Deploy.Army != null
                        && plan.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation);
                }
                default:
                    return false;
            }
        }

        private static float ArbitrationScore(PhaseACandidate c) =>
            Mathf.Max(0f, c.State.Demand.Value) * Mathf.Max(0.0001f, c.Plan.Score);

        public static StrategicPhaseResult UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            MaterializationReservation carriedReservation)
        {
            var result = new StrategicPhaseResult
            {
                Reservation = carriedReservation ?? new MaterializationReservation(),
            };
            if (player == null || root == null || hand == null || ctx == null)
                return result;

            // A strategic invalidation owns the remaining AP until the one bounded reaction pass
            // consumes it. Discovery is one reason; a late capability/hand change can now be another.
            if (StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber))
            {
                AiDebugLog.Write($"[AI][V2]   strat.B — deferred: pending strategic reaction interrupt; "
                    + $"preserve {root.ActionPoints} AP for bounded reaction pass");
                return result;
            }

            AiDebugLog.Write($"[AI][V2]   strat.B — {player.Nickname} hand {AiCardLog.Hand(hand)}");

            bool cleanStop = true;
            for (int i = 0; i < AiConfigV2.maxSurplusActionsPerTurn; i++)
            {
                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                (MaterializationPlan plan, float utility)? pick = MaterializationCandidateBuilder.BestSurplus(
                    snap, player, root, hand, ctx, inv, commitments, result.Reservation);
                if (pick == null)
                    break;

                MaterializationPlan plan = pick.Value.plan;
                AxisDemand matchedResidual = result.Reservation.BestUnresolvedDemandFor(plan);
                AxisDemand residual = matchedResidual != null && CanDeliverResidualOperationally(plan, matchedResidual)
                    ? matchedResidual
                    : null;
                SurplusAdmission admission = SurplusAdmissionPolicy.Evaluate(root, player, plan);

                if (matchedResidual != null && residual == null)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — residual bypass denied for {plan.StableKey}: "
                        + $"{plan.Deploy.Kind} cannot operationally deliver {matchedResidual.Capability}; "
                        + "evaluate as generic surplus");
                }

                if (residual == null && pick.Value.utility < admission.EffectiveThreshold)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — defer {plan.StableKey} {AiCardLog.Plan(plan)} "
                        + $"util {F(pick.Value.utility)} < threshold {F(admission.EffectiveThreshold)} "
                        + $"(base {F(admission.BaseThreshold)}, apSlack {F(admission.ApSlack)}, "
                        + $"resSlack {F(admission.ResourceSlackFactor)}), stop");
                    break;
                }

                if (residual != null)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — admit residual {residual} via "
                        + $"{plan.StableKey} {AiCardLog.Plan(plan)} util {F(pick.Value.utility)} "
                        + "(operational strategic residual outranks generic surplus)");
                }
                else
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — admit {plan.StableKey} {AiCardLog.Plan(plan)} "
                        + $"util {F(pick.Value.utility)} >= threshold {F(admission.EffectiveThreshold)} "
                        + $"(base {F(admission.BaseThreshold)}, apSlack {F(admission.ApSlack)}, "
                        + $"resSlack {F(admission.ResourceSlackFactor)})");
                }

                MaterializationResult play = MaterializationExecutor.Execute(snap, player, root, hand, ctx, plan, commitments);
                if (plan.Generation != null)
                    result.Reservation.RecordGenerationAttempt(plan.Generation, play);
                if (play.StateChanged)
                    result.StateChanged = true;
                if (!play.Deployed)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                        + $"chain did not deploy ({play.FailReason}); stop");
                    if (play.StateChanged)
                        snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    cleanStop = false;
                    break;
                }
                result.CardsPlayed++;

                // Rebuild operational supply before saying a residual was delivered. Garrison
                // deposit can be good reserve housekeeping without creating a mobile Raid actor.
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                CapabilityInventory afterInv = CapabilityInventory.Build(snap, player, commitments);
                float delivered = residual != null ? DeliveredCapabilityAmount(residual, inv, afterInv) : 0f;
                if (residual != null && delivered > AiConfigV2.allocatorSliceEpsilon)
                {
                    residual.DesiredAmount = Mathf.Max(0f, residual.DesiredAmount - delivered);
                    if (residual.DesiredAmount <= AiConfigV2.allocatorSliceEpsilon)
                        result.Reservation.UnresolvedDemands.Remove(residual);
                }
                AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"util {F(pick.Value.utility)} (ap {F(play.ApSpent)}, {plan.Deploy.Kind}, "
                    + $"delivered {F(delivered)}, {plan.StableKey})");

                // A residual capability did not exist when the ordinary MissionLayer was built.
                // Request a reaction only when this chain actually created executable supply.
                if (residual != null && delivered > AiConfigV2.allocatorSliceEpsilon)
                {
                    StrategicInterruptRegistry.MarkCapabilityChanged(player, ctx.TurnNumber, hand);
                    AiDebugLog.Write($"[AI][V2] strategic interrupt — Phase B delivered operational "
                        + $"{residual.Capability}; re-admit missions before further surplus spending");
                    cleanStop = false;
                    break;
                }
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {result.CardsPlayed} surplus chain(s) played");

            // Terminal draw — Phase B found no residual demand it could action and no worthwhile
            // surplus chain. AP does not carry to the next turn and nothing late-turn owns it
            // (housekeeping is zero-AP by invariant), so convert the genuinely stranded AP into
            // card option value. Skipped after a deploy FAILURE or a newly materialized residual
            // capability (both need the reaction pass first). Bounded.
            if (cleanStop && RunTerminalDraws(snap, player, root, hand, ctx, commitments, result))
                result.StateChanged = true;
            return result;
        }

        // spec §11–§15 / AC14–AC19. Priority stays: executable residual strategic demand and
        // worthwhile proactive surplus were already exhausted by the loop above; a card a prior
        // draw revealed that now makes either actionable STOPS further drawing (its slot is not
        // stolen — spec §13 / AC16). If that opportunity appeared only after a draw, request the
        // bounded strategic reaction so it is actually consumed this turn instead of merely noticed.
        private static bool RunTerminalDraws(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, ActorCommitments commitments, StrategicPhaseResult result)
        {
            if (!AiConfigV2.surplusAllowDraw || root == null || hand == null || ctx == null)
                return false;

            int drawn = 0;
            while (drawn < AiConfigV2.maxTerminalDrawsPerTurn)
            {
                if (!hand.HasFreeSlot || !hand.HasCardsLeftToDraw || !root.CanSpendActionPoints(ctx.DrawApCost))
                    break;

                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                (MaterializationPlan plan, float utility)? pick = MaterializationCandidateBuilder.BestSurplus(
                    snap, player, root, hand, ctx, inv, commitments, result.Reservation);
                if (pick != null)
                {
                    AxisDemand matchedResidual = result.Reservation.BestUnresolvedDemandFor(pick.Value.plan);
                    AxisDemand residual = matchedResidual != null
                        && CanDeliverResidualOperationally(pick.Value.plan, matchedResidual)
                        ? matchedResidual
                        : null;
                    SurplusAdmission adm = SurplusAdmissionPolicy.Evaluate(root, player, pick.Value.plan);
                    if (residual != null || pick.Value.utility >= adm.EffectiveThreshold)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.B terminal — stop: "
                            + $"{(residual != null ? "an operational residual demand" : "a worthwhile surplus chain")} "
                            + $"is now actionable ({pick.Value.plan.StableKey})");
                        if (drawn > 0)
                        {
                            StrategicInterruptRegistry.MarkHandOpportunity(player, ctx.TurnNumber, hand);
                            AiDebugLog.Write($"[AI][V2] strategic interrupt — terminal draw changed the "
                                + "actionable hand; replan before converting any more AP to draws");
                        }
                        break;
                    }
                }

                int apBefore = root.ActionPoints;
                int handBefore = hand.Hand.Count;
                if (!CardDrawExecutor.TryCycle(root, hand, ctx))
                    break;
                drawn++;
                AiDebugLog.Write($"[AI][V2]   strat.B terminal — no actionable residual/surplus, "
                    + $"convert stranded AP to draw; ap {apBefore}->{root.ActionPoints} "
                    + $"hand {handBefore}->{hand.Hand.Count}");
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (drawn > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {drawn} terminal draw(s)");
            result.CardsDrawn += drawn;
            return drawn > 0;
        }

        internal static bool ReservesOkAfterChain(PlayerRoot root, MaterializationPlan plan)
        {
            if (root == null || plan == null)
                return false;

            // Phase B runs after ordinary mission execution. Therefore it must protect only REAL
            // work still scheduled after it, not fixed speculative floors. There is currently no
            // resource/AP-costing late V2 stage: housekeeping is zero-cost by invariant, and AP
            // cannot be banked into the next turn. Consequently the exact safe pool is the real
            // PlayerRoot state remaining after earlier V2 mutations. If a future subsystem truly
            // needs resources after Phase B, that subsystem must add an explicit V2 reservation
            // contract before Phase B; V1 AiResourceReservation is intentionally bypassed in V2.
            if (root.ActionPoints - plan.ApCost < 0f)
                return false;

            ResourceCost cost = plan.ResCost;
            if (cost == null)
                return true;

            return Has(ResourceType.Human, cost.human)
                && Has(ResourceType.Energy, cost.energy)
                && Has(ResourceType.Materials, cost.materials)
                && Has(ResourceType.Tech, cost.tech);

            bool Has(ResourceType type, int spend)
            {
                float available = Mathf.Max(0f, root.GetResource(type));
                return available >= Mathf.Max(0, spend);
            }
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
