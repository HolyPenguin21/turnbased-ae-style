using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Shows/hides a UI panel with the selected hex's action buttons (Garrison/Base). Hidden by
    // default (see Awake) — build the actual Canvas/panel hierarchy in the editor and wire the
    // references here.
    public class HexInfoPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button garrisonButton;
        [SerializeField] private Button baseButton;

        private void Awake()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }

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
    }
}
