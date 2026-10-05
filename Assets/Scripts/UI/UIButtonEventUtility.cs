using Game.Audio;
using UnityEngine.UI;

namespace Game.UI
{
    public static class UIButtonEventUtility
    {
        public static void ResetRuntimeListeners(Button button)
        {
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            SceneUIAudioBinder.BindCreatedRoot(button);
        }
    }
}
