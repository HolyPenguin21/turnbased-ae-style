using System.Collections.Generic;
using System.Linq;
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

                var opponentTokens = new List<int>();
                foreach (PlayerSetupData o in players)
                {
                    if (o == null || ReferenceEquals(o, p) || o.IsNeutral || o.IsEliminated)
                        continue;
                    int est = InitiativeRules.BaseTokens + InitiativePublicHistory.EstimatedBonusTokens(o);
                    opponentTokens.Add(Mathf.Clamp(est, InitiativeRules.BaseTokens, InitiativeRules.MaxTotalTokens));
                }

                // Only already accepted, still-held Economy builds are hard commitments.
                // The canonical Continuity predicate owns validity of the site lease; merely
                // owning an unselected card or a transient Phase A demand is not a commitment.
                AiHandData hand = AiHandRegistry.Peek(p);
                IEnumerable<ResourceCost> committedBuildCosts = MissionIntentRegistry.Peek(p)?.All
                    .Where(EconomyContinuity.IsLiveEconomyBuild)
                    .Where(i => i.Economy.BuildResourceCost != null
                        && (i.Economy.Kind != EconomyTaskKind.FoundBase
                            || (i.Economy.BuildCard != null
                                && hand?.Hand?.Contains(i.Economy.BuildCard) == true)))
                    .Select(i => i.Economy.BuildResourceCost);
                PreTurnCapacityAnalysis analysis = PreTurnCapacityAnalysis.Build(
                    p, root, map, deckCatalog, committedBuildCosts);
                InitiativePlan plan = InitiativePlanner.Plan(analysis, opponentTokens);
                planned.Add((p, root, plan));
                string bottleneck = InitiativeBottleneckDiagnostics.Describe(p, analysis);

                AiDebugLog.Write($"[AI][V2][Initiative] {p.Nickname} (turn {turnNumber}) — "
                    + $"apPressure={analysis.ApPressure:0.00} (cur={analysis.CurrentApPressure:0.00} hist={analysis.HistoricalApPressure:0.00}), "
                    + $"turnOrderPressure={analysis.TurnOrderPressure:0.00}, bottleneck={bottleneck}, "
                    + $"armies={analysis.ActionableFieldArmyCount} power={analysis.ActionableMilitaryPower:0.0} apCards={analysis.ApCostingActionsAvailable}, "
                    + $"avail H/E/M/T={analysis.Available[0]}/{analysis.Available[1]}/{analysis.Available[2]}/{analysis.Available[3]}, "
                    + $"committed H/E/M/T={analysis.CommittedResourceFloor[0]}/{analysis.CommittedResourceFloor[1]}/{analysis.CommittedResourceFloor[2]}/{analysis.CommittedResourceFloor[3]}, "
                    + $"oppTokens=[{string.Join(",", opponentTokens)}] => plan: {plan.Rationale}");
            }

            foreach ((PlayerSetupData Player, PlayerRoot Root, InitiativePlan Plan) entry in planned)
            {
                if (entry.Plan.TokensToBuy <= 0)
                    continue;

                // Cross-turn spendability feedback: when the previous turn stranded a configured
                // fraction of AP, the planner's theoretical AP pressure is not enough evidence to
                // spend scarce H/E/M/T on more initiative. One clean turn with low leftover removes
                // this guard automatically.
                if (InitiativeBottleneckDiagnostics.ShouldSuppressBonusTokens(entry.Player, out string suppressReason))
                {
                    AiDebugLog.Write($"[AI][V2][Initiative] {entry.Player.Nickname} — suppress "
                        + $"{entry.Plan.TokensToBuy} planned bonus tokens: {suppressReason}.");
                    AiMatchStats.RecordInitiativeSuppressed(entry.Player);
                    continue;
                }

                // Plan.PaymentUnits is flat — one H/E/M/T entry per resource UNIT, across every
                // planned die, in purchase order (see InitiativeFundingOptimizer). Slice off
                // NextInitiativeTokenCost entries per die and pay them one unit at a time, mixing
                // resources within a single die exactly like the human buy panel now can.
                int applied = 0;
                var spent = new int[4];
                int unitCursor = 0;
                for (int tokenNum = 0; tokenNum < entry.Plan.TokensToBuy; tokenNum++)
                {
                    int tokenCost = entry.Root.NextInitiativeTokenCost;
                    var paidUnits = new List<ResourceType>(tokenCost);
                    bool tokenComplete = true;
                    for (int u = 0; u < tokenCost; u++)
                    {
                        if (unitCursor >= entry.Plan.PaymentUnits.Count)
                        {
                            tokenComplete = false;
                            break;
                        }
                        ResourceType resource = entry.Plan.PaymentUnits[unitCursor];
                        if (!entry.Root.CanBuyInitiativeToken(resource) || !entry.Root.PurchaseInitiativeToken(resource))
                        {
                            tokenComplete = false;
                            break;
                        }
                        paidUnits.Add(resource);
                        unitCursor++;
                    }

                    if (!tokenComplete)
                    {
                        // Can't finish this die live (planned resources ran out or a live check
                        // failed) — undo exactly the units just paid, most-recent-first, each
                        // with the resource that actually paid it (PlayerRoot only allows
                        // refunding the single most recent contribution), and stop planning
                        // further tokens this turn.
                        for (int i = paidUnits.Count - 1; i >= 0; i--)
                            entry.Root.RefundLastInitiativeToken(paidUnits[i]);
                        break;
                    }

                    foreach (ResourceType resource in paidUnits)
                        spent[ResourceIndex(resource)]++;
                    applied++;
                }
                AiMatchStats.RecordInitiativeBought(entry.Player, applied, spent);

                if (applied != entry.Plan.TokensToBuy)
                    AiDebugLog.Write($"[AI][V2][Initiative] {entry.Player.Nickname} — applied {applied}/{entry.Plan.TokensToBuy} planned tokens "
                        + "(remaining tokens failed live revalidation).");
                else if (applied > 0)
                    AiDebugLog.Write($"[AI][V2][Initiative] {entry.Player.Nickname} — bought {applied} bonus tokens "
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
