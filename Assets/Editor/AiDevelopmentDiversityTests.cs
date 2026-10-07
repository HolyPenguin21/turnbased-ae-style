#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-10-07 (user decision) — Research/Production must not breed identical items: a
    // saturating ability is damped by the units already carrying it, and a card attempted
    // recently is damped by a small repeat weight (DevelopmentDiversity).
    public class AiDevelopmentDiversityTests
    {
        private PlayerSetupData player;

        [SetUp]
        public void Setup()
        {
            player = new PlayerSetupData();
            DevelopmentDiversity.ClearAll();
        }

        [TearDown]
        public void Cleanup() => DevelopmentDiversity.ClearAll();

        [Test]
        public void RepeatFactor_IsOneForAFreshCardAndFallsWithRecentAttempts()
        {
            float w = AiConfigV2.devDiversityRecentWeight;
            Assert.That(DevelopmentDiversity.RepeatFactor(0), Is.EqualTo(1f));
            Assert.That(DevelopmentDiversity.RepeatFactor(2), Is.EqualTo(1f / (1f + 2f * w)).Within(1e-5f));
            Assert.That(DevelopmentDiversity.RepeatFactor(3), Is.LessThan(DevelopmentDiversity.RepeatFactor(1)));
            Assert.That(DevelopmentDiversity.RepeatFactor(-3), Is.EqualTo(1f), "negative counts never raise the value");
        }

        [Test]
        public void ProductionSupplyMultiplier_ReadsDeckPlusHandAndTreatsNoSelfAsFull()
        {
            Assert.That(DevelopmentOpportunityEvaluator.ProductionSupplyMultiplier(null), Is.EqualTo(1f));
            Assert.That(DevelopmentOpportunityEvaluator.ProductionSupplyMultiplier(new WorldSnapshot()), Is.EqualTo(1f));
            var low = new WorldSnapshot { Self = new SelfSnapshot {
                Deck = new CardDefinition[5], Hand = new Game.Cards.CardData[4] } };
            var full = new WorldSnapshot { Self = new SelfSnapshot {
                Deck = new CardDefinition[29], Hand = new Game.Cards.CardData[6] } };
            Assert.That(DevelopmentOpportunityEvaluator.ProductionSupplyMultiplier(full), Is.EqualTo(1f));
            Assert.That(DevelopmentOpportunityEvaluator.ProductionSupplyMultiplier(low),
                Is.GreaterThan(AiConfigV2.equipSupplyMidMultiplier * 0.9f), "9 of 35 cards: near the one-third point and beyond");
        }

        [Test]
        public void FamilyOf_GroupsStealthAndRecceAndIgnoresStatAbilities()
        {
            Assert.That(DevelopmentDiversity.FamilyOf(UnitAbilities.Stealth4), Is.EqualTo("Stealth"));
            Assert.That(DevelopmentDiversity.FamilyOf(UnitAbilities.R1S4), Is.EqualTo("Recce"));
            Assert.That(DevelopmentDiversity.FamilyOf(UnitAbilities.Splash), Is.EqualTo(UnitAbilities.Splash));
            Assert.That(DevelopmentDiversity.FamilyOf(UnitAbilities.Regeneration), Is.EqualTo(UnitAbilities.Regeneration));
            Assert.That(DevelopmentDiversity.FamilyOf(UnitAbilities.Hyperkinetic), Is.Null,
                "target-share abilities do not saturate by carriers");
            Assert.That(DevelopmentDiversity.FamilyOf(null), Is.Null);
        }

        [Test]
        public void PureStatCard_HasNoSaturatingFamilyAndTwoAbilitiesOneFamily()
        {
            var stat = new CardDefinition { cardType = CardType.Equipment, authoredKey = "stat",
                equipment = new EquipmentGrant() };
            Assert.That(DevelopmentDiversity.SaturatingFamilies(stat), Is.Empty);

            var twice = new CardDefinition { cardType = CardType.Equipment, authoredKey = "twice",
                equipment = new EquipmentGrant { addAbilities = new List<string> {
                    UnitAbilities.Stealth4, UnitAbilities.R1S4, UnitAbilities.Hyperkinetic } } };
            Assert.That(DevelopmentDiversity.SaturatingFamilies(twice), Is.EquivalentTo(new[] { "Stealth", "Recce" }));
        }

        [Test]
        public void RecentAttempts_CountsOnlyTheSameCardInsideTheWindow()
        {
            var a = new CardDefinition { authoredKey = "card-a" };
            var b = new CardDefinition { authoredKey = "card-b" };
            DevelopmentDiversity.RecordAttempt(player, 3, a);
            DevelopmentDiversity.RecordAttempt(player, 4, a);
            DevelopmentDiversity.RecordAttempt(player, 4, b);

            int window = AiConfigV2.devDiversityWindowTurns;
            Assert.That(DevelopmentDiversity.RecentAttempts(player, 4, a), Is.EqualTo(2));
            Assert.That(DevelopmentDiversity.RecentAttempts(player, 4, b), Is.EqualTo(1));
            Assert.That(DevelopmentDiversity.RecentAttempts(player, 3 + window, a), Is.EqualTo(1),
                "the turn-3 attempt leaves the window");
            Assert.That(DevelopmentDiversity.RecentAttempts(player, 4 + window, a), Is.Zero);
            Assert.That(DevelopmentDiversity.RecentAttempts(new PlayerSetupData(), 4, a), Is.Zero,
                "history is per player");
        }
    }
}
#endif
