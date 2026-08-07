using Game.Core;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Drifting cloud-shadow overlay for the strategic map — a single large flat quad above the
    // terrain, rendered by Custom/CloudDrift.shader (see its own comment for how the cloud
    // shapes themselves are generated). Purely ambient/atmospheric: unlike HexShaderHighlight,
    // there's no Show/Hide state to drive — it's built once (Start, self-initializing rather than
    // driven by HexMapGenerator, which is [ExecuteAlways] and removes itself at runtime — this
    // stays simple and independent of that lifecycle) and left rendering continuously; the drift
    // animation itself lives entirely in the shader (driven by _Time.y), so this component does
    // no per-frame work at all once set up.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class MapCloudOverlay : MonoBehaviour
    {
        [SerializeField] private HexMap map;
        [SerializeField] private GameConfig gameConfig;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int ScaleId = Shader.PropertyToID("_Scale");
        private static readonly int CoverageId = Shader.PropertyToID("_Coverage");
        private static readonly int SoftnessId = Shader.PropertyToID("_Softness");
        private static readonly int SpeedId = Shader.PropertyToID("_Speed");
        private static readonly int DirectionId = Shader.PropertyToID("_Direction");

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Material _material;

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            Shader shader = Shader.Find("Custom/CloudDrift");
            if (shader == null)
                Debug.LogWarning("MapCloudOverlay: shader 'Custom/CloudDrift' not found.");
            _material = new Material(shader);
            _renderer.sharedMaterial = _material;

            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        private void Start()
        {
            if (map != null && gameConfig != null)
                Initialize(map.transform.position, gameConfig.cloudStyle);
        }

        // Centers the overlay on `mapCenter` (typically HexMap's own transform.position) and
        // applies every tunable from `style`. Called automatically from Start (see above) once
        // both serialized references are wired in the scene; exposed publicly too in case a
        // future feature needs to re-center or re-style it later (e.g. a "change weather"
        // system) — rebuilds the quad and reapplies every property fresh either way.
        public void Initialize(Vector3 mapCenter, CloudStyle style)
        {
            if (style == null || _filter == null || _renderer == null)
                return;

            transform.position = mapCenter + Vector3.up * style.height;
            _filter.mesh = BuildQuad(style.planeHalfSize);

            _material.SetColor(ColorId, style.color);
            _material.SetFloat(ScaleId, style.scale);
            _material.SetFloat(CoverageId, style.coverage);
            _material.SetFloat(SoftnessId, style.softness);
            _material.SetFloat(SpeedId, style.speed);
            _material.SetVector(DirectionId, new Vector4(style.direction.x, style.direction.y, 0f, 0f));
        }

        // Vertices are LOCAL to this object (centered at its own origin, flat on the XZ plane)
        // — same construction as HexShaderHighlight.BuildQuad, just a plain square instead of a
        // hex-sized one.
        private static Mesh BuildQuad(float half)
        {
            var mesh = new Mesh { name = "CloudOverlayQuad" };
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
    }
}
