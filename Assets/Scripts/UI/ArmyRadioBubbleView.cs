using System.Collections.Generic;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Reused presentation slot; the controller alone chooses speakers and schedules replies.
    internal sealed class ArmyRadioBubbleView
    {
        private readonly RectTransform _group, _panel, _shadow;
        private readonly CanvasGroup _fade;
        private readonly ArmyRadioBubbleGraphic _bubble, _bubbleShadow;
        private readonly Image _art;
        private readonly TMP_Text _message;
        private float _measuredWidth = -1f, _messageHeight;
        private Vector2? _offset;
        public ArmyData Speaker { get; private set; }
        public UnitData Unit { get; private set; }
        public ArmyRadioTiming Timing { get; private set; }
        public Rect Bounds { get; private set; }
        private Vector2 _pointerStart, _pointerTip;
        private bool _hasPointer;
        public bool Active => Speaker != null;

        public ArmyRadioBubbleView(RectTransform canvas)
        {
            RectTransform _canvas = canvas;
            _group = CreateRect(_canvas, "RadioMessage");
            _fade = _group.gameObject.AddComponent<CanvasGroup>(); _fade.blocksRaycasts = false; _fade.interactable = false;
            _shadow = CreateRect(_group, "Shadow"); _bubbleShadow = _shadow.gameObject.AddComponent<ArmyRadioBubbleGraphic>();
            _bubbleShadow.color = new Color(.13f, .10f, .07f, .16f); _bubbleShadow.BorderColor = Color.clear;
            _bubbleShadow.raycastTarget = false;
            _panel = CreateRect(_group, "Message"); _bubble = _panel.gameObject.AddComponent<ArmyRadioBubbleGraphic>();
            _bubble.color = new Color(.80f, .72f, .60f, .86f); _bubble.raycastTarget = false;
            RectTransform label = CreateRect(_panel, "Speech");
            label.anchorMin = Vector2.zero; label.anchorMax = Vector2.one; label.offsetMin = new Vector2(11f, 10f); label.offsetMax = new Vector2(-11f, -10f);
            _message = label.gameObject.AddComponent<TextMeshProUGUI>();
            _message.font = Resources.Load<TMP_FontAsset>("Fonts/GameMenuFont") ?? TMP_Settings.defaultFontAsset;
            _message.color = CollectionUIElements.CollectionInk; _message.alignment = TextAlignmentOptions.MidlineLeft;
            _message.richText = false; _message.textWrappingMode = TextWrappingModes.Normal;
            _message.overflowMode = TextOverflowModes.Overflow; _message.raycastTarget = false;
            _art = CreateRect(_group, "FirstUnitDetailArt").gameObject.AddComponent<Image>();
            _art.preserveAspect = true; _art.raycastTarget = false;
            Reset();
        }


        public void Show(ArmyData army, string text, float now, ArmyRadioChatterSettings settings)
        {
            Speaker = army; Unit = ArmyRadioChatter.FirstUnit(army);
            Timing = new ArmyRadioTiming(now, settings.displaySeconds, settings.fadeInSeconds, settings.fadeOutSeconds);
            _art.sprite = ArmyRadioChatter.DetailArt(Unit); _art.color = Color.white;
            _message.text = text; _message.fontSize = Mathf.Clamp(settings.fontSize, 12f, 20f);
            _measuredWidth = -1f; _offset = null; _fade.alpha = 0f; _group.gameObject.SetActive(true);
        }

        public void Tick(float now)
        {
            if (!Active) return;
            if (Timing.Finished(now)) Reset();
            else _fade.alpha = Timing.Alpha(now);
        }
        public void Dismiss(float now) { if (Active) Timing.Dismiss(now); }
        public void Reset()
        {
            Speaker = null; Unit = null; Timing = null; _offset = null;
            _fade.alpha = 0f; _group.gameObject.SetActive(false);
        }

        public bool Follow(Rect safe, Vector2 anchor, float radius, IList<Rect> occupied, ArmyRadioChatterSettings settings)
        {
            Vector2 artSize = ArmyRadioLayout.ArtSize(_art.sprite, Mathf.Clamp(settings.artHeight, 40f, 110f));
            float width = Mathf.Min(Mathf.Clamp(settings.messageWidth, 140f, 300f), safe.width - artSize.x - 8f);
            if (width < 100f || safe.height < artSize.y) return false;
            if (Mathf.Abs(width - _measuredWidth) > .01f)
            {
                _measuredWidth = width;
                _messageHeight = Mathf.Max(52f, _message.GetPreferredValues(_message.text, width - 22f, 1000f).y + 20f);
            }
            Vector2 size = new Vector2(width + artSize.x + 4f, Mathf.Max(artSize.y, _messageHeight));
            if (!ArmyRadioLayout.TryPlace(safe, anchor, size, radius + 12f, occupied, _offset, out var position, out bool left, artSize.x + 4f, _messageHeight, radius + 2f))
                return false;
            _offset = position - anchor; Bounds = ArmyRadioLayout.Bounds(position, size);
            _group.sizeDelta = size; _group.anchoredPosition = position;
            _art.rectTransform.sizeDelta = artSize;
            _art.rectTransform.anchoredPosition = new Vector2((left ? -1f : 1f) * (size.x - artSize.x) * .5f, 0f);
            Vector2 panelPosition = new Vector2((left ? 1f : -1f) * (artSize.x + 4f) * .5f, 0f);
            if (ArmyRadioLayout.Pointer(ArmyRadioLayout.PanelBounds(position, size, artSize.x + 4f, _messageHeight, left),
                anchor, 9f, radius + 2f, out var a, out var b, out var tip))
            {
                _pointerStart = (a + b) * .5f; _pointerTip = tip;
                if (!ArmyRadioLayout.PointerPathClear(_pointerStart, tip, occupied)) return false;
                _hasPointer = true;
            }
            else _hasPointer = false;
            _panel.sizeDelta = _shadow.sizeDelta = new Vector2(width, _messageHeight);
            _panel.anchoredPosition = panelPosition; _shadow.anchoredPosition = panelPosition + new Vector2(0f, -2f);
            _bubble.AimAt(anchor - position - panelPosition, radius + 2f);
            _bubbleShadow.AimAt(anchor - position - _shadow.anchoredPosition, radius + 2f);
            return true;
        }

        public void AddObstacles(IList<Rect> occupied)
        {
            occupied.Add(Bounds);
            if (_hasPointer) ArmyRadioLayout.AddPointerObstacles(_pointerStart, _pointerTip, occupied);
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            return rect;
        }
    }
}
