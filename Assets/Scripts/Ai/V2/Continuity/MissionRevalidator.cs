using System.Linq;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MISSION REVALIDATOR  (Strategy V2 — live mission revalidation between provisioned missions)
    // ===========================================================================================
    //  Provisioning is a batch; this is the live gate before every mission. Generic Refresh is
    //  explicitly distinct from Explore: a previously VISITED hex can still be a valid stale-info
    //  objective, and only observing it again completes that Refresh.
    // ===========================================================================================
    internal enum MissionValidity
    {
        Valid,
        StaleGoalMet,
        StaleTargetInvalidated,
        StaleMoverLost,
        StaleUnaffordable,
    }

    internal static class MissionRevalidator
    {
        public static bool IsStale(MissionValidity v) => v != MissionValidity.Valid;

        public static MissionValidity Validate(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm)
        {
            if (pm == null)
                return MissionValidity.StaleMoverLost;

            ArmyData mover = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == pm.MoverArmyId);
            if (mover == null || mover.Owner != player)
                return MissionValidity.StaleMoverLost;

            if (pm.Kind == MissionKind.Scout && !AiArmyRoles.IsSoloRecce(mover))
                return MissionValidity.StaleMoverLost;

            if (root != null && !mover.HasActivatedThisTurn && mover.ActivationApCost > 0
                && root.ActionPoints < mover.ActivationApCost)
                return MissionValidity.StaleUnaffordable;

            if (pm.Kind == MissionKind.Raid)
            {
                if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTargetArmyId))
                    return MissionValidity.StaleGoalMet;
                return MissionValidity.Valid;
            }

            if (pm.Kind == MissionKind.Economy)
            {
                if (MissionOutcomeLedger.EconomyObjectiveSatisfied(player, pm.EconomyTarget))
                    return MissionValidity.StaleGoalMet;
                if (pm.EconomyTarget.Kind == EconomyTaskKind.ReturnBuilder)
                    return pm.EconomyTarget.BuilderArmyId == mover.Id
                        ? MissionValidity.Valid
                        : MissionValidity.StaleMoverLost;
                if (pm.EconomyTarget.Kind == EconomyTaskKind.FoundBase
                    && pm.EconomyTarget.BuildCard == null)
                    return MissionValidity.StaleTargetInvalidated;
                return MissionValidity.Valid;
            }

            if (ReconScoutKinds.IsSurveil(pm.ScoutKind))
            {
                if (ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId,
                        pm.BaselineObservedTurn))
                    return MissionValidity.StaleGoalMet;
                if (ctx != null && ScoutExecutionSafety.VantageBlockedNow(player, pm.ExecutionHex, ctx.TurnNumber))
                    return MissionValidity.StaleTargetInvalidated;
                return MissionValidity.Valid;
            }

            if (ReconScoutKinds.IsRefresh(pm.ScoutKind))
            {
                if (ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, pm.FocusHex))
                    return MissionValidity.StaleGoalMet;
                if (AiMapMemory.KnownEnemySightingAt(player, pm.ExecutionHex).HasValue)
                    return MissionValidity.StaleTargetInvalidated;
                return MissionValidity.Valid;
            }

            // Never silently reinterpret a future/invalid Scout kind as Explore. The enum has three
            // explicit semantics and every lifecycle stage must reject values it does not understand.
            if (!ReconScoutKinds.IsExplore(pm.ScoutKind))
            {
                AiDebugLog.Write($"[AI][V2][Recon] revalidate reject — unknown Scout kind {(int)pm.ScoutKind}");
                return MissionValidity.StaleTargetInvalidated;
            }

            // Explore only. Physical visitation is completion here; generic Refresh intentionally
            // does NOT share this shortcut.
            if (VisionSystem.IsVisited(player, pm.ExecutionHex))
                return MissionValidity.StaleGoalMet;
            if (AiMapMemory.KnownEnemySightingAt(player, pm.ExecutionHex).HasValue)
                return MissionValidity.StaleTargetInvalidated;
            return MissionValidity.Valid;
        }
        public static bool WasAttempt(ExecutionResult r) => r != null;

        public static bool WasGenuineExecution(ExecutionResult r) =>
            r != null && r.Outcome.Succeeded && r.Outcome.StateChanged;

        public static bool WasStaleOrSkipped(ExecutionResult r) =>
            r != null && r.StepsMoved == 0 && r.ApSpent <= Mathf.Epsilon;
    }
}
