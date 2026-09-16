using Game.Styles;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Map
{
    // Single-hex highlight renderer. The class name is kept for scene/prefab compatibility, but
    // the selected-hex visual no longer uses Custom/HexSelectionGlow: a static, worn paint mask
    // is baked into a small runtime texture on the CPU and drawn with URP's stock Unlit shader.
    // This removes the animated neon/procedural-noise look while preserving the existing public
    // API and serialized component reference. HexClusterHighlight remains a separate concern.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HexShaderHighlight : MonoBehaviour
    {
        private const int PaintTextureSize = 256;
        private const float MinPaintWidthRatio = 0.055f;
        private const float PaintOpacity = 0.74f;
        private const float PaintWear = 0.42f;

        [SerializeField] private Color color = new Color(0.78f, 0.71f, 0.56f, 1f);

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
        // animated noise/glow values are copied only to preserve the shared config contract.
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

            // The old 0.03 line gained apparent weight from its glow/noise. With a plain texture
            // it reads too thin, so retain a small radius-relative floor while still respecting
            // styles that deliberately request a thicker line.
            float lineWidth = Mathf.Max(_style.lineThickness, outerRadius * MinPaintWidthRatio);
            float half = radius + Mathf.Max(lineWidth * 1.75f, outerRadius * 0.025f);

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
            var texture = new Texture2D(
                PaintTextureSize,
                PaintTextureSize,
                TextureFormat.RGBA32,
                true)
            {
                name = "Hex Worn Paint Mask (Runtime)",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 0,
            };

            var pixels = new Color32[PaintTextureSize * PaintTextureSize];
            float pixelWorld = (half * 2f) / PaintTextureSize;
            float safeRadius = Mathf.Max(radius, 0.001f);

            for (int y = 0; y < PaintTextureSize; y++)
            {
                float v = (y + 0.5f) / PaintTextureSize;
                float localZ = Mathf.Lerp(-half, half, v);

                for (int x = 0; x < PaintTextureSize; x++)
                {
                    float u = (x + 0.5f) / PaintTextureSize;
                    float localX = Mathf.Lerp(-half, half, u);
                    var p = new Vector2(localX, localZ);

                    float nx = localX / safeRadius;
                    float nz = localZ / safeRadius;

                    // Static broad noise only varies deposited paint width. Unlike the previous
                    // shader it never grows moving tendrils outside the hex.
                    float edgeNoise = FractalNoise(nx, nz, 3.1f, 11.7f, 4.3f);
                    float widthScale = Mathf.Lerp(0.82f, 1.14f, edgeNoise);
                    float halfLine = lineWidth * 0.5f * widthScale;
                    float feather = Mathf.Max(pixelWorld * 1.35f, lineWidth * 0.07f);
                    float distance = Mathf.Abs(HexSignedDistance(p, radius));
                    float ring = 1f - SmoothStep(
                        Mathf.Max(0f, halfLine - feather),
                        halfLine + feather,
                        distance);

                    if (ring <= 0.001f)
                    {
                        pixels[y * PaintTextureSize + x] = new Color32(255, 255, 255, 0);
                        continue;
                    }

                    // Faded stretches + fine mottling + sparse pinholes: intentionally irregular
                    // but still continuous enough to read immediately as the selected hex.
                    float wearNoise = FractalNoise(nx, nz, 2.25f, 27.4f, 19.1f);
                    float patch = SmoothStep(0.30f, 0.68f, wearNoise);
                    float wornCoverage = Mathf.Lerp(1f, 0.22f + 0.78f * patch, PaintWear);

                    float fine = Mathf.PerlinNoise(nx * 17.3f + 7.2f, nz * 17.3f + 31.6f);
                    float grainCoverage = Mathf.Lerp(0.62f, 1f, fine);
                    float chip = Hash01(x, y) < PaintWear * 0.085f ? 0.12f : 1f;

                    float alpha = Mathf.Clamp01(ring * wornCoverage * grainCoverage * chip);
                    pixels[y * PaintTextureSize + x] =
                        new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        // Same regular-hex SDF convention the old shader used: vertex 0 lies on +X, matching
        // HexGridMath's 60-degree corner convention, so the texture stays on the real hex edge.
        private static float HexSignedDistance(Vector2 point, float radius)
        {
            const float kx = -0.8660254f;
            const float ky = 0.5f;
            const float kz = 0.5773503f;

            Vector2 p = new Vector2(Mathf.Abs(point.x), Mathf.Abs(point.y));
            float projected = kx * p.x + ky * p.y;
            float correction = Mathf.Min(projected, 0f);
            p -= 2f * correction * new Vector2(kx, ky);
            p -= new Vector2(Mathf.Clamp(p.x, -kz * radius, kz * radius), radius);
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
