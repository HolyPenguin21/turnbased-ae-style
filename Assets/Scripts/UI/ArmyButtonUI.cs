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
        [SerializeField] private GameObject lampOn;
        [SerializeField] private GameObject lampOff;
        [SerializeField] private TMP_Text apLabel;
        [SerializeField] private TMP_Text movementLabel;
        [SerializeField] private GameObject apIcon;
        [SerializeField] private GameObject movementIcon;

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
            ResolveViewReferences();
            Refresh();

            SetSelected(selected);

            if (button != null)
            {
                // Disabled, not hidden — a disabled Button already reads visually as "this one's
                // picked" via its own DisabledColor (see the standard ColorTint ButtonUI setup),
                // no extra styling needed. Never disabled for the garrison specifically — see
                // ArmyButtonRowUI.Show, which is the one that decides `selected` per-army.
                button.interactable = !selected;
                Game.UI.UIButtonEventUtility.ResetRuntimeListeners(button);
                button.onClick.AddListener(() => onClick?.Invoke(army));
            }
        }

        public void SetSelected(bool selected)
        {
            ResolveViewReferences();
            if (lampOn != null) lampOn.SetActive(selected);
            if (lampOff != null) lampOff.SetActive(!selected);
        }

        private void ResolveViewReferences()
        {
            if (lampOn == null) lampOn = transform.Find("Image_LampOn")?.gameObject;
            if (lampOff == null) lampOff = transform.Find("Image_LampOff")?.gameObject;
            if (apLabel == null) apLabel = transform.Find("Text_AP")?.GetComponent<TMP_Text>();
            if (movementLabel == null) movementLabel = transform.Find("Text_Cap")?.GetComponent<TMP_Text>();
            if (apIcon == null) apIcon = transform.Find("Image_AP")?.gameObject;
            if (movementIcon == null) movementIcon = transform.Find("Image_Cap")?.gameObject;
        }

        // Called after a rename so an already-instantiated button (both the hex-side row's and
        // the modal's own) picks up the new name without needing to be torn down and rebuilt.
        public void Refresh()
        {
            if (label == null || Army == null)
                return;

            bool statsVisible = _showStats && !Army.IsGarrison && !Army.IsAirfield && !Army.IsPrison;
            if (apLabel != null)
            {
                apLabel.gameObject.SetActive(statsVisible);
                apLabel.text = Army.PendingActivationApCost.ToString();
            }
            if (movementLabel != null)
            {
                movementLabel.gameObject.SetActive(statsVisible);
                // Preserve the map row's existing movement semantics despite the prefab's Text_Cap name.
                movementLabel.text = $"{Army.CurrentMovement}/{Army.MaxMovement}";
            }
            if (apIcon != null) apIcon.SetActive(statsVisible);
            if (movementIcon != null) movementIcon.SetActive(statsVisible);
            if (apLabel != null && movementLabel != null)
            {
                label.text = Army.Name;
                return;
            }

            if (!_showStats || Army.IsGarrison || Army.IsAirfield)
            {
                label.text = Army.Name;
                return;
            }

            label.text = Army.PendingActivationApCost <= 0
                ? $"{Army.Name} - {Army.CurrentMovement}/{Army.MaxMovement}"
                : $"{Army.Name} - {Army.PendingActivationApCost}AP, {Army.CurrentMovement}/{Army.MaxMovement}";
        }
    }
}

