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
            public readonly DemandCandidate Cand;

            public PhaseACandidate(DemandState state, DemandCandidate cand)
            {
                State = state;
                Cand = cand;
            }

            public MaterializationPlan Plan => Cand.Plan;
            public float FollowupAp => Cand.FollowupAp;
            public float DecisionScore => Cand.DecisionScore;
        }

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
                        ? BestInjectiveAssignment(options, root, player,
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
                    .OrderByDescending(ArbitrationScore)
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
                bool operationallyDelivered = FinalizeOperationalDelivery(player, ctx, snap, plan,
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

        // AI-MGR-01 P1.3 — the physical hand-card instances a chain consumes (base + equipment).
        // The generation source is tracked separately by GenerationStep.CardKey.
        private static IReadOnlyList<CardData> PlanCards(MaterializationPlan p)
        {
            var list = new List<CardData>(2);
            if (p?.BaseCardInHand != null) list.Add(p.BaseCardInHand);
            if (p?.EquipmentInHand != null) list.Add(p.EquipmentInHand);
            return list;
        }

        private static IReadOnlyList<int> OperationalLeaseArmyIds(HashSet<int> armyIdsBefore,
            WorldSnapshot after, MaterializationPlan plan, AxisDemand demand)
        {
            var ids = new HashSet<int>();
            if (after?.Self?.Armies == null || demand == null)
                return ids.ToList();

            int existingRecipient = plan?.Deploy.Army != null ? plan.Deploy.Army.Id : -1;
            foreach (ArmySnapshot army in after.Self.Armies)
            {
                if (army == null || (!armyIdsBefore.Contains(army.ArmyId) && !IsOperationalForDemand(army, demand)))
                    continue;
                if (army.ArmyId == existingRecipient && IsOperationalForDemand(army, demand))
                    ids.Add(army.ArmyId);
            }
            foreach (ArmySnapshot army in after.Self.Armies)
                if (army != null && !armyIdsBefore.Contains(army.ArmyId) && IsOperationalForDemand(army, demand))
                    ids.Add(army.ArmyId);
            return ids.OrderBy(id => id).ToList();
        }

        private static bool IsOperationalForDemand(ArmySnapshot army, AxisDemand demand)
        {
            if (army == null || demand == null)
                return false;
            switch (demand.Capability)
            {
                case CapabilityKind.FieldCombatPower:
                    return RaidAssemblyPlanner.IsReadyRaidActor(army);
                case CapabilityKind.GarrisonCombatPower:
                    return army.IsGarrison;
                case CapabilityKind.Hero:
                    return army.HasHero && RaidAssemblyPlanner.IsReadyRaidActor(army);
                case CapabilityKind.ScoutCapability:
                    if (!army.IsSoloRecce || army.CurrentMovement <= 0)
                        return false;
                    return (demand.RequiredTraits & TraitPreference.Stealth) == 0
                        || army.IsHidden || army.CanEnterStealth;
                default:
                    return false;
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

        // §3 — the ONE post-delivery finalization path shared by Phase A, Phase B and (through
        // those two) the bounded StrategicReactionPass. It owns delivered-capability measurement
        // and the Housekeeping capability lease for every army created/modified to satisfy a live
        // strategic demand, so a later Phase A / Phase B divergence cannot silently drop the lease
        // again. Callers still own the parts that genuinely differ by phase: Phase A's discrete
        // follow-up AP borrow against the axis ledger, and each phase's own residual bookkeeping.
        private static bool FinalizeOperationalDelivery(PlayerSetupData player, AiTurnContext ctx,
            WorldSnapshot afterSnap, MaterializationPlan plan, AxisDemand demand,
            CapabilityInventory before, CapabilityInventory after, HashSet<int> armyIdsBefore,
            out float delivered)
        {
            delivered = DeliveredCapabilityAmount(demand, before, after);
            if (delivered <= AiConfigV2.allocatorSliceEpsilon)
                return false;
            IReadOnlyList<int> leased = OperationalLeaseArmyIds(armyIdsBefore, afterSnap, plan, demand);
            StrategicCapabilityLeaseRegistry.Mark(player, ctx.TurnNumber, demand.Capability, leased);
            return true;
        }

        private static bool PlanBaseIsHeroCard(MaterializationPlan plan)
        {
            CardDefinition def = plan?.BaseCardInHand?.Definition ?? plan?.GeneratedBaseDef;
            return def != null && def.cardType == CardType.Hero;
        }

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

        // AI-MGR-01 review-r3 — cross-demand arbitration ranks purely on the opportunity-adjusted
        // DecisionScore (Play - Hold + urgency), computed once in the builder. demand.Value is NOT
        // re-multiplied here — its weight already entered DecisionScore through UrgencyBonus.
        private static float ArbitrationScore(PhaseACandidate c) => c.DecisionScore;

        // Bounded max-total injective assignment over the active demands (<= maxDemandFulfillment
        // ActionsPerTurn, each with <= phaseATopK options): choose one Worthwhile chain per demand
        // (or none) so no hand card / generation source is used twice, maximising the total
        // DecisionScore. Branching factor (K+1)^demandCount — trivial at K=3, count<=3.
        //
        // AI-MGR-01 review-r4 finding 3 — the chosen portfolio must be JOINTLY feasible, not just
        // card-disjoint: the ONE per-turn generation attempt and the shared AP / H-E-M-T pools are
        // consumed by the whole accepted set. Two chains that are each individually affordable can be
        // un-runnable together (both want the last Tech; both want the single Challenge with
        // different CardKeys). Without this the search returns a phantom portfolio and the downstream
        // pick has to paper over it — which is exactly the hidden capability-priority layer finding 1
        // removes.
        private static Dictionary<DemandState, DemandCandidate>
            BestInjectiveAssignment(
                Dictionary<DemandState, List<DemandCandidate>> options,
                PlayerRoot root, PlayerSetupData player, int genAttemptsRemaining)
        {
            var demands = options.Keys.OrderBy(d => d.Ordinal).ToList();
            var best = new Dictionary<DemandState, DemandCandidate>();
            float bestSum = float.NegativeInfinity;
            var usedCards = new HashSet<CardData>();
            var usedGen = new HashSet<string>();
            var acc = new Dictionary<DemandState, DemandCandidate>();

            float apPool = root != null
                ? root.ActionPoints - AiConfigV2.housekeepingApReserve : float.MaxValue;
            var resPool = new Dictionary<ResourceType, int>();
            foreach (ResourceType t in ResourceBundle.All)
                resPool[t] = root != null
                    ? Mathf.Max(0, Mathf.FloorToInt(Game.Ai.AiResourceReservation.Available(root, player, t)))
                    : int.MaxValue;

            float apUsed = 0f;
            int genUsed = 0;
            var resUsed = new Dictionary<ResourceType, int>();
            foreach (ResourceType t in ResourceBundle.All) resUsed[t] = 0;

            bool Fits(DemandCandidate c)
            {
                float ap = (c.Plan?.ApCost ?? 0f) + c.FollowupAp;
                if (apUsed + ap > apPool + AiConfigV2.allocatorSliceEpsilon)
                    return false;
                if (c.Plan?.Generation != null && genUsed + 1 > genAttemptsRemaining)
                    return false;
                ResourceCost rc = c.Plan?.ResCost;
                if (rc != null)
                    foreach (ResourceType t in ResourceBundle.All)
                        if (resUsed[t] + rc.Get(t) > resPool[t])
                            return false;
                return true;
            }

            void Rec(int i, float sum)
            {
                if (i == demands.Count)
                {
                    if (sum > bestSum || (sum == bestSum && acc.Count > best.Count))
                    {
                        bestSum = sum;
                        best = new Dictionary<DemandState, DemandCandidate>(acc);
                    }
                    return;
                }
                DemandState d = demands[i];
                Rec(i + 1, sum); // skip this demand
                foreach (DemandCandidate c in options[d])
                {
                    if (!c.Worthwhile)
                        continue;
                    IReadOnlyList<CardData> cards = PlanCards(c.Plan);
                    string gk = c.Plan.Generation?.CardKey;
                    if (cards.Any(usedCards.Contains))
                        continue;
                    if (!string.IsNullOrEmpty(gk) && usedGen.Contains(gk))
                        continue;
                    if (!Fits(c))
                        continue;
                    foreach (CardData cc in cards) usedCards.Add(cc);
                    bool addedGen = !string.IsNullOrEmpty(gk) && usedGen.Add(gk);
                    bool countGen = c.Plan?.Generation != null;
                    if (countGen) genUsed++;
                    float apAdd = (c.Plan?.ApCost ?? 0f) + c.FollowupAp;
                    apUsed += apAdd;
                    ResourceCost rc = c.Plan?.ResCost;
                    if (rc != null)
                        foreach (ResourceType t in ResourceBundle.All) resUsed[t] += rc.Get(t);

                    acc[d] = c;
                    Rec(i + 1, sum + c.DecisionScore);
                    acc.Remove(d);

                    if (rc != null)
                        foreach (ResourceType t in ResourceBundle.All) resUsed[t] -= rc.Get(t);
                    apUsed -= apAdd;
                    if (countGen) genUsed--;
                    foreach (CardData cc in cards) usedCards.Remove(cc);
                    if (addedGen) usedGen.Remove(gk);
                }
            }
            Rec(0, 0f);
            return best;
        }

        // §P1 — multiplier on the surplus-admission threshold for a generic garrison deposit when
        // the garrison is already a strong defensive stack (>= a fraction of BestStackPotential)
        // and no asset is threatened. 1f otherwise.
        private static float GarrisonSaturationThresholdMult(WorldSnapshot snap, MaterializationPlan plan,
            AxisDemand residual)
        {
            if (residual != null || plan == null || plan.Deploy.Kind != DeploymentKind.Garrison
                || snap?.Self == null)
                return 1f;
            bool assetThreat = snap.Threat?.Threats != null && snap.Threat.Threats.Count > 0;
            if (assetThreat)
                return 1f;
            float reserve = AiConfigV2.garrisonSaturatedReserveFractionOfBestStack
                * Mathf.Max(0f, snap.Self.BestStackPotential);
            return reserve > 0f && snap.Self.GarrisonPower >= reserve
                ? AiConfigV2.garrisonSaturatedSurplusThresholdMult : 1f;
        }

        // §P1 — a generic (no-residual) surplus chain of ANY kind (Direct / Attach / Generate*)
        // that founds a fresh lone-member army (NewArmy / ReusableShell) on a hex where a garrison
        // OR an already-viable friendly field army sits: Housekeeping folds/absorbs that
        // lone-member army the same turn (create -> fold). A genuine forward outpost — no base and
        // no viable force of ours on the hex — is still allowed.
        private static bool GenericSurplusWouldChurn(PlayerSetupData player, MaterializationPlan plan)
        {
            if (plan == null)
                return false;
            if (plan.Deploy.Kind != DeploymentKind.NewArmy && plan.Deploy.Kind != DeploymentKind.ReusableShell)
                return false;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || !a.Hex.Equals(plan.Deploy.Hex))
                    continue;
                if (a.IsGarrison)
                    return true;
                if (a.IsPrison || a.IsAirArmy || a.IsAirfield || AiArmyRoles.IsSoloRecce(a))
                    continue;
                if (a.Members.Count >= 2
                    && AiPower.EffectiveArmyPower(a.Members) >= AiConfigV2.housekeepingViabilityPowerFloor)
                    return true;
            }
            return false;
        }

        // §P1 — generic surplus must not add a scout beyond the physical IsSoloRecce portfolio
        // cap, across EVERY chain kind (BestSurplus treats a recce card as ScoutCapability and
        // will build NewArmy / ReusableShell / Attach / Generate placements for it — the Recon
        // DemandLayer portfolio cap never sees those). Primary bound is the CURRENT desired
        // concurrency + a warm spare; ReconConcurrencyPolicy.HardCap is the absolute ceiling.
        private static bool ScoutSurplusPortfolioSaturated(PlayerSetupData player, MaterializationPlan plan,
            WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives)
        {
            if (plan == null || plan.FinalCapability != CapabilityKind.ScoutCapability)
                return false;
            int solo = ArmyRegistry.AllForOwner(player).Count(a => a != null && AiArmyRoles.IsSoloRecce(a));
            if (solo >= ReconConcurrencyPolicy.HardCap)
                return true;
            if (reconObjectives == null)
                return false;
            var runnable = reconObjectives
                .Where(o => o != null && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            int desired = ReconConcurrencyPolicy.DesiredTotal(snap, runnable);
            return solo >= desired + AiConfigV2.scoutSurplusWarmSpare;
        }

        // ===================================================================================
        //  END-OF-TURN TEMPO ARBITER  (AI-MGR-02)
        // ===================================================================================
        //  The SINGLE late-turn spend entry. Every end-of-turn decision — PlayCard (materialization
        //  OR non-combat, scored ONLY by StrategicCardEvaluator), DrawCard, an existing strategic
        //  spend (maintenance / decisive structure pressure), HoldResources, EndTurn — is a
        //  candidate in ONE comparable utility space. Each iteration rebuilds live world / hand /
        //  resources / reservations, rebuilds every candidate, executes exactly ONE, inspects the
        //  REAL result, and rebuilds again. It stops when max(Hold, EndTurn) >= the best actionable
        //  spend, or the hard action bound is hit. There is no fixed lane order and no bypass:
        //  a failed / no-op candidate is parked for the current state version and cannot be
        //  re-chosen until a real state mutation invalidates the whole candidate set (spec §2/§3).
        //
        //  §5 single-count: a PlayCard candidate's utility is the StrategicCardEvaluator NetScore
        //  VERBATIM — the arbiter never re-adds hand pressure / resource pressure / hold. Those
        //  factors are recomputed here ONLY for the arbiter-owned candidates (Draw / Hold / spend).
        public static System.Collections.IEnumerator UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            MaterializationReservation carriedReservation, StrategicPhaseResult result,
            IReadOnlyList<ReconObjective> reconObjectives = null)
        {
            result.Reservation = carriedReservation ?? new MaterializationReservation();
            if (player == null || root == null || hand == null || ctx == null)
                yield break;

            // AI-MGR-02 §P0.4 — ONE turn-scoped budget for every tempo action. Every hard cap
            // (total actions / surplus card plays / draws / generation attempts) is enforced
            // against this, so re-entering the arbiter (main Phase B, reaction round, reaction
            // follow-up, Housekeeping tempo re-run) cannot buy more than the per-turn limit. Keep
            // MGR-01's internal generation counter in sync with the shared budget.
            StrategicTempoBudget budget = StrategicTempoBudget.For(player, ctx.TurnNumber);
            result.Reservation.GenerationAttemptsUsed =
                Mathf.Max(result.Reservation.GenerationAttemptsUsed, budget.GenerationAttemptsUsed);

            // --- §7 (round 5) reaction reservation: a BOUNDED AP BUDGET + the persistent H/E/M/T
            //     ENVELOPE that a REAL feasibility probe proved is needed to keep at least one
            //     feasible reaction possible. The budget stays generic (the replan picks its own
            //     action) but is only created when the probe passes and the AP >= min feasible AP.
            StrategicReactionOpportunity reactionOpp =
                StrategicReactionPass.BuildReactionOpportunity(player, root, ctx, snap);
            if (reactionOpp.IsActionable)
            {
                StrategicResourceReservationLedger.Upsert(player, ctx.TurnNumber,
                    new StrategicResourceReservation
                    {
                        Owner = reactionOpp.OwnerKey,
                        Reason = StrategicReservationReason.StrategicReactionPass,
                        Resource = StrategicReservedResource.ActionPoints,
                        Amount = reactionOpp.ReservedApBudget,
                        ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                    });
                if (reactionOpp.Envelope != null)
                    foreach (ResourceType rt in ResourceBundle.All)
                    {
                        int n = reactionOpp.Envelope.Get(rt);
                        if (n <= 0) continue;
                        StrategicResourceReservationLedger.Upsert(player, ctx.TurnNumber,
                            new StrategicResourceReservation
                            {
                                Owner = reactionOpp.OwnerKey,
                                Reason = StrategicReservationReason.StrategicReactionPass,
                                Resource = StrategicResourceReservationLedger.Map(rt),
                                Amount = n,
                                ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                            });
                    }
                AiDebugLog.Write($"[AI][V2]   strat.B — reaction feasible ({reactionOpp.Kind}); reserve BOUNDED "
                    + $"{F(reactionOpp.ReservedApBudget)} AP"
                    + (reactionOpp.Envelope != null ? $" + envelope [{ResCostStr(reactionOpp.Envelope)}]" : "")
                    + $" (owner={reactionOpp.OwnerKey} exp=EndOfReaction; {reactionOpp.Rationale}), spendable AP now "
                    + $"{F(StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints))}.");
            }
            else
            {
                // spec §7 — an existing reaction budget reservation is released the moment no
                // feasible same-turn reaction remains (same-turn re-arbitration is Housekeeping's re-run).
                StrategicResourceReservationLedger.ReleaseByReason(player, ctx.TurnNumber,
                    StrategicReservationReason.StrategicReactionPass);
                if (StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber))
                    AiDebugLog.Write($"[AI][V2]   strat.B — pending invalidation but no FEASIBLE reaction "
                        + $"({reactionOpp.FailReason}); NOT reserving — tempo uses the full pool (spec §7)");
            }

            AiDebugLog.Write($"[AI][V2]   strat.B — {player.Nickname} hand {AiCardLog.Hand(hand)}");

            AiDebugLog.Write($"[AI][V2]   strat.B/tempo — budget on entry: total {budget.TotalTempoActionsUsed}/"
                + $"{AiConfigV2.maxEndOfTurnTempoActionsPerTurn}, cards {budget.SurplusCardActionsUsed}/"
                + $"{AiConfigV2.maxSurplusActionsPerTurn}, draws {budget.DrawActionsUsed}/"
                + $"{AiConfigV2.maxTerminalDrawsPerTurn}, gen {budget.GenerationAttemptsUsed}/{AiConfigV2.maxGenerationActionsPerTurn}");

            // §P1.8 — parking is keyed by (ActionKey, StateVersion). A parked candidate stays
            // parked only while StateVersion is unchanged; any real mutation bumps the version and
            // every park goes stale (== the whole candidate set is rebuilt, spec §2/§3).
            var parkedAt = new Dictionary<string, int>(System.StringComparer.Ordinal);
            int stateVersion = 0;
            int iter = 0;
            string stopReason = null;
            while (!budget.TotalCapHit && iter <= AiConfigV2.maxEndOfTurnTempoActionsPerTurn + 1)
            {
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                float spendableAp = StrategicResourceReservationLedger.SpendableAp(
                    player, ctx.TurnNumber, root.ActionPoints);

                var cands = BuildTempoCandidates(snap, player, root, hand, ctx, commitments, result,
                    reconObjectives, spendableAp, budget, verbose: iter == 0);

                float endU = cands.First(c => c.Kind == TempoKind.EndTurn).Utility;   // 0
                float holdPolicyFull = HoldResourcesUtility(root, snap, null);        // whole pool — diagnostic only

                LogTempoIterationHeader(ctx, root, snap, player, hand, budget, iter, spendableAp);

                // §P0 (round 4) — ONE comparable space, but HoldResources is NOT a global stop gate.
                //   · PlayCard (mat / non-combat): utility = StrategicCardEvaluator NetScore VERBATIM
                //     (the evaluator already owns HoldValue / ScarcityValue / ResourcePressureBenefit).
                //   · AP-only actions (Draw, AP-only Pressure): utility verbatim — keeping H/E/M/T is
                //     COMPATIBLE with spending AP, so the persistent-hold policy never blocks them.
                //   · Non-card spend (capacity upgrade): effective = utility − holdOfConsumed, i.e.
                //     the retention value of ONLY the persistent resources IT consumes.
                // A candidate is eligible when effective > max(EndTurn, tempoMinSpendUtility).
                TempoCandidate best = null;
                float bestEff = float.NegativeInfinity;
                foreach (TempoCandidate c in cands
                    .Where(c => IsSpend(c.Kind))
                    .OrderByDescending(c => c.Utility)
                    .ThenBy(c => c.ActionKey, System.StringComparer.Ordinal))
                {
                    string block = TempoBlockReason(c, spendableAp, budget, parkedAt, stateVersion, player, root, ctx);
                    float holdOfConsumed = c.Kind == TempoKind.MaintenanceSpend && c.ResCost != null
                        ? HoldResourcesUtility(root, snap, c.ResCost) : 0f;
                    float eff = c.Utility - holdOfConsumed;
                    AiDebugLog.Write($"[AI][V2]     cand {c.Kind} rawUtil {F(c.Utility)} holdOfConsumed {F(holdOfConsumed)}"
                        + $" eff {F(eff)} apCost {F(c.ApCost)} resCost [{ResCostStr(c.ResCost)}] key={c.ActionKey}"
                        + (block != null ? $" BLOCKED: {block}" : "")
                        + (c.DrawDiag != null ? $" {{{c.DrawDiag}}}" : "")
                        + $" — {c.Label}");
                    if (block == null && eff > bestEff)
                    {
                        best = c;
                        bestEff = eff;
                    }
                }

                AiDebugLog.Write($"[AI][V2]     policy Hold(full pool) {F(holdPolicyFull)} (diag only)  |  EndTurn {F(endU)}");

                float spendBar = Mathf.Max(AiConfigV2.tempoMinSpendUtility, endU);
                if (best == null)
                {
                    stopReason = "no eligible spend candidate " + BudgetSummary(budget);
                    break;
                }
                if (bestEff <= spendBar)
                {
                    stopReason = $"best spend {best.Kind} eff {F(bestEff)} <= max(minSpend {F(AiConfigV2.tempoMinSpendUtility)}, "
                        + $"endTurn {F(endU)}) = {F(spendBar)}";
                    break;
                }

                int ap0 = root.ActionPoints;
                int h0 = root.GetResource(Game.Economy.ResourceType.Human), e0 = root.GetResource(Game.Economy.ResourceType.Energy);
                int m0 = root.GetResource(Game.Economy.ResourceType.Materials), t0 = root.GetResource(Game.Economy.ResourceType.Tech);
                var exec = new TempoExecutionResult();
                switch (best.Kind)
                {
                    case TempoKind.PlayMat:
                        snap = ExecuteMatSurplus(best.Mat, snap, player, root, hand, ctx, commitments, result, ref exec);
                        break;
                    case TempoKind.PlayNonCombat:
                        snap = ExecuteNonCombatSurplus(best.Nc, snap, player, root, hand, ctx, result, ref exec);
                        break;
                    case TempoKind.Draw:
                        if (CardDrawExecutor.TryCycle(root, hand, ctx))
                        {
                            exec.Succeeded = exec.StateChanged = exec.Progressed = exec.Drawn = true;
                            result.CardsDrawn++;
                        }
                        else exec.FailReason = "TryCycle refused";
                        break;
                    case TempoKind.MaintenanceSpend:
                        // Execute EXACTLY the chosen candidate (no re-selection by the policy).
                        exec.Succeeded = best.Spend.Execute(player, root, ctx,
                            out bool msChanged, out bool msProgressed);
                        exec.StateChanged = msChanged;
                        exec.Progressed = msProgressed;
                        if (!exec.Succeeded) exec.FailReason = "capacity upgrade refused";
                        break;
                    case TempoKind.PressureSpend:
                    {
                        bool pc = false;
                        yield return StrategicPressureAdvance.Execute(player, root, ctx, best.Pressure, v => pc = v);
                        exec.Succeeded = exec.StateChanged = exec.Progressed = pc;
                        if (!pc) exec.FailReason = "no advance step taken";
                        break;
                    }
                }
                exec.ApSpent = Mathf.Max(0, ap0 - root.ActionPoints);
                exec.HumanSpent = Mathf.Max(0, h0 - root.GetResource(Game.Economy.ResourceType.Human));
                exec.EnergySpent = Mathf.Max(0, e0 - root.GetResource(Game.Economy.ResourceType.Energy));
                exec.MaterialsSpent = Mathf.Max(0, m0 - root.GetResource(Game.Economy.ResourceType.Materials));
                exec.TechSpent = Mathf.Max(0, t0 - root.GetResource(Game.Economy.ResourceType.Tech));

                iter++;
                // Debit the turn budget only for an action that actually EXECUTED (progressed or
                // mutated real state). A candidate that turned out to be a no-op is parked below,
                // not counted against the per-turn action limit. Generation attempts debit
                // GenerationAttemptsUsed directly from the execute paths.
                if (exec.Progressed || exec.StateChanged)
                    budget.RecordAction(
                        card: exec.Progressed && best.CountsAsSurplusCardPlay,
                        draw: exec.Progressed && best.CountsAsTerminalDraw,
                        generationAttempt: false);

                AiDebugLog.Write($"[AI][V2]   tempo[{iter - 1}] — WINNER {best.Kind} util {F(best.Utility)} eff {F(bestEff)} — {best.Label}"
                    + $"  => ok {(exec.Succeeded ? 1 : 0)} progressed {(exec.Progressed ? 1 : 0)} stateChanged {(exec.StateChanged ? 1 : 0)}"
                    + $" spent ap {exec.ApSpent} H/E/M/T {exec.HumanSpent}/{exec.EnergySpent}/{exec.MaterialsSpent}/{exec.TechSpent}"
                    + $" card {(exec.CardPlayed ? 1 : 0)} drawn {(exec.Drawn ? 1 : 0)} gen {(exec.Generated ? 1 : 0)} attach {(exec.Attached ? 1 : 0)}"
                    + (exec.FailReason != null ? $" fail={exec.FailReason}" : ""));

                if (exec.StateChanged || exec.Progressed)
                {
                    stateVersion++;
                    result.StateChanged |= exec.StateChanged;
                }
                if (!exec.Progressed)
                {
                    parkedAt[best.ActionKey] = stateVersion;
                    AiDebugLog.Write($"[AI][V2]   tempo — {best.Kind} did not complete; parked {best.ActionKey}@v{stateVersion}");
                }
                if (exec.Interrupt)
                {
                    stopReason = "Phase B delivered an operational residual — re-admit missions before more spending";
                    break;
                }
            }
            if (stopReason == null)
                stopReason = budget.TotalCapHit
                    ? $"turn tempo action budget {AiConfigV2.maxEndOfTurnTempoActionsPerTurn} reached"
                    : "local iteration guard";

            // §13 — the mandatory final line: it must be impossible to read "AP left, reservation
            // none, reason unknown" off the log.
            AiDebugLog.Write($"[AI][V2] strat.B/tempo — END: iters {iter}, turn budget total {budget.TotalTempoActionsUsed}/"
                + $"{AiConfigV2.maxEndOfTurnTempoActionsPerTurn} cards {budget.SurplusCardActionsUsed}/{AiConfigV2.maxSurplusActionsPerTurn}"
                + $" draws {budget.DrawActionsUsed}/{AiConfigV2.maxTerminalDrawsPerTurn} gen {budget.GenerationAttemptsUsed}/{AiConfigV2.maxGenerationActionsPerTurn}; "
                + $"cardsPlayed {result.CardsPlayed}, drawn {result.CardsDrawn}; ap {root.ActionPoints} "
                + $"(spendable {F(StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints))}), "
                + $"H/E/M/T {root.GetResource(Game.Economy.ResourceType.Human)}/{root.GetResource(Game.Economy.ResourceType.Energy)}/"
                + $"{root.GetResource(Game.Economy.ResourceType.Materials)}/{root.GetResource(Game.Economy.ResourceType.Tech)}; "
                + $"reservations [{StrategicResourceReservationLedger.DebugLine(player, ctx.TurnNumber)}]; stop={stopReason}");
        }

        // ---- tempo diagnostics / helpers ----------------------------------------------------
        private static string BudgetSummary(StrategicTempoBudget b) =>
            $"(budget total {b.TotalTempoActionsUsed}/{AiConfigV2.maxEndOfTurnTempoActionsPerTurn}, "
            + $"cards {b.SurplusCardActionsUsed}/{AiConfigV2.maxSurplusActionsPerTurn}, "
            + $"draws {b.DrawActionsUsed}/{AiConfigV2.maxTerminalDrawsPerTurn}, "
            + $"gen {b.GenerationAttemptsUsed}/{AiConfigV2.maxGenerationActionsPerTurn})";

        private static string ResCostStr(ResourceCost c)
        {
            if (c == null) return "-";
            if (c.human == 0 && c.energy == 0 && c.materials == 0 && c.tech == 0) return "0";
            return $"H{c.human} E{c.energy} M{c.materials} T{c.tech}";
        }

        // Per-iteration mandatory diagnostic: AP, per-resource total/reserved/spendable/runway-target/
        // expected-income/strategic-overstock (NOT a physical overflow — the game has no storage cap),
        // hand, deck, and the shared turn budget.
        private static void LogTempoIterationHeader(AiTurnContext ctx, PlayerRoot root, WorldSnapshot snap,
            PlayerSetupData player, AiHandData hand, StrategicTempoBudget budget, int iter, float spendableAp)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[AI][V2]   tempo[{iter}] T{ctx.TurnNumber} — ap {root.ActionPoints} spendable {F(spendableAp)}; ");
            float comfortable = Mathf.Max(1f, AiConfigV2.tempoHoldResourceComfortableStock);
            foreach (ResourceType rt in ResourceBundle.All)
            {
                int stock = root.GetResource(rt);
                float reserved = StrategicResourceReservationLedger.Active(
                    player, ctx.TurnNumber, StrategicResourceReservationLedger.Map(rt));
                float spendable = Mathf.Max(0f, stock - reserved);
                float incomeTarget = snap?.Economy?.IncomeTarget.Get(rt) ?? 0f;
                float nextIncome = snap?.Self != null ? snap.Self.PerTurnIncome.Get(rt) : 0f;
                float runwayTarget = Mathf.Max(comfortable, incomeTarget * AiConfigV2.tempoHoldOverstockRunwayHorizon);
                float overstock = Mathf.Max(0f, (stock + nextIncome) - runwayTarget);
                sb.Append($"{rt.ToString()[0]} {stock}(rsv {F(reserved)} sp {F(spendable)} runway {F(runwayTarget)} inc {F(nextIncome)} overstock {F(overstock)}) ");
            }
            sb.Append($"| hand {hand.Hand.Count}/{ctx.HandCapacity} deck {hand.RemainingDeckCount} ");
            sb.Append(BudgetSummary(budget));
            AiDebugLog.Write(sb.ToString());
        }

        // null => the candidate may be chosen; otherwise a short reason it is currently blocked.
        // Every cap is checked against the turn budget (spec §P0.4), not a per-call local.
        private static string TempoBlockReason(TempoCandidate c, float spendableAp, StrategicTempoBudget budget,
            Dictionary<string, int> parkedAt, int stateVersion, PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            if (parkedAt.TryGetValue(c.ActionKey, out int v) && v == stateVersion)
                return $"parked@v{v}";
            if (c.CountsAsSurplusCardPlay && budget.CardCapHit) return "surplus card-play budget";
            if (c.CountsAsTerminalDraw && budget.DrawCapHit) return "draw budget";
            if (c.ConsumesGeneration && budget.GenerationCapHit) return "generation budget";
            if (c.ApCost > spendableAp + AiConfigV2.allocatorSliceEpsilon)
                return $"spendable AP ({F(c.ApCost)} > {F(spendableAp)})";
            if (!FitsSpendableResources(player, root, ctx, c.ResCost))
                return "spendable resources";
            return null;
        }

        // ---- tempo candidate model ------------------------------------------------------------
        private enum TempoKind { PlayMat, PlayNonCombat, Draw, MaintenanceSpend, PressureSpend, Hold, EndTurn }

        private static bool IsSpend(TempoKind k) =>
            k == TempoKind.PlayMat || k == TempoKind.PlayNonCombat || k == TempoKind.Draw
            || k == TempoKind.MaintenanceSpend || k == TempoKind.PressureSpend;

        private sealed class TempoCandidate
        {
            public TempoKind Kind;
            public string ActionKey;
            public float Utility;
            public float ApCost;              // spec §6 — must fit SPENDABLE (not raw) AP
            public ResourceCost ResCost;      // spec §6 — full persistent-resource cost vector (null = none)
            public bool ConsumesGeneration;   // spec §P0 — shared maxGenerationActionsPerTurn budget
            public bool CountsAsSurplusCardPlay; // spec §P0 — MGR-01 maxSurplusActionsPerTurn sub-cap
            public bool CountsAsTerminalDraw;    // spec §P0 — maxTerminalDrawsPerTurn sub-cap
            public string Label;
            public string DrawDiag;   // Draw only — preformatted valuation breakdown for the log
            public MatSurplusDecision Mat;
            public NonCombatCardPlayer.NonCombatPlay Nc;
            public StrategicPressurePlan Pressure;
            public StrategicSpendCandidate Spend;   // non-card strategic spend — executed verbatim
        }

        // spec §P1.7 — one structured result for EVERY tempo action (planning/execution parity,
        // diagnostics, retry-loop protection). Resource-spent fields are measured by the arbiter
        // around the call; the execute paths own the semantic flags.
        private struct TempoExecutionResult
        {
            public bool Succeeded;
            public bool StateChanged;
            public bool Progressed;
            public bool Interrupt;
            public float ApSpent, HumanSpent, EnergySpent, MaterialsSpent, TechSpent;
            public bool CardPlayed, Drawn, GenerationAttempted, Generated, Attached;
            public string FailReason;
        }

        private static List<TempoCandidate> BuildTempoCandidates(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            StrategicPhaseResult result, IReadOnlyList<ReconObjective> reconObjectives, float spendableAp,
            StrategicTempoBudget budget, bool verbose)
        {
            var list = new List<TempoCandidate>();

            // PlayCard — materialization lane. Utility = StrategicCardEvaluator decision score, verbatim.
            MatSurplusDecision mat = ComputeMatDecision(snap, player, root, hand, ctx, commitments,
                result, reconObjectives);
            if (mat.Admissible && mat.Plan != null)
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PlayMat, Mat = mat, Utility = mat.Utility,
                    ApCost = mat.Plan.ApCost, ResCost = mat.Plan.ResCost,
                    ConsumesGeneration = mat.Plan.Generation != null,
                    CountsAsSurplusCardPlay = true,
                    ActionKey = "mat:" + mat.Plan.StableKey,
                    Label = $"{mat.Plan.Kind} {AiCardLog.Plan(mat.Plan)}"
                        + (mat.Residual != null ? $" (residual {mat.Residual.Capability})" : ""),
                });
            else if (mat.DeferLog != null && verbose)
                AiDebugLog.Write(mat.DeferLog + " [tempo: not a candidate]");

            // PlayCard — non-combat lane (Aviation / Base / Facility / standalone Equipment).
            // Utility = StrategicCardEvaluator.ScoreNonCombat NetScore, verbatim (via BestPlay.Score).
            NonCombatCardPlayer.NonCombatPlay nc = NonCombatCardPlayer.BestPlay(
                snap, player, root, hand, ctx, out _, null, result.Reservation);
            TempoCandidate ncCand = null;
            if (nc != null)
            {
                // §P1.4 — a GENERATED non-combat candidate still owes the Challenge's ResourceCost
                // pre-mint. EffectivePlayResourceCost of the temporary stand-in is null after a
                // successful mint, which is wrong at arbitration time. Use the generation cost.
                ResourceCost ncResCost = nc.Generation != null
                    ? nc.Generation.GenerationResourceCost
                    : (nc.Card != null ? nc.Card.EffectivePlayResourceCost : null);
                ncCand = new TempoCandidate
                {
                    Kind = TempoKind.PlayNonCombat, Nc = nc, Utility = nc.Score,
                    ApCost = nc.Card != null ? nc.Card.EffectivePlayApCost : 0f,
                    ResCost = ncResCost,
                    ConsumesGeneration = nc.Generation != null,
                    CountsAsSurplusCardPlay = true,
                    ActionKey = $"nc:{nc.Kind}:{nc.Explain}",
                    Label = $"{nc.Kind} {nc.Explain}",
                };
                list.Add(ncCand);
            }

            // §P0.1 — the only card alternatives that suppress Draw are ones actually SELECTABLE
            // right now: not over the surplus card-play budget, AP + resources spendable. A card
            // blocked by the budget / affordability / placement must not make Draw look worthless.
            TempoCandidate matCand = list.FirstOrDefault(c => c.Kind == TempoKind.PlayMat);
            bool CardSelectableNow(TempoCandidate c) => c != null && !budget.CardCapHit
                && c.ApCost <= spendableAp + AiConfigV2.allocatorSliceEpsilon
                && FitsSpendableResources(player, root, ctx, c.ResCost);
            float bestSelectablePlay = Mathf.Max(
                CardSelectableNow(matCand) ? matCand.Utility : 0f,
                CardSelectableNow(ncCand) ? ncCand.Utility : 0f);

            // DrawCard — a real scored peer (spec §1), NOT penalised for holding H/E/M/T (costs AP only).
            if (AiConfigV2.surplusAllowDraw && CardDrawExecutor.CanCycle(root, hand, ctx)
                && spendableAp + AiConfigV2.allocatorSliceEpsilon >= ctx.DrawApCost)
            {
                float drawU = DrawCandidateUtility(snap, hand, ctx, bestSelectablePlay, out string drawDiag);
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.Draw, Utility = drawU, ApCost = ctx.DrawApCost, ActionKey = "draw",
                    CountsAsTerminalDraw = true, DrawDiag = drawDiag,
                    Label = $"cycle 1 card ({ctx.DrawApCost} AP), hand {hand.Hand.Count}/{ctx.HandCapacity}",
                });
            }

            // ExistingStrategicSpendAction — genuinely NON-CARD strategic actions only (Base/Citadel
            // slot-capacity upgrade). Facility / Equipment / generation are ordinary PlayCard
            // candidates above (one StrategicCardEvaluator, spec §5). Every eligible non-card spend
            // is its own candidate — no hidden category priority chain (spec §3).
            foreach (StrategicSpendCandidate sp in StrategicMaintenancePolicy.EnumerateCandidates(player, root, hand, ctx))
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.MaintenanceSpend, Utility = sp.Utility, ApCost = sp.ApCost,
                    ResCost = sp.ResCost, Spend = sp,
                    ActionKey = "maint:" + sp.StableKey, Label = sp.Label,
                });

            StrategicPressurePlan pressure = StrategicPressureAdvance.BuildPlan(player, root, hand, ctx, commitments);
            if (pressure != null && pressure.Army != null)
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PressureSpend, Pressure = pressure,
                    Utility = AiConfigV2.tempoPressureAdvanceValue,
                    ApCost = pressure.Army.HasActivatedThisTurn ? 0f : pressure.Army.ActivationApCost,
                    ActionKey = "pressure:" + pressure.Army.Id,
                    Label = $"advance army #{pressure.Army.Id} toward known enemy Citadel "
                        + $"({pressure.TargetHex.Q},{pressure.TargetHex.R})",
                });

            // HoldResources — the value of NOT spending. AP is lost at EndTurn so holding it is ~0;
            // the loose persistent-resource pool is worth holding only when the economy is fragile.
            // (Per-card hold value is already inside every PlayCard NetScore — spec §5.)
            list.Add(new TempoCandidate
            {
                Kind = TempoKind.Hold, ActionKey = "hold",
                Utility = HoldResourcesUtility(root, snap),
                Label = "keep unspent resources for future turns",
            });
            list.Add(new TempoCandidate
            {
                Kind = TempoKind.EndTurn, ActionKey = "endturn", Utility = 0f, Label = "end the turn",
            });
            return list;
        }

        // spec §1/§P0.1/§P1.6 — expected value of converting stranded AP into a fresh card option,
        // in the same [~0..5] band the PlayCard candidates use. Terms:
        //   · expectedDeckValue  = floor + normalised mean remaining-deck STRATEGIC card value
        //                          (combat power + generic role coverage + equipment/infra profile),
        //                          tapered when the deck is nearly empty;
        //   · fill factor         (softened — a single free slot is still a legal, ~0.70 draw);
        //   · last-slot block risk (softened to a small penalty);
        //   · AP opportunity cost;
        //   · handQualityPenalty  = weight * the best play SELECTABLE RIGHT NOW (0 if every card
        //                          alternative is blocked by budget / affordability / placement).
        private static float DrawCandidateUtility(WorldSnapshot snap, AiHandData hand, AiTurnContext ctx,
            float bestSelectablePlay, out string diag)
        {
            int freeSlots = Mathf.Max(0, ctx.HandCapacity - hand.Hand.Count);
            float fill = Mathf.Clamp(AiConfigV2.tempoDrawFillFloor
                + AiConfigV2.tempoDrawFillPerSlot * freeSlots, 0f, 1f);

            var deck = hand.RemainingDeck?.Where(d => d != null).ToList();
            float deckMean = 0f;
            if (deck != null && deck.Count > 0)
            {
                float sum = 0f;
                foreach (CardDefinition d in deck)
                    sum += GenericStrategicCardValue(d);
                deckMean = sum / deck.Count;
            }
            float deckValue = Mathf.Clamp01(deckMean / Mathf.Max(1f, AiConfigV2.tempoDrawDeckValueNorm));
            float thinTaper = deck == null ? 0f
                : Mathf.Clamp01(deck.Count / Mathf.Max(1f, AiConfigV2.tempoDrawThinDeckTaperCards));
            float expectedDeckValue =
                (AiConfigV2.tempoDrawBaseValue + AiConfigV2.tempoDrawDeckValueWeight * deckValue) * thinTaper;

            float blockRisk = freeSlots <= 1 ? AiConfigV2.tempoDrawFutureBlockPenalty : 0f;
            float apOpp = AiConfigV2.tempoDrawApOpportunityWeight * ctx.DrawApCost;
            float handQualityPenalty = AiConfigV2.tempoDrawHandActionableWeight * Mathf.Max(0f, bestSelectablePlay);

            float u = expectedDeckValue * fill - blockRisk - apOpp - handQualityPenalty;
            diag = $"expDeckVal {F(expectedDeckValue)} (mean {F(deckMean)} taper {F(thinTaper)}) freeSlots {freeSlots} "
                + $"fill {F(fill)} blockRisk {F(blockRisk)} apOpp {F(apOpp)} handQualPen {F(handQualityPenalty)} "
                + $"(selectablePlay {F(bestSelectablePlay)}) => draw {F(u)}";
            return u;
        }

        // spec §P1.6 — a lightweight GENERIC strategic value for an unseen deck card (the concrete
        // card is not drawn yet, so this is not a second StrategicCardEvaluator). Combat body power
        // + one bump per generic strategic role the card's granted abilities cover (AoE / Regen /
        // Aura / Summon / … via StrategicEffectRegistry), plus a flat profile for the non-combat
        // card families.
        private static float GenericStrategicCardValue(CardDefinition d)
        {
            if (d == null) return 0f;
            switch (d.cardType)
            {
                case CardType.Equipment:
                    return AiConfigV2.tempoDrawEquipmentValue;
                case CardType.Base:
                case CardType.Facility:
                    return AiConfigV2.tempoDrawInfraValue;
                case CardType.Unit:
                case CardType.Hero:
                default:
                {
                    float v = Mathf.Max(0f, AiPower.ToPowerUnit(d).BasePower);
                    if (d.grantedAbilities != null && d.grantedAbilities.Count > 0)
                    {
                        int roles = StrategicEffectRegistry
                            .Roles(d.grantedAbilities, Mathf.Max(1, d.moveMax)).Distinct().Count();
                        v += roles * AiConfigV2.tempoDrawEffectRoleValue;
                    }
                    return v;
                }
            }
        }

        // §P0 (round 4) — the H/E/M/T RETENTION policy value. NOT a global stop gate: the arbiter
        // calls this ONLY for a non-card spend, passing that spend's own ResourceCost as
        // `onlyConsumed` so the value covers just the resources it burns (an AP-only action never
        // consults this; PlayCard's retention is StrategicCardEvaluator's job). Passing null returns
        // the whole-pool value — used only for the diagnostic line.
        //   base = (fragility*fragilityWeight + scarcity*scarcityWeight) * scale
        //          where scarcity = 1 - min over the in-scope resources of (stock / comfortable)
        //   - Σ STRATEGIC OVERSTOCK relief: the game has NO hard resource cap so nothing is
        //     physically lost; a resource far above its runway need (runwayTarget = max(comfortable,
        //     IncomeTarget[r] * overstockRunwayHorizon)) is just worth less to hoard. overstock =
        //     max(0, (stock + PerTurnIncome[r]) - runwayTarget); summed, floored at 0 per resource.
        private static float HoldResourcesUtility(PlayerRoot root, WorldSnapshot snap, ResourceCost onlyConsumed = null)
        {
            if (root == null)
                return 0f;

            float eco = snap?.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;
            float fragility = 1f - eco;
            float comfortable = Mathf.Max(1f, AiConfigV2.tempoHoldResourceComfortableStock);

            float minStockNorm = 1f;
            float overstockRelief = 0f;
            bool anyInScope = false;
            foreach (ResourceType rt in ResourceBundle.All)
            {
                if (onlyConsumed != null && onlyConsumed.Get(rt) <= 0)
                    continue;
                anyInScope = true;
                int stock = root.GetResource(rt);
                minStockNorm = Mathf.Min(minStockNorm, Mathf.Clamp01(stock / comfortable));

                float incomeTarget = snap?.Economy?.IncomeTarget.Get(rt) ?? 0f;
                float nextIncome = snap?.Self != null ? snap.Self.PerTurnIncome.Get(rt) : 0f;
                float runwayTarget = Mathf.Max(comfortable, incomeTarget * AiConfigV2.tempoHoldOverstockRunwayHorizon);
                float overstock = Mathf.Max(0f, (stock + nextIncome) - runwayTarget);
                overstockRelief += overstock * AiConfigV2.tempoHoldOverstockReliefWeight;
            }
            if (!anyInScope)
                return 0f;

            float scarcity = 1f - minStockNorm;
            float u = (fragility * AiConfigV2.tempoHoldFragilityWeight
                       + scarcity * AiConfigV2.tempoHoldScarcityWeight)
                      * AiConfigV2.tempoHoldPersistentResourceValueScale;
            u -= Mathf.Min(AiConfigV2.tempoHoldOverstockReliefCap, overstockRelief);
            return Mathf.Clamp(u,
                -AiConfigV2.tempoHoldOverstockReliefCap, AiConfigV2.tempoHoldPersistentResourceValueCap);
        }

        // spec §6 — a spend candidate must fit SPENDABLE persistent resources, not just raw stock:
        // the strategic reservation ledger AND the legacy recon-air reservation are both netted out
        // so the same resource is never promised to two owners. Also the canonical resource-
        // affordability probe reused by StrategicReactionPass (spec round 5 §3/§4).
        // round 6 §P1 — `excludeReason` drops the caller's OWN reservations from the strategic
        // spendable, so a re-probe of the very reaction that placed a hold doesn't fail against it.
        internal static bool FitsSpendableResources(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceCost cost, StrategicReservationReason? excludeReason = null)
        {
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = cost.Get(t);
                if (need <= 0)
                    continue;
                StrategicReservedResource srr = StrategicResourceReservationLedger.Map(t);
                float strategic = excludeReason == null
                    ? StrategicResourceReservationLedger.Spendable(player, ctx.TurnNumber, srr, root.GetResource(t))
                    : StrategicResourceReservationLedger.SpendableExcluding(
                        player, ctx.TurnNumber, srr, root.GetResource(t), excludeReason.Value);
                float legacy = Mathf.Max(0f, Game.Ai.AiResourceReservation.Available(root, player, t));
                if (Mathf.Min(strategic, legacy) + AiConfigV2.allocatorSliceEpsilon < need)
                    return false;
            }
            return true;
        }

        // Execute one materialization-surplus chain, mirroring the old inline Phase-B path
        // (finalization / residual bookkeeping / capability-changed interrupt). Returns the
        // refreshed snapshot; `exec` drives the arbiter's park / rebuild / stop logic (spec §P1.7).
        private static WorldSnapshot ExecuteMatSurplus(MatSurplusDecision mat, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            ActorCommitments commitments, StrategicPhaseResult result, ref TempoExecutionResult exec)
        {
            MaterializationPlan plan = mat.Plan;
            AxisDemand residual = mat.Residual;
            CapabilityInventory inv = mat.Inv;

            var armyIdsBefore = new HashSet<int>(snap.Self?.Armies?
                .Where(a => a != null).Select(a => a.ArmyId) ?? Enumerable.Empty<int>());
            MaterializationResult play = MaterializationExecutor.Execute(snap, player, root, hand, ctx, plan, commitments);
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
                exec.GenerationAttempted = true;
            }
            exec.Generated |= play.Generated;
            exec.Attached |= play.Attached;
            if (play.StateChanged) { exec.StateChanged = true; result.StateChanged = true; }

            if (!play.Deployed)
            {
                exec.FailReason = play.FailReason;
                AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"chain did not deploy ({play.FailReason})");
                return play.StateChanged
                    ? WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx) : snap;
            }
            result.CardsPlayed++;
            exec.Succeeded = true; exec.Progressed = true; exec.CardPlayed = true;

            snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            CapabilityInventory afterInv = CapabilityInventory.Build(snap, player, commitments);
            float delivered = 0f;
            bool operationalResidual = residual != null && FinalizeOperationalDelivery(
                player, ctx, snap, plan, residual, inv, afterInv, armyIdsBefore, out delivered);
            if (operationalResidual)
            {
                residual.DesiredAmount = Mathf.Max(0f, residual.DesiredAmount - delivered);
                if (residual.DesiredAmount <= AiConfigV2.allocatorSliceEpsilon)
                    result.Reservation.UnresolvedDemands.Remove(residual);
                result.CapabilityDeliveries++;
            }
            AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                + $"util {F(mat.Utility)} (ap {F(play.ApSpent)}, {plan.Deploy.Kind}, delivered {F(delivered)}, {plan.StableKey})");

            if (operationalResidual)
            {
                StrategicInterruptRegistry.MarkCapabilityChanged(player, ctx.TurnNumber, hand);
                AiDebugLog.Write($"[AI][V2] strategic interrupt — Phase B delivered operational "
                    + $"{residual.Capability}; re-admit missions before further surplus spending");
                exec.Interrupt = true;
            }
            return snap;
        }

        private static WorldSnapshot ExecuteNonCombatSurplus(NonCombatCardPlayer.NonCombatPlay nc,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            StrategicPhaseResult result, ref TempoExecutionResult exec)
        {
            result.MaterializationAttempts++;
            NonCombatCardPlayer.NonCombatExecuteResult ncRes =
                NonCombatCardPlayer.Execute(nc, snap, player, root, hand, ctx);
            if (ncRes.StateChanged) { exec.StateChanged = true; result.StateChanged = true; }
            if (ncRes.GenerationAttempted)
            {
                result.Reservation.RecordGenerationAttempt(nc.Generation, null);
                StrategicTempoBudget.RecordGenerationAttempt(player, ctx.TurnNumber);
                exec.GenerationAttempted = true;
                result.GeneratedCardAttempts++;
                if (ncRes.Generated) result.GeneratedCardsSucceeded++;
            }
            exec.Generated |= ncRes.Generated;
            if (!ncRes.Played)
            {
                exec.FailReason = ncRes.FailReason;
                AiDebugLog.Write($"[AI][V2]   strat.B non-combat — {nc.Kind} {nc.Explain} "
                    + $"did not play ({ncRes.FailReason}{(ncRes.Generated ? "; generated card kept in hand" : "")})");
                return ncRes.StateChanged
                    ? WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx) : snap;
            }
            result.MaterializationsSucceeded++;
            result.CardsPlayed++;
            exec.Succeeded = true; exec.Progressed = true; exec.CardPlayed = true;
            if (nc.Kind == NonCombatCardPlayer.PlayKind.Base || nc.Kind == NonCombatCardPlayer.PlayKind.Facility)
            {
                result.InfrastructureAttempts++;
                result.InfrastructureBuilt++;
            }
            else if (nc.Kind == NonCombatCardPlayer.PlayKind.Equipment)
            {
                result.EquipmentAssignmentAttempts++;
                result.EquipmentAssignmentsSucceeded++;
                exec.Attached = true;
            }
            AiDebugLog.Write($"[AI][V2]   strat.B non-combat — played {nc.Kind} {nc.Explain} (ap {F(ncRes.ApSpent)})");
            return WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
        }

        // AI-MGR-01 review-r4 finding 9a — the materialization-surplus lane's per-iteration decision.
        private struct MatSurplusDecision
        {
            public bool Admissible;
            public MaterializationPlan Plan;
            public float Utility;
            public AxisDemand Residual;      // non-null => operational strategic residual
            public CapabilityInventory Inv;
            public string DeferLog;
        }

        private static MatSurplusDecision ComputeMatDecision(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            StrategicPhaseResult result, IReadOnlyList<ReconObjective> reconObjectives)
        {
            var dec = new MatSurplusDecision
            {
                Inv = CapabilityInventory.Build(snap, player, commitments),
            };
            (MaterializationPlan plan, float utility)? pick = MaterializationCandidateBuilder.BestSurplus(
                snap, player, root, hand, ctx, dec.Inv, commitments, result.Reservation);
            if (pick == null)
                return dec;

            MaterializationPlan plan = pick.Value.plan;
            AxisDemand matchedResidual = result.Reservation.BestUnresolvedDemandFor(plan);
            AxisDemand residual = matchedResidual != null && CanDeliverResidualOperationally(plan, matchedResidual)
                ? matchedResidual : null;
            SurplusAdmission admission = SurplusAdmissionPolicy.Evaluate(root, player, plan);

            if (matchedResidual != null && residual == null)
                AiDebugLog.Write($"[AI][V2]   strat.B — residual bypass denied for {plan.StableKey}: "
                    + $"{plan.Deploy.Kind} cannot operationally deliver {matchedResidual.Capability}; "
                    + "evaluate as generic surplus");

            if (matchedResidual != null && residual == null
                && matchedResidual.Capability == CapabilityKind.Hero && PlanBaseIsHeroCard(plan))
            {
                dec.DeferLog = $"[AI][V2]   strat.B — hold {plan.StableKey}: hero card matches "
                    + $"unresolved {matchedResidual} but no placement delivers it; keep in hand";
                return dec;
            }

            // §P1 anti-grind — a strong-garrison generic deposit with nothing threatened must clear
            // a much higher bar (satMult). This is STRUCTURAL, not the ordinary utility floor: it
            // still gates the candidate even under stranded-AP tempo pressure so the garrison is not
            // ground from 6 to 40+ power with threats=0.
            float satMult = GarrisonSaturationThresholdMult(snap, plan, residual);
            float effThreshold = admission.EffectiveThreshold * satMult;

            if (residual == null && GenericSurplusWouldChurn(player, plan))
            {
                dec.DeferLog = $"[AI][V2]   strat.B — hold {plan.StableKey} {AiCardLog.Plan(plan)}: "
                    + "generic surplus would found a lone-member army housekeeping folds the same turn";
                return dec;
            }
            if (residual == null && ScoutSurplusPortfolioSaturated(player, plan, snap, reconObjectives))
            {
                dec.DeferLog = $"[AI][V2]   strat.B — hold {plan.StableKey} {AiCardLog.Plan(plan)}: "
                    + "generic surplus would add a scout beyond the physical portfolio "
                    + $"(desired concurrency + warm spare, hard cap {ReconConcurrencyPolicy.HardCap})";
                return dec;
            }
            if (residual == null && satMult > 1f && plan.Score < effThreshold)
            {
                dec.DeferLog = $"[AI][V2]   strat.B — defer {plan.StableKey} {AiCardLog.Plan(plan)} "
                    + $"score {F(plan.Score)} < garrison-saturated bar {F(effThreshold)} (x{F(satMult)})";
                return dec;
            }

            dec.Admissible = true;
            dec.Plan = plan;
            // pick.Value.utility is ALREADY the global decision score (NetScore + operational-residual
            // urgency), computed once in MaterializationCandidateBuilder.BestSurplus. Not re-adjusted
            // here — the arbiter compares it against Hold/EndTurn as-is (spec §5).
            dec.Utility = pick.Value.utility;
            dec.Residual = residual;
            return dec;
        }

        internal static bool ReservesOkAfterChain(PlayerRoot root, MaterializationPlan plan,
            PlayerSetupData player = null)
        {
            if (root == null || plan == null)
                return false;
            if (root.ActionPoints - plan.ApCost < 0f)
                return false;

            ResourceCost cost = plan.ResCost;
            if (cost == null)
                return true;

            return Has(ResourceType.Human, cost.human)
                && Has(ResourceType.Energy, cost.energy)
                && Has(ResourceType.Materials, cost.materials)
                && Has(ResourceType.Tech, cost.tech);

            // AI-RECON-01 — go through AiResourceReservation.Available so the recon-air reservation's
            // protected Energy is netted out here too: Phase A must not commit a materialisation
            // chain that spends Energy a planned-but-unlaunched recon sortie is holding.
            bool Has(ResourceType type, int spend)
            {
                float available = Mathf.Max(0f, Game.Ai.AiResourceReservation.Available(root, player, type));
                return available >= Mathf.Max(0, spend);
            }
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
