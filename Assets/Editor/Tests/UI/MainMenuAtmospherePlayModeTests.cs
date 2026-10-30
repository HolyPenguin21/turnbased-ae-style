#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System.Collections;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.EditorTests
{
    // Enters Play Mode per test (~20 s each): full suite only (Tools/ai-verify/README.md, "Unity runs").
    [Category("Extended")]
    public sealed class MainMenuAtmospherePlayModeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string name, object value) => typeof(MainMenuAtmosphereGraphic).GetField(name, Private).SetValue(target, value);
        private static double Clock(MainMenuAtmosphereGraphic graphic) => (double)typeof(MainMenuAtmosphereGraphic).GetField("elapsed", Private).GetValue(graphic);

        [UnitySetUp]
        public IEnumerator Enter() { yield return new EnterPlayMode(); yield return null; }

        [UnityTearDown]
        public IEnumerator Exit() { yield return null; yield return new ExitPlayMode(); }

        [UnityTest]
        public IEnumerator WindUsesUnscaledTimeButStopsForCollectionAndSetup()
        {
            float previousScale = Time.timeScale;
            GameObject root = null;
            Texture2D atlas = null, mask = null;
            try
            {
                var shader = Shader.Find("Game/UI/MainMenuAtmosphere");
                Assert.That(shader, Is.Not.Null);
                root = new GameObject("wind clock play test", typeof(RectTransform), typeof(Canvas));
                root.SetActive(false);
                var menu = new GameObject("menu"); menu.transform.SetParent(root.transform);
                var collection = new GameObject("collection"); collection.transform.SetParent(root.transform); collection.SetActive(false);
                var go = new GameObject("wind", typeof(RectTransform), typeof(CanvasRenderer));
                go.transform.SetParent(root.transform, false);
                ((RectTransform)go.transform).sizeDelta = new Vector2(1600, 900);
                var graphic = go.AddComponent<MainMenuAtmosphereGraphic>();
                atlas = new Texture2D(4, 4); mask = new Texture2D(1, 1);
                Set(graphic, "mainMenuPanel", menu); Set(graphic, "collectionWindow", collection);
                Set(graphic, "dustAtlas", atlas); Set(graphic, "architectureMask", mask); Set(graphic, "atmosphereShader", shader);
                root.SetActive(true);
                Time.timeScale = 0;
                double initial = Clock(graphic);
                yield return null;
                yield return null;
                Assert.That(Clock(graphic), Is.GreaterThan(initial), "A paused game clock must not freeze the menu.");

                // Also covers collection activation before MainMenuPanel has been hidden.
                collection.SetActive(true);
                yield return null;
                double paused = Clock(graphic);
                yield return null;
                yield return null;
                Assert.That(Clock(graphic), Is.EqualTo(paused));
                Canvas.ForceUpdateCanvases();
                Mesh mesh = graphic.canvasRenderer.GetMesh();
                Assert.That(mesh != null ? mesh.vertexCount : 0, Is.Zero, "The actual CanvasRenderer must contain no effects in collection.");

                menu.SetActive(false); collection.SetActive(false);
                yield return null;
                Assert.That(Clock(graphic), Is.EqualTo(paused), "Returning to setup must keep the wind paused.");
                menu.SetActive(true);
                yield return null;
                yield return null;
                Assert.That(Clock(graphic), Is.GreaterThan(paused));
                Canvas.ForceUpdateCanvases();
                mesh = graphic.canvasRenderer.GetMesh();
                Assert.That(mesh, Is.Not.Null);
                Assert.That(mesh.vertexCount, Is.GreaterThan(0));
            }
            finally
            {
                Time.timeScale = previousScale;
                if (root != null) Object.Destroy(root);
                if (atlas != null) Object.Destroy(atlas);
                if (mask != null) Object.Destroy(mask);
            }
            yield return null;
        }
    }
}
#endif
