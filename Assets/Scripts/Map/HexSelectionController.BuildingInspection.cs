using Game.Economy;
using Game.Players;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Game.Map
{
    // Foreign-building inspection is deliberately isolated from the main selection file so the
    // existing own-building management flow stays untouched. The ordinary click handler already
    // selects a foreign building's hex; this late-frame pass only upgrades a PRECISE click on the
    // currently visible foreign marker into BaseViewerModalUI.ShowReadOnly, mirroring the enemy-
    // army marker's inspection behaviour without exposing any management actions.
    public partial class HexSelectionController
    {
        private void LateUpdate()
        {
            if (baseViewerModal == null || targetCamera == null || turnController == null
                || Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
                return;
            if (turnController.InputBlocked || !turnController.TurnConfirmed)
                return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            PlayerSetupData viewer = turnController.CurrentPlayer;
            if (viewer == null || !viewer.IsHuman)
                return;

            Vector2 screenPoint = Mouse.current.position.ReadValue();
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building == null || building.Owner == null || building.Owner == viewer)
                    continue;
                // This modal represents Citadel/Base and the existing hero-built resource-site
                // building type. Internal Facilities live inside those buildings and are exposed
                // through the read-only grid once the host building is opened.
                if (!building.IsBase && building.HasTieredUnlock)
                    continue;
                // Never turn visual inspection into an intelligence leak. Last-seen building
                // memory currently stores marker appearance/existence, not a frozen Facility
                // roster, so only a building visible RIGHT NOW may open the live detail modal.
                if (!VisionSystem.IsVisible(viewer, building.Hex))
                    continue;
                if (building.Visual == null
                    || !building.Visual.ContainsScreenPoint(targetCamera, screenPoint, mapMarkerClickPadding))
                    continue;

                if (armyViewerModal != null)
                    armyViewerModal.Hide();
                if (researchProductionModal != null)
                    researchProductionModal.Hide();
                baseViewerModal.ShowReadOnly(building);
                return;
            }
        }
    }
}
