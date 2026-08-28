using Game.Styles;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // UI-space counterpart to Game.Map.HexShaderHighlight — same ragged noise-driven edge +
    // animated glow technique (Custom/UIRaggedGlow.shader, adapted from HexSelectionGlow.shader
    // for a rectangle instead of a hexagon), just drawn around a UGUI RectTransform instead of a
    // world-space mesh. Sized bigger than the cell it's highlighting (see ShowAt) so the ragged
    // edge/glow has room to bleed outward past the crisp ring, same reasoning as
    // HexShaderHighlight.BuildQuad's own margin.
    [RequireComponent(typeof(RectTransform), typeof(Image))]
    public class UIRaggedGlowUI : MonoBehaviour
    {
        [SerializeField] private RectTransform rectTransform;
        [SerializeField] private Image image;
        [SerializeField] private Color color = TechnicalColors.BattleActingUnit;

        private static readonly int TrueSizeId = Shader.PropertyToID("_TrueSize");
        private static readonly int RectSizeId = Shader.PropertyToID("_RectSize");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int LineThicknessId = Shader.PropertyToID("_LineThickness");
        private static readonly int NoiseReachId = Shader.PropertyToID("_NoiseReach");
        private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
        private static readonly int NoiseSpeedId = Shader.PropertyToID("_NoiseSpeed");
        private static readonly int GlowIntensityId = Shader.PropertyToID("_GlowIntensity");
        private static readonly int GlowWidthId = Shader.PropertyToID("_GlowWidth");
        private static readonly int RadiusScaleId = Shader.PropertyToID("_RadiusScale");

        private HexHighlightStyle _style = new HexHighlightStyle();
        private Material _material;
        private bool _initialized;

        private void Awake()
        {
            EnsureInitialized();
        }

        // Idempotent one-time setup — resolve the RectTransform/Image, find the shader, build
        // the unique Material and clear the sprite. Deliberately does NOT touch visibility.
        //
        // BattleGridCell authors its ActingHighlight child inactive (prefab m_IsActive: 0), and
        // the Battle grid constantly re-instantiates those cells, so the FIRST call on a fresh
        // instance is ShowAt() — its own gameObject.SetActive(true) is what finally runs this
        // component's Awake(), synchronously, mid-ShowAt(). If Awake() also hid the object here
        // (the old behaviour), that first ShowAt() switched itself straight back off and the
        // acting-unit glow never appeared. Initial hidden state now comes only from the prefab;
        // both Awake() and ShowAt() route through this so init never depends on call order.
        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            _initialized = true;

            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
            if (image == null)
                image = GetComponent<Image>();

            Shader shader = Shader.Find("Custom/UIRaggedGlow");
            if (shader == null)
                Debug.LogWarning("UIRaggedGlowUI: shader 'Custom/UIRaggedGlow' not found.");
            // A unique Material instance (not a MaterialPropertyBlock — CanvasRenderer-driven
            // Graphics don't support those the way a MeshRenderer does) — this only ever
            // highlights one cell at a time, so losing UI batching for it is a non-issue.
            _material = new Material(shader);
            image.material = _material;
            // No sprite — an Image with none still renders as a plain shaded rect (same
            // convention as ArmyUnitCardUI's EmptySlotColor), which is all the shader needs:
            // it only reads the mesh's own UV, not any texture.
            image.sprite = null;
        }

        // Copies the values rather than keeping the reference — see HexShaderHighlight.
        // ApplyStyle's identical reasoning (typically a shared GameConfig asset's own instance).
        public void ApplyStyle(HexHighlightStyle style)
        {
            if (style == null)
                return;
            _style = new HexHighlightStyle
            {
                radiusScale = style.radiusScale,
                margin = style.margin,
                lineThickness = style.lineThickness,
                noiseReach = style.noiseReach,
                noiseScale = style.noiseScale,
                noiseSpeed = style.noiseSpeed,
                glowIntensity = style.glowIntensity,
                glowWidth = style.glowWidth,
            };
        }

        public void SetColor(Color newColor)
        {
            color = newColor;
            if (_material != null)
                _material.SetColor(ColorId, color);
        }

        // trueSize is the real cell size the ring is drawn at (e.g. BattleGridCellUI's own
        // fixed GridLayoutGroup cell size) — this object's own rect is padded beyond that by
        // the style's margin so the ragged edge/glow has room to render past the crisp ring.
        public void ShowAt(Vector2 trueSize)
        {
            // May be the very first thing to run on a freshly instantiated (prefab-inactive)
            // cell — build the Material before touching it (see EnsureInitialized).
            EnsureInitialized();

            Vector2 rectSize = trueSize + Vector2.one * (2f * _style.margin);
            if (rectTransform != null)
                rectTransform.sizeDelta = rectSize;

            if (_material != null)
            {
                _material.SetVector(TrueSizeId, trueSize);
                _material.SetVector(RectSizeId, rectSize);
                _material.SetColor(ColorId, color);
                _material.SetFloat(RadiusScaleId, _style.radiusScale);
                _material.SetFloat(LineThicknessId, _style.lineThickness);
                _material.SetFloat(NoiseReachId, _style.noiseReach);
                _material.SetFloat(NoiseScaleId, _style.noiseScale);
                _material.SetFloat(NoiseSpeedId, _style.noiseSpeed);
                _material.SetFloat(GlowIntensityId, _style.glowIntensity);
                _material.SetFloat(GlowWidthId, _style.glowWidth);
            }

            gameObject.SetActive(true);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }
    }
}
