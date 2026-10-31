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
    }
}
#endif
