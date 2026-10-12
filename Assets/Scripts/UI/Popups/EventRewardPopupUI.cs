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
    // CLAUDE.md, UIFocusUtility.WasSpacePressed()) exactly like PopupPanelUI — a plain
    // acknowledgement, not a decision, unlike EventChoicePopupUI.
    public class EventRewardPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Image unitImage;
        [SerializeField] private TMP_Text summaryText;
        [SerializeField] private Image rewardCardImage;
        [SerializeField] private Button okButton;
        private RectTransform rewardWindow, recipientFrame, cardFrame, summaryPanel;

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
            ApplyRewardSkin();

            if (okButton != null)
            {
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(okButton);
                okButton.onClick.AddListener(Hide);
            }
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
        }

        private void ApplyRewardSkin()
        {
            if (panelRoot == null) return;
            if (rewardWindow == null)
            {
                var backdrop = (RectTransform)panelRoot.transform; CollectionUIElements.Stretch(backdrop);
                var dimmer = panelRoot.GetComponent<Image>();
                if (dimmer != null) { dimmer.sprite = null; dimmer.type = Image.Type.Simple; dimmer.color = new Color(0, 0, 0, .8f); }
                rewardWindow = CollectionUIElements.RewardWindow(backdrop, "EventRewardWindow");
                // Keep all authored object identities and button bindings while moving the
                // controls into the centered window under the original visibility owner.
                for (int i = backdrop.childCount - 2; i >= 0; i--)
                    backdrop.GetChild(i).SetParent(rewardWindow, false);
                var title = rewardWindow.Find("TitleText")?.GetComponent<TMP_Text>();
                CollectionUIElements.RewardTitle(rewardWindow, "EVENT REWARD", false, title);
                recipientFrame = CollectionUIElements.Panel(rewardWindow, "RecipientFrame");
                cardFrame = CollectionUIElements.Panel(rewardWindow, "RewardCardFrame");
                CollectionUIElements.Place(recipientFrame, 60, 148, 200, 340);
                CollectionUIElements.Place(cardFrame, 800, 148, 200, 340);
                CollectionUIElements.ApplySurface(recipientFrame.GetComponent<Image>(), "Panel_Inset");
                CollectionUIElements.ApplySurface(cardFrame.GetComponent<Image>(), "Panel_Inset");
                recipientFrame.GetComponent<Image>().raycastTarget = cardFrame.GetComponent<Image>().raycastTarget = false;
                // Art stays above the frame backing; it uses its full authored aspect ratio.
                if (unitImage != null)
                {
                    unitImage.transform.SetParent(recipientFrame, false);
                    CollectionUIElements.Place(unitImage.rectTransform, 12, 22, 176, 257);
                    unitImage.preserveAspect = true; unitImage.raycastTarget = false;
                }
                if (rewardCardImage != null)
                {
                    rewardCardImage.transform.SetParent(cardFrame, false);
                    CollectionUIElements.Place(rewardCardImage.rectTransform, 12, 22, 176, 257);
                    rewardCardImage.preserveAspect = true; rewardCardImage.raycastTarget = false;
                }
                var recipientCaption = CollectionUIElements.Label(recipientFrame, "RECIPIENT", 10, 296, 180, 28, 18);
                var cardCaption = CollectionUIElements.Label(cardFrame, "REWARD CARD", 10, 296, 180, 28, 18);
                recipientCaption.alignment = cardCaption.alignment = TextAlignmentOptions.Center;
                summaryPanel = CollectionUIElements.Panel(rewardWindow, "RewardSummary");
                var paper = summaryPanel.GetComponent<Image>(); paper.sprite = Resources.Load<Sprite>("UI/Backgrounds/Campaign/Parchment");
                paper.type = Image.Type.Tiled; paper.color = Color.white; paper.raycastTarget = false;
                var frame = CollectionUIElements.Panel(summaryPanel, "SummaryFrame"); CollectionUIElements.Stretch(frame);
                CollectionUIElements.ApplyCampaignSurface(frame, "Panel_Inset"); frame.GetComponent<Image>().raycastTarget = false;
                if (summaryText != null)
                {
                    summaryText.transform.SetParent(summaryPanel, false);
                    summaryText.color = CollectionUIElements.CollectionInk; summaryText.alignment = TextAlignmentOptions.Center;
                    summaryText.enableAutoSizing = true; summaryText.fontSizeMin = 14; summaryText.fontSizeMax = 22;
                    summaryText.textWrappingMode = TextWrappingModes.Normal; summaryText.raycastTarget = false;
                }
                if (okButton != null)
                {
                    CollectionUIElements.Place((RectTransform)okButton.transform, 400, 532, 260, 44);
                    CollectionUIElements.ApplyControlsSkin(okButton.transform);
                    var label = okButton.GetComponentInChildren<TMP_Text>(true);
                    if (label != null)
                    {
                        CollectionUIElements.Place((RectTransform)label.transform, 10, 4, 240, 36);
                        label.text = "CONTINUE"; label.fontSize = 22; label.fontStyle = FontStyles.Bold;
                    }
                }
            }
            bool portrait = unitImage != null && unitImage.gameObject.activeSelf;
            bool card = rewardCardImage != null && rewardCardImage.gameObject.activeSelf;
            recipientFrame.gameObject.SetActive(portrait); cardFrame.gameObject.SetActive(card);
            float x = portrait ? (card ? 284 : 300) : (card ? 60 : 130);
            float width = portrait && card ? 492 : portrait || card ? 700 : 800;
            CollectionUIElements.Place(summaryPanel, x, 148, width, 328);
            if (summaryText != null) CollectionUIElements.Place(summaryText.rectTransform, 30, 30, width - 60, 268);
        }

        // "<Army> receives <resources> as a reward." / "<Army> receives <card> as a reward." /
        // "<Army> receives <resources> and <card> as a reward." — the three shapes the user asked
        // for, picked based on what this event's rewards actually contain.
        private static string BuildSummary(HexEventRegistry.Entry entry, ArmyData recipient)
        {
            string armyName = recipient?.Name ?? "The army";
            string resourceList = DescribeResources(entry.SelectedRewards);
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
            {
                Game.Audio.GameAudioManager.Instance?.PlayClick();
                Hide();
            }
        }
    }
}
