using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Cards
{
    // Every card design available to one faction — one file per faction, the config the user
    // asked for: editing a faction's whole card set means opening this one asset, not jumping
    // between per-card files (CardDefinition is a plain embedded class, not its own asset —
    // see CardDefinition). Cards carry their own CardType, so this holds one flat list rather
    // than three parallel Hero/Unit/Facility lists that could drift out of sync with each
    // card's own declared type; ForType filters on demand.
    //
    // CardHandUI references this catalog plus index lists (startingHandIndices/
    // drawPoolIndices) rather than picking individual CardDefinitions, since those no longer
    // exist as separately-referenceable assets.
    [CreateAssetMenu(fileName = "FactionCardCatalog", menuName = "Game/Faction Card Catalog")]
    public class FactionCardCatalog : ScriptableObject
    {
        public Faction faction;
        // Shown in ArmyViewerModalUI's title bar (logo + name, alongside the owning player and
        // whichever army/garrison is currently open) — there's no other place a faction's
        // display identity lives yet.
        [Header("Identity")]
        public string displayName = "Iron Concord";
        public Sprite logo;

        public List<CardDefinition> cards = new List<CardDefinition>();

        // Keeps every CardDefinition.id in sync with its actual list position, so the inspector
        // always shows the correct index even after cards are added/removed/reordered.
        private void OnValidate()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] != null)
                    cards[i].id = i;
            }
        }

        // Random display name for a newly created army (see Game.Map.ArmyData) — drawn once
        // per creation, not reused/tracked, so duplicates across a game are possible and fine
        // (matches "random name" as asked for, not "unique name").
        [Header("Armies")]
        public List<string> armyNamePool = new List<string>();

        public IEnumerable<CardDefinition> ForType(CardType type) => cards.Where(c => c != null && c.cardType == type);

        // Used by ArmyViewerModalUI's Create Army button. takenNames is whichever names are
        // already in use (see ArmyRegistry.AllForOwner) — a fresh army must never collide with
        // one of those; ordering beyond that is still random. Falls back to a numbered suffix
        // ("Alpha Company 2") only once the whole pool is exhausted, so a name never actually
        // repeats.
        public string GetRandomArmyName(IEnumerable<string> takenNames = null)
        {
            var taken = takenNames != null ? new HashSet<string>(takenNames) : null;
            if (armyNamePool != null && armyNamePool.Count > 0)
            {
                List<string> available = taken != null
                    ? armyNamePool.Where(n => !taken.Contains(n)).ToList()
                    : armyNamePool;
                if (available.Count > 0)
                    return available[Random.Range(0, available.Count)];
            }

            string baseName = armyNamePool != null && armyNamePool.Count > 0
                ? armyNamePool[Random.Range(0, armyNamePool.Count)]
                : "Army";
            if (taken == null)
                return baseName;

            int suffix = 2;
            string candidate = $"{baseName} {suffix}";
            while (taken.Contains(candidate))
                candidate = $"{baseName} {++suffix}";
            return candidate;
        }
    }
}
