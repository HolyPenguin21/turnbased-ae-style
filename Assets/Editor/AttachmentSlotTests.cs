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
        public void EveryPublishedAttachmentProjectsTheLiveResultWithWoundsAndEitherOtherSlot()
        {
            var items = AttachmentContentTestData.Blocks(AttachmentContentTestData.NeutralPath)
                .Select(AttachmentContentTestData.Read).Where(c => c.cardType == CardType.Equipment).ToArray();
            Assert.That(items.Length, Is.EqualTo(62));
            Assert.That(items.Count(c => c.attachmentSlot == AttachmentSlot.Equipment), Is.EqualTo(42));
            var paths = new[] { "IronConcord", "TheAshen", "TheVessels" };
            var hosts = paths.SelectMany(f => AttachmentContentTestData.Blocks(
                    $"Assets/Cards/{f}/CardCatalog_{f}.asset"))
                .Select(AttachmentContentTestData.Read)
                .Where(c => c.cardType == CardType.Unit || c.cardType == CardType.Hero).ToArray();
            foreach (var item in items)
            {
                int cases = 0;
                foreach (var host in hosts.Where(h => EquipmentSystem.FitsHost(item, h, out _)))
                foreach (var other in new CardDefinition[] { null }.Concat(items.Where(o =>
                    o.attachmentSlot != item.attachmentSlot && EquipmentSystem.FitsHost(o, host, out _))))
                {
                    var equipment = item.attachmentSlot == AttachmentSlot.Equipment ? item : other;
                    var mutator = item.attachmentSlot == AttachmentSlot.Mutator ? item : other;
                    var expected = EquipmentSystem.Project(host, equipment, mutator);
                    var body = Body(host);
                    int wound = host.hitPoints > 1 ? 1 : 0;
                    int spentFate = host.fate > 0 ? 1 : 0;
                    body.HitPointsCurrent -= wound;
                    body.Fate -= spentFate;
                    EquipmentSystem.ApplyAttachments(body, equipment, mutator);
                    string context = $"{item.authoredKey}/{host.authoredKey}/{other?.authoredKey}";
                    Assert.That(new[] { body.Attack, body.Defense, body.Resistance, body.Range,
                        body.HitPointsMax, body.MoveMax, body.Initiative, body.ActivationApCost,
                        body.CommandRating, body.FateMax }, Is.EqualTo(new[] {
                        expected.Stats[EquipmentStat.Attack], expected.Stats[EquipmentStat.Defense],
                        expected.Stats[EquipmentStat.Resistance], expected.Stats[EquipmentStat.Range],
                        expected.Stats[EquipmentStat.HitPoints], expected.Stats[EquipmentStat.MoveMax],
                        expected.Stats[EquipmentStat.Initiative], expected.Stats[EquipmentStat.ActivationApCost],
                        expected.Stats[EquipmentStat.CommandRating], expected.Stats[EquipmentStat.Fate] }), context);
                    Assert.That(body.Abilities, Is.EquivalentTo(expected.Abilities), context);
                    Assert.That(body.HitPointsCurrent,
                        Is.EqualTo(System.Math.Max(1, body.HitPointsMax - wound)), context);
                    Assert.That(body.Fate, Is.EqualTo(System.Math.Max(0, body.FateMax - spentFate)), context);
                    cases++;
                }
                Assert.That(cases, Is.GreaterThan(0), item.authoredKey);
            }
        }

        [Test]
        public void AuthoredTwinSmgOverridesBeforeMutatorAttackAndPreservesWounds()
        {
            var cards = AttachmentContentTestData.Blocks(AttachmentContentTestData.NeutralPath)
                .Select(AttachmentContentTestData.Read).ToArray();
            var equipment = cards.Single(c => c.authoredKey == "neutral.equipment.infantry.twin-smg");
            var mutator = cards.Single(c => c.authoredKey == "neutral.mutator.regenerative-culture");
            var host = Host(); host.attack = 3; host.unitTypeTags.Add(UnitTypeTag.Infantry);
            var body = Body(host); body.HitPointsCurrent -= 3;
            EquipmentSystem.ApplyAttachments(body, equipment, null);
            Assert.That(body.Attack, Is.EqualTo(8));
            EquipmentSystem.ApplyAttachments(body, null, mutator);
            Assert.That(body.Attack, Is.EqualTo(9));
            Assert.That(body.Range, Is.EqualTo(1));
            Assert.That(body.HitPointsMax - body.HitPointsCurrent, Is.EqualTo(3));
            Assert.That(body.Abilities, Has.Member(UnitAbilities.CriticalDamage));
            Assert.That(body.Abilities, Has.Member(UnitAbilities.Regeneration));
        }

        [Test]
        public void OccupancyOnlyChangePublishesCapabilityInvalidation()
        {
            var player = new Game.Players.PlayerSetupData();
            var before = new WorldSnapshot { Self = new SelfSnapshot { Armies = new[]
                { new ArmySnapshot { ArmyId = 9, NonHeroMutatorOccupied = new[] { false } } } } };
            var after = new WorldSnapshot { Self = new SelfSnapshot { Armies = new[]
                { new ArmySnapshot { ArmyId = 9, NonHeroMutatorOccupied = new[] { true } } } } };
            try
            {
                WorldAnalysis.PublishStepObservationDelta(player, 1,
                    new WorldAnalysis.StepObservationStamp(before, default, null),
                    new WorldAnalysis.StepObservationStamp(after, default, null), null);
                Assert.That(StrategicInterruptRegistry.Peek(player, 1).Reasons.HasFlag(
                    StrategicInvalidationReason.Capability), Is.True);
            }
            finally { StrategicInterruptRegistry.ClearAll(); }
        }

        [Test]
        [TestCase(AttachmentSlot.Equipment, false)]
        [TestCase(AttachmentSlot.Mutator, false)]
        [TestCase(AttachmentSlot.Equipment, true)]
        [TestCase(AttachmentSlot.Mutator, true)]
        public void PortfolioCostCountsOnlyTheNewAttachmentAndPopReleasesConsumption(AttachmentSlot slot, bool produced)
        {
            var host = Host(); host.apCost = 2; host.resourceCost = new ResourceCost { human = 1 };
            var card = new CardData(host);
            var alreadyPaid = Attachment(slot == AttachmentSlot.Mutator ? AttachmentSlot.Equipment : AttachmentSlot.Mutator);
            alreadyPaid.resourceCost = new ResourceCost { tech = 99 };
            if (slot == AttachmentSlot.Mutator) card.Equipment = alreadyPaid;
            else card.Mutator = alreadyPaid;
            var definition = Attachment(slot); definition.apCost = 3; definition.activationApCost = 1;
            definition.resourceCost = new ResourceCost { tech = 2 };
            var attachment = new CardData(definition) { ResearchProductionCreated = produced };
            var plan = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.AttachDeploy,
                null, card, 0, attachment, 1, new PlacementOption(default, DeploymentKind.ExistingArmy, null),
                EquipmentSystem.EffectiveAbilities(card, definition));
            Assert.That(plan.ApCost, Is.EqualTo(produced ? 3 : 5));
            Assert.That(plan.ResCost.human, Is.EqualTo(1));
            Assert.That(plan.ResCost.tech, Is.EqualTo(produced ? 0 : 2));
            var consumed = new MaterializationConsumptionState();
            var token = consumed.Push(plan);
            Assert.That(consumed.CardsDisjoint(plan), Is.False);
            Assert.That(consumed.ExternalDisjoint(attachment, null, null), Is.False,
                "Standalone and chained play cannot both spend the same attachment");
            Assert.That(consumed.ApUsed, Is.EqualTo(plan.ApCost));
            Assert.That(consumed.TechUsed, Is.EqualTo(plan.ResCost.tech));
            consumed.Pop(token);
            Assert.That(consumed.CardsDisjoint(plan), Is.True);
            Assert.That(consumed.ApUsed, Is.Zero);
            Assert.That(consumed.HumanUsed, Is.Zero);
            Assert.That(consumed.TechUsed, Is.Zero);
        }

        [Test]
        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void GeneratedAttachmentPaysOneStakeAndClaimsOneGenerationSource(AttachmentSlot slot)
        {
            var definition = Attachment(slot); definition.apCost = 3; definition.activationApCost = 1;
            definition.resourceCost = new ResourceCost { tech = 2 };
            var host = Host(); host.apCost = 2; host.resourceCost = new ResourceCost { human = 1 };
            var card = new CardData(host);
            var source = new GenerationStep { CardDef = definition, ProducesEquipment = true, CardKey = "source:card" };
            var plan = MaterializationPlanFactory.MakeGeneratedPlan(MaterializationChainKind.GenerateAttachDeploy,
                null, source, card, 0, true, new PlacementOption(default, DeploymentKind.ExistingArmy, null),
                EquipmentSystem.EffectiveAbilities(card, definition));
            Assert.That(plan.ApCost, Is.EqualTo(6));
            Assert.That(plan.ResCost.tech, Is.EqualTo(2), "Stake is paid once; minted attachment has no resource charge");
            Assert.That(plan.ResCost.human, Is.EqualTo(1));
            var consumed = new MaterializationConsumptionState();
            var token = consumed.Push(plan);
            Assert.That(consumed.GenerationAttempts, Is.EqualTo(1));
            Assert.That(consumed.ExternalDisjoint(null, source.CardKey, null), Is.False);
            consumed.Pop(token);
            Assert.That(consumed.GenerationAttempts, Is.Zero);
            Assert.That(consumed.ExternalDisjoint(null, source.CardKey, null), Is.True);
            Assert.That(consumed.TechUsed, Is.Zero);
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

        [TestCase(EquipmentStat.MoveMax)]
        [TestCase(EquipmentStat.Fate)]
        public void RefillClearsConsumptionHiddenByAnAttachmentMaximumClamp(EquipmentStat stat)
        {
            var unit = Body();
            unit.MoveCurrent = 1; unit.Fate = 0; unit.HitPointsCurrent = 4;
            // The first attachment clamps the spent resource at a smaller maximum.
            EquipmentSystem.ApplyAttachments(unit, Attachment(AttachmentSlot.Equipment, stat, 2, replace: true), null);
            if (stat == EquipmentStat.MoveMax) unit.ReplenishMoveForNewTurn();
            else unit.ReplenishFateForNewBattle();
            // Spending after the refill must still survive a later increase.
            if (stat == EquipmentStat.MoveMax) unit.MoveCurrent--;
            else unit.Fate--;
            var mutator = Attachment(AttachmentSlot.Mutator, stat, 4);
            Assert.That(EquipmentSystem.CurrentAfterAttachment(unit, stat, 6), Is.EqualTo(5));
            EquipmentSystem.ApplyAttachments(unit, null, mutator);
            Assert.That(stat == EquipmentStat.MoveMax ? unit.MoveCurrent : unit.Fate, Is.EqualTo(5));
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(4), "Refill must not heal HP");
            Assert.That(stat == EquipmentStat.MoveMax ? unit.Fate : unit.MoveCurrent,
                Is.EqualTo(stat == EquipmentStat.MoveMax ? 0 : 1), "Other consumption must survive");
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
            // Speed alone (no route known): the move proxy on the hero reference line. Nothing for Fate -
            // the spent point is neither healed nor read as a gain.
            float eRef = EquipmentEfficiency.Base((int)System.Math.Round(AiConfigV2.equipHeroArmyAttackDefault),
                unit.Defense, unit.HitPointsMax, 2);
            float expected = AiConfigV2.equipCardValuePerE * AiConfigV2.equipMoveFactor * eRef * 2f
                * (3f / unit.MoveMax) / AiConfigV2.equipmentUpgradePersistence;
            Assert.That(delta.Tactical, Is.EqualTo(expected).Within(0.0001f));
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
