using TMPro;
using UnityEngine;

namespace Game.UI
{
    // Both live card views temporarily give their text space to management actions.
    internal sealed class CardActionHoverText
    {
        private readonly GameObject _title;
        private readonly TMP_Text[] _skills;
        private readonly bool[] _skillsWereEnabled;
        private bool _titleWasActive;
        private bool _hidden;

        public CardActionHoverText(GameObject title, params TMP_Text[] skills)
        {
            _title = title;
            _skills = skills;
            _skillsWereEnabled = new bool[skills.Length];
        }

        public void SetHidden(bool hidden)
        {
            if (!hidden)
            {
                Restore();
                return;
            }
            if (_hidden)
                return;

            _hidden = true;
            _titleWasActive = _title != null && _title.activeSelf;
            // Capture before changing anything: Army's moveText/skillsText can be aliases.
            for (int i = 0; i < _skills.Length; i++)
                _skillsWereEnabled[i] = _skills[i] != null && _skills[i].enabled;

            _title?.SetActive(false);
            for (int i = 0; i < _skills.Length; i++)
                if (_skills[i] != null)
                    // Equipment previews may activate the label's GameObject during hover.
                    // Disable its renderer so the action buttons still have clear space.
                    _skills[i].enabled = false;
        }

        public void Restore()
        {
            if (!_hidden)
                return;
            _hidden = false;
            _title?.SetActive(_titleWasActive);
            for (int i = 0; i < _skills.Length; i++)
                if (_skills[i] != null)
                    _skills[i].enabled = _skillsWereEnabled[i];
        }
    }
}
