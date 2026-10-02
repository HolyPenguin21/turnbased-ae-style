#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiIdleCardPressureTests
    {
        private static float Bank(ResourceType type) => 10f;
        private static float EmptyBank(ResourceType type) => 0f;

        [Test]
        public void NoBonusWithinGrace()
        {
            Assert.That(IdleCardPressure.Bonus(AiConfigV2.idleCardGraceTurns, 1f), Is.EqualTo(0f));
            Assert.That(IdleCardPressure.Bonus(0, 1f), Is.EqualTo(0f));
        }

        [Test]
        public void BonusGrowsLinearlyWithAgeThenHitsCap()
        {
            int g = AiConfigV2.idleCardGraceTurns;
            float one = IdleCardPressure.Bonus(g + 1, 1f);
            float two = IdleCardPressure.Bonus(g + 2, 1f);
            Assert.That(one, Is.EqualTo(AiConfigV2.idleCardBonusPerTurn).Within(1e-5f));
            Assert.That(two, Is.EqualTo(2f * AiConfigV2.idleCardBonusPerTurn).Within(1e-5f));
            Assert.That(IdleCardPressure.Bonus(g + 1000, 1f), Is.EqualTo(AiConfigV2.idleCardBonusCap));
        }

        [Test]
        public void BonusScalesWithSaturationAndVanishesWithoutSurplus()
        {
            int age = AiConfigV2.idleCardGraceTurns + 5;
            float full = IdleCardPressure.Bonus(age, 1f);
            Assert.That(IdleCardPressure.Bonus(age, 0.5f), Is.EqualTo(full * 0.5f).Within(1e-5f));
            Assert.That(IdleCardPressure.Bonus(age, 0f), Is.EqualTo(0f));
        }

        [Test]
        public void SaturationIsZeroWhenBankCannotCoverTheCost()
        {
            var cost = new ResourceCost(human: 2, tech: 3);
            Assert.That(IdleCardPressure.Saturation(cost, EmptyBank), Is.EqualTo(0f));
            Assert.That(IdleCardPressure.Saturation(cost, t => t == ResourceType.Tech ? 3f : 50f),
                Is.EqualTo(0f), "the tightest costed resource decides");
            Assert.That(IdleCardPressure.Saturation(cost, Bank), Is.EqualTo(1f));
        }

        [Test]
        public void SaturationWithoutBankReadingIsZeroAndWithoutCostIsOne()
        {
            Assert.That(IdleCardPressure.Saturation(new ResourceCost(human: 1), null), Is.EqualTo(0f));
            Assert.That(IdleCardPressure.Saturation(null, Bank), Is.EqualTo(1f));
        }

        [Test]
        public void IdleBonusIsCappedBelowARealPlayScore()
        {
            // A card with genuine value (+0.5 and up) keeps its lead over an idle card whose
            // ordinary score is slightly negative: the cap alone can lift -0.3 to +0.3 at most.
            Assert.That(IdleCardPressure.Bonus(1000, 1f) + (-0.3f), Is.LessThan(0.5f));
        }

        [Test]
        public void HandStampsAcquiredTurnAndAgeReadsOffTheCard()
        {
            var hand = new AiHandData(null, Faction.None, 0);
            hand.SetCurrentTurn(3);
            var card = new CardData(new CardDefinition { cardType = CardType.Unit });
            hand.AddCard(card);

            Assert.That(card.AcquiredTurn, Is.EqualTo(3));
            Assert.That(AiHandData.AgeInTurns(card, 10), Is.EqualTo(7));
            Assert.That(AiHandData.AgeInTurns(new CardData(null), 10), Is.EqualTo(0), "unstamped card has no age");
        }
    }
}
#endif
