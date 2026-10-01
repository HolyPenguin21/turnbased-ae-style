using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AP TURN PRESSURE — the AP the AI really wanted to spend, measured at turn end
    // ===========================================================================================
    //  The structural AP model (ApActionEconomySnapshot) counts activations and hand-card AP only,
    //  ignores draws and discounts the sum to 60%. In playtest 2026-10-01 #4 it read "AP idle"
    //  (marginal utility 0.1-0.2) on turns whose real demand was twice the AP, so a Base's or a
    //  hero's +2 AP/turn was priced at 0. This is the witnessed fact instead:
    //
    //      demand = AP spent this turn + AP the turn still wanted and could not pay:
    //               draws up to handReplenishTargetCards, affordable AP-costing hand cards left
    //               unplayed, missions the allocator deferred for budget.
    //
    //  The mean over the last turns becomes ApActionEconomySnapshot.WitnessedApDemand, which
    //  EffectEvaluationContext.ResolveUsefulApDemand prefers over the structural guess. Written
    //  once at turn end by the pipeline; the snapshot reads it at the next scan, so it never
    //  changes inside a turn.
    // ===========================================================================================
    internal readonly struct ApTurnMeasure
    {
        public readonly int StartAp, EndAp;
        public readonly float DrawsUnmet, CardsUnmet, MissionsUnmet;
        public readonly int DrawsShort, CardsAffordable, MissionsDeferred;

        public ApTurnMeasure(int startAp, int endAp, float drawsUnmet, int drawsShort,
            float cardsUnmet, int cardsAffordable, float missionsUnmet, int missionsDeferred)
        {
            StartAp = startAp; EndAp = endAp;
            DrawsUnmet = drawsUnmet; DrawsShort = drawsShort;
            CardsUnmet = cardsUnmet; CardsAffordable = cardsAffordable;
            MissionsUnmet = missionsUnmet; MissionsDeferred = missionsDeferred;
        }

        public int Spent => Mathf.Max(0, StartAp - EndAp);
        public float Unmet => DrawsUnmet + CardsUnmet + MissionsUnmet;
        public float Demand => Spent + Unmet;
        public float Pressure => StartAp > 0 ? Demand / StartAp : 0f;
    }

    internal static class ApTurnPressure
    {
        private static readonly Dictionary<PlayerSetupData, List<(int Turn, float Demand)>> ByPlayer =
            new Dictionary<PlayerSetupData, List<(int, float)>>();

        // Match-start reset (CitadelSetupController), alongside the other V2 registries.
        internal static void ClearAll() => ByPlayer.Clear();

        internal static ApTurnMeasure Measure(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, int startAp, IReadOnlyList<DeferredEntry> lastDeferred)
        {
            int endAp = root != null ? root.ActionPoints : startAp;
            int drawCost = ctx != null ? ctx.DrawApCost : 0;
            int handCount = hand?.Hand?.Count ?? 0;
            int deckCount = hand?.RemainingDeck?.Count ?? 0;
            int drawsShort = Mathf.Min(deckCount,
                Mathf.Max(0, AiConfigV2.handReplenishTargetCards - handCount));

            float cardsUnmet = 0f;
            int cardsAffordable = 0;
            if (hand?.Hand != null && root != null && ctx != null)
                foreach (CardData c in hand.Hand)
                {
                    int ap = c != null ? CardCostRules.PlayAp(c) : 0;
                    if (ap <= 0)
                        continue;
                    var cost = CardCostRules.PlayResources(c);
                    if (cost != null && !StrategicSpendability.FitsSpendableResources(player, root, ctx, cost))
                        continue;
                    cardsAffordable++;
                    cardsUnmet += ap;
                }

            List<DeferredEntry> budget = (lastDeferred ?? System.Array.Empty<DeferredEntry>())
                .Where(d => d?.Mission?.Requirements != null && d.Reason == DeferReason.InsufficientBudget)
                .ToList();
            return new ApTurnMeasure(startAp, endAp, drawsShort * drawCost, drawsShort,
                cardsUnmet, cardsAffordable, budget.Sum(d => d.Mission.Requirements.ApDesired), budget.Count);
        }

        internal static void Record(PlayerSetupData player, int turn, ApTurnMeasure measure)
        {
            if (player == null)
                return;
            if (!ByPlayer.TryGetValue(player, out List<(int Turn, float Demand)> list))
                ByPlayer[player] = list = new List<(int, float)>();
            list.RemoveAll(e => e.Turn >= turn);
            list.Add((turn, measure.Demand));
            while (list.Count > AiConfigV2.apWitnessedDemandHistoryTurns)
                list.RemoveAt(0);
        }

        // Mean real AP demand of the recorded turns before `turn`; null before the first one.
        internal static float? WitnessedDemand(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out List<(int Turn, float Demand)> list))
                return null;
            List<float> past = list.Where(e => e.Turn < turn).Select(e => e.Demand).ToList();
            return past.Count > 0 ? past.Average() : (float?)null;
        }
    }
}
