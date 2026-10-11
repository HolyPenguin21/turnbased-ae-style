using Game.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    [DisallowMultipleComponent]
    public sealed class UIButtonSound : MonoBehaviour
    {
        private Button button;
        public void Bind(Button target)
        {
            if (button != null) button.onClick.RemoveListener(Play);
            button = target; Rebind();
        }
        public void Rebind()
        {
            if (button == null) button = GetComponent<Button>();
            if (button == null) return;
            button.onClick.RemoveListener(Play);
            button.onClick.AddListener(Play);
        }
        private void Play()
        {
            // Do not test active state here: an earlier action may already have hidden the button.
            GameAudioManager.Instance?.PlayClick();
        }
        private void OnDestroy() { if (button != null) button.onClick.RemoveListener(Play); }
    }
}
