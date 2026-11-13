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
            if (player == null || ReferenceEquals(root, null) || root == null || ctx == null || cost == null
                || root.TradeRate == int.MaxValue) return false;
            if (StrategicSpendability.FitsSpendableResources(player, root, ctx, cost, authority)) return false;
            if (!previewAccess && (!root.TradeTurnOpen || !TradeActions.HasAccess(player))) return false;
            var hand = AiHandRegistry.Peek(player);
            var forecast = ResourceStarvationRegistry.ForecastOperationalNeed(player, ctx.TurnNumber);
            var free = new int[4]; var donors = new int[4]; var missing = new int[4]; var external = new int[4];
            var ordinaryFree = new float[4]; var operational = new float[4]; var income = new float[4];
            long units = 0;
            for (int i = 0; i < 4; i++)
            {
                var type = (ResourceType)i;
                if (cost.Get(type) < 0) return false;
                free[i] = Mathf.Max(0, Mathf.FloorToInt(StrategicSpendability.SpendableAmount(player, root, ctx, type, authority)));
                // Only this action's own held donor units may be used; foreign holds remain protected.
                ordinaryFree[i] = Mathf.Max(0, Mathf.FloorToInt(StrategicSpendability.SpendableAmount(player, root, ctx, type, new SpendAuthority(authority.Owner, false))));
                operational[i] = forecast.Get(type);
                income[i] = ctx.Map != null ? IncomeProjection.IncomeFor(player, type, ctx.Map) : 0f;
                missing[i] = Math.Max(0, cost.Get(type) - free[i]); units += missing[i];
                external[i] = cost.Get(type);
            }
            // The same hand + remaining deck bills ActionPrice prices (held cards as played).
            var cards = new List<ResourceCost>();
            if (hand != null)
            {
                cards.AddRange(hand.Hand.Select(CardCostRules.PlayResources));
                cards.AddRange(hand.RemainingDeck.Select(c => c?.resourceCost));
            }
            float[] protect = ProtectedStock(cards, ordinaryFree, operational, income,
                AiConfigV2.economyDeckNeedHorizonTurns);
            for (int i = 0; i < 4; i++)
                donors[i] = (int)Math.Max(0, Math.Floor(ordinaryFree[i] - Math.Max(protect[i], cost.Get((ResourceType)i))));
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

        // Mission requirements describe the action's resources; allocation reserves the
        // external debit. Ceil each physical component so fractional forecasts never underfund.
        internal static ResourceCost CostOf(ResourceVector cost) => new ResourceCost(
            Mathf.CeilToInt(Mathf.Max(0f, cost.Human)), Mathf.CeilToInt(Mathf.Max(0f, cost.Energy)),
            Mathf.CeilToInt(Mathf.Max(0f, cost.Materials)), Mathf.CeilToInt(Mathf.Max(0f, cost.Tech)));

        internal static ResourceVector PricedVector(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ResourceVector cost, SpendAuthority authority = default) =>
            cost.AnyPhysical && !ReferenceEquals(root, null)
                && TryPlan(player, root, ctx, CostOf(cost), out var quote, authority)
                ? ProvisioningManager.CostVector(quote.ExternalCost) : cost;

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

        // The payment of a quote, per donor resource (step i pays Rate + i).
        internal static ResourceCost Payment(TradeFundingPlan plan)
        {
            var paid = new ResourceCost();
            if (plan == null) return paid;
            for (int i = 0; i < plan.Steps.Count; i++)
                switch (plan.Steps[i].Give)
                {
                    case ResourceType.Human: paid.human += plan.Rate + i; break;
                    case ResourceType.Energy: paid.energy += plan.Rate + i; break;
                    case ResourceType.Materials: paid.materials += plan.Rate + i; break;
                    case ResourceType.Tech: paid.tech += plan.Rate + i; break;
                }
            return paid;
        }

        // Phase B proactive conversion of protected surplus into `type`, still missing for the
        // hand/deck/operational need. The whole profitable batch is ONE exchange (one tempo
        // action): k grows while the priced total gain still grows. Utility is card score.
        internal static bool TryPlanConversion(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ResourceType type, out TradeFundingPlan best, out ResourceCost give, out float utility)
        {
            best = null; give = null; utility = 0f;
            if (snap?.Economy == null || ReferenceEquals(root, null) || root == null || ctx == null) return false;
            // Units still missing over the economy horizon (income counted, the same supply
            // ActionPrice.Scarcity reads); income that covers the need is no reason to buy.
            int deficit = Mathf.FloorToInt(snap.Economy.DeckResourceNeed.Get(type)
                + snap.Economy.ForecastOperationalNeed.Get(type) - root.GetResource(type)
                - Mathf.Max(0f, snap.Self?.PerTurnIncome.Get(type) ?? 0f) * AiConfigV2.economyDeckNeedHorizonTurns);
            if (deficit < 1) return false;
            // Quote against the spendable amount so the batch buys exactly k units.
            int free = Mathf.Max(0, Mathf.FloorToInt(StrategicSpendability.SpendableAmount(player, root, ctx, type)));
            float unit = ActionPrice.Resources(t => t == type ? 1f : 0f, snap);
            float bestGain = 0f;
            for (int k = 1; k <= Math.Min(deficit, AiConfigV2.tradeMaxPreviewUnits); k++)
            {
                if ((long)free + k > int.MaxValue) break;
                var want = new ResourceCost(type == ResourceType.Human ? free + k : 0,
                    type == ResourceType.Energy ? free + k : 0,
                    type == ResourceType.Materials ? free + k : 0,
                    type == ResourceType.Tech ? free + k : 0);
                if (!TryPlan(player, root, ctx, want, out var plan)) break;
                ResourceCost paid = Payment(plan);
                float gain = k * unit - ActionPrice.Resources(paid, snap);
                if (gain <= bestGain) break;
                bestGain = gain; best = plan; give = paid;
            }
            if (best == null) return false;
            utility = ActionPrice.ToCardScore(bestGain);
            return utility > 0f;
        }

        // Protected stock per resource (one rule for funding quotes, the access value and the
        // proactive conversion): what the hand + remaining deck can still use within the economy
        // horizon - DeckPlayability's effective demand (playable cards + cards waiting on THIS
        // type) on the horizon supply, capped by that supply - net of the income the horizon
        // brings, plus the full operational forecast. ActionPrice.ScarcityProfile reads the same
        // DeckPlayability. Energy carried by cards that wait on Materials is idle: useless
        // accumulation (owner TZ 2026-11-08: the Vessels' Human, a spent deck, Korrin E/T).
        internal static float[] ProtectedStock(IEnumerable<ResourceCost> cards, float[] stock,
            float[] operational, float[] income, float horizonTurns)
        {
            var protect = new float[4];
            if (stock == null || operational == null || income == null
                || stock.Length != 4 || operational.Length != 4 || income.Length != 4)
                return protect;
            float horizon = Mathf.Max(0f, horizonTurns);
            var supply = new float[4];
            for (int i = 0; i < 4; i++)
                supply[i] = Mathf.Max(0f, stock[i]) + Mathf.Max(0f, income[i]) * horizon;
            DeckPlayability play = DeckPlayability.Of(cards, supply);
            for (int i = 0; i < 4; i++)
                protect[i] = Mathf.Max(0f, Mathf.Min(play.EffectiveDemand(i), supply[i])
                        - Mathf.Max(0f, income[i]) * horizon)
                    + Mathf.Max(0f, operational[i]);
            return protect;
        }

        // Value of gaining Trade access, in AP-equivalents: what the AI's own conversion policy
        // (TryPlanConversion each turn while a unit's priced gain is positive) realises over
        // `horizonTurns` of projected income, with ProtectedStock's donors and the horizon
        // shortfall as targets. Zero when access already exists.
        internal static float AccessValueApEquivalents(WorldSnapshot snap, int horizonTurns) =>
            AccessValueApEquivalents(snap, horizonTurns, out _);

        // 2026-11-13 (owner, P5, match Korrin vs Brynn) — plus the AP the trade unblocks: Brynn
        // held the Trading Post T1-T29 with Energy 8-13, Materials 0 and 3-9 AP idle every turn
        // (net -0.87..-0.02); the conversion price alone never saw the stranded AP or the blocked
        // hand. Per horizon turn: the smaller of the AP left idle on average (this turn's AP minus
        // the witnessed mean AP demand, ApTurnPressure) and the AP of the cheapest held card the
        // stock cannot pay now that a conversion from the donors above ProtectedStock can.
        // AP-equivalents like the conversion gain; the caller's effectGlobalRecurringValueCap
        // bounds the sum. Zero without idle AP or without such a card.
        internal static float AccessValueApEquivalents(WorldSnapshot snap, int horizonTurns, out float unlockedAp)
        {
            unlockedAp = 0f;
            if (snap?.Self == null || snap.Self.HasTrade || snap.Economy == null) return 0f;
            var stock = new float[4]; var operational = new float[4]; var income = new float[4];
            foreach (ResourceType t in ResourceBundle.All)
            {
                int i = (int)t;
                stock[i] = snap.Self.Stockpile.Get(t);
                operational[i] = snap.Economy.ForecastOperationalNeed.Get(t);
                income[i] = Mathf.Max(0f, snap.Self.PerTurnIncome.Get(t));
            }
            List<ResourceCost> cards = ActionPrice.CardCosts(snap).ToList();
            float conversion = SimulateConversionGain(cards, stock, operational, income,
                ActionPrice.ScarcityProfile(snap), snap.Self.TradeRate, horizonTurns);
            ApActionEconomySnapshot ap = snap.Self.ApEconomy;
            float idleAp = ap?.WitnessedApDemand.HasValue == true
                ? Mathf.Max(0f, ap.BaseActionPoints - ap.WitnessedApDemand.Value) : 0f;
            if (idleAp > 0f)
            {
                float[] protect = ProtectedStock(cards, stock, operational, income, horizonTurns);
                float cardAp = UnlockedCardAp((snap.Self.Hand ?? (IReadOnlyList<CardData>)Array.Empty<CardData>())
                        .Where(c => c != null).Select(c => (CardCostRules.PlayResources(c), c.EffectivePlayApCost)),
                    stock, protect, Math.Max(TradeActions.MinimumRate, snap.Self.TradeRate));
                unlockedAp = Mathf.Min(idleAp, cardAp) * Mathf.Max(0, horizonTurns);
            }
            return conversion + unlockedAp;
        }

        // Pure: the AP of the cheapest held card (AP cost > 0) the stock cannot pay now while a
        // progressive conversion at `rate` from the donors above `protect` (and above the card's
        // own need of that type) can; 0 when there is none.
        internal static float UnlockedCardAp(IEnumerable<(ResourceCost Cost, int Ap)> hand, float[] stock,
            float[] protect, int rate)
        {
            if (hand == null || stock == null || protect == null || stock.Length != 4 || protect.Length != 4)
                return 0f;
            float best = float.MaxValue;
            foreach ((ResourceCost cost, int ap) in hand)
            {
                if (cost == null || ap <= 0 || ap >= best) continue;
                int units = 0;
                float surplus = 0f;
                for (int i = 0; i < 4; i++)
                {
                    int need = cost.Get((ResourceType)i);
                    int shortBy = Mathf.Max(0, Mathf.CeilToInt(need - stock[i]));
                    units += shortBy;
                    if (shortBy == 0)
                        surplus += Mathf.Max(0f, Mathf.Floor(stock[i] - Mathf.Max(protect[i], need)));
                }
                if (units == 0 || units > AiConfigV2.tradeMaxPreviewUnits) continue;
                long price = (long)units * rate + (long)units * (units - 1) / 2;
                if (surplus + AiConfigV2.allocatorSliceEpsilon >= price)
                    best = ap;
            }
            return best == float.MaxValue ? 0f : best;
        }

        // Pure: greedy per-turn conversions (best-priced missing unit from the cheapest donor
        // above ProtectedStock for the remaining horizon) with the real progressive rate and its
        // idle-turn decay.
        internal static float SimulateConversionGain(IReadOnlyList<ResourceCost> cards, float[] stock,
            float[] operational, float[] income, float[] scarcity, int rate, int horizonTurns)
        {
            if (stock == null || operational == null || income == null || scarcity == null
                || stock.Length != 4 || operational.Length != 4
                || income.Length != 4 || scarcity.Length != 4) return 0f;
            float[] demand = DeckPlayability.Of(cards, new float[4]).Demand;
            var s = (float[])stock.Clone();
            int r = Math.Max(TradeActions.MinimumRate, rate);
            float total = 0f;
            for (int turn = 0; turn < horizonTurns; turn++)
            {
                if (turn > 0) for (int i = 0; i < 4; i++) s[i] += Mathf.Max(0f, income[i]);
                int horizonLeft = horizonTurns - turn;
                bool traded = false;
                for (int n = 0; n < AiConfigV2.tradeMaxPreviewUnits && r < int.MaxValue; n++)
                {
                    float[] protect = ProtectedStock(cards, s, operational, income, horizonLeft);
                    int target = -1, donor = -1;
                    for (int i = 0; i < 4; i++)
                    {
                        float shortfall = demand[i] + operational[i] - s[i] - Mathf.Max(0f, income[i]) * horizonLeft;
                        if (shortfall >= 1f && (target < 0 || scarcity[i] > scarcity[target])) target = i;
                    }
                    if (target < 0) break;
                    for (int i = 0; i < 4; i++)
                        if (i != target && s[i] - protect[i] >= r && (donor < 0 || scarcity[i] < scarcity[donor])) donor = i;
                    if (donor < 0) break;
                    float gain = (scarcity[target] - r * scarcity[donor]) * AiConfigV2.actionPriceResourceAp;
                    if (gain <= 0f) break;
                    total += gain; s[donor] -= r; s[target] += 1f; r++; traded = true;
                }
                if (!traded) r = Math.Max(TradeActions.MinimumRate, r - 1);
            }
            return total;
        }
    }
}
