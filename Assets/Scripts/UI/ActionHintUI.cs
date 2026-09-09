using TMPro;
using UnityEngine;

namespace Game.UI
{
    // A non-blocking, persistent caption for a multi-step input mode — currently just the
    // equipment-attach flow (see CardHandUI.BeginAttachMode). Unlike SpawnHintPopupUI, this one
    // has NO confirm button, raises no events, and is deliberately NOT folded into
    // GameTurnController.InputBlocked/CardDraggingBlocked: it stays up for the whole duration of
    // the mode while the player keeps navigating the map and clicking cards/panels underneath it.
    // Dismissal is entirely the owning flow's job (it calls Hide() on complete/cancel).
    //
    // Position it wherever suits in the scene — it never grabs pointer input (its graphic must
    // have Raycast Target off), so it can sit anywhere over the HUD without stealing clicks.
    public class ActionHintUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text infoText;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Relies solely on the scene's own initial inactive state for "hidden until shown" — same
        // reasoning as SpawnHintPopupUI.Awake: panelRoot may be this component's own GameObject,
        // so a SetActive(false) here could undo the very activation that ran Awake.
        public void Show(string message)
        {
            if (infoText != null)
                infoText.text = message;
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                // Draw order on this project's single shared Canvas is sibling order — keep the
                // caption on top of whatever HUD it overlays.
                panelRoot.transform.SetAsLastSibling();
            }
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
