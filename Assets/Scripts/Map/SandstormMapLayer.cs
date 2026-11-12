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
    // The rules change a storm instantly; this layer only eases the picture, as a crawl:
    //  - a hex that stays stormy and is not needed by a chain keeps its sprite still;
    //  - every new hex is filled by a chain of one-hex steps: the storm sprite nearest to it
    //    steps in, the sprite behind steps into the hex just vacated, and so on back to a hex the
    //    storm left. No sprite ever moves further than to a neighbouring hex of its own storm, so
    //    it never sweeps across another storm;
    //  - a sprite enters a hex only once the sprite leaving that hex (any storm) is under way;
    //  - hexes no chain can reach fade in, sprites no chain needs fade out where they stand.
    // The whole change settles within SandstormVisualStyle.moveSeconds. The rules never let two
    // storms share a hex, so at rest every stormy hex shows exactly one sprite. Nothing here
    // feeds back into the game.
    [RequireComponent(typeof(HexMap))]
    public class SandstormMapLayer : MonoBehaviour
    {
        // The next step of a chain starts when the step ahead of it is this far along.
        private const float FollowAt = 0.4f;

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

        // One scheduled change of one sprite: a one-hex step, a fade-in or a fade-out.
        private sealed class Change
        {
            public Puff Puff;
            public HexCoord? Leaves;
            public HexCoord? Enters;
            public bool FadeOut;
            public float Start;
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

        // ---- Planning one weather change ------------------------------------------------------------
        private void Place(SandstormSnapshot weather, bool animate)
        {
            var bySlot = new Dictionary<int, Dictionary<HexCoord, Puff>>();
            foreach (Puff puff in _puffs)
            {
                if (puff.Retiring) continue;
                if (!bySlot.TryGetValue(puff.Slot, out var map)) bySlot[puff.Slot] = map = new Dictionary<HexCoord, Puff>();
                map[puff.Hex] = puff;
            }

            var changes = new List<Change>();
            var live = new HashSet<int>();
            if (weather != null)
            {
                foreach (SandstormInstanceView view in weather.Instances)
                {
                    if (view.Body.Count == 0) continue;
                    live.Add(view.SlotId);
                    if (!bySlot.TryGetValue(view.SlotId, out var old)) old = new Dictionary<HexCoord, Puff>();
                    PlanStorm(view, old, changes);
                }
            }
            foreach (var pair in bySlot)
            {
                if (live.Contains(pair.Key)) continue;
                float start = RandomStart();
                foreach (Puff gone in pair.Value.Values)
                    changes.Add(new Change { Puff = gone, Leaves = gone.Hex, FadeOut = true, Start = start });
            }

            Schedule(changes, animate);
        }

        // One storm: chains of one-hex steps from the hexes it left to the hexes it gained.
        private void PlanStorm(SandstormInstanceView view, Dictionary<HexCoord, Puff> old, List<Change> changes)
        {
            var body = new HashSet<HexCoord>(view.Body);
            var left = new List<HexCoord>();          // old sprites whose hex is no longer stormy
            foreach (HexCoord hex in old.Keys) if (!body.Contains(hex)) left.Add(hex);
            var gained = new List<HexCoord>();        // new hexes with no sprite yet
            foreach (HexCoord hex in view.Body) if (!old.ContainsKey(hex)) gained.Add(hex);
            var staying = new HashSet<HexCoord>();    // stormy before and after: chains may pass
            foreach (HexCoord hex in view.Body) if (old.ContainsKey(hex)) staying.Add(hex);

            // Shortest chain first, until no gained hex can be reached from a left one.
            while (gained.Count > 0 && left.Count > 0)
            {
                List<HexCoord> best = null;
                foreach (HexCoord target in gained)
                {
                    List<HexCoord> path = ShortestChain(target, left, staying);
                    if (path != null && (best == null || path.Count < best.Count)) best = path;
                }
                if (best == null) break;
                // best = target, s1, ..., source. Step k moves the sprite on best[k + 1] into best[k].
                float start = RandomStart();
                for (int k = 0; k + 1 < best.Count; k++)
                {
                    Puff mover = old[best[k + 1]];
                    changes.Add(new Change { Puff = mover, Leaves = best[k + 1], Enters = best[k], Start = start });
                    mover.Hex = best[k];
                }
                for (int k = 1; k + 1 < best.Count; k++) staying.Remove(best[k]); // each sprite steps once
                gained.Remove(best[0]);
                left.Remove(best[best.Count - 1]);
            }

            foreach (HexCoord hex in left)
                changes.Add(new Change { Puff = old[hex], Leaves = hex, FadeOut = true, Start = RandomStart() });
            foreach (HexCoord hex in gained)
            {
                Puff puff = Spawn(view.SlotId, hex, _map.HexToWorld(hex));
                changes.Add(new Change { Puff = puff, Enters = hex, Start = RandomStart() });
            }
        }

        // Breadth-first from `target` through hexes the storm keeps (each passable once) to the
        // nearest hex it left; neighbours only. Returns target..source, or null.
        private static List<HexCoord> ShortestChain(HexCoord target, List<HexCoord> left, HashSet<HexCoord> staying)
        {
            var sources = new HashSet<HexCoord>(left);
            var cameFrom = new Dictionary<HexCoord, HexCoord> { [target] = target };
            var queue = new Queue<HexCoord>();
            queue.Enqueue(target);
            while (queue.Count > 0)
            {
                HexCoord hex = queue.Dequeue();
                for (int d = 0; d < 6; d++)
                {
                    HexCoord next = SandstormRules.Step(hex, d);
                    if (cameFrom.ContainsKey(next)) continue;
                    if (sources.Contains(next))
                    {
                        var path = new List<HexCoord> { next };
                        for (HexCoord at = hex; ; at = cameFrom[at])
                        {
                            path.Add(at);
                            if (at.Equals(target)) break;
                        }
                        path.Reverse();
                        return path;
                    }
                    if (!staying.Contains(next)) continue;
                    cameFrom[next] = hex;
                    queue.Enqueue(next);
                }
            }
            return null;
        }

        private float StepSeconds => _moveSeconds * _spriteMoveShare;

        private float RandomStart() => (float)_random.NextDouble() * Mathf.Max(0f, _moveSeconds - StepSeconds);

        // A sprite enters a hex only once the sprite leaving it is FollowAt under way (this also
        // orders every chain front to back), then the whole change is squeezed into moveSeconds.
        private void Schedule(List<Change> changes, bool animate)
        {
            float now = Time.time;
            float step = StepSeconds;
            if (!animate || _moveSeconds <= 0f)
            {
                foreach (Change c in changes) Apply(c, now, 0f);
                return;
            }

            var leaver = new Dictionary<HexCoord, Change>();
            foreach (Change c in changes)
                if (c.Leaves.HasValue) leaver[c.Leaves.Value] = c;
            // Relaxation; a cycle (two storms swapping hexes) just stops tightening after the cap.
            for (int pass = 0; pass < 16; pass++)
            {
                bool changed = false;
                foreach (Change c in changes)
                {
                    if (!c.Enters.HasValue || !leaver.TryGetValue(c.Enters.Value, out Change before) || before == c) continue;
                    float earliest = before.Start + FollowAt * step;
                    if (c.Start < earliest - 1e-4f) { c.Start = earliest; changed = true; }
                }
                if (!changed) break;
            }

            float end = 0f;
            foreach (Change c in changes) end = Mathf.Max(end, c.Start + step);
            float squeeze = end > _moveSeconds ? _moveSeconds / end : 1f;
            foreach (Change c in changes) Apply(c, now + c.Start * squeeze, step * squeeze);
        }

        private void Apply(Change change, float start, float duration)
        {
            Puff puff = change.Puff;
            if (change.FadeOut) puff.Retiring = true;
            puff.From = puff.Current;
            puff.FromAlpha = puff.CurrentAlpha;
            puff.To = change.FadeOut ? puff.Current : _map.HexToWorld(puff.Hex);
            puff.ToAlpha = change.FadeOut ? 0f : 1f;
            puff.Start = start;
            puff.Duration = duration;
        }

        // ---- Sprites ----------------------------------------------------------------------------------
        private Puff Spawn(int slot, HexCoord hex, Vector3 position)
        {
            SpriteRenderer renderer = _idle.Count > 0 ? _idle.Pop() : CreateRenderer();
            int phaseSlots = _frames.Length > 0 ? Mathf.Max(1, (_frames.Length + _startFrameStep - 1) / _startFrameStep) : 1;
            var puff = new Puff
            {
                Renderer = renderer, Slot = slot, Hex = hex,
                Phase = _frames.Length > 0 ? _random.Next(phaseSlots) * _startFrameStep % _frames.Length : 0,
                Current = position, To = position, CurrentAlpha = 0f,
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
