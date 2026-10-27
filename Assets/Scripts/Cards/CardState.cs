using System;
using System.Collections.Generic;

namespace Game.Cards
{
    // Authored keys identify definitions; runtime ids identify individual live copies.
    [Serializable]
    public sealed class CardState
    {
        public int runtimeId;
        public string definitionKey;
        public string equipmentKey;
        public string mutatorKey;
        public bool researchProductionCreated;
        public int acquiredTurn;

        public static CardState Capture(CardData card)
        {
            if (card == null) throw new InvalidOperationException("Cannot save a missing card.");
            return new CardState
            {
                runtimeId = card.RuntimeId, definitionKey = Key(card.Definition),
                equipmentKey = card.Equipment == null ? null : Key(card.Equipment),
                mutatorKey = card.Mutator == null ? null : Key(card.Mutator),
                researchProductionCreated = card.ResearchProductionCreated, acquiredTurn = card.AcquiredTurn
            };
        }

        public CardData Restore(Func<string, CardDefinition> resolve)
        {
            if (runtimeId <= 0) throw new InvalidOperationException("Invalid saved card runtime identity.");
            var definition = Resolve(definitionKey, resolve);
            var equipment = string.IsNullOrEmpty(equipmentKey) ? null : Resolve(equipmentKey, resolve);
            var mutator = string.IsNullOrEmpty(mutatorKey) ? null : Resolve(mutatorKey, resolve);
            var card = CardData.WithRuntimeId(definition, runtimeId);
            card.Equipment = equipment;
            card.Mutator = mutator;
            card.ResearchProductionCreated = researchProductionCreated;
            card.AcquiredTurn = acquiredTurn;
            return card;
        }

        internal static string Key(CardDefinition definition)
        {
            if (string.IsNullOrEmpty(definition?.authoredKey))
                throw new InvalidOperationException("Saved cards require an authored key.");
            return definition.authoredKey;
        }

        internal static CardDefinition Resolve(string key, Func<string, CardDefinition> resolve)
        {
            if (string.IsNullOrEmpty(key) || resolve == null)
                throw new InvalidOperationException("Missing saved card authored key or resolver.");
            var definition = resolve(key);
            if (definition == null || definition.authoredKey != key)
                throw new InvalidOperationException("Cannot resolve saved card: " + key);
            return definition;
        }
    }

    [Serializable]
    public sealed class HandState
    {
        public int capacity;
        public int currentTurn;
        public int mutationVersion;
        public List<CardState> cards = new List<CardState>();
        // Ordered multiset: restoring a pool must retain both duplicate copies and list order.
        public List<string> remainingDeckKeys = new List<string>();
    }
}
