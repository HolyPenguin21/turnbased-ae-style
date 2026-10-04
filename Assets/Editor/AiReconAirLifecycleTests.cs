#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Aviation;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Air-recon lifecycle facts that must reach Continuity honestly.
    public class AiReconAirLifecycleTests
    {
        [Test]
        public void OwnedAirfieldDuringOutbound_DoesNotCompleteSortie()
        {
            var wing = new ReconAirSortieState { Phase = ReconAirPhase.Outbound };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, atAirfield: true,
                hasDeparted: true), Is.False, "an intermediate airfield does not end an outbound sortie");
        }

        [Test]
        public void ReturnPhaseAtOwnedAirfield_CompletesSortie()
        {
            var wing = new ReconAirSortieState { Phase = ReconAirPhase.Return };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, true), Is.True);
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, hasDeparted: false),
                Is.False, "a wing that never left its airfield has not flown a sortie");
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, atAirfield: false, true),
                Is.False);
        }

        [TestCase(0, 10, 5)]
        [TestCase(1, 10, 10)]
        [TestCase(2, 5, 5)]
        public void FirstTurnOutboundBudget_IsDerivedFromEndurance(
            int turnsWithoutRefuel, int movement, int expected)
        {
            var unit = new UnitData
            {
                IsAviation = true,
                TurnsWithoutRefuel = turnsWithoutRefuel,
                MoveMax = movement,
                MoveCurrent = movement,
            };
            Assert.That(AviationRange.FirstTurnOutboundBudget(
                new List<UnitData> { unit }), Is.EqualTo(expected));
        }

        [TestCase(0, 0, 0)]
        [TestCase(1, 0, 1)]
        [TestCase(2, 0, 2)]
        [TestCase(2, 1, 1)]
        [TestCase(2, 2, 0)]
        public void RemainingEndurance_IsDerivedOnlyFromTurnsWithoutRefuel(
            int turnsWithoutRefuel, int unlandedEnds, int expected)
        {
            var unit = new UnitData
            {
                IsAviation = true,
                TurnsWithoutRefuel = turnsWithoutRefuel,
                ConsecutiveUnlandedEnds = unlandedEnds,
            };
            Assert.That(AviationRange.SafeUnlandedEndsRemaining(
                new List<UnitData> { unit }), Is.EqualTo(expected));
        }

        [TestCase(0, 4, false, 0)]
        [TestCase(1, 4, true, 2)]
        [TestCase(2, 6, true, 3)]
        public void SortieRoute_UsesLiveEnduranceAndProvesLanding(
            int endurance, int targetDistance, bool multiTurnExpected, int expectedTurns)
        {
            var owner = new PlayerSetupData();
            GameObject mapObject = new GameObject("aviation-endurance-route-map");
            try
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
                HexMap map = mapObject.AddComponent<HexMap>();
                var terrain = new TerrainTypeEntry { moveCost = 1 };
                var hexes = new Dictionary<HexCoord, TerrainTypeEntry>();
                for (int q = 0; q <= 6; q++)
                    hexes[new HexCoord(q, 0)] = terrain;
                map.SetData(6, 1f, hexes);
                HexCoord home = new HexCoord(0, 0);
                BuildingRegistry.Register(home, new BuildingData
                {
                    Owner = owner, Hex = home, IsBase = true,
                    IsStartingCitadel = true, AirfieldCapacity = 2,
                });
                var wing = new ArmyData { Owner = owner, Hex = home, IsAirArmy = true };
                wing.AddMemberSorted(new UnitData
                {
                    Owner = owner, IsAviation = true, MoveMax = 4, MoveCurrent = 4,
                    TurnsWithoutRefuel = endurance,
                });
                ArmyRegistry.Register(wing);

                HexCoord target = new HexCoord(targetDistance, 0);
                Assert.That(AiAirSortiePlanner.TryPlanSortie(wing, target, map, owner), Is.Null);
                MultiTurnSortie? route = AiAirSortiePlanner.TryPlanMultiTurnSortie(
                    wing, target, map, owner);
                Assert.That(route.HasValue, Is.EqualTo(multiTurnExpected));
                if (route.HasValue)
                {
                    Assert.That(route.Value.RequiredTurns, Is.EqualTo(expectedTurns));
                    Assert.That(route.Value.RequiredUnlandedEnds, Is.EqualTo(endurance));
                    Assert.That(route.Value.LandingHex, Is.EqualTo(home));
                }
                if (endurance == 2)
                {
                    wing.Hex = new HexCoord(4, 0);
                    wing.Members[0].MoveCurrent = 0;
                    Assert.That(AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, map, owner), Is.True);
                    wing.Members[0].ConsecutiveUnlandedEnds = 1;
                    Assert.That(AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, map, owner), Is.True);
                    wing.Members[0].ConsecutiveUnlandedEnds = 2;
                    Assert.That(AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, map, owner), Is.False,
                        "a third airborne end would trigger fuel damage");
                }
            }
            finally
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
                Object.DestroyImmediate(mapObject);
            }
        }

        [Test]
        public void StationaryStrike_PaysOnlyAnUnpaidLaunchAndNeedsNoMovement()
        {
            var owner = new PlayerSetupData();
            GameObject rootObject = new GameObject("stationary-air-activation-root");
            GameObject presenterObject = new GameObject("stationary-air-strike-presenter");
            try
            {
                PlayerRootRegistry.Clear();
                ArmyRegistry.Clear();
                PlayerRoot root = rootObject.AddComponent<PlayerRoot>();
                PlayerRootRegistry.Register(owner, root);
                var wing = new ArmyData { Owner = owner, IsAirArmy = true };
                wing.AddMemberSorted(new UnitData
                {
                    Owner = owner, IsAviation = true, MoveMax = 4, MoveCurrent = 0,
                    ActivationApCost = 2, LaunchEnergyCost = 3,
                });

                root.ActionPoints = 2;
                Assert.That(AviationActions.CanActivateForStationaryStrike(wing), Is.False);
                root.AddResource(ResourceType.Energy, 3);
                Assert.That(AviationActions.CanActivateForStationaryStrike(wing), Is.True,
                    "a strike spends no MP but an unpaid launch still needs AP and Energy");
                var presenter = presenterObject.AddComponent<AviationCombatPresenter>();
                Assert.That(AviationActions.ResolveStationaryStrike(presenter, wing).MoveNext(), Is.False,
                    "an empty hex cannot consume activation resources");
                Assert.That(root.ActionPoints, Is.EqualTo(2));
                Assert.That(root.GetResource(ResourceType.Energy), Is.EqualTo(3));
                root.ActionPoints = 0;
                Assert.That(AviationActions.CanActivateForStationaryStrike(wing), Is.False);
                wing.MarkActivated();
                Assert.That(AviationActions.CanActivateForStationaryStrike(wing), Is.False,
                    "a turn activation is not a paid sortie launch");
                wing.Members[0].SortieLaunchPaid = true;
                Assert.That(AviationActions.CanActivateForStationaryStrike(wing), Is.True,
                    "a wing on a paid sortie strikes without paying twice");
            }
            finally
            {
                PlayerRootRegistry.Clear();
                ArmyRegistry.Clear();
                Object.DestroyImmediate(presenterObject);
                Object.DestroyImmediate(rootObject);
            }
        }

    }
}
#endif
