using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public static class CollectionUIElements
    {
        internal static RectTransform Rect(Transform parent, string name)
        { var go = new GameObject(name, typeof(RectTransform)); var rect = (RectTransform)go.transform; rect.SetParent(parent, false); return rect; }
        internal static void Place(RectTransform r, float x, float y, float width, float height)
        { r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(width, height); }
        internal static TMP_Text Label(Transform parent, string value, float x, float y, float w, float h, int size = 16)
        {
            var r = Rect(parent, "Label"); Place(r, x, y, w, h);
            var text = r.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = GameAssetCatalog.Load<TMP_FontAsset>("Fonts/GameMenuFont") ?? TMP_Settings.defaultFontAsset;
            text.text = value; text.fontSize = size; text.color = new Color(.82f, .85f, .8f);
            text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.Normal; return text;
        }
        internal static void Clear(Transform root)
        { foreach (Transform child in root) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); } }
        internal static RectTransform Canvas(string name, Vector2? referenceResolution = null)
        {
            var root = Rect(null, name);
            var canvas = root.gameObject.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 200;
            var scaler = root.gameObject.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution ?? new Vector2(1024, 768); scaler.matchWidthOrHeight = .5f;
            root.gameObject.AddComponent<GraphicRaycaster>();
            return root;
        }
    }
}
