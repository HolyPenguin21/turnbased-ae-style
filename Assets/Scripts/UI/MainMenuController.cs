using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Game.UI
{
    // Hook these methods up to Button OnClick events in the MainMenu scene.
    public class MainMenuController : MonoBehaviour
    {
        [SerializeField] private GameObject mainMenuCanvas;
        [FormerlySerializedAs("gameSetupPanel")]
        [SerializeField] private GameObject gameSetupCanvas;

        private void Update()
        {
            // Guarded by mainMenuCanvas's own active state — this component isn't disabled
            // when the setup screen takes over, so Space would otherwise keep re-triggering
            // "New Game" from there too.
            if (mainMenuCanvas != null && !mainMenuCanvas.activeSelf)
                return;

            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                OnNewGameClicked();
        }

        public void OnNewGameClicked()
        {
            if (gameSetupCanvas != null)
                gameSetupCanvas.SetActive(true);
            if (mainMenuCanvas != null)
                mainMenuCanvas.SetActive(false);
        }

        public void OnQuitClicked()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
