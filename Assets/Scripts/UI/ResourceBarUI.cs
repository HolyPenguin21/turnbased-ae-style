using Game.Core;
using Game.Economy;
using Game.Map;
using Game.Turns;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // Always-on top-of-screen HUD: the human player's AP + stockpiled resources, plus the
    // turn counter. Both are event-driven, not polled: resources refresh off
    // PlayerRoot.ResourcesChanged (see RefreshResourceText), the turn counter off
    // GameTurnController.TurnStarted (see OnTurnStarted) — neither changes more than a handful
    // of times per turn, so there was nothing to gain from checking every frame.
    public class ResourceBarUI : MonoBehaviour
    {
        [SerializeField] private GameTurnController turnController;
        [SerializeField] private TMP_Text apText;
        [SerializeField] private TMP_Text humanText;
        [SerializeField] private TMP_Text energyText;
        [SerializeField] private TMP_Text materialsText;
        [SerializeField] private TMP_Text techText;
        [SerializeField] private TMP_Text turnText;

        // Resolved once in OnEnable, by which point setup has already registered it — Show()
        // (this object's only activation trigger) is only ever called "right after citadel
        // setup finishes" per its own comment above, so the human's PlayerRoot is guaranteed to
        // exist by the time OnEnable runs.
        private PlayerRoot _humanRoot;

        // Whichever PlayerRoot the bar is actually reading from right now — _humanRoot except
        // during GameTurnController's debugFollowAiVision (see ShowRootDebug), when it's
        // temporarily the acting AI's own root instead. Always the one RefreshResourceText reads
        // and ResourcesChanged is subscribed to; SetDisplayedRoot is the only place that changes.
        private PlayerRoot _displayedRoot;

        // Hidden until a citadel exists to report on — GameTurnController calls this once,
        // right after citadel setup finishes, and it never hides again after that.
        public void Show()
        {
            gameObject.SetActive(true);
        }

        private void OnEnable()
        {
            _humanRoot = GameSession.FindHumanRoot();
            SetDisplayedRoot(_humanRoot);
            if (turnController == null)
                return;
            turnController.TurnStarted += OnTurnStarted;
            OnTurnStarted(turnController.TurnNumber);
        }

        private void OnDisable()
        {
            if (_displayedRoot != null)
                _displayedRoot.ResourcesChanged -= RefreshResourceText;
            _displayedRoot = null;
            if (turnController != null)
                turnController.TurnStarted -= OnTurnStarted;
        }

        // Dev-only (see GameTurnController.debugFollowAiVision): points the bar at `root`
        // instead of the human's own — the acting AI's own AP/resources, for the same span its
        // hand is shown via CardHandUI.ShowAiHandDebug. Null (or the human's own root) reverts to
        // normal.
        public void ShowRootDebug(PlayerRoot root)
        {
            SetDisplayedRoot(root != null ? root : _humanRoot);
        }

        public void HideRootDebug()
        {
            SetDisplayedRoot(_humanRoot);
        }

        private void SetDisplayedRoot(PlayerRoot root)
        {
            if (root == _displayedRoot)
                return;
            if (_displayedRoot != null)
                _displayedRoot.ResourcesChanged -= RefreshResourceText;
            _displayedRoot = root;
            if (_displayedRoot != null)
                _displayedRoot.ResourcesChanged += RefreshResourceText;
            RefreshResourceText();
        }

        private void OnTurnStarted(int turnNumber)
        {
            if (turnText != null)
                turnText.text = turnNumber.ToString();
        }

        // Driven by PlayerRoot.ResourcesChanged now instead of polling every frame — AP and the
        // four stockpiled resources only actually change on a handful of discrete actions
        // (spend, citadel income, dice purchase/refund), not continuously, so there was never a
        // reason to re-format and re-assign five TMP strings 60 times a second.
        private void RefreshResourceText()
        {
            if (_displayedRoot == null)
                return;

            if (apText != null)
                apText.text = _displayedRoot.ActionPoints.ToString();
            if (humanText != null)
                humanText.text = _displayedRoot.GetResource(ResourceType.Human).ToString();
            if (energyText != null)
                energyText.text = _displayedRoot.GetResource(ResourceType.Energy).ToString();
            if (materialsText != null)
                materialsText.text = _displayedRoot.GetResource(ResourceType.Materials).ToString();
            if (techText != null)
                techText.text = _displayedRoot.GetResource(ResourceType.Tech).ToString();
        }
    }
}
