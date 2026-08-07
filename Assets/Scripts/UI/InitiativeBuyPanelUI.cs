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
    // player name/colour, the flat price of one bonus die, and one BuyDiceRowUI per resource
    // type to spend on it. Locked once TurnOrderPopupUI.RollAll fires, so nothing can change
    // after the dice are already rolling.
    public class InitiativeBuyPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text playerNameText;
        [SerializeField] private TMP_Text priceText;
        // Exactly 4, wired in the Editor in this fixed order: Human, Energy, Materials, Tech.
        [SerializeField] private BuyDiceRowUI[] resourceRows;

        private static readonly ResourceType[] RowOrder =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        private PlayerRoot _root;
        private int _price;
        private bool _locked;

        // Fired whenever a purchase/refund changes the player's bonus dice count, so
        // TurnOrderPopupUI can resize that player's DiceRowUI slots to match.
        public event Action DiceCountChanged;

        public void Show(PlayerSetupData player, PlayerRoot root, int price)
        {
            _root = root;
            _price = price;
            _locked = false;

            bool active = player != null && player.IsHuman && root != null;
            if (panelRoot != null)
                panelRoot.SetActive(active);
            if (!active)
                return;

            if (playerNameText != null)
            {
                playerNameText.text = player.Nickname;
                playerNameText.color = root.Color;
            }
            if (priceText != null)
                priceText.text = price.ToString();

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
            if (_locked || _root == null || !_root.CanBuyInitiativeDie(type, _price))
                return;
            _root.BuyInitiativeDie(type, _price);
            RefreshRows();
            DiceCountChanged?.Invoke();
        }

        private void OnRefund(ResourceType type)
        {
            if (_locked || _root == null || !_root.CanRefundInitiativeDie(type))
                return;
            _root.RefundInitiativeDie(type, _price);
            RefreshRows();
            DiceCountChanged?.Invoke();
        }

        private void RefreshRows()
        {
            if (resourceRows == null || _root == null)
                return;
            foreach (BuyDiceRowUI row in resourceRows)
                if (row != null)
                    row.Refresh(_root, _price, _locked);
        }
    }
}
