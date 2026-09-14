using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Turns;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    public static class InitiativeCoordinatorV2
    {
        public static void PlanAndApplyForAll(List<PlayerSetupData> players, HexMap map,
            StartingDeckCatalog deckCatalog, int turnNumber)
        {
            if (players == null || players.Count == 0)
                return;

            var planned = new List<(PlayerSetupData Player, PlayerRoot Root, InitiativePlan Plan)>();

            foreach (PlayerSetupData p in players)
            {
                if (p == null || p.IsHuman || p.IsNeutral || p.IsEliminated)
                    continue;
                PlayerRoot root = PlayerRootRegistry.FindFor(p);
                if (root == null)
                    continue;

                var opponentDice = new List<int>();
                foreach (PlayerSetupData o in players)
                {
                    if (o == null || ReferenceEquals(o, p) || o.IsNeutral || o.IsEliminated)
                        continue;
                    int est = InitiativeRules.BaseDice + InitiativePublicHistory.EstimatedBonusDice(o);
                    opponentDice.Add(Mathf.Clamp(est, InitiativeRules.BaseDice, InitiativeRules.MaxTotalDice));
                }

                PreTurnCapacityAnalysis analysis = PreTurnCapacityAnalysis.Build(p, root, map, deckCatalog);
                InitiativePlan plan = InitiativePlanner.Plan(analysis, opponentDice);
                planned.Add((p, root, plan));
                string bottleneck = InitiativeBottleneckDiagnostics.Describe(p, analysis);

                AiDebugLog.Write($"[AI][V2][Initiative] {p.Nickname} (turn {turnNumber}) — "
                    + $"apPressure={analysis.ApPressure:0.00} (cur={analysis.CurrentApPressure:0.00} hist={analysis.HistoricalApPressure:0.00}), "
                    + $"turnOrderPressure={analysis.TurnOrderPressure:0.00}, bottleneck={bottleneck}, "
                    + $"armies={analysis.ActionableFieldArmyCount} power={analysis.ActionableMilitaryPower:0.0} apCards={analysis.ApCostingActionsAvailable}, "
                    + $"avail H/E/M/T={analysis.Available[0]}/{analysis.Available[1]}/{analysis.Available[2]}/{analysis.Available[3]}, "
                    + $"oppDice=[{string.Join(",", opponentDice)}] => plan: {plan.Rationale}");
            }

            foreach ((PlayerSetupData Player, PlayerRoot Root, InitiativePlan Plan) entry in planned)
            {
                if (entry.Plan.DiceToBuy <= 0)
                    continue;

                // Cross-turn spendability feedback: when the previous turn stranded a configured
                // fraction of AP, the planner's theoretical AP pressure is not enough evidence to
                // spend scarce H/E/M/T on more initiative. One clean turn with low leftover removes
                // this guard automatically.
                if (InitiativeBottleneckDiagnostics.ShouldSuppressBonusDice(entry.Player, out string suppressReason))
                {
                    AiDebugLog.Write($"[AI][V2][Initiative] {entry.Player.Nickname} — suppress "
                        + $"{entry.Plan.DiceToBuy} planned bonus dice: {suppressReason}.");
                    continue;
                }

                // Each PaymentResources entry still means "fund one whole die from this resource"
                // (the AI's own planned strategy) — PlayerRoot now only sells dice 1 resource-unit
                // at a time (see PurchaseInitiativeDie), so funding one die from a single resource
                // takes that die's whole progressive cost in repeated 1-unit calls.
                int applied = 0;
                var spent = new int[4];
                foreach (ResourceType resource in entry.Plan.PaymentResources)
                {
                    int dieCost = entry.Root.NextInitiativeDieCost;
                    int paidThisDie = 0;
                    for (; paidThisDie < dieCost; paidThisDie++)
                    {
                        if (!entry.Root.CanBuyInitiativeDie(resource) || !entry.Root.PurchaseInitiativeDie(resource))
                            break;
                    }

                    if (paidThisDie < dieCost)
                    {
                        // Partial die bought and can't be completed from this resource — refund
                        // the units already spent on it so the ledger stays whole-dice-only for
                        // the AI's own planned strategy, and stop planning further dice this turn.
                        for (int i = 0; i < paidThisDie; i++)
                            entry.Root.RefundLastInitiativeDie(resource);
                        break;
                    }

                    spent[ResourceIndex(resource)] += dieCost;
                    applied++;
                }

                if (applied != entry.Plan.DiceToBuy)
                    AiDebugLog.Write($"[AI][V2][Initiative] {entry.Player.Nickname} — applied {applied}/{entry.Plan.DiceToBuy} planned dice "
                        + "(remaining dice failed live revalidation).");
                else if (applied > 0)
                    AiDebugLog.Write($"[AI][V2][Initiative] {entry.Player.Nickname} — bought {applied} bonus dice "
                        + $"for H/E/M/T={spent[0]}/{spent[1]}/{spent[2]}/{spent[3]}.");
            }
        }

        private static int ResourceIndex(ResourceType type)
        {
            switch (type)
            {
                case ResourceType.Human: return 0;
                case ResourceType.Energy: return 1;
                case ResourceType.Materials: return 2;
                default: return 3;
            }
        }
    }
}
