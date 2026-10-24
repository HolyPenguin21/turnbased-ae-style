using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // A presentation-only overlay. The encounter owns its start timestamp, so rebuilding
    // a card neither repeats the skill nor resets its fade, growth or upward motion.
    // Heal and Drill share the same animation with separate sprites.
    public sealed class BattleHealEffect : MonoBehaviour
    {
        public const float Duration = 2f;
        public const string HealSpritePath = "UI/Effects/HealCross";
        public const string DrillSpritePath = "UI/Effects/DrillFist";
        public const string BodyguardSpritePath = "UI/Effects/BodyguardShield";
        public const string ShockSpritePath = "UI/Effects/ShockImpulse";
        private const float VisibleAlpha = 0.8f;
        private const float InitialScale = 0.7f;
        private const float RiseFraction = 0.1f;
        private Image _image;
        private RectTransform _rect;
        private RectTransform _cardRect;
        private float _started;
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> _sprites =
            new System.Collections.Generic.Dictionary<string, Sprite>();

        public static float AlphaAt(float elapsed)
        {
            if (elapsed < 0f || elapsed >= Duration) return 0f;
            if (elapsed < 0.5f) return VisibleAlpha * Mathf.SmoothStep(0f, 1f, elapsed / 0.5f);
            if (elapsed <= Duration - 0.5f) return VisibleAlpha;
            return VisibleAlpha * Mathf.SmoothStep(1f, 0f, (elapsed - (Duration - 0.5f)) / 0.5f);
        }

        public static void Show(Transform card, float started, string spritePath = HealSpritePath)
        {
            if (!_sprites.TryGetValue(spritePath, out Sprite sprite) || sprite == null)
                _sprites[spritePath] = sprite = GameAssetCatalog.Load<Sprite>(spritePath);
            if (sprite == null) return;
            string effectName = spritePath == HealSpritePath ? "Heal" : spritePath == DrillSpritePath ? "Drill"
                : spritePath.Substring(spritePath.LastIndexOf('/') + 1);
            var previous = card.Find(effectName);
            if (previous != null) { previous.gameObject.SetActive(false); Destroy(previous.gameObject); }
            var go = new GameObject(effectName,
                typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(BattleHealEffect));
            var rect = (RectTransform)go.transform;
            rect.SetParent(card, false);
            rect.anchorMin = new Vector2(0.25f, 0.25f);
            rect.anchorMax = new Vector2(0.75f, 0.75f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var effect = go.GetComponent<BattleHealEffect>();
            effect._rect = rect;
            effect._cardRect = card as RectTransform;
            effect._image = go.GetComponent<Image>();
            effect._image.sprite = sprite;
            effect._image.preserveAspect = true;
            effect._image.raycastTarget = false;
            effect._started = started;
            effect.Update();
        }

        private void Update()
        {
            if (_image == null) return;
            float elapsed = Time.unscaledTime - _started;
            _image.color = new Color(1f, 1f, 1f, AlphaAt(elapsed));
            float progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / Duration));
            _rect.localScale = Vector3.one * Mathf.Lerp(InitialScale, 1f, progress);
            // Derive from elapsed time and current card height, so rebuilding/resizing
            // the grid restores the same motion instead of starting it over.
            float cardHeight = _cardRect != null ? _cardRect.rect.height : 0f;
            _rect.anchoredPosition = Vector2.up * (cardHeight * RiseFraction * progress);
            if (elapsed >= Duration) Destroy(gameObject);
        }
    }
}
