using System;
using Game.Economy;
using Game.Map;
using Game.Players;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // "Buy Initiative Die" block: shown only for the human player. AI purchases are already
    // planned/applied by InitiativeCoordinatorV2 before the popup opens. Each row pays the full
    // progressive price from exactly one resource type — the same purchase semantics the AI uses.
    // Locked once TurnOrderPopupUI.RollAll fires, so nothing can change after dice start rolling.
    public class InitiativeBuyPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text priceText;
        // Exactly 4, wired in the Editor in this fixed order: Human, Energy, Materials, Tech.
        [SerializeField] private BuyDiceRowUI[] resourceRows;

        private static readonly ResourceType[] RowOrder =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        private PlayerRoot _root;
        private bool _locked;

        // Cost of the NEXT die: 1 -> 2 -> 4 -> 8 -> 16. Re-read after every buy/refund.
        private int CurrentPrice => _root != null ? _root.NextInitiativeDieCost : 0;

        // Fired whenever a purchase/refund changes the player's bonus dice count, so
        // TurnOrderPopupUI can resize that player's DiceRowUI slots to match.
        public event Action DiceCountChanged;

        public void Show(PlayerSetupData player, PlayerRoot root)
        {
            _root = root;
            _locked = false;

            bool active = player != null && player.IsHuman && root != null;
            if (panelRoot != null)
                panelRoot.SetActive(active);
            if (!active)
                return;

            if (resourceRows != null)
                for (int i = 0; i < resourceRows.Length && i < RowOrder.Length; i++)
                    resourceRows[i].Setup(RowOrder[i], OnBuy, OnRefund);

            RefreshRows();
        }

        public void Lock()
        {
            _locked = true;
            RefreshRows();
        }

        private void OnBuy(ResourceType type)
        {
            if (_locked || _root == null || !_root.PurchaseInitiativeDie(type))
                return;
            RefreshRows();
            DiceCountChanged?.Invoke();
        }

        private void OnRefund(ResourceType type)
        {
            if (_locked || _root == null || !_root.RefundLastInitiativeDie(type))
                return;
            RefreshRows();
            DiceCountChanged?.Invoke();
        }

        private void RefreshRows()
        {
            if (resourceRows == null || _root == null)
                return;

            if (priceText != null)
                priceText.text = _root.CanBuyMoreInitiativeDice ? CurrentPrice.ToString() : "—";
            foreach (BuyDiceRowUI row in resourceRows)
                if (row != null)
                    row.Refresh(_root, _locked);
        }
    }
}
