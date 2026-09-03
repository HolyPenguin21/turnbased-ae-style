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
                    result.Reservation.RecordGenerationAttempt(plan.Generation, play);
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

            // --- §7 reaction reservation: reserve a REAL BOUNDED AP requirement for a REAL
            //     actionable owner, or reserve nothing. Never hold back the whole pool for
            //     "there is some pending interrupt".
            StrategicReactionOpportunity reactionOpp =
                StrategicReactionPass.BuildReactionOpportunity(player, root, ctx);
            if (reactionOpp.IsActionable)
            {
                StrategicResourceReservationLedger.Upsert(player, ctx.TurnNumber,
                    new StrategicResourceReservation
                    {
                        Owner = "StrategicReactionPass",
                        Reason = StrategicReservationReason.StrategicReactionPass,
                        Resource = StrategicReservedResource.ActionPoints,
                        Amount = reactionOpp.ApRequired,
                        ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                    });
                AiDebugLog.Write($"[AI][V2]   strat.B — reaction pending & actionable; reserve "
                    + $"{F(reactionOpp.ApRequired)} AP (owner=StrategicReactionPass exp=EndOfReaction), "
                    + $"spendable AP now {F(StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints))}. "
                    + "Tempo arbitration proceeds with the remainder.");
            }
            else
            {
                // spec §7 — an existing reaction reservation whose owner is no longer actionable is
                // released immediately (the same-turn re-arbitration path is HousekeepingManager).
                StrategicResourceReservationLedger.ReleaseByReason(player, ctx.TurnNumber,
                    StrategicReservationReason.StrategicReactionPass);
                if (StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber))
                    AiDebugLog.Write($"[AI][V2]   strat.B — pending invalidation but reaction pass not "
                        + $"actionable ({reactionOpp.Reason}); NOT reserving AP — tempo arbitration uses "
                        + "the full pool (spec §7)");
            }

            AiDebugLog.Write($"[AI][V2]   strat.B — {player.Nickname} hand {AiCardLog.Hand(hand)}");

            var rejectedForState = new HashSet<string>(System.StringComparer.Ordinal);
            int actions = 0;
            string stopReason = null;
            while (actions < AiConfigV2.maxEndOfTurnTempoActionsPerTurn)
            {
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                float spendableAp = StrategicResourceReservationLedger.SpendableAp(
                    player, ctx.TurnNumber, root.ActionPoints);

                var cands = BuildTempoCandidates(snap, player, root, hand, ctx, commitments, result,
                    reconObjectives, spendableAp, verbose: actions == 0);

                float holdU = cands.First(c => c.Kind == TempoKind.Hold).Utility;
                float endU = cands.First(c => c.Kind == TempoKind.EndTurn).Utility;
                float bar = Mathf.Max(AiConfigV2.tempoMinSpendUtility, Mathf.Max(holdU, endU));

                AiDebugLog.Write($"[AI][V2]   tempo[{actions}] — ap {root.ActionPoints} spendable {F(spendableAp)}"
                    + $" reservations [{StrategicResourceReservationLedger.DebugLine(player, ctx.TurnNumber)}]"
                    + $" hand {hand.Hand.Count}/{ctx.HandCapacity} | bar {F(bar)} (hold {F(holdU)} endTurn {F(endU)})");
                foreach (TempoCandidate c in cands.OrderByDescending(c => c.Utility).ThenBy(c => c.ActionKey, System.StringComparer.Ordinal))
                    AiDebugLog.Write($"[AI][V2]     cand {c.Kind} util {F(c.Utility)} key={c.ActionKey}"
                        + (rejectedForState.Contains(c.ActionKey) ? " [parked]" : "") + $" — {c.Label}");

                // spec §6 — a spend candidate must fit SPENDABLE AP (raw AP minus active
                // reservations), so a play never dips into AP the reaction pass is holding.
                TempoCandidate best = cands
                    .Where(c => IsSpend(c.Kind) && !rejectedForState.Contains(c.ActionKey)
                                && c.ApCost <= spendableAp + AiConfigV2.allocatorSliceEpsilon)
                    .OrderByDescending(c => c.Utility)
                    .ThenBy(c => c.ActionKey, System.StringComparer.Ordinal)
                    .FirstOrDefault();

                if (best == null || best.Utility < bar)
                {
                    stopReason = best == null
                        ? "no actionable spend candidate within spendable AP"
                        : $"max(Hold {F(holdU)}, EndTurn {F(endU)}) bar {F(bar)} >= best spend {best.Kind} {F(best.Utility)}";
                    break;
                }

                AiDebugLog.Write($"[AI][V2]   tempo[{actions}] — WIN {best.Kind} util {F(best.Utility)} — {best.Label}");

                bool changed = false, progressed = false, interrupt = false;
                switch (best.Kind)
                {
                    case TempoKind.PlayMat:
                        snap = ExecuteMatSurplus(best.Mat, snap, player, root, hand, ctx, commitments,
                            result, out changed, out progressed, out interrupt);
                        break;
                    case TempoKind.PlayNonCombat:
                        snap = ExecuteNonCombatSurplus(best.Nc, snap, player, root, hand, ctx,
                            result, out changed, out progressed);
                        break;
                    case TempoKind.Draw:
                    {
                        int apB = root.ActionPoints, hB = hand.Hand.Count;
                        if (CardDrawExecutor.TryCycle(root, hand, ctx))
                        {
                            changed = true; progressed = true; result.CardsDrawn++;
                            AiDebugLog.Write($"[AI][V2]   tempo — Draw: ap {apB}->{root.ActionPoints} hand {hB}->{hand.Hand.Count}");
                        }
                        break;
                    }
                    case TempoKind.MaintenanceSpend:
                        if (StrategicMaintenancePolicy.TryExecuteBest(snap, player, root, hand, ctx))
                        { changed = true; progressed = true; }
                        break;
                    case TempoKind.PressureSpend:
                    {
                        bool pc = false;
                        yield return StrategicPressureAdvance.Execute(player, root, ctx, best.Pressure, v => pc = v);
                        changed = pc; progressed = pc;
                        break;
                    }
                }

                actions++;
                if (changed) result.StateChanged = true;

                if (interrupt)
                {
                    stopReason = "Phase B delivered an operational residual — re-admit missions before more spending";
                    break;
                }
                // spec §2/§3 — a real state mutation destroys the whole candidate set (rebuilt next
                // iteration). A candidate that neither completed nor mutated state is parked so it
                // cannot be re-chosen until a later mutation invalidates the set. A candidate that
                // mutated something incidental but did NOT accomplish its goal is BOTH: the set is
                // rebuilt, and this one key is parked so we do not immediately retry the same failure.
                if (changed)
                    rejectedForState.Clear();
                if (!progressed)
                {
                    rejectedForState.Add(best.ActionKey);
                    AiDebugLog.Write($"[AI][V2]   tempo — {best.Kind} did not complete; parked {best.ActionKey}");
                }
            }
            if (stopReason == null)
                stopReason = $"hard action bound {AiConfigV2.maxEndOfTurnTempoActionsPerTurn} reached";

            // §13 — the mandatory final line: it must be impossible to read "AP left, reservation
            // none, reason unknown" off the log.
            AiDebugLog.Write($"[AI][V2] strat.B/tempo — END: actions {actions}, cardsPlayed {result.CardsPlayed}, "
                + $"drawn {result.CardsDrawn}; ap {root.ActionPoints} "
                + $"(spendable {F(StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints))}), "
                + $"H/E/M/T {root.GetResource(Game.Economy.ResourceType.Human)}/{root.GetResource(Game.Economy.ResourceType.Energy)}/"
                + $"{root.GetResource(Game.Economy.ResourceType.Materials)}/{root.GetResource(Game.Economy.ResourceType.Tech)}; "
                + $"reservations [{StrategicResourceReservationLedger.DebugLine(player, ctx.TurnNumber)}]; stop={stopReason}");
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
            public float ApCost;      // spec §6 — a spend candidate must fit SPENDABLE (not raw) AP
            public string Label;
            public MatSurplusDecision Mat;
            public NonCombatCardPlayer.NonCombatPlay Nc;
            public StrategicPressurePlan Pressure;
        }

        private static List<TempoCandidate> BuildTempoCandidates(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            StrategicPhaseResult result, IReadOnlyList<ReconObjective> reconObjectives, float spendableAp,
            bool verbose)
        {
            var list = new List<TempoCandidate>();

            // PlayCard — materialization lane. Utility = StrategicCardEvaluator decision score, verbatim.
            MatSurplusDecision mat = ComputeMatDecision(snap, player, root, hand, ctx, commitments,
                result, reconObjectives);
            if (mat.Admissible && mat.Plan != null)
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PlayMat, Mat = mat, Utility = mat.Utility,
                    ApCost = mat.Plan.ApCost,
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
            if (nc != null)
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.PlayNonCombat, Nc = nc, Utility = nc.Score,
                    ApCost = nc.Card != null ? nc.Card.EffectivePlayApCost : 0f,
                    ActionKey = $"nc:{nc.Kind}:{nc.Explain}",
                    Label = $"{nc.Kind} {nc.Explain}",
                });

            // DrawCard — a real scored alternative, not a terminal fallback (spec §1).
            if (AiConfigV2.surplusAllowDraw && CardDrawExecutor.CanCycle(root, hand, ctx)
                && spendableAp + AiConfigV2.allocatorSliceEpsilon >= ctx.DrawApCost)
            {
                float drawU = DrawCandidateUtility(snap, player, root, hand, ctx, commitments, result, mat, nc);
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.Draw, Utility = drawU, ApCost = ctx.DrawApCost, ActionKey = "draw",
                    Label = $"cycle 1 card ({ctx.DrawApCost} AP), hand {hand.Hand.Count}/{ctx.HandCapacity}",
                });
            }

            // ExistingStrategicSpendAction — internal facility / capacity upgrade / equipment /
            // standalone generation, and decisive structure pressure. Both fold into the SAME
            // arbitration instead of running after the loop.
            if (StrategicMaintenancePolicy.DescribeBest(player, root, hand, ctx,
                    out string mLabel, out string mKey, out float mUtil, out float mApCost))
                list.Add(new TempoCandidate
                {
                    Kind = TempoKind.MaintenanceSpend, Utility = mUtil, ApCost = mApCost,
                    ActionKey = "maint:" + mKey, Label = mLabel,
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

        // Expected value of converting stranded AP into a fresh card option, in the same [~0..5]
        // NetScore band the PlayCard candidates use (spec §1 — Draw is a full peer). Terms:
        // base option value, hand-fill discount, near-full-hand block risk, AP opportunity cost,
        // and a discount when the hand already holds a play we are only deferring on a structural
        // guard (so we do not draw into an already-actionable hand).
        private static float DrawCandidateUtility(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, ActorCommitments commitments, StrategicPhaseResult result,
            MatSurplusDecision mat, NonCombatCardPlayer.NonCombatPlay nc)
        {
            int freeSlots = Mathf.Max(0, ctx.HandCapacity - hand.Hand.Count);
            float fill = Mathf.Clamp01(freeSlots / 3f);
            float u = AiConfigV2.tempoDrawBaseValue * fill;
            if (freeSlots <= 1)
                u -= AiConfigV2.tempoDrawFutureBlockPenalty;          // drawing into the last slot
            u -= AiConfigV2.tempoDrawApOpportunityWeight * ctx.DrawApCost;
            // Hand already actionable (a play exists, held only by a structural guard / low score)
            // -> a further draw is worth less.
            bool alreadyActionable = (mat.Plan != null) || (nc != null && nc.Score > 0f);
            if (alreadyActionable)
                u -= AiConfigV2.tempoDrawHandActionablePenalty;
            return u;
        }

        // spec §4 — the value of NOT spending the loose persistent stockpile (AP is never held; it
        // does not carry over). Rises with (a) a fragile economy and (b) how SCARCE the thinnest
        // persistent resource is. Capped so a genuinely strong play (NetScore well above the cap)
        // still wins, while a marginal one loses to keeping a scarce resource (Case B). Per-CARD
        // hold value is priced only inside the PlayCard NetScore — this is the loose pool (§5).
        private static float HoldResourcesUtility(PlayerRoot root, WorldSnapshot snap)
        {
            if (root == null)
                return 0f;
            int persistent = root.GetResource(Game.Economy.ResourceType.Human)
                + root.GetResource(Game.Economy.ResourceType.Energy)
                + root.GetResource(Game.Economy.ResourceType.Materials)
                + root.GetResource(Game.Economy.ResourceType.Tech);
            if (persistent <= 0)
                return 0f;

            float eco = snap?.Economy != null ? Mathf.Clamp01(snap.Economy.EconomicSecurity) : 0.5f;
            float fragility = 1f - eco;

            float minStockNorm = 1f;
            foreach (ResourceType rt in ResourceBundle.All)
                minStockNorm = Mathf.Min(minStockNorm,
                    Mathf.Clamp01(root.GetResource(rt) / Mathf.Max(1f, (float)AiConfigV2.tempoHoldResourceComfortableStock)));
            float scarcity = 1f - minStockNorm;   // 0 when every resource is comfortable

            float u = (fragility * AiConfigV2.tempoHoldFragilityWeight
                       + scarcity * AiConfigV2.tempoHoldScarcityWeight)
                      * AiConfigV2.tempoHoldPersistentResourceValueScale;
            return Mathf.Clamp(u, 0f, AiConfigV2.tempoHoldPersistentResourceValueCap);
        }

        // Execute one materialization-surplus chain, mirroring the old inline Phase-B path
        // (finalization / residual bookkeeping / capability-changed interrupt). Returns the
        // refreshed snapshot; out flags drive the arbiter's park / clear / stop logic.
        private static WorldSnapshot ExecuteMatSurplus(MatSurplusDecision mat, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            ActorCommitments commitments, StrategicPhaseResult result,
            out bool changed, out bool progressed, out bool interrupt)
        {
            changed = false; progressed = false; interrupt = false;
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
                result.Reservation.RecordGenerationAttempt(plan.Generation, play);
            if (play.StateChanged) { changed = true; result.StateChanged = true; }

            if (!play.Deployed)
            {
                AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"chain did not deploy ({play.FailReason})");
                return play.StateChanged
                    ? WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx) : snap;
            }
            result.CardsPlayed++;
            progressed = true;

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
                interrupt = true;
            }
            return snap;
        }

        private static WorldSnapshot ExecuteNonCombatSurplus(NonCombatCardPlayer.NonCombatPlay nc,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            StrategicPhaseResult result, out bool changed, out bool progressed)
        {
            changed = false; progressed = false;
            result.MaterializationAttempts++;
            NonCombatCardPlayer.NonCombatExecuteResult ncRes =
                NonCombatCardPlayer.Execute(nc, snap, player, root, hand, ctx);
            if (ncRes.StateChanged) { changed = true; result.StateChanged = true; }
            if (ncRes.GenerationAttempted)
            {
                result.Reservation.RecordGenerationAttempt(nc.Generation, null);
                result.GeneratedCardAttempts++;
                if (ncRes.Generated) result.GeneratedCardsSucceeded++;
            }
            if (!ncRes.Played)
            {
                AiDebugLog.Write($"[AI][V2]   strat.B non-combat — {nc.Kind} {nc.Explain} "
                    + $"did not play ({ncRes.FailReason}{(ncRes.Generated ? "; generated card kept in hand" : "")})");
                return ncRes.StateChanged
                    ? WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx) : snap;
            }
            result.MaterializationsSucceeded++;
            result.CardsPlayed++;
            progressed = true;
            if (nc.Kind == NonCombatCardPlayer.PlayKind.Base || nc.Kind == NonCombatCardPlayer.PlayKind.Facility)
            {
                result.InfrastructureAttempts++;
                result.InfrastructureBuilt++;
            }
            else if (nc.Kind == NonCombatCardPlayer.PlayKind.Equipment)
            {
                result.EquipmentAssignmentAttempts++;
                result.EquipmentAssignmentsSucceeded++;
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
