using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Core;
using UnityEngine;

namespace Game.UI
{
    // One entry in HexInfoPanelUI's hex-action row (see ResourceActionRowUI). Deliberately
    // minimal so the row can carry more than resource extraction: a visible label, a click
    // callback, and — only for actions that actually cost something — the CardDefinition the
    // button reads AP/resource badges off for its hover preview. Research/Production have no
    // cost source at this stage and pass null, which puts the button in its label-only mode.
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

    // The contextual hex-action buttons next to Garrison/Base on HexInfoPanelUI — originally
    // just up to 4 "build an extraction Facility" buttons, now a generic list of whatever
    // actions the selected hex currently offers (Research/Production first, then extraction —
    // see HexSelectionController.RefreshResourceActionRow). Same instantiate-per-item +
    // tracked-list-cleared-before-every-render pattern as ArmyButtonRowUI, without that one's
    // scroll/paging machinery — the row is sized for all 6 possible entries at once (2
    // contextual + 4 extraction), so it always fits without scrolling.
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
