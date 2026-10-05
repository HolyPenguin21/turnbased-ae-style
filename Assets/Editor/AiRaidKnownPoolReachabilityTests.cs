#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    // T06 — a Raid target is "proven unreachable" only when the WHOLE known pool (map, hand,
    // deck, every Research/Production output, every known equipment) cannot cover a defender.
    public class AiRaidKnownPoolReachabilityTests
    {
        private static WorthIt.DefenderProfile Unit(float attack, float defense = 2f) =>
            new WorthIt.DefenderProfile(defense, false, null, attack, 5f, 1);

        private static CardDefinition UnitCard(int attack) => new CardDefinition
        {
            cardType = CardType.Unit, attack = attack, defenseRating = 2, hitPoints = 5,
            displayName = "U" + attack,
        };

        private static CardDefinition AttackEquipment(int bonus) => new CardDefinition
        {
            cardType = CardType.Equipment, displayName = "E" + bonus,
            equipment = new EquipmentGrant
            {
                statChanges = new List<EquipmentStatChange>
                {
                    new EquipmentStatChange { stat = EquipmentStat.Attack, amount = bonus },
                },
            },
        };

        // Our only fighter hits for 4; the wall defends at 12 — nothing we know can scratch it.
        private static WorldSnapshot Snapshot(bool catalogKnown = true,
            CardDefinition[] deck = null, CardDefinition[] outputs = null) => new WorldSnapshot
        {
            Self = new SelfSnapshot
            {
                Armies = new[]
                {
                    new ArmySnapshot { ArmyId = 1, MemberCount = 1, Members = new[] { Unit(4f) } },
                },
                Hand = new CardData[0],
                Deck = deck ?? new CardDefinition[0],
            },
            Development = new DevelopmentReadiness
            {
                CatalogKnown = catalogKnown,
                CatalogOutputs = outputs ?? new CardDefinition[0],
            },
        };

        private static readonly IReadOnlyList<WorthIt.DefendingArmy> Wall = new[]
        {
            new WorthIt.DefendingArmy(new[] { Unit(3f, defense: 12f) }, default),
        };

        [Test]
        [TestCase(false, false, true)]
        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        public void HypotheticalMutatorRequiresBioAndAnUnoccupiedFrozenSlot(bool bio, bool occupied, bool unreachable)
        {
            var mutator = AttackEquipment(16);
            mutator.attachmentSlot = AttachmentSlot.Mutator;
            mutator.equipment.hostKinds.Add(EquipmentHostKind.Unit);
            var snap = Snapshot(deck: new[] { mutator });
            snap.Self.Armies = new[] { new ArmySnapshot
            {
                MemberCount = 1,
                Members = new[] { new WorthIt.DefenderProfile(2, false,
                    new[] { bio ? UnitTypeTag.Bio : UnitTypeTag.Mechanical }, 4, 5, 1) },
                NonHeroMutatorOccupied = new[] { occupied },
            } };
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(snap, Wall, 0, out _),
                Is.EqualTo(unreachable));
        }

        [Test]
        public void HandMutatorCannotBeStackedWithAnotherKnownMutator()
        {
            var host = UnitCard(4); host.unitTypeTags.Add(UnitTypeTag.Bio);
            var existing = AttackEquipment(1); existing.attachmentSlot = AttachmentSlot.Mutator;
            var candidate = AttackEquipment(16); candidate.attachmentSlot = AttachmentSlot.Mutator;
            candidate.equipment.hostKinds.Add(EquipmentHostKind.Unit);
            var snap = Snapshot(deck: new[] { candidate });
            snap.Self.Armies = new ArmySnapshot[0];
            snap.Self.PoolCards = new[] { (host, (CardDefinition)null, existing, true),
                (candidate, (CardDefinition)null, (CardDefinition)null, false) };
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(snap, Wall, 0, out _), Is.True);
        }

        [Test]
        public void WholePoolCannotDamageDefender_IsProvenUnreachable()
        {
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                Snapshot(), Wall, 0f, out string reason), Is.True);
            Assert.That(reason, Does.Contain("no_known_pool_unit_damages_defender"));
        }

        [Test]
        public void UndrawnStrongCard_OrKnownOutput_OrEquipment_KeepsTargetOpen()
        {
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                Snapshot(deck: new[] { UnitCard(20) }), Wall, 0f, out _), Is.False, "deck card");
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                Snapshot(outputs: new[] { UnitCard(20) }), Wall, 0f, out _), Is.False, "catalog output");
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                Snapshot(deck: new[] { AttackEquipment(16) }), Wall, 0f, out _), Is.False, "equipment");
        }

        // The live hand may already have lost a card whose unit the frozen Armies do not show yet:
        // the proof reads the frozen PoolCards, never the live lists.
        [Test]
        public void FrozenPoolCards_WinOverLiveHand()
        {
            WorldSnapshot snap = Snapshot();
            snap.Self.PoolCards = new[] { (UnitCard(20), (CardDefinition)null, (CardDefinition)null, true) };
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                snap, Wall, 0f, out _), Is.False);
        }

        [Test]
        public void NoGenerationBound_ProvesNothing()
        {
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                Snapshot(catalogKnown: false), Wall, 0f, out _), Is.False);
        }

        [Test]
        public void SiteDefenceIsPartOfTheProof_WeakerSiteReopens()
        {
            var soft = new[] { new WorthIt.DefendingArmy(new[] { Unit(3f, defense: 2f) }, default) };
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                Snapshot(), soft, 0f, out _), Is.False);
            Assert.That(CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(
                Snapshot(), soft, 10f, out _), Is.True);
        }
    }
}
#endif
