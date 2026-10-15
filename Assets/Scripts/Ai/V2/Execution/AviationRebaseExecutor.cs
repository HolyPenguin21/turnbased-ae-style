using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // The execution half of an aviation rebase (2026-10-15: moved out of Strategy). Strategy's
    // AviationRebasePlanner decides WHICH wing goes where (an AviationRebasePlan); this executes
    // the funded plan through the canonical sortie / movement calls and the continuation of an
    // already-launched rebase. It never re-plans.
    internal static class AviationRebaseExecutor
    {
        // Launch and guarded first flight form one transaction. Consume its factual receipt
        // before looking for a surviving wing; later flight steps have separate receipts.
        internal static void RecordLaunchReceipt(AiMoveExecutionTrace trace) =>
            WorldDeltaLifecycle.RecordExecutionMutation(null, trace?.FormationCommitted == true);

        internal static IEnumerator Execute(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AviationRebasePlan plan, System.Action<bool> setChanged)
        {
            if (plan?.Aircraft == null || plan.Aircraft.Count == 0 || player == null
                || root == null || ctx?.Map == null)
                yield break;
            if (plan.FormOnly)
            {
                yield return ExecuteFormation(player, root, ctx, plan, setChanged);
                yield break;
            }
            ArmyData source = AviationRules.FindAirfieldAt(plan.SourceHex, player);
            if (source == null || plan.Aircraft.Any(u => !source.Members.Contains(u))
                || !AiAirSortiePlanner.CanAffordLaunch(root, plan.Aircraft))
                yield break;

            AiAirSortiePlanner.RebaseRoute? liveRoute =
                AiAirSortiePlanner.TryPlanRebaseFromStorage(plan.SourceHex, plan.Aircraft,
                    plan.DestinationHex, ctx.Map, player);
            if (!liveRoute.HasValue)
                yield break;

            int liveAp = plan.Aircraft.Sum(u => Mathf.Max(0, u.ActivationApCost));
            int liveEnergy = plan.Aircraft.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
            float spendableAp = StrategicSpendability.SpendableAp(player, root, ctx);
            float spendableEnergy = StrategicSpendability.SpendableAmount(
                player, root, ctx, ResourceType.Energy);
            if (liveAp > spendableAp + AiConfigV2.allocatorSliceEpsilon
                || liveEnergy > spendableEnergy + AiConfigV2.allocatorSliceEpsilon)
            {
                AiDebugLog.Write("[AI][V2][Aviation][Rebase] cancelled — AP/Energy held by the resource bank");
                yield break;
            }

            var before = new HashSet<int>(ArmyRegistry.AllForOwner(player).Select(a => a.Id));
            var decision = new AiDecision
            {
                Kind = AiActionKind.LaunchAirRecon,
                TargetHex = plan.SourceHex,
                AircraftToLaunch = plan.Aircraft,
                AirActionHex = plan.DestinationHex,
                AirLandingHex = plan.DestinationHex,
                Score = plan.Utility,
                Reason = $"V2 Aviation Rebase — current objective service improves "
                    + $"{plan.SourceWitness ?? "none"} -> {plan.DestinationWitness ?? "none"}",
            };
            var trace = new AiMoveExecutionTrace();
            yield return AiAirSortiePlanner.LaunchRoutine(
                player, decision, ctx, AirSortieKind.Rebase, trace);
            RecordLaunchReceipt(trace);

            ArmyData wing = ArmyRegistry.AllForOwner(player)
                .Where(a => AviationRules.IsValidAirArmy(a) && !before.Contains(a.Id))
                .OrderBy(a => a.Id).FirstOrDefault();
            if (wing == null)
                yield break;

            bool changed = !wing.Hex.Equals(plan.SourceHex);
            int guard = Mathf.Max(1, wing.CurrentMovement + 1);
            while (!wing.Hex.Equals(plan.DestinationHex) && wing.CurrentMovement > 0 && guard-- > 0)
            {
                AirSortie task = AirSortieRegistry.ForArmy(player, wing);
                AiDecision move = task == null ? null : AiAirSortiePlanner.ContinueSortie(
                    player, root, ctx, task, "AviationRebase",
                    "relocates to the selected forward airfield", plan.Utility);
                if (move == null)
                    break;
                HexCoord prior = wing.Hex;
                yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
                wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == wing.Id);
                WorldDeltaLifecycle.RecordExecutionMutation(null,
                    !(wing?.Hex ?? trace.EndHex).Equals(prior));
                if (wing == null || wing.Hex.Equals(prior))
                    break;
                changed = true;
            }
            if (wing != null && wing.Hex.Equals(plan.DestinationHex))
                AirSortieRegistry.Remove(player, wing);
            setChanged?.Invoke(changed);
            AiDebugLog.Write($"[AI][V2][Aviation][Rebase] source=({plan.SourceHex.Q},{plan.SourceHex.R}) "
                + $"destination=({plan.DestinationHex.Q},{plan.DestinationHex.R}) "
                + $"score={plan.Score.Value:0.00} changed={(changed ? 1 : 0)} "
                + $"witness={plan.DestinationWitness ?? "none"}");
        }

        private static IEnumerator ExecuteFormation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AviationRebasePlan plan, System.Action<bool> setChanged)
        {
            ArmyData source = AviationRules.FindAirfieldAt(plan.SourceHex, player);
            if (source == null || plan.Aircraft.Any(u => !source.Members.Contains(u)))
                yield break;
            // Re-proved against the live bank: the flight this wing exists for must still be payable.
            int ap = plan.Aircraft.Sum(u => Mathf.Max(0, u.ActivationApCost));
            int energy = plan.Aircraft.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
            if (!AiAirSortiePlanner.CanAffordLaunch(root, plan.Aircraft)
                || ap > StrategicSpendability.SpendableAp(player, root, ctx) + AiConfigV2.allocatorSliceEpsilon
                || energy > StrategicSpendability.SpendableAmount(player, root, ctx, ResourceType.Energy)
                    + AiConfigV2.allocatorSliceEpsilon)
            {
                AiDebugLog.Write("[AI][V2][Aviation][FormWing] cancelled — AP/Energy held by the resource bank");
                yield break;
            }
            if (!AviationWingPreparation.TryForm(player, ctx, source, plan.Aircraft, plan.Commitments,
                    out ArmyData wing, out string how) || wing == null)
            {
                AiDebugLog.Write($"[AI][V2][Aviation][FormWing] failed — {how}");
                yield break;
            }
            AiDebugLog.Write($"[AI][V2][Aviation][FormWing] {player.Nickname}: \"{wing.Name}\" #{wing.Id} formed from "
                + $"{plan.Aircraft.Count} stored aircraft at ({plan.SourceHex.Q},{plan.SourceHex.R}) ({how}) "
                + $"service={plan.Score.Value:0.00} witness={plan.DestinationWitness ?? "none"} "
                + $"(sortie launch ap {ap} energy {energy} is paid by the funded leg, not here)");
            setChanged?.Invoke(true);
            yield return AiTurnController.WaitStep(ctx);
        }

        // The caller may suppress a strike for this invocation. Former support wings retain
        // their exact targeting policy in the sortie, rather than a permanent strike ban.
        internal static IEnumerator ExecuteContinuation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ArmyData wing, System.Action<bool> setChanged,
            bool allowRecoveryStrike = true, ExecutionResult result = null)
        {
            if (player == null || root == null || ctx?.Map == null || wing == null)
                yield break;
            AirSortie task = AirSortieRegistry.ForArmy(player, wing);
            if (task == null || task.Kind != AirSortieKind.Rebase)
                yield break;

            // Paid airborne members owe nothing. If a member still owes its launch share,
            // both the optional strike and the return must respect other owners' bank holds.
            if (wing.PendingActivationApCost > StrategicSpendability.SpendableAp(player, root, ctx)
                    + AiConfigV2.allocatorSliceEpsilon
                || wing.PendingActivationEnergyCost > StrategicSpendability.SpendableAmount(
                    player, root, ctx, ResourceType.Energy) + AiConfigV2.allocatorSliceEpsilon)
                yield break;

            bool changed = false;
            var trace = new AiMoveExecutionTrace();

            // An airborne recovery/rebase wing does not become pacifist just because its original
            // mission released it. If it starts this continuation on an enemy-occupied hex and a
            // landing is still recoverable inside live endurance, take the free stationary strike
            // under the sortie's own target policy; movement remains untouched and return continues.
            if (allowRecoveryStrike
                && !AviationRules.IsOwnedAirfieldAt(wing.Hex, player)
                && AviationActions.CanActivateForStationaryStrike(wing)
                && AiAirSortiePlanner.CanStrikeAndRecover(wing, ctx.Map, player, task.StrikePolicy))
            {
                AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
                if (presenter != null)
                {
                    var strike = new AviationCombatPresenter.AirStrikeResult();
                    yield return AviationActions.ResolveStationaryStrike(presenter, wing, task.StrikePolicy, strike);
                    if (strike.Attacked)
                    {
                        changed = true;
                        wing.LastAirStrikeHex = wing.Hex;
                        wing.LastAirStrikeAttacked = true;
                        if (result != null)
                        {
                            result.CombatChanged = true;
                            result.AirSupportStrikeSucceeded = true;
                        }
                        WorldDeltaLifecycle.RecordExecutionMutation(result, true);
                        AiDebugLog.Write($"[AI][V2][Aviation][RecoveryStrike] actor=#{wing.Id} "
                            + $"hex=({wing.Hex.Q},{wing.Hex.R}) attacked=1 "
                            + $"policy={task.StrikePolicy.Kind} "
                            + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(wing)}");
                    }
                }
            }

            int guard = Mathf.Max(1, wing.CurrentMovement + 1);
            while (wing != null && wing.CurrentMovement > 0 && guard-- > 0)
            {
                AiDecision move = AiAirSortiePlanner.ContinueSortie(
                    player, root, ctx, task, "AviationRebase",
                    "relocates to the selected forward airfield", AiConfig.airStrikeContinuationScore);
                if (move == null)
                    break;
                HexCoord prior = wing.Hex;
                yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
                wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == wing.Id);
                WorldDeltaLifecycle.RecordExecutionMutation(result,
                    !(wing?.Hex ?? trace.EndHex).Equals(prior));
                if (wing == null || wing.Hex.Equals(prior))
                    break;
                changed = true;
                if (result != null)
                    result.StepsMoved++;
                if (wing.Hex.Equals(task.LandingHex))
                {
                    AirSortieRegistry.Remove(player, task);
                    break;
                }
            }

            // If the requested destination disappeared while the aircraft was already safely on
            // another owned airfield, there is no recovery obligation left to retain forever.
            if (!changed && wing != null && AviationRules.IsOwnedAirfieldAt(wing.Hex, player)
                && !AviationRules.IsOwnedAirfieldAt(task.LandingHex, player))
                AirSortieRegistry.Remove(player, task);
            setChanged?.Invoke(changed);
        }
    }
}
