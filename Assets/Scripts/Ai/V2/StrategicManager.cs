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

        public static StrategicPhaseResult UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            MaterializationReservation carriedReservation,
            IReadOnlyList<ReconObjective> reconObjectives = null)
        {
            var result = new StrategicPhaseResult
            {
                Reservation = carriedReservation ?? new MaterializationReservation(),
            };
            if (player == null || root == null || hand == null || ctx == null)
                return result;

            // AI-MGR-02 §4 — only hold AP back for the bounded reaction pass when that pass will
            // ACTUALLY run and has actionable content. Otherwise the old blanket early-return left
            // (e.g.) 10 AP stranded because ReconOnly suppressed the pass. The hold is now an
            // EXPLICIT owner+reason StrategicResourceReservation, not a hidden "just return".
            if (ReactionPassWillReserve(player, ctx))
            {
                StrategicResourceReservationLedger.Reserve(player, ctx.TurnNumber,
                    new StrategicResourceReservation
                    {
                        Owner = "StrategicReactionPass",
                        Reason = StrategicReservationReason.StrategicReactionPass,
                        Resource = StrategicReservedResource.ActionPoints,
                        Amount = root.ActionPoints,
                        ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                    });
                AiDebugLog.Write($"[AI][V2]   strat.B — deferred: actionable strategic reaction pending; "
                    + $"explicit reservation {root.ActionPoints} AP (owner=StrategicReactionPass "
                    + $"expire=EndOfReaction), spendable now "
                    + $"{F(StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints))}");
                return result;
            }
            if (StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber))
                AiDebugLog.Write($"[AI][V2]   strat.B — pending strategic invalidation but the bounded "
                    + $"reaction pass will not run ("
                    + $"{(AiStrategyV2Scope.IsReconOnly ? "scope=ReconOnly" : "no actionable content")}); "
                    + $"NOT preserving {root.ActionPoints} AP — continue to surplus + end-of-turn tempo (spec §4)");

            AiDebugLog.Write($"[AI][V2]   strat.B — {player.Nickname} hand {AiCardLog.Hand(hand)}");
            bool cleanStop = true;
            bool aviationPlayedInLoop = false;
            List<string> lastNcBlocked = null;

            // AI-MGR-01 review-r4 finding 9a — ONE per-iteration ranked pick across BOTH surplus
            // lanes. Each iteration builds the best materialization-surplus chain AND the best
            // non-combat play (Aviation / Base / Facility / standalone Equipment), scores them on
            // the SAME StrategicCardEvaluator NetScore band, and executes the higher one. An
            // operational strategic residual is must-do and always outranks a generic non-combat
            // play. The old "run the whole materialization loop, THEN the whole non-combat loop"
            // ordering (which let a 0.8 Unit foreclose a 2.5 Base on the last AP) is gone. The two
            // executors stay specialised; only the DECISION is unified. maxSurplusActionsPerTurn is
            // the whole-phase safety bound; ReactionPassWillReserve was already handled by the
            // early return above, so no per-iteration reaction gate is needed here.
            int surplusActionsUsed = 0;
            for (; surplusActionsUsed < AiConfigV2.maxSurplusActionsPerTurn; surplusActionsUsed++)
            {
                MatSurplusDecision mat = ComputeMatDecision(snap, player, root, hand, ctx,
                    commitments, result, reconObjectives);

                NonCombatCardPlayer.NonCombatPlay nc = NonCombatCardPlayer.BestPlay(
                    snap, player, root, hand, ctx, out lastNcBlocked, null, result.Reservation);
                bool ncAdmissible = nc != null && nc.Score >= AiConfigV2.surplusUtilityThreshold;

                bool doMat = mat.Admissible
                    && (!ncAdmissible || mat.Residual != null || mat.Utility >= nc.Score);

                if (!doMat && !ncAdmissible)
                {
                    if (mat.DeferLog != null)
                        AiDebugLog.Write(mat.DeferLog + "; stop Phase B surplus");
                    else if (nc != null)
                        AiDebugLog.Write($"[AI][V2]   strat.B — defer non-combat {nc.Kind} {nc.Explain} "
                            + $"score {F(nc.Score)} < threshold {F(AiConfigV2.surplusUtilityThreshold)}; stop");
                    break;
                }

                if (doMat)
                {
                    MaterializationPlan plan = mat.Plan;
                    AxisDemand residual = mat.Residual;
                    CapabilityInventory inv = mat.Inv;

                    if (residual != null)
                        AiDebugLog.Write($"[AI][V2]   strat.B — admit residual {residual} via "
                            + $"{plan.StableKey} {AiCardLog.Plan(plan)} util {F(mat.Utility)} "
                            + "(operational strategic residual outranks generic surplus)");
                    else
                        AiDebugLog.Write($"[AI][V2]   strat.B — admit {plan.StableKey} {AiCardLog.Plan(plan)} "
                            + $"util {F(mat.Utility)} (ranked pick: materialization {F(mat.Utility)} "
                            + $"vs non-combat {F(nc?.Score ?? 0f)})");

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

                    snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    CapabilityInventory afterInv = CapabilityInventory.Build(snap, player, commitments);
                    float delivered = 0f;
                    // §3 — Phase B runs the SAME finalization path as Phase A: an army it created or
                    // modified to satisfy a strategic residual gets the Housekeeping capability
                    // lease, so the later zero-AP structural pass can no longer fold that force away
                    // the same turn it was materialized.
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
                        + $"util {F(mat.Utility)} (ap {F(play.ApSpent)}, {plan.Deploy.Kind}, "
                        + $"delivered {F(delivered)}, {plan.StableKey})");

                    if (operationalResidual)
                    {
                        StrategicInterruptRegistry.MarkCapabilityChanged(player, ctx.TurnNumber, hand);
                        AiDebugLog.Write($"[AI][V2] strategic interrupt — Phase B delivered operational "
                            + $"{residual.Capability}; re-admit missions before further surplus spending");
                        cleanStop = false;
                        break;
                    }
                    continue;
                }

                // ---- non-combat lane (spec §5/§13) — through the same canonical gameplay APIs
                //      the human UI uses (BuildingPlayExecutor / AviationActions / EquipmentSystem).
                result.MaterializationAttempts++;
                bool ncOk = NonCombatCardPlayer.Execute(nc, snap, player, root, hand, ctx,
                    out float ncAp, out string ncFail);
                if (!ncOk)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B non-combat — {nc.Kind} {nc.Explain} "
                        + $"did not play ({ncFail}); stop");
                    cleanStop = false;
                    break;
                }
                result.MaterializationsSucceeded++;
                result.CardsPlayed++;
                result.StateChanged = true;
                if (nc.Generation != null)
                {
                    // finding 9b — a generated non-combat card consumed the turn's Challenge.
                    result.Reservation.RecordGenerationAttempt(nc.Generation, null);
                    result.GeneratedCardAttempts++;
                    result.GeneratedCardsSucceeded++;
                }
                if (nc.Kind == NonCombatCardPlayer.PlayKind.Base
                    || nc.Kind == NonCombatCardPlayer.PlayKind.Facility)
                {
                    result.InfrastructureAttempts++;
                    result.InfrastructureBuilt++;
                }
                else if (nc.Kind == NonCombatCardPlayer.PlayKind.Equipment)
                {
                    result.EquipmentAssignmentAttempts++;
                    result.EquipmentAssignmentsSucceeded++;
                }
                if (nc.Kind == NonCombatCardPlayer.PlayKind.Aviation)
                    aviationPlayedInLoop = true;
                AiDebugLog.Write($"[AI][V2]   strat.B non-combat — played {nc.Kind} {nc.Explain} "
                    + $"(ap {F(ncAp)}; ranked pick: non-combat {F(nc.Score)} vs materialization "
                    + $"{F(mat.Admissible ? mat.Utility : 0f)})");
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {result.CardsPlayed} surplus chain(s) played");
            if (lastNcBlocked != null && lastNcBlocked.Count > 0)
                AiDebugLog.Write($"[AI][V2]   strat.B non-combat — still blocked [{string.Join(", ", lastNcBlocked)}]");

            // §P0 — a DEDICATED final slot for a playable Aviation card the shared budget may not
            // have reached, so a stored aircraft (what makes AirRecon possible) is not starved by a
            // run of higher-scored Base/Facility. Still GATED on the evaluator score, and skipped if
            // an Aviation card already went out in the ranked loop this pass.
            if (cleanStop && !aviationPlayedInLoop && !ReactionPassWillReserve(player, ctx))
                snap = RunDedicatedAviationSlot(snap, player, root, hand, ctx, result);

            if (cleanStop && RunTerminalDraws(snap, player, root, hand, ctx, commitments, result, reconObjectives))
                result.StateChanged = true;
            return result;
        }

        // AI-MGR-01 review-r4 finding 9a — the materialization-surplus lane's per-iteration decision,
        // factored out so the unified Phase-B loop can rank it against the non-combat lane. Returns
        // either an admissible chain (with its utility + any operational strategic residual) or a
        // not-admissible verdict carrying the one-line defer reason to log if the whole phase stops.
        private struct MatSurplusDecision
        {
            public bool Admissible;
            public MaterializationPlan Plan;
            public float Utility;
            public AxisDemand Residual;      // non-null => operational strategic residual, must-do
            public CapabilityInventory Inv;
            public string DeferLog;         // set when !Admissible and there is a reason worth logging
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

            // §2 — a Hero card that still matches an unresolved Hero demand must never be spent as
            // generic surplus through a placement that delivers 0 Hero. Belt-and-braces stop: leave
            // it in hand.
            if (matchedResidual != null && residual == null
                && matchedResidual.Capability == CapabilityKind.Hero && PlanBaseIsHeroCard(plan))
            {
                dec.DeferLog = $"[AI][V2]   strat.B — hold {plan.StableKey}: hero card matches "
                    + $"unresolved {matchedResidual} but no placement delivers it; keep in hand";
                return dec;
            }

            // §P1 — a strong garrison stack with nothing threatening an asset makes a generic
            // garrison-only card clear a much higher bar (stranded AP converts to draws instead).
            float satMult = GarrisonSaturationThresholdMult(snap, plan, residual);
            float effThreshold = admission.EffectiveThreshold * satMult;

            // §P1 — a generic surplus card must not FOUND a lone-member army on a hex that already
            // holds our garrison (Housekeeping folds it the same turn). A forward outpost away from
            // any base of ours is still fine.
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

            if (residual == null && pick.Value.utility < effThreshold)
            {
                dec.DeferLog = $"[AI][V2]   strat.B — defer {plan.StableKey} {AiCardLog.Plan(plan)} "
                    + $"util {F(pick.Value.utility)} < threshold {F(effThreshold)} "
                    + $"(base {F(admission.BaseThreshold)}, apSlack {F(admission.ApSlack)}, "
                    + $"resSlack {F(admission.ResourceSlackFactor)}"
                    + $"{(satMult > 1f ? $", garrisonSaturatedx{F(satMult)}" : "")})";
                return dec;
            }

            dec.Admissible = true;
            dec.Plan = plan;
            dec.Utility = pick.Value.utility;
            dec.Residual = residual;
            return dec;
        }

        // AI-MGR-01 review-r4 finding 9a — one gated attempt at a stored Aviation card the unified
        // ranked loop did not reach (and did not already play). Same evaluator-score gate.
        private static WorldSnapshot RunDedicatedAviationSlot(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, StrategicPhaseResult result)
        {
            NonCombatCardPlayer.NonCombatPlay avia = NonCombatCardPlayer.BestPlay(
                snap, player, root, hand, ctx, out _, NonCombatCardPlayer.PlayKind.Aviation,
                result.Reservation);
            if (avia == null || avia.Score < AiConfigV2.surplusUtilityThreshold)
                return snap;

            result.MaterializationAttempts++;
            if (NonCombatCardPlayer.Execute(avia, snap, player, root, hand, ctx,
                out float aviaAp, out string aviaFail))
            {
                result.MaterializationsSucceeded++;
                result.CardsPlayed++;
                result.StateChanged = true;
                if (avia.Generation != null)
                {
                    result.Reservation.RecordGenerationAttempt(avia.Generation, null);
                    result.GeneratedCardAttempts++;
                    result.GeneratedCardsSucceeded++;
                }
                AiDebugLog.Write($"[AI][V2]   strat.B non-combat — played Aviation {avia.Explain} "
                    + $"(ap {F(aviaAp)}, dedicated aviation slot)");
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }
            else
            {
                AiDebugLog.Write($"[AI][V2]   strat.B non-combat — dedicated aviation slot: "
                    + $"{avia.Explain} did not play ({aviaFail})");
            }
            return snap;
        }

        private static bool RunTerminalDraws(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, ActorCommitments commitments, StrategicPhaseResult result,
            IReadOnlyList<ReconObjective> reconObjectives = null)
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

                string actionable = null;
                if (pick != null)
                {
                    AxisDemand matchedResidual = result.Reservation.BestUnresolvedDemandFor(pick.Value.plan);
                    AxisDemand residual = matchedResidual != null
                        && CanDeliverResidualOperationally(pick.Value.plan, matchedResidual) ? matchedResidual : null;
                    SurplusAdmission adm = SurplusAdmissionPolicy.Evaluate(root, player, pick.Value.plan);
                    float termThreshold = adm.EffectiveThreshold
                        * GarrisonSaturationThresholdMult(snap, pick.Value.plan, residual);
                    if (residual == null && (GenericSurplusWouldChurn(player, pick.Value.plan)
                        || ScoutSurplusPortfolioSaturated(player, pick.Value.plan, snap, reconObjectives)))
                        termThreshold = float.MaxValue;
                    if (residual != null || pick.Value.utility >= termThreshold)
                        actionable = residual != null
                            ? $"an operational residual demand ({pick.Value.plan.StableKey})"
                            : $"a worthwhile surplus chain ({pick.Value.plan.StableKey})";
                }
                // §5/§13 — a non-combat Aviation / Base / Facility / Equipment card a DRAW has just
                // revealed is as actionable as a Unit chain (fires MarkHandOpportunity below).
                // §P0 — but a non-combat card that was ALREADY in hand at the start of this loop
                // was offered to the unified Phase-B ranked loop (+ the dedicated aviation slot)
                // this turn and lost / was declined; it must NOT now strand every AP by blocking
                // the whole terminal draw. Only a fresh (drawn>0) reveal stops the loop.
                if (actionable == null && drawn > 0
                    && NonCombatCardPlayer.BestPlay(snap, player, root, hand, ctx, out _, null,
                        result.Reservation) is { } ncPlay)
                    actionable = $"a playable {ncPlay.Kind} card ({ncPlay.Explain})";

                if (actionable != null)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B terminal — stop: {actionable} is now actionable");
                    if (drawn > 0)
                    {
                        StrategicInterruptRegistry.MarkHandOpportunity(player, ctx.TurnNumber, hand);
                        AiDebugLog.Write($"[AI][V2] strategic interrupt — terminal draw changed the "
                            + "actionable hand; replan before converting any more AP to draws");
                    }
                    break;
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

        // AI-MGR-02 §4 — true only when Phase B should hold AP back for the bounded reaction pass:
        // an invalidation is pending AND the pass can run in this scope AND it has actionable
        // content. When false, non-combat surplus + terminal draws must NOT be gated off either.
        private static bool ReactionPassWillReserve(PlayerSetupData player, AiTurnContext ctx) =>
            player != null && ctx != null
            && StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber)
            && StrategicReactionPass.CanStrategicReactionPassRun(player, ctx)
            && StrategicReactionPass.HasActionableStrategicReaction(player, ctx);

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
