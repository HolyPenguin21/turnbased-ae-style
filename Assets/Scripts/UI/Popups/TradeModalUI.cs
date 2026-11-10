using System;
using Game.Core;
using Game.Economy;
using Game.Map;
using Game.Turns;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class TradeModalUI : MonoBehaviour
    {
        private const int ResourceCount = 4;
        [SerializeField] private Button tradeButton, confirmButton, cancelButton;
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text priceText;
        // All three arrays are indexed by (int)ResourceType: Human, Energy, Materials, Tech.
        [SerializeField] private Button[] giveButtons;
        [SerializeField] private Button[] receiveButtons;
        [SerializeField] private TMP_Text[] resultTexts;
        [SerializeField] private GameTurnController turnController;
        public event Action VisibilityChanged;
        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
        private PlayerRoot root;
        // Last deal settings. Kept while the window is closed and across turns; reset only for a new match.
        private ResourceType? give, receive;
        private long settingsGeneration = -1;
        private string notice;
        private long generation;
        private float nextAccessCheck;
        private bool CanTrade => root != null && GameSession.IsCurrent(generation)
            && TradeActions.HasAccess(root.Setup) && root.TradeTurnOpen && turnController != null
            && turnController.CurrentPlayer == root.Setup && turnController.TurnConfirmed
            && turnController.Phase == StrategicTurnPhase.Ready;
        private void Awake()
        {
            panelRoot.SetActive(false);
            tradeButton.onClick.AddListener(Show);
            confirmButton.onClick.AddListener(Confirm);
            cancelButton.onClick.AddListener(Hide);
            for (int i = 0; i < ResourceCount; i++)
            {
                var type = (ResourceType)i;
                UIUtility.ResetRuntimeListeners(giveButtons[i]);
                UIUtility.ResetRuntimeListeners(receiveButtons[i]);
                giveButtons[i].onClick.AddListener(() => SelectGive(type));
                receiveButtons[i].onClick.AddListener(() => SelectReceive(type));
            }
        }
        private void Update()
        {
            if (IsShowing && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Hide();
            if (Time.unscaledTime < nextAccessCheck) return;
            nextAccessCheck = Time.unscaledTime + 0.2f;
            var human = GameSession.FindHumanPlayer();
            bool access = TradeActions.HasAccess(human);
            tradeButton.gameObject.SetActive(access);
            tradeButton.interactable = access && turnController != null && turnController.CurrentPlayer == human
                && turnController.TurnConfirmed && turnController.Phase == StrategicTurnPhase.Ready
                && (!turnController.InputBlocked || IsShowing);
            if (IsShowing && !CanTrade) Hide();
        }
        public void Show()
        {
            if (IsShowing) return;
            generation = GameSession.Generation;
            Bind(PlayerRootRegistry.FindFor(GameSession.FindHumanPlayer()));
            if (!CanTrade || turnController.InputBlocked) { Bind(null); return; }
            if (settingsGeneration != generation) { give = receive = null; settingsGeneration = generation; }
            notice = null;
            panelRoot.SetActive(true); panelRoot.transform.SetAsLastSibling();
            Refresh(); VisibilityChanged?.Invoke();
        }
        private void Bind(PlayerRoot value)
        {
            if (root != null) { root.ResourcesChanged -= Refresh; root.TradeChanged -= Refresh; }
            root = value;
            if (root != null) { root.ResourcesChanged += Refresh; root.TradeChanged += Refresh; }
        }
        private void SelectGive(ResourceType value)
        {
            give = value;
            if (receive == value) receive = null;
            notice = null; Refresh();
        }
        private void SelectReceive(ResourceType value)
        {
            if (give == null || give == value) return;
            receive = value; notice = null; Refresh();
        }
        private bool TrySimulate(out int[] after, out int nextRate)
        {
            after = null; nextRate = 0;
            if (root == null || give == null || receive == null) return false;
            var stock = new int[ResourceCount];
            for (int i = 0; i < ResourceCount; i++) stock[i] = root.GetResource((ResourceType)i);
            return TradeActions.Simulate(stock, root.TradeRate,
                new[] { new TradeStep(give.Value, receive.Value) }, out after, out nextRate);
        }
        // The only place resources change: the player agreed to the deal. Window stays open (the rate has
        // risen) so the same deal can be repeated or adjusted; Cancel / Escape closes it.
        private void Confirm()
        {
            if (!CanTrade) { Hide(); return; }
            if (give == null || receive == null) return;
            notice = TradeActions.TryExchange(root.Setup, root.TradeRate,
                new[] { new TradeStep(give.Value, receive.Value) }, generation, out string reason) ? null : reason;
            Refresh();
        }
        private void Refresh()
        {
            if (root == null || !IsShowing) return;
            bool locked = !CanTrade;
            bool deal = give != null && receive != null;
            bool affordable = TrySimulate(out var after, out int nextRate);
            for (int i = 0; i < ResourceCount; i++)
            {
                var type = (ResourceType)i;
                giveButtons[i].interactable = !locked && give != type;
                receiveButtons[i].gameObject.SetActive(give != type);
                receiveButtons[i].interactable = !locked && give != null && receive != type;
                resultTexts[i].text = (affordable ? after[i] : root.GetResource(type)).ToString();
            }
            confirmButton.interactable = !locked && affordable;
            cancelButton.interactable = true;
            priceText.text = BuildPriceText(deal, affordable, nextRate);
        }
        private string BuildPriceText(bool deal, bool affordable, int nextRate)
        {
            string text;
            int rate = root.TradeRate;
            if (give == null) text = "Select the resource to trade away.";
            else if (receive == null) text = $"Give {rate} {give} for 1 resource.\nSelect the resource to receive.";
            else if (affordable)
                text = $"Give {rate} {give} for 1 {receive}  |  Next: {nextRate}:1\nConfirm to complete the exchange.";
            else text = $"Give {rate} {give} for 1 {receive}\nNot enough {give}: need {rate}, have {root.GetResource(give.Value)}.";
            text += "\nA turn without trades lowers the rate by 1 (minimum 2:1). No AP cost.";
            return string.IsNullOrEmpty(notice) ? text : notice + "\n" + text;
        }
        public void Hide()
        {
            bool shown = IsShowing;
            panelRoot.SetActive(false); Bind(null);
            if (shown) VisibilityChanged?.Invoke();
        }
        private void OnDisable() => Hide();
        private void OnDestroy()
        {
            if (tradeButton != null) tradeButton.onClick.RemoveListener(Show);
            if (confirmButton != null) confirmButton.onClick.RemoveListener(Confirm);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(Hide);
            Bind(null);
        }
    }
}
