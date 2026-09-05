using System.Linq;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON OPERATIONAL FEASIBILITY — canonical seam
    // ===========================================================================================
    //  Splits "can this actor do this Recon job at all" into the two questions the pipeline order
    //  actually admits answers to:
    //
    //    OperationallyFeasibleIfFunded — pre-ledger. Everything knowable before AxisBudgetLedger
    //      exists: actor alive/owned/role-capable, not durable-committed elsewhere, target still
    //      runnable, a path/vantage exists. This is the ONLY question DemandLayer is allowed to ask
    //      (it runs before AxisBudgetLedger.Create — see AiTurnController pipeline order). A
    //      reachable path is necessary but not sufficient for this: it is the full set of
    //      objectively-known-in-advance constraints, not a synonym for pathing.
    //
    //    FundedActionableNow — post-ledger. Whether the requesting axis actually holds AP on the
    //      per-turn AxisBudgetLedger right now. root.ActionPoints is NEVER used here — physical AP
    //      existing globally does not mean THIS axis's entitlement covers the work (another axis
    //      may own all of it). Only meaningful once AxisBudgetLedger.Create has run.
    //
    //  ReconActorReservationPlanner (the real reservation planner) and DemandLayer (witness /
    //  persistence-gate capacity) both call OperationallyFeasibleIfFunded — ONE feasibility model,
    //  not two independently-drifting definitions. StrategicPhaseA's persistence-gate reconciliation
    //  is the only FundedActionableNow caller (it runs after AxisBudgetLedger.Create).
    // ===========================================================================================

    public enum ReconFeasibilityBlockReason
    {
        None,
        ActorMissing,
        NoRoute,
        NoReachableVantage,
    }

    public readonly struct ReconOperationalFeasibilityResult
    {
        public readonly bool FeasibleIfFunded;
        public readonly ReconFeasibilityBlockReason BlockReason;

        public ReconOperationalFeasibilityResult(bool feasible, ReconFeasibilityBlockReason reason)
        {
            FeasibleIfFunded = feasible;
            BlockReason = reason;
        }

        public static readonly ReconOperationalFeasibilityResult Feasible =
            new ReconOperationalFeasibilityResult(true, ReconFeasibilityBlockReason.None);

        public static ReconOperationalFeasibilityResult Blocked(ReconFeasibilityBlockReason reason) =>
            new ReconOperationalFeasibilityResult(false, reason);
    }

    public static class ReconOperationalFeasibility
    {
        // Pre-ledger. Actor uniqueness / concrete-job uniqueness / class-quota bookkeeping is the
        // CALLER's job (ReconActorReservationPlanner's room/matching, DemandLayer's joint bipartite
        // witness) — this seam only answers "this one actor, this one job, ignoring everyone else".
        public static ReconOperationalFeasibilityResult EvaluateIfFunded(AiTurnContext ctx,
            PlayerSetupData player, WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (mover == null)
                return ReconOperationalFeasibilityResult.Blocked(ReconFeasibilityBlockReason.ActorMissing);
            if (ctx?.Map == null)
                return ReconOperationalFeasibilityResult.Feasible;

            ArmyData live = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == mover.ArmyId);
            if (live == null)
                return ReconOperationalFeasibilityResult.Blocked(ReconFeasibilityBlockReason.ActorMissing);

            if (target.Kind != ScoutTargetKind.Surveil)
                return SafeStepPathing.FindNextSafeStep(ctx.Map, live, target.FocusHex) != null
                    ? ReconOperationalFeasibilityResult.Feasible
                    : ReconOperationalFeasibilityResult.Blocked(ReconFeasibilityBlockReason.NoRoute);

            foreach (SurveilVantageCandidate v in SurveilVantageSelector.Rank(snap, mover, target))
                if (SafeStepPathing.FindNextSafeStep(ctx.Map, live, v.ExecutionHex) != null)
                    return ReconOperationalFeasibilityResult.Feasible;
            return ReconOperationalFeasibilityResult.Blocked(ReconFeasibilityBlockReason.NoReachableVantage);
        }

        public static bool OperationallyFeasibleIfFunded(AiTurnContext ctx, PlayerSetupData player,
            WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target) =>
            EvaluateIfFunded(ctx, player, snap, mover, target).FeasibleIfFunded;

        // Post-ledger. Requires AxisBudgetLedger.Create to already have run this turn — callers
        // before that point (DemandLayer) must never call this; there is nothing to read yet.
        // A positive balance is a necessary, not sufficient, condition (the mission allocator still
        // owns the rest of Pack()/fundability) — this is deliberately a coarse "is there anything
        // here at all for this axis" gate for the persistence-gate boundary, not a private allocator.
        public static bool FundedActionableNow(AxisBudgetLedger ledger, DesireAxis requestingAxis)
        {
            if (ledger == null)
                return false;
            float eps = Mathf.Max(0.0001f, AiConfigV2.allocatorSliceEpsilon);
            return ledger.Balance(requestingAxis) > eps;
        }
    }
}
