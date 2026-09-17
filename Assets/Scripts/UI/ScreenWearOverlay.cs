using Game.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    // Screen-space wear overlay for the Game scene HUD: a static authored texture (edge
    // scratches/grime, clean centre) alpha-blended over everything. Never receives raycasts.
    //
    // Spawns as an ordinary child of the scene's own Canvas_UI (same as every other HUD element)
    // instead of a separate persistent cross-scene canvas — it lives and is torn down with the
    // Game scene like anything else under Canvas_UI, so no DontDestroyOnLoad singleton is needed.
    [DisallowMultipleComponent]
    public sealed class ScreenWearOverlay : MonoBehaviour
    {
        private const string MainCanvasName = "Canvas_UI";
        private const string HandPanelName = "CardHandPanel";
        private const int OverlaySortingOrder = 32760;

        // Kept in Resources alongside the shader itself, so both are guaranteed to be included in
        // a player build the same way (see the shader lookup below).
        private const string TextureResourcePath = "Effects/Overlay";

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

        [SerializeField, Range(0f, 2f)] private float intensity = 1f;
        [SerializeField] private Color tint = Color.white;

        private Material _material;
        private Texture2D _texture;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            SceneManager.sceneLoaded += (scene, _) => TrySpawn(scene);
            TrySpawn(SceneManager.GetActiveScene());
        }

        private static void TrySpawn(Scene scene)
        {
            if (scene.name != SceneNames.Game)
                return;

            GameObject canvasObject = FindInScene(scene, MainCanvasName);
            if (canvasObject == null)
            {
                Debug.LogWarning($"ScreenWearOverlay: '{MainCanvasName}' was not found in scene '{scene.name}'.");
                return;
            }

            var overlayObject = new GameObject(nameof(ScreenWearOverlay), typeof(RectTransform));
            var rect = (RectTransform)overlayObject.transform;
            rect.SetParent(canvasObject.transform, worldPositionStays: false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMax = Vector2.zero;

            // Anchored from the top of CardHandPanel up to the top of the screen — the hand
            // itself sits below the overlay, not under it. CardHandPanel is bottom-anchored with
            // a fixed pixel height (see its own RectTransform), so pushing this rect's bottom
            // edge up by that same height lines the two up exactly, in the same canvas space.
            GameObject handPanelObject = FindInScene(scene, HandPanelName);
            float handPanelHeight = handPanelObject != null
                ? ((RectTransform)handPanelObject.transform).rect.height
                : 0f;
            if (handPanelObject == null)
                Debug.LogWarning($"ScreenWearOverlay: '{HandPanelName}' was not found in scene '{scene.name}'; overlay will cover the full screen.");
            rect.offsetMin = new Vector2(0f, handPanelHeight);

            overlayObject.AddComponent<ScreenWearOverlay>();
        }

        private void Awake()
        {
            // A child Canvas with its own sorting order, rather than relying on sibling index,
            // guarantees this stays above other Canvas_UI content (including nested popup
            // canvases) regardless of where in the hierarchy it gets parented.
            Canvas canvas = gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = OverlaySortingOrder;

            // Keeping the shader in Resources guarantees that it is included in a player build.
            Shader shader = Resources.Load<Shader>("ScreenWear");
            if (shader == null)
                shader = Shader.Find("Custom/ScreenWear");

            if (shader == null)
            {
                Debug.LogWarning("ScreenWearOverlay: shader 'Custom/ScreenWear' was not found.");
                Destroy(gameObject);
                return;
            }

            _texture = Resources.Load<Texture2D>(TextureResourcePath);
            if (_texture == null)
                Debug.LogWarning($"ScreenWearOverlay: texture '{TextureResourcePath}' was not found.");

            _material = new Material(shader)
            {
                name = "ScreenWear (Runtime)",
                hideFlags = HideFlags.DontSave,
            };
            ApplyMaterialSettings();

            RawImage image = gameObject.AddComponent<RawImage>();
            image.texture = _texture != null ? _texture : Texture2D.whiteTexture;
            image.material = _material;
            image.color = Color.white;
            image.raycastTarget = false;
            image.maskable = false;
        }

        private void OnValidate()
        {
            if (_material != null)
                ApplyMaterialSettings();
        }

        private void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
        }

        private void ApplyMaterialSettings()
        {
            if (_texture != null)
                _material.SetTexture(MainTexId, _texture);
            _material.SetColor(ColorId, tint);
            _material.SetFloat(IntensityId, intensity);
        }

        private static GameObject FindInScene(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform found = FindRecursive(root.transform, name);
                if (found != null)
                    return found.gameObject;
            }

            return null;
        }

        private static Transform FindRecursive(Transform parent, string name)
        {
            if (parent.name == name)
                return parent;

            foreach (Transform child in parent)
            {
                Transform found = FindRecursive(child, name);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
