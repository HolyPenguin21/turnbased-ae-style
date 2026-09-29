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

    // One reward an event can pay out. EventVariant.rewards holds optional card rewards;
    // its random resource payout is resolved once when the event is placed.
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

    // One complete outcome tier. The guard and its reward are selected together at map
    // generation, then kept on the hex for the rest of the game.
    [System.Serializable]
    public class EventVariant
    {
        [ArmyTag] public string guardArmyName;
        [Min(0)] public int resourceCount;
        // Optional card reward, selected with this guard tier.
        public List<RewardEntry> rewards = new List<RewardEntry>();
    }

    // One random/special-hex event — `name` is shown as this entry's own label in the
    // catalog's `events` list (see EventDefinitionDrawer) instead of Unity's default
    // "Element N". Three variants carry the light, medium and heavy guard/reward pairs;
    // CitadelSetupController.MapContent selects one during map generation.
    [System.Serializable]
    public class EventDefinition
    {
        public string name;
        public Sprite image;
        [TextArea]
        public string description;
        // Ordered light / medium / heavy. The map selects one uniformly at placement.
        public List<EventVariant> variants = new List<EventVariant>();
        // Legacy fields remain for assets/tests not yet converted to variants.
        [HideInInspector]
        [ArmyTag]
        public string guardArmyName;
        [HideInInspector]
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
