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
    // slot + Defense/Resistance) and improving/repairing either the Base or a placed Facility
    // both happen via a small hover-revealed button pair on the relevant cell (see
    // BaseSlotCardUI's Improve/Repair buttons) rather than a title-bar context button.
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
        // improve-cap check (see IsFacilityAtYieldCap) — nothing else here touches the map.
        [SerializeField] private HexMap map;

        // Read by BaseSlotCardUI (via the modal reference it already keeps) for GameConfig.
        // FormatAbilities — see ShowBaseSummary/ShowFacilityDetail for this modal's own use.
        public GameConfig GameConfig => gameConfig;

        private readonly List<BaseSlotCardUI> _cards = new List<BaseSlotCardUI>();
        private BuildingData _currentBuilding;
        private Canvas _canvas;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Same purpose as ArmyViewerModalUI.Closed — HexSelectionController re-runs SelectHex
        // on close so an Upgrade purchased in here (new Facility slot, higher Defense/
        // Resistance) shows up on the hex-side info panel immediately.
        public event Action Closed;

        // Lets GameTurnController react to this modal opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked).
        public event Action VisibilityChanged;

        // Read by CardHandUI to know which building a dropped Facility card should join (see
        // TryDeployIntoBaseModal) — mirrors ArmyViewerModalUI.CurrentArmy.
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
            if (panelRoot != null)
                panelRoot.SetActive(true);
            _currentBuilding = building;
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

            // Level is meaningless for a non-tiered building (see BuildingData.HasTieredUnlock)
            // — a resource site never upgrades, so showing "Level 1" forever would just be noise.
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
                detailText1.text = $"{facility.Name}\nUpgrade Level: {facility.UpgradeLevel}";
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
        // TryDeployIntoBaseModal) — screenPosition is the drop's raw screen coordinates, same
        // convention as ArmyViewerModalUI.TryDropUnit. For a citadel/card-built Base, cell 0
        // (the Base itself) is never a valid target; a resource site has no such cell at all
        // (see RefreshGrid), so every cell there is a Facility slot. A locked or already-filled
        // slot silently rejects the drop (card snaps back to hand — CardHandUI's own failure
        // path).
        public bool TryPlaceFacility(CardDefinition definition, Vector2 screenPosition)
        {
            if (_currentBuilding == null || definition == null)
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

        // Same idea as PeekNextUpgradeTier, but for a placed Facility's own Improve button (see
        // ImproveFacility) — null for a Facility with no Collect ability at all (those Improve
        // for free, nothing to preview), once every tier's been bought, or once it's already
        // collecting the hex's full yield for its resource (see IsFacilityAtYieldCap — an extra
        // tier there would raise a number nothing ever reads, since collection is capped at the
        // hex's own yield either way).
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

        // Read by BaseSlotCardUI to decide whether a Facility cell's Improve button should show
        // at all — true for a cosmetic-only Facility (free no-op improve, out of scope to hide)
        // or a Collect-tagged one still short of the hex's yield; false once it's already at cap
        // (see IsFacilityAtYieldCap) — improving further would raise a number the actual
        // collection math (GameTurnController.CollectResourceIncome) already clamps away.
        public bool CanImproveFacility(FacilityData facility)
        {
            return facility != null && !IsFacilityAtYieldCap(facility);
        }

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
        // Defense/Resistance. Blocked + hinted, same pattern as ArmyViewerModalUI.CreateArmy,
        // once every tier in gameConfig.baseUpgradeTiers has been bought (Level - 1 == tier
        // count) or if unaffordable.
        public void UpgradeBase()
        {
            if (_currentBuilding == null || gameConfig == null || gameConfig.baseUpgradeTiers == null)
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

        // Called by BaseSlotCardUI's Repair button on cell 0 — only ever enabled when Structure
        // Points aren't already full (see BaseSlotCardUI.OnPointerEnter); nothing can currently
        // damage a building, so this exists for correctness rather than because it's reachable
        // today.
        public void RepairBase()
        {
            if (_currentBuilding == null)
                return;
            _currentBuilding.StructurePointsCurrent = _currentBuilding.StructurePointsMax;
            ShowBaseSummary();
        }

        // Called by a Facility cell's Improve button. A resource-collecting Facility (see
        // UnitAbilities.CollectAbilities) has a real paid upgrade — each tier raises its
        // per-turn contribution by 1 (see GameTurnController's collection math) — everything
        // else (Research Facility/Lab and any other cosmetic-only Facility) keeps the original
        // free no-op stub, since their behavior is out of scope this pass.
        public void ImproveFacility(int facilityIndex)
        {
            if (_currentBuilding == null || facilityIndex < 0 || facilityIndex >= _currentBuilding.TotalFacilitySlots)
                return;
            FacilityData facility = _currentBuilding.FacilitySlots[facilityIndex];
            if (facility == null)
                return;

            if (!facility.Abilities.Overlaps(UnitAbilities.CollectAbilities))
            {
                facility.UpgradeLevel++;
                ShowFacilityDetail(facility);
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
