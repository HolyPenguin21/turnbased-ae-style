#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class HexMapLayoutTests
    {
        private GameObject _object;
        private GameConfig _config;
        private Texture2D _texture;
        private HexMap _map;
        private HexMapGenerator _generator;
        private UnityEngine.Random.State _randomState;

        [SetUp] public void SetUp()
        {
            _randomState = UnityEngine.Random.state;
            _object = new GameObject("HexMapLayoutTests");
            _map = _object.AddComponent<HexMap>();
            if (_object.GetComponent<MeshFilter>() == null) _object.AddComponent<MeshFilter>();
            if (_object.GetComponent<MeshRenderer>() == null) _object.AddComponent<MeshRenderer>();
            _generator = _object.AddComponent<HexMapGenerator>();
            _texture = new Texture2D(2, 2) { name = "original" };
            _config = ScriptableObject.CreateInstance<GameConfig>();
            _config.mapGeneration = new MapGenerationSettings
            {
                radius = 1, borderDepthFraction = 0.5f, complexCount = 0,
                terrainTypes = new List<TerrainTypeEntry>
                { new TerrainTypeEntry { terrainName = "Desert", texture = _texture } },
            };
            typeof(HexMapGenerator).GetField("gameConfig", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_generator, _config);
        }

        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_object);
            UnityEngine.Object.DestroyImmediate(_config);
            UnityEngine.Object.DestroyImmediate(_texture);
            UnityEngine.Random.state = _randomState;
        }


        private void Restore(HexMapLayout layout)
        {
            MethodInfo method = typeof(HexMapGenerator).GetMethod("BuildFromLayout");
            Assert.That(method, Is.Not.Null, "restore must use the existing generator render pipeline");
            try { method.Invoke(_generator, new object[] { layout }); }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }

        [Test] public void LayoutSnapshotsDoNotExposeTheirNestedArraysToCallers()
        {
            var input = new HexMapLayout
            {
                Cells = new[] { new MapLayoutCell { Q = 2, TerrainKey = "terrain.sand" } },
                Complexes = new[] { new MapLayoutComplex { Parts = new[] { new MapLayoutCell { R = 3 } } } },
            };
            _map.SetLayout(input);
            input.Cells[0].Q = 99;
            input.Complexes[0].Parts[0].R = 99;
            HexMapLayout output = _map.Layout;
            output.Cells[0].Q = 88;
            output.Complexes[0].Parts[0].R = 88;
            Assert.That(_map.Layout.Cells[0].Q, Is.EqualTo(2));
            Assert.That(_map.Layout.Complexes[0].Parts[0].R, Is.EqualTo(3));
        }

        [Test] public void RestoreUsesCapturedMeshValuesAndDoesNotConsumeRandomNumbers()
        {
            _config.mapGeneration.terrainTypes[0].contentKey = "terrain.sand";
            _config.mapGeneration.terrainTypes[0].textureKey = "texture.sand";
            _generator.Generate();
            HexMapLayout layout = _map.Layout;
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.Cells.Length, Is.EqualTo(7));
            Assert.That(layout.BorderCells.Length, Is.GreaterThan(0));
            Assert.That(layout.Cells.All(c => c.TerrainKey == "terrain.sand" && c.TextureKey == "texture.sand"), Is.True);
            Mesh original = _object.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices = original.vertices.ToArray();
            Vector2[] uv = original.uv.ToArray();
            Color[] colors = original.colors.ToArray();
            _config.mapGeneration.radius = 8;
            _config.mapGeneration.outerRadius = 4;
            _config.mapGeneration.blend = 0.8f;
            _config.mapGeneration.borderDepthFraction = 0;
            _config.mapGeneration.terrainTypes[0].terrainName = "Renamed";
            _texture.name = "Renamed texture";
            UnityEngine.Random.InitState(913);
            float expectedNext = UnityEngine.Random.value;
            UnityEngine.Random.InitState(913);
            Restore(layout);
            Assert.That(UnityEngine.Random.value, Is.EqualTo(expectedNext));
            Mesh restored = _object.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(restored.vertices, Is.EqualTo(vertices));
            Assert.That(restored.uv, Is.EqualTo(uv));
            Assert.That(restored.colors, Is.EqualTo(colors));
            Assert.That(_map.FieldRadius, Is.EqualTo(1));
            Assert.That(_map.OuterRadius, Is.EqualTo(1));
            Assert.That(_map.AllCoords.Count(), Is.EqualTo(7));
        }

        [Test] public void RestoreRejectsMissingContentBeforeReplacingTheCurrentMap()
        {
            _generator.Generate();
            HexMapLayout layout = _map.Layout;
            Assert.That(layout, Is.Not.Null);
            Mesh original = _object.GetComponent<MeshFilter>().sharedMesh;
            int version = _map.PathingVersion;
            layout.Cells[0].TerrainKey = "terrain.unavailable";
            Assert.Throws<ArgumentException>(() => Restore(layout));
            Assert.That(_object.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(original));
            Assert.That(_map.PathingVersion, Is.EqualTo(version));
        }

        [Test] public void RestoreRejectsIncompleteOrDuplicatedPlayableFootprints()
        {
            _generator.Generate();
            HexMapLayout layout = _map.Layout;
            Assert.That(layout, Is.Not.Null);
            HexMapLayout incomplete = layout.Copy();
            incomplete.Cells = incomplete.Cells.Take(6).ToArray();
            Assert.Throws<ArgumentException>(() => Restore(incomplete));
            layout.Cells[1] = layout.Cells[0].Copy();
            Assert.Throws<ArgumentException>(() => Restore(layout));
        }

        [Test] public void RestoreDoesNotFallBackToAnotherBiomeWhenTheSavedPaletteIsMissing()
        {
            _generator.Generate();
            HexMapLayout layout = _map.Layout;
            layout.Biome = Biome.Desert;
            _config.mapGeneration.desertOverride = null;
            Assert.Throws<ArgumentException>(() => Restore(layout));
        }

        [Test] public void TextureKeysPreserveActualCellTexturesWhenVariantsAreReordered()
        {
            var other = new Texture2D(2, 2) { name = "Other texture" };
            try
            {
                TerrainTypeEntry terrain = _config.mapGeneration.terrainTypes[0];
                terrain.textureKey = "texture.first";
                terrain.alternativeTextures = new[] { other };
                terrain.alternativeTextureKeys = new[] { "texture.second" };
                _generator.Generate();
                HexMapLayout layout = _map.Layout;
                Assert.That(layout.Cells.Select(c => c.TextureKey).Distinct().Count(), Is.EqualTo(2));
                terrain.texture = other; terrain.textureKey = "texture.second";
                terrain.alternativeTextures = new[] { _texture };
                terrain.alternativeTextureKeys = new[] { "texture.first" };
                Restore(layout);
                Mesh mesh = _object.GetComponent<MeshFilter>().sharedMesh;
                Material[] materials = _object.GetComponent<MeshRenderer>().sharedMaterials;
                for (int i = 0; i < layout.Cells.Length; i++)
                {
                    Vector3 center = HexGridMath.AxialToWorld(layout.Cells[i].Q, layout.Cells[i].R, layout.OuterRadius);
                    int vertex = Array.FindIndex(mesh.vertices, v => v.Equals(center));
                    int slot = Enumerable.Range(0, mesh.subMeshCount).Single(n => mesh.GetTriangles(n).Contains(vertex));
                    Assert.That(materials[slot].mainTexture, Is.SameAs(layout.Cells[i].TextureKey == "texture.first" ? _texture : other));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }

#if TERRAIN_MANAGED_HARNESS
        [Test] public void PendingMapBootstrapRestoresWithoutRandomFallbackEvenWhenInvalid()
        {
            _generator.Generate();
            HexMapLayout saved = _map.Layout;
            _config.mapGeneration.radius = 5;
            Application.isPlaying = true;
            try
            {
                GameSession.PendingRestore = new PendingMapRestore { Map = saved };
                GameSession.SetupError = null;
                UnityEngine.Random.InitState(43);
                float next = UnityEngine.Random.value;
                UnityEngine.Random.InitState(43);
                typeof(HexMapGenerator).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(_generator, null);
                Assert.That(_map.FieldRadius, Is.EqualTo(1));
                Assert.That(GameSession.SetupError, Is.Null);
                Assert.That(UnityEngine.Random.value, Is.EqualTo(next));
                saved.Cells[0].TerrainKey = "unknown";
                Mesh original = _object.GetComponent<MeshFilter>().sharedMesh;
                typeof(HexMapGenerator).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(_generator, null);
                Assert.That(GameSession.SetupError, Is.Not.Null);
                Assert.That(_object.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(original));
            }
            finally
            {
                GameSession.PendingRestore = null;
                GameSession.SetupError = null;
                Application.isPlaying = false;
            }
        }
#endif

#if !TERRAIN_MANAGED_HARNESS
        [Test] public void UnityJsonRoundTripPreservesPrimitiveLayoutForRestore()
        {
            _generator.Generate();
            string json = JsonUtility.ToJson(_map.Layout);
            Assert.That(json, Does.Not.Contain("instanceID"));
            HexMapLayout restored = JsonUtility.FromJson<HexMapLayout>(json);
            Restore(restored);
            Assert.That(_map.AllCoords.Count(), Is.EqualTo(7));
            Assert.That(_map.Layout.Cells[0].TerrainKey, Is.EqualTo(restored.Cells[0].TerrainKey));
        }
#endif

        [Test] public void FogOverlayCoversTheSavedDecorativeCellsWithoutUsingCurrentSizeDefaults()
        {
            _generator.Generate();
            HexMapLayout layout = _map.Layout;
            layout.BorderCells = new[] { new MapLayoutCell { Q = 5, R = 0,
                TerrainKey = layout.Cells[0].TerrainKey, TextureKey = layout.Cells[0].TextureKey } };
            _map.SetLayout(layout);
            _config.mapGeneration.radius = 8;
            _config.mapGeneration.outerRadius = 10;
            _config.mapGeneration.borderDepthFraction = 1;
            MethodInfo build = typeof(FogOfWarController).GetMethod("BuildQuadMesh", BindingFlags.Static | BindingFlags.NonPublic);
            Mesh mesh = (Mesh)build.Invoke(null, new object[] { _map });
            try
            {
                Assert.That(mesh.vertices.Min(v => v.x), Is.EqualTo(-3f).Within(0.001));
                Assert.That(mesh.vertices.Max(v => v.x), Is.EqualTo(9f).Within(0.001));
                Assert.That(_map.AllCoords.Count(), Is.EqualTo(7), "decorative cells remain outside gameplay");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test] public void RestoreRetainsComplexPartOrderRotationAndAnimationPhase()
        {
            _config.mapGeneration.radius = 3;
            _config.mapGeneration.complexCount = 2;
            _config.mapGeneration.terrainTypes.Add(new TerrainTypeEntry
            { contentKey = "terrain.lake", terrainName = "Lake", baselineWeight = 0 });
            _config.mapGeneration.complexes.Add(new TerrainComplexTemplate
            {
                contentKey = "complex.lake", name = "Lake pair", terrainName = "Lake",
                allowedTerrainNames = new[] { "Desert" }, randomizeRotation = true,
                framesPerSecond = 2.5f,
                parts = new[]
                {
                    new TerrainComplexPart { offset = new Vector2Int(0, 0), frames = new[] { _texture, _texture } },
                    new TerrainComplexPart { offset = new Vector2Int(1, 0), frames = new[] { _texture, _texture } },
                },
            });
            UnityEngine.Random.InitState(18);
            _generator.Generate();
            HexMapLayout layout = _map.Layout;
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.Complexes.Length, Is.EqualTo(2));
            Assert.That(layout.Complexes.All(c => c.Parts.Length == 2 && c.TemplateKey == "complex.lake"), Is.True);
            Vector2[] uv = _object.GetComponent<MeshFilter>().sharedMesh.uv.ToArray();
            _config.mapGeneration.complexes[0].name = "Renamed complex";
            _config.mapGeneration.complexes[0].useInGeneration = false;
            _config.mapGeneration.complexes[0].framesPerSecond = 99;
            Restore(layout);
            Assert.That(_object.GetComponent<MeshFilter>().sharedMesh.uv, Is.EqualTo(uv));
            var groups = (List<MapTerrainAnimator.Group>)typeof(MapTerrainAnimator)
                .GetField("_groups", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_object.GetComponent<MapTerrainAnimator>());
            Assert.That(groups.Select(g => g.Phase), Is.EqualTo(layout.Complexes.Select(c => c.Phase)));
            Assert.That(groups.All(g => g.FramesPerSecond == 2.5f), Is.True);
        }

        [Test] public void GeneratedMapRetainsItsVisualLayoutAfterGeneratorIsRemoved()
        {
            _generator.Generate();
            PropertyInfo property = typeof(HexMap).GetProperty("Layout");
            Assert.That(property, Is.Not.Null, "HexMap must retain the layout after its generator is destroyed");
            Assert.That(property.GetValue(_map), Is.Not.Null);
        }
    }
}
#endif
