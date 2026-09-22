using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Core;
using UnityEngine;

namespace Game.UI
{
    // One entry in HexInfoPanelUI's extraction-Facility hex-action row (see
    // ResourceActionRowUI): a visible label, a click callback, and the CardDefinition the button
    // reads AP/resource badges off for its hover preview.
    public sealed class HexActionDescriptor
    {
        public string Label;
        public Action OnClick;
        // Non-null only for cost actions (the extraction-Facility buttons) — drives the
        // hover cost preview in ResourceActionButtonUI. Null => simple label-only action.
        public CardDefinition CostSource;

        public HexActionDescriptor(string label, Action onClick, CardDefinition costSource = null)
        {
            Label = label;
            OnClick = onClick;
            CostSource = costSource;
        }
    }

    // The contextual "build an extraction Facility" buttons next to Garrison/Base/Research/
    // Production on HexInfoPanelUI — up to 4 entries, one per resource type (see
    // HexSelectionController.RefreshResourceActionRow). Research/Production used to also live
    // here but now have their own fixed buttons on HexInfoPanelUI's nav row instead. Same
    // instantiate-per-item + tracked-list-cleared-before-every-render pattern as ArmyButtonRowUI,
    // without that one's scroll/paging machinery — the row is sized for all 4 possible entries at
    // once, so it always fits without scrolling.
    public class ResourceActionRowUI : MonoBehaviour
    {
        [SerializeField] private Transform buttonContainer;
        [SerializeField] private GameConfig gameConfig;

        private readonly List<ResourceActionButtonUI> _buttons = new List<ResourceActionButtonUI>();

        public void Show(IReadOnlyList<HexActionDescriptor> actions)
        {
            ClearButtons();
            if (buttonContainer == null || gameConfig == null || gameConfig.resourceActionButtonPrefab == null || actions == null)
                return;

            foreach (HexActionDescriptor action in actions)
            {
                if (action == null)
                    continue;
                ResourceActionButtonUI button = Instantiate(gameConfig.resourceActionButtonPrefab, buttonContainer);
                button.Setup(action);
                _buttons.Add(button);
            }
        }

        public void Hide() => ClearButtons();

        private void ClearButtons() => UIListUtility.DestroyAndClear(_buttons);
    }
}
