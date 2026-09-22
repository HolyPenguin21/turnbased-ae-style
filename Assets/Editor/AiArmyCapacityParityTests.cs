#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiArmyCapacityParityTests
    {
        private static Type ProjectionRules => typeof(CardPlayExecutor).Assembly.GetType(
            "Game.Ai.V2.ArmyCapacityRules", throwOnError: true);

        private static int ProjectedCapacity(int nominal, bool hasHero, int addedHeroes, int firstRating)
        {
            MethodInfo method = ProjectionRules.GetMethod("ProjectedCapacity",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (int)method.Invoke(null, new object[] { nominal, hasHero, addedHeroes, firstRating });
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        public void FirstHeroProjectionMatchesLiveArmyEvenAtZeroCommand(int commandRating)
        {
            var hero = new UnitData { IsHero = true, CommandRating = commandRating };
            int live = ArmyData.ComputeCapacity(new[] { hero }, isGarrison: false);

            Assert.That(ProjectedCapacity(2, false, 1, commandRating), Is.EqualTo(live));
            Assert.That(ProjectedCapacity(4, false, 1, commandRating), Is.EqualTo(live),
                "A first hero replaces even the larger garrison default rather than inheriting it.");
        }

        [Test]
        public void ExistingCommanderRemainsAuthoritativeWhenMoreHeroesArrive()
        {
            var commander = new UnitData { IsHero = true, CommandRating = 3 };
            var reinforcement = new UnitData { IsHero = true, CommandRating = 0 };
            int live = ArmyData.ComputeCapacity(new[] { commander, reinforcement }, false);
            Assert.That(ProjectedCapacity(3, true, 1, 0), Is.EqualTo(live));
            Assert.That(ProjectedCapacity(2, false, 0, 0), Is.EqualTo(2),
                "Without an added hero the original field capacity must remain in force.");
        }

        [Test]
        public void MaterializationPortfolioRejectsAZeroCommandFirstHero()
        {
            Type stateType = typeof(MaterializationPlan).Assembly.GetType(
                "Game.Ai.V2.ProjectedPhysicalState", throwOnError: true);
            object state = Activator.CreateInstance(stateType, nonPublic: true);
            MethodInfo canAdd = stateType.GetMethod("CanAdd",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(canAdd, Is.Not.Null);

            var zero = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateDeploy,
                GeneratedBaseDef = new CardDefinition { cardType = CardType.Hero, commandRating = 0 },
                StableKey = "zero-command",
            };
            var one = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateDeploy,
                GeneratedBaseDef = new CardDefinition { cardType = CardType.Hero, commandRating = 1 },
                StableKey = "one-command",
            };

            Assert.That((bool)canAdd.Invoke(state, new object[] { zero }), Is.False,
                "Planner must reject the hero that the live ArmyActions deployment cannot fit.");
            Assert.That((bool)canAdd.Invoke(state, new object[] { one }), Is.True,
                "Valid one-body hero deployments must remain admissible.");
        }

        [Test]
        public void FreshArmyPreflightRejectsZeroCommandBeforeCreateArmyCanSpendAp()
        {
            var player = new PlayerSetupData();
            var hex = new HexCoord(82, -14);
            var root = PlayerRoot.Create(player, "capacity preflight test");
            try
            {
                root.ActionPoints = 10;
                var building = new BuildingData { Owner = player, Hex = hex };
                building.Abilities.Add(UnitAbilities.Barracks);
                BuildingRegistry.Register(hex, building);
                var definition = new CardDefinition
                {
                    cardType = CardType.Hero,
                    commandRating = 0,
                    requiredBuildingAbility = UnitAbilities.Barracks,
                };
                var card = new CardData(definition);
                var hand = new AiHandData(null, player.Faction, 0);
                hand.AddCard(card);
                var plan = CardPlayPlan.NewArmyAt(card, hex);

                Assert.That(CardPlayExecutor.Preflight(player, root, hand, new AiTurnContext(),
                    plan, out string failure), Is.False);
                Assert.That(failure, Does.Contain("first card would not fit"));
                Assert.That(root.ActionPoints, Is.EqualTo(10),
                    "A rejected first Hero must not spend the 2 AP for CreateArmy.");
                Assert.That(hand.Hand, Does.Contain(card));

                definition.commandRating = 1;
                Assert.That(CardPlayExecutor.Preflight(player, root, hand, new AiTurnContext(),
                    plan, out string validReason), Is.True, validReason);
                Assert.That(root.ActionPoints, Is.EqualTo(10));
            }
            finally
            {
                BuildingRegistry.Clear();
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }
    }
}
#endif
