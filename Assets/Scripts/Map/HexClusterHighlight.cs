using System.Collections.Generic;
using Game.HexGrid;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Shader-based replacement for HexHighlightMarker.ShowPolygon — same visual family as
    // HexShaderHighlight (ragged noise border + glow) but driven by an arbitrary set of hex
    // cells instead of one: HexClusterGlow.shader itself works out, per pixel, which hex it's
    // nearest to and whether each of that hex's 6 neighbours is also in the set, so only true
    // outer-boundary edges get drawn — no boundary geometry is computed here in C#.
    //
    // Tunables (noise/glow/margin) come from a HexHighlightStyle applied via ApplyStyle —
    // GameConfig holds one per usage context (region highlight, citadel pick, hex selection)
    // so each can look different instead of every instance sharing the shader's own defaults.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HexClusterHighlight : MonoBehaviour
    {
        private const int MaxClusterHexes = 32;

        [SerializeField] private Color color = new Color(1f, 0.35f, 0.1f, 1f);

        private static readonly int ClusterHexesId = Shader.PropertyToID("_ClusterHexes");
        private static readonly int ClusterHexCountId = Shader.PropertyToID("_ClusterHexCount");
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
        private Vector4[] _packedCells = new Vector4[MaxClusterHexes];

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            Shader shader = Shader.Find("Custom/HexClusterGlow");
            if (shader == null)
                Debug.LogWarning("HexClusterHighlight: shader 'Custom/HexClusterGlow' not found.");
            _material = new Material(shader);
            _renderer.sharedMaterial = _material;

            // _ClusterHexes/_ClusterHexCount/_OuterRadius aren't in the shader's Properties
            // block (arrays can't be), and Material.Set* silently no-ops for a property name
            // it doesn't recognise from that block — that was the actual bug behind the
            // cluster highlight never appearing. A MaterialPropertyBlock is the API Unity
            // provides specifically for setting arbitrary per-renderer shader data like this.
            _propertyBlock = new MaterialPropertyBlock();

            // Cluster membership math in the shader assumes this object's mesh vertices are
            // world-space positions directly (see BuildQuad) — keep the transform itself at
            // world identity so it can't double-offset them.
            transform.position = Vector3.zero;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            Hide();
        }

        // Call once, right after creating the instance — sets the noise/glow tunables and
        // margin for this usage context (typically gameConfig.<...>Style). Safe to call before
        // the first ShowCluster. Copies the values rather than keeping the reference, since
        // that reference is often the shared GameConfig asset's own instance.
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
            SetSortingOrder(_style.sortingOrder);
        }

        public void ShowCluster(IReadOnlyList<HexCoord> cells, float outerRadius)
        {
            if (cells == null || cells.Count == 0)
            {
                Hide();
                return;
            }

            int count = Mathf.Min(cells.Count, MaxClusterHexes);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;

            for (int i = 0; i < count; i++)
            {
                HexCoord c = cells[i];
                _packedCells[i] = new Vector4(c.Q, c.R, 0f, 0f);

                Vector3 world = HexGridMath.AxialToWorld(c.Q, c.R, outerRadius);
                minX = Mathf.Min(minX, world.x);
                maxX = Mathf.Max(maxX, world.x);
                minZ = Mathf.Min(minZ, world.z);
                maxZ = Mathf.Max(maxZ, world.z);
            }

            _renderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetVectorArray(ClusterHexesId, _packedCells);
            _propertyBlock.SetInt(ClusterHexCountId, count);
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

            float pad = outerRadius + _style.margin;
            _filter.mesh = BuildQuad(minX - pad, maxX + pad, minZ - pad, maxZ + pad);
            _renderer.enabled = true;
        }

        private static Mesh BuildQuad(float minX, float maxX, float minZ, float maxZ)
        {
            var mesh = new Mesh { name = "HexClusterQuad" };
            mesh.SetVertices(new[]
            {
                new Vector3(minX, 0f, minZ),
                new Vector3(maxX, 0f, minZ),
                new Vector3(minX, 0f, maxZ),
                new Vector3(maxX, 0f, maxZ),
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
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

        // See HexShaderHighlight.SetSortingOrder — same reasoning, resolves draw order
        // between the two transparent MeshRenderers instead of relying on height alone.
        public void SetSortingOrder(int order)
        {
            if (_renderer != null)
                _renderer.sortingOrder = order;
        }
    }
}
