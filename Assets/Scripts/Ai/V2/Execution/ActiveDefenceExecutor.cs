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
        internal static bool TryMaterializeSortie(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, WorldSnapshot snapshot)
        {
            if (pm?.Kind != MissionKind.ActiveDefence || !pm.ActiveDefenceTarget.RequiresSortieMaterialization) return false;
            GarrisonSortiePlan plan = pm.ActiveDefenceTarget.Sortie;
            result.StartHex = result.FinalHex = plan.StrongholdHex;
            var commitments = ActorCommitments.FromIntents(WorldSnapshot.IntentsOf(snapshot), snapshot, null);
            if (!ActiveDefenceProvisioner.TryResolveSortie(player, root, ctx, pm.ActiveDefenceTarget,
                commitments, pm.ClaimedAp, out ArmyData donor, out ArmyData shell, out List<UnitData> units, out string why))
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated; result.NeedsReplan = true;
                AiDebugLog.Write($"[AI][V2][ActiveDefence][Sortie] rejected: {why}"); return true;
            }
            ArmyData actor = MaterializeDetachment(player, ctx, donor, shell, units, out why);
            if (actor == null)
            { result.StopReason = ExecutionStopReason.TargetInvalidated; result.NeedsReplan = true; return true; }
            pm.MoverArmyId = actor.Id; pm.ActiveDefenceTarget.PrimaryArmyId = actor.Id;
            pm.ActiveDefenceTarget.SortieMaterialized = true;
            result.ActorMaterialized = true; result.ActualActorArmyId = actor.Id;
            result.StopReason = ExecutionStopReason.StepCompleted;
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Sortie] actor=#{actor.Id} donor=#{donor.Id} bodies={units.Count}");
            return true;
        }

        // The ONE mutation of a resolved garrison sortie (ActiveDefence intercept and Attack's second
        // operation alike): the pinned bodies leave the garrison into the pinned empty shell, or
        // into one canonical new container on that hex. Null (world unchanged) when refused.
        internal static ArmyData MaterializeDetachment(PlayerSetupData player, AiTurnContext ctx,
            ArmyData donor, ArmyData shell, List<UnitData> units, out string why)
        {
            if (shell == null)
                return ArmyActions.CreateArmyWithMembers(player, donor.Hex,
                    ctx.StartingDeckCatalog.GetCatalog(player.Faction), donor, units, ctx.HexSelection, out why);
            return ArmyActions.TransferMembersAtomic(units, donor, shell, ctx.HexSelection, out why) ? shell : null;
        }

        internal static IEnumerator RunActiveDefence(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ArmyData initial = Resolve(player, pm.MoverArmyId);
            int limit = (initial?.CurrentMovement ?? 0) + 1;
            for (int i = 0; i < limit; ++i)
            {
                yield return RunActiveDefenceStepCore(player, ctx, pm, result);
                // A relief extraction is its own terminal step (one mutation per step).
                if (result.StopReason != ExecutionStopReason.StepCompleted || result.ActorMaterialized) break;
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
                if (pm.ActiveDefenceTarget.ReturnPurpose == ActiveDefenceReturnPurpose.GarrisonRelief
                    && pm.ActiveDefenceTarget.ReliefDonorGarrisonId.HasValue)
                {
                    ExtractGarrisonRelief(player, ctx, pm, army, result);
                    yield break;
                }
                if (army.Hex.Equals(home))
                {
                    result.ReachedGoal = true;
                    result.StopReason = ExecutionStopReason.ReachedGoal;
                    yield break;
                }
                var returnLeg = new GroundLegStepResult();
                yield return GroundCombatLegStep.Transit(player, ctx, army, home,
                    "V2 active defence - return", returnLeg, result);
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
                    if (pm.ActiveDefenceTarget.ReturnPurpose == ActiveDefenceReturnPurpose.GarrisonRelief)
                        DeliverGarrisonRelief(player, ctx, army, home);
                }
                else if (army == null) result.StopReason = ExecutionStopReason.MoverLost;
                else if (returnLeg.BattleOccurred) result.StopReason = ExecutionStopReason.BattleStarted;
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
            if (pm.ActiveDefenceTarget.IsMaterializedSortie
                && !ActiveDefenceProvisioner.TryEvaluateSortie(player, ctx, pm.ActiveDefenceTarget,
                    army.Hex, army.Members, out _))
            { result.StopReason = ExecutionStopReason.TargetInvalidated; result.NeedsReplan = true; yield break; }
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
            // An immediate opportunity is re-proved before EVERY step: the enemy must still be seen
            // now, and the fight must still be startable with the movement that is left. Otherwise
            // the intent ends here (Continuity releases it) and the army simply stays where it is.
            if (pm.ActiveDefenceTarget.InterceptPurpose == ActiveDefenceInterceptPurpose.ImmediateOpportunity)
            {
                bool seen = VisionSystem.IsVisible(player, targetHex);
                bool reach = seen && ActiveDefenceObjectiveEvaluator.CanReachNow(ctx.Map, player, army.Hex,
                    targetHex, army.CurrentMovement, army.MaxMovement, out _);
                if (!seen || !reach || pm.ActiveDefenceTarget.ImmediateTurn != ctx.TurnNumber)
                {
                    AiDebugLog.Write($"[AI][V2][ActiveDefence][Immediate][Execution] enemy={enemyId} "
                        + $"actor={army.Id} mp={army.CurrentMovement} visible={seen} reachable={reach} "
                        + "decision=END reason=immediate_opportunity_lost");
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    result.NeedsReplan = true;
                    yield break;
                }
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
                    $"V2 active defence - intercept enemy #{enemyId}", 0f, AiGroundMoveAuthority.CombatAndCapture), ctx, trace);
            army = Resolve(player, pm.MoverArmyId);
            HexCoord after = army != null ? army.Hex : trace.EndHex;
            bool moved = !after.Equals(before);
            WorldDeltaLifecycle.RecordExecutionMutation(result, moved || trace.BattleOccurred
                || trace.EnteredStealthThisStep);
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
            if (army == null)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
            }
            else if (destroyedInOurBattle)
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
            }
            else if (trace.BattleOccurred) result.StopReason = ExecutionStopReason.BattleStarted;
            else if (trace.HexEventOccurred) result.StopReason = ExecutionStopReason.HexEventStarted;
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

        // GarrisonRelief — the pinned extraction as one terminal step: the planned spare bodies move
        // from the donor garrison into the empty shell in one atomic transfer, after the same live
        // check Provisioning ran (ActiveDefenceProvisioner.TryResolveReliefExtraction). A refusal
        // mutates nothing. Success materializes the detachment: the step is progress, Continuity
        // makes the leg durable with the shell as its mover, and the walk is the next step.
        private static void ExtractGarrisonRelief(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ArmyData shell, ExecutionResult result)
        {
            result.FinalHex = shell.Hex;
            string why = null;
            if (!ActiveDefenceProvisioner.TryResolveReliefExtraction(player, pm.ActiveDefenceTarget, shell,
                    null, out ArmyData donor, out List<UnitData> units, out why)
                || !ArmyActions.TransferMembersAtomic(units, donor, shell, ctx?.HexSelection, out why))
            {
                AiDebugLog.Write($"[AI][V2][GarrisonRelief][Execution] extraction refused into #{shell.Id}: {why}");
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                return;
            }
            result.ActorMaterialized = true;
            result.ActualActorArmyId = shell.Id;
            result.StopReason = ExecutionStopReason.StepCompleted;
            AiDebugLog.Write($"[AI][V2][GarrisonRelief][Execution] extracted {units.Count} bodies from garrison "
                + $"#{donor.Id}@({donor.Hex.Q},{donor.Hex.R}) into #{shell.Id} for "
                + $"({pm.ActiveDefenceTarget.ReturnHex.Value.Q},{pm.ActiveDefenceTarget.ReturnHex.Value.R})");
        }

        // GarrisonRelief — the plan's last action on arrival: the detachment's bodies join the
        // recipient's garrison in one atomic transfer (the garrison's capacity rules apply; a body
        // that does not fit stays in the shell on the base, which still defends the hex).
        private static void DeliverGarrisonRelief(PlayerSetupData player, AiTurnContext ctx,
            ArmyData shell, HexCoord home)
        {
            ArmyData garrison = OwnWorld.Of(player).Armies.FirstOrDefault(a => a != null && a.IsGarrison
                && a.Hex.Equals(home));
            if (garrison == null || shell.Members.Count == 0)
                return;
            var bodies = shell.Members.Where(u => u != null).ToList();
            while (bodies.Count > 0
                && !ArmyActions.TransferMembersAtomic(bodies, shell, garrison, ctx?.HexSelection, out _))
                bodies.RemoveAt(bodies.Count - 1);
            AiDebugLog.Write($"[AI][V2][GarrisonRelief][Execution] delivered {bodies.Count}/"
                + $"{bodies.Count + shell.Members.Count} bodies of #{shell.Id} into garrison #{garrison.Id}@({home.Q},{home.R})");
        }

        private static void FinishActiveDefence(PlayerSetupData player, PlayerRoot root,
            ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            result.FinalHex = Resolve(player, pm?.MoverArmyId ?? -1)?.Hex ?? result.FinalHex;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Execution] enemy={pm?.ActiveDefenceTarget.EnemyArmyId} "
                + $"actor={pm?.MoverArmyId} intercept={pm?.ActiveDefenceTarget.InterceptPurpose} "
                + $"purpose={pm?.ActiveDefenceTarget.ReturnPurpose} "
                + $"destination={pm?.ActiveDefenceTarget.ReturnHex} steps={result.StepsMoved} "
                + $"apBefore={apBefore} apSpent={result.ApSpent} apAfter={root?.ActionPoints} stop={result.StopReason}");
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);
    }
}

