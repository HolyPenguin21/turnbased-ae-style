#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Players;
using Game.Progression;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class StartingHandTests
    {
        private Random.State randomState;

        [SetUp] public void SetUp() => randomState = Random.state;
        [TearDown] public void TearDown() => Random.state = randomState;

        private static CardDefinition Unit(string key, int ap = 1, int resources = 0, bool scout = false)
            => new CardDefinition
            {
                authoredKey = key, cardType = CardType.Unit, apCost = ap, moveMax = 3,
                resourceCost = new ResourceCost(materials: resources),
                grantedAbilities = scout ? new List<string> { UnitAbilities.R1S4 } : new List<string>()
            };

        [Test]
        public void CheapestScoutIsGuaranteedAndEverySlotCanReceiveIt()
        {
            var scout = Unit("cheap-scout", 1, 1, true);
            var expensiveScout = Unit("expensive-scout", 3, 3, true);
            var slots = new HashSet<int>();
            for (int seed = 0; seed < 256; seed++)
            {
                Random.InitState(seed);
                var pool = Enumerable.Range(0, 20).Select(i => Unit("unit-" + i)).ToList();
                pool.Add(expensiveScout);
                pool.Add(scout);
                var original = pool.ToList();
                var hand = DeckDraw.DrawStartingHand(pool, 7);

                Assert.That(hand, Has.Count.EqualTo(7));
                Assert.That(hand.Count(c => c == scout), Is.EqualTo(1));
                Assert.That(pool, Has.Count.EqualTo(15));
                CollectionAssert.AreEquivalent(original, hand.Concat(pool), "No created, lost or duplicated cards.");
                slots.Add(hand.IndexOf(scout));
            }
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 7), slots);
        }

        [Test]
        public void ScoutSelectionDoesNotIssueCardRuntimeIdentities()
        {
            var scout = Unit("scout", scout: true);
            int before = new CardData(scout).RuntimeId;
            DeckDraw.DrawStartingHand(new List<CardDefinition> { scout, scout }, 1);
            Assert.That(new CardData(scout).RuntimeId, Is.EqualTo(before + 1));
        }

        [Test]
        public void RapidReactionUsesTheActualDeployApRule()
        {
            var rapid = Unit("rapid", 4, 1, true);
            rapid.grantedAbilities.Add(UnitAbilities.RapidReaction);
            var ordinary = Unit("ordinary", 1, 1, true);
            var pool = new List<CardDefinition> { ordinary, rapid };
            Assert.That(DeckDraw.DrawStartingHand(pool, 1).Single(), Is.SameAs(rapid));
            Assert.That(pool.Single(), Is.SameAs(ordinary));
        }

        [Test]
        public void EqualApPrefersLowerResourceCost()
        {
            var cheap = Unit("cheap", 1, 1, true);
            var costly = Unit("costly", 1, 3, true);
            Assert.That(DeckDraw.DrawStartingHand(new List<CardDefinition> { costly, cheap }, 1).Single(), Is.SameAs(cheap));
        }

        [Test]
        public void EqualCostScoutsAreBothEligible()
        {
            var first = Unit("first", 1, 1, true);
            var second = Unit("second", 1, 1, true);
            var selected = new HashSet<CardDefinition>();
            for (int seed = 0; seed < 64; seed++)
            {
                Random.InitState(seed);
                selected.Add(DeckDraw.DrawStartingHand(new List<CardDefinition> { first, second }, 1).Single());
            }
            CollectionAssert.AreEquivalent(new[] { first, second }, selected);
        }

        [Test]
        public void HeroesAircraftBuildingsAndImmobileUnitsCannotReplaceTheOpeningGroundScout()
        {
            var hero = Unit("hero", 0, 0, true); hero.cardType = CardType.Hero;
            var plane = Unit("plane", 0, 0, true); plane.isAviation = true;
            var building = Unit("base", 0, 0, true); building.cardType = CardType.Base;
            var immobile = Unit("immobile", 0, 0, true); immobile.moveMax = 0;
            var scout = Unit("scout", 1, 1, true);
            var pool = new List<CardDefinition> { hero, plane, building, immobile, scout };
            Assert.That(DeckDraw.DrawStartingHand(pool, 1).Single(), Is.SameAs(scout));
            CollectionAssert.AreEquivalent(new[] { hero, plane, building, immobile }, pool);
        }

        [Test]
        public void VisionOnlyGroundScoutIsEligible()
        {
            var scout = Unit("vision-only", 1, 1);
            scout.grantedAbilities.Add(UnitAbilities.R1S0);
            Assert.That(DeckDraw.DrawStartingHand(new List<CardDefinition> { Unit("plain"), scout }, 1).Single(), Is.SameAs(scout));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void NonPositiveHandSizeDoesNotConsumeAnyCards(int count)
        {
            var pool = new List<CardDefinition> { Unit("scout", scout: true) };
            Assert.That(DeckDraw.DrawStartingHand(pool, count), Is.Empty);
            Assert.That(pool, Has.Count.EqualTo(1));
        }

        [Test]
        public void EmptyAndScoutFreeDecksRemainSafeAndDoNotInventCards()
        {
            Assert.That(DeckDraw.DrawStartingHand(null, 7), Is.Empty);
            Assert.That(DeckDraw.DrawStartingHand(new List<CardDefinition>(), 7), Is.Empty);
            var plain = Unit("plain");
            var pool = new List<CardDefinition> { plain };
            Assert.That(DeckDraw.DrawStartingHand(pool, 7), Is.EqualTo(new[] { plain }));
            Assert.That(pool, Is.Empty);
        }

        [Test]
        public void ShortDeckPreservesDuplicateCopies()
        {
            var scout = Unit("scout", scout: true);
            var pool = new List<CardDefinition> { scout, scout, Unit("plain") };
            var hand = DeckDraw.DrawStartingHand(pool, 7);
            Assert.That(hand, Has.Count.EqualTo(3));
            Assert.That(hand.Count(c => c == scout), Is.EqualTo(2));
            Assert.That(pool, Is.Empty);
        }

        [TestCase(Faction.IronConcord, "concord.card.scout", 29)]
        [TestCase(Faction.Ashen, "ashen.card.hooded", 29)]
        [TestCase(Faction.Vessels, "vessels.10.whisper-scout", 29)]
        public void AuthoredDefaultAndHeadlessOpeningRespectPoolCapacityAndBlueprints(Faction faction, string scoutKey, int poolSize)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset");
            Assert.That(catalog, Is.Not.Null);
            var deck = catalog.GetAiDecks(faction).Single();
            var original = catalog.BuildAiDeckPool(faction, deck.deckName);
            Assert.That(original.Count, Is.EqualTo(poolSize));
            Assert.That(original.Count(c => c.isAviation), Is.EqualTo(2));
            Assert.That(original.Count(c => c.cardType == CardType.Unit), Is.EqualTo(18));
            CollectionAssert.AreEquivalent(catalog.GetDeck(faction).cards.Where(e => catalog.ResolveCard(e.cardKey).cardType != CardType.Equipment)
                .Select(e => e.cardKey + ":" + e.count), deck.cards.Select(e => e.cardKey + ":" + e.count));

            var entries = deck.cards.Concat(deck.equipment).Concat(deck.mutators).ToList();
            Assert.That(entries.All(e => catalog.ResolveCard(e.cardKey) != null), Is.True);
            Assert.That(entries.All(e => e.count > 0 && e.count <= DeckRules.CopyLimit(catalog.ResolveCard(e.cardKey))), Is.True);
            Assert.That(deck.cards.Select(e => e.cardKey).Distinct().Count(), Is.EqualTo(deck.cards.Count));
            Assert.That(deck.cards.Single(e => e.cardKey == scoutKey).count, Is.EqualTo(2));
            var heroes = catalog.GetCatalog(faction).cards.Where(c => c.cardType == CardType.Hero).ToList();
            CollectionAssert.AreEquivalent(heroes.Select(c => c.authoredKey),
                deck.cards.Where(e => catalog.ResolveCard(e.cardKey).cardType == CardType.Hero).Select(e => e.cardKey));
            Assert.That(heroes.All(c => deck.cards.Single(e => e.cardKey == c.authoredKey).count == 1), Is.True);
            Assert.That(deck.cards.Single(e => e.cardKey == "neutral.facility.lab").count, Is.EqualTo(1));
            Assert.That(deck.cards.Single(e => e.cardKey == "neutral.facility.factory").count, Is.EqualTo(1));
            var research = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset");
            var rules = new DeckRules(catalog, research);
            // AI presets intentionally exceed the human deck budget. Check complete faction
            // availability, not SavedDeck's 100-point validation; blueprints never enter the hand.
            CollectionAssert.AreEquivalent(research.ResolveFor(ResearchProductionMode.Production, faction)
                .Select(c => c.authoredKey), deck.equipment.Select(e => e.cardKey));
            CollectionAssert.AreEquivalent(research.ResolveFor(ResearchProductionMode.Research, faction)
                .Select(c => c.authoredKey), deck.mutators.Select(e => e.cardKey));
            Assert.That(entries.All(e => rules.Permitted(catalog.ResolveCard(e.cardKey), faction)), Is.True);
            Assert.That(deck.equipment.Concat(deck.mutators).All(e => e.count == 1), Is.True);

            foreach (int capacity in new[] { 0, 1, 3, 10 })
            {
                var hand = new AiHandData(catalog, faction, 7, capacity, deck.deckName);
                Assert.That(hand.Hand.Count, Is.EqualTo(System.Math.Min(7, capacity)));
                CollectionAssert.AreEquivalent(original, hand.Hand.Select(c => c.Definition).Concat(hand.RemainingDeck));
                Assert.That(hand.MutationVersion, Is.EqualTo(hand.Hand.Count));
                Assert.That(hand.Hand.All(c => c.AcquiredTurn == 0), Is.True);
                if (capacity > 0)
                    Assert.That(hand.Hand.Any(c => c.Definition.authoredKey == scoutKey), Is.True);
            }

            // Names stored by an older match also resolve to the single remaining faction deck.
            var oldPool = catalog.BuildAiDeckPool(faction, "Iron Concord Carrier AI Deck");
            CollectionAssert.AreEquivalent(original, oldPool);
            var blueprints = catalog.BuildAiBlueprintKeys(faction, deck.deckName);
            CollectionAssert.AreEquivalent(deck.equipment.Concat(deck.mutators).Select(e => e.cardKey), blueprints);
        }

        [TestCase(Faction.IronConcord)]
        [TestCase(Faction.Ashen)]
        [TestCase(Faction.Vessels)]
        public void SelectedHumanLoadoutAndEveryAiFactionUseTheOpeningScoutRule(Faction faction)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset");
            var research = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset");
            var rules = new DeckRules(catalog, research);
            var units = catalog.GetCatalog(faction).cards.Where(c => c.cardType == CardType.Unit
                && !c.isAviation && !c.deckBuilderExcluded && c.moveMax > 0).ToList();
            var scout = units.First(c => AbilityParams.GetBestRecceRadius(c.grantedAbilities) > 0);
            var saved = new SavedDeck { deckId = "opening-scout", name = "Opening scout", faction = faction };
            foreach (var unit in units.Where(c => AbilityParams.GetBestRecceRadius(c.grantedAbilities) == 0)
                .OrderBy(c => c.deckPointCost).Take(4))
                saved.mainCards.Add(new DeckCardEntry { cardKey = unit.authoredKey, count = DeckRules.CopyLimit(unit) });
            saved.mainCards.Add(new DeckCardEntry { cardKey = scout.authoredKey, count = 1 });
            var hero = catalog.GetCatalog(faction).cards.First(c => c.cardType == CardType.Hero);
            saved.mainCards.Add(new DeckCardEntry { cardKey = hero.authoredKey, count = 1 });

            // Regular games and manual campaign matches both provide this same selected
            // MatchLoadout to CardHandUI. The rule has no game-mode or faction condition.
            var loadout = new MatchLoadout(saved, rules, key => DeckRules.CopyLimit(rules.Resolve(key)));
            for (int seed = 0; seed < 32; seed++)
            {
                Random.InitState(seed);
                Assert.That(loadout.TryBuildPool(rules, out var pool, out var error), Is.True, error);
                var original = pool.ToList();
                var hand = DeckDraw.DrawStartingHand(pool, 7);
                Assert.That(hand, Has.Count.EqualTo(7));
                Assert.That(hand, Does.Contain(scout));
                CollectionAssert.AreEquivalent(original, hand.Concat(pool));

                // AI-vs-human, AI-only observation and campaign enemies all enter here,
                // including Vessels whose authored AI deck was not replaced by this change.
                var ai = new AiHandData(catalog, faction, 7);
                Assert.That(ai.Hand, Has.Count.EqualTo(7));
                Assert.That(ai.Hand.Any(c => c.Definition.cardType == CardType.Unit && !c.Definition.isAviation
                    && c.Definition.moveMax > 0
                    && AbilityParams.GetBestRecceRadius(c.Definition.grantedAbilities) > 0), Is.True);
            }
        }
    }
}
#endif
