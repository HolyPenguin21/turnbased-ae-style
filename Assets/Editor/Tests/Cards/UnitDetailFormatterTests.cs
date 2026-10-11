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
    }
}
#endif
