using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // A presentation-only overlay. The encounter owns its start timestamp, so rebuilding
    // a card neither repeats Heal nor resets the three-second animation.
    public sealed class BattleHealEffect : MonoBehaviour
    {
        public const float Duration = 3f;
        private const float VisibleAlpha = 0.8f;
        private Image _image;
        private float _started;
        private static Sprite _sprite;

        public static float AlphaAt(float elapsed)
        {
            if (elapsed < 0f || elapsed >= Duration) return 0f;
            if (elapsed < 0.5f) return VisibleAlpha * Mathf.SmoothStep(0f, 1f, elapsed / 0.5f);
            if (elapsed <= 2.5f) return VisibleAlpha;
            return VisibleAlpha * Mathf.SmoothStep(1f, 0f, (elapsed - 2.5f) / 0.5f);
        }

        public static void Show(Transform card, float started)
        {
            if (_sprite == null) _sprite = GameAssetCatalog.Load<Sprite>("UI/Effects/HealCross");
            if (_sprite == null) return;
            var go = new GameObject("Heal", typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(BattleHealEffect));
            var rect = (RectTransform)go.transform;
            rect.SetParent(card, false);
            rect.anchorMin = new Vector2(0.25f, 0.25f);
            rect.anchorMax = new Vector2(0.75f, 0.75f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var effect = go.GetComponent<BattleHealEffect>();
            effect._image = go.GetComponent<Image>();
            effect._image.sprite = _sprite;
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
            if (elapsed >= Duration) Destroy(gameObject);
        }
    }
}
