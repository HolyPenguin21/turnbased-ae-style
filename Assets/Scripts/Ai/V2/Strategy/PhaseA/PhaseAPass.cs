using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Phase-A working state for one capability demand.
    internal sealed class DemandState
    {
        public AxisDemand Demand;
        public float Remaining;
        public int Ordinal;
        public bool Blocked;
    }

    // The state of ONE StrategicPhaseA.FulfillDemands call, shared by its stages (economy holds,
    // infrastructure lane, card rounds). Nothing here survives the call: what a later entry of the
    // same turn sees lives in the carried MaterializationReservation, the PhaseAApBudget, the
    // ledgers and the intent store. Snap, Demands, ActiveIntents and Commitments are re-read in
    // the pass (a played card refreshes the snapshot, a released build the intents), so every
    // stage reads them here, never from a copy taken earlier.
    internal sealed class PhaseAPass
    {
        public readonly PlayerSetupData Player;
        public readonly PlayerRoot Root;
        public readonly AiHandData Hand;
        public readonly AiTurnContext Ctx;
        public readonly PhaseAApBudget ApBudget;
        public readonly MissionIntentState IntentState;
        public readonly IReadOnlyList<ReconObjective> ReconObjectives;
        public readonly Radar Radar;
        // The axes this entry re-evaluated (StrategicPhaseA.FulfillDemands `admittedAxes`). For
        // them an absent demand means "resolved"; any other axis was not looked at, so its
        // residuals, build-card claims and Economy hold are left as an earlier entry set them.
        public readonly HashSet<DesireAxis> AdmittedAxes;
        public readonly bool DeferFreshZeroRadar;
        public readonly StrategicPhaseResult Result;

        public WorldSnapshot Snap;
        public IReadOnlyList<AxisDemand> Demands;
        public IReadOnlyList<MissionIntent> ActiveIntents;
        public ActorCommitments Commitments;

        // Demand pools (Partition). States compete now; DeferredStates wait for the persistence
        // gate; ColdStates are fresh zero-Radar work left to the late residual entry.
        public List<DemandState> AllStates;
        public List<DemandState> States;
        public List<DemandState> DeferredStates;
        public List<DemandState> ColdStates;
        // Infrastructure lanes (PhaseAInfrastructureLane.Classify).
        public List<DemandState> ResidualInfrastructure;
        public HashSet<DemandState> ArbitratedInfrastructure;
        public bool AnyProtectedActiveEconomyBuild;

        // The owner-witnessed AP workload the recurring-AP effect is priced against (PhaseACandidateRound).
        public float CommittedNonCardAp;
        public float? WitnessedUsefulApDemand;
        public bool ReachableGlobalRecurringCarrier;
        public int ChainAttempts;

        public PhaseAPass(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, PhaseAApBudget apBudget, IReadOnlyList<AxisDemand> demands,
            ActorCommitments commitments, MissionIntentState intentState,
            IReadOnlyList<MissionIntent> activeIntents, IReadOnlyList<ReconObjective> reconObjectives,
            IReadOnlyCollection<DesireAxis> admittedAxes, Radar radar, bool deferFreshZeroRadar,
            StrategicPhaseResult result)
        {
            Snap = snap;
            Player = player;
            Root = root;
            Hand = hand;
            Ctx = ctx;
            ApBudget = apBudget;
            Demands = demands ?? System.Array.Empty<AxisDemand>();
            Commitments = commitments;
            IntentState = intentState;
            ActiveIntents = activeIntents;
            ReconObjectives = reconObjectives;
            AdmittedAxes = new HashSet<DesireAxis>(admittedAxes ?? DesireAxes.All);
            Radar = radar ?? Radar.Even();
            DeferFreshZeroRadar = deferFreshZeroRadar;
            Result = result;
        }

        public int Turn => Ctx.TurnNumber;
        public bool EconomyAxisAuthoritative => AdmittedAxes.Contains(DesireAxis.Economy);
        public MaterializationReservation Reservation => Result.Reservation;

        public void RefreshKnowledge() =>
            Snap = WorldAnalysis.RefreshStrategicKnowledge(Snap, Player, Root, Hand, Ctx);

        // Early admissions defer NEW zero-priority work, but never delay a durable Economy build or
        // a consumer-linked Raid reinforcement (StrategicPhaseA.ShouldDeferFreshZeroRadarDemand).
        // Late residual admission uses this same owner with DeferFreshZeroRadar=false.
        //
        // Persistence-gate reconciliation (spec "Bootstrap & No-Alternative-Work Escape") —
        // a demand an axis marked IsPersistenceDeferred is a REAL runnable opportunity with a
        // deliverable capability gap, but its capacity deficit has not persisted long enough to
        // auto-play. It must not compete in the normal arbitration pool from the start (that
        // would defeat the point of persistence), so it is held out of `States` here and only
        // reconsidered once every other demand this pass is satisfied, blocked, or infeasible —
        // see TryPromotePersistenceDeferred below.
        public void Partition()
        {
            AllStates = Demands.Select((d, i) => new DemandState
                {
                    Demand = d,
                    Remaining = d != null ? Mathf.Max(0f, d.DesiredAmount) : 0f,
                    Ordinal = i,
                })
                .Where(s => s.Demand != null && s.Remaining > 0f)
                .ToList();
            ColdStates = DeferFreshZeroRadar
                ? AllStates.Where(s => StrategicPhaseA.ShouldDeferFreshZeroRadarDemand(
                    Radar, s.Demand, ActiveIntents)).ToList()
                : new List<DemandState>();
            States = AllStates.Where(s => !s.Demand.IsPersistenceDeferred
                && !ColdStates.Contains(s)).ToList();
            DeferredStates = AllStates.Where(s => s.Demand.IsPersistenceDeferred
                && !ColdStates.Contains(s)).ToList();
            if (ColdStates.Count > 0)
                AiDebugLog.Write($"[AI][V2]   strat.A radar - defer {ColdStates.Count} fresh zero-priority demand(s) until operational/tempo admission finishes");
        }

        public bool NothingToAdmit => States.Count == 0 && DeferredStates.Count == 0;

        // Materialization demands still open this round (arbitrated infrastructure bids separately).
        public List<DemandState> ActiveMaterializationStates() => States.Where(s => !s.Blocked
            && s.Remaining > 0f && !ArbitratedInfrastructure.Contains(s)).ToList();

        // A global-source demand (AxisDemand.EconomySourceCard) is about the card being in
        // play, not about who played it: a carrier Hero that another demand already fielded
        // (leading an army, delivering a build) or a Facility already placed closes it.
        public void CloseSourceDemandsAlreadyInPlay()
        {
            foreach (DemandState s in AllStates)
            {
                CardData source = s.Demand.EconomySourceCard;
                if (source == null || s.Remaining <= 0f || Hand.Hand.Contains(source))
                    continue;
                s.Remaining = 0f;
                AiDebugLog.Write($"[AI][V2]   strat.A - {s.Demand}: source card already left the "
                    + "hand through another play; global-source demand closed");
            }
        }

        // Persistence-gate reconciliation (spec "Bootstrap & No-Alternative-Work Escape", Rule 2).
        // Called only at a point where the normal per-turn arbitration loop is about to give up —
        // every currently active (non-deferred) demand is satisfied, blocked, or has no feasible
        // chain this round. That is exactly "no other actionable work exists to prefer instead" in
        // Phase A's own frame, so any persistence-deferred demand gets one real shot: if it has a
        // legal/affordable/operationally-deliverable candidate RIGHT NOW (the same TopForDemand
        // query every other demand uses — never a phantom fulfillment, AC7), it is moved into the
        // normal `States` pool and competes there through the ordinary candidate scoring (AC10);
        // otherwise it is left deferred and the caller proceeds to Phase B as usual (AC7/AC8).
        // Every deferred demand is tried once per call so several axes can each get a fair chance
        // in the same reconciliation window; promotion never re-derives runnable-opportunity or
        // capacity facts — those were already established once by the emitting axis (DemandLayer).
        public bool TryPromotePersistenceDeferred()
        {
            if (DeferredStates.Count == 0)
                return false;

            bool promotedAny = false;
            CapabilityInventory inv = CapabilityInventory.Build(Snap, Player, Commitments);
            foreach (DemandState ds in DeferredStates.ToList())
            {
                // §6/§7 — Demand already measures its persistence-gate deficit against EXISTING
                // FEASIBLE capacity only (desired - existing feasible), never against future mission
                // funding (AxisDemand no longer even carries ExistingUsableCapacityAtEmission — the
                // pre-ledger/post-ledger composite that lived here, FundedActionableNow, is removed
                // entirely per the architecture rule against a third concept combining CAPABILITY and
                // FUNDING). So the only question left for the reconciliation window is the generic
                // one every other demand answers the same way: is there a legal/affordable/
                // deliverable candidate for it RIGHT NOW (AC7) — never a phantom fulfillment.
                List<DemandCandidate> top = MaterializationCandidateBuilder.TopForDemand(Snap, Player, Root, Hand,
                    Ctx, ds.Demand, ApBudget, Commitments, ApBudget.ReservedFollowup(),
                    Reservation, inv, hasCompetingHeroDemand: false, AiConfigV2.phaseATopK,
                    witnessedUsefulApDemand: WitnessedUsefulApDemand, activeIntents: ActiveIntents);
                if (top.Count == 0)
                {
                    // Stays a VALID, UNRESOLVED, temporarily-unfulfillable demand — must remain in
                    // DeferredStates (not removed) so it still reaches Reservation.UnresolvedDemands
                    // below. Removing it here would silently turn "no candidate right now" into
                    // "no demand ever existed", losing the residual for Phase B and next turn's log.
                    AiDebugLog.Write($"[AI][V2]   strat.A persistence-reconcile - {ds.Demand}: "
                        + "decision=DEFER reason=no_deliverable_candidate; stays deferred, Phase B proceeds");
                    continue;
                }
                AiDebugLog.Write($"[AI][V2]   strat.A persistence-reconcile - {ds.Demand}: "
                    + $"decision=PROMOTE previousGate=persistence reason=no_alternative_actionable_work "
                    + $"deliverableCandidates={top.Count} best={top[0].Plan.StableKey}");
                // A promoted demand IS now an ordinary actionable demand — clear the flag on the
                // SAME AxisDemand instance so residual reporting / Phase B's UnresolvedClaimFor
                // cannot tell it apart from a demand that was never persistence-gated at all.
                ds.Demand.IsPersistenceDeferred = false;
                DeferredStates.Remove(ds);
                States.Add(ds);
                promotedAny = true;
            }
            return promotedAny;
        }

        // The residual pool is shared by every entry of the turn (the carried reservation). An
        // entry rewrites only the residuals of the axes it re-evaluated — owner decision
        // 2026-10-24: a dirty-axis re-admission must not erase another axis' unmet need, and an
        // admitted axis with no demand left has no residual.
        private void DropOwnResiduals() =>
            Reservation.UnresolvedDemands.RemoveAll(d => d == null || AdmittedAxes.Contains(d.RequestingAxis));

        // The early exit (nothing to admit now): only deferred cold work is carried.
        public void PublishEarlyResiduals()
        {
            DropOwnResiduals();
            foreach (DemandState cold in ColdStates)
                Reservation.UnresolvedDemands.Add(CloneResidualDemand(cold));
        }

        public void PublishResiduals()
        {
            DropOwnResiduals();
            // A residual Phase A has structurally no way to deliver keeps no claim on hand cards
            // (AxisDemand.StructurallyUndeliverable): judged on the end-of-pass world, one read
            // per residual, AP/resource shortfalls excluded (that is timing, the claim stays).
            foreach (DemandState state in States.Where(s => s.Remaining > 0f && !s.Demand.AttackLocalRefit))
            {
                if (!state.Demand.StructurallyUndeliverable)
                {
                    MaterializationDeliveryAvailability availability =
                        MaterializationCandidateBuilder.OperationalDeliveryAvailabilityForDemand(
                            Snap, Player, Root, Hand, Ctx, state.Demand, Commitments, Reservation,
                            ActiveIntents);
                    state.Demand.StructurallyUndeliverable = availability.StructurallyUndeliverable;
                }
                Reservation.UnresolvedDemands.Add(CloneResidualDemand(state));
            }
            // Deferred demands never promoted this pass (no runnable window to try them, or no
            // deliverable candidate — AC7) are still real unmet strategic need; carry them into the
            // same residual pool the reaction pass / Phase B read, so a later chance this turn is not
            // treated as if the need never existed.
            foreach (DemandState state in DeferredStates.Where(s => s.Remaining > 0f && !s.Demand.AttackLocalRefit))
                Reservation.UnresolvedDemands.Add(CloneResidualDemand(state));
            foreach (DemandState cold in ColdStates.Where(s => s.Remaining > 0f && !s.Demand.AttackLocalRefit))
                Reservation.UnresolvedDemands.Add(CloneResidualDemand(cold));
        }

        private static AxisDemand CloneResidualDemand(DemandState state)
        {
            AxisDemand d = state.Demand;
            return new AxisDemand
            {
                StructurallyUndeliverable = d.StructurallyUndeliverable,
                TraceId = d.TraceId,
                RequestingAxis = d.RequestingAxis,
                Value = d.Value,
                WorldTaskScore = d.WorldTaskScore,
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
                EconomyHeroOpportunityCost = d.EconomyHeroOpportunityCost,
                EconomyAssignmentApCost = d.EconomyAssignmentApCost,
                EconomyPaybackTurns = d.EconomyPaybackTurns,
                EconomySourceCard = d.EconomySourceCard,
                EconomySwitchIncumbentValue = d.EconomySwitchIncumbentValue,
                EconomyReadyDeliveryCost = d.EconomyReadyDeliveryCost,
                EconomyReadyBuilderIsSupport = d.EconomyReadyBuilderIsSupport,
                EconomyPreferredBuilderArmyId = d.EconomyPreferredBuilderArmyId,
                EconomyEscortArmyId = d.EconomyEscortArmyId,
                EconomyBuilderRoutes = d.EconomyBuilderRoutes,
                RequiredCapabilityPower = d.RequiredCapabilityPower,
                AttackFistArmyId = d.AttackFistArmyId,
                AttackFistIsPreparationHost = d.AttackFistIsPreparationHost,
                AttackObjectiveHex = d.AttackObjectiveHex,
                AttackLocalRefit = d.AttackLocalRefit,
                AttackCoverageGap = d.AttackCoverageGap,
                AttackCoverageTargetHex = d.AttackCoverageTargetHex,
                DeliveryShape = d.DeliveryShape,
                ConsumerIntentKey = d.ConsumerIntentKey,
                Explain = d.Explain,
                IsPersistenceDeferred = d.IsPersistenceDeferred,
            };
        }
    }
}
