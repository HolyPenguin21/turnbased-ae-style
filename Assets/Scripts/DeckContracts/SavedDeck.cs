using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Players;

namespace Game.Progression
{
    [Serializable] public sealed class SavedDeck
    {
        public string deckId;
        public bool isStarter;
        public string name;
        public Faction faction;
        public List<DeckCardEntry> mainCards = new List<DeckCardEntry>();
        public List<DeckCardEntry> equipment = new List<DeckCardEntry>();
        public List<DeckCardEntry> mutators = new List<DeckCardEntry>();

        public SavedDeck Copy() => new SavedDeck
        {
            deckId = deckId,
            isStarter = isStarter,
            name = name,
            faction = faction,
            mainCards = DeckCardEntry.CopyEntries(mainCards),
            equipment = DeckCardEntry.CopyEntries(equipment),
            mutators = DeckCardEntry.CopyEntries(mutators)
        };
    }
}
