using System.Collections.Generic;
using UnityEngine;

namespace Game.Map
{
    // A detached, non-interactive visual echo of an army marker. The battle can delete the
    // original ArmyController immediately; the map plays this effect only after its outcome
    // window closes. No army/vision/selection state is retained or modified here.
    public sealed class ArmyDestructionEffect : MonoBehaviour
    {
        private const float DurationSeconds = 2f;
        private const int AshCount = 12;

        public sealed class Appearance
        {
            internal readonly Layer[] Layers;
            internal readonly Vector3 Centre;
            internal readonly float Radius;

            internal Appearance(Layer[] layers, Vector3 centre, float radius)
            {
                Layers = layers;
                Centre = centre;
                Radius = radius;
            }
        }

        internal struct Layer
        {
            public Sprite Sprite;
            public Material Material;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Color Color;
            public int SortingLayer;
            public int SortingOrder;
            public bool FlipX;
            public bool FlipY;
        }

        private struct Piece
        {
            public SpriteRenderer Renderer;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Vector3 Drift;
            public float Spin;
            public Color Color;
        }

        private struct Ash
        {
            public SpriteRenderer Renderer;
            public Vector3 Origin;
            public Vector3 Drift;
            public Color Color;
        }

        private static Sprite _ashSprite;
        private Piece[] _pieces;
        private Ash[] _ash;
        private float _elapsed;
        private float _radius;

        // Capture BEFORE the battle removes or hides its last unit. In particular, an army
        // hidden by FOW or by another representative of the same owner must not leave a
        // destruction effect that reveals its existence to the map viewer.
        public static Appearance Capture(MapObjectVisual visual)
        {
            if (visual == null || !visual.IsVisible || !visual.gameObject.activeInHierarchy)
                return null;

            var layers = new List<Layer>();
            Bounds bounds = default;
            foreach (SpriteRenderer renderer in visual.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || renderer.sprite == null)
                    continue;

                layers.Add(new Layer
                {
                    Sprite = renderer.sprite,
                    Material = renderer.sharedMaterial,
                    Position = renderer.transform.position,
                    Rotation = renderer.transform.rotation,
                    Scale = renderer.transform.lossyScale,
                    Color = renderer.color,
                    SortingLayer = renderer.sortingLayerID,
                    SortingOrder = renderer.sortingOrder,
                    FlipX = renderer.flipX,
                    FlipY = renderer.flipY
                });
                if (layers.Count == 1) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
            }

            if (layers.Count == 0)
                return null;

