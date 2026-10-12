#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Cards;
using Game.UI;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AttachmentCompatibilityTextTests
    {
        [TestCase(UnitTypeTag.Bio, EquipmentHostKind.Unit, "Bio, Unit")]
        [TestCase(UnitTypeTag.Bio, EquipmentHostKind.Hero, "Bio, Hero")]
        [TestCase(UnitTypeTag.Infantry, EquipmentHostKind.Unit, "Infantry, Unit")]
        [TestCase(UnitTypeTag.Mechanical, EquipmentHostKind.Unit, "Mechanical, Unit")]
        public void TagsPrecedeHostKindOnOneLine(UnitTypeTag tag, EquipmentHostKind kind, string expected)
        {
            var card = new CardDefinition { equipment = new EquipmentGrant
            {
                hostTypeTags = new List<UnitTypeTag> { tag },
                hostKinds = new List<EquipmentHostKind> { kind },
            } };
            Assert.That(EquipmentCardText.CardFace(card, null), Is.EqualTo(expected));
            Assert.That(EquipmentCardText.Description(card, null), Is.EqualTo(expected));
        }

        [Test]
        public void MultipleTagsUseTheSameCompactRuleForEquipmentAndMutator()
        {
            var card = new CardDefinition { equipment = new EquipmentGrant
            {
                hostTypeTags = new List<UnitTypeTag> { UnitTypeTag.Bio, UnitTypeTag.Infantry },
                hostKinds = new List<EquipmentHostKind> { EquipmentHostKind.Unit },
            } };
            foreach (var slot in new[] { AttachmentSlot.Equipment, AttachmentSlot.Mutator })
            {
                card.attachmentSlot = slot;
                Assert.That(EquipmentCardText.CardFace(card, null), Is.EqualTo("Bio, Infantry, Unit"));
            }
            card.equipment.hostTypeTags = new List<UnitTypeTag> { UnitTypeTag.Armored, UnitTypeTag.Vehicle };
            card.attachmentSlot = AttachmentSlot.Equipment;
            Assert.That(EquipmentCardText.CardFace(card, null), Is.EqualTo("Armored, Vehicle, Unit"));
        }

        [Test]
        public void DuplicatesAndMissingFieldsDoNotCreateExtraCommasOrEmptyLines()
        {
            var grant = new EquipmentGrant
            {
                hostTypeTags = new List<UnitTypeTag> { UnitTypeTag.Hero, UnitTypeTag.Hero },
                hostKinds = new List<EquipmentHostKind> { EquipmentHostKind.Hero, EquipmentHostKind.Hero },
            };
            Assert.That(EquipmentCardText.AttachTargets(grant), Is.EqualTo("Hero"));
            grant.hostTypeTags.Clear();
            Assert.That(EquipmentCardText.AttachTargets(grant), Is.EqualTo("Hero"));
            grant.hostKinds.Clear();
            Assert.That(EquipmentCardText.AttachTargets(grant), Is.Empty);
            grant.hostTypeTags.Add(UnitTypeTag.Bio);
            Assert.That(EquipmentCardText.AttachTargets(grant), Is.EqualTo("Bio"));
            grant.hostTypeTags = null; grant.hostKinds = null;
            Assert.That(EquipmentCardText.AttachTargets(grant), Is.Empty);
            Assert.That(EquipmentCardText.AttachTargets(null), Is.Empty);
            var card = new CardDefinition { equipment = grant };
            grant.addAbilities.Add(UnitAbilities.ShockAttack);
            Assert.That(EquipmentCardText.CardFace(card, null), Is.EqualTo("Shock Attack"));
        }

        [TestCase(EquipmentHostKind.Unit, "Bio, Unit")]
        [TestCase(EquipmentHostKind.Hero, "Bio, Hero")]
        public void MutatorMandatoryBioIsShownEvenWithAnEmptyAuthoredTagList(EquipmentHostKind kind, string expected)
        {
            var card = new CardDefinition { attachmentSlot = AttachmentSlot.Mutator,
                equipment = new EquipmentGrant { hostKinds = new List<EquipmentHostKind> { kind } } };
            Assert.That(EquipmentCardText.CardFace(card, null), Is.EqualTo(expected));
            Assert.That(EquipmentCardText.Description(card, null), Is.EqualTo(expected));
        }
    }
}
#endif
