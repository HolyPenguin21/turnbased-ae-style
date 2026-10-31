#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.Map;
using Game.Players;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class ArmyDestructionEffectTests
    {
        private GameObject _marker;
        private Texture2D _texture;
        private Sprite _sprite;

        [TearDown]
        public void TearDown()
        {
            var effect = GameObject.Find("Army Marker Destruction");
            if (effect != null) Object.DestroyImmediate(effect);
            if (_marker != null) Object.DestroyImmediate(_marker);
            if (_sprite != null) Object.DestroyImmediate(_sprite);
            if (_texture != null) Object.DestroyImmediate(_texture);
        }

        private MapObjectVisual CreateMarker()
        {
            _marker = new GameObject("Army marker");
            var visual = _marker.AddComponent<MapObjectVisual>();
            _texture = new Texture2D(2, 2);
            _texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            _texture.Apply();
            _sprite = Sprite.Create(_texture, new Rect(0, 0, 2, 2),
                new Vector2(0.5f, 0.5f), 2f);

            var icon = new GameObject("Base");
            icon.transform.SetParent(_marker.transform, false);
            var renderer = icon.AddComponent<SpriteRenderer>();
            renderer.sprite = _sprite;
            renderer.color = Color.red;
            renderer.sortingOrder = 10;

            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(MapObjectVisual).GetField("objectImage", flags).SetValue(visual, renderer);
            typeof(MapObjectVisual).GetField("hitRendererOverride", flags).SetValue(visual, renderer);
            return visual;
        }

        [Test]
        public void CapturedArtStillPlaysAfterLiveArmyMarkerIsDestroyed()
        {
            MapObjectVisual marker = CreateMarker();
            var captured = ArmyDestructionEffect.Capture(marker);
            Assert.That(captured, Is.Not.Null);

            marker.SetVisible(false);
            Object.DestroyImmediate(_marker);
            _marker = null;

            ArmyDestructionEffect.Play(captured);
            var effect = GameObject.Find("Army Marker Destruction");
            Assert.That(effect, Is.Not.Null);
            var renderers = effect.GetComponentsInChildren<SpriteRenderer>(true);
            Assert.That(System.Array.Exists(renderers, r => r.sprite == _sprite), Is.True);
            Assert.That(effect.GetComponent<ArmyController>(), Is.Null,
                "the visual echo must never become a selectable army");
        }

        [Test]
        public void ViewerChangeBetweenBattleAndOutcomeCannotRevealOldMarker()
        {
            MapObjectVisual marker = CreateMarker();
            var previousViewer = VisionSystem.CurrentViewer;
            try
            {
                VisionSystem.CurrentViewer = new PlayerSetupData { PlayerId = "first" };
                var captured = ArmyDestructionEffect.Capture(marker);
                Assert.That(captured, Is.Not.Null);
                VisionSystem.CurrentViewer = new PlayerSetupData { PlayerId = "second" };
                ArmyDestructionEffect.Play(captured);
                Assert.That(GameObject.Find("Army Marker Destruction"), Is.Null);
            }
            finally
            {
                VisionSystem.CurrentViewer = previousViewer;
            }
        }

        [Test]
        public void HiddenArmyCannotLeaveAnInformationLeakingDestructionEffect()
        {
            MapObjectVisual marker = CreateMarker();
            marker.SetVisible(false);
            Assert.That(ArmyDestructionEffect.Capture(marker), Is.Null);
            ArmyDestructionEffect.Play(null);
            Assert.That(GameObject.Find("Army Marker Destruction"), Is.Null);
        }

        [TestCase("MapObject_Army_Base", "GroundBase")]
        [TestCase("MapObject_Army_Air_Base", "AirBase")]
        [TestCase("MapObject_Army_FactionAccent", "GroundAccent")]
        [TestCase("MapObject_Army_Air_FactionAccent", "AirAccent")]
        public void BuiltInGroundAndAirMarkersUseActualBakedFrames(string spriteName, string sheetName)
        {
            MapObjectVisual marker = CreateMarker();
            _sprite.name = spriteName;
            var captured = ArmyDestructionEffect.Capture(marker);
            Assert.That(captured, Is.Not.Null);
            Object.DestroyImmediate(_marker);
            _marker = null;
            ArmyDestructionEffect.Play(captured);

            var root = GameObject.Find("Army Marker Destruction");
            Assert.That(root, Is.Not.Null);
            var flipbook = root.GetComponent<ArmyDestructionEffect>();
            var renderer = root.GetComponentInChildren<SpriteRenderer>();
            Assert.That(renderer.sprite, Is.Not.Null, "missing flipbook: " + sheetName);
            Assert.That(renderer.sprite, Is.Not.SameAs(_sprite),
                "built-in marker must use a pre-rendered frame");
            Assert.That(renderer.sprite.texture.width, Is.EqualTo(1280));
            Assert.That(renderer.sprite.texture.height, Is.EqualTo(320));
            Assert.That(renderer.sprite.name, Is.EqualTo(sheetName + "_00"));

            typeof(ArmyDestructionEffect).GetMethod("SetFrame",
                BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(flipbook, new object[] { 8 });
            Assert.That(renderer.sprite.name, Is.EqualTo(sheetName + "_08"),
                "frame-by-frame sprite replacement must advance artwork");
        }

        [Test]
        public void AccentKeepsFactionColorOnFirstFrameThenTurnsToAsh()
        {
            MapObjectVisual marker = CreateMarker();
            _sprite.name = "MapObject_Army_FactionAccent";
            ArmyDestructionEffect.Play(ArmyDestructionEffect.Capture(marker));
            var root = GameObject.Find("Army Marker Destruction");
            var renderer = root.GetComponentInChildren<SpriteRenderer>();
            Assert.That(renderer.color, Is.EqualTo(Color.red));

            typeof(ArmyDestructionEffect).GetMethod("SetFrame",
                BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(root.GetComponent<ArmyDestructionEffect>(), new object[] { 6 });
            Assert.That(renderer.color.r, Is.EqualTo(0.52f).Within(0.001f));
            Assert.That(renderer.color.g, Is.EqualTo(0.52f).Within(0.001f));
        }
    }
}
#endif
