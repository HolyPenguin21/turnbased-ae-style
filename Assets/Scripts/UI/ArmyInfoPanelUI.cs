using Game.Map;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // Shows/hides a UI panel with the selected hex's army info — only ever shown for a hex
    // with exactly one (non-garrison) army on it; see HexSelectionController.SelectHex. Two+
    // armies use ArmyButtonRowUI instead, and a lone garrison shows neither (nothing to select
    // there until it's sorted into a real army — see ArmyViewerModalUI). Separate Canvas from
    // HexInfoPanelUI by design — build the actual Canvas/panel/text hierarchy in the editor and
    // wire the references here.
    public class ArmyInfoPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text infoText;

        private void Awake()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }

        public void ShowArmy(ArmyData army)
        {
            if (panelRoot != null)
                panelRoot.SetActive(true);
            if (infoText == null || army == null)
                return;

            int count = army.Members.Count;
            infoText.text = $"{army.Name}\nMove: {army.CurrentMovement} / {army.MaxMovement}\n{count} unit{(count == 1 ? "" : "s")}";
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
