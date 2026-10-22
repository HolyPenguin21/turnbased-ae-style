#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Game.Campaign;
using Game.Cards;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiHeroDeckRulesTests
    {
        // Only plain fields and catalogue methods are exercised; no native Object APIs are used.
        private static T Shell<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
        private static StartingDeckCatalog Catalog(out CardDefinition hero, out CardDefinition body)
        {
            hero = new CardDefinition { authoredKey = "hero", displayName = "Leader", cardType = CardType.Hero,
                faction = Faction.IronConcord, deckPointCost = 1 };
            body = new CardDefinition { authoredKey = "body", cardType = CardType.Unit, deckPointCost = 3 };
            var faction = Shell<FactionCardCatalog>(); faction.cards = new List<CardDefinition> { hero, body };
            var catalog = Shell<StartingDeckCatalog>(); catalog.catalogs = new List<FactionCardCatalog> { faction };
            catalog.decks = new List<StartingDeck>();
            catalog.aiDecks = new List<StartingDeck> { new StartingDeck { deckName = "Leader", leaderCardKey = "hero",
                faction = Faction.IronConcord, cards = new List<DeckCardEntry> { Row("hero"), Row("body", 3) } } };
            return catalog;
        }
        private static DeckCardEntry Row(string key, int count = 1) => new DeckCardEntry { cardKey = key, count = count };

        [Test]
        public void AiBudgetCountsOnlyMainCardsRegardlessOfBlueprintPointPrices()
        {
            var catalog = Catalog(out _, out _); var deck = catalog.aiDecks.Single();
            var expensive = new CardDefinition { authoredKey = "blueprint", cardType = CardType.Equipment, deckPointCost = 1000 };
            catalog.catalogs.Single().cards.Add(expensive);
            deck.equipment.Add(Row("blueprint")); deck.mutators.Add(Row("blueprint"));
            Assert.That(catalog.AiDeckPoints(deck), Is.EqualTo(10));
            Assert.That(catalog.BuildAiDeckPool(Faction.IronConcord, deck.deckName).Count, Is.EqualTo(4));
            Assert.That(catalog.BuildAiBlueprintKeys(Faction.IronConcord, deck.deckName), Is.EquivalentTo(new[] { "blueprint" }));
        }

        [Test]
        public void GrantConsumesOnlyTheLeaderCopyAndDoesNotMutateTheAuthoredDeck()
        {
            var catalog = Catalog(out var hero, out var body);
            var pool = catalog.BuildAiDeckPool(Faction.IronConcord, "Leader", "hero");
            Assert.That(pool.Count, Is.EqualTo(3)); Assert.That(pool.All(c => c == body), Is.True);
            Assert.That(catalog.BuildAiDeckPool(Faction.IronConcord, "Leader").Count(c => c == hero), Is.EqualTo(1));
            var player = new PlayerSetupData { Faction = Faction.IronConcord, AiDeckName = "Leader", AiGrantedLeaderKey = "hero" };
            Assert.That(catalog.BuildDeckPool(player), Is.EquivalentTo(pool));
        }

        [Test]
        public void LeaderMustBeAnOwnHeroPresentExactlyOnceAndNamesNeverSubstituteForKeys()
        {
            var catalog = Catalog(out var hero, out _); var deck = catalog.aiDecks.Single();
            hero.displayName = "Renamed";
            Assert.That(catalog.ResolveAiLeader(Faction.IronConcord, "Leader"), Is.SameAs(hero));
            deck.leaderCardKey = "body"; Assert.That(catalog.ResolveAiLeader(Faction.IronConcord, "Leader"), Is.Null);
            deck.leaderCardKey = "hero"; hero.faction = Faction.Ashen;
            Assert.That(catalog.ResolveAiLeader(Faction.IronConcord, "Leader"), Is.Null);
            hero.faction = Faction.IronConcord; deck.cards[0].count = 2;
            Assert.That(catalog.ResolveAiLeader(Faction.IronConcord, "Leader"), Is.Null);
            Assert.That(catalog.ResolveAiLeader(Faction.IronConcord, "missing"), Is.Null);
        }

        [Test]
        public void CampaignBattleChoiceIsStableAcrossReadsCatalogueOrderAndSavedName()
        {
            var catalog = Catalog(out _, out _);
            for (int i = 0; i < 7; i++) catalog.aiDecks.Add(new StartingDeck { deckName = "Leader " + i,
                leaderCardKey = "hero-" + i, faction = Faction.IronConcord });
            var rules = new DeckRules(catalog, null); var seen = new HashSet<string>();
            for (int seed = 0; seed < 100; seed++)
            {
                var first = CampaignDeck.AiSource(Faction.IronConcord, rules, seed);
                catalog.aiDecks.Reverse();
                Assert.That(CampaignDeck.AiSource(Faction.IronConcord, rules, seed), Is.SameAs(first));
                Assert.That(CampaignDeck.AiSource(Faction.IronConcord, rules, seed + 1, first.deckName), Is.SameAs(first));
                seen.Add(first.deckName);
            }
            Assert.That(seen.Count, Is.EqualTo(8));
            Assert.That(CampaignDeck.AiSource(Faction.Ashen, rules, 5), Is.Null);
        }

        [Test]
        public void StaleDeckReadUsesOneStableFallbackRatherThanRerolling()
        {
            var catalog = Catalog(out _, out _); var first = catalog.aiDecks[0];
            catalog.aiDecks.Add(new StartingDeck { deckName = "Second", faction = Faction.IronConcord });
            for (int i = 0; i < 100; i++) Assert.That(catalog.GetAiDeck(Faction.IronConcord, "old-name"), Is.SameAs(first));
        }

        [Test]
        public void CampaignAutobattleUsesTheSelectedMainPoolAndAllowedBlueprints()
        {
            var catalog = Catalog(out _, out _); var source = catalog.aiDecks[0];
            var allowed = new CardDefinition { authoredKey = "allowed", cardType = CardType.Equipment };
            var forbidden = new CardDefinition { authoredKey = "forbidden", cardType = CardType.Equipment };
            catalog.catalogs.Single().cards.AddRange(new[] { allowed, forbidden });
            var research = Shell<ResearchProductionCatalog>(); research.cardCatalogs = catalog.catalogs;
            research.productionCards = new List<ResearchProductionEntry> {
                new ResearchProductionEntry { cardKey = "allowed" }, new ResearchProductionEntry { cardKey = "forbidden" } };
            research.researchCards = new List<ResearchProductionEntry>(); source.equipment.Add(Row("allowed"));
            var actual = CampaignDeck.Ai(Faction.IronConcord, new DeckRules(catalog, research), 5);
            Assert.That(actual.Name, Is.EqualTo(source.deckName));
            Assert.That(actual.Main, Is.EquivalentTo(catalog.BuildAiDeckPool(Faction.IronConcord, source.deckName)));
            Assert.That(actual.Attachments, Is.EquivalentTo(new[] { allowed }));
        }
    }
}
#endif
