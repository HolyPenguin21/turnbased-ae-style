using System;
using Game.Cards;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One pooled cell of the collection grid. The cell prefab owns the layout; the card preview is
    // instantiated once per cell and then only rebound when the filter changes.
    public sealed class CollectionCardCellView : MonoBehaviour
    {
        [SerializeField] private RectTransform cardHost;
        [SerializeField] private TMP_Text infoLabel;
        [SerializeField] private Button addButton;
        [SerializeField] private TMP_Text addLabel;
        private ArmyUnitCardUI preview;
        private CanvasGroup previewGroup;
        public CardDefinition Card { get; private set; }
        public RectTransform CardHost => cardHost;

        public void Init(Action<CardDefinition> onAdd)
        {
            CollectionUIElements.ApplyCellSkin((RectTransform)transform);
            addButton.onClick.AddListener(() => { if (Card != null) onAdd(Card); });
        }

        public void Bind(CardDefinition card, GameConfig config, Action<CardDefinition> onSelect, Func<bool> inputAllowed)
        {
            if (Card == card && preview != null) return;
            Card = card; gameObject.name = card.authoredKey;
            if (preview == null)
            {
                if (config == null || config.armyUnitCardPrefab == null) return;
                preview = Instantiate(config.armyUnitCardPrefab, cardHost);
                // Collection thumbnails stay inside their 106x155 host. Full rules remain in
                // the details column; long captions must not paint over counters/buttons.
                preview.gameObject.AddComponent<RectMask2D>();
                foreach (var label in preview.GetComponentsInChildren<TMP_Text>(true))
                {
                    label.overflowMode = TextOverflowModes.Ellipsis;
                    label.enableAutoSizing = true;
                    label.fontSizeMax = label.fontSize;
                    label.fontSizeMin = Mathf.Min(label.fontSize, 5f);
                }
                previewGroup = preview.GetComponent<CanvasGroup>();
                if (previewGroup == null) previewGroup = preview.gameObject.AddComponent<CanvasGroup>();
            }
            preview.SetupPreview(card, config, onSelect, inputAllowed);
            CollectionUIElements.FitCardPreview((RectTransform)preview.transform, cardHost);
        }

        public void SetCounts(int owned, int inDeck)
        {
            if (Card == null) return;
            infoLabel.text = $"{owned}/{DeckRules.CopyLimit(Card)}   {Card.deckPointCost} pt" + (owned >= DeckRules.CopyLimit(Card) ? " MAX" : "");
            addLabel.text = "+  In deck: " + inDeck;
            if (previewGroup != null) previewGroup.alpha = owned == 0 ? .4f : 1f;
        }
    }
}


