using System;
using Game.Cards;
using Game.Economy;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // One "build an extraction Facility" button on HexInfoPanelUI's resource-action row (see
    // ResourceActionRowUI) — mirrors ArmyButtonUI's Setup/click pattern. Icon appearance comes
    // straight from the prefab, not tinted per-resource at runtime. On hover, swaps the label
    // for the definition's cost badges (AP + non-zero resources) — same "icon + number" row
    // convention as ResourceBarUI/BaseSlotCardUI's own upgrade-cost preview.
    public class ResourceActionButtonUI : MonoBehaviour
    {
        [SerializeField] private Image icon;
        [SerializeField] private TMP_Text label;
        [SerializeField] private Button button;
        [SerializeField] private GameObject costPreviewRoot;
        // Order: AP, Human, Energy, Materials, Tech — same convention as BaseSlotCardUI.
        [SerializeField] private Image[] costBadgeIcons;
        [SerializeField] private TMP_Text[] costBadgeAmounts;

        private void Awake()
        {
            AddHoverTrigger(button, ShowCostPreview, HideCostPreview);
        }

        public void Setup(ResourceType type, CardDefinition definition, Action<ResourceType> onClick)
        {
            if (label != null)
                label.text = definition != null ? $"Build {definition.displayName}" : string.Empty;
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => onClick?.Invoke(type));
            }

            if (costPreviewRoot != null)
                costPreviewRoot.SetActive(false);
            if (definition == null)
                return;

            ResourceCost cost = definition.resourceCost;
            SetBadge(0, definition.apCost);
            SetBadge(1, cost != null ? cost.human : 0);
            SetBadge(2, cost != null ? cost.energy : 0);
            SetBadge(3, cost != null ? cost.materials : 0);
            SetBadge(4, cost != null ? cost.tech : 0);
        }

        private void SetBadge(int index, int amount)
        {
            if (costBadgeIcons == null || index >= costBadgeIcons.Length || costBadgeIcons[index] == null)
                return;
            bool visible = amount > 0;
            costBadgeIcons[index].gameObject.SetActive(visible);
            if (visible && costBadgeAmounts != null && index < costBadgeAmounts.Length && costBadgeAmounts[index] != null)
                costBadgeAmounts[index].text = amount.ToString();
        }

        private void ShowCostPreview()
        {
            if (label != null)
                label.gameObject.SetActive(false);
            if (costPreviewRoot != null)
                costPreviewRoot.SetActive(true);
        }

        private void HideCostPreview()
        {
            if (label != null)
                label.gameObject.SetActive(true);
            if (costPreviewRoot != null)
                costPreviewRoot.SetActive(false);
        }

        private static void AddHoverTrigger(Button targetButton, Action onEnter, Action onExit)
        {
            if (targetButton == null)
                return;
            EventTrigger trigger = targetButton.gameObject.AddComponent<EventTrigger>();
            trigger.triggers.Add(MakeHoverEntry(EventTriggerType.PointerEnter, onEnter));
            trigger.triggers.Add(MakeHoverEntry(EventTriggerType.PointerExit, onExit));
        }

        private static EventTrigger.Entry MakeHoverEntry(EventTriggerType type, Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            return entry;
        }
    }
}
