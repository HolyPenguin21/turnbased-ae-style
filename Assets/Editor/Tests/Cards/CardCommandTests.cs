#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class CardCommandTests
    {
        [Test]
        public void HandRejectsDifferentObjectWithAlreadyHeldRuntimeId()
        {
            var definition = AttachmentSlotTests.Host();
            var hand = new PlayerHandData(null, 0);
            var card = new CardData(definition);
            hand.AddCard(card);
            int version = hand.MutationVersion;
            var alias = CardData.CreateDetached(definition, card.RuntimeId);
            Assert.That(hand.TryAddCard(alias), Is.False);
            Assert.That(hand.Hand, Is.EqualTo(new[] { card }));
            Assert.That(hand.MutationVersion, Is.EqualTo(version));
        }

        [Test]
        public void TemporaryBattleBonusDoesNotBecomeAttachmentBaseline()
        {
            var unit = AttachmentSlotTests.Body();
            unit.Attack += 3; unit.DrillAttackBonus = 2; unit.BerserkStacks = 1;
            unit.Defense -= 1; unit.BerserkDefenseLost = 1;
            var item = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Attack, 10, true);
            item.grantedAbilities.Add(UnitAbilities.SingleUse);
            EquipmentSystem.ApplyAttachments(unit, item, null);
            Assert.That(unit.Attack, Is.EqualTo(13), "Permanent override must preserve +3 temporary attack.");
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(unit.Attack, Is.EqualTo(8));
            Assert.That(unit.AttachmentBase.Value.Stats[EquipmentStat.Attack], Is.EqualTo(5));
            Assert.That(unit.AttachmentBase.Value.Stats[EquipmentStat.Defense], Is.EqualTo(3));
            EquipmentSystem.ApplyAttachments(unit, item, null);
            Assert.That(unit.Attack, Is.EqualTo(13));
            Assert.That(unit.Defense, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void AttachmentAndConsumptionPreserveDefenseFloorAndRevertOnlyActualBerserkLoss(bool consume)
        {
            var unit = AttachmentSlotTests.Body(); unit.Abilities.Add(UnitAbilities.Berserk);
            var item = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Defense, consume ? 4 : 1, !consume);
            if (consume)
            {
                item.grantedAbilities.Add(UnitAbilities.SingleUse); EquipmentSystem.ApplyAttachments(unit, item, null);
                unit.Defense = 1; unit.BerserkDefenseLost = 6; unit.BerserkStacks = 6; unit.Attack += 6;
                EquipmentSystem.ConsumeAfterAttack(unit);
            }
            else
            {
                unit.Defense = 1; unit.BerserkDefenseLost = 2; unit.BerserkStacks = 2; unit.Attack += 2;
                EquipmentSystem.ApplyAttachments(unit, item, null);
            }
            Assert.That(unit.Defense, Is.EqualTo(1));
            Assert.That(unit.BerserkDefenseLost, Is.EqualTo(consume ? 2 : 0));
            ChallengeResult.ApplyBerserkOnHit(unit, AbilityMagnitudes.Default);
            var army = new ArmyData(); army.Members.Add(unit);
            BattleEngine.RevertTemporaryBattleStats(army, AbilityMagnitudes.Default);
            Assert.That(unit.Defense, Is.EqualTo(consume ? 3 : 1)); Assert.That(unit.Attack, Is.EqualTo(5));
        }

        [Test] public void GrantingRapidReactionDoesNotMakeAnAttachmentCardFree()
        {
            var definition = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment); definition.apCost = 3;
            definition.equipment.addAbilities.Add(UnitAbilities.RapidReaction);
            var card = new CardData(definition);
            Assert.That(CardCostRules.PlayAp(card), Is.EqualTo(3));
            Assert.That(CardCostRules.PlayAp(card, new[] { UnitAbilities.RapidReaction }), Is.EqualTo(3));
        }
    }
}
#endif
