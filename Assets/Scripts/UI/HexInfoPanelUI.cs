using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Shows/hides a UI panel with the selected hex's action buttons (Garrison/Base). Hidden by
    // default via panelRoot's own inactive state in the scene/prefab — build the actual Canvas/
    // panel hierarchy in the editor and wire the references here. Deliberately no Awake() hiding
    // it too: panelRoot IS this component's own GameObject in the current scene, and that
    // GameObject starts inactive, so Awake() wouldn't run until the first SetActive(true) below
    // triggers it — at which point it would immediately re-hide the panel it was just asked to
    // show.
    public class HexInfoPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button garrisonButton;
        [SerializeField] private Button baseButton;
        [SerializeField] private Button researchButton;
        [SerializeField] private Button productionButton;

        public void ShowHex()
        {
            if (panelRoot != null)
                panelRoot.SetActive(true);
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
            if (garrisonButton != null)
                garrisonButton.gameObject.SetActive(false);
            if (baseButton != null)
                baseButton.gameObject.SetActive(false);
            if (researchButton != null)
                researchButton.gameObject.SetActive(false);
            if (productionButton != null)
                productionButton.gameObject.SetActive(false);
        }

        // Independent of ShowHex — a direct way to reach the garrison's modal regardless of
        // how many armies currently share the hex (see HexSelectionController.SelectHex),
        // since a lone unit sitting alone in the garrison would otherwise have no way to be
        // sorted into a movable army at all.
        public void SetGarrisonButtonVisible(bool visible, Action onClick)
        {
            if (garrisonButton == null)
                return;
            garrisonButton.gameObject.SetActive(visible);
            if (visible)
            {
                garrisonButton.onClick.RemoveAllListeners();
                garrisonButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }

        // Same idea as SetGarrisonButtonVisible, for BaseViewerModalUI — visible whenever this
        // hex's building has IsBase set and is owned by the current player (see
        // HexSelectionController.SelectHex).
        public void SetBaseButtonVisible(bool visible, Action onClick)
        {
            if (baseButton == null)
                return;
            baseButton.gameObject.SetActive(visible);
            if (visible)
            {
                baseButton.onClick.RemoveAllListeners();
                baseButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }

        // Same idea as SetGarrisonButtonVisible/SetBaseButtonVisible — Research now lives in
        // this fixed nav row instead of the variable-length ResourceActionRowUI (see
        // HexSelectionController.SelectHex), so it stays put next to Garrison/Base/Production
        // rather than shifting around with however many extraction-Facility buttons show.
        public void SetResearchButtonVisible(bool visible, Action onClick)
        {
            if (researchButton == null)
                return;
            researchButton.gameObject.SetActive(visible);
            if (visible)
            {
                researchButton.onClick.RemoveAllListeners();
                researchButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }

        // Same idea, for Production.
        public void SetProductionButtonVisible(bool visible, Action onClick)
        {
            if (productionButton == null)
                return;
            productionButton.gameObject.SetActive(visible);
            if (visible)
            {
                productionButton.onClick.RemoveAllListeners();
                productionButton.onClick.AddListener(() => onClick?.Invoke());
            }
        }
    }
}
