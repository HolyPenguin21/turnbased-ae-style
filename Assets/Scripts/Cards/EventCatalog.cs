using System.Collections.Generic;
using System.Linq;
using Game.Economy;
using UnityEngine;

namespace Game.Cards
{
    // What one RewardEntry actually grants — kept to the two kinds the project owner asked
    // for (resources or a card), not a third "grant an ability directly" kind: an ability is
    // always granted by giving the player a unit/hero card that already carries it, same as
    // every other card in the game.
    public enum RewardType
    {
        Resources,
        Card
    }

    // One reward an event can pay out — an EventDefinition's `rewards` list holds several of
    // these so an event can grant more than one thing at once (e.g. resources AND a card).
    // Only the field matching `type` is meaningful; drawn as a single row by RewardEntryDrawer
    // (Assets/Editor), which shows/hides `resources` vs `cardKey` based on the popup.
    [System.Serializable]
    public class RewardEntry
    {
        public RewardType type;
        public ResourceYields resources = new ResourceYields();
        // "<catalog.displayName>/<card.displayName>" — same cardKey format as
        // ArmyUnitEntry.cardKey/StartingDeckCatalog.DeckCardEntry.cardKey, resolved against
        // the owning EventCatalog's own `cardCatalogs` list (see EventCatalog.ResolveCard).
        public string cardKey;
    }

    // One random/special-hex event — `name` is shown as this entry's own label in the
    // catalog's `events` list (see EventDefinitionDrawer) instead of Unity's default
    // "Element N". `guardArmyName` names an army in whichever NeutralArmyCatalog asset exists
    // in the project (see [ArmyTag]/ArmyTagDrawer) — the force defending this event's reward,
    // resolved at the point something actually needs to spawn/fight it. Data only: this
    // catalog doesn't itself place events on the map or grant rewards on capture — see
    // CitadelSetupController.MapContent.GenerateRandomEvents, the placeholder pass this data
    // is meant to feed once that's wired up.
    [System.Serializable]
    public class EventDefinition
    {
        public string name;
        public Sprite image;
        [TextArea]
        public string description;
        [ArmyTag]
        public string guardArmyName;
        public List<RewardEntry> rewards = new List<RewardEntry>();
    }

    // Every hand-authored random/special-hex event in the game — a separate asset from
    // FactionCardCatalog/NeutralArmyCatalog, same reasoning as UnitAbilityCatalog living on
    // its own: this is event design data, tuned by editing one asset in the Cards folder.
    [CreateAssetMenu(fileName = "EventCatalog", menuName = "Game/Event Catalog")]
    public class EventCatalog : ScriptableObject
    {
        public List<FactionCardCatalog> cardCatalogs = new List<FactionCardCatalog>();
        public List<EventDefinition> events = new List<EventDefinition>();

        // Scans `cardCatalogs` for the card named by cardKey ("<catalog.displayName>/<card.
        // displayName>") — null if the catalog or the card inside it can no longer be found,
        // same fallback StartingDeckCatalog.ResolveCard/NeutralArmyCatalog.ResolveCard use.
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
    }
}
