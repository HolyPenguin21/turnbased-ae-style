using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Progression;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Game.EditorTools
{
    // Fails a player build for malformed content, before persistence can depend on identities.
    public sealed class CollectionContentValidation : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) { Validate(); }
        [MenuItem("Game/Collection/Validate Content")]
        public static void Validate()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:FactionCardCatalog"))
            {
                var catalog = AssetDatabase.LoadAssetAtPath<FactionCardCatalog>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var card in catalog.cards)
                {
                    if (card == null || string.IsNullOrWhiteSpace(card.authoredKey) || !keys.Add(card.authoredKey))
                        throw new BuildFailedException("Missing or duplicate authored card identity in " + catalog.name);
                    if (card.deckPointCost < 0 || card.deckCopyLimit < 0) throw new BuildFailedException("Invalid deck metadata: " + card.authoredKey);
                }
            }
            foreach (var guid in AssetDatabase.FindAssets("t:EventCatalog"))
            {
                var catalog = AssetDatabase.LoadAssetAtPath<EventCatalog>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var definition in catalog.events)
                    foreach (var reward in definition.rewards.Concat(definition.variants.SelectMany(v => v.rewards)))
                        if (reward.type == RewardType.Card && catalog.ResolveCard(reward.cardKey) == null)
                            throw new BuildFailedException("Unresolved event card: " + reward.cardKey);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:NeutralArmyCatalog"))
            {
                var catalog = AssetDatabase.LoadAssetAtPath<NeutralArmyCatalog>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var row in catalog.armies.SelectMany(a => a.members))
                    if (catalog.ResolveCard(row.cardKey) == null)
                        throw new BuildFailedException("Unresolved guard card: " + row.cardKey);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:GameConfig"))
            {
                var config = AssetDatabase.LoadAssetAtPath<GameConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (config.collectionDeckCatalog == null || config.collectionResearchCatalog == null)
                    throw new BuildFailedException("Missing collection catalogs in " + config.name);
                var rules = new DeckRules(config.collectionDeckCatalog, config.collectionResearchCatalog);
                foreach (var deck in rules.Starting.decks)
                    foreach (var row in deck.cards)
                        if (rules.Resolve(row.cardKey) == null) throw new BuildFailedException("Unresolved starter reference: " + row.cardKey);
                foreach (var row in rules.Research.researchCards.Concat(rules.Research.productionCards))
                    if (rules.Resolve(row.cardKey) == null) throw new BuildFailedException("Unresolved blueprint: " + row.cardKey);
                var profile = new CollectionProfile();
                var service = new CollectionService(rules, null, profile);
                service.InitializeStarters(profile);
                foreach (var deck in profile.savedDecks)
                {
                    foreach (var row in deck.equipment.Concat(deck.mutators))
                        if (!deck.mainCards.Select(e => rules.Resolve(e.cardKey)).Any(host => EquipmentSystem.FitsHost(rules.Resolve(row.cardKey), host, out _)))
                            throw new BuildFailedException("Initial blueprint has no compatible starter host: " + row.cardKey);
                }
            }
        }
    }
}
