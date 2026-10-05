#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Players;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using static Game.EditorTests.AttachmentContentTestData;

namespace Game.EditorTests
{
    // Read the committed content, rather than recreating synthetic Mutator definitions.
    // The small YAML reader follows the same external-test precedent as AiGarrisonHeroTests.
    public sealed class MutatorContentTests
    {
        private static CardDefinition Mutator(string slug) => Blocks(NeutralPath).Select(Read)
            .Single(c => c.authoredKey == "neutral.mutator." + slug);
        private static IEnumerable<CardDefinition> Mutators() => Blocks(NeutralPath).Select(Read)
            .Where(c => c.authoredKey.StartsWith("neutral.mutator."));

        [TestCase("dermal-plating", "Defense:1", "")]
        [TestCase("reactive-marrow", "HitPoints:2", "")]
        [TestCase("reinforced-skeleton", "Defense:1,HitPoints:1,Initiative:-1", "")]
        [TestCase("pain-suppression", "HitPoints:2,MoveMax:-1", "")]
        [TestCase("regenerative-culture", "", UnitAbilities.Regeneration)]
        [TestCase("hyper-regeneration", "HitPoints:1,MoveMax:-1", UnitAbilities.Regeneration)]
        [TestCase("survivor-strain", "Defense:1,MoveMax:-1", UnitAbilities.Regeneration)]
        [TestCase("adrenal-surge", "MoveMax:1,Initiative:1,Defense:-1", "")]
        [TestCase("metabolic-overdrive", "MoveMax:1,Defense:-1", "")]
        [TestCase("predator-reflexes", "Initiative:1,Defense:1,MoveMax:-1", "")]
        [TestCase("neural-accelerator", "Initiative:1,ActivationApCost:-1", "")]
        [TestCase("rapid-synapse", "Defense:-1", UnitAbilities.RapidReaction)]
        [TestCase("hunter-glands", "Defense:-1", UnitAbilities.R1S4)]
        [TestCase("enhanced-senses", "Initiative:1,MoveMax:-1", UnitAbilities.R1S4)]
        [TestCase("wanderer-strain", "MoveMax:1,Defense:-1", UnitAbilities.R1S4)]
        [TestCase("chameleon-tissue", "MoveMax:-1", UnitAbilities.Stealth4)]
        [TestCase("fortunate-genome", "Fate:1", "")]
        [TestCase("ghost-genome", "", UnitAbilities.Stealth4)]
        [TestCase("hunter-genome", "", UnitAbilities.R1S4)]
        [TestCase("reflex-genome", "", UnitAbilities.RapidReaction)]
        public void AuthoredEffectsMatchDesignAndApplyToRealHost(string slug, string stats, string skill)
        {
            CardDefinition card = Mutator(slug);
            Assert.That(string.Join(",", card.equipment.statChanges.Select(c => c.stat + ":" + c.amount)), Is.EqualTo(stats));
            Assert.That(card.equipment.addAbilities, Is.EquivalentTo(skill.Length == 0 ? new string[0] : new[] { skill }));
            bool hero = card.equipment.hostKinds.Contains(EquipmentHostKind.Hero);
            var host = AttachmentSlotTests.Host(hero);
            host.grantedAbilities.Clear();
            var body = AttachmentSlotTests.Body(host);
            var predicted = EquipmentSystem.Project(new CardData(host) { Mutator = card });
            EquipmentSystem.ApplyAttachments(body, null, card);
            Assert.That(body.Mutator, Is.SameAs(card));
            Assert.That(body.Equipment, Is.Null);
            Assert.That(body.Abilities, Is.EquivalentTo(predicted.Abilities));
            Assert.That(body.Defense, Is.EqualTo(predicted.Stats[EquipmentStat.Defense]));
            Assert.That(body.HitPointsMax, Is.EqualTo(predicted.Stats[EquipmentStat.HitPoints]));
            Assert.That(body.MoveMax, Is.EqualTo(predicted.Stats[EquipmentStat.MoveMax]));
            Assert.That(body.Initiative, Is.EqualTo(predicted.Stats[EquipmentStat.Initiative]));
            Assert.That(body.ActivationApCost, Is.EqualTo(predicted.Stats[EquipmentStat.ActivationApCost]));
            Assert.That(body.FateMax, Is.EqualTo(predicted.Stats[EquipmentStat.Fate]));
            Assert.That(body.Attack, Is.EqualTo(host.attack));
            Assert.That(body.Range, Is.EqualTo(host.range));
            Assert.That(body.CommandRating, Is.EqualTo(host.commandRating));
        }

