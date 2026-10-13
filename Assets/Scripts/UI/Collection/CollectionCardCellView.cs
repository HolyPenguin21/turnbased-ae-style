using System;
using Game.Cards;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One pooled cell of the collection grid. The CollectionCard prefab owns the whole layout, including the
    // nested Card_Army preview (mask, fonts, size are tuned on the prefab); here the preview is only rebound.
    public sealed class CollectionCardCellView : MonoBehaviour
    {
        [SerializeField] private RectTransform cardHost;
        [SerializeField] private TMP_Text infoLabel;
        [SerializeField] private Button addButton;
        [SerializeField] private TMP_Text addLabel;
        [SerializeField] private ArmyUnitCardUI preview;
        [SerializeField] private CanvasGroup previewGroup;
        public CardDefinition Card { get; private set; }
        public RectTransform CardHost => cardHost;

        public void Init(Action<CardDefinition> onAdd)
        {
            CollectionUIElements.ApplyControlsSkin(addButton.transform);
            infoLabel.color = CollectionUIElements.CollectionText;
            addButton.onClick.AddListener(() => { if (Card != null) onAdd(Card); });
        }

        public void Bind(CardDefinition card, GameConfig config, Action<CardDefinition> onSelect, Func<bool> inputAllowed)
        {
            if (Card == card) return;
            Card = card; gameObject.name = card.authoredKey;
            preview.SetupPreview(card, config, onSelect, inputAllowed);
        }

        public void SetCounts(int owned, int inDeck)
        {
            if (Card == null) return;
            infoLabel.text = $"{owned}/{DeckRules.CopyLimit(Card)}   {Card.deckPointCost} pt" + (owned >= DeckRules.CopyLimit(Card) ? " MAX" : "");
            addLabel.text = "+  In deck: " + inDeck;
            previewGroup.alpha = owned == 0 ? .4f : 1f;
        }
    }
}


