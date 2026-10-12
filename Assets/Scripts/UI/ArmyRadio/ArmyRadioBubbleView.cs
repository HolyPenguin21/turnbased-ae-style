using System.Collections.Generic;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Reused presentation slot; the controller alone chooses speakers and schedules replies.
    public sealed class ArmyRadioBubbleView : MonoBehaviour
    {
        [SerializeField] private RectTransform _group, _panel, _shadow;
        [SerializeField] private CanvasGroup _fade;
        [SerializeField] private ArmyRadioBubbleGraphic _bubble, _bubbleShadow;
        [SerializeField] private Image _art;
        [SerializeField] private TMP_Text _message;
        [Min(40f)] [SerializeField] private float artHeight = 78f;
        [Min(100f)] [SerializeField] private float messageWidth = 180f;
        [Min(28f)] [SerializeField] private float minimumPanelHeight = 56f;
        [Min(0f)] [SerializeField] private float cardGap = 14f;
        [Min(10f)] [SerializeField] private float markerGap = 12f;
        [SerializeField] private Vector2 shadowOffset = new Vector2(0f, -2f);
        private Vector2 _measuredPadding;
        private float _measuredFontSize, _measuredMinimumHeight, _measuredCornerRadius;
        private TMP_FontAsset _measuredFont;
        private float _measuredWidth = -1f, _messageHeight;
        private Vector2? _offset;
        public ArmyData Speaker { get; private set; }
        public UnitData Unit { get; private set; }
        public ArmyRadioTiming Timing { get; private set; }
        public Rect Bounds { get; private set; }
        private Vector2 _pointerStart, _pointerTip;
        private bool _hasPointer;
        public bool Active => Speaker != null;

        public bool IsConfigured => _group != null && _group == transform && _panel != null && _shadow != null
            && _fade != null && _bubble != null && _bubbleShadow != null && _art != null && _message != null
            && _fade.transform == _group && _bubble.transform == _panel && _bubbleShadow.transform == _shadow
            && _panel.parent == _group && _shadow.parent == _group && _art.transform.parent == _group
            && _message.transform.parent == _panel && _message.rectTransform.anchorMin == Vector2.zero
            && _message.rectTransform.anchorMax == Vector2.one;

        public void Show(ArmyData army, string text, float now, ArmyRadioChatterSettings settings)
        {
            Speaker = army; Unit = ArmyRadioChatter.FirstUnit(army);
            Timing = new ArmyRadioTiming(now, settings.displaySeconds, settings.fadeInSeconds, settings.fadeOutSeconds);
            _art.sprite = ArmyRadioChatter.DetailArt(Unit);
            _message.text = text;
            _measuredWidth = -1f; _offset = null; _fade.alpha = 0f; _group.gameObject.SetActive(true);
        }

        public void Tick(float now)
        {
            if (!Active) return;
            if (Timing.Finished(now)) Hide();
            else _fade.alpha = Timing.Alpha(now);
        }
        public void Dismiss(float now) { if (Active) Timing.Dismiss(now); }
        public void Hide()
        {
            Speaker = null; Unit = null; Timing = null; _offset = null;
            if (_fade != null) _fade.alpha = 0f;
            if (_group != null) _group.gameObject.SetActive(false);
        }

        public bool Follow(Rect safe, Vector2 anchor, float radius, IList<Rect> occupied, float scale = 1f)
        {
            scale = Mathf.Max(.01f, scale);
            Vector2 artSize = ArmyRadioLayout.ArtSize(_art.sprite, Mathf.Max(40f, artHeight));
            float width = Mathf.Min(Mathf.Max(100f, messageWidth), safe.width / scale - artSize.x - Mathf.Max(0f, cardGap));
            if (width < 100f || safe.height < artSize.y * scale) return false;
            RectTransform textRect = _message.rectTransform;
            Vector2 padding = textRect.offsetMin - textRect.offsetMax;
            if (Mathf.Abs(width - _measuredWidth) > .01f || padding != _measuredPadding
                || _message.font != _measuredFont || _message.fontSize != _measuredFontSize
                || minimumPanelHeight != _measuredMinimumHeight || _bubble.CornerRadius != _measuredCornerRadius)
            {
                _measuredWidth = width; _measuredPadding = padding;
                _measuredFont = _message.font; _measuredFontSize = _message.fontSize;
                _measuredMinimumHeight = minimumPanelHeight; _measuredCornerRadius = _bubble.CornerRadius;
                _messageHeight = Mathf.Max(Mathf.Max(minimumPanelHeight, _bubble.CornerRadius * 2f + 8f), _message.GetPreferredValues(_message.text, Mathf.Max(1f, width - padding.x), 1000f).y + padding.y);
            }
            Vector2 size = new Vector2(width + artSize.x + Mathf.Max(0f, cardGap), Mathf.Max(artSize.y, _messageHeight));
            Vector2 scaledSize = size * scale;
            float scaledArtWidth = (artSize.x + Mathf.Max(0f, cardGap)) * scale;
            Vector2? previousOffset = _offset.HasValue ? _offset.Value * scale : (Vector2?)null;
            if (!ArmyRadioLayout.TryPlace(safe, anchor, scaledSize, radius + Mathf.Max(10f, markerGap) * scale, occupied, previousOffset, out var position, out bool left, scaledArtWidth, _messageHeight * scale, radius + 2f * scale, _bubble.CornerRadius * scale, 4f * scale, artSize * scale))
                return false;
            _offset = (position - anchor) / scale; Bounds = ArmyRadioLayout.Bounds(position, scaledSize);
            _group.localScale = Vector3.one * scale;
            _group.sizeDelta = size; _group.anchoredPosition = position;
            _art.rectTransform.sizeDelta = artSize;
            _art.rectTransform.anchoredPosition = new Vector2((left ? -1f : 1f) * (size.x - artSize.x) * .5f, 0f);
            Vector2 panelPosition = new Vector2((left ? 1f : -1f) * (artSize.x + Mathf.Max(0f, cardGap)) * .5f, 0f);
            if (ArmyRadioLayout.Pointer(ArmyRadioLayout.PanelBounds(position, scaledSize, scaledArtWidth, _messageHeight * scale, left),
                anchor, _bubble.CornerRadius * scale, radius + 2f * scale, out var a, out var b, out var tip, 4f * scale))
            {
                _pointerStart = (a + b) * .5f; _pointerTip = tip;
                _hasPointer = true;
            }
            else _hasPointer = false;
            // These are absolute measured sizes. With stretch anchors, sizeDelta is
            // relative to the parent and would enlarge the drawn panel beyond Bounds.
            _panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _messageHeight);
            _shadow.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            _shadow.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _messageHeight);
            _panel.anchoredPosition = panelPosition; _shadow.anchoredPosition = panelPosition + shadowOffset;
            _bubble.AimAt((anchor - position) / scale - panelPosition, radius / scale + 2f);
            _bubbleShadow.AimAt((anchor - position) / scale - _shadow.anchoredPosition, radius / scale + 2f);
            return true;
        }

        public void AddObstacles(IList<Rect> occupied)
        {
            occupied.Add(Bounds);
            if (_hasPointer) ArmyRadioLayout.AddPointerObstacles(_pointerStart, _pointerTip, occupied, _bubble.SignalExtent * _group.localScale.x);
        }

    }
}