        [Test]
        public void AllTwentyEnforceBioHostKindLimitsAndEconomy()
        {
            var cards = Mutators().ToList();
            Assert.That(cards.Count, Is.EqualTo(20));
            Assert.That(cards.Count(c => c.equipment.hostKinds.SequenceEqual(new[] { EquipmentHostKind.Hero })), Is.EqualTo(4));
            foreach (var c in cards)
            {
                bool hero = c.equipment.hostKinds.Single() == EquipmentHostKind.Hero;
                Assert.That(c.cardType, Is.EqualTo(CardType.Equipment));
                Assert.That(c.attachmentSlot, Is.EqualTo(AttachmentSlot.Mutator));
                Assert.That(c.faction, Is.EqualTo(Faction.None));
                Assert.That(c.equipment.hostTypeTags, Is.EqualTo(new[] { UnitTypeTag.Bio }));
                Assert.That(EquipmentSystem.FitsHost(c, AttachmentSlotTests.Host(hero), out _), Is.True, c.displayName);
                Assert.That(EquipmentSystem.FitsHost(c, AttachmentSlotTests.Host(!hero), out _), Is.False, c.displayName);
                Assert.That(EquipmentSystem.FitsHost(c, AttachmentSlotTests.Host(hero, false), out _), Is.False, c.displayName);
                Assert.That(c.equipment.clearAbilityFamilies, Is.Empty);
                Assert.That(c.equipment.removeAbilities, Is.Empty);
                foreach (var change in c.equipment.statChanges)
                {
                    Assert.That(change.isOverride, Is.False);
                    Assert.That(change.stat == EquipmentStat.Attack || change.stat == EquipmentStat.Range
                        || change.stat == EquipmentStat.CommandRating || change.stat == EquipmentStat.Resistance,
                        Is.False, change.stat.ToString());
                    Assert.That(change.amount, Is.InRange(-1, change.stat == EquipmentStat.HitPoints && !hero ? 2 : 1));
                    if (hero) Assert.That(change.stat, Is.EqualTo(EquipmentStat.Fate));
                }
                Assert.That(c.apCost, Is.EqualTo(1));
                Assert.That(c.activationApCost, Is.EqualTo(1));
                Assert.That(ResearchProductionSystem.RequiredSuccesses(c), Is.InRange(3, 5));
                CardData minted = ResearchProductionSystem.MintCard(c);
                Assert.That(minted.EffectivePlayApCost, Is.EqualTo(1));
                Assert.That(minted.EffectivePlayResourceCost, Is.Null, "Creation already paid the stake");
                Assert.That(new CardData(c).EffectivePlayResourceCost, Is.SameAs(c.resourceCost));
            }
        }

