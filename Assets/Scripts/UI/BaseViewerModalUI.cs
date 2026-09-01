using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.Map;
using Game.Terrain;
using Game.Turns;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // The Base Viewer: shows one BuildingData at a time — cell 0 is always the Base itself,
    // cells 1..TotalFacilitySlots are Facility slots (locked/empty/filled). Deliberately much
    // simpler than ArmyViewerModalUI: nothing here is ever reordered or moved once placed (see
    // BaseSlotCardUI), so the grid is a plain GridLayoutGroup-driven layout with no manual
    // slot-position/drag-reorder machinery at all. Upgrading the Base (grants the next Facility
    // slot + Defense/Resistance) and repairing the Base happen via hover-revealed buttons on the
    // Base cell. Internal Facility improvement is deliberately disabled until that gameplay
    // feature has authoritative effects worth exposing.
    public class BaseViewerModalUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private Button closeButton;
        [SerializeField] private Transform gridContainer;
        // Same GameObject as gridContainer — see ArmyViewerModalUI's identical field for why
        // this is kept separately typed instead of duplicating cell metrics as tunables.
        [SerializeField] private GridLayoutGroup grid;
        [SerializeField] private Image detailArt;
        // Split in two: detailText1 is the fixed identity/stat block (name, level, HP, defense,
        // resistance, fate — same for a Base cell or a Facility cell, minus whichever of those
        // don't apply), detailText2 is the ability list (name + full description per tag, see
        // FormatAbilities) — kept separate so each can be laid out/styled on its own rather than
        // sharing one growing text block.
        [SerializeField] private TMP_Text detailText1;
        [SerializeField] private TMP_Text detailText2;
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private GameTurnController turnController;
        // Only needed to look up the hex's own effective yield for a Collect-tagged Facility's
        // dormant upgrade implementation — nothing else here touches the map.
        [SerializeField] private HexMap map;

        // Read by BaseSlotCardUI (via the modal reference it already keeps) for GameConfig.
        // FormatAbilities — see ShowBaseSummary/ShowFacilityDetail for this modal's own use.
        public GameConfig GameConfig => gameConfig;

        private readonly List<BaseSlotCardUI> _cards = new List<BaseSlotCardUI>();
        private BuildingData _currentBuilding;
        private Canvas _canvas;
        // Mirrors ArmyViewerModalUI's ownership boundary: an enemy building uses this exact same
        // viewer, but inspection must never expose any gameplay mutation. Show() also auto-hardens
        // when the live CurrentPlayer is not the building owner, so a wrong call site cannot turn
        // into an ownership exploit merely because it forgot to call ShowReadOnly().
        private bool _readOnly;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
        public bool IsReadOnly => _readOnly;

        // UI code reads this to decide whether management controls may be exposed. Gameplay
        // methods below repeat the same guard themselves; hiding a button is never authorization.
        public bool CanManageCurrentBuilding => !_readOnly && _currentBuilding != null
            && (turnController == null || turnController.CurrentPlayer == null
                || turnController.CurrentPlayer == _currentBuilding.Owner);

        // Same purpose as ArmyViewerModalUI.Closed — HexSelectionController re-runs SelectHex
        // on close so an Upgrade purchased in here (new Facility slot, higher Defense/
        // Resistance) shows up on the hex-side info panel immediately.
        public event Action Closed;

        // Lets GameTurnController react to this modal opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked).
        public event Action VisibilityChanged;

        // Read by CardHandUI to know which building a dropped Facility card should join (see
        // TryDeployIntoBaseModal) — mirrors ArmyViewerModalUI.CurrentArmy. The actual drop path
        // must still pass TryPlaceFacility, which enforces CanManageCurrentBuilding.
        public BuildingData CurrentBuilding => _currentBuilding;

        public bool ContainsScreenPoint(Vector2 screenPosition)
        {
            if (!IsShowing || panelRoot == null)
                return false;
            return RectTransformUtility.RectangleContainsScreenPoint((RectTransform)panelRoot.transform, screenPosition, ResolveEventCamera());
        }

        private Camera ResolveEventCamera()
        {
            return _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
        }

        private void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (closeButton != null)
                closeButton.onClick.AddListener(Hide);
        }

        public void Show(BuildingData building)
        {
            _currentBuilding = building;
            _readOnly = building != null && turnController != null && turnController.CurrentPlayer != null
                && building.Owner != turnController.CurrentPlayer;
            ActivatePanel();
        }

        // Same building data and detail presentation as Show(), but no Upgrade/Repair/drop actions.
        // Used for a currently visible foreign building — unlike remembered army snapshots this
        // viewer intentionally does not fabricate last-seen Facility state that the memory layer
        // does not currently store.
        public void ShowReadOnly(BuildingData building)
        {
            _currentBuilding = building;
            _readOnly = true;
            ActivatePanel();
        }

        private void ActivatePanel()
        {
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            RefreshTitle();
            RefreshGrid();
            ShowBaseSummary();
            VisibilityChanged?.Invoke();
        }

        public void Hide()
        {
            bool wasShowing = IsShowing;
            if (panelRoot != null)
                panelRoot.SetActive(false);
            ClearGrid();
            _currentBuilding = null;
            _readOnly = false;
            if (wasShowing)
            {
                Closed?.Invoke();
                VisibilityChanged?.Invoke();
            }
        }

        private void Update()
        {
            if (!IsShowing || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
                return;
            Hide();
        }

        private void RefreshTitle()
        {
            if (titleText == null)
                return;
            if (_currentBuilding == null)
            {
                titleText.text = string.Empty;
                return;
            }
            // Level is meaningless for a non-tiered building (see BuildingData.HasTieredUnlock).
            titleText.text = _currentBuilding.HasTieredUnlock
                ? $"{_currentBuilding.Name} — <b>Level {_currentBuilding.Level}</b>"
                : _currentBuilding.Name;
        }

        // For a citadel/card-built Base, cell 0 is always the Base itself and cells
        // 1..TotalFacilitySlots are Facility slots. A hero-built resource site has no such
        // identity of its own (see BuildingData.HasTieredUnlock) — no cell reserved for it at
        // all, so the grid is just its TotalFacilitySlots Facility cells directly, starting at
        // 0 (see BaseSlotCardUI, which resolves locked/empty/filled from _currentBuilding
        // itself — this just hands each cell its index).
        private void RefreshGrid()
        {
            ClearGrid();
            if (gridContainer == null || gameConfig == null || gameConfig.baseSlotCardPrefab == null || _currentBuilding == null)
                return;

            int cellCount = _currentBuilding.HasTieredUnlock
                ? _currentBuilding.TotalFacilitySlots + 1
                : _currentBuilding.TotalFacilitySlots;
            for (int i = 0; i < cellCount; i++)
            {
                BaseSlotCardUI card = Instantiate(gameConfig.baseSlotCardPrefab, gridContainer);
                card.Setup(this, i, _currentBuilding);
                _cards.Add(card);
            }
        }

        private void ClearGrid() => UIListUtility.DestroyAndClear(_cards);

        // Default detail-panel state — the Base's own aggregate stats, shown on open and
        // whenever nothing else is selected. Mirrors ArmyViewerModalUI.ShowArmySummary's role.
        public void ShowBaseSummary()
        {
            if (detailArt != null)
            {
                detailArt.sprite = _currentBuilding != null ? _currentBuilding.DetailArt : null;
                detailArt.gameObject.SetActive(_currentBuilding != null && _currentBuilding.DetailArt != null);
            }
            if (_currentBuilding == null)
                return;

            // Level is meaningful only for the Base/Citadel itself. Internal Facility upgrade UI
            // is intentionally suppressed until it has a complete gameplay contract.
            string levelLine = _currentBuilding.HasTieredUnlock ? $"Level {_currentBuilding.Level}\n" : string.Empty;
            if (detailText1 != null)
                detailText1.text = $"{_currentBuilding.Name}\n" +
                    levelLine +
                    $"Structure Points: {_currentBuilding.StructurePointsCurrent}/{_currentBuilding.StructurePointsMax}\n" +
                    $"Defense: {_currentBuilding.Defense}\n" +
                    $"Resistance: {_currentBuilding.Resistance}\n" +
                    $"Fate: {_currentBuilding.Fate}";
            if (detailText2 != null)
                detailText2.text = FormatAbilities(_currentBuilding.Abilities);
        }

        public void ShowFacilityDetail(FacilityData facility)
        {
            if (facility == null)
            {
                ShowBaseSummary();
                return;
            }

            if (detailArt != null)
            {
                detailArt.sprite = facility.DetailArt;
                detailArt.gameObject.SetActive(true);
            }
            if (detailText1 != null)
                detailText1.text = facility.Name;
            if (detailText2 != null)
                detailText2.text = FormatAbilities(facility.Abilities);
        }

        // Full name + description per ability (this modal only ever shows the detail panel, no
        // compact card form) — see GameConfig.FormatAbilitiesDetailed. Falls back to the raw tag
        // names if gameConfig isn't wired.
        private string FormatAbilities(IEnumerable<string> abilities)
        {
            return gameConfig != null ? gameConfig.FormatAbilitiesDetailed(abilities) : string.Join(" ", abilities);
        }

        // Called by CardHandUI when a Facility card is dropped onto this open modal (see
        // TryDeployIntoBaseModal). A read-only/foreign viewer rejects before resolving a slot,
        // so merely inspecting an enemy Base can never become a back door into its FacilitySlots.
        public bool TryPlaceFacility(CardDefinition definition, Vector2 screenPosition)
        {
            if (!CanManageCurrentBuilding || _currentBuilding == null || definition == null)
                return false;

            // Only a Base/Citadel takes Facility cards — matches the direct hex-drop path's own
            // IsValidFacilityHexDropTarget check (see CardHandUI). A hero-built resource site can
            // be shown in this viewer too, and without this its FacilitySlots could be filled
            // through the modal even though the hex-drop path forbids it.
            if (!_currentBuilding.IsBase)
                return false;

            int? cellIndex = ResolveGridSlotIndex(screenPosition);
            if (!cellIndex.HasValue)
                return false;

            bool hasBaseCell = _currentBuilding.HasTieredUnlock;
            if (hasBaseCell && cellIndex.Value == 0)
                return false;

            int facilityIndex = hasBaseCell ? cellIndex.Value - 1 : cellIndex.Value;
            if (facilityIndex < 0 || facilityIndex >= _currentBuilding.TotalFacilitySlots)
                return false;
            if (facilityIndex >= _currentBuilding.UnlockedFacilitySlots)
                return false;
            if (_currentBuilding.FacilitySlots[facilityIndex] != null)
                return false;

            _currentBuilding.FacilitySlots[facilityIndex] = FacilityData.FromDefinition(definition);
            RefreshGrid();
            return true;
        }

        // Same row-major top-left math as ArmyViewerModalUI.ResolveGridSlotIndex, reading this
        // grid's own cellSize/spacing/constraintCount directly instead of duplicating them.
        private int? ResolveGridSlotIndex(Vector2 screenPosition)
        {
            if (grid == null)
                return null;

            var gridRect = (RectTransform)grid.transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(gridRect, screenPosition, ResolveEventCamera(), out Vector2 local))
                return null;

            Rect rect = gridRect.rect;
            float x = local.x - rect.xMin;
            float y = rect.yMax - local.y;
            if (x < 0f || y < 0f || x >= rect.width)
                return null;

            int columns = Mathf.Max(1, grid.constraintCount);
            int col = Mathf.Clamp(Mathf.FloorToInt(x / (grid.cellSize.x + grid.spacing.x)), 0, columns - 1);
            int row = Mathf.Max(0, Mathf.FloorToInt(y / (grid.cellSize.y + grid.spacing.y)));
            return row * columns + col;
        }

        // Read by BaseSlotCardUI while hovering cell 0's Improve button, to preview the cost of
        // the next tier before committing to UpgradeBase() — null once every tier's been bought.
        public BaseUpgradeTier PeekNextUpgradeTier(BuildingData building)
        {
            if (building == null || gameConfig == null || gameConfig.baseUpgradeTiers == null)
                return null;
            int tierIndex = building.Level - 1;
            if (tierIndex < 0 || tierIndex >= gameConfig.baseUpgradeTiers.Length)
                return null;
            return gameConfig.baseUpgradeTiers[tierIndex];
        }

        // Retained as gameplay support for a later Facility-upgrade pass, but the current UI does
        // not expose it. Keeping one canonical implementation avoids deleting already-authored
        // collection math merely because its button is intentionally disabled for now.
        public BaseUpgradeTier PeekNextFacilityUpgradeTier(FacilityData facility)
        {
            if (facility == null || gameConfig == null || gameConfig.facilityUpgradeTiers == null)
                return null;
            if (!facility.Abilities.Overlaps(UnitAbilities.CollectAbilities))
                return null;
            if (IsFacilityAtYieldCap(facility))
                return null;
            int tierIndex = facility.UpgradeLevel;
            if (tierIndex < 0 || tierIndex >= gameConfig.facilityUpgradeTiers.Length)
                return null;
            return gameConfig.facilityUpgradeTiers[tierIndex];
        }

        // Internal Facility Improve is intentionally disabled in the UI in this stabilization
        // pass. The method remains for compatibility with any old prefab/code references, but no
        // BaseSlotCardUI should advertise it as an available action.
        public bool CanImproveFacility(FacilityData facility) => false;

        // True once this building's total collection of the Facility's resource (its own
        // baked-in ability, if any, plus every placed Facility for that type — see
        // BuildingData.CollectedAmount) already meets or exceeds the hex's own effective yield.
        // Always false for a Facility with no Collect ability (nothing to cap).
        private bool IsFacilityAtYieldCap(FacilityData facility)
        {
            if (facility == null || _currentBuilding == null)
                return false;
            ResourceType? type = ResolveCollectResourceType(facility);
            if (!type.HasValue)
                return false;
            return _currentBuilding.CollectedAmount(type.Value) >= GetHexYield(type.Value);
        }

        private static ResourceType? ResolveCollectResourceType(FacilityData facility)
        {
            foreach (string ability in facility.Abilities)
            {
                int index = Array.IndexOf(UnitAbilities.CollectAbilities, ability);
                if (index >= 0)
                    return (ResourceType)index;
            }
            return null;
        }

        // Fails open (int.MaxValue, i.e. "never at cap") if the map reference is missing rather
        // than blocking Improve on a misconfigured scene — same defensive spirit as this file's
        // other gameConfig-null checks.
        private int GetHexYield(ResourceType type)
        {
            if (map == null)
                return int.MaxValue;
            map.TryGetTerrainAt(_currentBuilding.Hex, out TerrainTypeEntry entry);
            ResourceYields yield = HexResourceCalculator.GetEffectiveYield(entry, HexResourceBonusRegistry.GetBonus(_currentBuilding.Hex));
            return yield.Get(type);
        }

        // Called by BaseSlotCardUI's Improve button on cell 0 — spends the current tier's cost,
        // raises Level (unlocking the next Facility slot via UnlockedFacilitySlots) and
        // Defense/Resistance. Ownership is rechecked here even though the read-only UI hides the
        // button, because visibility is not an authorization boundary.
        public void UpgradeBase()
        {
            if (!CanManageCurrentBuilding || _currentBuilding == null
                || gameConfig == null || gameConfig.baseUpgradeTiers == null)
                return;

            int tierIndex = _currentBuilding.Level - 1;
            if (tierIndex < 0 || tierIndex >= gameConfig.baseUpgradeTiers.Length)
            {
                turnController?.ShowSpawnHint($"{_currentBuilding.Name} is already fully upgraded.");
                return;
            }

            BaseUpgradeTier tier = gameConfig.baseUpgradeTiers[tierIndex];
            PlayerRoot root = PlayerRootRegistry.FindFor(_currentBuilding.Owner);
            if (root == null || !root.CanSpendActionPoints(tier.apCost) || !tier.cost.CanAfford(root))
            {
                turnController?.ShowSpawnHint($"Not enough resources to upgrade {_currentBuilding.Name}.");
                return;
            }

            root.SpendActionPoints(tier.apCost);
            tier.cost.PayFrom(root);

            _currentBuilding.Level++;
            _currentBuilding.Defense += tier.defenseGain;
            _currentBuilding.Resistance += tier.resistanceGain;

            RefreshTitle();
            RefreshGrid();
            ShowBaseSummary();
        }

        // Called by BaseSlotCardUI's Repair button on cell 0. Same ownership guard as UpgradeBase.
        public void RepairBase()
        {
            if (!CanManageCurrentBuilding || _currentBuilding == null)
                return;
            _currentBuilding.StructurePointsCurrent = _currentBuilding.StructurePointsMax;
            ShowBaseSummary();
        }

        // Legacy/dormant Facility upgrade implementation. The current BaseSlotCardUI does not
        // expose this action, but the hard ownership guard stays here so old references cannot
        // mutate a foreign building from a read-only inspection session.
        public void ImproveFacility(int facilityIndex)
        {
            if (!CanManageCurrentBuilding || _currentBuilding == null
                || facilityIndex < 0 || facilityIndex >= _currentBuilding.TotalFacilitySlots)
                return;
            FacilityData facility = _currentBuilding.FacilitySlots[facilityIndex];
            if (facility == null)
                return;

            if (!facility.Abilities.Overlaps(UnitAbilities.CollectAbilities))
            {
                // No gameplay effect exists for these facilities yet. Do not manufacture a fake
                // level counter merely because an old call site reached this method.
                return;
            }

            if (IsFacilityAtYieldCap(facility))
            {
                turnController?.ShowSpawnHint($"{facility.Name} is already collecting this hex's full yield.");
                return;
            }

            if (gameConfig == null || gameConfig.facilityUpgradeTiers == null
                || facility.UpgradeLevel < 0 || facility.UpgradeLevel >= gameConfig.facilityUpgradeTiers.Length)
            {
                turnController?.ShowSpawnHint($"{facility.Name} is already fully upgraded.");
                return;
            }

            BaseUpgradeTier tier = gameConfig.facilityUpgradeTiers[facility.UpgradeLevel];
            PlayerRoot root = PlayerRootRegistry.FindFor(_currentBuilding.Owner);
            if (root == null || !root.CanSpendActionPoints(tier.apCost) || !tier.cost.CanAfford(root))
            {
                turnController?.ShowSpawnHint($"Not enough resources to upgrade {facility.Name}.");
                return;
            }

            root.SpendActionPoints(tier.apCost);
            tier.cost.PayFrom(root);
            facility.UpgradeLevel++;
            ShowFacilityDetail(facility);
        }
    }
}
