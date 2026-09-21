using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // A generic aviation operation, deliberately not a new DesireAxis/MissionKind/manager.
    // Phase B admits it beside the other strategic spends, while current Recon objectives and
    // the canonical TaskScore service projection decide whether relocating one live aircraft is
    // materially better than leaving it at its present airfield.
    internal sealed class AviationRebasePlan
    {
        public HexCoord SourceHex;
        public HexCoord DestinationHex;
        public IReadOnlyList<UnitData> Aircraft;
        public AiAirSortiePlanner.RebaseRoute Route;
        public TaskScore Score;
        public float Utility => Score.Value;
        public int ActivationAp;
        public int EnergyCost;
        public string SourceWitness;
        public string DestinationWitness;
    }

    internal static class AviationRebasePlanner
    {
        internal static TaskScore ScoreImprovement(TaskScore sourceService,
            TaskScore destinationService, int activationAp, int energyCost,
            int requiredTurns = 1)
        {
            int futureActivations = Mathf.Max(0, requiredTurns - 1);
            float price = TaskScoreEvaluator.CardPrice(
                activationAp, energyCost * (1 + futureActivations));
            float delivery = TaskScoreEvaluator.DeliveryFromEta(
                activationAp, requiredTurns, AiConfigV2.taskScoreReactivationApWeight);
            return TaskScoreEvaluator.NetChange(
                sourceService, destinationService, price, delivery);
        }

        // A launched multi-turn rebase is a physical landing obligation, not a fresh strategic
        // choice. It survives turn boundaries in AirSortieRegistry and is resumed before optional
        // spending; the exact route, capacity, ownership, AA and fuel proof are still re-derived by
        // ContinueSortie on every step.
        internal static List<ArmyData> FindMandatoryContinuations(PlayerSetupData player)
        {
            var result = new List<ArmyData>();
            foreach (AirSortie sortie in AirSortieRegistry.For(player).ToList())
            {
                if (sortie == null || sortie.Kind != AirSortieKind.Rebase)
                    continue;
                ArmyData army = sortie.Army;
                bool live = army != null && army.Owner == player
                    && ArmyRegistry.AllForOwner(player).Contains(army)
                    && AviationRules.IsValidAirArmy(army) && army.Controller != null;
                if (!live || army.Hex.Equals(sortie.LandingHex))
                {
                    AirSortieRegistry.Remove(player, sortie);
                    continue;
                }
                if (army.CurrentMovement > 0)
                    result.Add(army);
            }
            return result.Distinct().OrderBy(a => a.Id).ToList();
        }

        internal static AviationRebasePlan BuildPlan(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<ReconObjective> objectives)
        {
            if (player == null || root == null || ctx?.Map == null || objectives == null
                || objectives.Count == 0)
                return null;

            AviationRebasePlan best = null;
            foreach (ArmyData source in ArmyRegistry.AllForOwner(player)
                .Where(AviationRules.IsAirfield)
                .OrderBy(a => a.Hex.Q).ThenBy(a => a.Hex.R))
            {
                foreach (UnitData aircraft in source.Members
                    .Where(AviationRules.IsAviation).OrderBy(u => u.RuntimeId))
                {
                    IReadOnlyList<UnitData> group = new[] { aircraft };
                    if (!AiAirSortiePlanner.CanAffordLaunch(root, player, group))
                        continue;
                    TaskScore sourceService = NonCombatCardPlayer.BestAirfieldServiceTaskScore(
                        snap, player, ctx, group, source.Hex, objectives,
                        out _, out string sourceWitness);

                    foreach (HexCoord destination in AiAirSortiePlanner.OwnedAirfieldHexes(player)
                        .Where(h => !h.Equals(source.Hex)))
                    {
                        TaskScore destinationService = NonCombatCardPlayer.BestAirfieldServiceTaskScore(
                            snap, player, ctx, group, destination, objectives,
                            out int coverage, out string destinationWitness);
                        float improvement = destinationService.Value - sourceService.Value;
                        if (coverage <= 0 || improvement <= AiConfigV2.allocatorSliceEpsilon)
                            continue;

                        AiAirSortiePlanner.RebaseRoute? route =
                            AiAirSortiePlanner.TryPlanRebaseFromStorage(
                                source.Hex, group, destination, ctx.Map, player);
                        if (!route.HasValue)
                            continue;

                        int ap = group.Sum(u => Mathf.Max(0, u.ActivationApCost));
                        int energy = group.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
                        TaskScore score = ScoreImprovement(
                            sourceService, destinationService, ap, energy,
                            route.Value.RequiredTurns);
                        if (score.Value <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        var candidate = new AviationRebasePlan
                        {
                            SourceHex = source.Hex,
                            DestinationHex = destination,
                            Aircraft = group,
                            Route = route.Value,
                            Score = score,
                            ActivationAp = ap,
                            EnergyCost = energy,
                            SourceWitness = sourceWitness,
                            DestinationWitness = destinationWitness,
                        };
                        if (best == null || candidate.Utility > best.Utility + AiConfigV2.allocatorSliceEpsilon
                            || (Mathf.Abs(candidate.Utility - best.Utility) <= AiConfigV2.allocatorSliceEpsilon
                                && aircraft.RuntimeId < best.Aircraft[0].RuntimeId))
                            best = candidate;
                    }
                }
            }
            return best;
        }

        internal static IEnumerator Execute(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AviationRebasePlan plan, System.Action<bool> setChanged)
        {
            if (plan?.Aircraft == null || plan.Aircraft.Count == 0 || player == null
                || root == null || ctx?.Map == null)
                yield break;
            ArmyData source = AviationRules.FindAirfieldAt(plan.SourceHex, player);
            if (source == null || plan.Aircraft.Any(u => !source.Members.Contains(u))
                || !AiAirSortiePlanner.CanAffordLaunch(root, player, plan.Aircraft)
                || !AiAirSortiePlanner.TryPlanRebaseFromStorage(plan.SourceHex, plan.Aircraft,
                    plan.DestinationHex, ctx.Map, player).HasValue)
                yield break;

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

        internal static IEnumerator ExecuteContinuation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ArmyData wing, System.Action<bool> setChanged)
        {
            if (player == null || root == null || ctx?.Map == null || wing == null)
                yield break;
            AirSortie task = AirSortieRegistry.ForArmy(player, wing);
            if (task == null || task.Kind != AirSortieKind.Rebase)
                yield break;

            bool changed = false;
            var trace = new AiMoveExecutionTrace();
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
                if (wing == null || wing.Hex.Equals(prior))
                    break;
                changed = true;
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
