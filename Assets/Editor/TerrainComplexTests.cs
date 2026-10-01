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
#if !TERRAIN_MANAGED_HARNESS
            VisionSystem.Clear();
            AiReconMemory.Clear();
#endif
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
        [Test] public void PublishedLayoutRejectsDataOnlyTerrainMutation()
        {
            int version = _map.PathingVersion;
            _map.PublishTerrainLayout();
            Assert.That(_map.SetTerrainAt(_origin, _lake), Is.False);
            Assert.That(_map.PathingVersion, Is.EqualTo(version));
            Assert.That(_map.CanEnter(_origin), Is.True);
        }
        [Test] public void MissingComplexListsResolveToEmptyForBothBiomes()
        {
            var settings = new MapGenerationSettings { complexes = null };
            Assert.That(settings.ResolveBiome(Biome.Arid).complexes, Is.Empty);
            settings.desertOverride.complexes = null;
            settings.desertOverride.terrainTypes.Add(_desert);
            Assert.That(settings.ResolveBiome(Biome.Desert).complexes, Is.Empty);
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
        [Test] public void SnakeTriangleAndWreckChainSupportAllSixRotations()
        {
            foreach (var t in new[] { Template(new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(1, 1)),
                Template(new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(0, 1)),
                Template(new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(2, 0)) })
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
        [Test] public void AuthoredComplexesKeepBothBiomesConnectedAtEverySize()
        {
            var config = UnityEditor.AssetDatabase.LoadAssetAtPath<Game.Core.GameConfig>("Assets/Config/GameConfig.asset");
            Assert.That(config, Is.Not.Null);
            BuildingRegistry.Clear(); ArmyRegistry.Clear(); HexEventRegistry.Clear(); HexResourceBonusRegistry.Clear();
            var generator = _object.AddComponent<HexMapGenerator>();
            Type type = typeof(HexMapGenerator);
            type.GetField("gameConfig", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(generator, config);
            var randomState = UnityEngine.Random.state;
            try
            {
                foreach (Biome biome in new[] { Biome.Arid, Biome.Desert })
                {
                    var palette = config.mapGeneration.ResolveBiome(biome);
                    Assert.That(palette.complexes.Count, Is.EqualTo(4));
                    Assert.That(palette.terrainTypes.Single(t => t.terrainName == "Mountains").blocksGroundMovement, Is.False);
                    foreach (var template in palette.complexes)
                    {
                        Assert.That(template.IsValid(), Is.True);
                        Assert.That(template.parts.Length, Is.EqualTo(template.terrainName == "Acid lake" || template.terrainName == "Boiling mud field" ? 2 : 3));
                        Assert.That(template.count, Is.EqualTo(template.terrainName == "Acid lake" ? 2 : 1));
                        Assert.That(template.rotations, Is.EquivalentTo(new[] { 0, 1, 2, 3, 4, 5 }));
                        Assert.That(template.allowedTerrainNames.All(n => palette.terrainTypes.Any(t => t.terrainName == n && !t.blocksGroundMovement)), Is.True);
                        Assert.That(template.parts.All(p => p.frames.Length == ((template.terrainName == "Acid lake" || template.terrainName == "Boiling mud field") ? 7 : 1)), Is.True);
                    }
                    type.GetField("_activeBiome", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(generator, palette);
                    foreach (MapSize size in Enum.GetValues(typeof(MapSize)))
                        for (int seed = 0; seed < 20; seed++)
                        {
                            UnityEngine.Random.InitState(seed);
                            type.GetField("_activeRadius", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(generator, (int)size);
                            var coords = HexGridMath.HexesInRange(_origin, (int)size).ToList();
                            var assignment = (Dictionary<HexCoord, int>)type.GetMethod("AssignTerrainTypes", BindingFlags.NonPublic | BindingFlags.Instance)
                                .Invoke(generator, new object[] { coords });
                            var placed = (IList)type.GetMethod("PlaceComplexes", BindingFlags.NonPublic | BindingFlags.Instance)
                                .Invoke(generator, new object[] { coords, assignment });
                            Assert.That(TerrainComplexPlacement.GroundRemainsConnected(assignment, palette.terrainTypes, new HashSet<HexCoord>(), false), Is.True);
                            var cells = new HashSet<HexCoord>();
                            foreach (object complex in placed)
                            {
                                var footprint = (HexCoord[])complex.GetType().GetField("Cells").GetValue(complex);
                                var template = (TerrainComplexTemplate)complex.GetType().GetField("Template").GetValue(complex);
                                Assert.That(footprint.Length, Is.EqualTo(template.parts.Length));
                                foreach (HexCoord h in footprint) Assert.That(cells.Add(h), Is.True);
                            }
                            _map.SetData((int)size, 1, assignment.ToDictionary(p => p.Key, p => palette.terrainTypes[p.Value]));
                            foreach (HexCoord h in cells)
                            {
                                Assert.That(_map.CanEnter(h), Is.False); Assert.That(_map.CanEnter(h, true), Is.True);
                                _map.TryGetTerrainAt(h, out var entry);
                                Assert.That(entry.baselineWeight, Is.Zero); Assert.That(entry.resourceYields.HasAnyYield, Is.False);
                            }
                        }
                }
            }
            finally { UnityEngine.Random.state = randomState; }
        }
        private void VisitAllGroundExcept(PlayerSetupData owner, params HexCoord[] except)
        {
            // Set only the footprint history; visibility is intentionally not broadened.
            var visited = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visited", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            visited[owner] = new HashSet<HexCoord>(_map.AllCoords.Where(h => _map.CanEnter(h) && !except.Contains(h)));
        }
        [Test] public void LakeDoesNotKeepHomeGapOrShoreInformationAlive()
        {
            VisionSystem.Clear();
            var owner = new PlayerSetupData { CitadelHexQ = 0, CitadelHexR = 0 };
            var army = new ArmyData { Owner = owner, Hex = _origin };
            _map.SetTerrainAt(new HexCoord(1, 0), _lake);
            VisitAllGroundExcept(owner);
            var home = ReconGroundStepPlanner.HomePressure.Build(owner, _map, army, null);
            Assert.That(home.LocalGap, Is.Zero);
            Assert.That(ReconGroundStepPlanner.FreshNeighborCount(owner, _map, _origin), Is.Zero);
            // A real unexplored area does retain information; the lake contributes nothing.
            VisitAllGroundExcept(owner, new HexCoord(-2, 0));
            Assert.That(ReconGroundStepPlanner.FreshNeighborCount(owner, _map, new HexCoord(-1, 0)), Is.EqualTo(1));
            Assert.That(ReconGroundStepPlanner.FreshNeighborCount(owner, _map, _origin), Is.Zero);
            army.Members.Add(new UnitData { MoveMax = 2, MoveCurrent = 2 });
            var patrol = new ReconPatrolState { Mode = ReconMode.Explore,
                StrategicAnchor = new HexCoord(-2, 0), StrategicSector = ReconDirectionModel.Sector(_origin, new HexCoord(-1, 0)) };
            var choice = ReconGroundStepPlanner.Pick(owner, _map, army, patrol, 1, null);
            Assert.That(choice.HasValue, Is.True);
            Assert.That(choice.Value.Hex, Is.EqualTo(new HexCoord(-1, 0)), "The scout follows real ground exploration rather than the shore.");
            VisionSystem.Clear();
        }
        private EnemyContactSnapshot CanyonContact(bool air = false, ContactKnowledge knowledge = ContactKnowledge.Exact) =>
            new EnemyContactSnapshot { Position = new HexCoord(-1, 0), Knowledge = knowledge,
                LastObservedTurn = 10, Army = new ArmySnapshot { ArmyId = 7, IsAir = air, MaxMovement = 3 } };
        private void PlaceCanyon()
        {
            foreach (HexCoord h in new[] { _origin, new HexCoord(1, 0), new HexCoord(1, 1) })
                _map.SetTerrainAt(h, _lake);
        }
        private AssetThreatSnapshot ApproachThreat(EnemyContactSnapshot contact, int turn = 10)
        {
            int? cost = WorldAnalysis.ContactApproachCost(_map, contact, new HexCoord(2, 0), turn);
            return new AssetThreatSnapshot { Contact = contact,
                Asset = new StrategicAssetSnapshot { Kind = AssetKind.Base, Hex = new HexCoord(2, 0) },
                EnemyApproachCost = cost, EnemyEta = cost.HasValue ? (cost.Value + 2) / 3 : (int?)null,
                AttackWinChance = 1, CanDamage = true };
        }
        [Test] public void CurrentGroundContactAcrossSnakeIsNotSiegeButAirIs()
        {
            PlaceCanyon();
            var ground = ApproachThreat(CanyonContact());
            Assert.That(ground.EnemyApproachCost, Is.GreaterThan(3));
            Assert.That(ground.EnemyEta, Is.GreaterThan(1));
            Assert.That(WorldAnalysis.IsSiegeThreat(ground), Is.False);
            var air = ApproachThreat(CanyonContact(air: true));
            Assert.That(air.EnemyApproachCost, Is.EqualTo(3));
            Assert.That(WorldAnalysis.IsSiegeThreat(air), Is.True);
        }
        [Test] public void CloseGroundPassRestoresSiegeAndDefenderPin()
        {
            PlaceCanyon();
            var contact = CanyonContact(); contact.Army.Owner = new PlayerSetupData();
            var defender = new ArmySnapshot { ArmyId = 1, Hex = new HexCoord(2, 0), EffectiveArmyPower = 10 };
            var snap = new WorldSnapshot { Self = new SelfSnapshot { BaseHexes = new[] { defender.Hex }, Armies = new[] { defender } },
                Threat = new ThreatModel { Threats = new[] { ApproachThreat(contact) } } };
            Assert.That(ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, defender), Is.False);
            _map.SetTerrainAt(_origin, _desert); _map.SetTerrainAt(new HexCoord(1, 0), _desert);
            var threat = ApproachThreat(contact); snap.Threat.Threats = new[] { threat };
            Assert.That(WorldAnalysis.IsSiegeThreat(threat), Is.True);
            Assert.That(ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, defender), Is.True);
        }
        [Test] public void HistoricalContactMayAdvanceAndUnreachableOriginDoesNotRemoveRisk()
        {
            PlaceCanyon();
            var historical = CanyonContact(knowledge: ContactKnowledge.LastKnown);
            Assert.That(ApproachThreat(historical, 12).EnemyApproachCost, Is.Zero);
            Assert.That(WorldAnalysis.IsSiegeThreat(ApproachThreat(historical, 12)), Is.True);
            foreach (HexCoord h in _map.AllCoords.ToList()) if (h.Q == 0) _map.SetTerrainAt(h, _lake);
            Assert.That(WorldAnalysis.ContactApproachCost(_map, CanyonContact(), new HexCoord(2, 0), 10), Is.Null);
            Assert.That(WorldAnalysis.ContactApproachCost(_map, historical, new HexCoord(2, 0), 11), Is.Zero);
        }
        [Test] public void MovementFactsSurviveReconHistoryAndChangeThreatRefreshKey()
        {
            var owner = new PlayerSetupData();
            AiReconMemory.Clear();
            AiReconMemory.Observe(owner, 10, 1, new[] { new AiMapMemory.KnownEnemySighting(_origin,
                new PlayerSetupData(), "air", 1, 1, 1, Array.Empty<WorthIt.DefenderProfile>(),
                seenTurn: 10, armyId: 7, isAir: true, maxMovement: 5) });
            var observation = AiReconMemory.Historical(owner, new HashSet<int>()).Single();
            Assert.That(observation.IsAir, Is.True); Assert.That(observation.MaxMovement, Is.EqualTo(5));
            var threat = ApproachThreat(CanyonContact()); string before = WorldAnalysis.ThreatKey(threat);
            threat.EnemyApproachCost++;
            Assert.That(WorldAnalysis.ThreatKey(threat), Is.Not.EqualTo(before));
            AiReconMemory.Clear();
        }
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
