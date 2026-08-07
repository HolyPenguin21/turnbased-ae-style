using System;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One button representing an army on a hex — used both by ArmyButtonRowUI's hex-side row
    // (outside any modal, replacing the brief unit-info panel once a hex has 2+ armies) and by
    // ArmyViewerModalUI's own in-modal row (switching which army is shown). Also doubles as a
    // drag-and-drop target: ArmyUnitCardUI hit-tests screen position against RectTransform to
    // detect a unit card dropped on this button (see ArmyViewerModalUI.TryDropUnit).
    public class ArmyButtonUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;
        [SerializeField] private Button button;

        public ArmyData Army { get; private set; }
        public RectTransform RectTransform => (RectTransform)transform;

        // Whether to append activation-AP/movement stats after the name — only the hex-side
        // row's "pick an army to move" use turns this on (see ArmyButtonRowUI.Show); the
        // in-modal row switching which army is displayed has no use for it. Remembered here
        // (not just a Setup parameter) so the parameterless Refresh() below — called after a
        // rename — keeps formatting the label the same way.
        private bool _showStats;

        public void Setup(ArmyData army, Action<ArmyData> onClick, bool selected = false, bool showStats = false)
        {
            Army = army;
            _showStats = showStats;
            Refresh();

            if (button != null)
            {
                // Disabled, not hidden — a disabled Button already reads visually as "this one's
                // picked" via its own DisabledColor (see the standard ColorTint ButtonUI setup),
                // no extra styling needed. Never disabled for the garrison specifically — see
                // ArmyButtonRowUI.Show, which is the one that decides `selected` per-army.
                button.interactable = !selected;
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => onClick?.Invoke(army));
            }
        }

        // Called after a rename so an already-instantiated button (both the hex-side row's and
        // the modal's own) picks up the new name without needing to be torn down and rebuilt.
        public void Refresh()
        {
            if (label == null || Army == null)
                return;

            if (!_showStats || Army.IsGarrison)
            {
                label.text = Army.Name;
                return;
            }

            int apCost = Army.HasActivatedThisTurn ? 0 : Army.ActivationApCost;
            label.text = $"{Army.Name} — {apCost}AP, {Army.CurrentMovement}/{Army.MaxMovement}";
        }
    }
}
