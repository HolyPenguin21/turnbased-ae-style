#if UNITY_INCLUDE_TESTS
using Game.Cards;
using Game.Players;
using Game.Progression;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class MatchLoadoutStateTests
    {
        [Test]
        public void RestoringHumanAndAiHandsDoesNotDealOrConsumeRandomness()
        {
            var savedRandom = Random.state;
            try
            {
                var definition = new CardDefinition { authoredKey = "saved.card", cardType = CardType.Unit };
                var model = new PlayerHandData(new[] { definition, definition }, 0);
                model.AddCard(new CardData(definition));
                var state = model.CaptureState();
                Random.InitState(1234);
                var expected = Random.value;
                Random.InitState(1234);
                var human = PlayerHandData.FromState(state, key => definition);
                var ai = Game.Ai.AiHandData.FromState(state, key => definition);
                Assert.That(Random.value, Is.EqualTo(expected));
                Assert.That(human.Hand.Count, Is.EqualTo(1));
                Assert.That(ai.Hand.Count, Is.EqualTo(1));
                Assert.That(human.RemainingDeckCount, Is.EqualTo(2));
                Assert.That(ai.RemainingDeckCount, Is.EqualTo(2));
            }
            finally { Random.state = savedRandom; }
        }

        [Test]
        public void RestoredLoadoutUsesItsOwnStartingCardsAndBlueprintsAfterProfileDeckChanges()
        {
            var starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            var research = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            var catalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
            try
            {
                catalog.faction = Faction.IronConcord;
                var unit = new CardDefinition { authoredKey = "saved.unit", cardType = CardType.Unit, deckPointCost = 25 };
                var gear = new CardDefinition { authoredKey = "saved.gear", cardType = CardType.Equipment,
                    attachmentSlot = AttachmentSlot.Equipment };
                catalog.cards.Add(unit); catalog.cards.Add(gear);
                starting.catalogs.Add(catalog); research.cardCatalogs.Add(catalog);
                research.productionCards.Add(new ResearchProductionEntry { cardKey = gear.authoredKey });
                var rules = new DeckRules(starting, research);
                var profileDeck = new SavedDeck { deckId = "selected", faction = Faction.IronConcord };
                profileDeck.mainCards.Add(new DeckCardEntry { cardKey = unit.authoredKey, count = 2 });
                profileDeck.equipment.Add(new DeckCardEntry { cardKey = gear.authoredKey, count = 1 });
                var loadout = new MatchLoadout(profileDeck, rules, key => 4);
                var state = loadout.CaptureState();
                profileDeck.mainCards.Clear(); profileDeck.equipment.Clear();
                var restored = MatchLoadout.FromState(state, rules);
                state.mainCards[0].count = 4; state.equipment.Clear();
                Assert.That(restored.TryBuildPool(rules, out var pool, out var error), Is.True, error);
                Assert.That(pool, Is.EqualTo(new[] { unit, unit }));
                Assert.That(new BlueprintQuota(restored).Unlocked(gear.authoredKey), Is.True);
                Assert.That(loadout.CaptureState().mainCards[0].count, Is.EqualTo(2));
                Assert.That(restored.CaptureState().deckId, Is.EqualTo("selected"));
            }
            finally
            {
                Object.DestroyImmediate(starting); Object.DestroyImmediate(research); Object.DestroyImmediate(catalog);
            }
        }
    }
}
#endif
