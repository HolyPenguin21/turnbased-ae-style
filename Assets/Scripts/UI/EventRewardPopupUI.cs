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
    // Shown once a Hex Event's reward has actually been granted (see HexSelectionController.
    // Events.cs's GrantEventReward). Per the user's own layout: the same hero/unit portrait as
    // EventChoicePopupUI (see EventChoicePopupUI.ResolvePortrait), one summary sentence
    // describing what was granted, and the rewarded card's own detail art if a card was part of
    // it — no per-reward row list. Reuses the project's established Space-confirm convention (see
    // CLAUDE.md, UIFocusUtility.WasSpacePressed()) exactly like SpawnHintPopupUI — a plain
    // acknowledgement, not a decision, unlike EventChoicePopupUI.
    public class EventRewardPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Image unitImage;
        [SerializeField] private TMP_Text summaryText;
        [SerializeField] private Image rewardCardImage;
        [SerializeField] private Button okButton;

        private static readonly (ResourceType type, string label)[] ResourceLabels =
        {
            (ResourceType.Human, "Human"), (ResourceType.Energy, "Energy"),
            (ResourceType.Materials, "Materials"), (ResourceType.Tech, "Tech"),
        };

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
        public event Action VisibilityChanged;

        public void Show(HexEventRegistry.Entry entry, ArmyData recipient)
        {
            if (entry?.Definition == null)
                return;

            if (unitImage != null)
            {
                Sprite portrait = EventChoicePopupUI.ResolvePortrait(recipient);
                unitImage.sprite = portrait;
                unitImage.gameObject.SetActive(portrait != null);
            }

            CardDefinition rewardedCard = entry.ResolvedCardRewards.Select(r => r.card).FirstOrDefault(c => c != null);
            if (rewardCardImage != null)
            {
                Sprite art = rewardedCard != null ? (rewardedCard.detailArt != null ? rewardedCard.detailArt : rewardedCard.art) : null;
                rewardCardImage.sprite = art;
                rewardCardImage.gameObject.SetActive(art != null);
            }

            if (summaryText != null)
                summaryText.text = BuildSummary(entry, recipient);

            if (okButton != null)
            {
                okButton.onClick.RemoveAllListeners();
                okButton.onClick.AddListener(Hide);
            }
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
        }

        // "<Army> receives <resources> as a reward." / "<Army> receives <card> as a reward." /
        // "<Army> receives <resources> and <card> as a reward." — the three shapes the user asked
        // for, picked based on what this event's rewards actually contain.
        private static string BuildSummary(HexEventRegistry.Entry entry, ArmyData recipient)
        {
            string armyName = recipient?.Name ?? "The army";
            string resourceList = DescribeResources(entry.Definition.rewards);
            string cardList = string.Join(" and ", entry.ResolvedCardRewards
                .Where(r => r.card != null).Select(r => r.card.displayName));

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

        private void Update()
        {
            if (okButton == null || !okButton.gameObject.activeInHierarchy || !okButton.interactable)
                return;
            if (UIFocusUtility.WasSpacePressed())
                Hide();
        }
    }
}
