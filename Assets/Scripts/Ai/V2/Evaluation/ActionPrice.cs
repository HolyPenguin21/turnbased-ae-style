using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ACTION PRICE — the ONE price table of AI V2
    // ===========================================================================================
    //  Everything an action spends is priced here in AP-equivalents, and only here:
    //    · 1 AP = 1 AP-equivalent, whatever it pays for — a card play, a Challenge, an army's
    //      activation now or on a later turn of its march. AP is fungible; there is no discount
    //      for "only moving".
    //    · 1 unit of H/E/M/T = actionPriceResourceAp AP-equivalents x Scarcity(type): pending
    //      hand/deck demand for that resource against stock + income over the economy horizon.
    //  The two value currencies are scales of this ONE table, never a second price:
    //    · TaskScore (world tasks): taskScorePerApEquivalent points per AP-equivalent;
    //    · card score (StrategicCardEvaluator): cardScorePerApEquivalent per AP-equivalent.
    //  A price always reaches a score through one of those two scales.
    // ===========================================================================================
    internal static class ActionPrice
    {
        internal static float Ap(float ap) => Mathf.Max(0f, ap);

        // Recurring activation of a multi-turn march: the army's per-turn activation, paid again
        // on each turn after the first (ArmyData.HasActivatedThisTurn resets every turn).
        internal static float RecurringAp(float perTurnActivationAp, float etaTurns) =>
            Mathf.Max(0f, perTurnActivationAp) * Mathf.Max(0f, etaTurns - 1f);

        // Price of one unit of `type` - see ScarcityProfile. 1 when nothing is known (no snapshot).
        internal static float Scarcity(ResourceType type, WorldSnapshot snap,
            Func<ResourceType, float> spendableResource = null) =>
            Prices(snap, AvailableResources(snap, spendableResource)).Get(type);

        // ===========================================================================================
        //  SCARCITY — one price per resource, read against what the hand + deck can actually play
        // ===========================================================================================
        //  Supply per type = available now + income x economyDeckNeedHorizonTurns. The cards are not
        //  priced one resource at a time: a card is played only when ALL its resources are there.
        //  DeckPlayability fills the horizon supply with the hand + remaining deck (cheapest card
        //  first); a card it cannot pay is blocked by the types it lacks.
        //    · effective demand of a type = the playable cards' share + the blocked cards that wait
        //      on THIS type. Cost a card carries for a type it does not lack, while another type
        //      blocks it, is idle: it does not make that type scarce (2026-11-08, Korrin T21:
        //      Energy 12 / Tech 12 priced ~1.0 while every card waited on Materials 1);
        //    · price = 0.2..1.8 by effective demand / (effective demand + supply) - unchanged
        //      whenever every card is payable, so a covered economy prices exactly as before;
        //    · a blocking type also carries the value it unlocks: the idle resources of the cards
        //      it blocks (at their own price), per missing unit. Value moves to the bottleneck
        //      instead of vanishing - a card's total price stays comparable, Materials that unlock
        //      three stalled cards are no longer capped at 1.8.
        //  One price table: every Resources/Scarcity reader (card cost, TaskScore delivery, hold,
        //  trade) sees this same profile.
        // ===========================================================================================
        internal static float[] ScarcityProfile(WorldSnapshot snap,
            Func<ResourceType, float> spendableResource = null)
        {
            ResourceBundle price = Prices(snap, AvailableResources(snap, spendableResource));
            return new[] { price.Human, price.Energy, price.Materials, price.Tech };
        }

        internal static ResourceBundle AvailableResources(WorldSnapshot snap,
            Func<ResourceType, float> spendableResource)
        {
            if (snap?.Self == null) return default;
            if (spendableResource == null) return snap.Self.Stockpile;
            var resources = new ResourceBundle();
            foreach (ResourceType type in ResourceBundle.All)
                resources.Add(type, Mathf.Max(0f, spendableResource(type)));
            return resources;
        }

        private static ResourceBundle Prices(WorldSnapshot snap, ResourceBundle availableNow)
        {
            if (snap?.Self == null)
                return new ResourceBundle { Human = 1f, Energy = 1f, Materials = 1f, Tech = 1f };

            ResourceBundle supply = HorizonSupply(snap, availableNow);
            SnapshotProfiles memo = ProfilesFor(snap);
            ProfileKey key = ProfileKey.Of(supply);
            if (memo.TryGet(key, out ResourceBundle cached))
                return cached;
            var available = new[] { supply.Human, supply.Energy, supply.Materials, supply.Tech };
            var price = new float[4];
            DeckPlayability play = DeckPlayability.Of(CardCosts(snap), available);
            for (int i = 0; i < 4; i++)
                price[i] = PressurePrice(play.EffectiveDemand(i), available[i]);
            var unlocked = new float[4];
            var missing = new float[4];
            foreach (DeckPlayability.BlockedCard card in play.Blocked)
            {
                float idle = 0f, lacking = 0f;
                for (int i = 0; i < 4; i++)
                {
                    if (card.Missing[i] > 0f) lacking += card.Missing[i];
                    else idle += card.Cost[i] * price[i];
                }
                if (lacking <= 0f) continue;
                for (int i = 0; i < 4; i++)
                    if (card.Missing[i] > 0f)
                    {
                        unlocked[i] += idle * card.Missing[i] / lacking;
                        missing[i] += card.Missing[i];
                    }
            }
            for (int i = 0; i < 4; i++)
                if (missing[i] > 0f)
                    price[i] += unlocked[i] / missing[i];
            var result = new ResourceBundle { Human = price[0], Energy = price[1], Materials = price[2], Tech = price[3] };
            memo.Add(key, result);
            return result;
        }

        private const int MaxProfilesPerSnapshot = 64;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            SnapshotProfiles> ProfileMemo =
            new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
                SnapshotProfiles>();

        private static SnapshotProfiles ProfilesFor(WorldSnapshot snap)
        {
            var memo = ProfileMemo.GetOrCreateValue(snap);
            memo.RefreshCosts(snap);
            return memo;
        }

        // Development uses the same ordered bills as the price instead of totals by resource.
        internal static string CardDemandKey(WorldSnapshot snap) =>
            snap?.Self == null ? string.Empty : ProfilesFor(snap).DemandKey;

        private sealed class SnapshotProfiles
        {
            // Detached values, not ResourceCost references that can change behind the cache.
            // A flat array also avoids one new object per card whenever demand is refreshed.
            private int[] costs = Array.Empty<int>();
            private readonly Dictionary<ProfileKey, LinkedListNode<(ProfileKey Key, ResourceBundle Price)>> profiles = new();
            private readonly LinkedList<(ProfileKey Key, ResourceBundle Price)> byRecency = new();
            private string demandKey;

            public SnapshotProfiles() { }

            internal string DemandKey => demandKey ??= string.Join(",", costs);

            internal bool TryGet(ProfileKey key, out ResourceBundle price)
            {
                if (!profiles.TryGetValue(key, out var node))
                {
                    price = default;
                    return false;
                }
                if (node != byRecency.First)
                {
                    byRecency.Remove(node);
                    byRecency.AddFirst(node);
                }
                price = node.Value.Price;
                return true;
            }

            internal void Add(ProfileKey key, ResourceBundle price)
            {
                if (profiles.Count < MaxProfilesPerSnapshot)
                {
                    profiles.Add(key, byRecency.AddFirst((key, price)));
                    return;
                }
                var node = byRecency.Last;
                profiles.Remove(node.Value.Key);
                byRecency.RemoveLast();
                node.Value = (key, price);
                byRecency.AddFirst(node);
                profiles.Add(key, node);
            }

            internal void RefreshCosts(WorldSnapshot snap)
            {
                var hand = snap.Self.Hand;
                var deck = snap.Self.Deck;
                int handCount = hand?.Count ?? 0, deckCount = deck?.Count ?? 0;
                bool matches = costs.Length == (handCount + deckCount) * 4;
                for (int i = 0; matches && i < handCount; i++)
                    matches = Matches(hand[i]?.EffectivePlayResourceCost, i * 4);
                for (int i = 0; matches && i < deckCount; i++)
                    matches = Matches(deck[i]?.resourceCost, (handCount + i) * 4);
                if (matches) return;

                if (costs.Length != (handCount + deckCount) * 4)
                    costs = new int[(handCount + deckCount) * 4];
                for (int i = 0; i < handCount; i++) Store(hand[i]?.EffectivePlayResourceCost, i * 4);
                for (int i = 0; i < deckCount; i++) Store(deck[i]?.resourceCost, (handCount + i) * 4);
                profiles.Clear();
                byRecency.Clear();
                demandKey = null;
            }

            private bool Matches(ResourceCost cost, int index) =>
                costs[index] == (cost?.human ?? 0) && costs[index + 1] == (cost?.energy ?? 0)
                && costs[index + 2] == (cost?.materials ?? 0) && costs[index + 3] == (cost?.tech ?? 0);

            private void Store(ResourceCost cost, int index)
            {
                costs[index] = cost?.human ?? 0;
                costs[index + 1] = cost?.energy ?? 0;
                costs[index + 2] = cost?.materials ?? 0;
                costs[index + 3] = cost?.tech ?? 0;
            }
        }

        private readonly struct ProfileKey : IEquatable<ProfileKey>
        {
            private readonly int _h, _e, _m, _t;

            private ProfileKey(int h, int e, int m, int t)
            {
                _h = h; _e = e; _m = m; _t = t;
            }

            // Calculation uses the exact float supply, so the key must not round it.
            internal static ProfileKey Of(ResourceBundle supply) => new ProfileKey(
                BitConverter.SingleToInt32Bits(supply.Human), BitConverter.SingleToInt32Bits(supply.Energy),
                BitConverter.SingleToInt32Bits(supply.Materials), BitConverter.SingleToInt32Bits(supply.Tech));

            public bool Equals(ProfileKey o) => _h == o._h && _e == o._e && _m == o._m && _t == o._t;
            public override bool Equals(object obj) => obj is ProfileKey o && Equals(o);
            public override int GetHashCode() =>
                (((_h * 397) ^ _e) * 397 ^ _m) * 397 ^ _t;
        }

        // The demand a type really faces (playable + waiting on it), on the price's own supply.
        // Development forecasts net this out instead of the raw hand + deck cost.
        internal static float[] EffectiveCardDemand(WorldSnapshot snap)
        {
            var demand = new float[4];
            if (snap?.Self == null)
                return demand;
            DeckPlayability play = DeckPlayability.Of(CardCosts(snap), HorizonSupply(snap));
            for (int i = 0; i < 4; i++) demand[i] = play.EffectiveDemand(i);
            return demand;
        }

        // The types the hand + deck waits on: some card the horizon supply cannot pay lacks them
        // (DeckPlayability on the same supply as EffectiveCardDemand). All false when every card
        // is payable — a covered economy has no bottleneck. Index = (int)ResourceType.
        internal static bool[] BottleneckTypes(WorldSnapshot snap)
        {
            var blocking = new bool[4];
            if (snap?.Self == null)
                return blocking;
            DeckPlayability play = DeckPlayability.Of(CardCosts(snap), HorizonSupply(snap));
            foreach (DeckPlayability.BlockedCard card in play.Blocked)
                for (int i = 0; i < 4; i++)
                    if (card.Missing[i] > AiConfigV2.allocatorSliceEpsilon)
                        blocking[i] = true;
            return blocking;
        }

        private static float[] HorizonSupply(WorldSnapshot snap)
        {
            ResourceBundle supply = HorizonSupply(snap, snap.Self.Stockpile);
            return new[] { supply.Human, supply.Energy, supply.Materials, supply.Tech };
        }

        private static ResourceBundle HorizonSupply(WorldSnapshot snap, ResourceBundle availableNow)
        {
            var supply = new ResourceBundle();
            foreach (ResourceType type in ResourceBundle.All)
                supply.Add(type, availableNow.Get(type) + Mathf.Max(0f, snap.Self.PerTurnIncome.Get(type))
                    * Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns));
            return supply;
        }

        private static float PressurePrice(float demand, float supply)
        {
            float pressure = demand <= 0.0001f ? 0f
                : demand / Mathf.Max(0.0001f, demand + Mathf.Max(0f, supply));
            return Mathf.Lerp(AiConfigV2.actionPriceScarcityMin, AiConfigV2.actionPriceScarcityMax,
                Mathf.Clamp01(pressure));
        }

        // The resource bills of the hand (as played) and the remaining deck - the same sources
        // PendingCardResourceDemand sums.
        internal static System.Collections.Generic.IEnumerable<ResourceCost> CardCosts(WorldSnapshot snap)
        {
            if (snap?.Self?.Hand != null)
                foreach (CardData card in snap.Self.Hand)
                    if (card?.EffectivePlayResourceCost != null) yield return card.EffectivePlayResourceCost;
            if (snap?.Self?.Deck != null)
                foreach (CardDefinition card in snap.Self.Deck)
                    if (card?.resourceCost != null) yield return card.resourceCost;
        }

        // AP-equivalents of a resource bundle, each type at its own scarcity (one profile per call).
        internal static float Resources(Func<ResourceType, float> amount, WorldSnapshot snap = null,
            Func<ResourceType, float> spendableResource = null)
        {
            if (amount == null)
                return 0f;
            ResourceBundle? prices = null;
            float total = 0f;
            foreach (ResourceType type in ResourceBundle.All)
            {
                float units = Mathf.Max(0f, amount(type));
                if (units > 0f)
                {
                    prices ??= Prices(snap, AvailableResources(snap, spendableResource));
                    total += units * AiConfigV2.actionPriceResourceAp * prices.Value.Get(type);
                }
            }
            return total;
        }

        internal static float Resources(ResourceCost cost, WorldSnapshot snap = null,
            Func<ResourceType, float> spendableResource = null)
        {
            ResourceBundle bill = Bill(cost);
            return bill.Sum > 0 ? PriceBill(bill, Prices(snap, AvailableResources(snap, spendableResource))) : 0f;
        }

        internal static float Resources(ResourceCost cost, WorldSnapshot snap, ResourceBundle availableNow)
        {
            ResourceBundle bill = Bill(cost);
            return bill.Sum > 0 ? PriceBill(bill, Prices(snap, availableNow)) : 0f;
        }

        internal static float Resources(ResourceVector cost, WorldSnapshot snap = null)
        {
            var bill = new ResourceBundle { Human = Mathf.Max(0f, cost.Human), Energy = Mathf.Max(0f, cost.Energy),
                Materials = Mathf.Max(0f, cost.Materials), Tech = Mathf.Max(0f, cost.Tech) };
            return bill.Human > 0 || bill.Energy > 0 || bill.Materials > 0 || bill.Tech > 0
                ? PriceBill(bill, Prices(snap, AvailableResources(snap, null))) : 0f;
        }

        private static ResourceBundle Bill(ResourceCost cost) => cost == null ? default : new ResourceBundle
        {
            Human = Mathf.Max(0, cost.human), Energy = Mathf.Max(0, cost.energy),
            Materials = Mathf.Max(0, cost.materials), Tech = Mathf.Max(0, cost.tech),
        };

        private static float PriceBill(ResourceBundle bill, ResourceBundle prices)
        {
            float total = 0;
            foreach (ResourceType type in ResourceBundle.All)
            {
                float units = bill.Get(type);
                if (units > 0)
                    total += units * AiConfigV2.actionPriceResourceAp * prices.Get(type);
            }
            return total;
        }

        // The two currencies — the only conversion of a price into score units.
        internal static float ToTaskScore(float apEquivalents) =>
            Mathf.Max(0f, apEquivalents) * AiConfigV2.taskScorePerApEquivalent;

        internal static float ToCardScore(float apEquivalents) =>
            Mathf.Max(0f, apEquivalents) * AiConfigV2.cardScorePerApEquivalent;

        // The MoverOpportunityCost raw fact (TaskScore units) of a plan that takes a garrison hero
        // out as its fallback commander (AiConfigV2.garrisonHeroFallbackApEquivalent).
        internal static float GarrisonHeroFallback(bool usesGarrisonHero) =>
            usesGarrisonHero ? ToTaskScore(AiConfigV2.garrisonHeroFallbackApEquivalent) : 0f;

        // A card-score value (StrategicCardEvaluator / StrategicEffectRegistry units) read on the
        // TaskScore scale: the same AP-equivalent through the other currency. A TaskScore slot fed
        // from a card evaluator converts here, never by taking the card number as it is.
        internal static float CardScoreToTaskScore(float cardScore) =>
            ToTaskScore(Mathf.Max(0f, cardScore) / Mathf.Max(0.0001f, AiConfigV2.cardScorePerApEquivalent));

        // Inverse, for a planner that must rank an already-scored loss against AP.
        internal static float FromTaskScore(float scoreUnits) =>
            Mathf.Max(0f, scoreUnits) / Mathf.Max(0.0001f, AiConfigV2.taskScorePerApEquivalent);

        internal static float PendingCardResourceDemand(WorldSnapshot snap, ResourceType type)
        {
            float demand = 0f;
            if (snap?.Self?.Hand != null)
                foreach (CardData card in snap.Self.Hand)
                    demand += card?.EffectivePlayResourceCost?.Get(type) ?? 0;
            if (snap?.Self?.Deck != null)
                foreach (CardDefinition card in snap.Self.Deck)
                    demand += card?.resourceCost?.Get(type) ?? 0;
            return demand;
        }
    }

    // Pure: which cards of the hand + remaining deck a horizon supply can pay, cheapest card first
    // (most cards played). A card that does not fit is blocked by the types it lacks at that
    // point; later cards still try the rest. Read by ActionPrice.ScarcityProfile and
    // TradeFunding.ProtectedStock - the one answer to "what does the deck really still need".
    internal sealed class DeckPlayability
    {
        internal sealed class BlockedCard
        {
            internal readonly float[] Cost = new float[4];
            internal readonly float[] Missing = new float[4];
        }

        internal readonly float[] Demand = new float[4];
        internal readonly float[] Usable = new float[4];
        // Cost of blocked cards on the types they lack (demand that is waiting on that type).
        internal readonly float[] Waiting = new float[4];
        internal readonly System.Collections.Generic.List<BlockedCard> Blocked =
            new System.Collections.Generic.List<BlockedCard>();

        internal float EffectiveDemand(int type) => Usable[type] + Waiting[type];

        internal static DeckPlayability Of(System.Collections.Generic.IEnumerable<ResourceCost> costs,
            float[] supply)
        {
            var result = new DeckPlayability();
            var cards = new System.Collections.Generic.List<(float[] Cost, float Total, int Order)>();
            int order = 0;
            foreach (ResourceCost cost in costs ?? System.Array.Empty<ResourceCost>())
            {
                if (cost == null) continue;
                var c = new float[4];
                float total = 0f;
                foreach (ResourceType t in ResourceBundle.All)
                {
                    c[(int)t] = Mathf.Max(0, cost.Get(t));
                    total += c[(int)t];
                    result.Demand[(int)t] += c[(int)t];
                }
                if (total > 0f) cards.Add((c, total, order++));
            }
            cards.Sort((a, b) => a.Total != b.Total ? a.Total.CompareTo(b.Total) : a.Order.CompareTo(b.Order));
            var remaining = new float[4];
            for (int i = 0; i < 4; i++)
                remaining[i] = supply != null && supply.Length == 4 ? Mathf.Max(0f, supply[i]) : 0f;
            foreach (var card in cards)
            {
                bool fits = true;
                for (int i = 0; i < 4 && fits; i++)
                    fits = card.Cost[i] <= remaining[i] + AiConfigV2.allocatorSliceEpsilon;
                if (fits)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        remaining[i] -= card.Cost[i];
                        result.Usable[i] += card.Cost[i];
                    }
                    continue;
                }
                var blocked = new BlockedCard();
                for (int i = 0; i < 4; i++)
                {
                    blocked.Cost[i] = card.Cost[i];
                    blocked.Missing[i] = Mathf.Max(0f, card.Cost[i] - remaining[i]);
                    if (blocked.Missing[i] > AiConfigV2.allocatorSliceEpsilon)
                        result.Waiting[i] += card.Cost[i];
                    else
                        blocked.Missing[i] = 0f;
                }
                result.Blocked.Add(blocked);
            }
            return result;
        }
    }
}