            return new Appearance(layers.ToArray(), bounds.center,
                Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, bounds.extents.z)));
        }

        public static void Play(Appearance appearance)
        {
            if (appearance == null || appearance.Layers.Length == 0)
                return;

            var root = new GameObject("Army Marker Destruction");
            root.AddComponent<ArmyDestructionEffect>().Initialize(appearance);
        }

        private void Initialize(Appearance appearance)
        {
            _radius = appearance.Radius;
            _pieces = new Piece[appearance.Layers.Length];
            int topSortingOrder = int.MinValue;
            int topSortingLayer = 0;
            for (int i = 0; i < appearance.Layers.Length; i++)
            {
                Layer layer = appearance.Layers[i];
                var partObject = new GameObject("Marker Layer");
                partObject.transform.SetParent(transform, false);
                partObject.transform.SetPositionAndRotation(layer.Position, layer.Rotation);
                partObject.transform.localScale = layer.Scale;

                var renderer = partObject.AddComponent<SpriteRenderer>();
                renderer.sprite = layer.Sprite;
                renderer.sharedMaterial = layer.Material;
                renderer.color = layer.Color;
                renderer.sortingLayerID = layer.SortingLayer;
                renderer.sortingOrder = layer.SortingOrder;
                renderer.flipX = layer.FlipX;
                renderer.flipY = layer.FlipY;

                float angle = (i + 0.5f) * 2.39996f;
                _pieces[i] = new Piece
                {
                    Renderer = renderer,
                    Position = partObject.transform.localPosition,
                    Rotation = partObject.transform.localRotation,
                    Scale = partObject.transform.localScale,
                    Drift = new Vector3(Mathf.Cos(angle), -0.28f, Mathf.Sin(angle)) * _radius * 0.28f,
                    Spin = i % 2 == 0 ? -16f : 17f,
                    Color = layer.Color
                };
                if (layer.SortingOrder > topSortingOrder)
                {
                    topSortingOrder = layer.SortingOrder;
                    topSortingLayer = layer.SortingLayer;
                }
            }

            Sprite ashSprite = GetAshSprite();
            _ash = new Ash[AshCount];
            for (int i = 0; i < _ash.Length; i++)
            {
                float angle = 2f * Mathf.PI * (i + 0.37f) / AshCount;
                float variation = 0.65f + 0.35f * (i % 3);
                var particleObject = new GameObject("Ash");
                particleObject.transform.SetParent(transform, false);
                particleObject.transform.position = appearance.Centre;
                particleObject.transform.rotation = appearance.Layers[0].Rotation;
                particleObject.transform.localScale = Vector3.one * _radius * (0.055f + 0.018f * (i % 3));
                var renderer = particleObject.AddComponent<SpriteRenderer>();
                renderer.sprite = ashSprite;
                renderer.sortingLayerID = topSortingLayer;
                renderer.sortingOrder = topSortingOrder + 1;
                renderer.enabled = false;
                _ash[i] = new Ash
                {
                    Renderer = renderer,
                    Origin = particleObject.transform.localPosition,
                    Drift = new Vector3(Mathf.Cos(angle), 0.12f + 0.14f * variation,
                        Mathf.Sin(angle)) * _radius * variation,
                    Color = new Color(0.39f + 0.05f * (i % 3), 0.38f + 0.05f * (i % 3),
                        0.35f + 0.05f * (i % 3), 0.75f)
                };
            }
        }

        private void Update()
        {
            if (_pieces == null) return;

            _elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_elapsed / DurationSeconds);
            float desaturate = Ease(t, 0f, 0.3f);
            float crumble = Ease(t, 0.35f, 0.95f);
            float fade = 1f - Ease(t, 0.63f, 1f);

            foreach (Piece piece in _pieces)
            {
                var grey = new Color(0.40f, 0.40f, 0.39f, piece.Color.a);
                Color color = Color.Lerp(piece.Color, grey, desaturate);
                color.a *= fade;
                piece.Renderer.color = color;
                Transform part = piece.Renderer.transform;
                part.localPosition = piece.Position + piece.Drift * crumble
                    + Vector3.down * (_radius * 0.22f * crumble);
                part.localRotation = piece.Rotation * Quaternion.Euler(0f, 0f, piece.Spin * crumble);
                part.localScale = piece.Scale * (1f - 0.52f * crumble);
            }

            float ashProgress = Mathf.Clamp01((t - 0.29f) / 0.71f);
            float ashOpacity = Mathf.Sin(ashProgress * Mathf.PI);
            foreach (Ash particle in _ash)
            {
                bool active = ashOpacity > 0.001f;
                particle.Renderer.enabled = active;
                if (!active) continue;
                particle.Renderer.transform.localPosition = particle.Origin
                    + particle.Drift * ashProgress
                    + Vector3.down * (_radius * 0.36f * ashProgress * ashProgress);
                Color color = particle.Color;
                color.a *= ashOpacity;
                particle.Renderer.color = color;
            }

            if (t >= 1f)
                Destroy(gameObject);
        }

        private static float Ease(float t, float start, float end) =>
            Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, end, t));

        private static Sprite GetAshSprite()
        {
            if (_ashSprite != null) return _ashSprite;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            texture.Apply();
            _ashSprite = Sprite.Create(texture, new Rect(0, 0, 2, 2),
                new Vector2(0.5f, 0.5f), 2f);
            _ashSprite.hideFlags = HideFlags.DontSave;
            return _ashSprite;
        }
    }
}