        [TestCase("hunter-glands")]
        [TestCase("enhanced-senses")]
        [TestCase("wanderer-strain")]
        [TestCase("hunter-genome")]
        public void RecceIsExactlyR1S4AndDoesNotRemoveCompatibleStealth(string slug)
        {
            var c = Mutator(slug);
            Assert.That(AbilityParams.TryGetRecce(c.equipment.addAbilities.Single(), out int radius, out int strength), Is.True);
            Assert.That(radius, Is.EqualTo(1)); Assert.That(strength, Is.EqualTo(4));
            var abilities = EquipmentSystem.EffectiveAbilities(new[] { UnitAbilities.Stealth4 }, c.equipment);
            Assert.That(abilities, Is.EquivalentTo(new[] { UnitAbilities.Stealth4, UnitAbilities.R1S4 }));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void NeuralAcceleratorRespectsZeroActivationFloor(int initial)
        {
            var host = AttachmentSlotTests.Host(); host.activationApCost = initial;
            var unit = AttachmentSlotTests.Body(host);
            EquipmentSystem.ApplyAttachments(unit, null, Mutator("neural-accelerator"));
            Assert.That(unit.ActivationApCost, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RealProductionEquipmentAndMutatorCoexistInEitherOrder(bool reverse)
        {
            CardDefinition equipment = Blocks(NeutralPath).Select(Read).Single(c => c.authoredKey == "neutral.equipment.bio.heavy-mg");
            CardDefinition mutator = Mutator("reactive-marrow");
            var host = AttachmentSlotTests.Host(); host.attack = 2; host.unitTypeTags.Add(UnitTypeTag.Infantry);
            var body = AttachmentSlotTests.Body(host);
            if (reverse)
            {
                EquipmentSystem.ApplyAttachments(body, null, mutator);
                EquipmentSystem.ApplyAttachments(body, equipment, null);
            }
            else EquipmentSystem.ApplyAttachments(body, equipment, mutator);
            Assert.That(body.Equipment, Is.SameAs(equipment)); Assert.That(body.Mutator, Is.SameAs(mutator));
            Assert.That(body.HitPointsMax, Is.EqualTo(host.hitPoints + 2));
            Assert.That(body.Attack, Is.EqualTo(5));
            Assert.That(EquipmentSystem.Project(new CardData(host) { Equipment = equipment, Mutator = mutator })
                .Stats[EquipmentStat.HitPoints], Is.EqualTo(body.HitPointsMax));
        }

        [Test]
        public void FormatterShowsHostKindHiddenStatsAndCompleteResearchDescription()
        {
            var neural = Mutator("neural-accelerator");
            string face = EquipmentCardText.CardFace(neural, null);
            Assert.That(face, Does.StartWith("Bio, Unit\n").And.Contain("Initiative 1").And.Contain("Activation AP -1"));
            Assert.That(EquipmentCardText.AttachedCardFace(neural, null), Does.Contain("Activation AP -1"));
            Assert.That(EquipmentCardText.CardFace(Mutator("fortunate-genome"), null), Does.Contain("Hero"));
            string description = EquipmentCardText.Description(Mutator("reinforced-skeleton"), null);
            Assert.That(description, Does.Contain("Defense 1").And.Contain("HP 1").And.Contain("Initiative -1"));
            Assert.That(description, Does.Not.Contain("+").And.Not.Contain("=").And.Not.Contain("—"));
        }

        [Test]
        public void ResearchHasOnlyTwentySharedMutatorsAndProductionKeepsItsSevenEquipment()
        {
            string text = Text("Assets/Cards/ResearchProductionCatalog.asset");
            string research = text.Split(new[] { "  researchCards:" }, StringSplitOptions.None)[1]
                .Split(new[] { "  productionCards:" }, StringSplitOptions.None)[0];
            string[] keys = Regex.Matches(research, @"cardKey: (\S+)").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.That(keys, Is.EquivalentTo(Mutators().Select(c => c.authoredKey)));
            Assert.That(Regex.Matches(research, @"factionRestriction: 2").Count, Is.EqualTo(20));
            string production = text.Split(new[] { "  productionCards:" }, StringSplitOptions.None)[1];
            Assert.That(Regex.Matches(production, @"cardKey: neutral.equipment\.").Count, Is.EqualTo(7));
            Assert.That(production, Does.Not.Contain("mutator"));
        }

        [Test]
        public void AuthoredOrganicHeroesAcceptGenomeAndMechanicalHeroesRejectIt()
        {
            int organic = 0, mechanical = 0;
            foreach (string faction in new[] { "IronConcord", "TheAshen", "TheVessels" })
                foreach (string block in Blocks("Assets/Cards/" + faction + "/CardCatalog_" + faction + ".asset"))
                {
                    if (Number(block, "cardType") != (int)CardType.Hero) continue;
                    var host = Read(block);
                    host.unitTypeTags = Packed<UnitTypeTag>(block, "unitTypeTags");
                    bool bio = faction != "TheVessels";
                    if (bio) organic++; else mechanical++;
                    Assert.That(host.unitTypeTags.Contains(UnitTypeTag.Bio), Is.EqualTo(bio), host.displayName);
                    foreach (var card in Mutators().Where(c => c.equipment.hostKinds.Contains(EquipmentHostKind.Hero)))
                        Assert.That(EquipmentSystem.FitsHost(card, host, out _), Is.EqualTo(bio), host.displayName);
                }
            Assert.That(organic, Is.EqualTo(16)); Assert.That(mechanical, Is.EqualTo(8));
        }

        [Test]
        public void UnityImportsActualDefinitionsAndResolvesAllPlayableFactions()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset");
            Assert.That(catalog, Is.Not.Null);
            Assert.That(EquipmentCardText.CardFace(Mutator("ghost-genome"), null),
                Does.Contain("Hero").And.Contain("Stealth4"));
            foreach (var faction in new[] { Faction.IronConcord, Faction.Ashen, Faction.Vessels })
            {
                var cards = catalog.ResolveFor(ResearchProductionMode.Research, faction);
                Assert.That(cards.Count, Is.EqualTo(20));
                foreach (var card in cards)
                {
                    Assert.That(card.attachmentSlot, Is.EqualTo(AttachmentSlot.Mutator));
                    Assert.That(card.art, Is.Not.Null); Assert.That(card.detailArt, Is.Not.Null);
                }
            }
        }
    }
}
#endif
