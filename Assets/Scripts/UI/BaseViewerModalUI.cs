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
            if (Game.UI.UIFocusUtility.IsGameplayInputBlocked) return;
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
                ? $"{_currentBuilding.Name} — Level {_currentBuilding.Level}"
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
                Game.Audio.SceneUIAudioBinder.BindCreatedRoot(card);
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
                detailText1.text = $"{facility.Name}\n" +
                    $"Level {facility.Level}\n" +
                    $"Structure Points: {facility.StructurePointsCurrent}/{facility.StructurePointsMax}\n" +
                    $"Defense: {facility.Defense}\n" +
                    $"Resistance: {facility.Resistance}\n" +
                    $"Fate: {facility.Fate}";
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
            // One gameplay primitive owns the legality, the payment and the Level/Defense/Resistance
            // mutation (the AI capacity step uses the same one); the UI only reports and refreshes.
            BaseUpgradeOutcome outcome = InfrastructureActions.TryUpgradeBase(
                _currentBuilding, gameConfig.baseUpgradeTiers);
            if (!outcome.Ok)
            {
                turnController?.ShowSpawnHint($"Not enough resources to upgrade {_currentBuilding.Name}.");
                return;
            }
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
    }
}