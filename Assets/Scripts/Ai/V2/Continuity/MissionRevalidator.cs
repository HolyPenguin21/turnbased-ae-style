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

            // Return is a noncapturing obligation, not a substitute Assault/LocalCapture.
            // Validate its pinned destination using only THIS player's observed ownership;
            // a foreign building learned about since Provisioning must hand the intent back
            // to Continuity, never become an incidental capture or a completed return.
            bool raidReturn = pm.Kind == MissionKind.Raid
                && (pm.RaidPhase == RaidMissionPhase.Return
                    || pm.RaidPhase == RaidMissionPhase.SupportReturn
                    || pm.RaidPhase == RaidMissionPhase.RecoveryReturn);
            bool defenceReturn = pm.Kind == MissionKind.ActiveDefence
                && pm.ActiveDefenceTarget.Phase == ActiveDefencePhase.Return;
            // ATK §47 — Attack's two walking-home legs are the same noncapturing obligation and get
            // the same check: their pinned destination must still be OURS by this player's own
            // observation, or the leg goes back to Continuity instead of becoming an incidental
            // capture of a base that changed hands while the army was in transit.
            bool attackReturn = pm.Kind == MissionKind.Attack
                && (pm.AttackTarget.Phase == AttackMissionPhase.RecoveryReturn
                    || pm.AttackTarget.Phase == AttackMissionPhase.SupportReturn);
            if (raidReturn || defenceReturn || attackReturn)
            {
                Game.HexGrid.HexCoord? home = raidReturn
                    ? pm.RaidDestinationHex
                    : attackReturn
                        ? pm.AttackTarget.DestinationHex
                        : pm.ActiveDefenceTarget.ReturnHex;
                if (!home.HasValue)
                    return MissionValidity.StaleTargetInvalidated;
                AiMapMemory.KnownBuilding? remembered = AiMapMemory.KnownBuildingAt(player, home.Value);
                if (remembered.HasValue && remembered.Value.Owner != player)
                    return MissionValidity.StaleTargetInvalidated;
            }

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
                // Knowledge of an enemy army is AiMapMemory's, never a global ArmyRegistry sweep:
                // an intercept ends because WE learned something, not because the army vanished
                // from the world, and the provisioner for the same mission reads the same
                // sightings.
                return ActiveDefenceObjectiveEvaluator.IsObjectiveSatisfiedLive(
                        player, target.EnemyArmyId)
                    ? MissionValidity.StaleGoalMet : MissionValidity.Valid;
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

            // ATK §25 — Attack needs its OWN branch, like every other non-Scout kind. Without one it
            // fell through to the Scout rules below, where an Attack target hex we have obviously
            // already seen reads as a satisfied Explore objective (VisionSystem.IsVisited) and the
            // assault is skipped as stale on every single step.
            if (pm.Kind == MissionKind.Attack)
            {
                AttackMissionTarget attack = pm.AttackTarget;
                if (attackReturn)
                    return mover.Hex.Equals(attack.DestinationHex)
                        ? MissionValidity.StaleGoalMet : MissionValidity.Valid;
                // Reinforcement is a rendezvous with the primary, not a fight with the site: its
                // validity is the primary's, and the executor re-reads the meeting hex itself.
                if (attack.Phase == AttackMissionPhase.Reinforcement)
                    return attack.PrimaryArmyId.HasValue
                        && ArmyRegistry.AllForOwner(player).Any(a =>
                            a.Id == attack.PrimaryArmyId.Value && a.Members.Count > 0)
                        ? MissionValidity.Valid : MissionValidity.StaleTargetInvalidated;
                // Assault: the one §25 owner answers whether this is still the thing we set out to
                // capture, from live own-Base truth plus this player's own honest memory.
                switch (AttackObjectiveEvaluator.EvaluateTargetLive(player, attack.Target))
                {
                    case AttackObjectiveEvaluator.AttackTargetStatus.Captured:
                        return MissionValidity.StaleGoalMet;
                    case AttackObjectiveEvaluator.AttackTargetStatus.Invalidated:
                        return MissionValidity.StaleTargetInvalidated;
                    default:
                        return MissionValidity.Valid;
                }
            }

            if (ReconScoutKinds.IsSurveil(pm.ScoutKind))
            {
                if (ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId,
                        pm.BaselineObservedTurn))
                    return MissionValidity.StaleGoalMet;
                if (ctx != null && ScoutExecutionSafety.VantageBlockedNow(player, pm.ExecutionHex,
                        ctx.TurnNumber, pm.RequiresStealth))
                    return MissionValidity.StaleTargetInvalidated;
                return MissionValidity.Valid;
            }

            // AirSweep is an aviation pass toward a moving anchor; it is never goal-met by
            // observation and its route safety is re-proved by the air step director every step.
            if (ReconScoutKinds.IsAirSweep(pm.ScoutKind))
                return MissionValidity.Valid;

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
