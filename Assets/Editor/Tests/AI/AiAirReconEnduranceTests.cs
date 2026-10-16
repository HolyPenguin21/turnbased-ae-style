#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Playtest 2026-10-07 — Rusty Vulture (TurnsWithoutRefuel = 1) turned for home after two steps
    // of an AirSweep as if it were a plane.
    public class AiAirReconEnduranceTests
    {
        [Test]
        public void Plane_TurnsHomeWhenOnlyTheReturnReserveIsLeft()
        {
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, 0, 0), Is.True);
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, AiConfigV2.airReconTurningMpReserveSlack, 0), Is.True);
        }

        [Test]
        public void Helicopter_WithASafeUnlandedEnd_KeepsFlyingOutward()
        {
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, 0, 1), Is.False);
        }

        [Test]
        public void PlentyOfMovementOrALongerRoute_NeverTriggersTheReserve()
        {
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, AiConfigV2.airReconTurningMpReserveSlack + 1, 0), Is.False);
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(2, 0, 0), Is.False);
        }

        // Playtest 2026-10-16 — Kryll's Mosquito (5 MP, plane) turned after one step: one hex out
        // with 4 MP, a second step out still leaves 1 MP beyond the 2-hex way home.
        [Test]
        public void OddMovePlane_StepThatStillLeavesOneSpareMp_KeepsFlyingOutward()
        {
            Assert.That(AirReconStepDirector.TurnsForReturnReserve(1, 1, 0), Is.False);
        }

        // The reserve reads the way home the Return leg flies (the cheapest landing with room),
        // not a farther forward airfield; a landing without room for the wing does not count.
        [Test]
        public void CheapestLandingRouteCost_ReadsTheNearestLandingWithRoom()
        {
            var player = new PlayerSetupData();
            var mapObject = new GameObject("air-recon-return-reserve");
            try
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
                HexMap map = mapObject.AddComponent<HexMap>();
                var terrain = new TerrainTypeEntry { moveCost = 1 };
                var hexes = new Dictionary<HexCoord, TerrainTypeEntry>();
                for (int q = 0; q <= 6; q++) hexes[new HexCoord(q, 0)] = terrain;
                map.SetData(6, 1f, hexes);
                var home = new HexCoord(0, 0);
                var forward = new HexCoord(6, 0);
                var homeBase = new BuildingData { Owner = player, Hex = home, IsBase = true,
                    AirfieldCapacity = 1 };
                BuildingRegistry.Register(home, homeBase);
                BuildingRegistry.Register(forward, new BuildingData { Owner = player, Hex = forward,
                    IsBase = true, AirfieldCapacity = 1 });
                var air = new ArmyData { Owner = player, Hex = new HexCoord(1, 0), IsAirArmy = true };
                air.AddMemberSorted(new UnitData { Owner = player, IsAviation = true,
                    MoveMax = 5, MoveCurrent = 4, TurnsWithoutRefuel = 0 });
                ArmyRegistry.Register(air);

                Assert.That(AiAirSortiePlanner.CheapestLandingRouteCost(player, map, air, new HexCoord(2, 0)),
                    Is.EqualTo(2));

                var parked = new ArmyData { Owner = player, Hex = home, IsAirArmy = true };
                parked.AddMemberSorted(new UnitData { Owner = player, IsAviation = true });
                ArmyRegistry.Register(parked);
                Assert.That(AiAirSortiePlanner.CheapestLandingRouteCost(player, map, air, new HexCoord(2, 0)),
                    Is.EqualTo(4));
            }
            finally
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
                UnityEngine.Object.DestroyImmediate(mapObject);
            }
        }
    }
}
#endif
