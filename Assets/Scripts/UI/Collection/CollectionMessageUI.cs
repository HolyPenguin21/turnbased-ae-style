using Game.Audio;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Owns the lifetime and input of transient profile errors and card descriptions.
    internal sealed class CollectionMessageUI : MonoBehaviour
    {
        internal const string MessageDialogKey = "Prefabs/Collection/MessageDialog";
        internal const string RewardDialogKey = "Prefabs/Rewards/RewardDialog";

        [SerializeField] private TMP_Text body;
        [SerializeField] private ScrollRect scroll;
        [SerializeField] private Button closeButton;

        private GameObject canvasRoot;
        internal static bool IsShowing => UIFocusUtility.Contains<CollectionMessageUI>() || UIFocusUtility.ClosedThisFrame;

        internal static void Show(string message)
        {
            var canvas = CreateCanvas(null);
            var prefab = GameAssetCatalog.Load<GameObject>(MessageDialogKey);
            var dialog = Instantiate(prefab.GetComponent<CollectionMessageUI>(), canvas);
            dialog.body.text = message;
            var width = dialog.scroll.content.rect.width;
            float height = Mathf.Max(1, dialog.body.GetPreferredValues(message, width, 10000).y);
            dialog.scroll.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            SceneUIAudioBinder.BindCreatedRoot(dialog.closeButton);
            dialog.Open(canvas.gameObject, dialog.closeButton);
        }

        internal static void ShowReward(string message, string title = "REWARD DETAILS")
        {
            var canvas = CreateCanvas(new Vector2(1920, 1080));
            var view = Instantiate(GameAssetCatalog.Load<GameObject>(RewardDialogKey).GetComponent<CollectionRewardView>(), canvas);
            view.Title.text = title;
            view.Body.text = message;
            canvas.gameObject.AddComponent<CollectionMessageUI>().Open(canvas.gameObject, view.PrimaryButton);
        }

        private static RectTransform CreateCanvas(Vector2? resolution)
        {
            var canvas = CollectionUIElements.Canvas("CollectionMessage", resolution);
            canvas.GetComponent<Canvas>().sortingOrder = 250;
            return canvas;
        }

        private void Open(GameObject root, Button button)
        {
            canvasRoot = root;
            button.onClick.AddListener(Close);
            UIFocusUtility.SetModal(this, root, ModalBlock.Full, button);
        }

        private void Update()
        {
            if (UIFocusUtility.WasEscapePressed(this)) Close();
        }

        private void Close()
        {
            if (!UIFocusUtility.IsTop(this)) return;
            canvasRoot.SetActive(false);
            Destroy(canvasRoot);
        }

        private void OnDisable() => UIFocusUtility.SetModal(this, null);
    }
}
