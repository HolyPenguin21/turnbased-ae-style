#if UNITY_INCLUDE_TESTS
using Game.UI;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class UnitDetailFormatterTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void DetailNamesBothAttachmentsAndKeepsHeroStatConvention(bool hero)
        {
            var unit = AttachmentSlotTests.Body(AttachmentSlotTests.Host(hero: hero));
            unit.Name = "detail-unit";
            unit.Equipment = AttachmentSlotTests.Attachment(Game.Cards.AttachmentSlot.Equipment);
            unit.Equipment.displayName = "field-kit";
            unit.Mutator = AttachmentSlotTests.Attachment(Game.Cards.AttachmentSlot.Mutator);
            unit.Mutator.displayName = "genome";
            string text = UnitDetailFormatter.Format(unit, 0,
                default(UnityEngine.Color), default(UnityEngine.Color), "Recce details");
            Assert.That(text, Does.Contain("detail-unit").And.Contain("HP 10/10"));
            Assert.That(text, Does.Contain("Equipment: field-kit").And.Contain("Mutator: genome"));
            Assert.That(text, Does.Contain("Recce details"));
            Assert.That(text.Contains("Attack "), Is.EqualTo(!hero));
            Assert.That(text.Contains("Defense "), Is.EqualTo(!hero));
            Assert.That(text.Contains("Command Rating:"), Is.EqualTo(hero));
            Assert.That(text.Contains("Fate:"), Is.EqualTo(hero));
            Assert.That(text, Does.Not.Contain("Resistance").And.Not.Contain("Activation"));
        }

        [Test]
        public void AttachmentsAndCombatRecordFollowAnEmptyLine()
        {
            var unit = AttachmentSlotTests.Body(AttachmentSlotTests.Host(hero: false));
            unit.Equipment = AttachmentSlotTests.Attachment(Game.Cards.AttachmentSlot.Equipment);
            unit.Equipment.displayName = "field-kit";
            unit.CombatRecord = new Game.Units.UnitCombatRecord { BattlesSurvived = 2, DamageDealt = 9, DamageTaken = 4, Kills = 1 };
            string text = UnitDetailFormatter.Format(unit, 0,
                default(UnityEngine.Color), default(UnityEngine.Color), "\nRecce\n  details");
            Assert.That(text, Does.Contain("details\n\nEquipment: field-kit\n\nBattles survived: 2"
                + "\nDamage dealt: 9\nDamage taken: 4\nKills: 1"));
            Assert.That(text, Does.EndWith("Kills: 1"));

            unit.Equipment = null;
            text = UnitDetailFormatter.Format(unit, 0, default(UnityEngine.Color), default(UnityEngine.Color), null);
            Assert.That(text, Does.Contain("Initiative 2\n\nBattles survived: 2"));
            Assert.That(text, Does.Not.Contain("\n\n\n"));

            Assert.That(UnitDetailFormatter.FormatBody(unit, null), Does.Not.Contain("Battles survived"),
                "A collection card definition has no combat record.");
        }

        [Test]
        public void TitleOwnedNameIsOmittedAndModifierIsSpacedFromStat()
        {
            var unit = AttachmentSlotTests.Body(AttachmentSlotTests.Host(hero: false));
            unit.Name = "detail-unit";
            string text = UnitDetailFormatter.Format(unit, 3,
                default(UnityEngine.Color), default(UnityEngine.Color), null, includeName: false);
            Assert.That(text, Does.Not.Contain("detail-unit"));
            Assert.That(text, Does.Match(@"Defense \d+ <color=#[0-9A-F]{6}>\(\+3\)</color>"));
        }
    }
}
#endif
