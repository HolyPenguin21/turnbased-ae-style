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

        // Production telemetry (Step 8B/8C — spec §12). Attempts/successes for each chain stage,
        // kept separate so a Generate → Attach → Deploy chain reads as one materialization with
        // one generated card and one equipment assignment. Scoring is untouched.
        public int MaterializationAttempts;
        public int MaterializationsSucceeded;
        public int GeneratedCardAttempts;
        public int GeneratedCardsSucceeded;
        public int EquipmentAssignmentAttempts;
        public int EquipmentAssignmentsSucceeded;
        public int InfrastructureAttempts;
        public int InfrastructureBuilt;
        public int CapabilityDeliveries;   // operational capability actually delivered to a demand

        public readonly Dictionary<DesireAxis, float> ApDebited = new Dictionary<DesireAxis, float>();
        public MaterializationReservation Reservation;

        public void AddDebit(DesireAxis a, float ap)
        {
            ApDebited.TryGetValue(a, out float cur);
            ApDebited[a] = cur + ap;
        }
    }

    // Phase-A working state for one capability demand.
    internal sealed class DemandState
    {
        public AxisDemand Demand;
        public float Remaining;
        public int Ordinal;
        public bool Blocked;
    }

    // ARCH-02 §8 — Strategic Phase A: the demand-driven card-play pass that runs BEFORE mission
    // planning. It orchestrates only; the algorithms it drives live in their own owners —
    // MaterializationCandidateBuilder (candidate chains), MaterializationPortfolioSolver (jointly
    // feasible set), MaterializationExecutor (play), CapabilityDeliveryEvaluator (delivered amount
    // + lease), InfrastructureFulfillment (build lane). Charged to the requesting axis through the
    // shared AxisBudgetLedger. Body is unchanged from the former StrategicManager.FulfillDemands.
    public static class StrategicPhaseA
    {
        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments)
        {
            if (player != null && root != null && ctx != null)
                TurnResourceTelemetry.CaptureStart(player, root, ctx.TurnNumber);

            var result = new StrategicPhaseResult { Reservation = new MaterializationReservation() };
            if (demands == null || demands.Count == 0 || player == null || root == null || hand == null || ledger == null)
                return result;

            // AI-MGR-02 §P0 — one shared per-turn generation budget: a reaction-round Phase A must
            // not reset the Challenge count a main-pass generation already spent.
            if (ctx != null)
                result.Reservation.GenerationAttemptsUsed = StrategicTempoBudget.GenerationUsed(player, ctx.TurnNumber);

            AiDebugLog.Write($"[AI][V2]   strat.A — {player.Nickname} hand {AiCardLog.Hand(hand)}");

            var states = demands.Select((d, i) => new DemandState
                {
                    Demand = d,
                    Remaining = d != null ? Mathf.Max(0f, d.DesiredAmount) : 0f,
                    Ordinal = i,
                })
                .Where(s => s.Demand != null && s.Remaining > 0f)
                .ToList();
            if (states.Count == 0)
                return result;

            // --- Infrastructure pre-pass. DEF/ECO/DEV EconomicInfrastructure / DevelopmentInfra
            //     demands are fulfilled by BuildingPlayExecutor through the authoritative gameplay
            //     API, NOT the Unit/Hero materialization chain below. Charged to the requesting
            //     axis exactly like a card play. Handled here once, then blocked so the generic
            //     loop does not emit a spurious "no feasible chain" for a capability it can't match.
            foreach (DemandState istate in states.Where(s => InfrastructureFulfillment.Handles(s.Demand.Capability)))
            {
                istate.Blocked = true;
                result.InfrastructureAttempts++;
                // Budget admission happens INSIDE TryFulfill, BEFORE any gameplay mutation: it
                // checks the requesting axis's discrete entitlement and live affordability, and
                // only then runs the authoritative build. A shortfall => nothing spent, not built.
                // §2.4 — independent controlled-state snapshot around the op (building count,
                // filled facility slots, army movement, resources), NOT derived from the op's own
                // result. A failed build that changed any of these is a rollback leak.
                V2InfraWorldStamp infraBefore = AiV2Trace.InfraStamp(player, root);
                InfraFulfillResult infra = InfrastructureFulfillment.TryFulfill(
                    snap, player, root, hand, ctx, istate.Demand, ledger);
                V2InfraWorldStamp infraAfter = AiV2Trace.InfraStamp(player, root);
                if (infra.StateChanged)
                    result.StateChanged = true;
                AiV2Trace.CheckInfrastructureRollback(istate.Demand.TraceId, infra.Built,
                    infra.StateChanged, infraBefore, infraAfter);
                if (infra.Built)
                {
                    // Debit the ACTUAL confirmed AP the authoritative transaction spent — the
                    // ledger records an already-permitted action, never grants overdraft.
                    // §2.3 — measure the REAL ledger balance drop around Debit so the check
                    // compares three independently sourced facts (physical / reported / ledger).
                    float infraLedgerBefore = ledger.Balance(istate.Demand.RequestingAxis);
                    if (infra.ApSpent > 0f)
                    {
                        ledger.Debit(istate.Demand.RequestingAxis, infra.ApSpent);
                        result.AddDebit(istate.Demand.RequestingAxis, infra.ApSpent);
                    }
                    float infraLedgerAfter = ledger.Balance(istate.Demand.RequestingAxis);
                    AiV2Trace.CheckPhaseAAp(istate.Demand.TraceId, istate.Demand.RequestingAxis,
                        infraBefore.Resources.Ap - infraAfter.Resources.Ap, infra.ApSpent,
                        infraLedgerBefore - infraLedgerAfter);
                    istate.Remaining = Mathf.Max(0f, istate.Remaining - 1f);
                    result.CardsPlayed++;
                    result.InfrastructureBuilt++;
                    result.CapabilityDeliveries++;
                    AiDebugLog.Write($"[AI][V2]   strat.A infra — {istate.Demand}: built {infra.Detail} "
                        + $"(ap {F(infra.ApSpent)} -> {DesireAxes.Abbrev(istate.Demand.RequestingAxis)})");
                    snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                }
                else
                {
                    AiDebugLog.Write($"[AI][V2]   strat.A infra — {istate.Demand}: not built ({infra.Detail})");
                }
            }

            int chainAttempts = 0;
            while (chainAttempts < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
            {
                List<DemandState> active = states.Where(s => !s.Blocked && s.Remaining > 0f).ToList();
                if (active.Count == 0)
                    break;

                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);

                // AI-MGR-01 review-r3 — TOP-K worthwhile chains per active demand (each carries its
                // own opportunity-adjusted DecisionScore), then a bounded max-total injective
                // assignment: exactly one collision-free chain per demand (or none), so no hand
                // card / generation source is ever double-counted as available capacity, and the
                // globally best total is chosen (not a greedy per-demand pick).
                var options = new Dictionary<DemandState, List<DemandCandidate>>();
                foreach (DemandState state in active)
                {
                    bool competingHeroDemand = state.Demand.Capability == CapabilityKind.ScoutCapability
                        && active.Any(other => !ReferenceEquals(other, state)
                            && other.Remaining > AiConfigV2.allocatorSliceEpsilon
                            && other.Demand.Capability == CapabilityKind.Hero);
                    List<DemandCandidate> top =
                        MaterializationCandidateBuilder.TopForDemand(snap, player, root, hand, ctx, state.Demand,
                            ledger, commitments, ledger.ReservedFollowup(state.Demand.RequestingAxis),
                            result.Reservation, inv, competingHeroDemand, AiConfigV2.phaseATopK);
                    if (top.Count > 0)
                        options[state] = top;
                }

                Dictionary<DemandState, DemandCandidate> assigned =
                    options.Count > 0
                        ? MaterializationPortfolioSolver.BestInjectiveAssignment(options, root, player,
                            Mathf.Max(0, AiConfigV2.maxGenerationActionsPerTurn
                                        - result.Reservation.GenerationAttemptsUsed))
                        : new Dictionary<DemandState, DemandCandidate>();

                var feasible = assigned.Select(kv => new PhaseACandidate(kv.Key, kv.Value)).ToList();

                if (feasible.Count == 0)
                {
                    foreach (DemandState state in active)
                    {
                        AxisDemand d = state.Demand;
                        float reserved = ledger.ReservedFollowup(d.RequestingAxis);
                        if (options.TryGetValue(state, out var topOpts) && topOpts.Count > 0)
                        {
                            DemandCandidate b = topOpts[0];
                            AiDebugLog.Write($"[AI][V2]   strat.A hold — {d}: best chain {b.Plan.StableKey} "
                                + $"play {F(b.PlayScore)} hold {F(b.HoldValue)} decision {F(b.DecisionScore)} "
                                + "not worth playing over holding the card / lost to contention; keep in hand");
                            continue;
                        }
                        string diag = MaterializationDiagnostics.ExplainNoChain(
                            snap, player, root, hand, ctx, d, ledger, commitments, reserved);
                        AiDebugLog.Write($"[AI][V2]   strat.A — {d}: no feasible useful chain "
                            + $"({DesireAxes.Abbrev(d.RequestingAxis)} entitlement {F(ledger.Balance(d.RequestingAxis))}, "
                            + $"discrete {F(ledger.DiscreteAdmissionBudget(d.RequestingAxis))}, "
                            + $"followup reserved {F(reserved)}); {diag}");

                        // §17 — an unfulfilled Aggression/Recon capability demand plus an empty
                        // resource stock is a starvation signal for that resource (own state only).
                        if (d.RequestingAxis == DesireAxis.Aggression || d.RequestingAxis == DesireAxis.Recon)
                            foreach (ResourceType rt in ResourceBundle.All)
                                if (root.GetResource(rt) <= 0f)
                                    ResourceStarvationRegistry.RecordBlock(player, rt);
                    }
                    break;
                }

                // AI-MGR-01 review-r4 finding 1 — the evaluator's opportunity-adjusted DecisionScore
                // is the FINAL arbiter. The `feasible` set is already a JOINTLY feasible collision-
                // free assignment (BestInjectiveAssignment now models the shared generation attempt +
                // AP + H/E/M/T pools), so there is no longer a hidden hardcoded capability-priority
                // layer deciding that a Hero chain "protects" resources from a higher-DecisionScore
                // Field chain. Only deterministic tie-breakers follow the score.
                PhaseACandidate selected = feasible
                    .OrderByDescending(MaterializationPortfolioSolver.ArbitrationScore)
                    .ThenByDescending(c => c.State.Demand.Value)
                    .ThenBy(c => (int)c.State.Demand.RequestingAxis)
                    .ThenBy(c => c.State.Ordinal)
                    .ThenBy(c => c.Plan.StableKey, System.StringComparer.Ordinal)
                    .First();

                AxisDemand chosenDemand = selected.State.Demand;
                MaterializationPlan plan = selected.Plan;
                var armyIdsBefore = new HashSet<int>(snap.Self?.Armies?
                    .Where(a => a != null).Select(a => a.ArmyId) ?? Enumerable.Empty<int>());
                int chainApBefore = root.ActionPoints;
                MaterializationResult play = MaterializationExecutor.Execute(
                    snap, player, root, hand, ctx, plan, commitments);
                int chainApAfter = root.ActionPoints;
                chainAttempts++;

                // Production telemetry (spec §12) — attempts/successes per chain stage. Derived
                // from the plan shape + MaterializationResult; no scoring change.
                result.MaterializationAttempts++;
                if (play.Deployed) result.MaterializationsSucceeded++;
                if (plan.Generation != null)
                {
                    result.GeneratedCardAttempts++;
                    if (play.Generated) result.GeneratedCardsSucceeded++;
                }
                if (plan.UsesEquipment)
                {
                    result.EquipmentAssignmentAttempts++;
                    if (play.Attached) result.EquipmentAssignmentsSucceeded++;
                }

                if (plan.Generation != null)
                {
                    result.Reservation.RecordGenerationAttempt(plan.Generation, play);
                    StrategicTempoBudget.RecordGenerationAttempt(player, ctx.TurnNumber);
                }
                if (play.StateChanged)
                    result.StateChanged = true;

                // §2.3 — measure the REAL AxisBudgetLedger balance drop around Debit, BEFORE any
                // discrete follow-up borrow moves balances, so the check has three independently
                // sourced facts: physical AP delta, the chain's reported ApSpent, and the actual
                // ledger debit (catches a missing / wrong-axis / wrong-amount Debit).
                float chainLedgerBefore = ledger.Balance(chosenDemand.RequestingAxis);
                if (play.ApSpent > 0f)
                {
                    ledger.Debit(chosenDemand.RequestingAxis, play.ApSpent);
                    result.AddDebit(chosenDemand.RequestingAxis, play.ApSpent);
                }
                float chainLedgerAfter = ledger.Balance(chosenDemand.RequestingAxis);
                AiV2Trace.CheckPhaseAAp(chosenDemand.TraceId, chosenDemand.RequestingAxis,
                    chainApBefore - chainApAfter, play.ApSpent, chainLedgerBefore - chainLedgerAfter);

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

                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                CapabilityInventory afterInv = CapabilityInventory.Build(snap, player, commitments);
                bool operationallyDelivered = CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(player, ctx, snap, plan,
                    chosenDemand, inv, afterInv, armyIdsBefore, out float delivered);

                float borrowed = 0f;
                if (operationallyDelivered)
                {
                    float alreadyReserved = ledger.ReservedFollowup(chosenDemand.RequestingAxis);
                    borrowed = ledger.CommitDiscreteFollowupBorrow(chosenDemand.RequestingAxis,
                        alreadyReserved + selected.FollowupAp);
                    ledger.ReserveFollowup(chosenDemand.RequestingAxis, selected.FollowupAp);
                    selected.State.Remaining = Mathf.Max(0f, selected.State.Remaining - delivered);
                    result.CapabilityDeliveries++;
                }
                else
                {
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
                TraceId = d.TraceId,
                RequestingAxis = d.RequestingAxis,
                Value = d.Value,
                TargetHex = d.TargetHex,
                Capability = d.Capability,
                DesiredAmount = Mathf.Max(0f, state.Remaining),
                RequiredTraits = d.RequiredTraits,
                PreferredTraits = d.PreferredTraits,
                MinimumFollowupAp = d.MinimumFollowupAp,
                ScoutContext = d.ScoutContext,
                EconomyResourceType = d.EconomyResourceType,
                RequiredCapabilityPower = d.RequiredCapabilityPower,
                Explain = d.Explain,
            };
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
