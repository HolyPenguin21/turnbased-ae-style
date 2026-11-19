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
            var commitments = ActorCommitments.FromIntents(WorldSnapshot.IntentsOf(snapshot), snapshot, null);
            ArmyData actor = GarrisonSortieStep.Materialize(player, ctx, pm.ActiveDefenceTarget.Sortie, pm, result,
                "ActiveDefence", (out ArmyData d, out ArmyData s, out List<UnitData> u, out string w) =>
                    ActiveDefenceProvisioner.TryResolveSortie(player, root, ctx, pm.ActiveDefenceTarget,
                        commitments, pm.ClaimedAp, out d, out s, out u, out w),
                out ArmyData donor, out int bodies);
            if (actor == null) return true;
            pm.ActiveDefenceTarget.PrimaryArmyId = actor.Id;
            pm.ActiveDefenceTarget.SortieMaterialized = true;
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Sortie] actor=#{actor.Id} donor=#{donor.Id} bodies={bodies}");
            return true;
        }

        internal static IEnumerator RunActiveDefence(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ArmyData initial = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            int limit = GroundCombatLegStep.StepBudget(initial, pm) + 1;
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
            ArmyData army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            if (army == null || army.Owner != player)
            {
                result.StopForReplan(ExecutionStopReason.MoverLost);
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
                    result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                    yield break;
                }
                HexCoord home = pm.ActiveDefenceTarget.ReturnHex.Value;
                if (pm.ActiveDefenceTarget.ReturnPurpose == ActiveDefenceReturnPurpose.GarrisonRelief
                    && pm.ActiveDefenceTarget.ReliefDonorGarrisonId.HasValue)
                {
                    ExtractGarrisonRelief(player, ctx, pm, army, result);
                    yield break;
                }
                bool muster = pm.ActiveDefenceTarget.ReturnPurpose == ActiveDefenceReturnPurpose.Muster;
                if (muster && pm.ActiveDefenceTarget.ReliefDonorGarrisonId.HasValue)
                {
                    // Intake on the staging hex, else the Detach extraction on the donor's hex.
                    if (army.Hex.Equals(home))
                        MusterIntake(player, ctx, pm, army, result);
                    else
                        ExtractGarrisonRelief(player, ctx, pm, army, result);
                    yield break;
                }
                yield return GroundCombatLegStep.WalkTo(player, ctx, pm, result, army, home,
                    "V2 active defence - return", () =>
                    {
                        result.ReachedGoal = true;
                        result.StopReason = ExecutionStopReason.ReachedGoal;
                        // The relief detachment's bodies join the garrison: a roster change.
                        if (pm.ActiveDefenceTarget.ReturnPurpose == ActiveDefenceReturnPurpose.GarrisonRelief)
                            result.CombatChanged |= DeliverGarrisonRelief(player, ctx,
                                AiV2Util.ResolveArmy(player, pm.MoverArmyId), home) > 0;
                        // The muster mover joins the standing fist on the staging Base.
                        if (muster)
                            result.CombatChanged |= JoinFist(player, ctx,
                                AiV2Util.ResolveArmy(player, pm.MoverArmyId), home) > 0;
                    });
                yield break;
            }

            int enemyId = pm.ActiveDefenceTarget.EnemyArmyId;
            // The canonical sighting store below is the ONLY admissible source of strategic
            // knowledge about this enemy — never a global ArmyRegistry sweep, which would let an
            // enemy destroyed somewhere the player never observed "complete" the interception.
            // Absence of honest knowledge is TargetInvalidated (a lifecycle question
            // Continuity answers), never a confirmed objective.
            AiMapMemory.KnownEnemySighting? witness = AiMapMemory.KnownHostileSightingOf(player, enemyId);
            if (!witness.HasValue)
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                yield break;
            }
            if (pm.ActiveDefenceTarget.IsMaterializedSortie
                && !ActiveDefenceProvisioner.TryEvaluateSortie(player, ctx, pm.ActiveDefenceTarget,
                    army.Hex, army.Members, out _))
            { result.StopForReplan(ExecutionStopReason.TargetInvalidated); yield break; }
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
                // In sight, or still standing on the ruin it razed (the shared admission rule).
                bool seen = VisionSystem.IsVisible(player, targetHex)
                    || ActiveDefenceObjectiveEvaluator.OnFreshOwnRuin(player, ctx.TurnNumber, targetHex,
                        witness.Value.SeenTurn);
                bool reach = seen && ActiveDefenceObjectiveEvaluator.CanReachNow(ctx.Map, player, army.Hex,
                    targetHex, army.CurrentMovement, army.MaxMovement, out _);
                if (!seen || !reach || pm.ActiveDefenceTarget.ImmediateTurn != ctx.TurnNumber)
                {
                    AiDebugLog.Write($"[AI][V2][ActiveDefence][Immediate][Execution] enemy={enemyId} "
                        + $"actor={army.Id} mp={army.CurrentMovement} visible={seen} reachable={reach} "
                        + "decision=END reason=immediate_opportunity_lost");
                    result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                    yield break;
                }
            }
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, targetHex,
                profile: SafeRouteProfile.Combat);
            if (!next.HasValue)
            {
                result.StopForReplan(ExecutionStopReason.NoSafeStep);
                yield break;
            }

            HexCoord before = army.Hex;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player,
                AiDecision.Move(army, next.Value,
                    $"V2 active defence - intercept enemy #{enemyId}", 0f, AiGroundMoveAuthority.CombatAndCapture), ctx, trace);
            army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
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
                result.StopForReplan(ExecutionStopReason.MoverLost);
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
            AiMapMemory.KnownEnemySighting? witness = AiMapMemory.KnownHostileSightingOf(player, enemyId);
            if (!(AviationRules.IsValidAirArmy(wing) || AviationRules.IsAirfield(wing)) || !witness.HasValue
                || !pm.ActiveDefenceTarget.AirSupportLandingHex.HasValue)
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                yield break;
            }
            HexCoord targetHex = witness.Value.Hex;
            pm.ActiveDefenceTarget.LastKnownHex = targetHex;
            if (!GroundCombatAirSupport.PreparationStrikeUsefulLive(player, ctx, pm.ActiveDefenceTarget, wing))
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                yield break;
            }
            wing = GroundCombatLegStep.MaterializeSupportWing(player, ctx, pm, result, wing);
            if (!AviationRules.IsValidAirArmy(wing))
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                yield break;
            }
            pm.ExecutionHex = targetHex;
            pm.ActiveDefenceTarget.LastKnownHex = targetHex;
            pm.ActiveDefenceTarget.LastObservedTurn = witness.Value.SeenTurn;
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            yield return GroundCombatLegStep.AirStrikeSortie(player, root, ctx, pm, result, wing,
                targetHex, pm.ActiveDefenceTarget.AirSupportLandingHex.Value,
                GroundCombatAirSupport.AirSupportPolicy(pm.ActiveDefenceTarget),
                pm.ActiveDefenceTarget.InterceptPurpose.ToString(),
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
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                return;
            }
            result.ActorMaterialized = true;
            result.ActualActorArmyId = shell.Id;
            result.StopReason = ExecutionStopReason.StepCompleted;
            AiDebugLog.Write($"[AI][V2][GarrisonRelief][Execution] extracted {units.Count} bodies from garrison "
                + $"#{donor.Id}@({donor.Hex.Q},{donor.Hex.R}) into #{shell.Id} for "
                + $"({pm.ActiveDefenceTarget.ReturnHex.Value.Q},{pm.ActiveDefenceTarget.ReturnHex.Value.R})");
        }

        // Muster intake (2026-11-19) — the staging garrison's pinned units move into the fist on
        // the same hex in one atomic transfer, after the same live check Provisioning ran
        // (ActiveDefenceProvisioner.TryResolveMusterIntake). The intake is the leg's whole work.
        private static void MusterIntake(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ArmyData fist, ExecutionResult result)
        {
            result.FinalHex = fist.Hex;
            string why = null;
            List<UnitData> units = null;
            ArmyData donor = null;
            bool ok = ActiveDefenceProvisioner.TryResolveMusterIntake(player, pm.ActiveDefenceTarget, fist,
                out donor, out units, out why);
            UnitData commander = ok && units.Count == 1 && units[0].IsHero
                && !fist.Members.Any(u => u != null && u.IsHero) ? units[0] : null;
            if (!ok || !ArmyActions.TransferMembersAtomic(units, donor, fist, ctx?.HexSelection, out why,
                    promoteToCommander: commander))
            {
                AiDebugLog.Write($"[AI][V2][Muster][Execution] intake refused into #{fist.Id}: {why}");
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                return;
            }
            result.ReachedGoal = true;
            result.CombatChanged = true;
            result.StopReason = ExecutionStopReason.ReachedGoal;
            AiDebugLog.Write($"[AI][V2][Muster][Execution] intake {units.Count} unit(s) "
                + $"[{string.Join(",", units.Select(u => u.Name))}] from garrison #{donor.Id} into fist #{fist.Id}"
                + $"@({fist.Hex.Q},{fist.Hex.R}) cap={fist.Capacity}");
        }

        // Muster arrival (2026-11-19) — the mover and the standing fist on the staging Base become
        // one army: the bodies go into whichever of the two commands more (the fist on a tie), as
        // many as fit, and only by a free transfer (ArmyActions.TransferMembersApCost = 0). A hero
        // of the giving army stays behind when the receiver already has a commander (it would only
        // take a slot). Nothing joins when no free fist stands there: the mover IS the fist then.
        // Returns the units moved.
        internal static int JoinFist(PlayerSetupData player, AiTurnContext ctx, ArmyData mover, HexCoord home)
        {
            if (player == null || mover == null || !mover.Hex.Equals(home) || mover.Members.Count == 0)
                return 0;
            HashSet<int> claimed = ActorCommitments.FromIntents(MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active), null, null).ClaimedArmyIdSet;
            ArmyData host = OwnWorld.Of(player).Armies
                .Where(a => a != null && a != mover && a.Hex.Equals(home) && a.Members.Count > 0
                    && !a.IsGarrison && !a.IsPrison && !a.IsAirfield && !a.IsCarrierContainer
                    && !AviationRules.IsAirArmy(a) && !claimed.Contains(a.Id)
                    && !a.Members.Any(u => u != null && u.IsAviation))
                .OrderByDescending(a => a.Capacity).ThenByDescending(a => a.Members.Count).ThenBy(a => a.Id)
                .FirstOrDefault();
            if (host == null)
            {
                AiDebugLog.Write($"[AI][V2][Muster][Execution] #{mover.Id} reached ({home.Q},{home.R}): no fist there, it is the fist");
                return 0;
            }
            var directions = mover.Capacity > host.Capacity
                ? new[] { (Source: host, Target: mover), (Source: mover, Target: host) }
                : new[] { (Source: mover, Target: host), (Source: host, Target: mover) };
            int moved = 0;
            foreach (var d in directions)
            {
                bool receiverCommands = d.Target.Members.Any(u => u != null && u.IsHero);
                List<UnitData> giving = d.Source.Members
                    .Where(u => u != null && !u.IsPrisoner && (!u.IsHero || !receiverCommands))
                    .OrderByDescending(u => u.IsHero)
                    .ThenByDescending(u => AiPower.EffectiveArmyPower(new List<UnitData> { u }))
                    .ToList();
                while (giving.Count > 0 && (ArmyActions.TransferMembersApCost(giving, d.Target) > 0
                    || !ArmyActions.TransferMembersAtomic(giving, d.Source, d.Target, ctx?.HexSelection, out _)))
                    giving.RemoveAt(giving.Count - 1);
                if (giving.Count == 0)
                    continue;
                moved = giving.Count;
                AiDebugLog.Write($"[AI][V2][Muster][Execution] joined {moved} unit(s) of #{d.Source.Id} into fist #{d.Target.Id}"
                    + $"@({home.Q},{home.R}) cap={d.Target.Capacity} members={d.Target.Members.Count}");
                break;
            }
            if (moved == 0)
                AiDebugLog.Write($"[AI][V2][Muster][Execution] #{mover.Id} and fist #{host.Id} on ({home.Q},{home.R}) "
                    + "could not join (capacity or activation charge); both stay on the Base");
            return moved;
        }

        // GarrisonRelief — the plan's last action on arrival: the detachment's bodies join the
        // recipient's garrison in one atomic transfer (the garrison's capacity rules apply; a body
        // that does not fit stays in the shell on the base, which still defends the hex).
        // The bodies delivered (0 when there is no garrison or nothing to deliver).
        private static int DeliverGarrisonRelief(PlayerSetupData player, AiTurnContext ctx,
            ArmyData shell, HexCoord home)
        {
            ArmyData garrison = OwnWorld.Of(player).Armies.FirstOrDefault(a => a != null && a.IsGarrison
                && a.Hex.Equals(home));
            if (garrison == null || shell == null || shell.Members.Count == 0)
                return 0;
            var bodies = shell.Members.Where(u => u != null).ToList();
            while (bodies.Count > 0
                && !ArmyActions.TransferMembersAtomic(bodies, shell, garrison, ctx?.HexSelection, out _))
                bodies.RemoveAt(bodies.Count - 1);
            AiDebugLog.Write($"[AI][V2][GarrisonRelief][Execution] delivered {bodies.Count}/"
                + $"{bodies.Count + shell.Members.Count} bodies of #{shell.Id} into garrison #{garrison.Id}@({home.Q},{home.R})");
            return bodies.Count;
        }

        private static void FinishActiveDefence(PlayerSetupData player, PlayerRoot root,
            ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            result.FinalHex = AiV2Util.ResolveArmy(player, pm?.MoverArmyId ?? -1)?.Hex ?? result.FinalHex;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2][ActiveDefence][Execution] enemy={pm?.ActiveDefenceTarget.EnemyArmyId} "
                + $"actor={pm?.MoverArmyId} phase={pm?.ActiveDefenceTarget.Phase} "
                // The purpose that applies to this leg's phase: an Intercept has no return purpose
                // (its default value used to read "SafeWithdrawal" on every intercept line).
                + (pm?.ActiveDefenceTarget.Phase == ActiveDefencePhase.Return
                    ? $"purpose={pm?.ActiveDefenceTarget.ReturnPurpose} destination={pm?.ActiveDefenceTarget.ReturnHex} "
                    : $"intercept={pm?.ActiveDefenceTarget.InterceptPurpose} ")
                + $"steps={result.StepsMoved} "
                + $"apBefore={apBefore} apSpent={result.ApSpent} apAfter={root?.ActionPoints} stop={result.StopReason}");
        }

    }
}

