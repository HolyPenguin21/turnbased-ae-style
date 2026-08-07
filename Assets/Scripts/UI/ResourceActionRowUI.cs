using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Core;
using Game.Economy;
using UnityEngine;

namespace Game.UI
{
    // Up to 4 "build an extraction Facility" buttons next to Garrison/Base on HexInfoPanelUI —
    // one per resource type the selected hex still has an open collection slot for (see
    // HexSelectionController.SelectHex). Same instantiate-per-item + tracked-list-cleared-before-
    // every-render pattern as ArmyButtonRowUI, without that one's scroll/paging machinery since
    // there are at most 4 entries here — always fits without scrolling.
    public class ResourceActionRowUI : MonoBehaviour
    {
        [SerializeField] private Transform buttonContainer;
        [SerializeField] private GameConfig gameConfig;

        private readonly List<ResourceActionButtonUI> _buttons = new List<ResourceActionButtonUI>();

        public void Show(IReadOnlyList<(ResourceType type, CardDefinition definition)> actions, Action<ResourceType> onClick)
        {
            ClearButtons();
            if (buttonContainer == null || gameConfig == null || gameConfig.resourceActionButtonPrefab == null || actions == null)
                return;

            foreach ((ResourceType type, CardDefinition definition) in actions)
            {
                ResourceActionButtonUI button = Instantiate(gameConfig.resourceActionButtonPrefab, buttonContainer);
                button.Setup(type, definition, onClick);
                _buttons.Add(button);
            }
        }

        public void Hide() => ClearButtons();

        private void ClearButtons() => UIListUtility.DestroyAndClear(_buttons);
    }
}
