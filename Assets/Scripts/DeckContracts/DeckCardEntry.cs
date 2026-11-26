using System.Collections.Generic;

namespace Game.Cards
{
    // A deck row references CardDefinition.authoredKey when available. Legacy
    // "<catalog.displayName>/<card.displayName>" references remain readable while existing
    // cards acquire stable identities; new authored references survive display-name changes.
    [System.Serializable]
    public class DeckCardEntry
    {
        public string cardKey;
        public int count = 1;

        public static List<DeckCardEntry> CopyEntries(List<DeckCardEntry> entries)
            => entries?.ConvertAll(entry => entry == null ? null : new DeckCardEntry
            {
                cardKey = entry.cardKey,
                count = entry.count
            });
    }
}
