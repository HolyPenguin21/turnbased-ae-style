using System;
using Game.Economy;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One resource row in the initiative buy panel: coloured icon + current amount + "-" to pay
    // the ENTIRE current progressive die price from this resource / "+" to undo the most recent
    // die when this resource paid for it.
    public class BuyDiceRowUI : MonoBehaviour
    {
        [SerializeField] private Image iconImage;
        [SerializeField] private TMP_Text amountText;
        [SerializeField] private Button buyButton;
        [SerializeField] private Button refundButton;

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

        // locked overrides affordability entirely once Roll has been pressed.
        public void Refresh(PlayerRoot root, bool locked)
        {
            if (root == null)
                return;

            if (amountText != null)
                amountText.text = root.GetResource(_type).ToString();
            if (buyButton != null)
                buyButton.interactable = !locked && root.CanBuyInitiativeDie(_type);
            if (refundButton != null)
                refundButton.interactable = !locked && root.CanRefundInitiativeDie(_type);
        }
    }
}
