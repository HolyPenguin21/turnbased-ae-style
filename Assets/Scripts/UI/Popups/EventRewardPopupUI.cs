using Game.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Also presents the opening opponent greeting through ShowMessage; both modes share the
    // same acknowledgement and input-blocking surface, without granting any reward.
    // Shown once a Hex Event's reward has actually been granted (see HexSelectionController.
    // Events.cs's GrantEventReward). Per the user's own layout: the same hero/unit portrait as
    // EventChoicePopupUI (see EventChoicePopupUI.ResolvePortrait), one summary sentence
    // describing what was granted, and the rewarded card's own detail art if a card was part of
    // it — no per-reward row list. Reuses the project's established Space-confirm convention (see
    // CLAUDE.md, UIFocusUtility.WasSpacePressed()) exactly like PopupPanelUI — a plain
    // acknowledgement, not a decision, unlike EventChoicePopupUI.
    public class EventRewardPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private Image unitImage;
        [SerializeField] private TMP_Text summaryText;
        [SerializeField] private Image rewardCardImage;
        [SerializeField] private Button okButton;

        private int _shownFrame = -1;
        private string _rewardTitle;
        private bool _rewardArtPreserveAspect;
        private bool _defaultsCaptured;

        private static readonly (ResourceType type, string label)[] ResourceLabels =
        {
            (ResourceType.Human, "Human"), (ResourceType.Energy, "Energy"),
            (ResourceType.Materials, "Materials"), (ResourceType.Tech, "Tech"),
        };

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
        public event Action VisibilityChanged;

        public void Show(HexEventRegistry.Entry entry, ArmyData recipient)
        {
            if (entry?.Definition == null || panelRoot == null)
                return;

            CardDefinition rewardedCard = entry.ResolvedCardRewards.Select(r => r.card).FirstOrDefault(c => c != null);
            Sprite art = rewardedCard != null ? (rewardedCard.detailArt != null ? rewardedCard.detailArt : rewardedCard.art) : null;
            ShowContent(false, EventChoicePopupUI.ResolvePortrait(recipient), art, BuildSummary(entry, recipient));
        }

        public void ShowMessage(Sprite heroPortrait, Sprite factionLogo, string message)
        {
            if (panelRoot == null)
                return;
            ShowContent(true, heroPortrait, factionLogo, message);
        }

        private void ShowContent(bool messageMode, Sprite portrait, Sprite art, string summary)
        {
            if (!_defaultsCaptured)
            {
                _rewardTitle = titleText != null ? titleText.text : "Reward";
                _rewardArtPreserveAspect = rewardCardImage != null && rewardCardImage.preserveAspect;
                _defaultsCaptured = true;
            }
            _shownFrame = Time.frameCount;
            if (titleText != null)
                titleText.text = messageMode ? "Message" : _rewardTitle;
            if (unitImage != null)
            {
                unitImage.sprite = portrait;
                unitImage.gameObject.SetActive(portrait != null);
            }
            if (rewardCardImage != null)
            {
                rewardCardImage.sprite = art;
                rewardCardImage.preserveAspect = messageMode || _rewardArtPreserveAspect;
                rewardCardImage.gameObject.SetActive(art != null);
            }

            if (summaryText != null)
                summaryText.text = summary;

            if (okButton != null)
            {
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(okButton);
                okButton.onClick.AddListener(Acknowledge);
            }
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
        }

        private static string BuildSummary(HexEventRegistry.Entry entry, ArmyData recipient)
        {
            string armyName = recipient?.Name ?? "The army";
            string resourceList = DescribeResources(entry.SelectedRewards);
            string cardList = string.Join(" and ", entry.ResolvedCardRewards
                .Where(r => r.card != null).Select(r => r.card.displayName));

            if (entry.Definition.grantsRandomCollectionCard)
            {
                if (!string.IsNullOrEmpty(entry.CollectionRewardError))
                    return entry.CollectionRewardError;
                if (string.IsNullOrEmpty(cardList))
                    return "You already own the maximum number of copies of every available card. The Library has no new card for your collection.";
                return $"Among the surviving books and military records, {armyName} uncovers forgotten knowledge. {cardList} has been added to your hand and permanently to your COLLECTION.";
            }

            bool hasResources = !string.IsNullOrEmpty(resourceList);
            bool hasCard = !string.IsNullOrEmpty(cardList);

            if (hasResources && hasCard)
                return $"{armyName} receives {resourceList} and {cardList} as a reward.";
            if (hasCard)
                return $"{armyName} receives {cardList} as a reward.";
            if (hasResources)
                return $"{armyName} receives {resourceList} as a reward.";
            return $"{armyName} finds nothing of value here.";
        }

        // "2 Materials, 1 Tech" — every ResourceType reward across the whole event, summed and
        // listed in a fixed order, rather than one sentence per RewardEntry (an event could
        // author its resources across several entries; the player only needs the total).
        private static string DescribeResources(List<RewardEntry> rewards)
        {
            var totals = new Dictionary<ResourceType, int>();
            foreach (RewardEntry reward in rewards)
            {
                if (reward.type != RewardType.Resources)
                    continue;
                foreach ((ResourceType type, string _) in ResourceLabels)
                {
                    int amount = reward.resources.Get(type);
                    if (amount <= 0)
                        continue;
                    totals.TryGetValue(type, out int existing);
                    totals[type] = existing + amount;
                }
            }
            if (totals.Count == 0)
                return null;

            var parts = new List<string>();
            foreach ((ResourceType type, string label) in ResourceLabels)
                if (totals.TryGetValue(type, out int amount))
                    parts.Add($"{amount} {label}");
            return string.Join(", ", parts);
        }

        public void Hide()
        {
            if (panelRoot == null || !panelRoot.activeSelf)
                return;
            panelRoot.SetActive(false);
            VisibilityChanged?.Invoke();
        }

        private void Acknowledge()
        {
            // A Submit used to confirm the turn must not also acknowledge its new popup.
            // Keep this input guard out of Hide so programmatic cleanup always works.
            if (_shownFrame != Time.frameCount)
                Hide();
        }

        private void Update()
        {
            if (!IsShowing || _shownFrame == Time.frameCount)
                return;
            if (okButton == null || !okButton.gameObject.activeInHierarchy || !okButton.interactable)
                return;
            if (UIFocusUtility.WasSpacePressed())
            {
                Game.Audio.GameAudioManager.Instance?.PlayClick();
                Acknowledge();
            }
        }
    }
}

