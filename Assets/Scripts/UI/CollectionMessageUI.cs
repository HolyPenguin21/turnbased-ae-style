using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // Owns the lifetime and input block of transient profile errors/card descriptions.
    internal sealed class CollectionMessageUI : MonoBehaviour
    {
        private static int showing;
        private static int closedFrame = -1;
        private GameObject previousSelection;
        internal static bool IsShowing => showing > 0 || closedFrame == Time.frameCount;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { showing = 0; closedFrame = -1; }
        internal static void Show(string message) => Show(message, null);
        internal static void ShowReward(string message, string title = "REWARD DETAILS") => Show(message, title);
        private static void Show(string message, string rewardTitle)
        {
            bool rewardStyle = rewardTitle != null;
            var canvas = CollectionUIElements.Canvas("CollectionMessage", rewardStyle ? new Vector2(1920, 1080) : (Vector2?)null);
            canvas.GetComponent<Canvas>().sortingOrder = 250;
            var owner = canvas.gameObject.AddComponent<CollectionMessageUI>();
            if (rewardStyle && EventSystem.current != null) owner.previousSelection = EventSystem.current.currentSelectedGameObject;
            showing++; UIFocusUtility.SetOverlay(owner, true);
            var panel = CollectionUIElements.Panel(canvas, "Message"); CollectionUIElements.Stretch(panel);
            if (rewardStyle)
            {
                panel.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
                panel = CollectionUIElements.RewardWindow(panel, "RewardDescription");
                CollectionUIElements.RewardTitle(panel, rewardTitle);
            }
            float width = rewardStyle ? 856 : 634;
            var content = rewardStyle ? CollectionUIElements.RewardScroll(panel, "Description", 84, 140, 892, 330)
                : CollectionUIElements.Scroll(panel, "Description", 180, 150, 664, 360);
            var label = CollectionUIElements.Label(content, message, 0, 0, width, 1, rewardStyle ? 24 : 20);
            float height = Mathf.Max(1, label.GetPreferredValues(message, width, 10000).y);
            ((RectTransform)label.transform).sizeDelta = new Vector2(width, height);
            content.sizeDelta = new Vector2(width, height);
            if (rewardStyle) CollectionUIElements.RewardButton(panel, "CLOSE", 400, 532, 260, 44, owner.Close).Select();
            else CollectionUIElements.Button(panel, "Close", 412, 550, 200, 40, owner.Close);
        }
        private void Update()
        { if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close(); }
        private void Close() { gameObject.SetActive(false); Destroy(gameObject); }
        private void OnDisable()
        {
            showing = Mathf.Max(0, showing - 1); closedFrame = Time.frameCount; UIFocusUtility.SetOverlay(this, false);
            if (EventSystem.current != null && previousSelection != null && previousSelection.activeInHierarchy)
                EventSystem.current.SetSelectedGameObject(previousSelection);
            previousSelection = null;
        }
    }
}
