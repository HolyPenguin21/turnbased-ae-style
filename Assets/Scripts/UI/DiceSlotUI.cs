using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One die face — an Image swapped between hitSprite/missSprite (see DiceFace_Hit.png/
    // DiceFace_Miss.png, Assets/Textures/UI/Dice) — with PlayRoll animating a coin-flip-style
    // spin instead of a hard cut: RectTransform.localScale.x oscillates through 0 a few times,
    // swapping to a random face each time it passes through zero, and lands on the real result
    // on the last cycle. Sells "rolling" without needing actual in-between spin art (no dice
    // model/sprite sheet exists — see DiceRowUI's own former comment on this).
    public class DiceSlotUI : MonoBehaviour
    {
        [SerializeField] private Image image;
        // Idle placeholder before this slot has ever rolled — Dice_Base.png, the neutral
        // unmarked die the two faces themselves were generated from (see the user's own
        // request), rather than defaulting to missSprite as if a roll had already happened.
        [SerializeField] private Sprite baseSprite;
        [SerializeField] private Sprite hitSprite;
        [SerializeField] private Sprite missSprite;

        // Every roll GROUP (one whole DiceRowUI.ShowRoll/BattleCombatantRowUI.SetDice call)
        // takes exactly this long overall, no matter how many dice are in it — individual dice
        // within the group land one after another (index 0 first, the last index landing at
        // exactly the requested group duration) rather than all landing together
        // (2026-08-24). See the index/count overload of PlayRoll.
        public const float FullRollDuration = 1f;
        public const float FateRerollDuration = 0.5f;
        // Target half-flip length a die's own share of the total duration is divided into whole
        // flips around (see RollRoutine) — not itself the duration of any single flip, since
        // that gets stretched/compressed slightly so flipCount flips exactly fill the die's
        // actual landing time.
        private const float TargetFlipHalfDuration = 0.08f;
        // Floor on flips even for a die landing very early in its group, so a fast landing
        // still reads as a quick spin rather than a bare flicker.
        private const int MinFlips = 3;

        private Coroutine _rollRoutine;

        private void Awake()
        {
            if (image != null)
                image.sprite = baseSprite;
        }

        // No animation — used to reset a slot to its default/placeholder face before a roll
        // exists yet (e.g. freshly spawned), same idea as the old "X" placeholder text.
        public void SetImmediate(bool hit)
        {
            if (_rollRoutine != null)
            {
                StopCoroutine(_rollRoutine);
                _rollRoutine = null;
            }
            ApplySprite(hit);
            if (image != null)
                image.rectTransform.localScale = Vector3.one;
        }

        // onComplete (optional): fired once the flip settles on its final face. A single-die
        // roll with no group of its own — lands after the full-roll duration, same as index 0
        // of a 1-die group.
        public void PlayRoll(bool hit, System.Action onComplete = null)
        {
            PlayRoll(hit, 0, 1, FullRollDuration, onComplete);
        }

        // index/count: this die's position (0-based) among the `count` dice rolled together in
        // this same call (see DiceRowUI.ShowRoll/BattleCombatantRowUI.SetDice) — dice land one
        // after another from index 0 to count-1, spaced evenly across the supplied duration, so
        // the whole group finishes in the same time regardless of how many dice it contains,
        // than every die spinning for a fixed length and the group as a whole taking longer the
        // more dice it has (per the user's own request, 2026-08-24).
        public void PlayRoll(bool hit, int index, int count, System.Action onComplete = null)
        {
            PlayRoll(hit, index, count, FullRollDuration, onComplete);
        }

        public void PlayRoll(bool hit, int index, int count, float groupDuration, System.Action onComplete = null)
        {
            if (!gameObject.activeInHierarchy)
            {
                SetImmediate(hit);
                onComplete?.Invoke();
                return;
            }
            if (_rollRoutine != null)
                StopCoroutine(_rollRoutine);
            float landDelay = Mathf.Max(0f, groupDuration) * (index + 1) / Mathf.Max(1, count);
            _rollRoutine = StartCoroutine(RollRoutine(hit, landDelay, onComplete));
        }

        private IEnumerator RollRoutine(bool finalHit, float duration, System.Action onComplete)
        {
            if (image == null)
            {
                _rollRoutine = null;
                onComplete?.Invoke();
                yield break;
            }
            RectTransform rt = image.rectTransform;
            // flipCount flips, each half taking halfDuration, exactly fill `duration` — so this
            // die visibly lands right at its own scheduled point in the group instead of finishing
            // its flips early/late and sitting idle (or overshooting) before/after.
            int flipCount = Mathf.Max(MinFlips, Mathf.RoundToInt(duration / (2f * TargetFlipHalfDuration)));
            float halfDuration = duration / (2f * flipCount);
            for (int i = 0; i < flipCount; i++)
            {
                bool isLast = i == flipCount - 1;
                bool faceThisFlip = isLast ? finalHit : Random.value < 0.5f;
                yield return ScaleX(rt, 1f, 0f, halfDuration);
                ApplySprite(faceThisFlip);
                yield return ScaleX(rt, 0f, 1f, halfDuration);
            }
            _rollRoutine = null;
            onComplete?.Invoke();
        }

        private static IEnumerator ScaleX(RectTransform rt, float from, float to, float duration)
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                Vector3 scale = rt.localScale;
                scale.x = Mathf.Lerp(from, to, Mathf.Clamp01(t / duration));
                rt.localScale = scale;
                yield return null;
            }
            Vector3 finalScale = rt.localScale;
            finalScale.x = to;
            rt.localScale = finalScale;
        }

        private void ApplySprite(bool hit)
        {
            if (image != null)
                image.sprite = hit ? hitSprite : missSprite;
        }
    }
}
