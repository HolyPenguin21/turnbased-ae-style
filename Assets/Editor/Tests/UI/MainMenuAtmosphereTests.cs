#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.EditorTests
{
    public sealed class MainMenuAtmosphereTests
    {
        private GameObject root, menu, collection;
        private MainMenuAtmosphereGraphic graphic;
        private Texture2D atlas, mask;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private void Refresh() => typeof(MainMenuAtmosphereGraphic).GetMethod("LateUpdate", Private).Invoke(graphic, null);
        private int Vertices()
        {
            using (var vh = new VertexHelper())
            {
                typeof(MainMenuAtmosphereGraphic).GetMethod("OnPopulateMesh", Private, null, new[] { typeof(UnityEngine.UI.VertexHelper) }, null).Invoke(graphic, new object[] { vh });
                return vh.currentVertCount;
            }
        }

        [SetUp]
        public void SetUp()
        {
            var shader = Shader.Find("Game/UI/MainMenuAtmosphere");
            Assert.That(shader, Is.Not.Null, "Import MainMenuAtmosphere.shader before running these tests.");
            root = new GameObject("atmosphere tests", typeof(RectTransform), typeof(Canvas));
            root.SetActive(false);
            menu = new GameObject("menu"); menu.transform.SetParent(root.transform);
            collection = new GameObject("collection"); collection.transform.SetParent(root.transform); collection.SetActive(false);
            var go = new GameObject("wind", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(root.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(1600, 900);
            graphic = go.AddComponent<MainMenuAtmosphereGraphic>();
            atlas = new Texture2D(4, 4); mask = new Texture2D(1, 1);
            Set(graphic, "mainMenuPanel", menu); Set(graphic, "collectionWindow", collection);
            Set(graphic, "dustAtlas", atlas); Set(graphic, "architectureMask", mask); Set(graphic, "atmosphereShader", shader);
            root.SetActive(true);
            Refresh();
            Assert.That(Vertices(), Is.GreaterThan(0), "Visible menu must produce an actual atlas mesh.");
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (atlas != null) Object.DestroyImmediate(atlas);
            if (mask != null) Object.DestroyImmediate(mask);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CollectionEmitsNoEffectsFromEitherEntry(bool menuStillActive)
        {
            menu.SetActive(menuStillActive); collection.SetActive(true); Refresh();
            Assert.That(Vertices(), Is.Zero);
            collection.SetActive(false); Refresh();
            Assert.That(Vertices() > 0, Is.EqualTo(menuStillActive));
            menu.SetActive(true); Refresh();
            Assert.That(Vertices(), Is.GreaterThan(0));
        }

        [Test]
        public void SetupAndSettingsHideTheWindWithoutAdvancingItsClock()
        {
            Set(graphic, "elapsed", 17.25d);
            menu.SetActive(false);
            for (int i = 0; i < 20; i++) Refresh();
            Assert.That(Vertices(), Is.Zero);
            Assert.That(typeof(MainMenuAtmosphereGraphic).GetField("elapsed", Private).GetValue(graphic), Is.EqualTo(17.25d));
        }

        [Test]
        public void ReopeningCollectionReusesTheMaterialAndDoesNotCaptureClicks()
        {
            var material = graphic.material;
            for (int i = 0; i < 20; i++)
            {
                collection.SetActive(true); Refresh(); Assert.That(Vertices(), Is.Zero);
                collection.SetActive(false); Refresh(); Assert.That(Vertices(), Is.GreaterThan(0));
                Assert.That(graphic.material, Is.SameAs(material));
            }
            Assert.That(graphic.raycastTarget, Is.False);
        }

        [Test]
        public void MissingPackOrVisibilityReferenceNeverDrawsWhiteQuads()
        {
            Set(graphic, "dustAtlas", null); Refresh(); Assert.That(Vertices(), Is.Zero);
            Set(graphic, "dustAtlas", atlas); Set(graphic, "architectureMask", null); Refresh(); Assert.That(Vertices(), Is.Zero);
            Set(graphic, "architectureMask", mask); Set(graphic, "collectionWindow", null); Refresh(); Assert.That(Vertices(), Is.Zero);
            Set(graphic, "collectionWindow", collection); Refresh(); Assert.That(Vertices(), Is.GreaterThan(0));
        }

        [TestCase(1280, 720)]
        [TestCase(2560, 1080)]
        [TestCase(720, 1280)]
        public void ArchitectureMaskTracksTheBackgroundAtDifferentAspectRatios(int width, int height)
        {
            graphic.rectTransform.sizeDelta = new Vector2(width, height);
            Rect rect = graphic.rectTransform.rect;
            using (var vh = new VertexHelper())
            {
                typeof(MainMenuAtmosphereGraphic).GetMethod("OnPopulateMesh", Private, null, new[] { typeof(UnityEngine.UI.VertexHelper) }, null).Invoke(graphic, new object[] { vh });
                var vertex = UIVertex.simpleVert;
                for (int i = 0; i < vh.currentVertCount; i++)
                {
                    vh.PopulateUIVertex(ref vertex, i);
                    Assert.That(vertex.uv1.x, Is.EqualTo((vertex.position.x - rect.xMin) / rect.width).Within(.00001));
                    Assert.That(vertex.uv1.y, Is.EqualTo((vertex.position.y - rect.yMin) / rect.height).Within(.00001));
                }
            }
        }

        [Test]
        public void DebrisRotatesInTheApprovedScreenSpaceDirection()
        {
            Set(graphic, "elapsed", 7d);
            var sample = MainMenuWindMotion.SampleDebris(7);
            Rect rect = graphic.rectTransform.rect;
            using (var vh = new VertexHelper())
            {
                typeof(MainMenuAtmosphereGraphic).GetMethod("OnPopulateMesh", Private, null, new[] { typeof(UnityEngine.UI.VertexHelper) }, null).Invoke(graphic, new object[] { vh });
                var vertex = UIVertex.simpleVert;
                vh.PopulateUIVertex(ref vertex, vh.currentVertCount - 4);
                float dx = -sample.Width * rect.width * .5f, dy = -sample.Height * rect.height * .5f;
                float cos = Mathf.Cos(sample.Angle), sin = Mathf.Sin(sample.Angle);
                float expectedX = sample.X * rect.width + dx * cos - dy * sin;
                float expectedY = sample.Y * rect.height + dx * sin + dy * cos;
                Assert.That(vertex.position.x - rect.xMin, Is.EqualTo(expectedX).Within(.001));
                Assert.That(rect.yMax - vertex.position.y, Is.EqualTo(expectedY).Within(.001));
            }
        }

        [Test]
        public void ValidationDoesNotDestroyTheLiveMaterial()
        {
            var owned = graphic.material;
            typeof(MainMenuAtmosphereGraphic).GetMethod("OnValidate", Private).Invoke(graphic, null);
            Assert.That(owned != null, Is.True, "OnValidate may run outside a safe native-object lifecycle callback.");
            Assert.That(graphic.material, Is.SameAs(owned));
        }

        [Test]
        public void MeshGenerationDoesNotCreateOrPublishMaterials()
        {
            Object.DestroyImmediate(graphic.material);
            int dirtyCalls = 0;
            graphic.RegisterDirtyMaterialCallback(() => dirtyCalls++);
            Assert.That(Vertices(), Is.Zero, "A missing material must be prepared before the canvas rebuild.");
            Assert.That(dirtyCalls, Is.Zero, "OnPopulateMesh must not enqueue another graphic rebuild.");
            Canvas.ForceUpdateCanvases();
            Assert.That(Vertices(), Is.GreaterThan(0), "The pre-render preparation must restore the material.");
        }

        [Test]
        public void DisableReleasesMaterialBeforeScriptDomainReload()
        {
            var owned = graphic.material;
            graphic.enabled = false;
            Assert.That(owned == null, Is.True, "OnDisable must release transient native objects before private fields are lost to domain reload.");
            Canvas.ForceUpdateCanvases();
            Assert.That(typeof(MainMenuAtmosphereGraphic).GetField("ownedMaterial", Private).GetValue(graphic), Is.Null);
            graphic.enabled = true;
            Assert.That(Vertices(), Is.GreaterThan(0));
        }

        [Test]
        public void GraphicAlphaAlsoFadesTheFlyingScrap()
        {
            Set(graphic, "elapsed", 7d);
            graphic.color = new Color(1, 1, 1, 0);
            using (var vh = new VertexHelper())
            {
                typeof(MainMenuAtmosphereGraphic).GetMethod("OnPopulateMesh", Private, null, new[] { typeof(UnityEngine.UI.VertexHelper) }, null).Invoke(graphic, new object[] { vh });
                Assert.That(vh.currentVertCount, Is.GreaterThan(0));
                var vertex = UIVertex.simpleVert;
                for (int i = 0; i < vh.currentVertCount; i++)
                { vh.PopulateUIVertex(ref vertex, i); Assert.That(vertex.color.a, Is.Zero); }
            }
        }

        [Test]
        public void DestroyingTheGraphicReleasesItsOwnedMaterial()
        {
            var owned = graphic.material;
            Object.DestroyImmediate(graphic.gameObject);
            Assert.That(owned == null, Is.True);
        }
    }
}
#endif
