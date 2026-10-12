using System;
using Game.Cards;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One pooled line of the deck list: either a category header or a card row.
    public sealed class CollectionDeckRowView : MonoBehaviour
    {
        private const float HeaderHeight = 22f, RowHeight = 38f;
        [SerializeField] private LayoutElement layout;
        [SerializeField] private GameObject headerRoot, rowRoot;
        [SerializeField] private TMP_Text headerLabel, nameLabel, costLabel, countLabel;
        [SerializeField] private Image art;
        [SerializeField] private Button nameButton, minusButton, plusButton;
        private CardDefinition card;
        private string key;

        public void Init(Action<CardDefinition> onSelect, Action<string, int> onChange)
        {
            CollectionUIElements.ApplyDeckRowSkin((RectTransform)transform);
            nameButton.onClick.AddListener(() => onSelect(card));
            minusButton.onClick.AddListener(() => onChange(key, -1));
            plusButton.onClick.AddListener(() => onChange(key, 1));
        }

        public void SetHeader(string text)
        {
            headerRoot.SetActive(true); rowRoot.SetActive(false);
            headerLabel.text = text;
            // TMP's line metrics exceed the font size. A 14px row can ellipsize a 13px
            // heading into an empty line; keep authored fonts readable when rows are reused.
            layout.preferredHeight = Mathf.Max(HeaderHeight, headerLabel.preferredHeight + 4f);
        }

        public void SetEntry(CardDefinition card, string key, int count, bool canAdd)
        {
            this.card = card; this.key = key;
            headerRoot.SetActive(false); rowRoot.SetActive(true);
            layout.preferredHeight = RowHeight;
            var sprite = card == null ? null : card.detailArt != null ? card.detailArt : card.art;
            art.sprite = sprite; art.enabled = sprite != null;
            nameLabel.text = card != null ? card.displayName : key;
            costLabel.text = $"{(long)(card != null ? card.deckPointCost : 0) * count} pt";
            countLabel.text = count.ToString();
            plusButton.gameObject.SetActive(canAdd);
        }
    }
}

