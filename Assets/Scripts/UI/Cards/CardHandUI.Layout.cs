using UnityEngine;

namespace Game.UI
{
    public partial class CardHandUI
    {
        // Actual HandContainer size in the authored 1280x720 scene, before Canvas scaling.
        [SerializeField] private Vector2 referenceHandSize = new Vector2(842.4f, 176.35f);
        [SerializeField] private RectTransform handCounterPanel;
        private Vector2 lastLayoutSize = new Vector2(-1f, -1f);
        private RectTransform[] slotRects;
        private RectTransform separatorLayer;
        private float[] separatorDesignY;
        private float PreferredSlotStep => cardSize.x * (1f - overlapFraction) + cardSpacing;
        private CardRowGeometry HandGeometry
        {
            get
            {
                Vector2 size = handContainer != null ? handContainer.rect.size : referenceHandSize;
                // Hidden/inactive parents can have no resolved rect yet; retry after layout.
                if (size.x <= 0f || size.y <= 0f) size = referenceHandSize;
                return CardRowGeometry.Fit(MaxVisible, PreferredSlotStep, referenceHandSize.x,
                    referenceHandSize.y, size.x, size.y);
            }
        }
        internal float HandLayoutScale => HandGeometry.Scale;

        private float SlotAnchorX(float slot) => .5f +
            (slot - (MaxVisible - 1) * .5f) * PreferredSlotStep / Mathf.Max(.01f, referenceHandSize.x);

        private void LateUpdate() => RefreshHandLayout();

        private void SyncSeparatorLayer()
        {
            if (separatorLayer == null || handContainer == null) return;
            separatorLayer.anchorMin = handContainer.anchorMin;
            separatorLayer.anchorMax = handContainer.anchorMax;
            separatorLayer.pivot = handContainer.pivot;
            separatorLayer.sizeDelta = handContainer.sizeDelta;
            separatorLayer.anchoredPosition = handContainer.anchoredPosition;
            separatorLayer.localScale = handContainer.localScale;
        }

        private void RefreshHandLayout()
        {
            if (handContainer == null || handContainer.rect.size == lastLayoutSize) return;
            if (handContainer.rect.width <= 0f || handContainer.rect.height <= 0f) return;
            lastLayoutSize = handContainer.rect.size;
            SyncSeparatorLayer();
            // Settle a flip at its chosen face before changing its scale mid-wave.
            StopTacticFlip();
            float scale = HandLayoutScale;
            if (handCounterPanel != null) handCounterPanel.localScale = Vector3.one * scale;
            if (scrollLeftButton != null) scrollLeftButton.transform.localScale = Vector3.one * scale;
            if (scrollRightButton != null) scrollRightButton.transform.localScale = Vector3.one * scale;
            // A resize invalidates the pointer offset and cached map/reorder band. End the
            // gesture without playing the card; a later OnEndDrag must not use stale geometry.
            foreach (var card in _cards)
                if (card != null) { card.CancelDrag(); card.SetHandLayoutScale(scale); }
            foreach (var card in _tacticCards)
                if (card != null) { card.CancelDrag(); card.SetHandLayoutScale(scale); }
            _dragWithinBand = true;
            _dragHexKnown = false;
            _lastDragHex = null;
            _lastHoverRaycastScreenPos = new Vector2(-1f, -1f);
            for (int i = 0; i < _debugCards.Count; i++)
                if (_debugCards[i] != null)
                {
                    _debugCards[i].CancelDrag();
                    _debugCards[i].SetHandLayoutScale(scale);
                    _debugCards[i].SetHome(new Vector2(SlotX(i), 0f), animated: false);
                }
            if (slotRects != null)
                foreach (var slot in slotRects) if (slot != null) slot.localScale = Vector3.one * scale;
            if (cardSeparators != null)
                for (int i = 0; i < cardSeparators.Length; i++)
                {
                    var separator = cardSeparators[i];
                    if (separator == null) continue;
                    separator.localScale = Vector3.one * scale;
                    if (separatorDesignY != null && i < separatorDesignY.Length)
                        separator.anchoredPosition = new Vector2(0f, HandGeometry.DecorationOffsetY(separatorDesignY[i]));
                }
            Relayout(animated: false);
        }
    }
}
