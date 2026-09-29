using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Game.Cards
{
    // One unit/hero within an ArmyDefinition — cardKey identifies the card across ALL of
    // NeutralArmyCatalog.cardCatalogs as "<catalog.displayName>/<card.displayName>", same
    // format and same reason as StartingDeckCatalog.DeckCardEntry.cardKey (drawn via a
    // dropdown, ArmyUnitEntryDrawer in Assets/Editor, not hand-typed).
    [System.Serializable]
    public class ArmyUnitEntry
    {
        public string cardKey;
        public int count = 1;
    }

    // One named, pre-composed neutral army — e.g. a map-guard force or an Events guard army
    // (see EventVariant.guardArmyName), as opposed to ordinary map neutral rolls. `name` is
    // shown as this entry's own label in the catalog's `armies` list. Event-only guards
    // are excluded from ordinary map generation.
    [System.Serializable]
    public class ArmyDefinition
    {
        public string name;
        // Event guards are chosen by EventCatalog and must not enter ordinary map rolls.
        public bool eventOnly;
        public List<ArmyUnitEntry> members = new List<ArmyUnitEntry>();
    }

    // Every hand-authored neutral army in the game — separate from FactionCardCatalog's own
    // per-card design data, same reasoning as UnitAbilityCatalog/StartingDeckCatalog living on
    // their own: this is army-composition data, tuned by editing one asset. Referenced by name
    // (see [ArmyTag]) from EventDefinition.guardArmyName, and available for map generation to
    // place fixed armies on the map alongside GenerateNeutralArmies' random ones.
    [CreateAssetMenu(fileName = "NeutralArmyCatalog", menuName = "Game/Neutral Army Catalog")]
    public class NeutralArmyCatalog : ScriptableObject
    {
        public List<FactionCardCatalog> cardCatalogs = new List<FactionCardCatalog>();
        public List<ArmyDefinition> armies = new List<ArmyDefinition>();
        public IEnumerable<ArmyDefinition> MapArmies =>
            armies?.Where(a => a != null && !a.eventOnly) ?? Enumerable.Empty<ArmyDefinition>();

        // Scans `cardCatalogs` for the card named by cardKey ("<catalog.displayName>/<card.
        // displayName>") — null if the catalog or the card inside it can no longer be found (a
        // stale key left over from a rename/removal), same fallback StartingDeckCatalog.
        // ResolveCard uses.
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

        public ArmyDefinition GetArmy(string armyName) =>
            armies?.FirstOrDefault(a => a != null && a.name == armyName);
    }
}
