using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One small atlas mesh on an isolated child Canvas. The parent is the same stretched
    // background Image, so dust and architecture occlusion stay aligned at every aspect ratio.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MainMenuAtmosphereGraphic : MaskableGraphic
    {
        [Header("Scene visibility")]
        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameObject collectionWindow;
        [Header("External image pack")]
        [SerializeField] private Texture2D dustAtlas;
        [SerializeField] private Texture2D architectureMask;
        [SerializeField] private Shader atmosphereShader;
        [Header("Approved motion")]
        [Range(0f, 2f)] [SerializeField] private float intensity = 1f;
        [Range(0f, 2f)] [SerializeField] private float speed = 1f;

        private Material ownedMaterial;
        private bool showing;
        private double elapsed;
        private static readonly int OcclusionId = Shader.PropertyToID("_OcclusionTex");
        public override Texture mainTexture => dustAtlas != null ? dustAtlas : Texture2D.whiteTexture;

        protected override void OnEnable()
        {
            // A decorative full-screen graphic must never intercept menu input.
            raycastTarget = false;
            base.OnEnable();
            if (canvas != null) canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
            // This runs before uGUI's graphic rebuild, including editor-only repaints.
            Canvas.preWillRenderCanvases -= PrepareForRender;
            Canvas.preWillRenderCanvases += PrepareForRender;
            PrepareForRender();
            SetAllDirty();
        }

        // Both roots are required: a broken visibility reference must fail closed.
        private bool CanShow() => mainMenuPanel != null && collectionWindow != null
            && mainMenuPanel.activeInHierarchy && !collectionWindow.activeInHierarchy;

        private bool AssetsReady() => dustAtlas != null && architectureMask != null && atmosphereShader != null
            && atmosphereShader.isSupported && dustAtlas.width == dustAtlas.height
            && dustAtlas.height >= 4 && dustAtlas.height % 4 == 0;

        private bool EnsureMaterial()
        {
            if (!AssetsReady()) return false;
            if (ownedMaterial != null && ownedMaterial.shader != atmosphereShader) ReleaseMaterial();
            if (ownedMaterial == null)
            {
                ownedMaterial = new Material(atmosphereShader)
                { name = "Main menu atmosphere (instance)", hideFlags = HideFlags.HideAndDontSave };
                material = ownedMaterial;
                SetVerticesDirty();
            }
            if (ownedMaterial.GetTexture(OcclusionId) != architectureMask)
                ownedMaterial.SetTexture(OcclusionId, architectureMask);
            return true;
        }

        protected override void Start()
        {
            base.Start();
            if (mainMenuPanel == null || collectionWindow == null || !AssetsReady())
                Debug.LogWarning("Main menu atmosphere: install the supplied image pack into Assets/Textures/MainMenu/ (including .meta files). Effects remain hidden until all assets are available.", this);
        }

        private void LateUpdate()
        {
            PrepareForRender();
            // Hidden windows neither rebuild geometry nor advance the wind clock. On return,
            // resume the existing state, without creating objects or a large elapsed-time jump.
            if (!showing) return;
            if (Application.isPlaying) elapsed += Time.unscaledDeltaTime * Mathf.Max(0f, speed);
            SetVerticesDirty();
        }

        private void PrepareForRender()
        {
            if (!isActiveAndEnabled || CanvasUpdateRegistry.IsRebuildingGraphics()) return;
            bool visible = CanShow() && intensity > 0f && EnsureMaterial();
            if (visible != showing)
            {
                showing = visible;
                SetAllDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            // Mesh generation is read-only: assigning a material here would enqueue a rebuild
            // while CanvasUpdateRegistry is already rebuilding graphics.
            if (!CanShow() || !AssetsReady() || ownedMaterial == null || intensity <= 0f) return;
            Rect rect = rectTransform.rect;
            if (rect.width <= 0 || rect.height <= 0) return;
            for (int layer = 0; layer < MainMenuWindMotion.LayerCount; layer++)
                for (int puff = 0; puff < MainMenuWindMotion.PuffCount(layer); puff++)
                {
                    AddCloud(vh, rect, MainMenuWindMotion.SampleDust(elapsed, layer, puff, false));
                    AddCloud(vh, rect, MainMenuWindMotion.SampleDust(elapsed, layer, puff, true));
                }
            AddCloud(vh, rect, MainMenuWindMotion.SampleDebris(elapsed));
        }

        private void AddCloud(VertexHelper vh, Rect rect, MainMenuWindMotion.Sample sample)
        {
            if (sample.Alpha <= .0001f) return;
            float halfWidth = sample.Width * .5f, halfHeight = sample.Height * .5f;
            // Dust is clipped in image space; the UVs follow the clipped fraction rather than
            // stretching the remaining texture at the screen edge.
            float left = Mathf.Max(0, sample.X - halfWidth), right = Mathf.Min(1, sample.X + halfWidth);
            float top = Mathf.Max(0, sample.Y - halfHeight), bottom = Mathf.Min(1, sample.Y + halfHeight);
            if (sample.Tile != 3 && (right <= left || bottom <= top)) return;
            Color tint = sample.Tile == 3 ? new Color(.23f, .17f, .133f, color.a) : color;
            tint.a *= Mathf.Clamp01(sample.Alpha * intensity);
            int first = vh.currentVertCount;
            if (sample.Tile == 3)
            {
                // Rotate in local pixels so the scrap does not distort at wide aspect ratios.
                AddVertex(vh, rect, sample, -halfWidth, -halfHeight, 0, 1, tint);
                AddVertex(vh, rect, sample, halfWidth, -halfHeight, 1, 1, tint);
                AddVertex(vh, rect, sample, halfWidth, halfHeight, 1, 0, tint);
                AddVertex(vh, rect, sample, -halfWidth, halfHeight, 0, 0, tint);
            }
            else
            {
                float u0 = (left - sample.X + halfWidth) / sample.Width;
                float u1 = (right - sample.X + halfWidth) / sample.Width;
                float v0 = 1 - (top - sample.Y + halfHeight) / sample.Height;
                float v1 = 1 - (bottom - sample.Y + halfHeight) / sample.Height;
                AddVertex(vh, rect, sample, left - sample.X, top - sample.Y, u0, v0, tint);
                AddVertex(vh, rect, sample, right - sample.X, top - sample.Y, u1, v0, tint);
                AddVertex(vh, rect, sample, right - sample.X, bottom - sample.Y, u1, v1, tint);
                AddVertex(vh, rect, sample, left - sample.X, bottom - sample.Y, u0, v1, tint);
            }
            vh.AddTriangle(first, first + 1, first + 2);
            vh.AddTriangle(first, first + 2, first + 3);
        }

        private void AddVertex(VertexHelper vh, Rect rect, MainMenuWindMotion.Sample sample,
            float dx, float dy, float u, float v, Color tint)
        {
            float x = dx * rect.width, y = -dy * rect.height;
            if (sample.Angle != 0f)
            {
                // The reference uses screen-space Y down; Unity's local Y points up.
                float cos = Mathf.Cos(-sample.Angle), sin = Mathf.Sin(-sample.Angle);
                float rotated = x * cos - y * sin;
                y = x * sin + y * cos; x = rotated;
            }
            Vector2 imageUV = new Vector2(sample.X + x / rect.width, 1 - sample.Y + y / rect.height);
            var vertex = UIVertex.simpleVert;
            vertex.position = new Vector2(rect.xMin + imageUV.x * rect.width, rect.yMin + imageUV.y * rect.height);
            vertex.color = tint;
            // Four horizontal strips, with a half-texel inset to avoid atlas bleeding.
            vertex.uv0 = new Vector2(Mathf.Lerp(.5f / dustAtlas.width, 1 - .5f / dustAtlas.width, u),
                (3 - sample.Tile) * .25f + Mathf.Lerp(.5f / dustAtlas.height, .25f - .5f / dustAtlas.height, v));
            vertex.uv1 = imageUV;
            vh.AddVert(vertex);
        }

        protected override void OnDisable()
        {
            Canvas.preWillRenderCanvases -= PrepareForRender;
            base.OnDisable();
            // OnDisable precedes assembly/domain reload. OnDestroy alone would leave a
            // HideAndDontSave native material alive after its managed owner was reset.
            ReleaseMaterial();
            showing = false;
        }

        protected override void OnDestroy()
        {
            Canvas.preWillRenderCanvases -= PrepareForRender;
            ReleaseMaterial();
            base.OnDestroy();
        }

        private void ReleaseMaterial()
        {
            if (ownedMaterial == null) return;
            material = null;
            if (Application.isPlaying) Destroy(ownedMaterial); else DestroyImmediate(ownedMaterial);
            ownedMaterial = null;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            // Do not create/destroy native objects in OnValidate. Shader/mask changes are
            // picked up safely by PrepareForRender, even when the editor is not playing.
            raycastTarget = false;
            base.OnValidate();
        }
#endif
    }
}
