#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.EditorTools;
using Game.Players;
using Game.Saves;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AuthoredContentValidationTests
    {
        private readonly Dictionary<Object, string> assets = new Dictionary<Object, string>();

        private T Asset<T>(string path) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            assets.Add(asset, path);
            return asset;
        }

        [TearDown]
        public void Clean()
        {
            foreach (var asset in assets.Keys) Object.DestroyImmediate(asset);
            assets.Clear();
        }

        [Test, Category("UnityNative")]
        public void BuildGraphValidatesCurrentAuthoredData()
        {
            Assert.DoesNotThrow(CollectionContentValidation.Validate);
        }

        [Test]
        public void PureValidationReportsOriginAndFindsMissingStarterHostWithoutOpeningProfile()
        {
            var faction = Asset<FactionCardCatalog>("Cards/faction.asset");
            faction.faction = Faction.IronConcord;
            faction.cards.Add(new CardDefinition { authoredKey = "hero", cardType = CardType.Hero });
            faction.cards.Add(new CardDefinition { authoredKey = "blueprint", cardType = CardType.Equipment });
            faction.cards.Add(new CardDefinition { authoredKey = "hero", cardType = CardType.Unit, apCost = -1,
                grantedAbilities = { "r999999999999999s1" } });
            var decks = Asset<StartingDeckCatalog>("Cards/decks.asset");
            decks.catalogs.Add(faction);
            decks.decks.Add(new StartingDeck { deckName = "Starter", faction = faction.faction,
                cards = { new DeckCardEntry { cardKey = "blueprint" }, new DeckCardEntry { cardKey = "missing" } } });
            var research = Asset<ResearchProductionCatalog>("Cards/research.asset");
            research.cardCatalogs.Add(faction);
            research.productionCards.Add(new ResearchProductionEntry { cardKey = "blueprint" });
            var config = Asset<GameConfig>("Config/match.asset");
            config.collectionDeckCatalog = decks;
            config.collectionResearchCatalog = research;
            config.extractionFacilityCards = new CardDefinition[0];

            var issues = AuthoredContentValidator.Validate(new AuthoredContentGraph(assets));
            Assert.That(issues.Any(issue => issue.Asset == faction && issue.Key == "hero"
                && issue.Path.StartsWith("Cards/faction.asset/cards[2]") && issue.Cause.Contains("duplicate")), Is.True);
            Assert.That(issues.Any(issue => issue.Asset == faction && issue.Cause.Contains("Negative card price")), Is.True);
            Assert.That(issues.Any(issue => issue.Asset == faction && issue.Cause.Contains("invalid ability parameter")), Is.True);
            Assert.That(issues.Any(issue => issue.Asset == decks && issue.Key == "missing"), Is.True);
            Assert.That(issues.Any(issue => issue.Asset == decks && issue.Key == "blueprint"
                && issue.Cause.Contains("compatible starter host")), Is.True);
            Assert.That(decks.decks[0].cards.Select(row => row.cardKey), Is.EqualTo(new[] { "blueprint", "missing" }));
        }

        [Test]
        public void IncompleteCardCatalogAndStarterAreReportedTogether()
        {
            var faction = Asset<FactionCardCatalog>("Cards/faction.asset");
            faction.faction = Faction.Ashen;
            var unfinished = Asset<FactionCardCatalog>("Cards/unfinished.asset");
            unfinished.cards = null;
            var decks = Asset<StartingDeckCatalog>("Cards/decks.asset");
            decks.catalogs.AddRange(new[] { faction, unfinished });
            decks.decks.Add(new StartingDeck { deckName = "Unfinished", cards = null, faction = Faction.IronConcord });
            var loan = new CardDefinition { authoredKey = "loan", cardType = CardType.Unit };
            faction.cards.Add(loan);
            decks.aiDecks.Add(new StartingDeck { deckName = "AI", faction = Faction.IronConcord,
                cards = { new DeckCardEntry { cardKey = loan.authoredKey } } });
            var research = Asset<ResearchProductionCatalog>("Cards/research.asset");
            var config = Asset<GameConfig>("Config/match.asset");
            config.collectionDeckCatalog = decks;
            config.collectionResearchCatalog = research;
            config.extractionFacilityCards = new CardDefinition[0];

            var rules = new DeckRules(decks, research);
            Assert.That(rules.Cards(Faction.Ashen).ToArray(), Does.Contain(loan));
            var issues = AuthoredContentValidator.Validate(new AuthoredContentGraph(assets));
            Assert.That(issues.Any(issue => issue.Asset == unfinished && issue.Path == "Cards/unfinished.asset/cards"), Is.True);
            Assert.That(issues.Any(issue => issue.Asset == decks && issue.Path == "Cards/decks.asset/decks/Unfinished/cards"), Is.True);
            Assert.That(issues.Any(issue => issue.Asset == decks && issue.Key == loan.authoredKey), Is.True);
        }

        [Test, Category("UnityNative")]
        public void RewardValidationChecksTheFirstGameObjectUsedByRuntime()
        {
            var catalog = Asset<GameAssetCatalog>("GameAssetCatalog.asset");
            var wrong = new GameObject("wrong prefab");
            var correct = new GameObject("reward prefab");
            try
            {
                correct.AddComponent<Game.UI.CollectionRewardView>();
                var key = Game.UI.CollectionRewardUI.RewardPanelKey;
                catalog.ReplaceEntries(new[] { new GameAssetCatalog.Entry { key = key, assets = new Object[] { wrong, correct } } });
                var issues = AuthoredContentValidator.Validate(new AuthoredContentGraph(assets));
                Assert.That(issues.Any(issue => issue.Key == key), Is.True);

                catalog.ReplaceEntries(new[] { new GameAssetCatalog.Entry { key = key, assets = new Object[] { correct, wrong } } });
                issues = AuthoredContentValidator.Validate(new AuthoredContentGraph(assets));
                Assert.That(issues.Any(issue => issue.Key == key), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(wrong);
                Object.DestroyImmediate(correct);
            }
        }

        [Test]
        public void SameDefinitionResolvesThroughLegacyAuthoringAndStrictSaveIdentity()
        {
            var faction = Asset<FactionCardCatalog>("Cards/neutral.asset");
            faction.displayName = "Neutral";
            var card = new CardDefinition { authoredKey = "neutral.reward", displayName = "Old reward", cardType = CardType.Unit };
            faction.cards.Add(card);
            var events = Asset<EventCatalog>("Cards/events.asset");
            events.cardCatalogs.Add(faction);
            var decks = Asset<StartingDeckCatalog>("Cards/decks.asset");
            decks.catalogs.Add(faction);
            var config = Asset<GameConfig>("Config/match.asset");
            config.collectionDeckCatalog = decks;
            var rules = new DeckRules(decks, null);
            var saves = new MatchContentResolver(config);

            Assert.That(events.ResolveCard("Neutral/Old reward"), Is.SameAs(card));
            Assert.That(decks.ResolveCard("Neutral/Old reward"), Is.SameAs(card));
            Assert.That(rules.Resolve("Neutral/Old reward"), Is.Null);
            Assert.That(rules.Resolve(card.authoredKey), Is.SameAs(card));
            Assert.That(saves.Card(card.authoredKey), Is.SameAs(card));
            Assert.Throws<System.InvalidOperationException>(() => saves.Card("Neutral/Old reward"));
        }
    }
}
#endif
