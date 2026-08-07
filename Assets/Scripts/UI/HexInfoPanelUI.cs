using System;
using System.Collections.Generic;
using Game.Economy;
using Game.Terrain;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Shows/hides a UI panel with the selected hex's info. Build the actual Canvas/panel/text
    // hierarchy in the editor and wire the references here.
    public class HexInfoPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text infoText;
        [SerializeField] private Button garrisonButton;
        [SerializeField] private Button baseButton;

        private void Awake()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }

        // effectiveYields is the terrain's own yield plus any citadel bonus already combined
        // (see HexResourceCalculator) — this panel just displays it, it doesn't compute it.
        // ownerName/buildingName are both null when the hex has no citadel on it.
        public void ShowHex(int col, int row, TerrainTypeEntry terrain, string ownerName, string buildingName, ResourceYields effectiveYields)
        {
            if (panelRoot != null)
                panelRoot.SetActive(true);
            if (infoText == null || terrain == null)
                return;

            string text = $"Hex ({col}, {row})\n{terrain.terrainName}";

            if (effectiveYields != null && effectiveYields.HasAnyYield)
                text += $"\nYields: {FormatYields(effectiveYields)}";

            text += $"\nBuildings: {(string.IsNullOrEmpty(buildingName) ? "none" : buildingName)}";
            text += $"\nOwner: {(string.IsNullOrEmpty(ownerName) ? "none" : ownerName)}";

            infoText.text = text;
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
        // hex's building is tagged BuildingAbilities.Base and owned by the current player (see
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

        // Only the non-zero amounts, e.g. "2 Human, 1 Materials" — skips whichever of the
        // four resources this hex doesn't produce instead of listing all four every time.
        private static string FormatYields(ResourceYields yields)
        {
            var parts = new List<string>();
            if (yields.human > 0) parts.Add($"{yields.human} Human");
            if (yields.energy > 0) parts.Add($"{yields.energy} Energy");
            if (yields.materials > 0) parts.Add($"{yields.materials} Materials");
            if (yields.tech > 0) parts.Add($"{yields.tech} Tech");
            return string.Join(", ", parts);
        }
    }
}
