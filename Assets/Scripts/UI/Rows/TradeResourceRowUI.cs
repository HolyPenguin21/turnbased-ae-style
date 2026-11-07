using System;
using Game.Economy;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class TradeResourceRowUI : MonoBehaviour
    {
        [SerializeField] private Image iconImage;
        [SerializeField] private TMP_Text amountText;
        [SerializeField] private Button buyButton;
        [SerializeField] private Button refundButton;
        private ResourceType type;
        public void Setup(ResourceType resource, Action<ResourceType> select, Action<ResourceType> exchange)
        {
            type = resource;
            iconImage.color = ResourceIconVisual.GetColor(type);
            UIUtility.ResetRuntimeListeners(buyButton);
            UIUtility.ResetRuntimeListeners(refundButton);
            buyButton.onClick.AddListener(() => select(type));
            refundButton.onClick.AddListener(() => exchange(type));
        }
        public void Refresh(PlayerRoot root, ResourceType source, bool locked)
        {
            amountText.text = root.GetResource(type).ToString() + (source == type ? " <" : "");
            buyButton.interactable = !locked;
            refundButton.interactable = !locked && root.TradeRate < int.MaxValue && source != type && root.GetResource(source) >= root.TradeRate;
        }
    }
}
