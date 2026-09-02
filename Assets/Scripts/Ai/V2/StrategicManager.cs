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
                var feasible = new List<PhaseACandidate>();
                foreach (DemandState state in active)
                {
                    AxisDemand demand = state.Demand;
                    float reserved = ledger.ReservedFollowup(demand.RequestingAxis);
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

        private static float ArbitrationScore(PhaseACandidate c) =>
            Mathf.Max(0f, c.State.Demand.Value) * Mathf.Max(0.0001f, c.Plan.Score);

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
        // DemandLayer portfolio cap never sees those).
        private static bool ScoutSurplusPortfolioSaturated(PlayerSetupData player, MaterializationPlan plan)
        {
            if (plan == null || plan.FinalCapability != CapabilityKind.ScoutCapability)
                return false;
            int solo = ArmyRegistry.AllForOwner(player).Count(a => a != null && AiArmyRoles.IsSoloRecce(a));
            return solo >= ReconConcurrencyPolicy.HardCap;
        }

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

            if (StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber))
            {
                AiDebugLog.Write($"[AI][V2]   strat.B — deferred: pending strategic reaction interrupt; "
                    + $"preserve {root.ActionPoints} AP for bounded reaction pass");
                return result;
            }

            AiDebugLog.Write($"[AI][V2]   strat.B — {player.Nickname} hand {AiCardLog.Hand(hand)}");
            bool cleanStop = true;
            // One shared Phase-B action budget across BOTH lanes (materialization surplus +
            // non-combat surplus) — maxSurplusActionsPerTurn is the whole-phase safety bound, not
            // per-lane.
            int surplusActionsUsed = 0;
            for (; surplusActionsUsed < AiConfigV2.maxSurplusActionsPerTurn; surplusActionsUsed++)
            {
                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                (MaterializationPlan plan, float utility)? pick = MaterializationCandidateBuilder.BestSurplus(
                    snap, player, root, hand, ctx, inv, commitments, result.Reservation);
                if (pick == null)
                    break;

                MaterializationPlan plan = pick.Value.plan;
                AxisDemand matchedResidual = result.Reservation.BestUnresolvedDemandFor(plan);
                AxisDemand residual = matchedResidual != null && CanDeliverResidualOperationally(plan, matchedResidual)
                    ? matchedResidual : null;
                SurplusAdmission admission = SurplusAdmissionPolicy.Evaluate(root, player, plan);

                if (matchedResidual != null && residual == null)
                    AiDebugLog.Write($"[AI][V2]   strat.B — residual bypass denied for {plan.StableKey}: "
                        + $"{plan.Deploy.Kind} cannot operationally deliver {matchedResidual.Capability}; "
                        + "evaluate as generic surplus");

                // §2 — a Hero card that still matches an unresolved Hero demand must never be spent
                // as generic surplus through a placement that delivers 0 Hero. Candidate
                // construction (MaterializationCandidateBuilder.BestSurplus) already withholds such
                // placements; this is the belt-and-braces stop so the hero is left in hand rather
                // than burned into the garrison while the demand stays actionable.
                if (matchedResidual != null && residual == null
                    && matchedResidual.Capability == CapabilityKind.Hero && PlanBaseIsHeroCard(plan))
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — hold {plan.StableKey}: hero card matches "
                        + $"unresolved {matchedResidual} but no placement delivers it; keep in hand, stop surplus");
                    break;
                }

                // §P1 — once the garrison is a strong defensive stack and nothing threatens an
                // asset, a generic (no-residual) card whose only placement is "into the garrison"
                // must clear a much higher bar, so the loop stops and stranded AP converts to
                // draws instead of grinding the garrison from 6 to 40+ power with threats=0.
                float satMult = GarrisonSaturationThresholdMult(snap, plan, residual);
                float effThreshold = admission.EffectiveThreshold * satMult;

                // §P1 — a generic surplus card must not FOUND a lone-member army at a hex that
                // already holds our garrison: Housekeeping reclassifies it as a structural defect
                // the same turn (create -> fold -> reseed). A genuine forward outpost away from
                // any base of ours is still allowed.
                if (residual == null && GenericSurplusWouldChurn(player, plan))
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — hold {plan.StableKey} {AiCardLog.Plan(plan)}: "
                        + "generic surplus would found a lone-member army housekeeping folds the "
                        + "same turn; stop surplus");
                    break;
                }

                if (residual == null && ScoutSurplusPortfolioSaturated(player, plan))
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — hold {plan.StableKey} {AiCardLog.Plan(plan)}: "
                        + $"generic surplus would add a scout beyond the physical portfolio cap "
                        + $"({ReconConcurrencyPolicy.HardCap}); stop surplus");
                    break;
                }

                if (residual == null && pick.Value.utility < effThreshold)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — defer {plan.StableKey} {AiCardLog.Plan(plan)} "
                        + $"util {F(pick.Value.utility)} < threshold {F(effThreshold)} "
                        + $"(base {F(admission.BaseThreshold)}, apSlack {F(admission.ApSlack)}, "
                        + $"resSlack {F(admission.ResourceSlackFactor)}"
                        + $"{(satMult > 1f ? $", garrisonSaturatedx{F(satMult)}" : "")}), stop");
                    break;
                }

                if (residual != null)
                    AiDebugLog.Write($"[AI][V2]   strat.B — admit residual {residual} via "
                        + $"{plan.StableKey} {AiCardLog.Plan(plan)} util {F(pick.Value.utility)} "
                        + "(operational strategic residual outranks generic surplus)");
                else
                    AiDebugLog.Write($"[AI][V2]   strat.B — admit {plan.StableKey} {AiCardLog.Plan(plan)} "
                        + $"util {F(pick.Value.utility)} >= threshold {F(effThreshold)} "
                        + $"(base {F(admission.BaseThreshold)}, apSlack {F(admission.ApSlack)}, "
                        + $"resSlack {F(admission.ResourceSlackFactor)}"
                        + $"{(satMult > 1f ? $", garrisonSaturatedx{F(satMult)}" : "")})");

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
                // §3 — Phase B now runs the SAME finalization path as Phase A: an army it created
                // or modified to satisfy a strategic residual gets the Housekeeping capability
                // lease, so the later zero-AP structural pass can no longer fold that force away
                // in the same turn it was materialized.
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
                    + $"util {F(pick.Value.utility)} (ap {F(play.ApSpent)}, {plan.Deploy.Kind}, "
                    + $"delivered {F(delivered)}, {plan.StableKey})");

                if (operationalResidual)
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

            // Spec §5/§13 — the non-combat lane: Aviation / Base / Facility / standalone Equipment
            // cards the materialization chain cannot body. Runs in every mode, through the same
            // canonical gameplay API the human UI uses, and every card left in hand carries a real
            // gameplay reason (never "wrong card type" / "ReconOnly").
            //
            // Ordering: materialization surplus first, then this lane, sharing one action budget.
            // The two utility scales are not comparable (SurplusUtility is a sum of small
            // config-weighted terms; NonCombatPlay.Score is a coarse 24-55 band), so they are NOT
            // merged into a single ranking — that would let a mid-value non-combat card out-bid
            // every Unit/Hero chain. Combat readiness / demand-relevant cards are also the more
            // time-sensitive: a facility or a stored aircraft is equally playable next turn.
            //
            // GATED on cleanStop AND no pending interrupt: if the materialization loop raised a
            // strategic interrupt ("re-admit missions before further surplus spending" —
            // operationalResidual, or a failed chain), NO further Phase-B spending of ANY kind may
            // happen this pass, or the bounded reaction pass would re-plan against AP/Energy the
            // non-combat lane already spent. Shares the one surplusActionsUsed budget.
            if (cleanStop && !StrategicInterruptRegistry.HasPendingDiscovery(player, ctx.TurnNumber))
                snap = RunNonCombatSurplus(snap, player, root, hand, ctx, result, ref surplusActionsUsed);
            else
                AiDebugLog.Write("[AI][V2]   strat.B non-combat — skipped: Phase B did not end cleanly "
                    + "(strategic interrupt / failed chain); non-combat cards wait for the next pass");

            if (cleanStop && RunTerminalDraws(snap, player, root, hand, ctx, commitments, result))
                result.StateChanged = true;
            return result;
        }

        private static WorldSnapshot RunNonCombatSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, StrategicPhaseResult result,
            ref int surplusActionsUsed)
        {
            int played = 0;
            int reservedUsed = 0;
            List<string> lastBlocked = null;
            // §P0 — the non-combat lane shares maxSurplusActionsPerTurn with the materialization
            // surplus loop, but is GUARANTEED surplusNonCombatReservedActions plays beyond an
            // exhausted shared budget. Without this a run of generic garrison dumps drains the
            // whole budget, a playable stored-Aviation card is never actually played, and it then
            // blocks RunTerminalDraws from converting stranded AP (the card is "actionable" but
            // nothing plays it) — leaving it in hand for turns.
            while (true)
            {
                bool sharedBudgetLeft = surplusActionsUsed < AiConfigV2.maxSurplusActionsPerTurn;
                bool reservedLeft = reservedUsed < AiConfigV2.surplusNonCombatReservedActions;
                if (!sharedBudgetLeft && !reservedLeft)
                    break;

                NonCombatCardPlayer.NonCombatPlay play =
                    NonCombatCardPlayer.BestPlay(snap, player, root, hand, ctx, out List<string> blocked);
                lastBlocked = blocked;
                if (play == null)
                    break;

                result.MaterializationAttempts++;
                bool ok = NonCombatCardPlayer.Execute(play, snap, player, root, hand, ctx,
                    out float apSpent, out string fail);
                if (ok)
                {
                    result.MaterializationsSucceeded++;
                    result.CardsPlayed++;
                    result.StateChanged = true;
                    if (play.Kind == NonCombatCardPlayer.PlayKind.Base
                        || play.Kind == NonCombatCardPlayer.PlayKind.Facility)
                    {
                        result.InfrastructureAttempts++;
                        result.InfrastructureBuilt++;
                    }
                    else if (play.Kind == NonCombatCardPlayer.PlayKind.Equipment)
                    {
                        result.EquipmentAssignmentAttempts++;
                        result.EquipmentAssignmentsSucceeded++;
                    }
                    played++;
                    if (surplusActionsUsed < AiConfigV2.maxSurplusActionsPerTurn)
                        surplusActionsUsed++;
                    else
                        reservedUsed++;
                    AiDebugLog.Write($"[AI][V2]   strat.B non-combat — played {play.Kind} {play.Explain} "
                        + $"(ap {F(apSpent)}{(reservedUsed > 0 ? ", reserved slot" : "")})");
                    snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                }
                else
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B non-combat — {play.Kind} {play.Explain} "
                        + $"did not play ({fail}); stop");
                    break;
                }
            }

            AiDebugLog.Write($"[AI][V2]   strat.B non-combat — played {played}"
                + (lastBlocked != null && lastBlocked.Count > 0
                    ? $"; still blocked [{string.Join(", ", lastBlocked)}]"
                    : "; nothing blocked"));
            return snap;
        }

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
                        || ScoutSurplusPortfolioSaturated(player, pick.Value.plan)))
                        termThreshold = float.MaxValue;
                    if (residual != null || pick.Value.utility >= termThreshold)
                        actionable = residual != null
                            ? $"an operational residual demand ({pick.Value.plan.StableKey})"
                            : $"a worthwhile surplus chain ({pick.Value.plan.StableKey})";
                }
                // §5/§13 — a non-combat Aviation / Base / Facility / Equipment card a DRAW has just
                // revealed is as actionable as a Unit chain (fires MarkHandOpportunity below).
                // §P0 — but a non-combat card that was ALREADY in hand at the start of this loop
                // was offered to RunNonCombatSurplus this turn and declined (shared budget +
                // reserved slots exhausted); it must NOT now strand every AP by blocking the whole
                // terminal draw. Only a fresh (drawn>0) reveal stops the loop.
                if (actionable == null && drawn > 0
                    && NonCombatCardPlayer.BestPlay(snap, player, root, hand, ctx, out _) is { } ncPlay)
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

        internal static bool ReservesOkAfterChain(PlayerRoot root, MaterializationPlan plan)
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

            bool Has(ResourceType type, int spend)
            {
                float available = Mathf.Max(0f, root.GetResource(type));
                return available >= Mathf.Max(0, spend);
            }
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
