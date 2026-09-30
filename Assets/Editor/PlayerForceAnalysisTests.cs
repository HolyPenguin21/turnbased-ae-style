#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class PlayerForceAnalysisTests
    {
        private static UnitData Body(int attack = 10) => new UnitData
        {
            Attack = attack, HitPointsCurrent = 5, HitPointsMax = 5
        };

        private static ArmyData Army(PlayerSetupData owner, params UnitData[] members)
        {
            var army = new ArmyData { Owner = owner, Name = "Test" };
            army.Members.AddRange(members);
            return army;
        }

        [Test]
        public void MilitaryPoolPreservesAttackOrderingAndFiltersNonGroundCards()
        {
            var body = new CardDefinition { cardType = CardType.Unit, attack = 10 };
            var hero = new CardDefinition { cardType = CardType.Hero, commandRating = 4 };
            var air = new CardDefinition { cardType = CardType.Unit, isAviation = true, attack = 100 };
            var gear = new CardDefinition { cardType = CardType.Equipment, attack = 500 };
            UnitData live = Body();
            var hand = new[] { new CardData(hero), new CardData(body), new CardData(gear) };
            var deck = new[] { hero, air, body };
            var expected = new[] { AiPower.ToPowerUnit(live), AiPower.ToPowerUnit(body),
                AiPower.ToPowerUnit(body), AiPower.ToPowerUnit(hero), AiPower.ToPowerUnit(hero) };
            var actual = AiPower.MilitaryPool(new[] { live }, hand, deck);
            Assert.That(actual.Select(p => p.IsHero), Is.EqualTo(expected.Select(p => p.IsHero)));
            Assert.That(actual.Select(p => p.BasePower), Is.EqualTo(expected.Select(p => p.BasePower)));
            Assert.That(AiPower.TotalMilitaryPotential(actual),
                Is.EqualTo(AiPower.TotalMilitaryPotential(expected)));
        }

        [Test]
        public void TotalIncludesAviationButAttackArmyExcludesGarrisonAirAndPrison()
        {
            var player = new PlayerSetupData();
            var field = Army(player, Body());
            var garrison = Army(player, Body(100)); garrison.IsGarrison = true;
            var air = Army(player, Body(100)); air.IsAirArmy = true; air.Members[0].IsAviation = true;
            var prison = Army(player, Body(1000)); prison.IsPrison = true;
            var force = PlayerForceAnalysis.Calculate(player, new[] { field, garrison, air, prison }, null, null);
            Assert.That(force.StrongestArmy, Is.SameAs(field));
            Assert.That(force.DeployedPower, Is.EqualTo(new[] { field, garrison, air }
                .SelectMany(a => a.Members).Sum(AiPower.UnitPower)));
            Assert.That(force.GroundArmyPotential, Is.EqualTo(AiPower.TotalMilitaryPotential(
                AiPower.MilitaryPool(field.Members.Concat(garrison.Members), null, null))));
        }

        [Test]
        public void DamageDeathsAndRewardsChangeDynamicPowerWithoutMutatingInputs()
        {
            var player = new PlayerSetupData { IsHuman = true };
            UnitData body = Body();
            ArmyData army = Army(player, body);
            var hand = new System.Collections.Generic.List<CardData>();
            var before = PlayerForceAnalysis.Calculate(player, new[] { army }, hand, null);
            body.HitPointsCurrent = 1;
            var damaged = PlayerForceAnalysis.Calculate(player, new[] { army }, hand, null);
            Assert.That(damaged.DeployedPower, Is.LessThan(before.DeployedPower));
            hand.Add(new CardData(new CardDefinition { cardType = CardType.Unit, attack = 100 }));
            var rewarded = PlayerForceAnalysis.Calculate(player, new[] { army }, hand, null);
            Assert.That(rewarded.GroundArmyPotential, Is.GreaterThan(damaged.GroundArmyPotential));
            Assert.That(hand.Count, Is.EqualTo(1));
            Assert.That(army.Members.Count, Is.EqualTo(1));
            army.Members.Clear();
            Assert.That(PlayerForceAnalysis.Calculate(player, new[] { army }, hand, null).DeployedPower, Is.Zero);
        }

        [Test]
        public void HumanAndAiUseIdenticalAnalysis()
        {
            var player = new PlayerSetupData();
            ArmyData army = Army(player, Body());
            var ai = PlayerForceAnalysis.Calculate(player, new[] { army }, null, null);
            player.IsHuman = true;
            var human = PlayerForceAnalysis.Calculate(player, new[] { army }, null, null);
            Assert.That(human.GroundArmyPotential, Is.EqualTo(ai.GroundArmyPotential));
            Assert.That(human.ReadinessPercent, Is.EqualTo(ai.ReadinessPercent));
        }

        [Test]
        public void EmptyForceNeverPassesAttackThreshold()
        {
            var force = PlayerForceAnalysis.Calculate(new PlayerSetupData(), null, null, null);
            Assert.That(force.ReadinessPercent, Is.Zero);
            Assert.That(force.ForceReady, Is.False);
            Assert.That(AttackObjectiveEvaluator.ForceReady(80f, 100f), Is.False);
            Assert.That(AttackObjectiveEvaluator.ForceReady(80.01f, 100f), Is.True);
            Assert.That(force.DeployedPercent, Is.Zero);
            Assert.That(force.MobilizationOpen, Is.False);
        }

        [Test]
        public void MobilizationOpensAtFourFifthsInclusiveWhileAssaultStaysStrict()
        {
            Assert.That(AttackObjectiveEvaluator.MobilizationOpen(144f, 180f), Is.True);
            Assert.That(AttackObjectiveEvaluator.MobilizationOpen(143.9f, 180f), Is.False);
            Assert.That(AttackObjectiveEvaluator.MobilizationOpen(0f, 0f), Is.False);
            Assert.That(AttackObjectiveEvaluator.ForceReady(56f, 70f), Is.False);
            Assert.That(AttackObjectiveEvaluator.ForceReady(56.1f, 70f), Is.True);
        }

        [Test]
        public void DeployedShareCountsMapAgainstMapPlusHandPlusDeck()
        {
            var player = new PlayerSetupData();
            var field = Army(player, Body());
            var unit = new CardDefinition { cardType = CardType.Unit, attack = 10 };
            var force = PlayerForceAnalysis.Calculate(player, new[] { field },
                new[] { new CardData(unit) }, new[] { unit });
            Assert.That(force.DeployedPercent,
                Is.EqualTo(100f * force.DeployedPower / force.TotalAvailablePower).Within(1e-4f));
            Assert.That(force.DeployedPower, Is.LessThan(force.TotalAvailablePower));
        }
    }
}
#endif
