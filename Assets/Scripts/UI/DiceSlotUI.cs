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

        // Half-flip (scale 1->0 or 0->1) duration and how many full flips before landing — six
        // flips at 0.08s each half is ~1s total, quick enough not to stall the turn-order/battle
        // flow but long enough to actually read as a roll rather than a flicker.
        private const int FlipCount = 6;
        private const float FlipHalfDuration = 0.08f;

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

        // onComplete (optional): fired once the flip settles on its final face — lets a caller
        // (BattleCombatantRowUI/BattleAttackPopupUI) actually wait for the animation instead of
        // firing-and-forgetting it, so e.g. the Accept button can stay locked out until every die
        // this call touched has visibly landed (see the user's own report: Accept was clickable,
        // and the AI's own reactive reroll could resolve, mid-flip).
        public void PlayRoll(bool hit, System.Action onComplete = null)
        {
            if (!gameObject.activeInHierarchy)
            {
                SetImmediate(hit);
                onComplete?.Invoke();
                return;
            }
            if (_rollRoutine != null)
                StopCoroutine(_rollRoutine);
            _rollRoutine = StartCoroutine(RollRoutine(hit, onComplete));
        }

        private IEnumerator RollRoutine(bool finalHit, System.Action onComplete)
        {
            if (image == null)
            {
                _rollRoutine = null;
                onComplete?.Invoke();
                yield break;
            }
            RectTransform rt = image.rectTransform;
            for (int i = 0; i < FlipCount; i++)
            {
                bool isLast = i == FlipCount - 1;
                bool faceThisFlip = isLast ? finalHit : Random.value < 0.5f;
                yield return ScaleX(rt, 1f, 0f, FlipHalfDuration);
                ApplySprite(faceThisFlip);
                yield return ScaleX(rt, 0f, 1f, FlipHalfDuration);
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
