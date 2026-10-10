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
        private const float HeaderHeight = 21f, RowHeight = 52f;
        [SerializeField] private LayoutElement layout;
        [SerializeField] private GameObject headerRoot, rowRoot;
        [SerializeField] private TMP_Text headerLabel, nameLabel, costLabel, countLabel;
        [SerializeField] private Image art;
        [SerializeField] private Button nameButton, minusButton, plusButton, removeButton;
        private CardDefinition card;
        private string key;

        public void Init(Action<CardDefinition> onSelect, Action<string, int> onChange, Action<string> onRemove)
        {
            nameButton.onClick.AddListener(() => onSelect(card));
            minusButton.onClick.AddListener(() => onChange(key, -1));
            plusButton.onClick.AddListener(() => onChange(key, 1));
            removeButton.onClick.AddListener(() => onRemove(key));
        }

        public void SetHeader(string text)
        {
            headerRoot.SetActive(true); rowRoot.SetActive(false);
            layout.preferredHeight = HeaderHeight; headerLabel.text = text;
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
