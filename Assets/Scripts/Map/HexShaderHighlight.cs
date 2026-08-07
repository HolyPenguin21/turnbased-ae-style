using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Selected-hex highlight: the hex outline, a ragged noise-driven edge, and an animated
    // glow are all computed per-pixel in HexSelectionGlow.shader instead of baked into
    // geometry. The mesh is a flat quad built in the object's own LOCAL space, centered on
    // the hex with a generous fixed world-space margin (not a UV-space budget — that
    // previously left too little room for the ragged edge/glow and clipped them at the
    // quad's boundary). _OuterRadius is set to the map's real hex size, not a guessed
    // constant, so the rendered ring always lands exactly on the actual hex boundary.
    //
    // Tunables (noise/glow/margin) come from a HexHighlightStyle applied via ApplyStyle —
    // GameConfig holds one per usage context (region highlight, citadel pick, hex selection)
    // so each can look different instead of every instance sharing the shader's own defaults.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HexShaderHighlight : MonoBehaviour
    {
        [SerializeField] private Color color = new Color(1f, 0.35f, 0.1f, 1f);

        private static readonly int OuterRadiusId = Shader.PropertyToID("_OuterRadius");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int LineThicknessId = Shader.PropertyToID("_LineThickness");
        private static readonly int NoiseReachId = Shader.PropertyToID("_NoiseReach");
        private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
        private static readonly int NoiseSpeedId = Shader.PropertyToID("_NoiseSpeed");
        private static readonly int GlowIntensityId = Shader.PropertyToID("_GlowIntensity");
        private static readonly int GlowWidthId = Shader.PropertyToID("_GlowWidth");
        private static readonly int RadiusScaleId = Shader.PropertyToID("_RadiusScale");

        private HexHighlightStyle _style = new HexHighlightStyle();
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Material _material;
        private MaterialPropertyBlock _propertyBlock;
        private float _builtRadius = -1f;

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            Shader shader = Shader.Find("Custom/HexSelectionGlow");
            if (shader == null)
                Debug.LogWarning("HexShaderHighlight: shader 'Custom/HexSelectionGlow' not found.");
            _material = new Material(shader);
            _renderer.sharedMaterial = _material;
            _propertyBlock = new MaterialPropertyBlock();

            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            Hide();
        }

        // Call once, right after creating the instance — sets the noise/glow tunables and
        // margin for this usage context (typically gameConfig.<...>Style). Safe to call before
        // the first ShowAt. Copies the values rather than keeping the reference, since that
        // reference is often the shared GameConfig asset's own instance, which must never be
        // written back to at runtime.
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
                sortingOrder = style.sortingOrder,
            };
            _builtRadius = -1f; // margin may have changed — force the mesh to rebuild
            SetSortingOrder(_style.sortingOrder);
        }

        public void ShowAt(Vector3 center, float outerRadius)
        {
            if (!Mathf.Approximately(outerRadius, _builtRadius))
            {
                _filter.mesh = BuildQuad(outerRadius + _style.margin);
                _builtRadius = outerRadius;
            }

            transform.position = center;

            _renderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetFloat(OuterRadiusId, outerRadius);
            _propertyBlock.SetFloat(RadiusScaleId, _style.radiusScale);
            _propertyBlock.SetColor(ColorId, color);
            _propertyBlock.SetFloat(LineThicknessId, _style.lineThickness);
            _propertyBlock.SetFloat(NoiseReachId, _style.noiseReach);
            _propertyBlock.SetFloat(NoiseScaleId, _style.noiseScale);
            _propertyBlock.SetFloat(NoiseSpeedId, _style.noiseSpeed);
            _propertyBlock.SetFloat(GlowIntensityId, _style.glowIntensity);
            _propertyBlock.SetFloat(GlowWidthId, _style.glowWidth);
            _renderer.SetPropertyBlock(_propertyBlock);

            _renderer.enabled = true;
        }

        // Vertices are LOCAL to this object (centered at its own origin) — the vertex shader
        // reads them directly as hex-relative coordinates, and TransformObjectToHClip alone
        // handles placing them correctly in the world via this object's transform.position.
        private static Mesh BuildQuad(float half)
        {
            var mesh = new Mesh { name = "HexShaderQuad" };
            mesh.SetVertices(new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3(half, 0f, -half),
                new Vector3(-half, 0f, half),
                new Vector3(half, 0f, half),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Hide()
        {
            if (_renderer != null)
                _renderer.enabled = false;
        }

        public void SetColor(Color newColor)
        {
            color = newColor;
            if (_renderer == null || _propertyBlock == null)
                return;
            _renderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor(ColorId, color);
            _renderer.SetPropertyBlock(_propertyBlock);
        }

        // Both this and HexClusterHighlight render on transparent MeshRenderers — sortingOrder
        // is what resolves draw order between them; everything sits flat at the same height.
        public void SetSortingOrder(int order)
        {
            if (_renderer != null)
                _renderer.sortingOrder = order;
        }
    }
}
