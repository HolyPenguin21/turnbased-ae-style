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

        // Multiple bounded local admissions in one turn report through one phase aggregate.
        // The shared ledgers/budgets remain the authorities; this only combines telemetry and
        // carries the newest reservation forward.
        public void Accumulate(StrategicPhaseResult other)
        {
            if (other == null) return;
            StateChanged |= other.StateChanged;
            CardsPlayed += other.CardsPlayed;
            CardsDrawn += other.CardsDrawn;
            MaterializationAttempts += other.MaterializationAttempts;
            MaterializationsSucceeded += other.MaterializationsSucceeded;
            GeneratedCardAttempts += other.GeneratedCardAttempts;
            GeneratedCardsSucceeded += other.GeneratedCardsSucceeded;
            EquipmentAssignmentAttempts += other.EquipmentAssignmentAttempts;
            EquipmentAssignmentsSucceeded += other.EquipmentAssignmentsSucceeded;
            InfrastructureAttempts += other.InfrastructureAttempts;
            InfrastructureBuilt += other.InfrastructureBuilt;
            CapabilityDeliveries += other.CapabilityDeliveries;
            foreach (KeyValuePair<DesireAxis, float> debit in other.ApDebited)
                AddDebit(debit.Key, debit.Value);
            if (other.Reservation != null)
                Reservation = other.Reservation;
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
        // economyAxisAuthoritative — true when `demands` reflects Economy's COMPLETE current view
        // (the Main pass, a reaction round's fresh DemandLayer.Generate, or an orchestration
        // reconciliation that actually re-evaluated Economy this round). False when `demands` is a
        // dirty-axis SUBSET that deliberately excludes Economy because Economy itself was not
        // re-evaluated this call (AiStrategyV2Pipeline.ReenterStrategicAxes) — there, an absent
        // Economy demand means "not looked at", not "resolved", and must never be read as license
        // to drop the deferred-build hold. Defaults to true: both full-list callers rely on it.
        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments,
            IReadOnlyList<MissionIntent> activeIntents = null,
            IReadOnlyList<ReconObjective> reconObjectives = null,
            MaterializationReservation carriedReservation = null,
            bool economyAxisAuthoritative = true)
        {
            if (player != null && root != null && ctx != null)
                TurnResourceTelemetry.CaptureStart(player, root, ctx.TurnNumber);

            // One turn-scoped identity set must survive main, reaction and housekeeping entries:
            // a failed generator/card pair is not a fresh candidate merely because Phase A re-entered.
            var result = new StrategicPhaseResult
            {
                Reservation = carriedReservation ?? new MaterializationReservation()
            };
            if (demands == null || demands.Count == 0 || player == null || root == null || hand == null || ledger == null)
            {
                // No demand at all this call. When Economy WAS authoritatively re-evaluated (not
                // just a non-Economy dirty-axis subset), an empty demand set really does mean
                // Economy has nothing left to protect — a stale hold from an earlier pass this turn
                // must not survive to block Phase B on a target that no longer exists.
                if (economyAxisAuthoritative && player != null && ctx != null)
                    InfrastructureFulfillment.ClearDeferredEconomyResources(player, ctx.TurnNumber);
                return result;
            }

            foreach (AxisDemand economyDemand in demands.Where(d => d != null
                         && d.RequestingAxis == DesireAxis.Economy
                         && d.EconomyBuildCard != null))
                result.Reservation.ClaimedEconomyBuildCards.Add(economyDemand.EconomyBuildCard);

            // AI-MGR — the NON-card half of the owner-witnessed AP workload the recurring-AP effect
            // (ApBonus) is priced against: AP that COMMITTED work will consume this turn — a durable
            // mission's claimed mover that has not activated yet, plus WITNESSED recon-air sorties
            // (ReconAssignmentPlanner.MeasureAirCapacity, the canonical air-capacity owner). Idle
            // unattached armies are deliberately NOT counted — a formation is not proof of a useful
            // operation. The card half is added per scoring pass below from the real candidate set.
            float committedNonCardAp = CommittedNonCardApDemand(
                snap, player, root, ctx, activeIntents, reconObjectives, commitments);
            float? witnessedUsefulApDemand = null;

            // AI-MGR — the witnessed AP-workload measurement pass only matters when a PlayerGlobal
            // recurring-resource effect (ApBonus today) could actually be SCORED this turn: a carrier
            // reachable through hand OR deck (a Challenge could still mint it). Descriptor-driven via
            // the registry — NOT a hardcoded UnitAbilities.ApBonus scan — so a generated / future
            // recurring mechanic is covered with no edit here. Recomputed nowhere; carrier set is
            // turn-stable.
            bool reachableGlobalRecurringCarrier = StrategicEffectRegistry.AnyGlobalRecurringCarrier(
                (snap?.Self?.Hand ?? System.Array.Empty<CardData>()).Select(c => c?.Definition)
                    .Concat(snap?.Self?.Deck ?? System.Array.Empty<CardDefinition>()));

            // AI-MGR-02 §P0 — one shared per-turn generation budget: a reaction-round Phase A must
            // not reset the Challenge count a main-pass generation already spent.
            if (ctx != null)
                result.Reservation.GenerationAttemptsUsed = StrategicTempoBudget.GenerationUsed(player, ctx.TurnNumber);

            AiDebugLog.Write($"[AI][V2]   strat.A — {player.Nickname} hand {AiCardLog.Hand(hand)}");

            var allStates = demands.Select((d, i) => new DemandState
                {
                    Demand = d,
                    Remaining = d != null ? Mathf.Max(0f, d.DesiredAmount) : 0f,
                    Ordinal = i,
                })
                .Where(s => s.Demand != null && s.Remaining > 0f)
                .ToList();

            // Persistence-gate reconciliation (spec "Bootstrap & No-Alternative-Work Escape") —
            // a demand an axis marked IsPersistenceDeferred is a REAL runnable opportunity with a
            // deliverable capability gap, but its capacity deficit has not persisted long enough to
            // auto-play. It must not compete in the normal arbitration pool from the start (that
            // would defeat the point of persistence), so it is held out of `states` here and only
            // reconsidered once every other demand this pass is satisfied, blocked, or infeasible —
            // see TryPromotePersistenceDeferred below.
            var states = allStates.Where(s => !s.Demand.IsPersistenceDeferred).ToList();
            var deferredStates = allStates.Where(s => s.Demand.IsPersistenceDeferred).ToList();
            if (states.Count == 0 && deferredStates.Count == 0)
            {
                if (economyAxisAuthoritative)
                    InfrastructureFulfillment.ClearDeferredEconomyResources(player, ctx.TurnNumber);
                return result;
            }

            // --- Infrastructure pre-pass. DEF/ECO/DEV EconomicInfrastructure / DevelopmentInfra
            //     demands are fulfilled by BuildingPlayExecutor through the authoritative gameplay
            //     API, NOT the Unit/Hero materialization chain below. The requesting axis labels
            //     value/telemetry; AP comes from the shared pool. Handled here once, then blocked so the generic
            //     loop does not emit a spurious "no feasible chain" for a capability it can't match.
            var deferredEconomyBuilds = new List<AxisDemand>();
            foreach (DemandState istate in states.Where(s => InfrastructureFulfillment.Handles(s.Demand.Capability)))
            {
                istate.Blocked = true;
                result.InfrastructureAttempts++;
                // Budget admission happens INSIDE TryFulfill, BEFORE any gameplay mutation: it
                // checks the shared AP pool and live affordability, and
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
                    if (istate.Demand.RequestingAxis == DesireAxis.Economy
                        && infra.BuilderArmyId.HasValue)
                        MissionContinuityLayer.BeginEconomyBuilderRecovery(
                            player, snap, istate.Demand, infra.BuilderArmyId.Value,
                            ctx.TurnNumber);
                }
                else
                {
                    // Only a real EconomicInfrastructure/Expansion demand reaches this hold:
                    // Demand has already proved a valuable site and an eligible builder route.
                    // A missing-builder Hero prerequisite is a different capability and therefore
                    // cannot lock the Human needed to create that hero.
                    if (istate.Demand.Capability == CapabilityKind.EconomicInfrastructure
                        || istate.Demand.Capability == CapabilityKind.EconomicExpansionBase)
                        deferredEconomyBuilds.Add(istate.Demand);
                    AiDebugLog.Write($"[AI][V2]   strat.A infra — {istate.Demand}: not built ({infra.Detail})");
                }
            }

            // Protect exactly one economy build vector — collected ONCE, before any per-round
            // reservation writer runs, from BOTH direct-build obligations (infra/expansion demands
            // that just failed TryFulfill) AND Hero-prerequisite obligations (an accepted Economy
            // build target with no Hero to send yet). Picking a single owner here up front means
            // the old per-round Hero-prerequisite writer inside the materialization loop below
            // cannot silently outbid (or be outbid by, in foreach order) this hold — there is only
            // ever one writer of StrategicReservationReason.EconomyDeferredBuild per FulfillDemands
            // call. Extraction and Base demands may coexist as alternatives, but reserving both
            // would manufacture a second resource-allocation layer inside Economy. The highest
            // admitted local priority owns the hold for this pass.
            var economyBuildObligations = deferredEconomyBuilds
                .Where(d => InfrastructureFulfillment.ShouldReserveDeferredEconomyResources(snap, d))
                .Concat(allStates
                    .Where(s => s.Demand != null
                        && s.Demand.RequestingAxis == DesireAxis.Economy
                        && s.Demand.Capability == CapabilityKind.Hero
                        && s.Demand.TargetHex.HasValue
                        && s.Demand.EconomyBuildResourceCost != null)
                    .Select(s => s.Demand))
                .ToList();
            AxisDemand protectedEconomyBuild = economyBuildObligations
                .OrderByDescending(d => IsCommittedEconomyBuild(activeIntents, d) ? 1 : 0)
                .ThenByDescending(d => d.Value + d.EconomyStrategicUrgency)
                .ThenByDescending(d => ResolveEconomyTaskKind(d) == EconomyTaskKind.FoundBase ? 1 : 0)
                .ThenByDescending(d => d.EconomySiteValue)
                .ThenBy(d => d.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(d => d.TargetHex?.R ?? int.MaxValue)
                .FirstOrDefault();
            if (protectedEconomyBuild != null)
            {
                if (protectedEconomyBuild.Capability == CapabilityKind.Hero)
                    InfrastructureFulfillment.ReserveDeferredEconomyResourcesForPendingHero(
                        player, ctx.TurnNumber, protectedEconomyBuild);
                else
                    InfrastructureFulfillment.ReserveDeferredEconomyResources(
                        snap, player, ctx.TurnNumber, protectedEconomyBuild);
                AiDebugLog.Write($"[AI][V2]   strat.A economy hold — protected "
                    + $"{protectedEconomyBuild.Capability} @({protectedEconomyBuild.TargetHex?.Q},"
                    + $"{protectedEconomyBuild.TargetHex?.R}) before card arbitration");
            }
            else if (economyAxisAuthoritative)
            {
                // No obligation survived selection — but only clear when this call actually had
                // Economy's authoritative view. A dirty-axis subset that never included Economy
                // (economyAxisAuthoritative == false) says nothing about whether Economy's build
                // target still exists; the hold must be left exactly as it was.
                InfrastructureFulfillment.ClearDeferredEconomyResources(
                    player, ctx.TurnNumber);
            }

            // CardUpgrade is intentionally not pre-executed here. It enters the same candidate
            // builder + jointly-feasible Phase-A portfolio below as every materialization demand.

            int chainAttempts = 0;
            while (chainAttempts < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
            {
                List<DemandState> active = states.Where(s => !s.Blocked && s.Remaining > 0f).ToList();
                if (active.Count == 0)
                {
                    if (TryPromotePersistenceDeferred(states, deferredStates, snap, player, root, hand, ctx,
                        ledger, commitments, result.Reservation, witnessedUsefulApDemand))
                        continue;
                    break;
                }

                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);

                // AI-MGR — MEASUREMENT PASS (two-pass within the round). Assemble the owner-witnessed
                // AP workload the recurring-AP effect is priced against from the SAME feasible
                // candidate set the scoring pass below uses, BEFORE any AP-dependent scoring — so a
                // good ApBonus carrier is never dropped by fallback-scored Top-K pruning before it is
                // priced against the real workload. Feasibility-based ONLY, never DecisionScore /
                // Worthwhile (that would be circular: AP utility -> card score -> workload -> AP
                // utility). Recomputed every round because an executed chain changes hand / AP /
                // generation / physical state.
                //
                // Only worth doing when something this round actually READS it: a reachable
                // PlayerGlobal recurring-resource carrier (GlobalRecurringValue) or a Hero demand
                // (Command 6-vs-7 filler count). Otherwise leave witnessedUsefulApDemand null -> the
                // evaluator uses the discounted structural fallback, exactly as before.
                bool needsWitnessedWorkload = reachableGlobalRecurringCarrier
                    || active.Any(s => s.Demand.Capability == CapabilityKind.Hero);

                List<(MaterializationPlan plan, float followupAp)> fillerUniverse = null;
                if (needsWitnessedWorkload)
                {
                    var measOptions = new Dictionary<DemandState, List<(MaterializationPlan plan, float followupAp)>>();
                    foreach (DemandState state in active)
                    {
                        var feas = MaterializationCandidateBuilder.AllFeasiblePlansForDemand(snap, player, root, hand,
                            ctx, state.Demand, ledger, commitments,
                            ledger.ReservedFollowup(state.Demand.RequestingAxis), result.Reservation);
                        if (feas.Count > 0)
                            measOptions[state] = feas;
                    }
                    int genRemaining = Mathf.Max(0, AiConfigV2.maxGenerationActionsPerTurn
                        - result.Reservation.GenerationAttemptsUsed);
                    float legalCardApWorkload = MaterializationPortfolioSolver.EstimateLegalApWorkload(
                        measOptions, root, player, ctx, hand, genRemaining);
                    witnessedUsefulApDemand = committedNonCardAp + legalCardApWorkload;

                    // Cross-demand filler universe for the hero Command 6-vs-7 valuation: every
                    // feasible body across ALL active demands this round (one per physical consumption
                    // signature), so a Hero demand's candidate can see the Unit plans a
                    // FieldCombatPower demand would put in the same recipient.
                    fillerUniverse = measOptions.Values.SelectMany(v => v).ToList();
                }
                else
                {
                    witnessedUsefulApDemand = null;
                }

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
                            result.Reservation, inv, competingHeroDemand, AiConfigV2.phaseATopK,
                            witnessedUsefulApDemand: witnessedUsefulApDemand,
                            fillerUniverse: fillerUniverse);
                    if (top.Count > 0)
                        options[state] = top;
                }

                Dictionary<DemandState, DemandCandidate> assigned =
                    options.Count > 0
                        ? MaterializationPortfolioSolver.BestInjectiveAssignment(options, root, player, ctx, hand,
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
                    // No currently-active demand has any feasible chain this round — the AI is about
                    // to give up on real strategic work for this pass. Before it does, give any
                    // persistence-deferred demand its no-alternative-work chance (spec Rule 2).
                    if (TryPromotePersistenceDeferred(states, deferredStates, snap, player, root, hand, ctx,
                        ledger, commitments, result.Reservation, witnessedUsefulApDemand))
                        continue;
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

                if (plan.Kind == MaterializationChainKind.GenerateAttachUpgrade)
                {
                    DevUpgradeResult up = DevelopmentUpgradeFulfillment.TryFulfill(
                        snap, player, root, hand, ctx, chosenDemand, plan, ledger);
                    int upgradeApAfter = root.ActionPoints;
                    chainAttempts++;
                    result.MaterializationAttempts++;
                    result.EquipmentAssignmentAttempts++;

                    if (up.ApSpent > 0f)
                    {
                        float ledgerBefore = ledger.Balance(chosenDemand.RequestingAxis);
                        ledger.Debit(chosenDemand.RequestingAxis, up.ApSpent);
                        result.AddDebit(chosenDemand.RequestingAxis, up.ApSpent);
                        float ledgerAfter = ledger.Balance(chosenDemand.RequestingAxis);
                        AiV2Trace.CheckPhaseAAp(chosenDemand.TraceId, chosenDemand.RequestingAxis,
                            chainApBefore - upgradeApAfter, up.ApSpent, ledgerBefore - ledgerAfter);
                    }

                    if (up.Executed)
                    {
                        result.GeneratedCardAttempts++;
                        var attempted = new MaterializationResult
                        {
                            StateChanged = up.StateChanged,
                            GenerationAttempted = true,
                            Generated = up.ChallengeWon,
                            Attached = up.Attached,
                            ApSpent = up.ApSpent,
                            AttemptedGenerationUseKey = plan.Generation?.UseKey,
                        };
                        result.Reservation.RecordGenerationAttempt(plan.Generation, attempted);
                        StrategicTempoBudget.RecordGenerationAttempt(player, ctx.TurnNumber);
                        if (up.ChallengeWon) result.GeneratedCardsSucceeded++;
                        if (up.Attached)
                        {
                            result.MaterializationsSucceeded++;
                            result.EquipmentAssignmentsSucceeded++;
                            result.CapabilityDeliveries++;
                            result.CardsPlayed++;
                            selected.State.Remaining = 0f;
                        }
                        else
                            selected.State.Blocked = true;
                    }
                    else
                        selected.State.Blocked = true;

                    if (up.StateChanged)
                    {
                        result.StateChanged = true;
                        snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    }
                    AiDebugLog.Write($"[AI][V2][Dev] {(up.Executed ? "EXEC" : "SKIP")} — "
                        + $"{chosenDemand.Explain} :: {up.Detail} (ap {F(up.ApSpent)} -> DEV)");
                    continue;
                }

                MaterializationResult play = MaterializationExecutor.Execute(
                    snap, player, root, hand, ctx, plan, commitments);
                int chainApAfter = root.ActionPoints;
                chainAttempts++;

                // Production telemetry (spec §12) — attempts/successes per chain stage. Derived
                // from the plan shape + MaterializationResult; no scoring change.
                result.MaterializationAttempts++;
                if (play.Deployed) result.MaterializationsSucceeded++;
                if (play.GenerationAttempted)
                {
                    result.GeneratedCardAttempts++;
                    if (play.Generated) result.GeneratedCardsSucceeded++;
                }
                if (plan.UsesEquipment)
                {
                    result.EquipmentAssignmentAttempts++;
                    if (play.Attached) result.EquipmentAssignmentsSucceeded++;
                }

                if (play.GenerationAttempted)
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
                    // ARCH-02 §35 — a stale-placement failure means "replan me", not "block me":
                    // refresh so the next TopForDemand enumerates against the current world, and
                    // leave the demand active. The loop is still bounded by chainAttempts.
                    if (play.StateChanged || play.PlacementStale)
                        snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    if (!play.StateChanged && !play.PlacementStale && plan.Generation == null)
                        selected.State.Blocked = true;
                    continue;
                }

                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                CapabilityInventory afterInv = CapabilityInventory.Build(snap, player, commitments);
                bool operationallyDelivered = CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(player, ctx, snap, plan,
                    chosenDemand, inv, afterInv, armyIdsBefore, out float delivered);

                if (operationallyDelivered
                    && MaterializationDeliveryPolicy.IsEconomyHeroDemand(chosenDemand))
                {
                    int builderArmyId = CapabilityDeliveryEvaluator.OperationalLeaseArmyIds(
                            armyIdsBefore, snap, plan, chosenDemand)
                        .OrderBy(id => id).FirstOrDefault();
                    if (builderArmyId != 0)
                    {
                        DemandLayer.EconomyBuilderChoice delivery =
                            CapabilityDeliveryEvaluator.EconomyDeliveryChoice(
                                snap, chosenDemand, builderArmyId, activeIntents,
                                commitments, out IReadOnlyList<EconomyBuilderRouteSnapshot> routes);
                        if (delivery != null)
                        {
                            chosenDemand.EconomyPreferredBuilderArmyId = builderArmyId;
                            chosenDemand.EconomyBuilderRoutes = routes;
                            chosenDemand.EconomyProjectedActivationApCost =
                                delivery.ProjectedActivationApCost;
                            chosenDemand.EconomyProjectedMaxMovement =
                                delivery.ProjectedMaxMovement;
                            chosenDemand.EconomyAssignmentApCost =
                                delivery.TotalAssignmentApCost;
                        }
                        MissionContinuityLayer.BeginEconomyDelivery(
                            player, chosenDemand, builderArmyId, ctx.TurnNumber);
                        // Continuity owns the actor from this point. Mirror that handoff into the
                        // current Phase-A view as well, so a later chain in this same bounded pass
                        // cannot treat the freshly delivered Economy army as a free recipient.
                        commitments?.Claim(builderArmyId);
                        // Reserve the saved build envelope the instant the dedicated builder is
                        // committed. Gating this on `delivery != null` (ShouldReserveDeferredEconomy
                        // Resources' witnessed-route check) left it unreserved for however many turns
                        // the freshly-materialized hero needed before that route witness could see it
                        // (it wasn't yet a recognised mobile builder or garrisoned on target) — during
                        // that window Phase B was free to spend the exact H/E/M/T this build still
                        // needs. BeginEconomyDelivery above already committed this exact actor to this
                        // exact target, so the envelope is owed regardless of route visibility.
                        InfrastructureFulfillment.ReserveEconomyCost(player, ctx.TurnNumber,
                            InfrastructureFulfillment.EconomyReservationOwner(new AxisDemand
                            {
                                RequestingAxis = DesireAxis.Economy,
                                Capability = chosenDemand.EconomyBuildCard?.Definition?.cardType
                                    == CardType.Base
                                        ? CapabilityKind.EconomicExpansionBase
                                        : CapabilityKind.EconomicInfrastructure,
                                TargetHex = chosenDemand.TargetHex,
                                EconomyResourceType = chosenDemand.EconomyResourceType,
                            }),
                            chosenDemand.EconomyBuildResourceCost, 0f,
                            StrategicReservationReason.EconomyDeferredBuild);
                    }
                }

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
            // Deferred demands never promoted this pass (no runnable window to try them, or no
            // deliverable candidate — AC7) are still real unmet strategic need; carry them into the
            // same residual pool the reaction pass / Phase B read, so a later chance this turn is not
            // treated as if the need never existed.
            foreach (DemandState state in deferredStates.Where(s => s.Remaining > 0f))
                result.Reservation.UnresolvedDemands.Add(CloneResidualDemand(state));

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — {result.CardsPlayed} chain(s), ledger now " + ledger.DebugLine());
            if (result.Reservation.UnresolvedDemands.Count > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — residual demands "
                    + string.Join(" | ", result.Reservation.UnresolvedDemands.Select(d => d.ToString())));
            return result;
        }

        private static bool IsCommittedEconomyBuild(
            IReadOnlyList<MissionIntent> activeIntents, AxisDemand demand)
        {
            if (activeIntents == null || demand?.TargetHex == null)
                return false;
            EconomyTaskKind kind = ResolveEconomyTaskKind(demand);
            return activeIntents.Any(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.Economy && i.Economy?.Kind == kind
                && i.Economy.TargetHex.Equals(demand.TargetHex.Value)
                && (kind != EconomyTaskKind.FoundBase || i.Economy.BuildCard == null
                    || i.Economy.BuildCard == demand.EconomyBuildCard));
        }

        // A Base-founding EconomyHeroPrerequisite demand (Capability.Hero) carries no
        // EconomicExpansionBase capability of its own — it is only distinguishable from an
        // extraction-facility Hero prerequisite through the underlying build card's cardType.
        // Falling back to BuildExtraction for every Hero demand here would make a Base-founding
        // pending Hero invisible to the active-commitment tie-break above.
        private static EconomyTaskKind ResolveEconomyTaskKind(AxisDemand demand)
        {
            if (demand.Capability == CapabilityKind.EconomicExpansionBase)
                return EconomyTaskKind.FoundBase;
            if (demand.Capability == CapabilityKind.Hero
                && demand.EconomyBuildCard?.Definition?.cardType == CardType.Base)
                return EconomyTaskKind.FoundBase;
            return EconomyTaskKind.BuildExtraction;
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
                DevOpportunity = d.DevOpportunity,
                DevelopmentOperatorMode = d.DevelopmentOperatorMode,
                EconomyResourceType = d.EconomyResourceType,
                EconomyBuildCard = d.EconomyBuildCard,
                EconomyBuildResourceCost = d.EconomyBuildResourceCost,
                EconomyBuildApCost = d.EconomyBuildApCost,
                EconomyExpectedIncomeGain = d.EconomyExpectedIncomeGain,
                EconomySiteValue = d.EconomySiteValue,
                EconomyTravelCost = d.EconomyTravelCost,
                EconomyThreatExposure = d.EconomyThreatExposure,
                EconomyHeroOpportunityCost = d.EconomyHeroOpportunityCost,
                EconomyAssignmentApCost = d.EconomyAssignmentApCost,
                EconomyPaybackTurns = d.EconomyPaybackTurns,
                EconomyPreferredBuilderArmyId = d.EconomyPreferredBuilderArmyId,
                EconomyProjectedActivationApCost = d.EconomyProjectedActivationApCost,
                EconomyProjectedMaxMovement = d.EconomyProjectedMaxMovement,
                EconomyBuilderRoutes = d.EconomyBuilderRoutes,
                RequiredCapabilityPower = d.RequiredCapabilityPower,
                Explain = d.Explain,
                IsPersistenceDeferred = d.IsPersistenceDeferred,
            };
        }

        // AI-MGR — the NON-card half of the owner-witnessed AP workload (see FulfillDemands). Only
        // DEMONSTRATED obligations count: a durable mission's claimed mover that has not activated
        // this turn (activation AP, floored at 1), plus the WITNESSED recon-air sortie count from the
        // canonical air-capacity owner (ReconAssignmentPlanner.MeasureAirCapacity). Idle unattached
        // armies and "actionable formation" heuristics are deliberately excluded — a formation is not
        // proof of a useful AP operation. Development is 0 here: DemandLayer emits its axis demand
        // precisely when there is NO operator base, so it is not a runnable AP action.
        private static float CommittedNonCardApDemand(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<ReconObjective> reconObjectives, ActorCommitments commitments)
        {
            float ap = 0f;
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies;
            if (activeIntents != null && armies != null)
            {
                var countedMovers = new HashSet<int>();
                foreach (MissionIntent intent in activeIntents)
                {
                    if (intent?.PreferredMoverArmyId == null) continue;
                    int moverId = intent.PreferredMoverArmyId.Value;
                    if (!countedMovers.Add(moverId)) continue;
                    ArmySnapshot army = null;
                    foreach (ArmySnapshot a in armies)
                        if (a != null && a.ArmyId == moverId) { army = a; break; }
                    if (army == null || army.HasActivatedThisTurn) continue;
                    ap += Mathf.Max(1f, army.ActivationApCost);
                }
            }

            (int airborne, int spare) = ReconAssignmentPlanner.MeasureAirCapacity(
                ctx, player, root, snap, reconObjectives, activeIntents, commitments);
            ap += (Mathf.Max(0, airborne) + Mathf.Max(0, spare)) * AiConfigV2.apAirSortieApProxy;
            return ap;
        }

        // Persistence-gate reconciliation (spec "Bootstrap & No-Alternative-Work Escape", Rule 2).
        // Called only at a point where the normal per-turn arbitration loop is about to give up —
        // every currently active (non-deferred) demand is satisfied, blocked, or has no feasible
        // chain this round. That is exactly "no other actionable work exists to prefer instead" in
        // Phase A's own frame, so any persistence-deferred demand gets one real shot: if it has a
        // legal/affordable/operationally-deliverable candidate RIGHT NOW (the same TopForDemand
        // query every other demand uses — never a phantom fulfillment, AC7), it is moved into the
        // normal `states` pool and competes there through the ordinary candidate scoring (AC10);
        // otherwise it is left deferred and the caller proceeds to Phase B as usual (AC7/AC8).
        // Every deferred demand is tried once per call so several axes can each get a fair chance
        // in the same reconciliation window; promotion never re-derives runnable-opportunity or
        // capacity facts — those were already established once by the emitting axis (DemandLayer).
        private static bool TryPromotePersistenceDeferred(List<DemandState> states,
            List<DemandState> deferredStates, WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger, ActorCommitments commitments,
            MaterializationReservation reservation, float? witnessedUsefulApDemand)
        {
            if (deferredStates.Count == 0)
                return false;

            bool promotedAny = false;
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
            foreach (DemandState ds in deferredStates.ToList())
            {
                // §6/§7 — Demand already measures its persistence-gate deficit against EXISTING
                // FEASIBLE capacity only (desired - existing feasible), never against future mission
                // funding (AxisDemand no longer even carries ExistingUsableCapacityAtEmission — the
                // pre-ledger/post-ledger composite that lived here, FundedActionableNow, is removed
                // entirely per the architecture rule against a third concept combining CAPABILITY and
                // FUNDING). So the only question left for the reconciliation window is the generic
                // one every other demand answers the same way: is there a legal/affordable/
                // deliverable candidate for it RIGHT NOW (AC7) — never a phantom fulfillment.
                List<DemandCandidate> top = MaterializationCandidateBuilder.TopForDemand(snap, player, root, hand,
                    ctx, ds.Demand, ledger, commitments, ledger.ReservedFollowup(ds.Demand.RequestingAxis),
                    reservation, inv, hasCompetingHeroDemand: false, AiConfigV2.phaseATopK,
                    witnessedUsefulApDemand: witnessedUsefulApDemand);
                if (top.Count == 0)
                {
                    // Stays a VALID, UNRESOLVED, temporarily-unfulfillable demand — must remain in
                    // deferredStates (not removed) so it still reaches Reservation.UnresolvedDemands
                    // below. Removing it here would silently turn "no candidate right now" into
                    // "no demand ever existed", losing the residual for Phase B and next turn's log.
                    AiDebugLog.Write($"[AI][V2]   strat.A persistence-reconcile — {ds.Demand}: "
                        + "decision=DEFER reason=no_deliverable_candidate; stays deferred, Phase B proceeds");
                    continue;
                }
                AiDebugLog.Write($"[AI][V2]   strat.A persistence-reconcile — {ds.Demand}: "
                    + $"decision=PROMOTE previousGate=persistence reason=no_alternative_actionable_work "
                    + $"deliverableCandidates={top.Count} best={top[0].Plan.StableKey}");
                // A promoted demand IS now an ordinary actionable demand — clear the flag on the
                // SAME AxisDemand instance so residual reporting / Phase B's UnresolvedClaimFor
                // cannot tell it apart from a demand that was never persistence-gated at all.
                ds.Demand.IsPersistenceDeferred = false;
                deferredStates.Remove(ds);
                states.Add(ds);
                promotedAny = true;
            }
            return promotedAny;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
