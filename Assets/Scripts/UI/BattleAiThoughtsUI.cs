using System.Collections;
using Game.Units;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // Drives the AiЕhoughts_Text panel — "freshest wins", per the user's own spec: only ONE
    // pending thought is ever held, a new event always overwrites whatever hasn't been shown yet
    // so a stale reaction can never display out of context. minDisplaySeconds only gates how fast
    // the VISIBLE text can change, never what it changes to — that's always whatever's newest at
    // the moment the wait ends.
    public class BattleAiThoughtsUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text thoughtText;
        [SerializeField] private float minDisplaySeconds = 3.5f;
        // Per the user's own spec — the phrase types in gradually rather than appearing all at
        // once. Counted against minDisplaySeconds, not on top of it (see DisplayLoop), so a
        // phrase's total time on screen doesn't grow just because it types in slower/faster.
        [SerializeField] private float typeInDuration = 1f;

        private string _pendingText;
        private bool _hasPending;
        private Coroutine _displayRoutine;

        // The AI side's own hero (if any) narrates, per the placeholder text's own "Hero name :
        // ..." format — the hero is the army's single voice, not each individual acting unit.
        // With no hero present for that side, the thought shows on its own.
        public void Show(UnitData sideHero, string thought)
        {
            if (string.IsNullOrEmpty(thought))
                return;
            SetPending(sideHero != null ? $"{sideHero.Name}: {thought}" : thought);
        }

        // Player-idle nudge — commentary on the wait itself, not tied to a specific unit, so no
        // name prefix regardless of whether either side has a hero.
        public void ShowIdle(string thought)
        {
            if (!string.IsNullOrEmpty(thought))
                SetPending(thought);
        }

        private void SetPending(string formatted)
        {
            _pendingText = formatted;
            _hasPending = true;
            if (_displayRoutine == null)
                _displayRoutine = StartCoroutine(DisplayLoop());
        }

        private IEnumerator DisplayLoop()
        {
            while (_hasPending)
            {
                string text = _pendingText;
                _hasPending = false;
                yield return TypeIn(text);

                // The hold is measured from the start of typing, not from when typing finishes —
                // a slower/faster type-in doesn't change how long the finished phrase stays up.
                float remaining = minDisplaySeconds - typeInDuration;
                if (remaining > 0f)
                    yield return new WaitForSeconds(remaining);
            }
            _displayRoutine = null;
        }

        // Reveals `text` character-by-character via TMP's maxVisibleCharacters (handles the
        // "Hero: " prefix and the phrase as one continuous reveal, no special-casing needed).
        // Bails out early the instant something newer has already arrived (_hasPending) instead
        // of finishing a reveal nobody's about to read — same "freshest wins, no stale phrases"
        // rule the class comment already promises, just applied mid-type as well as between
        // phrases.
        private IEnumerator TypeIn(string text)
        {
            if (thoughtText == null)
                yield break;
            thoughtText.text = text;
            thoughtText.ForceMeshUpdate();
            int totalChars = thoughtText.textInfo.characterCount;

            if (totalChars <= 0 || typeInDuration <= 0f)
            {
                thoughtText.maxVisibleCharacters = int.MaxValue;
                yield break;
            }

            thoughtText.maxVisibleCharacters = 0;
            float t = 0f;
            while (t < typeInDuration)
            {
                if (_hasPending)
                    yield break;
                t += Time.deltaTime;
                thoughtText.maxVisibleCharacters = Mathf.FloorToInt(totalChars * Mathf.Clamp01(t / typeInDuration));
                yield return null;
            }
            thoughtText.maxVisibleCharacters = totalChars;
        }

        public void Clear()
        {
            if (_displayRoutine != null)
            {
                StopCoroutine(_displayRoutine);
                _displayRoutine = null;
            }
            _hasPending = false;
            if (thoughtText != null)
            {
                thoughtText.text = string.Empty;
                thoughtText.maxVisibleCharacters = int.MaxValue;
            }
        }
    }
}
