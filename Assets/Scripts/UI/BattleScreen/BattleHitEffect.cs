using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class BattleHitEffect : MonoBehaviour
    {
        public const float Duration = 1.1f;
        private TMP_Text _text;
        private RectTransform _card, _rect, _overlay;
        private float _started;
        public static string FormatDamage(int damage, bool wasHit) => damage > 0 ? "-" + damage : wasHit ? "BLOCK" : "MISS";
        public static float AlphaAt(float elapsed) => elapsed < 0f || elapsed >= Duration ? 0f
            : elapsed < .15f ? elapsed / .15f : elapsed < .65f ? 1f : (Duration - elapsed) / .45f;
        public static void Show(RectTransform card, int damage, bool wasHit, RectTransform overlay = null)
        {
            if (card == null) return;
            var go = new GameObject("IncomingDamage", typeof(RectTransform), typeof(LayoutElement),
                typeof(TextMeshProUGUI), typeof(BattleHitEffect));
            var rect = (RectTransform)go.transform;
            rect.SetParent(overlay != null ? overlay : card, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.sizeDelta = new Vector2(card.rect.width, 48f);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var text = go.GetComponent<TextMeshProUGUI>();
            var template = card.GetComponentInChildren<TMP_Text>(true);
            if (template != null && template != text) text.font = template.font;
            text.text = FormatDamage(damage, wasHit);
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.color = damage > 0 ? new Color(1f,.32f,.2f) : new Color(.85f,.85f,.8f);
            text.outlineColor = Color.black;
            text.outlineWidth = .2f;
            text.raycastTarget = false;
            var effect = go.GetComponent<BattleHitEffect>();
            effect._text = text; effect._card = card; effect._rect = rect;
            effect._overlay = overlay; effect._started = Time.unscaledTime;
            effect.Update();
        }
        private void Update()
        {
            if (_card == null) { Destroy(gameObject); return; }
            float elapsed = Time.unscaledTime - _started;
            _text.alpha = AlphaAt(elapsed);
            Vector2 point = _card.rect.center + Vector2.up * (_card.rect.height
                * (.18f + .1f * Mathf.Clamp01(elapsed / Duration)));
            var parent = _overlay != null ? _overlay : _card;
            _rect.localPosition = parent.InverseTransformPoint(_card.TransformPoint(point));
            Vector2 a = parent.InverseTransformPoint(_card.TransformPoint(_card.rect.center));
            Vector2 b = parent.InverseTransformPoint(_card.TransformPoint(_card.rect.center + Vector2.up * _card.rect.height));
            _text.fontSize = Mathf.Clamp((b-a).magnitude * .2f, 18f, 42f);
            if (elapsed >= Duration) Destroy(gameObject);
        }
    }
}
