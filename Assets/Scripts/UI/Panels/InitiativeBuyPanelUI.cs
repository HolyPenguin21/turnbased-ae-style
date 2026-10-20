using System;
using Game.Economy;
using Game.Map;
using Game.Players;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // "Buy Initiative Token" block: shown only for the human player. AI purchases are already
    // planned/applied by InitiativeCoordinatorV2 before the popup opens. Each row spends 1 unit
    // of that resource per click toward the die currently being assembled — the player can mix
    // any of the four resources, in any order, to cover one die's progressive cost (see
    // PlayerRoot.PurchaseInitiativeToken). Locked once TurnOrderPopupUI.RollAll fires, so nothing
    // can change after tokens start rolling.
    public class InitiativeBuyPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text priceText;
        // Exactly 4, wired in the Editor in this fixed order: Human, Energy, Materials, Tech.
        [SerializeField] private BuyTokenRowUI[] resourceRows;

        private static readonly ResourceType[] RowOrder =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        private PlayerRoot _root;
        private bool _locked;

        // Total cost of the die currently being assembled: 1 -> 2 -> 4 -> 8 -> 16. Paid off in
        // 1-unit clicks that can mix any H/E/M/T (see PlayerRoot.PurchaseInitiativeToken), so the
        // panel shows progress toward it rather than a single one-shot price.
        private int CurrentTokenCost => _root != null ? _root.NextInitiativeTokenCost : 0;
        private int CurrentTokenProgress => _root != null ? _root.CurrentTokenUnitsContributed : 0;

        // Fired whenever a purchase/refund changes the player's bonus tokens count, so
        // TurnOrderPopupUI can resize that player's TokenRowUI slots to match.
        public event Action TokenCountChanged;

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
            if (_locked || _root == null || !_root.PurchaseInitiativeToken(type))
                return;
            RefreshRows();
            TokenCountChanged?.Invoke();
        }

        private void OnRefund(ResourceType type)
        {
            if (_locked || _root == null || !_root.RefundLastInitiativeToken(type))
                return;
            RefreshRows();
            TokenCountChanged?.Invoke();
        }

        private void RefreshRows()
        {
            if (resourceRows == null || _root == null)
                return;

            if (priceText != null)
                priceText.text = _root.CanBuyMoreInitiativeTokens ? $"{CurrentTokenProgress}/{CurrentTokenCost}" : "—";
            foreach (BuyTokenRowUI row in resourceRows)
                if (row != null)
                    row.Refresh(_root, _locked);
        }
    }
}
