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

                // Plan.PaymentUnits is flat — one H/E/M/T entry per resource UNIT, across every
                // planned die, in purchase order (see InitiativeFundingOptimizer). Slice off
                // NextInitiativeDieCost entries per die and pay them one unit at a time, mixing
                // resources within a single die exactly like the human buy panel now can.
                int applied = 0;
                var spent = new int[4];
                int unitCursor = 0;
                for (int dieNum = 0; dieNum < entry.Plan.DiceToBuy; dieNum++)
                {
                    int dieCost = entry.Root.NextInitiativeDieCost;
                    var paidUnits = new List<ResourceType>(dieCost);
                    bool dieComplete = true;
                    for (int u = 0; u < dieCost; u++)
                    {
                        if (unitCursor >= entry.Plan.PaymentUnits.Count)
                        {
                            dieComplete = false;
                            break;
                        }
                        ResourceType resource = entry.Plan.PaymentUnits[unitCursor];
                        if (!entry.Root.CanBuyInitiativeDie(resource) || !entry.Root.PurchaseInitiativeDie(resource))
                        {
                            dieComplete = false;
                            break;
                        }
                        paidUnits.Add(resource);
                        unitCursor++;
                    }

                    if (!dieComplete)
                    {
                        // Can't finish this die live (planned resources ran out or a live check
                        // failed) — undo exactly the units just paid, most-recent-first, each
                        // with the resource that actually paid it (PlayerRoot only allows
                        // refunding the single most recent contribution), and stop planning
                        // further dice this turn.
                        for (int i = paidUnits.Count - 1; i >= 0; i--)
                            entry.Root.RefundLastInitiativeDie(paidUnits[i]);
                        break;
                    }

                    foreach (ResourceType resource in paidUnits)
                        spent[ResourceIndex(resource)]++;
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
