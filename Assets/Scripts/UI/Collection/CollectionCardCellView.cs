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
        [SerializeField] private ArmyUnitCardUI preview;
        [SerializeField] private CanvasGroup previewGroup;
        [SerializeField] private GameObject selection;
        [SerializeField] private RectTransform visual;
        public CardDefinition Card { get; private set; }

        private void OnRectTransformDimensionsChange() => FitVisual();
        private void OnEnable() => FitVisual();

        private void FitVisual()
        {
            if (visual == null) return;
            var cell = (RectTransform)transform;
            if (visual.rect.width <= 0f || visual.rect.height <= 0f) return;
            float scale = Mathf.Max(0f, Mathf.Min(cell.rect.width / visual.rect.width,
                cell.rect.height / visual.rect.height));
            visual.localScale = Vector3.one * scale;
        }

        public void Init(Action<CardDefinition> onAdd)
        {
            addButton.onClick.AddListener(() => { if (Card != null) onAdd(Card); });
        }

        public void Bind(CardDefinition card, GameConfig config, Action<CardDefinition> onSelect, Func<bool> inputAllowed)
        {
            FitVisual();
            if (Card == card) return;
            Card = card; gameObject.name = card.authoredKey;
            preview.SetupPreview(card, config, onSelect, inputAllowed);
        }

        public void SetSelected(bool on) { if (selection != null) selection.SetActive(on); }

        public void SetCounts(int owned, int inDeck, bool canAdd)
        {
            if (Card == null) return;
            infoLabel.text = $"{Card.deckPointCost}pt. In deck: {inDeck}/{DeckRules.CopyLimit(Card)}";
            previewGroup.alpha = owned == 0 ? .4f : 1f;
            addButton.interactable = canAdd;
        }
    }
}


