using Game.Units;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // Finish the visible phrase; retain only the newest pending thought, never a backlog.
    public class BattleAiThoughtsUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text thoughtText;
        [SerializeField] private float minDisplaySeconds = 3.5f;
        [SerializeField] private float typeInDuration = 0.75f;

        private string _currentText;
        private string _pendingText;
        private float _elapsed;
        private float _typeSeconds;
        private float _holdSeconds;
        private int _totalCharacters;
        private int _visibleCharacters;

        private void Awake() => Clear();
        private void OnDisable() => Clear();

        // The side's commander is its voice; hero-less armies have no name prefix.
        // Terminal results appear complete immediately, before the outcome popup can close.
        public void Show(UnitData sideHero, string thought, bool immediate = false)
        {
            if (!CanShow || string.IsNullOrWhiteSpace(thought))
                return;
            SetPending(sideHero != null ? $"{sideHero.Name}: {thought}" : thought, immediate);
        }

        // A nudge must never replace a combat reaction or wait to appear after the player acts.
        public void ShowIdle(string thought)
        {
            if (!CanShow || string.IsNullOrWhiteSpace(thought) || _pendingText != null
                || (_currentText != null && (_visibleCharacters < _totalCharacters || _elapsed < _holdSeconds)))
                return;
            SetPending(thought, false);
        }

        private bool CanShow => isActiveAndEnabled && thoughtText != null;

        private void SetPending(string formatted, bool immediate)
        {
            if (immediate)
            {
                BeginDisplay(formatted, true);
                return;
            }
            // Repeated events do not restart typing or extend the visible phrase's timer.
            if (formatted == _currentText)
            {
                _pendingText = null;
                return;
            }
            if (formatted == _pendingText)
                return;
            if (_currentText == null || (_visibleCharacters >= _totalCharacters && _elapsed >= _holdSeconds))
                BeginDisplay(formatted, false);
            else
                _pendingText = formatted;
        }

        private void BeginDisplay(string text, bool immediate)
        {
            _currentText = text;
            _pendingText = null;
            _elapsed = 0f;
            _typeSeconds = immediate ? 0f : Mathf.Max(0f, typeInDuration);
            _holdSeconds = Mathf.Max(_typeSeconds, minDisplaySeconds);

            // Parse once per phrase, with all characters included. Unit names are literal text.
            thoughtText.richText = false;
            thoughtText.maxVisibleCharacters = int.MaxValue;
            thoughtText.text = text;
            thoughtText.ForceMeshUpdate();
            _totalCharacters = thoughtText.textInfo.characterCount;
            _visibleCharacters = _typeSeconds <= 0f ? _totalCharacters : 0;
            thoughtText.maxVisibleCharacters = _visibleCharacters;
        }

        private void Update() => AdvanceDisplay(Time.unscaledDeltaTime);

        private void AdvanceDisplay(float deltaSeconds)
        {
            if (_currentText == null || thoughtText == null)
                return;
            _elapsed += Mathf.Max(0f, deltaSeconds);
            if (_visibleCharacters < _totalCharacters)
            {
                int visible = _typeSeconds <= 0f ? _totalCharacters
                    : Mathf.FloorToInt(_totalCharacters * Mathf.Clamp01(_elapsed / _typeSeconds));
                if (visible != _visibleCharacters)
                {
                    _visibleCharacters = visible;
                    thoughtText.maxVisibleCharacters = visible;
                }
                // Even a long frame must render the completed phrase before replacing it.
                return;
            }
            if (_pendingText != null && _elapsed >= _holdSeconds)
                BeginDisplay(_pendingText, false);
        }

        public void Clear()
        {
            _currentText = null;
            _pendingText = null;
            _elapsed = 0f;
            _totalCharacters = 0;
            _visibleCharacters = 0;
            if (thoughtText != null)
            {
                thoughtText.text = string.Empty;
                thoughtText.maxVisibleCharacters = int.MaxValue;
            }
        }
    }
}
