#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.Economy;
using Game.Players;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class TerrainComplexTests
    {
        private GameObject _object;
        private HexMap _map;
        private Texture2D _texture;
        private readonly TerrainTypeEntry _desert = new TerrainTypeEntry { terrainName = "Desert" };
        private readonly TerrainTypeEntry _lake = new TerrainTypeEntry { terrainName = "Acid lake", blocksGroundMovement = true };
        private readonly HexCoord _origin = new HexCoord(0, 0);

        [SetUp] public void SetUp()
        {
            AiMapMemory.Clear();
            _object = new GameObject("TerrainComplexTests");
            _map = _object.AddComponent<HexMap>();
            _texture = new Texture2D(2, 2);
            _map.SetData(3, 1, HexGridMath.HexesInRange(_origin, 3).ToDictionary(h => h, h => _desert));
        }
        [TearDown] public void TearDown()
        {
            AiMapMemory.Clear();
            UnityEngine.Object.DestroyImmediate(_object);
            UnityEngine.Object.DestroyImmediate(_texture);
        }
        private TerrainComplexTemplate Template(params Vector2Int[] offsets) => new TerrainComplexTemplate
        {
            terrainName = "Acid lake",
            parts = offsets.Select(o => new TerrainComplexPart { offset = o, frames = new[] { _texture } }).ToArray(),
        };
        private Dictionary<HexCoord, int> Assignment() => _map.AllCoords.ToDictionary(h => h, h => 0);
        private bool Validate(TerrainComplexTemplate t, HexCoord origin, int rotation, Dictionary<HexCoord, int> data,
            HashSet<HexCoord> claimed, Func<HexCoord, bool> reserved, out HexCoord[] cells) =>
            TerrainComplexPlacement.TryValidate(t, origin, rotation, data, new[] { _desert, _lake }, 1,
                claimed, reserved, out cells);

        [Test] public void OldTerrainIncludingMountainsRemainsPassable()
        {
            _map.SetTerrainAt(_origin, new TerrainTypeEntry { terrainName = "Mountains", moveCost = 3 });
            Assert.That(_map.CanEnter(_origin), Is.True);
        }
        [Test] public void GroundCannotEnterButAirCan()
        {
            _map.SetTerrainAt(_origin, _lake);
            Assert.That(_map.CanEnter(_origin), Is.False);
            Assert.That(_map.CanEnter(_origin, airborne: true), Is.True);
            Assert.That(_map.CanEnter(new HexCoord(100, 100), airborne: true), Is.False);
        }
        [Test] public void ImpassableTerrainProducesNoResourcesEvenWithBonus()
        {
            _lake.resourceYields = new ResourceYields { energy = 5 };
            Assert.That(HexResourceCalculator.GetEffectiveYield(_lake, new ResourceYields { human = 3 }).HasAnyYield, Is.False);
        }
        [Test] public void GroundPathDetoursButAirPathRemainsFlat()
        {
            var a = new HexCoord(-1, 0); var b = new HexCoord(1, 0);
            _map.SetTerrainAt(_origin, _lake);
            HexPath ground = HexPathfinder.FindPath(_map, a, b);
            HexPath air = HexPathfinder.FindPath(_map, a, b, flatCost: true);
            Assert.That(ground.Hexes.Contains(_origin), Is.False);
            Assert.That(ground.TotalCost, Is.GreaterThan(2));
            Assert.That(air.Hexes.Contains(_origin), Is.True);
            Assert.That(air.TotalCost, Is.EqualTo(2));
        }
        [Test] public void ForbiddenDestinationCannotBeExemptedByThreatBlocker()
        {
            _map.SetTerrainAt(_origin, _lake);
            var start = new HexCoord(-1, 0);
            Assert.That(HexPathfinder.FindPath(_map, start, _origin, blockHex: h => false), Is.Null);
            Assert.That(HexPathfinder.FindCosts(_map, new[] { start }, blockHex: h => true).ContainsKey(_origin), Is.False);
        }
        [Test] public void ForwardAndReverseGroundFieldsExcludeObstacles()
        {
            _map.SetTerrainAt(_origin, _lake);
            var start = new HexCoord(-1, 0);
            Assert.That(HexPathfinder.FindCosts(_map, new[] { start }).ContainsKey(_origin), Is.False);
            Assert.That(HexPathfinder.FindCosts(_map, new[] { start }, reverse: true).ContainsKey(_origin), Is.False);
            Assert.That(HexPathfinder.FindCosts(_map, new[] { start }, flatCost: true)[_origin], Is.EqualTo(1));
        }
        [Test] public void WallMakesGroundTargetUnreachable()
        {
            foreach (HexCoord h in _map.AllCoords.ToList()) if (h.Q == 0) _map.SetTerrainAt(h, _lake);
            Assert.That(HexPathfinder.FindPath(_map, new HexCoord(-1, 0), new HexCoord(1, 0)), Is.Null);
            Assert.That(HexPathfinder.FindPath(_map, new HexCoord(-1, 0), new HexCoord(1, 0), flatCost: true), Is.Not.Null);
        }
        [Test] public void TerrainMutationBumpsRevisionIncludingMutatedSharedEntry()
        {
            int before = _map.PathingVersion;
            _map.SetTerrainAt(_origin, _lake);
            Assert.That(_map.PathingVersion, Is.EqualTo(before + 1));
            _map.SetTerrainAt(_origin, _lake);
            Assert.That(_map.PathingVersion, Is.EqualTo(before + 2));
        }
        [Test] public void PairAcceptedAndValidationDoesNotMutateData()
        {
            var t = Template(new Vector2Int(0, 0), new Vector2Int(1, 0)); var data = Assignment();
            Assert.That(Validate(t, _origin, 0, data, new HashSet<HexCoord>(), null, out var cells), Is.True);
            Assert.That(cells.Length, Is.EqualTo(2));
            Assert.That(data.Values.All(x => x == 0), Is.True);
        }
        [Test] public void OutOfMapPairRejectedAtomically()
        {
            var t = Template(new Vector2Int(0, 0), new Vector2Int(1, 0)); var data = Assignment();
            Assert.That(Validate(t, new HexCoord(3, 0), 0, data, new HashSet<HexCoord>(), null, out var cells), Is.False);
            Assert.That(cells, Is.Null); Assert.That(data.Values.All(x => x == 0), Is.True);
        }
        [Test] public void ReservedCellRejectsWholeComplex()
        {
            var t = Template(new Vector2Int(0, 0), new Vector2Int(1, 0));
            Assert.That(Validate(t, _origin, 0, Assignment(), new HashSet<HexCoord>(), h => h.Q == 1, out var cells), Is.False);
            Assert.That(cells, Is.Null);
        }
        [Test] public void OverlapRejectsWholeComplex()
        {
            var t = Template(new Vector2Int(0, 0), new Vector2Int(1, 0));
            Assert.That(Validate(t, _origin, 0, Assignment(), new HashSet<HexCoord> { _origin }, null, out _), Is.False);
        }
        [Test] public void ProtectedTerrainIsNotOverwritten()
        {
            var t = Template(new Vector2Int(0, 0), new Vector2Int(1, 0)); var data = Assignment();
            data[_origin] = 1;
            Assert.That(Validate(t, _origin, 0, data, new HashSet<HexCoord>(), null, out _), Is.False);
        }
        [Test] public void ComplexThatCutsNarrowBridgeIsRejected()
        {
            var data = new Dictionary<HexCoord, int>();
            foreach (int q in new[] { -2, -1, 0, 1, 2 }) data[new HexCoord(q, 0)] = 0;
            var t = Template(new Vector2Int(0, 0), new Vector2Int(1, 0));
            Assert.That(Validate(t, _origin, 0, data, new HashSet<HexCoord>(), null, out var cells), Is.False);
            Assert.That(cells, Is.Null);
        }
        [Test] public void SnakeAndTriangleSupportAllSixRotations()
        {
            foreach (var t in new[] { Template(new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(1, 1)),
                Template(new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(0, 1)) })
                for (int rotation = 0; rotation < 6; rotation++)
                    Assert.That(Validate(t, _origin, rotation, Assignment(), new HashSet<HexCoord>(), null, out var cells), Is.True);
        }
        [Test] public void InvalidDisconnectedOrDuplicateShapesRejected()
        {
            Assert.That(Template(new Vector2Int(0, 0), new Vector2Int(3, 0)).IsValid(), Is.False);
            Assert.That(Template(new Vector2Int(0, 0), new Vector2Int(0, 0)).IsValid(), Is.False);
        }
        [Test] public void UnequalAnimationSequencesRejected()
        {
            var t = Template(new Vector2Int(0, 0), new Vector2Int(1, 0));
            t.parts[1].frames = new[] { _texture, _texture };
            Assert.That(t.IsValid(), Is.False);
        }
        [Test] public void AnimationClockLoopsSevenFramesAndSupportsPhaseOffset()
        {
            Assert.That(MapTerrainAnimator.FrameAtTime(0, 3, 0, 7), Is.EqualTo(0));
            Assert.That(MapTerrainAnimator.FrameAtTime(1, 3, 0, 7), Is.EqualTo(3));
            Assert.That(MapTerrainAnimator.FrameAtTime(7.0 / 3, 3, 0, 7), Is.EqualTo(0));
            Assert.That(MapTerrainAnimator.FrameAtTime(1, 3, 2, 7), Is.EqualTo(5));
        }
        [Test] public void AnimatedPartsStaySynchronousWithoutChangingMapRevision()
        {
            var frames = Enumerable.Range(0, 7).Select(i => new Texture2D(2, 2)).ToArray();
            var materials = new[] { new Material(Shader.Find("Custom/HexBlend")), new Material(Shader.Find("Custom/HexBlend")) };
            var animator = _object.AddComponent<MapTerrainAnimator>();
            int version = _map.PathingVersion;
            animator.Configure(new[] { new MapTerrainAnimator.Group { Materials = materials,
                Frames = new[] { frames, frames }, FramesPerSecond = 3, Phase = 1 } });
            animator.ApplyAtTime(1);
            Assert.That(materials[0].mainTexture, Is.SameAs(frames[4]));
            Assert.That(materials[1].mainTexture, Is.SameAs(frames[4]));
            Assert.That(_map.PathingVersion, Is.EqualTo(version));
            foreach (var material in materials) UnityEngine.Object.DestroyImmediate(material);
            foreach (var frame in frames) UnityEngine.Object.DestroyImmediate(frame);
        }
        [Test] public void GroundRouteCacheInvalidatesWhenTerrainChanges()
        {
            var owner = new PlayerSetupData(); var start = new HexCoord(-1, 0); var target = new HexCoord(1, 0);
            Assert.That(SafeStepPathing.FindSafePathCost(_map, owner, start, target), Is.EqualTo(2));
            _map.SetTerrainAt(_origin, _lake);
            Assert.That(SafeStepPathing.FindSafePathCost(_map, owner, start, target), Is.GreaterThan(2));
        }
        [Test] public void AirCostDoesNotReuseGroundRouteCache()
        {
            var owner = new PlayerSetupData(); var start = new HexCoord(-1, 0); var target = new HexCoord(1, 0);
            _map.SetTerrainAt(_origin, _lake);
            var ground = new ArmyData { Hex = start, Owner = owner };
            ground.Members.Add(new UnitData { MoveCurrent = 3, MoveMax = 3 });
            var air = new ArmyData { Hex = start, Owner = owner };
            air.Members.Add(new UnitData { MoveCurrent = 3, MoveMax = 3, IsAviation = true });
            Assert.That(SafeStepPathing.FindSafePathCost(_map, ground, target), Is.GreaterThan(2));
            Assert.That(SafeStepPathing.FindSafePathCost(_map, air, target), Is.EqualTo(2));
        }
#if !TERRAIN_MANAGED_HARNESS
        [Test] public void GroundRetreatFailsWhenEveryNeighborIsImpassable()
        {
            BuildingRegistry.Clear();
            var owner = new PlayerSetupData();
            var army = new ArmyData { Owner = owner, Hex = _origin };
            foreach (HexCoord h in HexGridMath.Neighbors(_origin)) _map.SetTerrainAt(h, _lake);
            Assert.That(BattleEngine.TryFindRetreatDestination(_map, army, _origin, 0, out _), Is.False);
            army.Members.Add(new UnitData { IsAviation = true });
            Assert.That(BattleEngine.TryFindRetreatDestination(_map, army, _origin, 0, out _), Is.True);
        }
        [Test] public void ScoutEstimatorDoesNotReplaceUnreachableRouteWithStraightDistance()
        {
            foreach (HexCoord h in _map.AllCoords.ToList()) if (h.Q == 0) _map.SetTerrainAt(h, _lake);
            var snapshot = new WorldSnapshot { Map = _map };
            var actor = new ArmySnapshot { Hex = new HexCoord(-1, 0), MaxMovement = 3, CurrentMovement = 3 };
            var cost = ScoutCostModel.PairCost(snapshot, actor, new HexCoord(1, 0), false);
            Assert.That(cost.Distance, Is.EqualTo(int.MaxValue));
            Assert.That(cost.EtaTurns, Is.EqualTo(int.MaxValue));
        }
#endif
        [Test] public void StaleMovementRouteDoesNotSpendMovementOrEnterObstacle()
        {
            var army = new ArmyData { Hex = _origin };
            var unit = new UnitData { MoveCurrent = 3, MoveMax = 3 }; army.Members.Add(unit);
            var controller = _object.AddComponent<ArmyController>(); controller.SetData(army);
            var target = new HexCoord(1, 0); _map.SetTerrainAt(target, _lake);
            int activations = 0;
            var method = typeof(ArmyController).GetMethod("MoveRoutine", BindingFlags.NonPublic | BindingFlags.Instance);
            var routine = (IEnumerator)method.Invoke(controller, new object[] { _map,
                new List<HexCoord> { _origin, target }, (Func<HexCoord, Vector3>)(h => Vector3.zero),
                null, null, null, null, null, (Func<bool>)(() => { activations++; return true; }) });
            Assert.That(routine.MoveNext(), Is.False);
            Assert.That(unit.MoveCurrent, Is.EqualTo(3));
            Assert.That(activations, Is.EqualTo(0), "A refused first step must not charge activation.");
            Assert.That(controller.CurrentHex, Is.EqualTo(_origin));
        }
    }
}
#endif
