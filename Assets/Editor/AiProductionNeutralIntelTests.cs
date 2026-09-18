#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiProductionNeutralIntelTests
    {
        [Test]
        public void HiddenNeutralRosterChangesCannotRetroactivelyUpdateProductionValuation()
        {
            var host = new CardData(new CardDefinition
            {
                cardType = CardType.Unit, attack = 1, defenseRating = 2, hitPoints = 8,
            });
            var weapon = new EquipmentGrant();
            weapon.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Attack, amount = 20,
            });
            var opportunity = new DevelopmentOpportunity
            {
                Card = new CardDefinition { cardType = CardType.Equipment, equipment = weapon },
                RecipientKind = DevRecipientKind.HandCard,
                RecipientCard = host,
            };
            var observedDefenders = new[]
            {
                new WorthIt.DefenderProfile(defense: 14, hasCeramicArmor: false,
                    attack: 5, hitPoints: 8),
            };
            var neutral = new ArmySnapshot
            {
                ArmyId = 77,
                Hex = new HexCoord(1, 1),
                Members = observedDefenders,
            };
            var snapshot = new WorldSnapshot
            {
                Known = new KnownSnapshot
                {
                    NeutralSightings = new[]
                    {
                        new AiMapMemory.KnownEnemySighting(new HexCoord(1, 1), null,
                            "observed neutral", 1, 14f, 5f, observedDefenders, armyId: 77),
                    },
                },
                TrueWorld = new TrueWorldSnapshot
                {
                    NeutralArmies = new[] { neutral },
                },
            };

            float observedFit = DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snapshot);
            Assert.That(observedFit, Is.EqualTo(1f),
                "An observed defender is a legitimate composition witness");

            neutral.Hex = new HexCoord(20, -20);
            neutral.Members = new[]
            {
                new WorthIt.DefenderProfile(defense: 100, hasCeramicArmor: false,
                    attack: 100, hitPoints: 80),
            };
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snapshot), Is.EqualTo(observedFit).Within(0.0001f),
                "Unobserved changes to the live neutral roster must not affect valuation");

            snapshot.TrueWorld.NeutralArmies = System.Array.Empty<ArmySnapshot>();
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snapshot), Is.EqualTo(observedFit).Within(0.0001f),
                "Last observed neutral profiles remain legitimate even when the live roster disappears");

            snapshot.Known.NeutralSightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>();
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snapshot), Is.Zero,
                "Without a known witness, an unseen neutral must provide no matchup information");
        }
    }
}
#endif
