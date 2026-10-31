using System;
using System.Collections.Generic;
using Game.Players;
using UnityEngine;

namespace Game.Map
{
    // Purely visual replay. The encounter may delete its ArmyController immediately;
    // the captured marker is replayed only once the outcome UI closes.
    // The destruction artwork is pre-rendered into four flipbook sheets, not generated
    // or fragmented at runtime. Two layers preserve the owning faction's accent.
    public sealed class ArmyDestructionEffect : MonoBehaviour
    {
        private const float DurationSeconds = 2f;
        private const int FrameCount = 16;
        private const int Columns = 8;
        private const int Rows = 2;
        private const int FramePixels = 160;
        // The four source army marker sprites all import at 100 pixels per unit.
        private const float PixelsPerUnit = 100f;

        private const string ResourceRoot = "ArmyDestruction/";
        private static readonly Dictionary<string, Sprite[]> FrameCache =
            new Dictionary<string, Sprite[]>();

        public sealed class Appearance
        {
            internal readonly Layer[] Layers;
            internal readonly PlayerSetupData Viewer;

            internal Appearance(Layer[] layers, PlayerSetupData viewer)
            {
                Layers = layers;
                Viewer = viewer;
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

        private struct FlipbookLayer
        {
            public SpriteRenderer Renderer;
            public Sprite[] Frames;
            public Color OriginalColor;
            public bool FactionAccent;
        }

        private FlipbookLayer[] _layers;
        private PlayerSetupData _viewer;
        private float _elapsed;
        private int _frame = -1;

        // Capture before combat removes or hides the marker's last living unit.
        // Never create an effect for a marker already hidden by FoW/stacking.
        public static Appearance Capture(MapObjectVisual visual)
        {
            if (visual == null || !visual.IsVisible || !visual.gameObject.activeInHierarchy)
                return null;

            var layers = new List<Layer>();
            foreach (SpriteRenderer renderer in visual.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    renderer.sprite == null)
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
            }

            return layers.Count == 0 ? null :
                new Appearance(layers.ToArray(), VisionSystem.CurrentViewer);
        }

        public static void Play(Appearance appearance)
        {
            if (appearance == null || appearance.Layers.Length == 0 ||
                !ReferenceEquals(appearance.Viewer, VisionSystem.CurrentViewer))
                return;

            var root = new GameObject("Army Marker Destruction");
            root.AddComponent<ArmyDestructionEffect>().Initialize(appearance);
        }

        private void Initialize(Appearance appearance)
        {
            _viewer = appearance.Viewer;
            _layers = new FlipbookLayer[appearance.Layers.Length];

            for (int i = 0; i < appearance.Layers.Length; i++)
            {
                Layer source = appearance.Layers[i];
                var part = new GameObject("Marker Flipbook Layer");
                part.transform.SetParent(transform, false);
                part.transform.SetPositionAndRotation(source.Position, source.Rotation);
                part.transform.localScale = source.Scale;

                var renderer = part.AddComponent<SpriteRenderer>();
                renderer.sharedMaterial = source.Material;
                renderer.sortingLayerID = source.SortingLayer;
                renderer.sortingOrder = source.SortingOrder;
                renderer.flipX = source.FlipX;
                renderer.flipY = source.FlipY;

                string sheetName = SheetFor(source.Sprite);
                Sprite[] frames = sheetName == null ? null : LoadFrames(sheetName);
                _layers[i] = new FlipbookLayer
                {
                    Renderer = renderer,
                    Frames = frames,
                    OriginalColor = source.Color,
                    FactionAccent = sheetName != null && sheetName.EndsWith("Accent", StringComparison.Ordinal)
                };
                // Unknown custom marker layers remain unchanged for compatibility;
                // the built-in Ground/Air art uses the pre-baked flipbooks.
                renderer.sprite = frames != null ? frames[0] : source.Sprite;
                renderer.color = source.Color;
            }

            SetFrame(0);
        }

        private void Update()
        {
            if (_layers == null)
                return;

            // Do not expose an old viewer's marker if ownership/vision changes during playback.
            if (!ReferenceEquals(_viewer, VisionSystem.CurrentViewer))
            {
                Destroy(gameObject);
                return;
            }

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed >= DurationSeconds)
            {
                Destroy(gameObject);
                return;
            }

            SetFrame(Mathf.Clamp(Mathf.FloorToInt(_elapsed * FrameCount / DurationSeconds),
                0, FrameCount - 1));
        }

        // Kept separate from Update so EditMode tests can verify deterministic sampling.
        private void SetFrame(int index)
        {
            if (_frame == index)
                return;
            _frame = index;

            for (int i = 0; i < _layers.Length; i++)
            {
                FlipbookLayer layer = _layers[i];
                if (layer.Frames != null)
                    layer.Renderer.sprite = layer.Frames[index];

                // The source faction accent is colored at runtime. Its pre-rendered
                // damage mask stays separate; only its tint becomes ash-grey.
                if (layer.FactionAccent && layer.Frames != null)
                {
                    Color grey = new Color(0.52f, 0.52f, 0.51f, layer.OriginalColor.a);
                    layer.Renderer.color = Color.Lerp(layer.OriginalColor, grey,
                        Mathf.Clamp01((index - 1) / 3f));
                }
            }
        }

        internal static string SheetFor(Sprite sprite)
        {
            if (sprite == null)
                return null;
            switch (sprite.name)
            {
                case "MapObject_Army_Base": return "GroundBase";
                case "MapObject_Army_FactionAccent": return "GroundAccent";
                case "MapObject_Army_Air_Base": return "AirBase";
                case "MapObject_Army_Air_FactionAccent": return "AirAccent";
                default: return null;
            }
        }

        private static Sprite[] LoadFrames(string sheetName)
        {
            if (FrameCache.TryGetValue(sheetName, out Sprite[] cached))
                return cached;

            Texture2D texture = Resources.Load<Texture2D>(ResourceRoot + sheetName);
            if (texture == null || texture.width != Columns * FramePixels ||
                texture.height != Rows * FramePixels)
            {
                Debug.LogError("[ArmyDestructionEffect] Missing/invalid flipbook " +
                    ResourceRoot + sheetName + " (expected 1280x320 RGBA PNG).");
                return null;
            }

            var frames = new Sprite[FrameCount];
            for (int index = 0; index < FrameCount; index++)
            {
                int column = index % Columns;
                int rowFromTop = index / Columns;
                var rect = new Rect(column * FramePixels,
                    (Rows - 1 - rowFromTop) * FramePixels, FramePixels, FramePixels);
                Sprite frame = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f),
                    PixelsPerUnit, 0, SpriteMeshType.FullRect);
                frame.name = sheetName + "_" + index.ToString("D2");
                frame.hideFlags = HideFlags.DontSave;
                frames[index] = frame;
            }

            FrameCache.Add(sheetName, frames);
            return frames;
        }
    }
}
