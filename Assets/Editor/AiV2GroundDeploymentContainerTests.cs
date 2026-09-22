#if UNITY_INCLUDE_TESTS
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
    public sealed class AiV2GroundDeploymentContainerTests
    {
        [Test]
        public void GroundCardPreflightRejectsAirfieldAndAirRosterWithoutSpending()
        {
            var owner = new PlayerSetupData();
            var hex = new HexCoord(83, -14);
            PlayerRoot root = PlayerRoot.Create(owner, "ground container parity");
            GameObject controllerObject = null;
            try
            {
                root.ActionPoints = 10;
                PlayerRootRegistry.Register(owner, root);
                var building = new BuildingData { Hex = hex, Owner = owner };
                building.Abilities.Add(UnitAbilities.Barracks);
                BuildingRegistry.Register(hex, building);

                var def = new CardDefinition
                {
                    cardType = CardType.Unit,
                    requiredBuildingAbility = UnitAbilities.Barracks,
                };
                var card = new CardData(def);
                var hand = new AiHandData(null, owner.Faction, 0);
                hand.AddCard(card);
                // The physical DeployUnitFromCard boundary requires a controller even for
                // existing recipients. Keep the positive test realistic while remaining
                // headless: an inactive component never needs scene dependencies or SpawnUnit.
                controllerObject = new GameObject("inactive ground deployment controller");
                controllerObject.SetActive(false);
                var ctx = new AiTurnContext
                {
                    HexSelection = controllerObject.AddComponent<HexSelectionController>(),
                };

                var airfield = new ArmyData { Hex = hex, Owner = owner, IsAirfield = true };
                CardPlayPlan airfieldPlan = CardPlayPlan.Into(card, hex, DeploymentKind.ReusableShell, airfield);
                Assert.That(CardPlayExecutor.Preflight(owner, root, hand, ctx, airfieldPlan,
                    out _), Is.False, "A ground card cannot be deployed into an airfield.");
                CardPlayResult rejected = CardPlayExecutor.Play(owner, root, hand, ctx, airfieldPlan);
                Assert.That(rejected.Deployed, Is.False);
                Assert.That(rejected.ApSpent, Is.Zero);
                Assert.That(root.ActionPoints, Is.EqualTo(10));
                Assert.That(hand.Hand, Does.Contain(card));

                var aviation = new ArmyData { Hex = hex, Owner = owner };
                aviation.Members.Add(new UnitData { IsAviation = true });
                CardPlayPlan aviationPlan = CardPlayPlan.Into(card, hex, DeploymentKind.ExistingArmy, aviation);
                Assert.That(CardPlayExecutor.Preflight(owner, root, hand, ctx, aviationPlan,
                    out _), Is.False, "A ground card cannot join an aviation roster.");
                Assert.That(root.ActionPoints, Is.EqualTo(10));

                var ground = new ArmyData { Hex = hex, Owner = owner };
                ground.Members.Add(new UnitData());
                CardPlayPlan valid = CardPlayPlan.Into(card, hex, DeploymentKind.ExistingArmy, ground);
                Assert.That(CardPlayExecutor.Preflight(owner, root, hand, ctx, valid,
                    out string reason), Is.True, reason);
                Assert.That(root.ActionPoints, Is.EqualTo(10));

                var unknown = new CardPlayPlan(card, hex, (DeploymentKind)999, ground);
                Assert.That(CardPlayExecutor.Preflight(owner, root, hand, ctx, unknown,
                    out string unknownReason), Is.False);
                Assert.That(unknownReason, Does.Contain("unknown deployment kind"));
                CardPlayResult unknownResult = CardPlayExecutor.Play(owner, root, hand, ctx, unknown);
                Assert.That(unknownResult.Deployed, Is.False);
                Assert.That(unknownResult.ApSpent, Is.Zero);
                Assert.That(root.ActionPoints, Is.EqualTo(10));
                Assert.That(hand.Hand, Does.Contain(card));
            }
            finally
            {
                BuildingRegistry.Clear();
                PlayerRootRegistry.Clear();
                if (controllerObject != null) Object.DestroyImmediate(controllerObject);
                Object.DestroyImmediate(root.gameObject);
            }
        }
    }
}
#endif
