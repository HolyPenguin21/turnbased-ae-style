#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.Map;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class MapObjectVisualTests
    {
        private GameObject _cameraObject;
        private GameObject _markerObject;
        private Texture2D _texture;
        private Sprite _sprite;

        [TearDown]
        public void TearDown()
        {
            if (_sprite != null) Object.DestroyImmediate(_sprite);
            if (_texture != null) Object.DestroyImmediate(_texture);
            if (_markerObject != null) Object.DestroyImmediate(_markerObject);
            if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
        }

        [Test]
        public void ContainsScreenPoint_ShrinksWithOrthographicZoom()
        {
            _cameraObject = new GameObject("Test camera");
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            camera.pixelRect = new Rect(0f, 0f, 1000f, 1000f);
            camera.transform.position = new Vector3(0f, 0f, -10f);

            _markerObject = new GameObject("Test marker");
            MapObjectVisual visual = _markerObject.AddComponent<MapObjectVisual>();
            SpriteRenderer renderer = _markerObject.AddComponent<SpriteRenderer>();
            _texture = new Texture2D(10, 10);
            _sprite = Sprite.Create(_texture, new Rect(0, 0, 10, 10), new Vector2(0.5f, 0.5f), 10f);
            renderer.sprite = _sprite; // one world unit square
            typeof(MapObjectVisual).GetField("innerCircle", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(visual, renderer);

            Assert.That(visual.ContainsScreenPoint(camera, new Vector2(540f, 500f), 0f), Is.True);

            camera.orthographicSize = 10f;

            Assert.That(visual.ContainsScreenPoint(camera, new Vector2(540f, 500f), 0f), Is.False);
        }
    }
}
#endif
