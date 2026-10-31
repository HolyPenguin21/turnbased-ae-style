using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Presentation-only skill flipbooks. The encounter timestamp survives card rebuilds;
    // rendering never delays the next attack or modifies any combat rules.
    public sealed class BattleHealEffect : MonoBehaviour
    {
        public const float Duration = 0.88f;
        private const int FrameCount = 8;
        public const string HealSpritePath = "UI/Effects/HealCross";
        public const string DrillSpritePath = "UI/Effects/DrillFist";
        public const string BerserkSpritePath = "UI/Effects/BerserkRage";
        public const string BodyguardSpritePath = "UI/Effects/BodyguardShield";
        public const string ShockSpritePath = "UI/Effects/ShockImpulse";

        private static readonly Dictionary<string, Texture2D> Atlases =
            new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Sprite> FallbackSprites =
            new Dictionary<string, Sprite>();

        private RawImage _atlasImage;
        private Graphic _graphic;
        private RectTransform _rect;
        private RectTransform _cardRect;
        private float _started;

        private static string AtlasPath(string spritePath)
        {
            switch (spritePath)
            {
                case HealSpritePath: return "UI/Effects/Flipbooks/HealA";
                case DrillSpritePath: return "UI/Effects/Flipbooks/DrillA";
                case BerserkSpritePath: return "UI/Effects/Flipbooks/BerserkA";
                case BodyguardSpritePath: return "UI/Effects/Flipbooks/BodyguardA";
                case ShockSpritePath: return "UI/Effects/Flipbooks/ShockA";
                default: return null;
            }
        }

        public static float AlphaAt(float elapsed)
        {
            if (elapsed < 0f || elapsed >= Duration) return 0f;
            if (elapsed < 0.12f) return Mathf.SmoothStep(0f, 0.95f, elapsed / 0.12f);
            if (elapsed < Duration - 0.22f) return 0.95f;
            return Mathf.SmoothStep(0.95f, 0f, (elapsed - (Duration - 0.22f)) / 0.22f);
        }

        public static void Show(Transform card, float started, string spritePath = HealSpritePath)
        {
            if (card == null) return;
            string atlasPath = AtlasPath(spritePath);
            Texture2D atlas = null;
            if (atlasPath != null)
            {
                if (!Atlases.TryGetValue(atlasPath, out atlas) || atlas == null)
                    Atlases[atlasPath] = atlas = GameAssetCatalog.Load<Texture2D>(atlasPath);
                // Guard against the wrong artwork being imported in the atlas slot.
                if (atlas != null && (atlas.width != FrameCount * 256 || atlas.height != 256))
                {
                    Debug.LogWarning("Skill VFX atlas has an unexpected grid: " + atlasPath);
                    atlas = null;
                }
            }

            Sprite fallback = null;
            // Berserk must never show the Drill fist again when its separate atlas is absent.
            if (atlas == null && spritePath != BerserkSpritePath)
            {
                if (!FallbackSprites.TryGetValue(spritePath, out fallback) || fallback == null)
                    FallbackSprites[spritePath] = fallback = GameAssetCatalog.Load<Sprite>(spritePath);
            }
            if (atlas == null && fallback == null) return;

            string effectName = spritePath.Substring(spritePath.LastIndexOf('/') + 1);
            Transform previous = card.Find(effectName);
            if (previous != null)
            {
                previous.gameObject.SetActive(false);
                Destroy(previous.gameObject);
            }

            var go = new GameObject(effectName, typeof(RectTransform), typeof(LayoutElement), typeof(BattleHealEffect));
            var rect = (RectTransform)go.transform;
            rect.SetParent(card, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            go.GetComponent<LayoutElement>().ignoreLayout = true;

            var effect = go.GetComponent<BattleHealEffect>();
            effect._rect = rect;
            effect._cardRect = card as RectTransform;
            if (atlas != null)
            {
                effect._atlasImage = go.AddComponent<RawImage>();
                effect._atlasImage.texture = atlas;
                effect._atlasImage.uvRect = new Rect(0f, 0f, 1f / FrameCount, 1f);
                effect._graphic = effect._atlasImage;
            }
            else
            {
                var image = go.AddComponent<Image>();
                image.sprite = fallback;
                image.preserveAspect = true;
                effect._graphic = image;
            }
            effect._graphic.raycastTarget = false;
            effect._started = started;
            effect.Update();
        }

        private void Update()
        {
            if (_graphic == null) return;
            float elapsed = Time.unscaledTime - _started;
            if (elapsed >= Duration)
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }
            if (elapsed < 0f) return;

            float progress = Mathf.Clamp01(elapsed / Duration);
            if (_atlasImage != null)
            {
                int frame = Mathf.Min(FrameCount - 1, Mathf.FloorToInt(progress * FrameCount));
                _atlasImage.uvRect = new Rect(frame / (float)FrameCount, 0f, 1f / FrameCount, 1f);
            }
            _graphic.color = new Color(1f, 1f, 1f, AlphaAt(elapsed));

            // Keep the artwork square on rectangular 106x155-ish battle cards.
            float width = _cardRect != null ? _cardRect.rect.width : 80f;
            float height = _cardRect != null ? _cardRect.rect.height : 100f;
            float size = Mathf.Max(1f, Mathf.Min(width * 0.78f, height * 0.65f));
            _rect.sizeDelta = Vector2.one * size;
            _rect.localScale = Vector3.one * Mathf.Lerp(0.94f, 1.03f, Mathf.SmoothStep(0f, 1f, progress));
            _rect.anchoredPosition = Vector2.up * (height * 0.04f * progress);
        }
    }
}
