using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Cards
{
    // One card offered by a Research or Production Facility — the same "cardKey resolved against
    // this asset's own cardCatalogs" idea as EventCatalog.RewardEntry, plus a per-entry faction
    // gate. `factionRestriction`:
    //   Faction.None  — every playable faction may pick this card
    //   Faction.IronConcord / Faction.Ashen — only that faction's own player may pick it
    // Drawn as a single inspector row by ResearchProductionEntryDrawer (Assets/Editor): a card
    // dropdown on the left (built from this asset's cardCatalogs, same as DeckCardEntryDrawer)
    // and a faction popup on the right.
    [System.Serializable]
    public class ResearchProductionEntry
    {
        // "<catalog.displayName>/<card.displayName>" — same cardKey format as
        // StartingDeckCatalog.DeckCardEntry.cardKey / EventCatalog.RewardEntry.cardKey, resolved
        // against the owning ResearchProductionCatalog's own `cardCatalogs` list (see ResolveCard).
        public string cardKey;
        public Faction factionRestriction = Faction.None;
    }

    // Every card that can be produced through a Research or Production Facility, split into two
    // independent lists. A separate asset from FactionCardCatalog / EventCatalog for the same
    // reason those are separate from each other: this is Research/Production availability data,
    // tuned by editing one asset in the Cards folder. It does NOT redefine CardDefinition or
    // FactionCardCatalog — it only declares which existing cards participate, and for whom.
    [CreateAssetMenu(fileName = "ResearchProductionCatalog", menuName = "Game/Research Production Catalog")]
    public class ResearchProductionCatalog : ScriptableObject
    {
        // The faction card catalogs every cardKey below is resolved against — point this at the
        // same FactionCardCatalog assets StartingDeckCatalog.catalogs / EventCatalog.cardCatalogs
        // already reference.
        public List<FactionCardCatalog> cardCatalogs = new List<FactionCardCatalog>();

        // Shown by ResearchProductionModalUI in Research mode (opened by a Hero carrying
        // UnitAbilities.Researcher on a hex whose building has a Research Facility).
        public List<ResearchProductionEntry> researchCards = new List<ResearchProductionEntry>();

        // Shown by ResearchProductionModalUI in Production mode (Hero with UnitAbilities.Assembler
        // + a Production Facility).
        public List<ResearchProductionEntry> productionCards = new List<ResearchProductionEntry>();

        // Scans `cardCatalogs` for the card named by cardKey ("<catalog.displayName>/<card.
        // displayName>") — null if the catalog or the card inside it can no longer be found,
        // same fallback EventCatalog.ResolveCard / StartingDeckCatalog.ResolveCard use.
        public CardDefinition ResolveCard(string cardKey)
        {
            if (string.IsNullOrEmpty(cardKey) || cardCatalogs == null)
                return null;

            foreach (FactionCardCatalog catalog in cardCatalogs)
            {
                if (catalog == null)
                    continue;
                string prefix = catalog.displayName + "/";
                if (!cardKey.StartsWith(prefix))
                    continue;
                string cardName = cardKey.Substring(prefix.Length);
                CardDefinition match = catalog.cards.FirstOrDefault(c => c != null && c.displayName == cardName);
                if (match != null)
                    return match;
            }
            return null;
        }

        // Every resolvable CardDefinition for `mode`, in list order, after applying each entry's
        // own faction gate against `viewerFaction`. A null/unresolvable cardKey is simply skipped.
        // This is the list ResearchProductionModalUI paginates over — the faction filter is
        // applied HERE, before pagination, exactly as the spec requires.
        public List<CardDefinition> ResolveFor(ResearchProductionMode mode, Faction viewerFaction)
        {
            var result = new List<CardDefinition>();
            List<ResearchProductionEntry> entries =
                mode == ResearchProductionMode.Research ? researchCards : productionCards;
            if (entries == null)
                return result;

            foreach (ResearchProductionEntry entry in entries)
            {
                if (entry == null)
                    continue;
                if (entry.factionRestriction != Faction.None && entry.factionRestriction != viewerFaction)
                    continue;
                CardDefinition card = ResolveCard(entry.cardKey);
                if (card != null)
                    result.Add(card);
            }
            return result;
        }
    }

    // Which of the two symmetric flows a ResearchProductionModalUI is currently showing. Lives
    // here (next to the catalog it selects a list from) rather than on the UI type so non-UI
    // callers — HexSelectionController's Research/Production hex actions — can name it without a
    // Game.UI dependency.
    public enum ResearchProductionMode
    {
        Research,
        Production
    }
}
