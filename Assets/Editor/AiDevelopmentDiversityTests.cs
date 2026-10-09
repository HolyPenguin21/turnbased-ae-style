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

        // The supply multiplier and the repeat damp are inputs of the Development decision, so the
        // admission fingerprint must change with them (otherwise Development is not re-evaluated).
        [Test]
        public void AdmissionFingerprint_ChangesWithDeckSize()
        {
            WorldSnapshot Snap(int deck) => new WorldSnapshot { TurnNumber = 8,
                Self = new SelfSnapshot { Deck = new CardDefinition[deck], Hand = new Game.Cards.CardData[0],
                    BaseHexes = new[] { new Game.HexGrid.HexCoord(0, 0) }, Armies = new ArmySnapshot[0] },
                Development = new DevelopmentReadiness { Facilities = new DevelopmentFacility[0] } };
            string full = DevelopmentAdmission.Fingerprint(Snap(30), null, 4, "3,3,3,3", 7);
            Assert.That(DevelopmentAdmission.Fingerprint(Snap(29), null, 4, "3,3,3,3", 7),
                Is.Not.EqualTo(full), "one card fewer in the deck moves the supply multiplier");
        }

        [Test]
        public void HistoryKey_FollowsAttemptsInsideTheWindow()
        {
            Assert.That(DevelopmentDiversity.HistoryKey(player, 7), Is.EqualTo("-"));
            DevelopmentDiversity.RecordAttempt(player, 7, new CardDefinition { authoredKey = "card-a" });
            string key = DevelopmentDiversity.HistoryKey(player, 7);
            Assert.That(key, Is.EqualTo("card-ax1"));
            DevelopmentDiversity.RecordAttempt(player, 8, new CardDefinition { authoredKey = "card-a" });
            Assert.That(DevelopmentDiversity.HistoryKey(player, 8), Is.Not.EqualTo(key));
            Assert.That(DevelopmentDiversity.HistoryKey(player, 8 + AiConfigV2.devDiversityWindowTurns), Is.Empty.Or.EqualTo(""),
                "attempts leave the key when they leave the window");
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

        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void GeneratedAttachmentDeploymentUsesTheSameRepeatFactorAndHandItemsStayUnchanged(AttachmentSlot slot)
        {
            var host = new CardData(AttachmentSlotTests.Host());
            var equipment = AttachmentSlotTests.Attachment(slot, EquipmentStat.Attack, 4);
            equipment.authoredKey = "repeat-output";
            var snap = new WorldSnapshot { Observer = player, TurnNumber = 8 };
            var plan = new MaterializationPlan { Kind = MaterializationChainKind.GenerateAttachDeploy,
                BaseCardInHand = host, GeneratedEquipmentDef = equipment,
                Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true } };
            float fresh = StrategicCardEvaluator.EquipmentUpgradeValue(plan, snap);
            Assert.That(fresh, Is.GreaterThan(0f));
            DevelopmentDiversity.RecordAttempt(player, 8, equipment);
            float factor = DevelopmentDiversity.RepeatFactor(player, 8, equipment, out _);
            Assert.That(StrategicCardEvaluator.EquipmentUpgradeValue(plan, snap),
                Is.EqualTo(fresh * factor).Within(1e-5f));

            // Existing item, first on an existing body, then on a generated body: its purchase
            // has already happened and cannot receive a production repeat discount again.
            plan.GeneratedEquipmentDef = null;
            plan.EquipmentInHand = new CardData(equipment);
            plan.Generation = null;
            plan.Kind = MaterializationChainKind.AttachDeploy;
            Assert.That(StrategicCardEvaluator.EquipmentUpgradeValue(plan, snap), Is.EqualTo(fresh).Within(1e-5f));
            plan.Generation = new GenerationStep { CardDef = host.Definition, ProducesEquipment = false };
            plan.Kind = MaterializationChainKind.GenerateAttachDeploy;
            Assert.That(StrategicCardEvaluator.EquipmentUpgradeValue(plan, snap), Is.EqualTo(fresh).Within(1e-5f));
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
