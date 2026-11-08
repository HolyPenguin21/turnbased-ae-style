using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public sealed class TradeFundingPlan
    {
        public readonly int Rate;
        public readonly IReadOnlyList<TradeStep> Steps;
        public readonly ResourceCost ExternalCost;
        internal TradeFundingPlan(int rate, List<TradeStep> steps, ResourceCost cost)
        { Rate = rate; Steps = steps.AsReadOnly(); ExternalCost = cost; }
    }

    // Read-only funding preview. No payment belongs in affordability or scoring.
    internal static class TradeFunding
    {
        internal static bool CanFund(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ResourceCost cost, SpendAuthority authority = default) =>
            StrategicSpendability.FitsSpendableResources(player, root, ctx, cost, authority)
            || TryPlan(player, root, ctx, cost, out _, authority);

        internal static bool TryPlan(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ResourceCost cost, out TradeFundingPlan plan, SpendAuthority authority = default, bool previewAccess = false)
        {
            plan = null;
            if (player == null || root == null || ctx == null || cost == null || root.TradeRate == int.MaxValue
                || (!previewAccess && (!root.TradeTurnOpen || !TradeActions.HasAccess(player)))) return false;
            var hand = AiHandRegistry.Peek(player);
            var forecast = ResourceStarvationRegistry.ForecastOperationalNeed(player, ctx.TurnNumber);
            var free = new int[4]; var donors = new int[4]; var missing = new int[4]; var external = new int[4];
            long units = 0;
            for (int i = 0; i < 4; i++)
            {
                var type = (ResourceType)i;
                if (cost.Get(type) < 0) return false;
                free[i] = Mathf.Max(0, Mathf.FloorToInt(StrategicSpendability.SpendableAmount(player, root, ctx, type, authority)));
                int ordinaryFree = Mathf.Max(0, Mathf.FloorToInt(StrategicSpendability.SpendableAmount(player, root, ctx, type)));
                double future = forecast.Get(type);
                if (hand != null)
                {
                    future += hand.Hand.Sum(c => (double)(CardCostRules.PlayResources(c)?.Get(type) ?? 0));
                    future += hand.RemainingDeck.Sum(c => (double)(c?.resourceCost?.Get(type) ?? 0));
                }
                // Conservatively protect the full remaining main deck AND non-card operations.
                // Claims are not released or re-denominated for a conversion.
                donors[i] = (int)Math.Max(0, ordinaryFree - Math.Max(future, cost.Get(type)));
                missing[i] = Math.Max(0, cost.Get(type) - free[i]); units += missing[i];
                external[i] = cost.Get(type);
            }
            if (units == 0 || units > AiConfigV2.tradeMaxPreviewUnits || (long)root.TradeRate + units > int.MaxValue) return false;
            // Reserve large rates first. Largest-donor-first in chronological order can
            // falsely reject a feasible quote (donors 3+2, rates 2+3).
            var available = (int[])donors.Clone();
            for (int i = 0; i < 4; i++) if (missing[i] > 0) available[i] = 0;
            if (!TryAllocateDonors(available, root.TradeRate, (int)units, out var assignment)) return false;
            var steps = new List<TradeStep>(); int stepIndex = 0;
            for (int target = 0; target < 4; target++)
                for (int n = 0; n < missing[target]; n++)
                {
                    int donor = assignment[stepIndex]; int rate = root.TradeRate + stepIndex++;
                    if ((long)external[donor] + rate > int.MaxValue) return false;
                    external[donor] += rate; external[target]--;
                    steps.Add(new TradeStep((ResourceType)donor, (ResourceType)target));
                }
            plan = new TradeFundingPlan(root.TradeRate, steps,
                new ResourceCost(external[0], external[1], external[2], external[3]));
            return true;
        }

        // Pure bounded bin allocation; the caller's capacities are never mutated.
        internal static bool TryAllocateDonors(int[] capacities, int rate, int count, out int[] allocation)
        {
            allocation = null;
            if (capacities == null || capacities.Length != 4 || rate < 2 || count < 1
                || count > AiConfigV2.tradeMaxPreviewUnits || (long)rate + count > int.MaxValue) return false;
            var remaining = (int[])capacities.Clone();
            if (remaining.Any(x => x < 0)) return false;
            var chosen = new int[count]; int nodes = 0;
            bool Place(int index)
            {
                if (index < 0) return true;
                if (++nodes > AiConfigV2.tradeMaxDonorSearchNodes) return false;
                int price = rate + index;
                long total = remaining.Sum(x => (long)x);
                if (total < (long)(index + 1) * rate + (long)index * (index + 1) / 2) return false;
                var seen = new HashSet<int>();
                foreach (int donor in Enumerable.Range(0, 4).OrderBy(i => remaining[i]))
                {
                    if (remaining[donor] < price || !seen.Add(remaining[donor])) continue;
                    remaining[donor] -= price; chosen[index] = donor;
                    if (Place(index - 1)) return true;
                    remaining[donor] += price;
                }
                return false;
            }
            if (!Place(count - 1)) return false;
            allocation = chosen; return true;
        }

        internal static ResourceCost PricedCost(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ResourceCost cost, SpendAuthority authority = default) =>
            TryPlan(player, root, ctx, cost, out var plan, authority) ? plan.ExternalCost : cost;

        internal static bool Fund(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ResourceCost cost, SpendAuthority authority = default)
        {
            if (StrategicSpendability.FitsSpendableResources(player, root, ctx, cost, authority)) return true;
            if (!TryPlan(player, root, ctx, cost, out var quote, authority)) return false;
            if (!TradeActions.TryExchange(player, quote.Rate, quote.Steps, GameSession.Generation, out _)) return false;
            WorldDeltaLifecycle.Apply(player, ctx.TurnNumber, new WorldDelta(true, StrategicInvalidationReason.Resources));
            AiDebugLog.Write($"[AI][V2][Trade] {player.Nickname}: exchanges={quote.Steps.Count} rate={quote.Rate}->{root.TradeRate} AP=0; continuing selected action");
            return StrategicSpendability.FitsSpendableResources(player, root, ctx, cost, authority);
        }

        internal static float AccessValue(WorldSnapshot snap)
        {
            if (snap?.Self == null || snap.Self.HasTrade) return 0f;
            if (snap.Economy == null) return 0f;
            var need = snap.Economy.DeckResourceNeed;
            bool donor = ResourceBundle.All.Any(t => snap.Self.Stockpile.Get(t) - need.Get(t)
                - snap.Economy.ForecastOperationalNeed.Get(t) >= snap.Self.TradeRate);
            bool deficit = ResourceBundle.All.Any(t => need.Get(t) + snap.Economy.ForecastOperationalNeed.Get(t) > snap.Self.Stockpile.Get(t));
            return donor && deficit ? AiConfigV2.tradeAccessValue : 0f;
        }
    }
}
