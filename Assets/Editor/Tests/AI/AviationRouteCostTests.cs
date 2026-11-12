#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Aviation forecasts over real transitions (spec 5): the calendar pays whole transitions,
    // ends turns where the next one does not fit, never treats a hex count as MP and never
    // refuels at an intermediate airfield inside one sortie proof.
    public sealed class AviationRouteCostTests
    {
        private PlayerSetupData _owner;

        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear();
            _owner = new PlayerSetupData();
        }

        [TearDown] public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear();
        }

        private UnitData Heli(int move, int endurance) => new UnitData
        {
            Owner = _owner, IsAviation = true, MoveMax = move, MoveCurrent = move, TurnsWithoutRefuel = endurance,
            HitPointsMax = 4, HitPointsCurrent = 4, Attack = 2,
        };

        private static List<HexCoord> Line(int length) =>
            Enumerable.Range(0, length + 1).Select(q => new HexCoord(q, 0)).ToList();

        [Test]
        public void ThreeTwoMpTransitions_AtThreeMp_TakeThreeTurns_NotTwo()
        {
            var turnEnds = new List<int>();
            Assert.That(AviationRange.TrySimulateTransitions(Line(3), new[] { 2, 2, 2 }, 3, 3, new[] { Heli(3, 2) }, 2,
                _owner, out int turns, out int ends, out HexCoord first, out bool reaches, out bool lands, turnEnds), Is.True);
            Assert.That(turns, Is.EqualTo(3), "ceil(6 / 3) = 2 would split transitions");
            Assert.That(turnEnds, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(ends, Is.EqualTo(2));
            Assert.That(first, Is.EqualTo(new HexCoord(1, 0)));
            Assert.That(reaches, Is.False);
            Assert.That(lands, Is.False);
            Assert.That(AviationRange.TurnsToFly(new[] { 2, 2, 2 }, 3, 3), Is.EqualTo(3));
        }

        [Test]
        public void TransitionAboveAFullTurn_IsImpossible_WithoutLooping()
        {
            Assert.That(AviationRange.TrySimulateTransitions(Line(2), new[] { 1, 3 }, 2, 2, new[] { Heli(2, 5) }, 5,
                _owner, out _, out _, out _, out _, out _), Is.False);
            Assert.That(AviationRange.TurnsToFly(new[] { 1, 3 }, 2, 2), Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void NoMpThisTurn_WaitsOnTheStartHexAsARealTurnEnd()
        {
            var turnEnds = new List<int>();
            Assert.That(AviationRange.TrySimulateTransitions(Line(2), new[] { 1, 1 }, 2, 0, new[] { Heli(2, 1) }, 1,
                _owner, out int turns, out int ends, out HexCoord first, out _, out _, turnEnds), Is.True);
            Assert.That(turnEnds, Is.EqualTo(new[] { 0, 2 }));
            Assert.That(turns, Is.EqualTo(2));
            Assert.That(ends, Is.EqualTo(1), "the wait consumes fuel");
            Assert.That(first, Is.EqualTo(new HexCoord(0, 0)));
            Assert.That(AviationRange.TrySimulateTransitions(Line(2), new[] { 1, 1 }, 2, 0, new[] { Heli(2, 0) }, 0,
                _owner, out _, out _, out _, out _, out _), Is.False, "no fuel for the wait");
        }

        [Test]
        public void ForecastIsReadOnly()
        {
            UnitData heli = Heli(3, 2);
            AviationRange.TrySimulateTransitions(Line(3), new[] { 2, 2, 2 }, 3, 3, new[] { heli }, 2,
                _owner, out _, out _, out _, out _, out _);
            Assert.That(heli.MoveCurrent, Is.EqualTo(3));
            Assert.That(heli.ConsecutiveUnlandedEnds, Is.Zero);
        }

        [Test]
        public void StrikeSeries_UsesTransitionCosts()
        {
            // Out 2 transitions, home 2; with a storm exit on the way home it costs 3.
            Assert.That(AviationRange.StrikeTurns(4, 4, 0, new[] { 1, 1 }, new[] { 1, 1 }), Is.EqualTo(1));
            Assert.That(AviationRange.StrikeTurns(4, 4, 0, new[] { 1, 1 }, new[] { 2, 1 }), Is.Zero,
                "the same hex counts no longer fit one turn");
            Assert.That(AviationRange.StrikeTurns(4, 4, 1, new[] { 1, 1 }, new[] { 2, 1 }), Is.EqualTo(2),
                "one safe end: hold a turn, then the full budget pays the dearer return");
        }

        // ---- on a real map (Unity only: HexMap is a MonoBehaviour) ----------------------------

        private static HexMap Map(int length, out GameObject mapObject)
        {
            mapObject = new GameObject("aviation-route-map");
            HexMap map = mapObject.AddComponent<HexMap>();
            var terrain = new Game.Terrain.TerrainTypeEntry { moveCost = 3 };
            var hexes = new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry>();
            for (int q = 0; q <= length; q++) hexes[new HexCoord(q, 0)] = terrain;
            map.SetData(length, 1f, hexes);
            return map;
        }

        private void Airfield(HexCoord hex, int capacity)
        {
            BuildingRegistry.Register(hex, new BuildingData
            {
                Owner = _owner, Hex = hex, IsBase = true, IsStartingCitadel = hex.Equals(default), AirfieldCapacity = capacity,
            });
            ArmyRegistry.Register(new ArmyData { Owner = _owner, Hex = hex, IsAirfield = true, Name = "Airfield" });
        }

        [Test, Category("UnityEngine")]
        public void ReturnThatLeavesAStorm_NeedsTheExtraMp_StorageAndWingAgree()
        {
            HexMap map = Map(3, out GameObject mapObject);
            try
            {
                Airfield(default, 2);
                var target = new HexCoord(1, 0);
                map.PublishWeather(MovementEnvironmentTests.Storms(new[] { target }));
                UnitData plane = Heli(2, 0);
                // Out 1 MP (expensive ground terrain is irrelevant to aircraft), home 2 MP.
                Assert.That(AiAirSortiePlanner.TryPlanSortieFromStorage(default, new[] { plane }, target, map, _owner),
                    Is.Null, "2 MP cannot pay 1 out + 2 back");
                plane.MoveMax = plane.MoveCurrent = 3;
                Sortie? stored = AiAirSortiePlanner.TryPlanSortieFromStorage(default, new[] { plane }, target, map, _owner);
                Assert.That(stored.HasValue, Is.True);
                Assert.That(stored.Value.TotalCost, Is.EqualTo(3));

                var wing = new ArmyData { Owner = _owner, Hex = default, IsAirArmy = true, Name = "Wing" };
                wing.AddMemberSorted(plane);
                ArmyRegistry.Register(wing);
                Sortie? formed = AiAirSortiePlanner.TryPlanSortie(wing, target, map, _owner);
                Assert.That(formed.HasValue, Is.True);
                Assert.That(formed.Value.TotalCost, Is.EqualTo(stored.Value.TotalCost),
                    "a stored group and the same formed wing see the same route price");
            }
            finally { Object.DestroyImmediate(mapObject); }
        }

        [Test, Category("UnityEngine")]
        public void StormMovesTheTurnEndOntoAnAirfield_TheThroughSortieIsRejected()
        {
            HexMap map = Map(6, out GameObject mapObject);
            try
            {
                Airfield(new HexCoord(2, 0), 2);
                var route = Line(6);
                var group = new[] { Heli(3, 3) };
                var ends = new List<int>();
                Assert.That(AviationRange.TrySimulateHexSequence(map, route, 6, 3, group, 3, _owner,
                    out _, out _, out _, out _, out _, ends), Is.True);
                Assert.That(ends, Is.EqualTo(new[] { 3, 6 }), "clear weather passes over the airfield");
                map.PublishWeather(MovementEnvironmentTests.Storms(new[] { new HexCoord(1, 0) }));
                Assert.That(AviationRange.TrySimulateHexSequence(map, route, 6, 3, group, 3, _owner,
                    out _, out _, out _, out _, out _, ends), Is.False,
                    "the 2-MP storm exit ends turn 1 on the airfield: a landing, not a free refuel");
            }
            finally { Object.DestroyImmediate(mapObject); }
        }
    }
}
#endif
