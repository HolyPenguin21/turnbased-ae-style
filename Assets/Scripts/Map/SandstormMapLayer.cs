using System.Collections.Generic;
using Game.HexGrid;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Presentation of the public weather published on HexMap.Weather (owner: SandstormState):
    // one flat sprite per hex of every storm at MapSortingOrder.Sandstorm. Weather is public map
    // data, like terrain, so this layer is never vision-gated; the fog overlay (drawn above it)
    // dims it over hexes the viewer can't see, exactly as it dims the terrain. Added and
    // configured at runtime by HexMapGenerator next to MapTerrainAnimator.
    //
    // The rules change a storm instantly; this layer only eases the picture. Per storm, a sprite
    // whose hex stays stormy does not move; the other sprites pair up with the new hexes nearest
    // first and glide there, each at its own random moment within
    // SandstormVisualStyle.moveSeconds; sprites left over fade out where they stand, hexes left
    // over fade in. The rules never let two storms share a hex, so at rest every stormy hex shows
    // exactly one sprite. Nothing here feeds back into the game.
    [RequireComponent(typeof(HexMap))]
    public class SandstormMapLayer : MonoBehaviour
    {
        private sealed class Puff
        {
            public SpriteRenderer Renderer;
            public int Slot;
            public HexCoord Hex;
            public int Phase;
            public Vector3 From, To, Current;
            public float FromAlpha, ToAlpha, CurrentAlpha;
            public float Start, Duration;
            public bool Retiring;
        }

        private HexMap _map;
        private Sprite[] _frames = new Sprite[0];
        private float _framesPerSecond;
        private float _sizeInHexes = 1f;
        private Color _tint = Color.white;
        private int _startFrameStep = 2;
        private float _moveSeconds = 5f;
        private float _spriteMoveShare = 0.5f;

        private SandstormSnapshot _shown;
        private Transform _root;
        private readonly List<Puff> _puffs = new List<Puff>();
        private readonly Stack<SpriteRenderer> _idle = new Stack<SpriteRenderer>();
        // Visual-only randomness: never UnityEngine.Random, whose shared state other systems use.
        private readonly System.Random _random = new System.Random();
        private int _lastFrame = -1;

        // Copies the tunables (the style is the shared GameConfig asset's instance).
        public void Configure(SandstormVisualStyle style)
        {
            _frames = style?.frames != null ? (Sprite[])style.frames.Clone() : new Sprite[0];
            _framesPerSecond = style != null ? Mathf.Max(0f, style.framesPerSecond) : 0f;
            _sizeInHexes = style != null ? Mathf.Max(0.01f, style.sizeInHexes) : 1f;
            _tint = style != null ? style.tint : Color.white;
            _startFrameStep = style != null ? Mathf.Max(1, style.startFrameStep) : 2;
            _moveSeconds = style != null ? Mathf.Max(0f, style.moveSeconds) : 5f;
            _spriteMoveShare = style != null ? Mathf.Clamp(style.spriteMoveShare, 0.05f, 1f) : 0.5f;
            // A new map (or a loaded match) shows its weather at once, without a glide.
            for (int i = _puffs.Count - 1; i >= 0; i--) Release(i);
            _shown = null;
            _lastFrame = -1;
        }

        private void LateUpdate()
        {
            if (_map == null) _map = GetComponent<HexMap>();
            SandstormSnapshot weather = _map != null ? _map.Weather : null;
            if (!ReferenceEquals(weather, _shown))
            {
                bool animate = _shown != null;
                _shown = weather;
                Place(weather, animate);
                _lastFrame = -1;
            }
            Move();
            Animate();
        }

        private void Place(SandstormSnapshot weather, bool animate)
        {
            float now = Time.time;
            var bySlot = new Dictionary<int, List<Puff>>();
            foreach (Puff puff in _puffs)
            {
                if (puff.Retiring) continue;
                if (!bySlot.TryGetValue(puff.Slot, out List<Puff> list)) bySlot[puff.Slot] = list = new List<Puff>();
                list.Add(puff);
            }

            var live = new HashSet<int>();
            if (weather != null)
            {
                foreach (SandstormInstanceView view in weather.Instances)
                {
                    if (view.Body.Count == 0) continue;
                    live.Add(view.SlotId);
                    if (!bySlot.TryGetValue(view.SlotId, out List<Puff> old)) old = new List<Puff>();

                    // 1. A hex that stays stormy keeps its sprite where it is.
                    var fresh = new List<HexCoord>();
                    foreach (HexCoord cell in view.Body)
                    {
                        int same = old.FindIndex(p => p.Hex.Equals(cell));
                        if (same < 0) { fresh.Add(cell); continue; }
                        Retarget(old[same], _map.HexToWorld(cell), 1f, animate, now);
                        old.RemoveAt(same);
                    }

                    // 2. Freed sprites glide to the new hexes, the closest pair first.
                    while (fresh.Count > 0 && old.Count > 0)
                    {
                        int bestCell = 0, bestPuff = 0;
                        float best = float.MaxValue;
                        for (int c = 0; c < fresh.Count; c++)
                        {
                            Vector3 target = _map.HexToWorld(fresh[c]);
                            for (int p = 0; p < old.Count; p++)
                            {
                                float distance = (old[p].Current - target).sqrMagnitude;
                                if (distance >= best) continue;
                                best = distance;
                                bestCell = c;
                                bestPuff = p;
                            }
                        }
                        Puff moving = old[bestPuff];
                        moving.Hex = fresh[bestCell];
                        Retarget(moving, _map.HexToWorld(fresh[bestCell]), 1f, animate, now);
                        old.RemoveAt(bestPuff);
                        fresh.RemoveAt(bestCell);
                    }

                    // 3. The storm grew: new sprites fade in on their hexes.
                    foreach (HexCoord cell in fresh)
                    {
                        Vector3 target = _map.HexToWorld(cell);
                        Retarget(Spawn(view.SlotId, cell, target), target, 1f, animate, now);
                    }

                    // 4. The storm shrank: the sprites left over fade out where they stand.
                    foreach (Puff gone in old) Retire(gone, animate, now);
                }
            }

            foreach (var pair in bySlot)
            {
                if (live.Contains(pair.Key)) continue;
                foreach (Puff gone in pair.Value) Retire(gone, animate, now);
            }
        }

        private void Retarget(Puff puff, Vector3 to, float alpha, bool animate, float now)
        {
            puff.From = puff.Current;
            puff.FromAlpha = puff.CurrentAlpha;
            puff.To = to;
            puff.ToAlpha = alpha;
            float duration = animate ? _moveSeconds * _spriteMoveShare : 0f;
            float window = animate ? Mathf.Max(0f, _moveSeconds - duration) : 0f;
            puff.Duration = duration;
            puff.Start = now + (float)_random.NextDouble() * window;
        }

        private void Retire(Puff puff, bool animate, float now)
        {
            puff.Retiring = true;
            Retarget(puff, puff.Current, 0f, animate, now);
        }

        private Puff Spawn(int slot, HexCoord hex, Vector3 position)
        {
            SpriteRenderer renderer = _idle.Count > 0 ? _idle.Pop() : CreateRenderer();
            int phaseSlots = _frames.Length > 0 ? Mathf.Max(1, (_frames.Length + _startFrameStep - 1) / _startFrameStep) : 1;
            var puff = new Puff
            {
                Renderer = renderer, Slot = slot, Hex = hex,
                Phase = _frames.Length > 0 ? _random.Next(phaseSlots) * _startFrameStep % _frames.Length : 0,
                Current = position, CurrentAlpha = 0f,
            };
            renderer.sprite = null;
            renderer.transform.position = position;
            renderer.color = WithAlpha(0f);
            renderer.enabled = true;
            _puffs.Add(puff);
            return puff;
        }

        private void Release(int index)
        {
            SpriteRenderer renderer = _puffs[index].Renderer;
            renderer.enabled = false;
            _idle.Push(renderer);
            _puffs.RemoveAt(index);
        }

        private void Move()
        {
            float now = Time.time;
            for (int i = _puffs.Count - 1; i >= 0; i--)
            {
                Puff puff = _puffs[i];
                bool settled = puff.Current == puff.To && puff.CurrentAlpha == puff.ToAlpha;
                if (!settled)
                {
                    float t = puff.Duration > 0f ? Mathf.Clamp01((now - puff.Start) / puff.Duration) : 1f;
                    float eased = Mathf.SmoothStep(0f, 1f, t);
                    puff.Current = t >= 1f ? puff.To : Vector3.Lerp(puff.From, puff.To, eased);
                    puff.CurrentAlpha = t >= 1f ? puff.ToAlpha : Mathf.Lerp(puff.FromAlpha, puff.ToAlpha, eased);
                    puff.Renderer.transform.position = puff.Current;
                    puff.Renderer.color = WithAlpha(puff.CurrentAlpha);
                    settled = t >= 1f;
                }
                if (settled && puff.Retiring) Release(i);
            }
        }

        private Color WithAlpha(float alpha)
        {
            Color color = _tint;
            color.a *= alpha;
            return color;
        }

        private void Animate()
        {
            if (_puffs.Count == 0 || _frames.Length == 0) return;
            int frame = _frames.Length > 1 && _framesPerSecond > 0f ? (int)(Time.time * _framesPerSecond) : 0;
            if (frame == _lastFrame) return;
            _lastFrame = frame;
            foreach (Puff puff in _puffs)
            {
                Sprite sprite = _frames[(frame + puff.Phase) % _frames.Length];
                SpriteRenderer renderer = puff.Renderer;
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
            return renderer;
        }
    }
}
