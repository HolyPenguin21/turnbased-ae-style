#if UNITY_INCLUDE_TESTS && ENABLE_INPUT_SYSTEM
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // Native camera/input tests: run in Unity, never replace projection with a managed stub.
    public sealed class HoverCacheUnityTests
    {
        private GameObject root;
        private Camera camera;
        private HexMap map;
        private HexSelectionController selection;
        private Mouse mouse, previousMouse;
        private readonly Vector2 position = new Vector2(600, 300);

        [SetUp]
        public void SetUp()
        {
            previousMouse = Mouse.current;
            mouse = InputSystem.AddDevice<Mouse>();
            mouse.MakeCurrent();
            InputSystem.QueueStateEvent(mouse, new MouseState { position = position });
            InputSystem.Update();
            root = new GameObject("HoverCacheUnityTests");
            root.SetActive(false);
            camera = root.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(0, 20, 0), Quaternion.Euler(90, 0, 0));
            camera.orthographic = true;
            camera.orthographicSize = 8;
            camera.pixelRect = new Rect(0, 0, 800, 600);
            var mapObject = new GameObject("map");
            mapObject.transform.SetParent(root.transform, false);
            // Camera's root is translated/rotated; keep the map on the XZ ground plane.
            mapObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            map = mapObject.AddComponent<HexMap>();
            map.SetData(12, 1, Terrain());
            selection = root.AddComponent<HexSelectionController>();
            typeof(HexSelectionController).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(selection, map);
            typeof(HexSelectionController).GetField("targetCamera", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(selection, camera);
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (mouse != null) InputSystem.RemoveDevice(mouse);
            previousMouse?.MakeCurrent();
        }

        private static Dictionary<HexCoord, TerrainTypeEntry> Terrain() =>
            HexGridMath.HexesInRange(new HexCoord(0, 0), 12)
                .ToDictionary(h => h, _ => new TerrainTypeEntry { moveCost = 1 });

        private HexCoord? Hover() => (HexCoord?)typeof(HexSelectionController)
            .GetMethod("RaycastHexCached", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(selection, null);

        [TestCase("viewport")]
        [TestCase("projection")]
        [TestCase("map-transform")]
        public void StationaryMouseTracksChangedGeometry(string change)
        {
            HexCoord? before = Hover();
            Assert.That(before.HasValue, Is.True);
            switch (change)
            {
                case "viewport": camera.pixelRect = new Rect(0, 0, 1600, 600); break;
                case "projection": camera.projectionMatrix = Matrix4x4.Ortho(0, 16, -8, 8, .3f, 100); break;
                case "map-transform": map.transform.position += new Vector3(6, 0, 0); break;
            }
            HexCoord? fresh = selection.RaycastHex(position);
            Assert.That(fresh.HasValue, Is.True);
            Assert.That(fresh, Is.Not.EqualTo(before), "fixture must change the hex under the same cursor");
            Assert.That(Hover(), Is.EqualTo(fresh));
            Assert.That(Hover(), Is.EqualTo(fresh), "the next cached read must agree too");
        }

        [Test]
        public void SameMapDataReplacementDropsRemovedHex()
        {
            HexCoord? before = Hover();
            Assert.That(before.HasValue, Is.True);
            var terrain = Terrain();
            terrain.Remove(before.Value);
            map.SetData(12, 1, terrain);
            Assert.That(selection.RaycastHex(position), Is.Null);
            Assert.That(Hover(), Is.Null);
        }

        [Test]
        public void DifferentMapAtSameRevisionCannotReuseOldHex()
        {
            HexCoord? before = Hover();
            Assert.That(before.HasValue, Is.True);
            var replacement = new GameObject("replacement map");
            replacement.transform.SetParent(root.transform, false);
            replacement.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var other = replacement.AddComponent<HexMap>();
            var terrain = Terrain();
            terrain.Remove(before.Value);
            other.SetData(12, 1, terrain);
            Assert.That(other.PathingVersion, Is.EqualTo(map.PathingVersion));
            typeof(HexSelectionController).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(selection, other);
            Assert.That(selection.RaycastHex(position), Is.Null);
            Assert.That(Hover(), Is.Null);
        }

        [Test, Explicit("Native timing diagnostic; run separately from correctness tests")]
        public void MeasureIdleHoverCacheAgainstDirectLookup()
        {
            var method = typeof(HexSelectionController).GetMethod("RaycastHexCached",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var cached = (System.Func<HexCoord?>)System.Delegate.CreateDelegate(
                typeof(System.Func<HexCoord?>), selection, method);
            HexCoord? result = null;
            for (int i = 0; i < 512; i++)
            {
                result = cached();
                result = selection.RaycastHex(Mouse.current.position.ReadValue());
            }
            foreach (bool useCache in new[] { true, false })
            {
                var times = new double[7];
                var bytes = new double[7];
                for (int sample = 0; sample < times.Length; sample++)
                {
                    const int count = 30000;
                    long allocated = System.GC.GetAllocatedBytesForCurrentThread();
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (int i = 0; i < count; i++)
                        result = useCache ? cached() : selection.RaycastHex(Mouse.current.position.ReadValue());
                    times[sample] = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1e6
                        / System.Diagnostics.Stopwatch.Frequency / count;
                    bytes[sample] = (System.GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count;
                }
                System.Array.Sort(times);
                System.Array.Sort(bytes);
                TestContext.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "NATIVE_HOVER_BENCH|cached={0}|median_us={1:F3}|bytes={2:F1}", useCache, times[3], bytes[3]));
            }
            System.GC.KeepAlive(result);
        }
    }
}
#endif
