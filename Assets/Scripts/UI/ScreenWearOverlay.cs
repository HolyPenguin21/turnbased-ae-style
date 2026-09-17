using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    // Runtime-only presentation layer for the Game scene. It intentionally sits above the normal
    // HUD but never receives raycasts: the effect is meant to read as a very light worn-screen /
    // protective-glass layer, not as another interactive UI panel.
    //
    // The shader itself keeps the centre almost clean and concentrates sparse scratches near the
    // outer frame. No blur, colour grading or scene sampling is involved, so the map and card UI
    // remain pixel-sharp underneath it.
    public sealed class ScreenWearOverlay : MonoBehaviour
    {
        private const int OverlaySortingOrder = 32760;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int EdgeWidthId = Shader.PropertyToID("_EdgeWidth");
        private static readonly int SpeckStrengthId = Shader.PropertyToID("_SpeckStrength");

        // Intentionally restrained compared with the visual concept. Individual scratches can
        // still catch the eye at the border, but they should disappear from attention during play.
        [SerializeField, Range(0f, 0.2f)] private float intensity = 0.065f;
        [SerializeField, Range(0.05f, 0.35f)] private float edgeWidth = 0.17f;
        [SerializeField, Range(0f, 1f)] private float speckStrength = 0.22f;
        [SerializeField] private Color scratchColor = new Color(0.82f, 0.76f, 0.65f, 1f);

        private static ScreenWearOverlay _instance;

        private GameObject _overlayRoot;
        private Material _material;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var root = new GameObject(nameof(ScreenWearOverlay));
            Object.DontDestroyOnLoad(root);
            _instance = root.AddComponent<ScreenWearOverlay>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void Start()
        {
            ApplyForScene(SceneManager.GetActiveScene());
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;

            SceneManager.sceneLoaded -= HandleSceneLoaded;
            DestroyOverlay();
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ApplyForScene(scene);
        }

        private void ApplyForScene(Scene scene)
        {
            if (scene.name == SceneNames.Game)
                EnsureOverlay();
            else
                DestroyOverlay();
        }

        private void EnsureOverlay()
        {
            if (_overlayRoot != null)
            {
                ApplyMaterialSettings();
                return;
            }

            // Resources.Load keeps the otherwise runtime-only shader referenced in player builds.
            // Shader.Find remains as an Editor-friendly fallback if the Resources asset is moved.
            Shader shader = Resources.Load<Shader>("ScreenWear");
            if (shader == null)
                shader = Shader.Find("Custom/ScreenWear");

            if (shader == null)
            {
                Debug.LogWarning("ScreenWearOverlay: shader 'Custom/ScreenWear' was not found.");
                return;
            }

            _material = new Material(shader)
            {
                name = "ScreenWear (Runtime)",
                hideFlags = HideFlags.DontSave,
            };
            ApplyMaterialSettings();

            _overlayRoot = new GameObject("ScreenWearCanvas", typeof(RectTransform), typeof(Canvas));
            _overlayRoot.transform.SetParent(transform, worldPositionStays: false);

            Canvas canvas = _overlayRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = OverlaySortingOrder;

            var layerObject = new GameObject("WearLayer", typeof(RectTransform), typeof(RawImage));
            layerObject.transform.SetParent(_overlayRoot.transform, worldPositionStays: false);

            RectTransform rect = layerObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            RawImage image = layerObject.GetComponent<RawImage>();
            image.texture = Texture2D.whiteTexture;
            image.material = _material;
            image.color = Color.white;
            image.raycastTarget = false;
            image.maskable = false;
        }

        private void ApplyMaterialSettings()
        {
            if (_material == null)
                return;

            _material.SetColor(ColorId, scratchColor);
            _material.SetFloat(IntensityId, intensity);
            _material.SetFloat(EdgeWidthId, edgeWidth);
            _material.SetFloat(SpeckStrengthId, speckStrength);
        }

        private void DestroyOverlay()
        {
            if (_overlayRoot != null)
            {
                Destroy(_overlayRoot);
                _overlayRoot = null;
            }

            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
        }
    }
}
