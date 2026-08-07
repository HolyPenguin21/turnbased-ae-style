using Game.Core;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Turns;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // Always-on top-of-screen HUD: the human player's AP + stockpiled resources, plus the
    // turn counter. Resources are polled every frame instead of hooking into every place that
    // can change them (citadel income, dice purchases, AP spend later) — cheap for a handful
    // of ints. The turn counter only changes once per full turn cycle, so it updates off
    // GameTurnController.TurnStarted instead of being re-set 60 times a second for nothing.
    public class ResourceBarUI : MonoBehaviour
    {
        [SerializeField] private GameTurnController turnController;
        [SerializeField] private TMP_Text apText;
        [SerializeField] private TMP_Text humanText;
        [SerializeField] private TMP_Text energyText;
        [SerializeField] private TMP_Text materialsText;
        [SerializeField] private TMP_Text techText;
        [SerializeField] private TMP_Text turnText;

        // Hidden until a citadel exists to report on — GameTurnController calls this once,
        // right after citadel setup finishes, and it never hides again after that.
        public void Show()
        {
            gameObject.SetActive(true);
        }

        private void OnEnable()
        {
            if (turnController == null)
                return;
            turnController.TurnStarted += OnTurnStarted;
            OnTurnStarted(turnController.TurnNumber);
        }

        private void OnDisable()
        {
            if (turnController != null)
                turnController.TurnStarted -= OnTurnStarted;
        }

        private void OnTurnStarted(int turnNumber)
        {
            if (turnText != null)
                turnText.text = turnNumber.ToString();
        }

        private void Update()
        {
            PlayerSetupData human = GameSession.Players?.Find(p => p != null && p.IsHuman);
            PlayerRoot root = human != null ? PlayerRootRegistry.FindFor(human) : null;
            if (root == null)
                return;

            if (apText != null)
                apText.text = root.ActionPoints.ToString();
            if (humanText != null)
                humanText.text = root.GetResource(ResourceType.Human).ToString();
            if (energyText != null)
                energyText.text = root.GetResource(ResourceType.Energy).ToString();
            if (materialsText != null)
                materialsText.text = root.GetResource(ResourceType.Materials).ToString();
            if (techText != null)
                techText.text = root.GetResource(ResourceType.Tech).ToString();
        }
    }
}
