#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Cards;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiHandCapacityParityTests
    {
        [Test]
        public void OrdinaryRewardOrReturnedCardCannotOverflowFullHand()
        {
            var hand = new AiHandData(null, Faction.None, 0, capacity: 1);
            var definition = new CardDefinition { cardType = CardType.Unit };
            var first = new CardData(definition);
            var overflow = new CardData(definition);
            int events = 0;
            hand.HandChanged += () => events++;

            hand.AddCard(first);
            int versionAtCapacity = hand.MutationVersion;
            hand.AddCard(overflow);

            Assert.That(hand.Hand, Has.Count.EqualTo(1));
            Assert.That(hand.Hand[0], Is.SameAs(first));
            Assert.That(hand.MutationVersion, Is.EqualTo(versionAtCapacity));
            Assert.That(events, Is.EqualTo(1), "Rejected grants must not trigger a false hand refresh.");
        }

        [Test]
        public void PrepaidProductionCanOverflowButOrdinaryGrantsStayBlocked()
        {
            var hand = new AiHandData(null, Faction.None, 0, capacity: 1);
            var definition = new CardDefinition { cardType = CardType.Hero };
            var ordinary = new CardData(definition);
            var generated = new CardData(definition) { ResearchProductionCreated = true };
            hand.AddCard(ordinary);
            hand.AddCard(generated);
            hand.AddCard(new CardData(definition));

            Assert.That(hand.Hand, Has.Count.EqualTo(2));
            Assert.That(hand.Hand[1], Is.SameAs(generated));
            Assert.That(hand.HasFreeSlot, Is.False);

            Assert.That(hand.RemoveCard(generated), Is.True);
            Assert.That(hand.HasFreeSlot, Is.False, "Ordinary hand still occupies its only slot.");
            Assert.That(hand.RemoveCard(ordinary), Is.True);
            Assert.That(hand.HasFreeSlot, Is.True);
            hand.AddCard(new CardData(definition));
            Assert.That(hand.Hand, Has.Count.EqualTo(1));
        }

        [Test]
        public void ZeroCapacityRejectsOrdinaryCardsButPreservesPrepaidOutput()
        {
            var hand = new AiHandData(null, Faction.None, 0, capacity: 0);
            var definition = new CardDefinition { cardType = CardType.Facility };
            hand.AddCard(new CardData(definition));
            Assert.That(hand.Hand, Is.Empty);

            hand.AddCard(new CardData(definition) { ResearchProductionCreated = true });
            Assert.That(hand.Hand, Has.Count.EqualTo(1));
        }
    }
}
#endif
