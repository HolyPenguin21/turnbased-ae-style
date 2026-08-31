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
                    (MaterializationPlan plan, float followupAp)? pick = MaterializationCandidateBuilder.BestForDemand(
                        snap, player, root, hand, ctx, demand, ledger, commitments, reserved, result.Reservation, inv);
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
                    .OrderBy(c => ConsumesTraitRequiredByOtherFeasibleDemand(c, feasible) ? 1 : 0)
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

                float alreadyReserved = ledger.ReservedFollowup(chosenDemand.RequestingAxis);
                float borrowed = ledger.CommitDiscreteFollowupBorrow(chosenDemand.RequestingAxis,
                    alreadyReserved + selected.FollowupAp);
                ledger.ReserveFollowup(chosenDemand.RequestingAxis, selected.FollowupAp);
                float delivered = DeliveredCapabilityAmount(chosenDemand, plan);
                selected.State.Remaining = Mathf.Max(0f, selected.State.Remaining - delivered);
                result.CardsPlayed++;

                AiDebugLog.Write($"[AI][V2]   strat.A — {chosenDemand}: {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"@{plan.Deploy.Hex.Q},{plan.Deploy.Hex.R} "
                    + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(chosenDemand.RequestingAxis)}, {plan.Deploy.Kind}, "
                    + $"followup {F(selected.FollowupAp)}ap reserved"
                    + (borrowed > AiConfigV2.allocatorSliceEpsilon ? $", discreteBorrow {F(borrowed)}ap" : "")
                    + $", {plan.StableKey})");

                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
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

        private static float DeliveredCapabilityAmount(AxisDemand demand, MaterializationPlan plan)
        {
            if (demand == null || plan == null)
                return 1f;
            switch (demand.Capability)
            {
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    CardDefinition d = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
                    return d != null ? Mathf.Max(1f, AiPower.ToPowerUnit(d).BasePower) : 1f;
                default:
                    return 1f;
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
                AxisDemand residual = result.Reservation.BestUnresolvedDemandFor(plan);
                SurplusAdmission admission = SurplusAdmissionPolicy.Evaluate(root, player, plan);
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
                        + "(strategic residual outranks generic surplus)");
                }
                else
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — admit {plan.StableKey} {AiCardLog.Plan(plan)} "
                        + $"util {F(pick.Value.utility)} >= threshold {F(admission.EffectiveThreshold)} "
                        + $"(base {F(admission.BaseThreshold)}, apSlack {F(admission.ApSlack)}, "
                        + $"resSlack {F(admission.ResourceSlackFactor)})");
                }

                bool handWasFull = !hand.HasFreeSlot;
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
                if (residual != null)
                {
                    residual.DesiredAmount = Mathf.Max(0f,
                        residual.DesiredAmount - DeliveredCapabilityAmount(residual, plan));
                    if (residual.DesiredAmount <= AiConfigV2.allocatorSliceEpsilon)
                        result.Reservation.UnresolvedDemands.Remove(residual);
                }
                AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"util {F(pick.Value.utility)} (ap {F(play.ApSpent)}, {plan.Deploy.Kind}, {plan.StableKey})");

                // Phase B is already after mission execution. There is no generic AP stockpile to
                // keep for a hypothetical later action: AP does not carry between turns. Draw only
                // needs its real cost to fit the AP that actually remains now. Any future late-turn
                // consumer must own an explicit reservation before Phase B rather than reviving a
                // global surplus floor.
                if (AiConfigV2.surplusAllowDraw && handWasFull && hand.HasFreeSlot
                    && root.ActionPoints >= ctx.DrawApCost
                    && CardDrawExecutor.TryCycle(root, hand, ctx))
                    result.StateChanged = true;

                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {result.CardsPlayed} surplus chain(s) played");

            // Terminal draw — Phase B found no residual demand it could action and no worthwhile
            // surplus chain. AP does not carry to the next turn and nothing late-turn owns it
            // (housekeeping is zero-AP by invariant), so convert the genuinely stranded AP into
            // card option value. Skipped after a deploy FAILURE (state may be mid-chain). Bounded.
            if (cleanStop && RunTerminalDraws(snap, player, root, hand, ctx, commitments, result))
                result.StateChanged = true;
            return result;
        }

        // spec §11–§15 / AC14–AC19. Priority stays: executable residual strategic demand and
        // worthwhile proactive surplus were already exhausted by the loop above; a card a prior
        // draw revealed that now makes either actionable STOPS further drawing (its slot is not
        // stolen — spec §13 / AC16). Never overflows the hand, never draws a dry deck, never
        // spends unaffordable AP.
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
                    AxisDemand residual = result.Reservation.BestUnresolvedDemandFor(pick.Value.plan);
                    SurplusAdmission adm = SurplusAdmissionPolicy.Evaluate(root, player, pick.Value.plan);
                    if (residual != null || pick.Value.utility >= adm.EffectiveThreshold)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.B terminal — stop: "
                            + $"{(residual != null ? "a residual demand" : "a worthwhile surplus chain")} "
                            + $"is now actionable ({pick.Value.plan.StableKey})");
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
