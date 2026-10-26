using UnityEngine;

namespace Game.UI
{
    public partial class CardHandUI
    {
        // Actual HandContainer size in the authored 1280x720 scene, before Canvas scaling.
        [SerializeField] private Vector2 referenceHandSize = new Vector2(842.4f, 176.35f);
        [SerializeField] private RectTransform handCounterPanel;
        private Vector2 lastLayoutSize = new Vector2(-1f, -1f);
        private Vector3 lastLayoutWorldScale;
        private RectTransform[] slotRects;
        private Vector2[] scrollButtonDesignPosition;
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

        private Vector2 HandSlotPosition(int index) => new Vector2(SlotX(index), HandGeometry.CenterOffsetY);

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

        private void FitScrollButtons(float scale)
        {
            var buttons = new[] { scrollLeftButton, scrollRightButton };
            if (scrollButtonDesignPosition == null)
            {
                scrollButtonDesignPosition = new Vector2[buttons.Length];
                // These buttons are siblings of the hand. Capture their authored offsets
                // once, before replacing the independently stretching canvas anchors.
                Vector2 span = handContainer.anchorMax - handContainer.anchorMin;
                Vector2 panelSize = new Vector2(
                    span.x > 0f ? referenceHandSize.x / span.x : referenceHandSize.x,
                    span.y > 0f ? referenceHandSize.y / span.y : referenceHandSize.y);
                Vector2 handCentre = Vector2.Scale((handContainer.anchorMin + handContainer.anchorMax) * .5f, panelSize)
                    + handContainer.anchoredPosition
                    + Vector2.Scale(Vector2.one * .5f - handContainer.pivot, handContainer.sizeDelta);
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] == null) continue;
                    var rect = (RectTransform)buttons[i].transform;
                    Vector2 anchor = new Vector2(
                        Mathf.Lerp(rect.anchorMin.x, rect.anchorMax.x, rect.pivot.x),
                        Mathf.Lerp(rect.anchorMin.y, rect.anchorMax.y, rect.pivot.y));
                    Vector2 position = rect.anchoredPosition + Vector2.Scale(anchor, panelSize);
                    scrollButtonDesignPosition[i] = new Vector2(position.x - handCentre.x, position.y);
                    rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0f);
                }
            }
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] == null) continue;
                var rect = (RectTransform)buttons[i].transform;
                var parent = rect.parent as RectTransform;
                if (parent == null) continue;
                float centreX = parent.InverseTransformPoint(handContainer.TransformPoint(handContainer.rect.center)).x
                    - parent.rect.center.x;
                rect.localScale = Vector3.one * scale;
                rect.anchoredPosition = new Vector2(centreX + scrollButtonDesignPosition[i].x * scale,
                    scrollButtonDesignPosition[i].y * scale);
            }
        }

        private void RefreshHandLayout()
        {
            if (handContainer == null) return;
            if (handContainer.rect.size == lastLayoutSize && handContainer.lossyScale == lastLayoutWorldScale) return;
            if (handContainer.rect.width <= 0f || handContainer.rect.height <= 0f) return;
            lastLayoutSize = handContainer.rect.size;
            lastLayoutWorldScale = handContainer.lossyScale;
            SyncSeparatorLayer();
            // Settle a flip at its chosen face before changing its scale mid-wave.
            StopTacticFlip();
            float scale = HandLayoutScale;
            if (handCounterPanel != null) handCounterPanel.localScale = Vector3.one * scale;
            FitScrollButtons(scale);
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
                    _debugCards[i].SetHome(HandSlotPosition(i), animated: false);
                }
            if (slotRects != null)
                for (int i = 0; i < slotRects.Length; i++)
                {
                    var slot = slotRects[i];
                    if (slot == null) continue;
                    slot.localScale = Vector3.one * scale;
                    slot.anchoredPosition = HandSlotPosition(i);
                }
            if (cardSeparators != null)
                for (int i = 0; i < cardSeparators.Length; i++)
                {
                    var separator = cardSeparators[i];
                    if (separator == null) continue;
                    separator.localScale = Vector3.one * scale;
                    if (separatorDesignY != null && i < separatorDesignY.Length)
                        separator.anchoredPosition = new Vector2(HandGeometry.SlotX(i + .5f), HandGeometry.DecorationOffsetY(separatorDesignY[i]));
                }
            Relayout(animated: false);
        }
    }
}
