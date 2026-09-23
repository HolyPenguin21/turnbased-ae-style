#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
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
        private static int ProjectedCapacity(int nominal, bool hasHero,
            int addedHeroes, int firstRating)
            => ArmyData.ComputeProjectedCapacity(
                nominal, hasHero, addedHeroes, firstRating);

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
            GameObject selectorObject = null;
            StartingDeckCatalog deckCatalog = null;
            FactionCardCatalog factionCatalog = null;
            try
            {
                ArmyRegistry.Clear();
                root.ActionPoints = 10;
                // The domain deployment transaction always charges the registered root.
                PlayerRootRegistry.Register(player, root);
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
                var ctx = new AiTurnContext();

                Assert.That(CardPlayExecutor.Preflight(player, root, hand, ctx,
                    plan, out string failure), Is.False);
                Assert.That(failure, Does.Contain("first card would not fit"));
                CardPlayResult rejected = CardPlayExecutor.Play(player, root, hand, ctx, plan);
                Assert.That(rejected.Deployed, Is.False);
                Assert.That(rejected.ArmyCreated, Is.False,
                    "No empty shell may be created after a known-invalid first-card preflight.");
                Assert.That(rejected.ApSpent, Is.EqualTo(0));
                Assert.That(root.ActionPoints, Is.EqualTo(10),
                    "A rejected first Hero must not spend the 2 AP for CreateArmy.");
                Assert.That(hand.Hand, Does.Contain(card));

                definition.commandRating = 1;
                Assert.That(CardPlayExecutor.Preflight(player, root, hand, ctx,
                    plan, out string noController), Is.False);
                Assert.That(noController, Does.Contain("deployment controller"));
                CardPlayResult missingController = CardPlayExecutor.Play(player, root, hand, ctx, plan);
                Assert.That(missingController.ArmyCreated, Is.False,
                    "CreateArmy must never charge AP before discovering a missing SpawnUnit controller.");
                Assert.That(missingController.ApSpent, Is.Zero);
                Assert.That(root.ActionPoints, Is.EqualTo(10));
                Assert.That(hand.Hand, Does.Contain(card));

                selectorObject = new GameObject("inactive deployment controller test stub");
                selectorObject.SetActive(false); // No scene wiring is needed for a pure preflight.
                ctx.HexSelection = selectorObject.AddComponent<HexSelectionController>();
                Assert.That(CardPlayExecutor.Preflight(player, root, hand, ctx,
                    plan, out string noCatalog), Is.False);
                Assert.That(noCatalog, Does.Contain("faction army catalog"));
                Assert.That(root.ActionPoints, Is.EqualTo(10));

                deckCatalog = ScriptableObject.CreateInstance<StartingDeckCatalog>();
                factionCatalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
                factionCatalog.faction = player.Faction;
                deckCatalog.catalogs.Add(factionCatalog);
                ctx.StartingDeckCatalog = deckCatalog;
                Assert.That(CardPlayExecutor.Preflight(player, root, hand, ctx,
                    plan, out string validReason), Is.True, validReason);
                Assert.That(root.ActionPoints, Is.EqualTo(10));

                // Both roots claim the SAME PlayerSetupData, but CreateArmy would charge the
                // registered one while DeployUnitFromCard and V2 bookkeeping use the argument.
                var staleRoot = PlayerRoot.Create(player, "stale same-owner root");
                try
                {
                    staleRoot.ActionPoints = 10;
                    Assert.That(CardPlayExecutor.Preflight(player, staleRoot, hand, ctx,
                        plan, out string staleReason), Is.False);
                    Assert.That(staleReason, Does.Contain("registered player root"));
                    CardPlayResult staleResult = CardPlayExecutor.Play(player, staleRoot, hand, ctx, plan);
                    Assert.That(staleResult.Deployed, Is.False);
                    Assert.That(staleResult.ArmyCreated, Is.False);
                    Assert.That(staleResult.ApSpent, Is.Zero);
                    Assert.That(root.ActionPoints, Is.EqualTo(10));
                    Assert.That(staleRoot.ActionPoints, Is.EqualTo(10));
                    Assert.That(hand.Hand, Does.Contain(card));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(staleRoot.gameObject);
                }
                Assert.That(CardPlayExecutor.Preflight(player, root, hand, ctx,
                    plan, out string stillValid), Is.True, stillValid);

                CardPlayResult success = CardPlayExecutor.Play(player, root, hand, ctx, plan);
                Assert.That(success.Deployed, Is.True, success.FailReason);
                Assert.That(success.ArmyCreated, Is.True);
                Assert.That(success.ArmyShell, Is.Not.Null);
                Assert.That(success.ArmyShell.Members.Count, Is.EqualTo(1),
                    "A fresh army must first become visible to the registry already populated.");
                Assert.That(ArmyRegistry.AllAt(hex).Count(a => a.Owner == player && !a.IsGarrison),
                    Is.EqualTo(1), "One logical deploy must publish exactly one field army.");
                Assert.That(root.ActionPoints, Is.EqualTo(8),
                    "A zero-card-AP first deploy pays exactly the 2 AP fresh-army cost once.");
                Assert.That(hand.Hand, Does.Not.Contain(card));
            }
            finally
            {
                ArmyRegistry.Clear();
                BuildingRegistry.Clear();
                PlayerRootRegistry.Clear();
                if (selectorObject != null) UnityEngine.Object.DestroyImmediate(selectorObject);
                if (deckCatalog != null) UnityEngine.Object.DestroyImmediate(deckCatalog);
                if (factionCatalog != null) UnityEngine.Object.DestroyImmediate(factionCatalog);
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }
    }
}
#endif
