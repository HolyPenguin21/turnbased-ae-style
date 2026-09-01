using Game.Cards;
using Game.Core;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // One cell in BaseViewerModalUI's grid. For a citadel/card-built Base (building.
    // HasTieredUnlock), cell index 0 is always the Base itself and index 1..TotalFacilitySlots
    // is a Facility slot. A hero-built resource site has no such identity of its own (see
    // BuildingData.HasTieredUnlock) — there IS no base cell there at all, so index 0 is just
    // its first Facility slot (see BaseViewerModalUI.RefreshGrid, which skips the base cell
    // entirely for one of these). Either way, each Facility cell is in one of three states:
    // locked ("NOT AVAILABLE", non-interactive), empty (unlocked, droppable — see
    // BaseViewerModalUI.TryPlaceFacility), or filled with a placed FacilityData. Nothing here
    // is ever dragged back out or reordered ("place and forget"), so unlike ArmyUnitCardUI this
    // has no drag handlers at all — just click-for-detail and Base-only management controls.
    public class BaseSlotCardUI : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Image artImage;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private Button improveButton;
        [SerializeField] private Button repairButton;
        // Sits in the gap between the name (top) and the hover Improve/Repair buttons (bottom) —
        // shown there while hovering Improve on cell 0, one coloured circle badge per non-zero
        // cost component, same "icon + number" convention as ResourceBarUI/BuyDiceRowUI. Order:
        // AP, Human, Energy, Materials, Tech. The name stays visible throughout.
        [SerializeField] private GameObject costPreviewRoot;
        [SerializeField] private Image[] costBadgeIcons;
        [SerializeField] private TMP_Text[] costBadgeAmounts;
        // Same 5-slot layout as CardUI's own StatsRow (Card.prefab), cloned into this prefab for
        // visual consistency — see CardUI's own field comment for the fixed per-slot mapping.
        // Base cell only — a Facility cell has no Attack/Defense/HP stats of its own (those
        // belong to the building hosting it), so this stays hidden there:
        //   AttackBadge slot: Level      DefenseBadge slot: Defense
        //   HpBadge slot: Structure Points, REAL current/max
        //   MoveBadge slot: Resistance   RangeBadge slot: Fate
        [SerializeField] private GameObject statsRow;
        [SerializeField] private TMP_Text attackStatText;
        [SerializeField] private TMP_Text defenseStatText;
        [SerializeField] private TMP_Text hpStatText;
        [SerializeField] private TMP_Text moveStatText;
        [SerializeField] private TMP_Text rangeStatText;

        // Same faint-box convention as ArmyUnitCardUI's own empty-slot placeholder.
        private static readonly Color EmptySlotColor = new Color(1f, 1f, 1f, 0.12f);

        private BaseViewerModalUI _modal;
        private BuildingData _building;
        private FacilityData _facility;
        private bool _locked;
        private bool _occupied;
        private bool _isBaseCell;
        private string _defaultNameText;
        private bool _canImprove;

        private void Awake()
        {
            // A cost preview is now meaningful only for the Base/Citadel cell. Facility Improve
            // is intentionally not exposed until its gameplay contract is implemented.
            AddHoverTrigger(improveButton, ShowUpgradeCostPreview, HideUpgradeCostPreview);
        }

        public void Setup(BaseViewerModalUI modal, int cellIndex, BuildingData building)
        {
            _modal = modal;
            _building = building;
            _facility = null;

            // See the class comment — a resource site has no base cell at all, so cellIndex 0
            // means its first Facility slot there, not "the building itself".
            bool hasBaseCell = building.HasTieredUnlock;
            bool isBaseCell = hasBaseCell && cellIndex == 0;
            _isBaseCell = isBaseCell;
            int facilityIndex = hasBaseCell ? cellIndex - 1 : cellIndex;
            _locked = !isBaseCell && facilityIndex >= building.UnlockedFacilitySlots;
            if (!isBaseCell && !_locked)
                _facility = building.FacilitySlots[facilityIndex];
            _occupied = isBaseCell || _facility != null;

            if (statusText != null)
            {
                statusText.text = _locked ? "NOT AVAILABLE" : string.Empty;
                statusText.gameObject.SetActive(_locked);
            }
            GameConfig config = modal.GameConfig;
            string baseAbilities = config != null ? config.FormatAbilities(building.Abilities) : string.Join(" ", building.Abilities);
            string facilityAbilities = _facility != null ? (config != null ? config.FormatAbilities(_facility.Abilities) : string.Join(" ", _facility.Abilities)) : string.Empty;
            _defaultNameText = isBaseCell
                ? building.Name + (!string.IsNullOrEmpty(baseAbilities) ? "\n" + baseAbilities : string.Empty)
                : _facility != null ? _facility.Name + (!string.IsNullOrEmpty(facilityAbilities) ? "\n" + facilityAbilities : string.Empty) : string.Empty;
            if (nameText != null)
                nameText.text = _defaultNameText;
            if (artImage != null)
            {
                artImage.sprite = isBaseCell ? building.Art : _facility != null ? _facility.Art : null;
                artImage.color = _occupied ? Color.white : EmptySlotColor;
            }

            RefreshStatsRow(isBaseCell, building);

            if (improveButton != null)
                improveButton.gameObject.SetActive(false);
            if (repairButton != null)
                repairButton.gameObject.SetActive(false);

            // Only the Base/Citadel cell has a real Improve action in the current rules, and only
            // its owner may manage it. Internal Facility cards remain inspectable but actionless.
            _canImprove = isBaseCell && modal.CanManageCurrentBuilding;
            if (improveButton != null)
            {
                improveButton.onClick.RemoveAllListeners();
                if (_canImprove)
                    improveButton.onClick.AddListener(() => _modal.UpgradeBase());
            }
            if (repairButton != null)
            {
                repairButton.onClick.RemoveAllListeners();
                if (isBaseCell && modal.CanManageCurrentBuilding)
                    repairButton.onClick.AddListener(() => _modal.RepairBase());
            }
        }

        // See the field block's own comment for the fixed per-slot mapping. Base cell only —
        // hidden for a Facility cell (locked, empty, or occupied alike), which has no
        // Attack/Defense/HP identity of its own to show.
        private void RefreshStatsRow(bool isBaseCell, BuildingData building)
        {
            if (statsRow == null)
                return;

            statsRow.SetActive(isBaseCell);
            if (!isBaseCell)
                return;

            if (attackStatText != null) attackStatText.text = building.Level.ToString();
            if (defenseStatText != null) defenseStatText.text = building.Defense.ToString();
            if (hpStatText != null) hpStatText.text = $"{building.StructurePointsCurrent}/{building.StructurePointsMax}";
            if (moveStatText != null) moveStatText.text = building.Resistance.ToString();
            if (rangeStatText != null) rangeStatText.text = building.Fate.ToString();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_locked || !_occupied)
                return;
            if (_isBaseCell)
                _modal?.ShowBaseSummary();
            else
                _modal?.ShowFacilityDetail(_facility);
        }

        // Repair only ever shows on an owned Base's own cell, and only while it's actually short
        // of full Structure Points. Read-only enemy views expose no management controls.
        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_locked || !_occupied)
                return;
            if (repairButton != null)
                repairButton.gameObject.SetActive(_isBaseCell && _modal != null && _modal.CanManageCurrentBuilding
                    && _building.StructurePointsCurrent < _building.StructurePointsMax);
            if (improveButton != null)
                improveButton.gameObject.SetActive(_canImprove);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (repairButton != null)
                repairButton.gameObject.SetActive(false);
            if (improveButton != null)
                improveButton.gameObject.SetActive(false);
            HideUpgradeCostPreview();
        }

        // Base/Citadel upgrade cost only. Facility Improve is deliberately disabled.
        private void ShowUpgradeCostPreview()
        {
            if (_modal == null || costPreviewRoot == null || !_isBaseCell || !_modal.CanManageCurrentBuilding)
                return;
            BaseUpgradeTier tier = _modal.PeekNextUpgradeTier(_building);
            if (tier == null)
                return;

            costPreviewRoot.SetActive(true);
            SetBadge(0, tier.apCost);
            SetBadge(1, tier.cost.human);
            SetBadge(2, tier.cost.energy);
            SetBadge(3, tier.cost.materials);
            SetBadge(4, tier.cost.tech);
        }

        private void SetBadge(int index, int amount)
        {
            if (costBadgeIcons == null || index >= costBadgeIcons.Length || costBadgeIcons[index] == null)
                return;
            bool visible = amount > 0;
            costBadgeIcons[index].gameObject.SetActive(visible);
            if (visible && costBadgeAmounts != null && index < costBadgeAmounts.Length && costBadgeAmounts[index] != null)
                costBadgeAmounts[index].text = amount.ToString();
        }

        private void HideUpgradeCostPreview()
        {
            if (costPreviewRoot != null)
                costPreviewRoot.SetActive(false);
        }

        private static void AddHoverTrigger(Button button, System.Action onEnter, System.Action onExit)
        {
            if (button == null)
                return;
            EventTrigger trigger = button.gameObject.AddComponent<EventTrigger>();
            trigger.triggers.Add(MakeHoverEntry(EventTriggerType.PointerEnter, onEnter));
            trigger.triggers.Add(MakeHoverEntry(EventTriggerType.PointerExit, onExit));
        }

        private static EventTrigger.Entry MakeHoverEntry(EventTriggerType type, System.Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            return entry;
        }
    }
}
