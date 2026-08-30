using System;
using Game.Economy;
using Game.Map;
using Game.Players;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // "Buy Initiative Die" block: shown only for the human player (AI already bought its dice
    // via InitiativeDiceAI before the popup opens, so there's nothing for it to show here) —
    // the flat price of one bonus die, and one BuyDiceRowUI per resource type to spend on it
    // (the player's own name/colour is shown by their DiceRowUI below this panel instead).
    // Locked once TurnOrderPopupUI.RollAll fires, so nothing can change after the dice are
    // already rolling.
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

        // Progressive price now: the cost of the NEXT die climbs 1 -> 2 -> 4 -> 8 -> 16 as this
        // player buys (InitiativeRules.NextBonusDieCost), so it is re-read from the root after
        // every buy/refund rather than being a single flat number passed in once. 0 once the
        // player has bought the maximum, which also disables every row's buy button.
        private int CurrentPrice => _root != null ? _root.NextInitiativeDieCost : 0;

        // Fired whenever a purchase/refund changes the player's bonus dice count, so
        // TurnOrderPopupUI can resize that player's DiceRowUI slots to match.
        public event Action DiceCountChanged;

        // `price` is kept in the signature for the existing call site but ignored — the real
        // cost is the progressive CurrentPrice, refreshed after every buy/refund.
        public void Show(PlayerSetupData player, PlayerRoot root, int price)
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
            if (_locked || _root == null || !_root.CanBuyInitiativeDie(type, CurrentPrice))
                return;
            _root.BuyInitiativeDie(type, CurrentPrice);
            RefreshRows();
            DiceCountChanged?.Invoke();
        }

        private void OnRefund(ResourceType type)
        {
            if (_locked || _root == null || !_root.CanRefundInitiativeDie(type))
                return;
            _root.RefundInitiativeDie(type, CurrentPrice);
            RefreshRows();
            DiceCountChanged?.Invoke();
        }

        private void RefreshRows()
        {
            if (resourceRows == null || _root == null)
                return;
            int price = CurrentPrice;
            if (priceText != null)
                priceText.text = _root.CanBuyMoreInitiativeDice ? price.ToString() : "—";
            foreach (BuyDiceRowUI row in resourceRows)
                if (row != null)
                    row.Refresh(_root, price, _locked);
        }
    }
}
