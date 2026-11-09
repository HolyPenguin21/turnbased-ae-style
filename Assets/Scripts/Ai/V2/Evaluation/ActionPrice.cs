using System;
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
            ScarcityProfile(snap, spendableResource)[(int)type];

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
            var price = new float[4];
            if (snap?.Self == null)
            {
                for (int i = 0; i < 4; i++) price[i] = 1f;
                return price;
            }
            var supply = new float[4];
            foreach (ResourceType type in ResourceBundle.All)
            {
                float availableNow = spendableResource != null
                    ? Mathf.Max(0f, spendableResource(type))
                    : snap.Self.Stockpile.Get(type);
                supply[(int)type] = availableNow + Mathf.Max(0f, snap.Self.PerTurnIncome.Get(type))
                    * Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns);
            }
            // Pure in (snapshot cards, supply): memoized per snapshot so the hundreds of price
            // reads of one card round sort the deck once. Hand identity is part of the key.
            ProfileKey key = ProfileKey.Of(snap, supply);
            var memo = ProfileMemo.GetOrCreateValue(snap);
            if (memo.TryGetValue(key, out float[] cached))
                return (float[])cached.Clone();
            DeckPlayability play = DeckPlayability.Of(CardCosts(snap), supply);
            for (int i = 0; i < 4; i++)
                price[i] = PressurePrice(play.EffectiveDemand(i), supply[i]);
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
            if (memo.Count >= MaxProfilesPerSnapshot) memo.Clear();
            memo[key] = (float[])price.Clone();
            return price;
        }

        private const int MaxProfilesPerSnapshot = 64;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            System.Collections.Generic.Dictionary<ProfileKey, float[]>> ProfileMemo =
            new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
                System.Collections.Generic.Dictionary<ProfileKey, float[]>>();

        private readonly struct ProfileKey : IEquatable<ProfileKey>
        {
            private readonly int _h, _e, _m, _t, _handCount, _deckCount;
            private readonly long _handIds;

            private ProfileKey(int h, int e, int m, int t, int handCount, long handIds, int deckCount)
            {
                _h = h; _e = e; _m = m; _t = t;
                _handCount = handCount; _handIds = handIds; _deckCount = deckCount;
            }

            internal static ProfileKey Of(WorldSnapshot snap, float[] supply)
            {
                int handCount = 0, deckCount = 0;
                long ids = 0;
                if (snap.Self.Hand != null)
                    foreach (CardData card in snap.Self.Hand)
                    {
                        handCount++;
                        ids = ids * 31 + (card?.RuntimeId ?? -1);
                    }
                if (snap.Self.Deck != null)
                    foreach (CardDefinition _ in snap.Self.Deck) deckCount++;
                return new ProfileKey(Mathf.RoundToInt(supply[0] * 100f), Mathf.RoundToInt(supply[1] * 100f),
                    Mathf.RoundToInt(supply[2] * 100f), Mathf.RoundToInt(supply[3] * 100f),
                    handCount, ids, deckCount);
            }

            public bool Equals(ProfileKey o) => _h == o._h && _e == o._e && _m == o._m && _t == o._t
                && _handCount == o._handCount && _handIds == o._handIds && _deckCount == o._deckCount;
            public override bool Equals(object obj) => obj is ProfileKey o && Equals(o);
            public override int GetHashCode() =>
                (((((_h * 397) ^ _e) * 397 ^ _m) * 397 ^ _t) * 397 ^ _handCount) * 397 ^ _handIds.GetHashCode() ^ _deckCount;
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
            var supply = new float[4];
            foreach (ResourceType t in ResourceBundle.All)
                supply[(int)t] = snap.Self.Stockpile.Get(t) + Mathf.Max(0f, snap.Self.PerTurnIncome.Get(t))
                    * Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns);
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
            float[] prices = null;
            float total = 0f;
            foreach (ResourceType type in ResourceBundle.All)
            {
                float units = Mathf.Max(0f, amount(type));
                if (units > 0f)
                {
                    prices ??= ScarcityProfile(snap, spendableResource);
                    total += units * AiConfigV2.actionPriceResourceAp * prices[(int)type];
                }
            }
            return total;
        }

        internal static float Resources(ResourceCost cost, WorldSnapshot snap = null,
            Func<ResourceType, float> spendableResource = null) =>
            cost == null ? 0f : Resources(t => cost.Get(t), snap, spendableResource);

        internal static float Resources(ResourceVector cost, WorldSnapshot snap = null) =>
            Resources(t => t == ResourceType.Human ? cost.Human
                : t == ResourceType.Energy ? cost.Energy
                : t == ResourceType.Materials ? cost.Materials : cost.Tech, snap);

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
