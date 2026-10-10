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
        { addButton.onClick.AddListener(() => { if (Card != null) onAdd(Card); }); }

        public void Bind(CardDefinition card, GameConfig config, Action<CardDefinition> onSelect, Func<bool> inputAllowed)
        {
            if (Card == card && preview != null) return;
            Card = card; gameObject.name = card.authoredKey;
            if (preview == null)
            {
                if (config == null || config.armyUnitCardPrefab == null) return;
                preview = Instantiate(config.armyUnitCardPrefab, cardHost);
                previewGroup = preview.GetComponent<CanvasGroup>();
                if (previewGroup == null) previewGroup = preview.gameObject.AddComponent<CanvasGroup>();
            }
            preview.SetupPreview(card, config, onSelect, inputAllowed);
            var rt = (RectTransform)preview.transform;
            Vector2 area = cardHost.rect.size;
            float scale = Mathf.Min(area.x / Mathf.Max(1, rt.rect.width), area.y / Mathf.Max(1, rt.rect.height));
            rt.localScale = Vector3.one * scale;
            rt.anchorMin = rt.anchorMax = new Vector2(.5f, 1); rt.pivot = new Vector2(.5f, 1); rt.anchoredPosition = Vector2.zero;
        }

        public void SetCounts(int owned, int inDeck)
        {
            if (Card == null) return;
            infoLabel.text = $"{owned}/{Card.deckCopyLimit}   {Card.deckPointCost} pt" + (owned >= Card.deckCopyLimit ? " MAX" : "");
            addLabel.text = "+  In deck: " + inDeck;
            if (previewGroup != null) previewGroup.alpha = owned == 0 ? .4f : 1f;
        }
    }
}
