#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AttachmentSlotTests
    {
        internal static CardDefinition Host(bool hero = false, bool bio = true) => new CardDefinition
        {
            cardType = hero ? CardType.Hero : CardType.Unit,
            attack = 5, defenseRating = 3, resistanceRating = 1, range = 2,
            hitPoints = 10, moveMax = 6, fate = 4, initiative = 2, activationApCost = 2,
            commandRating = 5,
            unitTypeTags = new List<UnitTypeTag> { bio ? UnitTypeTag.Bio : UnitTypeTag.Mechanical },
            grantedAbilities = new List<string> { "r1s4", UnitAbilities.CeramicArmor },
        };

        internal static CardDefinition Attachment(AttachmentSlot slot, EquipmentStat stat = EquipmentStat.Attack,
            int amount = 2, bool replace = false) => new CardDefinition
        {
            cardType = CardType.Equipment, attachmentSlot = slot,
            equipment = new EquipmentGrant
            {
                hostKinds = new List<EquipmentHostKind> { EquipmentHostKind.Unit, EquipmentHostKind.Hero },
                statChanges = new List<EquipmentStatChange>
                {
                    new EquipmentStatChange { stat = stat, amount = amount, isOverride = replace },
                },
            },
        };

        internal static UnitData Body(CardDefinition host = null)
        {
            host = host ?? Host();
            var unit = new UnitData
            {
                Attack = host.attack, Defense = host.defenseRating, Resistance = host.resistanceRating,
                Range = host.range, HitPointsMax = host.hitPoints, HitPointsCurrent = host.hitPoints,
                MoveMax = host.moveMax, MoveCurrent = host.moveMax, FateMax = host.fate, Fate = host.fate,
                Initiative = host.initiative, ActivationApCost = host.activationApCost,
                CommandRating = host.commandRating, IsHero = host.cardType == CardType.Hero, OriginatingCard = host,
            };
            unit.Abilities.UnionWith(host.grantedAbilities);
            unit.TypeTags.UnionWith(host.unitTypeTags);
            return unit;
        }

        [Test]
        public void LegacyDefinitionDefaultsToEquipmentWithoutDataMigration()
        {
            Assert.That(new CardDefinition().attachmentSlot, Is.EqualTo(AttachmentSlot.Equipment));
            Assert.That((int)AttachmentSlot.Equipment, Is.Zero);
            Assert.That(Attachment(AttachmentSlot.Mutator).cardType, Is.EqualTo(CardType.Equipment));
        }

        [TestCase(false, true, true)]
        [TestCase(false, false, false)]
        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        public void MutatorRequiresExplicitBioForUnitsAndHeroes(bool hero, bool bio, bool allowed)
        {
            var mutator = Attachment(AttachmentSlot.Mutator);
            // Empty authored tag constraints cannot bypass the mandatory system constraint.
            Assert.That(EquipmentSystem.FitsHost(mutator, Host(hero, bio), out _), Is.EqualTo(allowed));
            Assert.That(EquipmentSystem.FitsHost(Attachment(AttachmentSlot.Equipment), Host(hero, bio), out _), Is.True);
        }

        [Test]
        public void MutatorStillHonorsAuthoredHostKindsAndTags()
        {
            var mutator = Attachment(AttachmentSlot.Mutator);
            mutator.equipment.hostTypeTags.Add(UnitTypeTag.Infantry);
            var host = Host();
            Assert.That(EquipmentSystem.FitsHost(mutator, host, out _), Is.False);
            host.unitTypeTags.Add(UnitTypeTag.Infantry);
            Assert.That(EquipmentSystem.FitsHost(mutator, host, out _), Is.True);
            mutator.equipment.hostKinds.Clear();
            Assert.That(EquipmentSystem.FitsHost(mutator, host, out _), Is.False);
            mutator.cardType = CardType.Unit;
            Assert.That(EquipmentSystem.FitsHost(mutator, host, out _), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BothSlotsUseCanonicalOverrideThenAddOrder(bool reverse)
        {
            var equipment = Attachment(AttachmentSlot.Equipment, amount: 8, replace: true);
            var mutator = Attachment(AttachmentSlot.Mutator);
            var unit = Body();
            if (reverse)
            {
                EquipmentSystem.ApplyAttachments(unit, null, mutator);
                EquipmentSystem.ApplyAttachments(unit, equipment, null);
            }
            else EquipmentSystem.ApplyAttachments(unit, equipment, mutator);
            Assert.That(unit.Attack, Is.EqualTo(10));
            Assert.That(unit.Equipment, Is.SameAs(equipment));
            Assert.That(unit.Mutator, Is.SameAs(mutator));
            var card = new CardData(unit.OriginatingCard) { Equipment = equipment, Mutator = mutator };
            Assert.That(EquipmentSystem.Project(card).Stats[EquipmentStat.Attack], Is.EqualTo(unit.Attack));
            Assert.That(AiPower.EffectiveCardLine(card).Attack, Is.EqualTo(unit.Attack));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AbilityClearRemoveAndAddUseTheSameCanonicalOrder(bool reverse)
        {
            var equipment = Attachment(AttachmentSlot.Equipment);
            equipment.equipment.clearAbilityFamilies.Add(AbilityFamily.Recce);
            equipment.equipment.addAbilities.Add("r2s6");
            var mutator = Attachment(AttachmentSlot.Mutator);
            mutator.equipment.clearAbilityFamilies.Add(AbilityFamily.Recce);
            mutator.equipment.removeAbilities.Add(UnitAbilities.CeramicArmor);
            mutator.equipment.addAbilities.Add("r3s8");
            var unit = Body();
            if (reverse)
            {
                EquipmentSystem.ApplyAttachments(unit, null, mutator);
                EquipmentSystem.ApplyAttachments(unit, equipment, null);
            }
            else EquipmentSystem.ApplyAttachments(unit, equipment, mutator);
            Assert.That(unit.Abilities, Is.EquivalentTo(new[] { "r3s8" }));
            Assert.That(EquipmentSystem.Project(unit.OriginatingCard, equipment, mutator).Abilities,
                Is.EquivalentTo(unit.Abilities));
        }

        [Test]
        public void SingleEquipmentKeepsLegacyApplyStatsAbilitiesAndCurrentResources()
        {
            var equipment = Attachment(AttachmentSlot.Equipment, EquipmentStat.Defense, -20);
            equipment.equipment.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.HitPoints, amount = 3 });
            equipment.equipment.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.MoveMax, amount = 2 });
            equipment.equipment.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.Fate, amount = 1 });
            equipment.equipment.clearAbilityFamilies.Add(AbilityFamily.Recce);
            equipment.equipment.addAbilities.Add(UnitAbilities.RapidReaction);
            var legacy = Body(); var actual = Body();
            legacy.HitPointsCurrent = actual.HitPointsCurrent = 4;
            legacy.MoveCurrent = actual.MoveCurrent = 1;
            legacy.Fate = actual.Fate = 1;
            EquipmentSystem.Apply(equipment.equipment, legacy);
            EquipmentSystem.ApplyAttachments(actual, equipment, null);
            Assert.That(actual.Defense, Is.EqualTo(legacy.Defense).And.EqualTo(1));
            Assert.That(actual.HitPointsMax, Is.EqualTo(legacy.HitPointsMax));
            Assert.That(actual.HitPointsCurrent, Is.EqualTo(legacy.HitPointsCurrent).And.EqualTo(7));
            Assert.That(actual.MoveCurrent, Is.EqualTo(legacy.MoveCurrent).And.EqualTo(3));
            Assert.That(actual.Fate, Is.EqualTo(legacy.Fate).And.EqualTo(2));
            Assert.That(actual.ActivationApCost, Is.EqualTo(legacy.ActivationApCost).And.Zero);
            Assert.That(actual.Abilities, Is.EquivalentTo(legacy.Abilities));
        }

        [Test]
        public void RebuildPreservesDamageSpentResourcesAndTemporaryBattleState()
        {
            var equipment = Attachment(AttachmentSlot.Equipment, amount: 8, replace: true);
            var mutator = Attachment(AttachmentSlot.Mutator);
            var unit = Body();
            EquipmentSystem.ApplyAttachments(unit, null, mutator);
            unit.HitPointsCurrent = 4; unit.MoveCurrent = 1; unit.Fate = 1;
            unit.Attack += 3; unit.Defense -= 2;
            unit.BerserkStacks = 3; unit.BerserkDefenseLost = 2; unit.IsHidden = true;
            unit.Abilities.Add("runtime-only");
            unit.Abilities.Remove(UnitAbilities.CeramicArmor);
            var predicted = EquipmentSystem.PredictAttachment(equipment, unit);
            Assert.That(unit.Attack, Is.EqualTo(10), "Prediction must be read-only");
            EquipmentSystem.ApplyAttachments(unit, equipment, null);
            Assert.That(unit.Attack, Is.EqualTo(13));
            Assert.That(unit.Attack, Is.EqualTo(predicted.Stats[EquipmentStat.Attack]));
            Assert.That(unit.Defense, Is.EqualTo(1));
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(4));
            Assert.That(unit.MoveCurrent, Is.EqualTo(1));
            Assert.That(unit.Fate, Is.EqualTo(1));
            Assert.That(unit.BerserkStacks, Is.EqualTo(3));
            Assert.That(unit.BerserkDefenseLost, Is.EqualTo(2));
            Assert.That(unit.IsHidden, Is.True);
            Assert.That(unit.Abilities, Does.Contain("runtime-only").And.Not.Contain(UnitAbilities.CeramicArmor));
        }

        [TestCase(EquipmentStat.HitPoints)]
        [TestCase(EquipmentStat.MoveMax)]
        [TestCase(EquipmentStat.Fate)]
        public void IncreasingMaximumPreservesPreviouslySpentAmount(EquipmentStat stat)
        {
            var unit = Body();
            EquipmentSystem.ApplyAttachments(unit, null, Attachment(AttachmentSlot.Mutator));
            unit.HitPointsCurrent -= 3; unit.MoveCurrent -= 3; unit.Fate -= 3;
            EquipmentSystem.ApplyAttachments(unit, Attachment(AttachmentSlot.Equipment, stat, 2), null);
            Assert.That(unit.HitPointsMax - unit.HitPointsCurrent, Is.EqualTo(3));
            Assert.That(unit.MoveMax - unit.MoveCurrent, Is.EqualTo(3));
            Assert.That(unit.FateMax - unit.Fate, Is.EqualTo(3));
        }

        [Test]
        public void HandAndMaterializationProjectionInsertEquipmentBeforeExistingMutator()
        {
            var card = new CardData(Host()) { Mutator = Attachment(AttachmentSlot.Mutator) };
            var equipment = Attachment(AttachmentSlot.Equipment, amount: 8, replace: true);
            var plan = new MaterializationPlan { BaseCardInHand = card, EquipmentInHand = new CardData(equipment) };
            Assert.That(AiPower.ProjectMaterialization(plan).Attack, Is.EqualTo(10));
            Assert.That(EquipmentSystem.Project(card, equipment).Stats[EquipmentStat.Attack], Is.EqualTo(10));
            Assert.That(card.Equipment, Is.Null);
            Assert.That(EquipmentSystem.GetAttachment(card, equipment), Is.Null);
            Assert.That(EquipmentSystem.GetAttachment(card, card.Mutator), Is.SameAs(card.Mutator));
        }

        [Test]
        [TestCase(EquipmentStat.HitPoints)]
        [TestCase(EquipmentStat.MoveMax)]
        [TestCase(EquipmentStat.Fate)]
        public void ConflictingMaximumChangesPreserveConsumptionInEitherInstallationOrder(EquipmentStat stat)
        {
            var equipment = Attachment(AttachmentSlot.Equipment, stat, 5);
            var mutator = Attachment(AttachmentSlot.Mutator, stat, 8, replace: true);
            var forward = Body(); var reverse = Body();
            forward.HitPointsCurrent = reverse.HitPointsCurrent = 4;
            forward.MoveCurrent = reverse.MoveCurrent = 2;
            forward.Fate = reverse.Fate = 1;
            EquipmentSystem.ApplyAttachments(forward, equipment, mutator);
            EquipmentSystem.ApplyAttachments(reverse, null, mutator);
            EquipmentSystem.ApplyAttachments(reverse, equipment, null);
            Assert.That(forward.HitPointsCurrent, Is.EqualTo(reverse.HitPointsCurrent));
            Assert.That(forward.MoveCurrent, Is.EqualTo(reverse.MoveCurrent));
            Assert.That(forward.Fate, Is.EqualTo(reverse.Fate));
            Assert.That(forward.HitPointsMax - forward.HitPointsCurrent, Is.EqualTo(6));
            Assert.That(forward.MoveMax - forward.MoveCurrent, Is.EqualTo(4));
            Assert.That(forward.FateMax - forward.Fate, Is.EqualTo(3));
        }

        [Test]
        public void SlotAwareProjectionDoesNotInventPreviouslySpentHeroFateAsAnUpgrade()
        {
            var unit = Body(Host(hero: true));
            EquipmentSystem.ApplyAttachments(unit, null, Attachment(AttachmentSlot.Mutator));
            unit.Fate = 1;
            var mobility = Attachment(AttachmentSlot.Equipment, EquipmentStat.MoveMax, 2);
            var delta = StrategicCardEvaluator.EquipmentDeltaParts(mobility, unit);
            Assert.That(delta.Combat, Is.Zero);
            Assert.That(delta.Tactical, Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(unit.Fate, Is.EqualTo(1));
        }

        [Test]
        public void OccupancyRejectsOnlyCorrespondingSlotBeforePayment()
        {
            var card = new CardData(Host()) { Equipment = Attachment(AttachmentSlot.Equipment) };
            var mutator = Attachment(AttachmentSlot.Mutator);
            Assert.That(EquipmentSystem.CanAttach(card.Equipment, card, null, out var why), Is.False);
            Assert.That(why, Does.Contain("already has equipment"));
            Assert.That(EquipmentSystem.GetAttachment(card, mutator), Is.Null,
                "Equipment occupancy must not occupy the Mutator slot");
            card.Mutator = mutator;
            Assert.That(EquipmentSystem.CanAttach(mutator, card, null, out why), Is.False);
            Assert.That(why, Does.Contain("already has mutator"));
            var unit = Body(); unit.Mutator = mutator;
            Assert.That(EquipmentSystem.CanAttach(mutator, unit, null, out why), Is.False);
            Assert.That(why, Does.Contain("already has mutator"));
        }
    }
}
#endif
