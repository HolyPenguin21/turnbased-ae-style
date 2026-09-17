using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    // Runtime-only presentation layer for the Game scene. It sits above the normal HUD but never
    // receives raycasts. The shader keeps the centre clean and concentrates subtle, short wear
    // marks near the outer frame, so the effect adds character without softening readability.
    public sealed class ScreenWearOverlay : MonoBehaviour
    {
        private const int OverlaySortingOrder = 32760;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int EdgeWidthId = Shader.PropertyToID("_EdgeWidth");
        private static readonly int SpeckStrengthId = Shader.PropertyToID("_SpeckStrength");

        // Kept deliberately below the previous 0.13 treatment: scratches are now short clustered
        // scuffs rather than long full-height strokes, so they remain legible without becoming
        // foreground decoration.
        [SerializeField, Range(0f, 0.25f)] private float intensity = 0.085f;
        [SerializeField, Range(0.05f, 0.35f)] private float edgeWidth = 0.22f;
        [SerializeField, Range(0f, 1f)] private float speckStrength = 0.18f;
        [SerializeField] private Color scratchColor = new Color(0.84f, 0.78f, 0.67f, 1f);

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

            // Keeping the shader in Resources guarantees that it is included in a player build.
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
