using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.UI
{
    // Owns the lifetime and input block of transient profile errors/card descriptions.
    internal sealed class CollectionMessageUI : MonoBehaviour
    {
        private static int showing;
        private static int closedFrame = -1;
        internal static bool IsShowing => showing > 0 || closedFrame == Time.frameCount;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { showing = 0; closedFrame = -1; }
        internal static void Show(string message)
        {
            var canvas = CollectionUIElements.Canvas("CollectionMessage");
            canvas.GetComponent<Canvas>().sortingOrder = 250;
            var owner = canvas.gameObject.AddComponent<CollectionMessageUI>();
            showing++; UIFocusUtility.SetOverlay(owner, true);
            var panel = CollectionUIElements.Panel(canvas, "Message"); CollectionUIElements.Stretch(panel);
            var content = CollectionUIElements.Scroll(panel, "Description", 180, 150, 664, 360);
            var label = CollectionUIElements.Label(content, message, 0, 0, 634, 1, 20);
            float height = Mathf.Max(1, label.GetPreferredValues(message, 634, 10000).y);
            ((RectTransform)label.transform).sizeDelta = new Vector2(634, height);
            content.sizeDelta = new Vector2(634, height);
            CollectionUIElements.Button(panel, "Close", 412, 550, 200, 40, owner.Close);
        }
        private void Update()
        { if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close(); }
        private void Close() { gameObject.SetActive(false); Destroy(gameObject); }
        private void OnDisable() { showing = Mathf.Max(0, showing - 1); closedFrame = Time.frameCount; UIFocusUtility.SetOverlay(this, false); }
    }
}
