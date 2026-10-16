using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Progression;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Test helper: raises every card a playable faction may put in a deck to its copy limit in the
    // local profile (Application.persistentDataPath/collection-v1.json). The previous file stays
    // as collection-v1.json.bak (AtomicJsonFile). Decks are still capped at DeckRules.MaximumPoints.
    public static class FullCollectionGrant
    {
        private const string MenuPath = "Game/Collection/Grant Full Collection (Test)";

        [MenuItem(MenuPath)]
        public static void Grant()
        {
            var config = AssetDatabase.FindAssets("t:GameConfig")
                .Select(g => AssetDatabase.LoadAssetAtPath<GameConfig>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(c => c != null && c.collectionDeckCatalog != null && c.collectionResearchCatalog != null);
            if (config == null) { Debug.LogError("Full collection: no GameConfig with collection catalogs."); return; }

            var rules = new DeckRules(config.collectionDeckCatalog, config.collectionResearchCatalog);
            var store = new CollectionProfileStore(Application.persistentDataPath);
            var loaded = store.Load(out _);
            var service = new CollectionService(rules, store, loaded ?? new CollectionProfile());
            if (loaded == null && !service.Transact(service.InitializeStarters, out string initError))
            { Debug.LogError("Full collection: " + initError); return; }

            int raised = 0;
            bool saved = service.Transact(p =>
            {
                foreach (var card in DeckRules.PlayableFactions.SelectMany(rules.Cards).Distinct())
                {
                    int limit = DeckRules.CopyLimit(card);
                    if (limit <= 0) continue;
                    var owned = p.ownedCards.Find(e => e.cardKey == card.authoredKey);
                    if (owned == null) p.ownedCards.Add(owned = new DeckCardEntry { cardKey = card.authoredKey, count = 0 });
                    if (owned.count < limit) { owned.count = limit; raised++; }
                }
            }, out string error);
            if (!saved) { Debug.LogError("Full collection: " + error); return; }
            Debug.Log($"Full collection: {raised} card(s) raised to their copy limit in {Application.persistentDataPath}.");
        }

        // The running game holds its own profile copy (ProgressionContext) and would overwrite this edit.
        [MenuItem(MenuPath, true)]
        private static bool CanGrant() => !EditorApplication.isPlayingOrWillChangePlaymode;
    }
}
