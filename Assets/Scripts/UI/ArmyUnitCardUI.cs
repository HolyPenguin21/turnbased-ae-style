using System.Collections;
using Game.Cards;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // One unit's card inside ArmyViewerModalUI's grid — shows its real per-unit art (see
    // UnitData.Art) and can be dragged onto one of the modal's army buttons to move it there,
    // or clicked to show its full stats in the modal's detail panel. All drop resolution (which
    // button it landed on, capacity check, actually moving the unit between ArmyData.Members
    // lists) lives on ArmyViewerModalUI — same CardUI/CardHandUI-style split, just reporting
    // to a different owner. Not a CardUI (that component is hard-wired to CardHandUI's
    // hand-reorder/play-onto-hex logic, which doesn't fit "drag between armies").
    //
    // Positioning is entirely manual (see SetSlot) rather than left to the grid container's
    // GridLayoutGroup — the prefab's own LayoutElement is permanently ignoreLayout, so that
    // component is only ever read by ArmyViewerModalUI for its cell metrics (cellSize/spacing/
    // constraintCount), never as the thing actually arranging children. A GridLayoutGroup
    // arranges only its non-ignored children — if the DRAGGED card alone were ignoreLayout
    // (the earlier design), every OTHER card would instantly compact into the gap the moment a
    // drag started, with no way to show one sliding smoothly from its old slot to a new one —
    // exactly the "no clear sense of where it'll land" complaint this replaced.
    public class ArmyUnitCardUI : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler,
        IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private RectTransform rectTransform;
        [SerializeField] private Image artImage;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text moveText;
        [SerializeField] private GameObject commandBadgeRoot;
        [SerializeField] private TMP_Text commandBadgeText;
        // Same 5-slot layout as CardUI's own StatsRow (Card.prefab), cloned into this prefab for
        // visual consistency — see CardUI's own field comment for the fixed per-slot mapping.
        // Only Unit/Hero ever land here (a unit has no map presence without being in an army —
        // see UnitData — so there's no Base/Facility case to handle, unlike CardUI):
        //   AttackBadge slot: Attack (Unit) / Command Rating (Hero)
        //   DefenseBadge slot: Defense (Unit) / Fate (Hero)
        //   HpBadge slot: HP, REAL current/max (unlike CardUI's hand cards, this unit actually
        //   exists on the map, so this reflects real damage taken)
        //   MoveBadge slot: Move max (Unit and Hero alike)
        //   RangeBadge slot: Range (Unit) / Initiative (Hero)
        [SerializeField] private GameObject statsRow;
        [SerializeField] private TMP_Text attackStatText;
        [SerializeField] private TMP_Text defenseStatText;
        [SerializeField] private TMP_Text hpStatText;
        [SerializeField] private TMP_Text moveStatText;
        [SerializeField] private TMP_Text rangeStatText;
        [SerializeField] private float slotAnimDuration = 0.12f;
        // Hover-revealed Repair button + cost strip — same shape as BaseSlotCardUI's own
        // repairButton/costPreviewRoot (Improve there, Repair here; see UnitRepair for the cost
        // math). Order: AP, Human, Energy, Materials, Tech.
        [SerializeField] private Button repairButton;
        [SerializeField] private GameObject costPreviewRoot;
        [SerializeField] private Image[] costBadgeIcons;
        [SerializeField] private TMP_Text[] costBadgeAmounts;

        public UnitData Unit { get; private set; }
        public bool IsDragging { get; private set; }

        private ArmyViewerModalUI _modal;
        private Vector2 _homeSlot;
        private Canvas _canvas;
        private Coroutine _slotAnim;

        // How far above every other card in the grid this one renders while being dragged — the
        // prefab's own nested Canvas (see CardUI.DragSortingOrder, same technique) breaks out of
        // the grid's shared batch for the duration of the drag so it's never drawn underneath
        // whichever cards it's currently passing over.
        private const int DragSortingOrder = 1000;

        private void Awake()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
        }

        // Faint translucent box (no sprite, just this tint) — Image with no sprite still
        // renders as a plain colour rect, so an empty capacity slot reads as "a droppable spot
        // is here" instead of just being invisible dead space in the grid.
        private static readonly Color EmptySlotColor = new Color(1f, 1f, 1f, 0.12f);

        public void Setup(ArmyViewerModalUI modal, UnitData unit)
        {
            _modal = modal;
            Unit = unit;

            if (artImage != null)
            {
                artImage.sprite = unit != null ? unit.Art : null;
                artImage.color = unit != null ? Color.white : EmptySlotColor;
            }
            if (nameText != null)
                nameText.text = unit != null ? unit.Name : string.Empty;
            if (moveText != null)
            {
                // Abilities only now — Move used to be prefixed onto this same line, but now
                // has its own slot in statsRow below (see RefreshStatsRow), so showing it here
                // too would just be the same number twice. Abbreviated per GameConfig.
                // abilityAbbreviations (see GameConfig.FormatAbilities) — read via the modal
                // reference this card already keeps rather than needing its own config field.
                string formatted = unit != null ? _modal?.GameConfig?.FormatAbilities(unit.Abilities) : null;
                moveText.text = formatted ?? string.Empty;
            }

            RefreshStatsRow(unit);

            // Command Rating is already shown in the detail panel (ShowUnitDetail) once the
            // card's clicked — repeating it as a badge on every hero card in the grid was just
            // noise here.
            if (commandBadgeRoot != null)
                commandBadgeRoot.SetActive(false);

            // Hover-revealed (see OnPointerEnter/OnPointerExit) — hidden by default until then.
            if (repairButton != null)
            {
                repairButton.gameObject.SetActive(false);
                repairButton.onClick.RemoveAllListeners();
                if (unit != null)
                    repairButton.onClick.AddListener(() => _modal.RepairUnit(unit));
            }
            if (costPreviewRoot != null)
                costPreviewRoot.SetActive(false);
        }

        // See the field block's own comment for the fixed per-slot mapping. Hidden entirely for
        // an empty capacity slot (unit == null) — nothing to show stats for there.
        private void RefreshStatsRow(UnitData unit)
        {
            if (statsRow == null)
                return;

            statsRow.SetActive(unit != null);
            if (unit == null)
                return;

            int slot1, slot2, slot5;
            if (unit.IsHero)
            {
                slot1 = unit.CommandRating;
                // Always the hero's max, not however much is currently unspent — this compact
                // badge is "how much Fate this hero has to work with", not a live spend tracker
                // (see BattleAttackPopupUI/BattleCombatantRowUI for the actual current value).
                slot2 = unit.FateMax;
                slot5 = unit.Initiative;
            }
            else
            {
                slot1 = unit.Attack;
                slot2 = unit.Defense;
                slot5 = unit.Range;
            }

            if (attackStatText != null) attackStatText.text = slot1.ToString();
            if (defenseStatText != null) defenseStatText.text = slot2.ToString();
            if (hpStatText != null) hpStatText.text = $"{unit.HitPointsCurrent}/{unit.HitPointsMax}";
            if (moveStatText != null) moveStatText.text = unit.MoveMax.ToString();
            if (rangeStatText != null) rangeStatText.text = slot5.ToString();
        }

        // Called by ArmyViewerModalUI right after Setup (initial layout) and again, live,
        // during a drag for every OTHER card as the preview order changes (see
        // ArmyViewerModalUI.RepositionNonDraggedCards) — this card's own slot while IT is the
        // one being dragged is left alone (driven by OnDrag instead) and never receives a call
        // here for the duration.
        public void SetSlot(Vector2 anchoredPosition, bool animated)
        {
            bool changed = (anchoredPosition - _homeSlot).sqrMagnitude > 0.01f;
            _homeSlot = anchoredPosition;
            if (IsDragging || !changed)
                return;

            if (_slotAnim != null)
                StopCoroutine(_slotAnim);
            if (!animated)
            {
                rectTransform.anchoredPosition = anchoredPosition;
                return;
            }
            _slotAnim = StartCoroutine(AnimateSlot(anchoredPosition));
        }

        private IEnumerator AnimateSlot(Vector2 target)
        {
            Vector2 start = rectTransform.anchoredPosition;
            float t = 0f;
            while (t < slotAnimDuration)
            {
                t += Time.deltaTime;
                rectTransform.anchoredPosition = Vector2.Lerp(start, target, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / slotAnimDuration)));
                yield return null;
            }
            rectTransform.anchoredPosition = target;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            _modal?.ShowUnitDetail(Unit);
        }

        // Repair only ever shows while the unit is actually wounded AND its army is standing on
        // the player's own Base (see ArmyViewerModalUI.CanRepairUnit/UnitRepair.CanRepairAt) —
        // same "hover-only, condition re-checked live" shape as BaseSlotCardUI's own Repair.
        public void OnPointerEnter(PointerEventData eventData)
        {
            bool show = Unit != null && _modal != null && _modal.CanRepairUnit(Unit);
            repairButton?.gameObject.SetActive(show);
            if (show)
                ShowRepairCostPreview();
            else
                HideRepairCostPreview();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            repairButton?.gameObject.SetActive(false);
            HideRepairCostPreview();
        }

        // One coloured circle badge per non-zero cost component, AP first — same convention as
        // BaseSlotCardUI's own ShowUpgradeCostPreview/SetBadge.
        private void ShowRepairCostPreview()
        {
            if (costPreviewRoot == null || Unit == null)
                return;
            costPreviewRoot.SetActive(true);
            ResourceCost cost = UnitRepair.ResourceCost(Unit);
            SetBadge(0, UnitRepair.ApCost(Unit));
            SetBadge(1, cost.human);
            SetBadge(2, cost.energy);
            SetBadge(3, cost.materials);
            SetBadge(4, cost.tech);
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

        private void HideRepairCostPreview()
        {
            if (costPreviewRoot != null)
                costPreviewRoot.SetActive(false);
        }

        // An empty slot has nothing to drag — guarded here rather than left to TryDropUnit's
        // own null check so an empty placeholder never even visually follows the pointer. A
        // read-only modal (someone else's army — see ArmyViewerModalUI.ShowReadOnly) refuses to
        // even start a drag, so neither a reorder nor a move-into-another-army can ever begin;
        // OnPointerClick's detail view stays available regardless.
        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Unit == null || (_modal != null && _modal.IsReadOnly))
                return;
            IsDragging = true;
            if (_slotAnim != null)
            {
                StopCoroutine(_slotAnim);
                _slotAnim = null;
            }
            if (_canvas != null)
            {
                _canvas.overrideSorting = true;
                _canvas.sortingOrder = DragSortingOrder;
            }
            _modal?.BeginReorderPreview(this);
        }

        public void OnDrag(PointerEventData eventData)
        {
            // IsDragging (not just Unit == null) — OnBeginDrag refuses to even start a drag on
            // a read-only modal (see its own comment), but EventSystem still calls this every
            // frame regardless of whether OnBeginDrag actually did anything; without this check
            // the card would visually follow the pointer anyway despite never having "begun".
            if (!IsDragging)
                return;
            UIDragUtility.ApplyScreenDelta(rectTransform, eventData, _canvas);
            _modal?.PreviewReorder(this, eventData.position);
        }

        // The grid is rebuilt (RefreshGrid) the moment a drop actually succeeds, which destroys
        // this card along with every other one currently shown — so a successful drop needs no
        // "snap into its new slot" case here, only the failure path (snap back to where it was
        // picked up from) ever actually runs this card's own code afterward.
        public void OnEndDrag(PointerEventData eventData)
        {
            if (!IsDragging)
                return;
            bool moved = _modal != null && _modal.TryDropUnit(this, eventData.position);
            IsDragging = false;
            if (_canvas != null)
                _canvas.overrideSorting = false;
            if (!moved)
                rectTransform.anchoredPosition = _homeSlot;
        }
    }
}
