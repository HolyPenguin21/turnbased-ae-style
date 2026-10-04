using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.Aviation;
using UnityEngine;

namespace Game.Ai.V2
{
    internal static class ActiveDefenceExecutor
    {
        internal static IEnumerator RunActiveDefence(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ArmyData initial = Resolve(player, pm.MoverArmyId);
            int limit = (initial?.CurrentMovement ?? 0) + 1;
            for (int i = 0; i < limit; ++i)
            {
                yield return RunActiveDefenceStepCore(player, ctx, pm, result);
                if (result.StopReason != ExecutionStopReason.StepCompleted) break;
                // An air-support series that turned home has handed the wing to aviation recovery.
                if (pm.ActiveDefenceTarget.Phase == ActiveDefencePhase.AirSupport
                    && !GroundCombatAirSupport.SortieLive(player, pm.MoverArmyId, out _)) break;
            }
            FinishActiveDefence(player, root, pm, result, apBefore);
        }

        internal static IEnumerator RunActiveDefenceStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            yield return RunActiveDefenceStepCore(player, ctx, pm, result);
            FinishActiveDefence(player, root, pm, result, apBefore);
        }

        private static IEnumerator RunActiveDefenceStepCore(PlayerSetupData player,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result)
        {
            ArmyData army = Resolve(player, pm.MoverArmyId);
            if (army == null || army.Owner != player)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            if (ctx?.Map == null || ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
            {
                result.StopReason = ctx?.Map == null
                    ? ExecutionStopReason.TargetInvalidated : ExecutionStopReason.BattleStarted;
                yield break;
            }

            if (pm.ActiveDefenceTarget.Phase == ActiveDefencePhase.AirSupport)
            {
                yield return RunAirSupportStep(player, ctx, pm, result, army);
                yield break;
            }

            if (pm.ActiveDefenceTarget.Phase == ActiveDefencePhase.Return)
            {
                if (!pm.ActiveDefenceTarget.ReturnHex.HasValue)
                {
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    result.NeedsReplan = true;
                    yield break;
                }
                HexCoord home = pm.ActiveDefenceTarget.ReturnHex.Value;
                if (army.Hex.Equals(home))
                {
                    result.ReachedGoal = true;
                    result.StopReason = ExecutionStopReason.ReachedGoal;
                    yield break;
                }
                var returnLeg = new GroundLegStepResult();
                yield return GroundCombatLegStep.Transit(player, ctx, army, home,
                    "V2 active defence — return", returnLeg);
                if (returnLeg.Blocked.HasValue)
                {
                    result.StopReason = returnLeg.Blocked.Value;
                    result.NeedsReplan = returnLeg.NeedsReplan;
                    yield break;
                }
                army = returnLeg.Army;
                if (returnLeg.Moved) result.StepsMoved++;
                result.FinalHex = returnLeg.EndHex;
                result.ActualActorArmyId = pm.MoverArmyId;
                if (army != null && army.Hex.Equals(home))
                {
                    result.ReachedGoal = true;
                    result.StopReason = ExecutionStopReason.ReachedGoal;
                }
                else if (returnLeg.BattleOccurred) result.StopReason = ExecutionStopReason.BattleStarted;
                else if (army == null) result.StopReason = ExecutionStopReason.MoverLost;
                else if (!returnLeg.Moved) result.StopReason = ExecutionStopReason.MoveRejected;
                else result.StopReason = army.CurrentMovement > 0
                    ? ExecutionStopReason.StepCompleted : ExecutionStopReason.OutOfMovement;
                yield break;
            }

            int enemyId = pm.ActiveDefenceTarget.EnemyArmyId;
            // The canonical sighting store below is the ONLY admissible source of strategic
            // knowledge about this enemy — never a global ArmyRegistry sweep, which would let an
            // enemy destroyed somewhere the player never observed "complete" the interception.
            // Absence of honest knowledge is TargetInvalidated (a lifecycle question
            // Continuity answers), never a confirmed objective.
            AiMapMemory.KnownEnemySighting? witness = AiMapMemory.AllKnownEnemySightings(player)
                .Where(s => s.ArmyId == enemyId && s.Owner != null && s.Owner != player
                    && !s.Owner.IsNeutral)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            if (!witness.HasValue)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }
            HexCoord targetHex = witness.Value.Hex;
            pm.ExecutionHex = targetHex;
            pm.ActiveDefenceTarget.LastKnownHex = targetHex;
            pm.ActiveDefenceTarget.LastObservedTurn = witness.Value.SeenTurn;
            if (army.Hex.Equals(targetHex))
            {
                result.StopReason = ExecutionStopReason.EnemyDiscovered;
                yield break;
            }
            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, targetHex,
                profile: SafeRouteProfile.Combat);
            if (!next.HasValue)
            {
                result.StopReason = ExecutionStopReason.NoSafeStep;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord before = army.Hex;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player,
                AiDecision.Move(army, next.Value,
                    $"V2 active defence — intercept enemy #{enemyId}", 0f, AiGroundMoveAuthority.CombatAndCapture), ctx, trace);
            army = Resolve(player, pm.MoverArmyId);
            HexCoord after = army != null ? army.Hex : trace.EndHex;
            bool moved = !after.Equals(before);
            if (moved) result.StepsMoved++;
            result.FinalHex = after;
            result.ActualActorArmyId = pm.MoverArmyId;
            result.CombatChanged |= trace.BattleOccurred;

