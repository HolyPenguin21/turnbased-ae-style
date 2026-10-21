#if UNITY_INCLUDE_TESTS
using Game.Cards;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class ResearchProductionChallengeTests
    {
        [TestCase(0)]
        [TestCase(3)]
        [TestCase(7)]
        public void FirstPoolUsesFullHeroFateIncludingAttachments(int fateMax)
        {
            var hero = new UnitData { FateMax = fateMax, Fate = 0, CommandRating = 9, Attack = 12 };
            Assert.That(ResearchProductionSystem.TokenPoolSizeFor(hero), Is.EqualTo(fateMax));
            Assert.That(ResearchProductionSystem.TokenPoolSizeFor(null), Is.Zero);
        }

        [TestCase(2, 1, 3, 2)] // two hits, one miss, three Fate: two ghosts
        [TestCase(3, 0, 3, 3)] // all three hits: maximum six
        [TestCase(3, 0, 2, 2)] // successful reroll preserves the two ghosts
        [TestCase(2, 1, 2, 1)] // failed reroll removes one ghost
        [TestCase(2, 1, 1, 0)]
        [TestCase(0, 3, 2, 0)] // Fate smaller than the miss count
        [TestCase(3, 1, 2, 1)] // failed overflow: one real miss, one ghost
        [TestCase(4, 0, 2, 2)] // successful overflow converts a ghost into a hit
        [TestCase(3, 0, 0, 0)]
        public void PreviewReservesFateForMisses(int hits, int misses, int fate, int expected)
        {
            var tokens = new bool[hits + misses];
            for (int i = 0; i < hits; i++) tokens[i] = true;
            Assert.That(ResearchProductionSystem.FatePreviewCount(tokens, fate), Is.EqualTo(expected));
            Assert.That(ResearchProductionSystem.CountHits(tokens), Is.EqualTo(hits), "Ghosts never score.");
            Assert.That(ResearchProductionSystem.FatePreviewCount(null, fate), Is.Zero);
        }

        [Test]
        public void ForecastUsesFullPoolAndOnlyRemainingFateForRerolls()
        {
            var hero = new UnitData { FateMax = 3, Fate = 1 };
            var card = new CardDefinition { fate = 4 };
            Assert.That(ResearchProductionSystem.EstimateSuccessChance(hero, card),
                Is.EqualTo(1f / 16).Within(0.000001f));
            Assert.That(ResearchProductionSystem.EstimateSuccessChance(hero, card, 0), Is.Zero);
            Assert.That(hero.Fate, Is.EqualTo(1));
            Assert.That(hero.FateMax, Is.EqualTo(3));
        }

        [Test]
        public void FateUpgradeImprovesBothTheInitialPoolAndFurtherAttempts()
        {
            var card = new CardDefinition { fate = 5 };
            Assert.That(ResearchProductionSystem.EstimateSuccessChanceForFate(3, card),
                Is.EqualTo(7f / 64).Within(0.000001f));
            Assert.That(ResearchProductionSystem.EstimateSuccessChanceForFate(4, card),
                Is.EqualTo(93f / 256).Within(0.000001f));
        }
    }
}
#endif
