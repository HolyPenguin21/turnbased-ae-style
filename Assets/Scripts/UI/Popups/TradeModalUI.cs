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
        [SerializeField] private Button tradeButton, closeButton;
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text priceText;
        [SerializeField] private TradeResourceRowUI[] resourceRows;
        [SerializeField] private GameTurnController turnController;
        public event Action VisibilityChanged;
        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
        private PlayerRoot root;
        private ResourceType source;
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
            closeButton.onClick.AddListener(Hide);
            for (int i = 0; i < resourceRows.Length; i++)
                resourceRows[i].Setup((ResourceType)i, Select, Exchange);
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
            generation = GameSession.Generation;
            Bind(PlayerRootRegistry.FindFor(GameSession.FindHumanPlayer()));
            if (!CanTrade || turnController.InputBlocked) { Bind(null); return; }
            panelRoot.SetActive(true); panelRoot.transform.SetAsLastSibling();
            Refresh(); VisibilityChanged?.Invoke();
        }
        private void Bind(PlayerRoot value)
        {
            if (root != null) { root.ResourcesChanged -= Refresh; root.TradeChanged -= Refresh; }
            root = value;
            if (root != null) { root.ResourcesChanged += Refresh; root.TradeChanged += Refresh; }
        }
        private void Select(ResourceType value) { source = value; Refresh(); }
        private void Exchange(ResourceType receive)
        {
            if (!CanTrade) { Hide(); return; }
            if (!TradeActions.TryExchange(root.Setup, root.TradeRate,
                    new[] { new TradeStep(source, receive) }, generation, out string reason))
                priceText.text = reason;
            else Refresh();
        }
        private void Refresh()
        {
            if (root == null || !IsShowing) return;
            priceText.text = $"Give {root.TradeRate} {source} for 1 resource  |  Next: {(long)root.TradeRate + 1}:1\n"
                + "- Select what to give     + Receive one resource\nA turn without trades lowers the rate by 1 (minimum 2:1). No AP cost.";
            foreach (var row in resourceRows) row.Refresh(root, source, !CanTrade);
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
            if (closeButton != null) closeButton.onClick.RemoveListener(Hide);
            Bind(null);
        }
    }
}