            // Objective completion may only be read from the outcome of the
            // canonical gameplay operation this step just performed. The encounter-resolved
            // trace supplies BOTH the specific participant identity and its terminal fate;
            // a global registry sweep cannot prove either one, even if some battle occurred.
            // With no battle, this step proves nothing about the enemy's existence —
            // it just moved — and the honest sighting store keeps owning what we know.
            bool destroyedInOurBattle = trace.BattleOccurred
                && trace.WasDestroyedInOwnBattle(enemyId);
            if (destroyedInOurBattle)
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
            }
            else if (trace.BattleOccurred) result.StopReason = ExecutionStopReason.BattleStarted;
            else if (trace.HexEventOccurred) result.StopReason = ExecutionStopReason.HexEventStarted;
            else if (army == null) result.StopReason = ExecutionStopReason.MoverLost;
            else if (!moved) result.StopReason = ExecutionStopReason.MoveRejected;
            else result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted : ExecutionStopReason.OutOfMovement;
        }

        // The wing's strike-series step against the threat at its honest current hex
        // (re-read from memory every step: a moving threat is followed). Nothing is read from a
        // global registry or a projection: only the real strike changes the world, and its
        // published observation is what the next pass learns from.
        private static IEnumerator RunAirSupportStep(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, ArmyData wing)
        {
            int enemyId = pm.ActiveDefenceTarget.EnemyArmyId;
            AiMapMemory.KnownEnemySighting? witness = AiMapMemory.AllKnownEnemySightings(player)
                .Where(s => s.ArmyId == enemyId && s.Owner != null && s.Owner != player
                    && !s.Owner.IsNeutral)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            if (!AviationRules.IsValidAirArmy(wing) || !witness.HasValue
                || !pm.ActiveDefenceTarget.AirSupportLandingHex.HasValue)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }
            HexCoord targetHex = witness.Value.Hex;
            pm.ExecutionHex = targetHex;
            pm.ActiveDefenceTarget.LastKnownHex = targetHex;
            pm.ActiveDefenceTarget.LastObservedTurn = witness.Value.SeenTurn;
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            yield return GroundCombatLegStep.AirStrikeSortie(player, root, ctx, pm, result, wing,
                targetHex, pm.ActiveDefenceTarget.AirSupportLandingHex.Value,
                AirStrikePolicy.DefenceSupport(enemyId), "DefenceSupport",
                $"flies to strike threat #{enemyId}");
            // The real strike publishes the hex (VisionSystem.NotifyContentChanged); whether the
            // threat is gone is then read from honest memory by Continuity on the next pass.
        }

        private static void FinishActiveDefence(PlayerSetupData player, PlayerRoot root,
            ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            result.FinalHex = Resolve(player, pm?.MoverArmyId ?? -1)?.Hex ?? result.FinalHex;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Execution] enemy={pm?.ActiveDefenceTarget.EnemyArmyId} "
                + $"actor={pm?.MoverArmyId} steps={result.StepsMoved} stop={result.StopReason}");
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);
    }
}
