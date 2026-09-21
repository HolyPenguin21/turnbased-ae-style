using System.Linq;
using Game.Cards;
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

            if (pm.Kind == MissionKind.Development)
            {
                DevelopmentMissionTarget target = pm.DevelopmentTarget;
                if (target.Hero == null || !mover.Members.Contains(target.Hero)
                    || !string.Equals(target.HeroKey,
                        GenerationSource.StableHeroKey(target.Hero),
                        System.StringComparison.Ordinal)
                    || target.Hero.Owner != player || target.Hero.IsPrisoner
                    || !target.Hero.HasAbility(ResearchProductionSystem.RoleAbility(target.Mode)))
                    return MissionValidity.StaleMoverLost;
                BuildingData building = BuildingRegistry.FindAt(target.FacilityHex);
                if (building == null || building.Owner != player
                    || !building.HasFacilityWithAbility(
                        ResearchProductionSystem.FacilityAbility(target.Mode)))
                    return MissionValidity.StaleTargetInvalidated;
                // Only this exact Hero completing the actual gameplay eligibility is success.
                // A different operator at the site may justify retiring an unnecessary mission,
                // but must never silently substitute for the bound actor.
                if (ResearchProductionSystem.ActorStillQualifies(
                        player, target.Hero, target.FacilityHex, target.Mode)
                    && ResearchProductionSystem.IsEligible(player,
                        target.FacilityHex, target.Mode, out _))
                    return MissionValidity.StaleGoalMet;
                if (mover.Hex.Equals(target.FacilityHex))
                    return MissionValidity.StaleTargetInvalidated;
                if (Game.Combat.BattleInitiator.FindEnemyAt(target.FacilityHex, player) != null)
                    return MissionValidity.StaleTargetInvalidated;
                if (root != null && !mover.HasActivatedThisTurn
                    && root.ActionPoints < mover.ActivationApCost)
                    return MissionValidity.StaleUnaffordable;
                return MissionValidity.Valid;
            }

            if (root != null && !mover.HasActivatedThisTurn && mover.ActivationApCost > 0
                && root.ActionPoints < mover.ActivationApCost)
                return MissionValidity.StaleUnaffordable;

            if (pm.Kind == MissionKind.Raid)
            {
                if (pm.RaidPhase == RaidMissionPhase.Assault
                    && RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTarget))
                    return MissionValidity.StaleGoalMet;
                return MissionValidity.Valid;
            }

            if (pm.Kind == MissionKind.ActiveDefence)
            {
                ActiveDefenceMissionTarget target = pm.ActiveDefenceTarget;
                if (target.Phase == ActiveDefencePhase.Return)
                    return target.ReturnHex.HasValue && mover.Hex.Equals(target.ReturnHex.Value)
                        ? MissionValidity.StaleGoalMet : MissionValidity.Valid;
                bool exists = ArmyRegistry.AllOccupiedHexes().SelectMany(ArmyRegistry.AllAt)
                    .Any(a => a != null && a.Id == target.EnemyArmyId
                        && a.Owner != null && a.Owner != player && !a.Owner.IsNeutral);
                return exists ? MissionValidity.Valid : MissionValidity.StaleGoalMet;
            }

            if (pm.Kind == MissionKind.Economy)
            {
                if (pm.Mission?.FromDurableIntent == true
                    && pm.Mission.PreferredMoverArmyId.HasValue
                    && pm.Mission.PreferredMoverArmyId.Value != mover.Id)
                    return MissionValidity.StaleMoverLost;
                if (MissionOutcomeLedger.EconomyObjectiveSatisfied(player, pm.EconomyTarget))
                    return MissionValidity.StaleGoalMet;
                if (pm.EconomyTarget.Kind == EconomyTaskKind.ReturnCollector)
                    return pm.EconomyTarget.CollectorArmyId == mover.Id
                        ? MissionValidity.Valid
                        : MissionValidity.StaleMoverLost;
                if (pm.EconomyTarget.Kind == EconomyTaskKind.MobileCollection)
                    return pm.EconomyTarget.CollectorArmyId == mover.Id
                        ? MissionValidity.Valid
                        : MissionValidity.StaleMoverLost;
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
