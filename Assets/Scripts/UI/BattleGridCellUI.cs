using System.Collections;
using Game.Styles;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // One cell of BattleScreenUI's grid — empty, or showing whichever unit currently occupies
    // it. Same 5-slot StatsRow pattern as CardUI/ArmyUnitCardUI/BaseSlotCardUI (see the field
    // block's own comment) rather than reusing ArmyUnitCardUI directly — that component is hard-
    // wired to ArmyViewerModalUI (its click/drag calls straight into `_modal`), which doesn't
    // exist in this context.
    //
    // During the Arrangement phase only (see draggable/Setup), a cell showing one of the local
    // player's own units can be dragged onto another cell within that same player's own two
    // rows to swap places. Unlike ArmyUnitCardUI's capacity grid, this grid's cells stay fixed
    // in their GridLayoutGroup slots — dragging only shows a small floating "ghost" icon that
    // follows the pointer (see OnBeginDrag); the actual swap happens in the BattleGrid model on
    // drop (BattleScreenUI.TryDropOnCell), then the whole grid is rebuilt from scratch the same
    // way RefreshTurnOrder's icon strip always has been, rather than animating cards into place.
    // The Round phase's own move action instead moves the real card itself (see AnimateMoveTo,
    // called by BattleScreenUI.AnimateThenMove) — a quick slide from source to destination cell
    // before the grid rebuild, rather than an instant teleport.
    public class BattleGridCellUI : MonoBehaviour, IPointerClickHandler, IPointerExitHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private RectTransform rectTransform;
        [SerializeField] private Image artImage;
        [SerializeField] private TMP_Text nameText;
        // Press-and-hold to preview the attached Equipment card's art (see UnitData.Equipment /
        // EquipmentArtToggle). Hidden by that component when nothing's attached. Optional.
        [SerializeField] private EquipmentArtToggle equipmentArtToggle;
        // Same faint tint everywhere — occupied, empty, or the shared neutral row alike (see
        // the user's own spec: alpha 15/255, no special-casing) — a unit's own art/name/stats
        // are what actually distinguish an occupied cell, not the background.
        [SerializeField] private Image background;
        private static readonly Color CellColor = new Color(1f, 1f, 1f, 15f / 255f);
        // Legal-target hints for the current human unit's turn (see BattleScreenUI.RefreshGrid)
        // — a bit more visible than the baseline tint so they read as a hint, not just noise.
        private static readonly Color LegalMoveColor = new Color(0.25f, 1f, 0.35f, 40f / 255f);
        private static readonly Color LegalAttackColor = new Color(1f, 0.2f, 0.2f, 40f / 255f);
        // Shown on every enemy cell the current unit could attack right now (see the user's own
        // spec) — on top of the red background tint above, not instead of it.
        [SerializeField] private GameObject attackIcon;

        // Same fixed per-slot mapping as CardUI's own StatsRow:
        //   AttackBadge slot: Attack (Unit) / Command Rating (Hero)
        //   DefenseBadge slot: Defense (Unit) / Fate (Hero)
        //   HpBadge slot: HP, current/max (real values — this unit actually exists on the grid)
        //   MoveBadge slot: Move max (Unit and Hero alike)
        //   RangeBadge slot: Range (Unit) / Initiative (Hero)
        [SerializeField] private GameObject statsRow;
        [SerializeField] private TMP_Text attackStatText;
        [SerializeField] private TMP_Text defenseStatText;
        [SerializeField] private TMP_Text hpStatText;
        [SerializeField] private TMP_Text moveStatText;
        [SerializeField] private TMP_Text rangeStatText;

        // The fixed size GridLayoutGroup assigns every cell (see gridContainer's own
        // GridLayoutGroup.cellSize) — used as the acting-unit highlight's "true size" rather
        // than reading rectTransform.rect.size live, since that reflects the layout group's
        // last completed pass, not necessarily this frame's (Setup runs synchronously right
        // after Instantiate, before layout rebuilds).
        [SerializeField] private Vector2 cellSize = new Vector2(92f, 134f);
        // Shown for whichever unit is currently up in the turn order (see
        // BattleScreenUI.RefreshTurnOrder) — a UI-space ragged glow ring, same technique as the
        // map's own hex selection highlight (see UIRaggedGlowUI's own comment).
        [SerializeField] private UIRaggedGlowUI actingHighlight;

        // How far above every other cell this one's drag-ghost renders — same idea/value as
        // ArmyUnitCardUI.DragSortingOrder, just applied to a standalone ghost object instead of
        // this cell's own (never-moving) Canvas.
        private const int DragSortingOrder = 1000;
        private const float GhostAlpha = 0.6f;

        public UnitData Unit { get; private set; }
        public int Row { get; private set; }
        public int Col { get; private set; }
        public bool IsDragging { get; private set; }
        public RectTransform RectTransform => rectTransform;

        private BattleScreenUI _screen;
        private bool _draggable;
        private Canvas _rootCanvas;
        private RectTransform _ghost;

        private void Awake()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
        }

        public void Setup(BattleScreenUI screen, UnitData unit, int row, int col, bool draggable,
            bool isActingUnit = false, bool isLegalMoveTarget = false, bool isLegalAttackTarget = false)
        {
            _screen = screen;
            Unit = unit;
            Row = row;
            Col = col;
            _draggable = draggable;

            if (artImage != null)
            {
                artImage.sprite = unit != null ? unit.Art : null;
                artImage.gameObject.SetActive(unit != null);
            }
            equipmentArtToggle?.Configure(unit?.Equipment, _screen != null ? _screen.GameConfig : null);
            if (nameText != null)
                nameText.text = unit != null ? unit.Name : string.Empty;
            if (background != null)
                background.color = isLegalAttackTarget ? LegalAttackColor : isLegalMoveTarget ? LegalMoveColor : CellColor;
            if (attackIcon != null)
                attackIcon.SetActive(isLegalAttackTarget);

            RefreshStatsRow(unit);
            RefreshActingHighlight(isActingUnit);
        }

        private void RefreshActingHighlight(bool isActingUnit)
        {
            if (actingHighlight == null)
                return;
            if (!isActingUnit)
            {
                actingHighlight.Hide();
                return;
            }
            actingHighlight.ApplyStyle(_screen != null ? _screen.ActingHighlightStyle : null);
            actingHighlight.SetColor(TechnicalColors.BattleActingUnit);
            actingHighlight.ShowAt(cellSize);
        }

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
                // Terrain + Base-building defense bonus, folded in so this badge always matches
                // what the unit would actually roll with right now (see
                // BattleScreenUI.GetDisplayedDefenseBonus's own comment).
                slot2 = unit.Defense + (_screen != null ? _screen.GetDisplayedDefenseBonus(unit) : 0);
                slot5 = unit.Range;
            }

            if (attackStatText != null) attackStatText.text = slot1.ToString();
            if (defenseStatText != null) defenseStatText.text = slot2.ToString();
            if (hpStatText != null) hpStatText.text = $"{unit.HitPointsCurrent}/{unit.HitPointsMax}";
            if (moveStatText != null) moveStatText.text = unit.MoveMax.ToString();
            if (rangeStatText != null) rangeStatText.text = slot5.ToString();
        }

        // Same split as the strategic map (HexSelectionController): left click only ever
        // inspects, right click is the one that acts. Right click reaches OnCellClicked
        // regardless of whether Unit is null (an empty cell can be a legal move destination) —
        // BattleScreenUI itself decides whether it amounts to a legal move/attack for whoever's
        // current; anything else is a no-op, same as clicking an irrelevant cell always having
        // been harmless.
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                _screen?.OnCellClicked(this);
                return;
            }
            if (Unit != null)
                _screen?.ShowUnitDetail(Unit);
        }

        // Restore the portrait if the pointer leaves the cell mid press-and-hold of the
        // equipment-art toggle (per the spec — reverts on leaving the card, not only on release).
        public void OnPointerExit(PointerEventData eventData) => equipmentArtToggle?.Revert();

        // Shared by the drag-ghost (OnBeginDrag, follows the pointer) and the move-animation
        // ghost (AnimateMoveTo, slides to a fixed destination) — same floating, half-transparent,
        // always-on-top Image, just driven differently afterward.
        private RectTransform CreateGhost()
        {
            if (artImage == null)
                return null;
            _rootCanvas = GetComponentInParent<Canvas>()?.rootCanvas;
            if (_rootCanvas == null)
                return null;

            GameObject ghostObj = new GameObject("Ghost", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform ghost = (RectTransform)ghostObj.transform;
            ghost.SetParent(_rootCanvas.transform, worldPositionStays: false);
            ghost.sizeDelta = rectTransform.rect.size;

            Image ghostImage = ghostObj.GetComponent<Image>();
            ghostImage.sprite = artImage.sprite;
            ghostImage.color = new Color(1f, 1f, 1f, GhostAlpha);
            ghostImage.raycastTarget = false;

            Canvas ghostCanvas = ghostObj.AddComponent<Canvas>();
            ghostCanvas.overrideSorting = true;
            ghostCanvas.sortingOrder = DragSortingOrder;

            return ghost;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_draggable || Unit == null)
                return;
            _ghost = CreateGhost();
            if (_ghost == null)
                return;
            IsDragging = true;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)_rootCanvas.transform, eventData.position, eventData.pressEventCamera, out Vector2 localPoint);
            _ghost.anchoredPosition = localPoint;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!IsDragging || _ghost == null)
                return;
            UIDragUtility.ApplyScreenDelta(_ghost, eventData, _rootCanvas);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!IsDragging)
                return;
            IsDragging = false;
            if (_ghost != null)
                Destroy(_ghost.gameObject);
            _ghost = null;
            _screen?.TryDropOnCell(this, eventData.position);
        }

        // A quick slide from this cell's own position to `destination` — used for the Round
        // phase's move action (see BattleScreenUI.AnimateThenMove), called BEFORE the actual
        // BattleGrid.Swap/RefreshGrid, purely a visual cue. Moves this cell's own RectTransform
        // (full card — art, name, stats, everything — at full opacity), not a translucent ghost
        // clone: a clone at a different pivot was what caused an earlier version's ~50%-of-cell
        // vertical offset (a freshly created GameObject defaults to a centre pivot, which never
        // matched this cell's own top-centre one). Stays parented to gridContainer the whole
        // time — reparenting out (an even earlier version's approach) removed it from the
        // GridLayoutGroup's child list, which reflowed every OTHER cell into the gap for the
        // animation's duration (the "neighbours jump" bug). The caller disables that
        // GridLayoutGroup for the duration instead (see AnimateThenMove), so this can freely
        // move the RectTransform without the layout fighting it or any sibling reflowing; the
        // object is destroyed a moment later anyway by RefreshGrid's own full rebuild.
        public IEnumerator AnimateMoveTo(RectTransform destination, float duration)
        {
            if (Unit == null || destination == null)
                yield break;

            Vector3 startPos = rectTransform.position;
            Vector3 endPos = destination.position;
            rectTransform.SetAsLastSibling();

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                rectTransform.position = Vector3.Lerp(startPos, endPos, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration)));
                yield return null;
            }
            rectTransform.position = endPos;
        }
    }
}
