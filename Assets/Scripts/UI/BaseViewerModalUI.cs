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
    // Presentation for a Base and its Facility slots. The real BuildingData remains the
    // gameplay source of truth; changes must be published after successful mutation.
    public class BaseViewerModalUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private Button closeButton;
        [SerializeField] private Transform gridContainer;
        [SerializeField] private Image detailArt;
        [SerializeField] private TMP_Text detailText1;
        [SerializeField] private TMP_Text detailText2;
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private GameTurnController turnController;
        [SerializeField] private HexMap map;

        public GameConfig GameConfig => gameConfig;
        private readonly List<BaseSlotCardUI> _cards = new List<BaseSlotCardUI>();
        private BuildingData _currentBuilding;
        private Canvas _canvas;
        private bool _readOnly;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
        public bool IsReadOnly => _readOnly;
        public bool CanManageCurrentBuilding => !_readOnly && _currentBuilding != null
            && (turnController == null || turnController.CurrentPlayer == null
                || turnController.CurrentPlayer == _currentBuilding.Owner);
        public event Action Closed;
        public event Action VisibilityChanged;
        public BuildingData CurrentBuilding => _currentBuilding;

        public void RefreshAfterExternalFacilityPlacement()
        {
            if (!IsShowing || _currentBuilding == null)
                return;
            RefreshGrid();
            ShowBaseSummary();
        }

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
            titleText.text = _currentBuilding.HasTieredUnlock
                ? $"{_currentBuilding.Name} — <b>Level {_currentBuilding.Level}</b>"
                : _currentBuilding.Name;
        }

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

        public void ShowBaseSummary()
        {
            if (detailArt != null)
            {
                detailArt.sprite = _currentBuilding != null ? _currentBuilding.DetailArt : null;
                detailArt.gameObject.SetActive(_currentBuilding != null && _currentBuilding.DetailArt != null);
            }
            if (_currentBuilding == null)
                return;
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

        private string FormatAbilities(IEnumerable<string> abilities)
        {
            return gameConfig != null ? gameConfig.FormatAbilitiesDetailed(abilities) : string.Join(" ", abilities);
        }

        public BaseUpgradeTier PeekNextUpgradeTier(BuildingData building)
        {
            if (building == null || gameConfig == null || gameConfig.baseUpgradeTiers == null)
                return null;
            int tierIndex = building.Level - 1;
            if (tierIndex < 0 || tierIndex >= gameConfig.baseUpgradeTiers.Length)
                return null;
            return gameConfig.baseUpgradeTiers[tierIndex];
        }

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

        // UI deliberately leaves this dormant. A legacy direct invocation still uses the
        // normal owner guard and, on success, publishes its actual income change.
        public bool CanImproveFacility(FacilityData facility) => false;

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

        private int GetHexYield(ResourceType type)
        {
            if (map == null)
                return int.MaxValue;
            map.TryGetTerrainAt(_currentBuilding.Hex, out TerrainTypeEntry entry);
            ResourceYields yield = HexResourceCalculator.GetEffectiveYield(entry, HexResourceBonusRegistry.GetBonus(_currentBuilding.Hex));
            return yield.Get(type);
        }

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

            // Upgrade changes UnlockedFacilitySlots/FreeFacilitySlots while the hex and
            // its owner's vision remain unchanged. Publish the completed building state.
            VisionSystem.NotifyContentChanged(_currentBuilding.Hex);
            RefreshTitle();
            RefreshGrid();
            ShowBaseSummary();
        }

        public void RepairBase()
        {
            if (!CanManageCurrentBuilding || _currentBuilding == null)
                return;
            _currentBuilding.StructurePointsCurrent = _currentBuilding.StructurePointsMax;
            ShowBaseSummary();
        }

        public void ImproveFacility(int facilityIndex)
        {
            if (!CanManageCurrentBuilding || _currentBuilding == null
                || facilityIndex < 0 || facilityIndex >= _currentBuilding.TotalFacilitySlots)
                return;
            FacilityData facility = _currentBuilding.FacilitySlots[facilityIndex];
            if (facility == null)
                return;
            if (!facility.Abilities.Overlaps(UnitAbilities.CollectAbilities))
                return;
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
            VisionSystem.NotifyContentChanged(_currentBuilding.Hex);
            ShowFacilityDetail(facility);
        }
    }
}