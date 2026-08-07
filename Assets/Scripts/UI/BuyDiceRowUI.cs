using System;
using Game.Economy;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One resource's row in the initiative dice buy panel: coloured icon (matches
    // ResourceIconVisual's map colours) + current amount + "-" (spend `price` of this
    // resource, gain one bonus die) / "+" (undo: refund this resource, lose one bonus die,
    // only enabled if a die was actually bought with THIS resource this turn).
    public class BuyDiceRowUI : MonoBehaviour
    {
        [SerializeField] private Image iconImage;
        [SerializeField] private TMP_Text amountText;
        [SerializeField] private Button buyButton; // "-": spend resource, gain a die
        [SerializeField] private Button refundButton; // "+": undo, refund resource, lose a die

        private ResourceType _type;
        private Action<ResourceType> _onBuy;
        private Action<ResourceType> _onRefund;

        public void Setup(ResourceType type, Action<ResourceType> onBuy, Action<ResourceType> onRefund)
        {
            _type = type;
            _onBuy = onBuy;
            _onRefund = onRefund;

            if (iconImage != null)
                iconImage.color = ResourceIconVisual.GetColor(type);

            if (buyButton != null)
            {
                buyButton.onClick.RemoveAllListeners();
                buyButton.onClick.AddListener(() => _onBuy?.Invoke(_type));
            }
            if (refundButton != null)
            {
                refundButton.onClick.RemoveAllListeners();
                refundButton.onClick.AddListener(() => _onRefund?.Invoke(_type));
            }
        }

        // locked overrides affordability entirely — used once Roll has been pressed, when
        // buying/refunding must stop no matter what the player could otherwise afford.
        public void Refresh(PlayerRoot root, int price, bool locked)
        {
            if (root == null)
                return;

            if (amountText != null)
                amountText.text = root.GetResource(_type).ToString();
            if (buyButton != null)
                buyButton.interactable = !locked && root.CanBuyInitiativeDie(_type, price);
            if (refundButton != null)
                refundButton.interactable = !locked && root.CanRefundInitiativeDie(_type);
        }
    }
}
