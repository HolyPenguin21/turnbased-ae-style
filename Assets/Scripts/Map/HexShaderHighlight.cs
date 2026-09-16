using Game.Styles;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Map
{
    // Single-hex highlight renderer. The class name is kept for scene/prefab compatibility, but
    // the selected-hex visual no longer uses Custom/HexSelectionGlow: a static, worn paint mask
    // is baked into a runtime texture on the CPU and drawn with URP's stock Unlit shader.
    // This keeps the marker grounded in the map instead of reading as an animated UI overlay.
    // HexClusterHighlight remains a separate concern.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HexShaderHighlight : MonoBehaviour
    {
        private const int PaintTextureSize = 512;
        private const float MinPaintWidthRatio = 0.072f;
        private const float PaintOpacity = 1f;
        private const float HexApothemRatio = 0.8660254f;
        private const float SparseFleckChance = 0.012f;

        // General map selection is intentionally a fixed authored visual now, not a GameConfig
        // tuning surface. GameConfig exposes this only as a read-only compatibility accessor for
        // the existing HexSelectionController call site; the values themselves live here next to
        // the renderer that owns them.
        public static HexHighlightStyle FixedMapSelectionStyle { get; } = new HexHighlightStyle
        {
            radiusScale = 0.94f,
            margin = 0f,
            lineThickness = 0.06f,
            noiseReach = 0f,
            noiseScale = 0f,
            noiseSpeed = 0f,
            glowIntensity = 0f,
            glowWidth = 0f,
            sortingOrder = 1,
        };

        // Warm neutral rather than yellow/olive: the paint should read as sun-bleached chalk on
        // the sand, while remaining slightly softer than pure UI white.
        [SerializeField] private Color color = new Color(0.97f, 0.96f, 0.92f, 1f);

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private HexHighlightStyle _style = new HexHighlightStyle();
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Material _material;
        private MaterialPropertyBlock _propertyBlock;
        private Mesh _runtimeMesh;
        private Texture2D _paintTexture;
        private float _builtRadius = -1f;

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _propertyBlock = new MaterialPropertyBlock();

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogWarning("HexShaderHighlight: URP Unlit shader not found.");
            }
            else
            {
                _material = new Material(shader)
                {
                    name = "Hex Worn Paint (Runtime)",
                    hideFlags = HideFlags.DontSave,
                    renderQueue = (int)RenderQueue.Transparent,
                };
                ConfigureTransparentUnlit(_material);
                _renderer.sharedMaterial = _material;
            }

            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            Hide();
        }

        // HexHighlightStyle is shared with HexClusterHighlight. For this single-hex renderer,
        // radiusScale, lineThickness and sortingOrder are the meaningful visual inputs. The old
        // animated noise/glow values are copied only to preserve the shared config contract used
        // by the citadel setup highlight; the ordinary map selection receives the fixed preset
        // above and therefore has no editable GameConfig values.
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

            _builtRadius = -1f;
            SetSortingOrder(_style.sortingOrder);
        }

        public void ShowAt(Vector3 center, float outerRadius)
        {
            if (_renderer == null || _material == null)
                return;

            if (!Mathf.Approximately(outerRadius, _builtRadius))
            {
                RebuildPaintVisual(outerRadius);
                _builtRadius = outerRadius;
            }

            transform.position = center;
            ApplyMaterialProperties();
            _renderer.enabled = _runtimeMesh != null && _paintTexture != null;
        }

        private void RebuildPaintVisual(float outerRadius)
        {
            float radius = Mathf.Max(0.001f, outerRadius * _style.radiusScale);
            float lineWidth = Mathf.Max(_style.lineThickness, outerRadius * MinPaintWidthRatio);

            // Keep a real transparent gutter around the complete stroke. The old SDF treated its
            // input as an apothem while callers supplied a corner radius, so the +X/-X vertices
            // actually extended ~15% farther than `radius` and were cut by this quad. The SDF is
            // corrected below, and two paint widths of padding make clipping impossible even at
            // the roughest/thickest parts of the new stroke.
            float padding = Mathf.Max(lineWidth * 2f, outerRadius * 0.035f);
            float half = radius + padding;

            DestroyRuntimeMesh();
            DestroyPaintTexture();

            _runtimeMesh = BuildQuad(half);
            _filter.sharedMesh = _runtimeMesh;
            _paintTexture = BuildPaintTexture(radius, lineWidth, half);
        }

        private static void ConfigureTransparentUnlit(Material material)
        {
            // URP/Unlit is opaque by default. Configure ordinary alpha blending once; the
            // selection shape itself then comes only from the texture mask and tint.
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_Cull", (int)CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
        }

        private Texture2D BuildPaintTexture(float radius, float lineWidth, float half)
        {
            // The mask deliberately has no mip chain: at strategic-map zoom the reference is a
            // dry painted stroke with a definite edge, not a blurred translucent band. 512px
            // leaves enough resolution for real multi-pixel chips while bilinear filtering still
            // provides stable sub-pixel movement.
            var texture = new Texture2D(
                PaintTextureSize,
                PaintTextureSize,
                TextureFormat.RGBA32,
                false)
            {
                name = "Hex Worn Paint Mask (Runtime)",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };

            var pixels = new Color32[PaintTextureSize * PaintTextureSize];
            float pixelWorld = (half * 2f) / PaintTextureSize;
            float safeRadius = Mathf.Max(radius, 0.001f);
            float feather = Mathf.Max(pixelWorld * 0.72f, lineWidth * 0.015f);
            float maxPaintDistance = lineWidth * 0.5f * 1.12f * 1.05f + feather;

            for (int y = 0; y < PaintTextureSize; y++)
            {
                float v = (y + 0.5f) / PaintTextureSize;
                float localZ = Mathf.Lerp(-half, half, v);

                for (int x = 0; x < PaintTextureSize; x++)
                {
                    float u = (x + 0.5f) / PaintTextureSize;
                    float localX = Mathf.Lerp(-half, half, u);
                    var p = new Vector2(localX, localZ);
                    float distance = Mathf.Abs(HexSignedDistance(p, radius));

                    // The expensive paint noise only matters in a narrow band around the hex.
                    // Reject the rest of the 512x512 texture before any Perlin calls so the extra
                    // texture detail does not introduce a noticeable first-selection hitch.
                    if (distance > maxPaintDistance)
                    {
                        pixels[y * PaintTextureSize + x] = new Color32(255, 255, 255, 0);
                        continue;
                    }

                    float nx = localX / safeRadius;
                    float nz = localZ / safeRadius;

                    // Two coherent bands perturb deposited width rather than the whole marker's
                    // opacity. That creates a visibly painted, imperfect edge without bringing
                    // back the old shader's soft animated halo.
                    float broadEdge = FractalNoise(nx, nz, 6.4f, 11.7f, 4.3f);
                    float fineEdge = FractalNoise(nx, nz, 17.5f, 39.2f, 8.6f);
                    float widthScale =
                        Mathf.Lerp(0.86f, 1.12f, broadEdge) *
                        Mathf.Lerp(0.95f, 1.05f, fineEdge);
                    float halfLine = lineWidth * 0.5f * widthScale;

                    // About one source pixel of AA is enough to stop shimmer but keeps the edge
                    // visibly sharper than the previous soft shader contour.
                    float ring = 1f - SmoothStep(
                        Mathf.Max(0f, halfLine - feather),
                        halfLine + feather,
                        distance);

                    if (ring <= 0.001f)
                    {
                        pixels[y * PaintTextureSize + x] = new Color32(255, 255, 255, 0);
                        continue;
                    }

                    // Wear is local coverage loss, not uniform transparency. Large low-frequency
                    // patches fade some deposited paint; a second tighter field punches genuine
                    // chips through it, and a few 2x2 flecks add dry-grain breakup. Most surviving
                    // paint remains opaque so the selection is still readable at a glance.
                    float wearNoise = FractalNoise(nx, nz, 4.7f, 27.4f, 19.1f);
                    float wearPatch = SmoothStep(0.31f, 0.53f, wearNoise);
                    float wornCoverage = Mathf.Lerp(0.58f, 1f, wearPatch);

                    float chipNoise = FractalNoise(nx, nz, 15.5f, 73.1f, 41.9f);
                    float chipCoverage = Mathf.Lerp(
                        0.06f,
                        1f,
                        SmoothStep(0.34f, 0.48f, chipNoise));

                    float dryGrain = Mathf.PerlinNoise(
                        nx * 31.7f + 7.2f,
                        nz * 31.7f + 31.6f);
                    float grainCoverage = Mathf.Lerp(0.86f, 1f, dryGrain);

                    float fleck = Hash01(x / 2, y / 2) < SparseFleckChance ? 0.12f : 1f;
                    float alpha = Mathf.Clamp01(
                        ring * wornCoverage * chipCoverage * grainCoverage * fleck);

                    // Tiny pigment variation helps the mark read as dry material laid on the
                    // ground rather than a mathematically flat UI colour.
                    float pigmentNoise = Mathf.PerlinNoise(
                        nx * 12.3f + 53.4f,
                        nz * 12.3f + 12.8f);
                    byte pigment = (byte)Mathf.RoundToInt(Mathf.Lerp(236f, 255f, pigmentNoise));
                    pixels[y * PaintTextureSize + x] = new Color32(
                        pigment,
                        pigment,
                        pigment,
                        (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // Inigo Quilez's regular-hex SDF takes the centre-to-flat distance (apothem), while the
        // rest of this project consistently calls `outerRadius` the centre-to-corner distance
        // (see HexGridMath and HexTileMeshGenerator). Converting here makes radiusScale mean what
        // HexHighlightStyle documents and, crucially, keeps the +/-X vertices inside the quad.
        private static float HexSignedDistance(Vector2 point, float outerRadius)
        {
            const float kx = -0.8660254f;
            const float ky = 0.5f;
            const float kz = 0.5773503f;

            float apothem = outerRadius * HexApothemRatio;
            Vector2 p = new Vector2(Mathf.Abs(point.x), Mathf.Abs(point.y));
            float projected = kx * p.x + ky * p.y;
            float correction = Mathf.Min(projected, 0f);
            p -= 2f * correction * new Vector2(kx, ky);
            p -= new Vector2(Mathf.Clamp(p.x, -kz * apothem, kz * apothem), apothem);
            return p.magnitude * Mathf.Sign(p.y);
        }

        private static float FractalNoise(float x, float y, float scale, float offsetX, float offsetY)
        {
            float low = Mathf.PerlinNoise(x * scale + offsetX, y * scale + offsetY);
            float high = Mathf.PerlinNoise(
                x * scale * 2.37f + offsetX * 0.41f,
                y * scale * 2.37f + offsetY * 0.53f);
            return low * 0.72f + high * 0.28f;
        }

        private static float Hash01(int x, int y)
        {
            unchecked
            {
                uint hash = (uint)x * 374761393u + (uint)y * 668265263u + 0x9E3779B9u;
                hash = (hash ^ (hash >> 13)) * 1274126177u;
                hash ^= hash >> 16;
                return hash / 4294967295f;
            }
        }

        private static float SmoothStep(float from, float to, float value)
        {
            if (to <= from)
                return value >= to ? 1f : 0f;

            float t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        private static Mesh BuildQuad(float half)
        {
            var mesh = new Mesh
            {
                name = "Hex Worn Paint Quad",
                hideFlags = HideFlags.DontSave,
            };

            mesh.vertices = new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3(half, 0f, -half),
                new Vector3(-half, 0f, half),
                new Vector3(half, 0f, half),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private void ApplyMaterialProperties()
        {
            if (_renderer == null || _propertyBlock == null)
                return;

            _renderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.Clear();

            Color tinted = color;
            tinted.a *= PaintOpacity;
            _propertyBlock.SetColor(BaseColorId, tinted);
            if (_paintTexture != null)
                _propertyBlock.SetTexture(BaseMapId, _paintTexture);

            _renderer.SetPropertyBlock(_propertyBlock);
        }

        public void Hide()
        {
            if (_renderer != null)
                _renderer.enabled = false;
        }

        public void SetColor(Color newColor)
        {
            color = newColor;
            ApplyMaterialProperties();
        }

        // The map tiles and highlights sit flat in the transparent queue, so sortingOrder
        // remains the authoritative draw-order control exactly as before.
        public void SetSortingOrder(int order)
        {
            if (_renderer != null)
                _renderer.sortingOrder = order;
        }

        private void OnDestroy()
        {
            DestroyRuntimeMesh();
            DestroyPaintTexture();

            if (_material != null)
                Destroy(_material);
        }

        private void DestroyRuntimeMesh()
        {
            if (_runtimeMesh == null)
                return;

            if (_filter != null && _filter.sharedMesh == _runtimeMesh)
                _filter.sharedMesh = null;

            Destroy(_runtimeMesh);
            _runtimeMesh = null;
        }

        private void DestroyPaintTexture()
        {
            if (_paintTexture == null)
                return;

            Destroy(_paintTexture);
            _paintTexture = null;
        }
    }
}
