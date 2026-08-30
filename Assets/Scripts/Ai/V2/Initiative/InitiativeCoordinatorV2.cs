using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Turns;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    // The one initiative-purchase AI. Runs at the pre-popup round boundary
    // (GameTurnController.ProceedWithNewTurn), after ResetBonusInitiativeDice and before the human
    // initiative UI. The old random/free InitiativeDiceAI implementation no longer exists.
    //
    // Strict one-way boundary: it reads only own world/army workload, own resources + income, own
    // deck appetite, own initiative/AP history, own existing AiResourceReservation claims, and
    // the PUBLIC previous-initiative results. It writes nothing into the V2 strategic pipeline —
    // its only effect on the later AI turn is ordinary gameplay state (resources actually spent,
    // AP/order actually won).
    //
    // Orchestration guarantee: EVERY AI plans from the same immutable pre-purchase state
    // (opponent estimates come from public history, never from another AI's freshly-formed plan),
    // then all plans are applied. Applying one AI's purchase can't feed information to another.
    public static class InitiativeCoordinatorV2
    {
        public static void PlanAndApplyForAll(List<PlayerSetupData> players, HexMap map,
            StartingDeckCatalog deckCatalog, int turnNumber)
        {
            if (players == null || players.Count == 0)
                return;

            // ---- 1. PLAN (nothing applied yet) --------------------------------------------------
            var planned = new List<(PlayerSetupData Player, PlayerRoot Root, InitiativePlan Plan)>();

            foreach (PlayerSetupData p in players)
            {
                if (p == null || p.IsHuman || p.IsNeutral || p.IsEliminated)
                    continue;
                PlayerRoot root = PlayerRootRegistry.FindFor(p);
                if (root == null)
                    continue;

                // Opponent pool: every OTHER active (non-neutral, non-eliminated) player, sized
                // purely from public previous-initiative behaviour. No history => 5 base dice.
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

                AiDebugLog.Write($"[AI][V2][Initiative] {p.Nickname} (turn {turnNumber}) — "
                    + $"apPressure={analysis.ApPressure:0.00} (cur={analysis.CurrentApPressure:0.00} hist={analysis.HistoricalApPressure:0.00}), "
                    + $"turnOrderPressure={analysis.TurnOrderPressure:0.00}, "
                    + $"armies={analysis.ActionableFieldArmyCount} power={analysis.ActionableMilitaryPower:0.0} apCards={analysis.ApCostingActionsAvailable}, "
                    + $"avail H/E/M/T={analysis.Available[0]}/{analysis.Available[1]}/{analysis.Available[2]}/{analysis.Available[3]}, "
                    + $"oppDice=[{string.Join(",", opponentDice)}] => plan: {plan.Rationale}");
            }

            // ---- 2. APPLY, revalidating every die against the live canonical rules -------------
            foreach ((PlayerSetupData Player, PlayerRoot Root, InitiativePlan Plan) entry in planned)
            {
                if (entry.Plan.DiceToBuy <= 0)
                    continue;

                int applied = 0;
                var spent = new int[4];
                foreach (ResourceType resource in entry.Plan.PaymentResources)
                {
                    if (!entry.Root.CanBuyInitiativeDie(resource))
                        break;

                    int price = entry.Root.NextInitiativeDieCost;
                    if (!entry.Root.PurchaseInitiativeDie(resource))
                        break;

                    spent[ResourceIndex(resource)] += price;
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
