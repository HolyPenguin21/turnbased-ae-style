using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    // Full-screen black loading curtain that survives a scene change. Shown before a scene
    // load (LoadScene renders one black frame first, so the freeze of a synchronous load sits
    // behind black instead of a frozen menu) and stays up until the loaded scene is ready.
    // Optional picture (GameConfig.loadingScreenImage) fills the screen over the black.
    //
    // A scene that runs its own reveal (CitadelSetupController's intro) calls Hold in Awake and
    // Release when it is ready; any other loaded scene fades the curtain out on its own one
    // frame after loading, so failure paths back to the menu never leave the screen black.
    public sealed class SceneLoadCurtain : MonoBehaviour
    {
        private const int CurtainSortingOrder = 32767;
        private const float DefaultFade = 0.3f;

        private static SceneLoadCurtain instance;

        private CanvasGroup group;
        private Image picture;
        private AspectRatioFitter pictureFitter;
        private Coroutine fade;
        private bool loading;
        private bool hasHold;
        private Scene heldScene;

        public static bool IsLoading => instance != null && instance.loading;

        // Covers the screen, lets that black frame render, then loads the scene.
        public static void LoadScene(string sceneName, Sprite image = null)
        {
            SceneLoadCurtain curtain = Show(image);
            if (curtain.loading) return;
            curtain.loading = true;
            curtain.StartCoroutine(curtain.LoadAfterFrame(sceneName));
        }

        // Fully opaque curtain now; keeps an already shown picture when image is null.
        public static SceneLoadCurtain Show(Sprite image = null)
        {
            if (instance == null) instance = Create();
            instance.StopFade();
            instance.group.alpha = 1f;
            instance.group.blocksRaycasts = true;
            if (image != null) instance.SetPicture(image);
            return instance;
        }

        // The given scene dismisses the curtain itself through Release.
        public static void Hold(Scene scene, Sprite image = null)
        {
            Show(image);
            instance.hasHold = true;
            instance.heldScene = scene;
        }

        public static void Release(float duration = DefaultFade)
        {
            if (instance == null || instance.loading) return;
            instance.hasHold = false;
            instance.StopFade();
            instance.fade = instance.StartCoroutine(instance.FadeOut(Mathf.Max(0f, duration)));
        }

        private static SceneLoadCurtain Create()
        {
            var root = new GameObject(nameof(SceneLoadCurtain), typeof(RectTransform), typeof(Canvas),
                typeof(CanvasGroup), typeof(GraphicRaycaster));
            DontDestroyOnLoad(root);
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CurtainSortingOrder;

            var black = CreateFullScreenImage("Black", root.transform);
            black.color = Color.black;

            var curtain = root.AddComponent<SceneLoadCurtain>();
            curtain.group = root.GetComponent<CanvasGroup>();
            curtain.picture = CreateFullScreenImage("Picture", black.transform);
            curtain.picture.raycastTarget = false;
            curtain.picture.enabled = false;
            curtain.pictureFitter = curtain.picture.gameObject.AddComponent<AspectRatioFitter>();
            curtain.pictureFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            SceneManager.sceneLoaded += curtain.OnSceneLoaded;
            return curtain;
        }

        private static Image CreateFullScreenImage(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return go.GetComponent<Image>();
        }

        private void SetPicture(Sprite image)
        {
            picture.sprite = image;
            picture.enabled = true;
            Rect r = image.rect;
            pictureFitter.aspectRatio = r.height > 0f ? r.width / r.height : 1f;
        }

        private IEnumerator LoadAfterFrame(string sceneName)
        {
            yield return null;
            SceneManager.LoadScene(sceneName);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;
            loading = false;
            if (hasHold && heldScene != scene) hasHold = false;
            if (!hasHold) StartCoroutine(ReleaseUnlessHeld(scene));
        }

        // One frame later, so a scene object can still claim the curtain from Start.
        private IEnumerator ReleaseUnlessHeld(Scene scene)
        {
            yield return null;
            if (!(hasHold && heldScene == scene) && !loading) Release();
        }

        private IEnumerator FadeOut(float duration)
        {
            group.blocksRaycasts = false;
            float start = group.alpha;
            for (float t = 0f; t < duration; t += Time.unscaledDeltaTime)
            {
                group.alpha = Mathf.Lerp(start, 0f, t / duration);
                yield return null;
            }
            fade = null;
            Destroy(gameObject);
        }

        private void StopFade()
        {
            if (fade == null) return;
            StopCoroutine(fade);
            fade = null;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (instance == this) instance = null;
        }
    }
}
