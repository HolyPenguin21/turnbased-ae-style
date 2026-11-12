using System.Collections.Generic;
using Game.HexGrid;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Presentation of the public weather published on HexMap.Weather (owner: SandstormState):
    // one flat sprite per stormy hex at MapSortingOrder.Sandstorm. Weather is public map data,
    // like terrain, so this layer is never vision-gated; the fog overlay (drawn above it)
    // dims it over hexes the viewer can't see, exactly as it dims the terrain. Added and
    // configured at runtime by HexMapGenerator next to MapTerrainAnimator.
    [RequireComponent(typeof(HexMap))]
    public class SandstormMapLayer : MonoBehaviour
    {
        private HexMap _map;
        private Sprite[] _frames = new Sprite[0];
        private float _framesPerSecond;
        private float _sizeInHexes = 1f;
        private Color _tint = Color.white;

        private SandstormSnapshot _shown;
        private Transform _root;
        private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
        private readonly List<int> _phases = new List<int>();
        private int _activeCount;
        private int _lastFrame = -1;

        // Copies the tunables (the style is the shared GameConfig asset's instance).
        public void Configure(SandstormVisualStyle style)
        {
            _frames = style?.frames != null ? (Sprite[])style.frames.Clone() : new Sprite[0];
            _framesPerSecond = style != null ? Mathf.Max(0f, style.framesPerSecond) : 0f;
            _sizeInHexes = style != null ? Mathf.Max(0.01f, style.sizeInHexes) : 1f;
            _tint = style != null ? style.tint : Color.white;
            _shown = null;
            _lastFrame = -1;
        }

        private void LateUpdate()
        {
            if (_map == null) _map = GetComponent<HexMap>();
            SandstormSnapshot weather = _map != null ? _map.Weather : null;
            if (!ReferenceEquals(weather, _shown))
            {
                _shown = weather;
                Place(weather);
                _lastFrame = -1;
            }
            Animate();
        }

        private void Place(SandstormSnapshot weather)
        {
            IReadOnlyList<HexCoord> cells = weather != null ? weather.Cells : (IReadOnlyList<HexCoord>)new HexCoord[0];
            for (int i = 0; i < cells.Count; i++)
            {
                SpriteRenderer renderer = i < _renderers.Count ? _renderers[i] : CreateRenderer();
                HexCoord hex = cells[i];
                renderer.transform.position = _map.HexToWorld(hex);
                renderer.color = _tint;
                // Spread the loop start per hex so neighbouring tiles don't pulse in lockstep.
                _phases[i] = _frames.Length > 0 ? (int)((uint)(hex.Q * 73856093 ^ hex.R * 19349663) % (uint)_frames.Length) : 0;
                renderer.enabled = true;
            }
            for (int i = cells.Count; i < _renderers.Count; i++)
                _renderers[i].enabled = false;
            _activeCount = cells.Count;
        }

        private void Animate()
        {
            if (_activeCount == 0 || _frames.Length == 0) return;
            int frame = _frames.Length > 1 && _framesPerSecond > 0f ? (int)(Time.time * _framesPerSecond) : 0;
            if (frame == _lastFrame) return;
            _lastFrame = frame;
            for (int i = 0; i < _activeCount; i++)
            {
                Sprite sprite = _frames[(frame + _phases[i]) % _frames.Length];
                SpriteRenderer renderer = _renderers[i];
                if (renderer.sprite == sprite) continue;
                renderer.sprite = sprite;
                float width = sprite != null ? sprite.bounds.size.x : 0f;
                float scale = width > 0f ? 2f * _map.OuterRadius * _sizeInHexes / width : 1f;
                renderer.transform.localScale = new Vector3(scale, scale, 1f);
            }
        }

        private SpriteRenderer CreateRenderer()
        {
            if (_root == null)
            {
                _root = new GameObject("SandstormLayer").transform;
                _root.SetParent(transform, worldPositionStays: false);
            }
            var go = new GameObject("Sandstorm");
            go.transform.SetParent(_root, worldPositionStays: false);
            // Flat on the map plane, same convention as every flat map decal (Euler 90,0,0).
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sortingOrder = MapSortingOrder.Sandstorm;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _renderers.Add(renderer);
            _phases.Add(0);
            return renderer;
        }
    }
}
