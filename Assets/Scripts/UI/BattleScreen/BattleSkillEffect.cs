using System;
using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Five skill animations live in one 1536x1024 RGBA atlas, top-to-bottom:
    // Heal, Drill, Berserk, Bodyguard, Shock. There are eight 192px-wide frames per row.
    // Heights alternate between 204 and 205 pixels; never assume a 256px square grid.
    // This component is presentation-only; the battle engine and its timings do not wait for it.
    public sealed class BattleSkillEffect : MonoBehaviour
    {
        public enum Kind { Heal = 0, Drill = 1, Berserk = 2, Bodyguard = 3, Shock = 4 }

        public const string AtlasKey = "UI/Effects/Effects";
        public const int AtlasWidth = 1536;
        public const int AtlasHeight = 1024;
        public const int FrameCount = 8;
        public const int SkillCount = 5;
        public const float Duration = 0.88f;
        private const float VisibleAlpha = 0.95f;

        private static Texture2D _atlas;
        private static Sprite[][] _spriteRows;
        private static bool _warnedAboutAtlas;

        private RectTransform _rect;
        private RectTransform _cardRect;
        private Image _image;
        private Sprite[] _frames;
        private float _started;

        // Unity sprite rects use bottom-left coordinates, while the source art's
        // skill rows are numbered from the top. Rounded boundaries tile all 1024
        // vertical pixels without gaps or crossing into the neighbouring animation.
        public static Rect FrameRect(Kind skill, int frame)
        {
            int row = (int)skill;
            if (row < 0 || row >= SkillCount) throw new ArgumentOutOfRangeException(nameof(skill));
            if (frame < 0 || frame >= FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));

            int top = Mathf.RoundToInt(row * (AtlasHeight / (float)SkillCount));
            int bottom = Mathf.RoundToInt((row + 1) * (AtlasHeight / (float)SkillCount));
            return new Rect(frame * (AtlasWidth / FrameCount), AtlasHeight - bottom,
                AtlasWidth / FrameCount, bottom - top);
        }

        public static float AlphaAt(float elapsed)
        {
            if (elapsed < 0f || elapsed >= Duration) return 0f;
            if (elapsed < 0.12f)
                return VisibleAlpha * Mathf.SmoothStep(0f, 1f, elapsed / 0.12f);
            if (elapsed <= Duration - 0.18f) return VisibleAlpha;
            return VisibleAlpha * Mathf.SmoothStep(1f, 0f,
                (elapsed - (Duration - 0.18f)) / 0.18f);
        }

        private static Sprite[] GetFrames(Kind kind)
        {
            int row = (int)kind;
            if (row < 0 || row >= SkillCount) return null;
            if (_spriteRows != null && _spriteRows[row] != null) return _spriteRows[row];

            if (_atlas == null) _atlas = GameAssetCatalog.Load<Texture2D>(AtlasKey);
            if (_atlas == null || _atlas.width != AtlasWidth || _atlas.height != AtlasHeight)
            {
                if (!_warnedAboutAtlas)
                {
                    _warnedAboutAtlas = true;
                    Debug.LogWarning("Battle skill atlas is missing or has the wrong dimensions. " +
                        "Install Assets/Textures/UI/Effects/Effects.png (1536x1024) and keep its .meta file.");
                }
                return null;
            }

            if (_spriteRows == null) _spriteRows = new Sprite[SkillCount][];
            var sprites = new Sprite[FrameCount];
            for (int i = 0; i < FrameCount; i++)
            {
                Sprite frame = Sprite.Create(_atlas, FrameRect(kind, i),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                frame.name = kind + "_frame_" + i;
                frame.hideFlags = HideFlags.HideAndDontSave;
                sprites[i] = frame;
            }
            _spriteRows[row] = sprites;
            return sprites;
        }

        public static void Show(Transform card, float started, Kind kind)
        {
            if (card == null || Time.unscaledTime - started >= Duration) return;
            Sprite[] frames = GetFrames(kind);
            if (frames == null) return;

            string name = kind + "Effect";
            Transform old = card.Find(name);
            if (old != null)
            {
                old.gameObject.SetActive(false);
                Destroy(old.gameObject);
            }

            var go = new GameObject(name, typeof(RectTransform), typeof(Image),
                typeof(LayoutElement), typeof(BattleSkillEffect));
            var rect = (RectTransform)go.transform;
            rect.SetParent(card, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            go.GetComponent<LayoutElement>().ignoreLayout = true;

            var effect = go.GetComponent<BattleSkillEffect>();
            effect._rect = rect;
            effect._cardRect = card as RectTransform;
            effect._image = go.GetComponent<Image>();
            effect._image.sprite = frames[0];
            effect._image.preserveAspect = true;
            effect._image.raycastTarget = false;
            effect._image.maskable = false;
            effect._frames = frames;
            effect._started = started;
            effect.Update();
        }

        private void Update()
        {
            if (_image == null) return;
            float elapsed = Time.unscaledTime - _started;
            if (elapsed >= Duration)
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }
            if (elapsed < 0f) return;

            int frame = Mathf.Min(FrameCount - 1,
                Mathf.FloorToInt(elapsed / Duration * FrameCount));
            if (_image.sprite != _frames[frame]) _image.sprite = _frames[frame];
            _image.color = new Color(1f, 1f, 1f, AlphaAt(elapsed));

            // Keep the illustration stationary. All motion is authored in the frames.
            float width = _cardRect != null ? _cardRect.rect.width : 80f;
            float height = _cardRect != null ? _cardRect.rect.height : 100f;
            float size = Mathf.Max(1f, Mathf.Min(width * 0.88f, height * 0.72f));
            _rect.sizeDelta = Vector2.one * size;
            _rect.localScale = Vector3.one;
            _rect.anchoredPosition = Vector2.zero;
        }
    }
}
