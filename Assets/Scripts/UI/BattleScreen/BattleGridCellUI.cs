using System.Collections;
using System.Collections.Generic;
using Game.Cards;
using Game.Core;
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
    // drop (BattleScreenUI.TryDropOnCell), then the existing cells display their new occupants.
    // A move during a round temporarily slides the source card before the model move commits.
    public class BattleGridCellUI : MonoBehaviour, IPointerClickHandler, IPointerExitHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private RectTransform rectTransform;
        [SerializeField] private Image artImage;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text skillsText;
        // Press-and-hold to preview the attached Equipment card's art (see UnitData.Equipment /
        // EquipmentArtToggle). Hidden by that component when nothing's attached. Optional.
        [SerializeField] private EquipmentArtToggle equipmentArtToggle;
        [SerializeField] private EquipmentArtToggle mutatorArtToggle;
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

        // Shown for whichever unit is currently up in the turn order (see
        // BattleScreenUI.RefreshTurnOrder) — the shared amber selection frame (UI_CardSelection,
        // sliced), authored on the prefab and only toggled here. Same look as collection selection.
        [SerializeField] private GameObject actingHighlight;

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
        internal Image CardArt => artImage;

        private BattleScreenUI _screen;
        private bool _draggable;
        private Canvas _rootCanvas;
        private RectTransform _ghost;
        private bool _hasBinding;
        private (Sprite Art, string Name, CardDefinition Equipment, CardDefinition Mutator, bool IsHero) _face;
        private HashSet<string> _abilities;
        private bool _isAviation;
        private (int Attack, int Defense, int Hp, int MaxHp, int Move, int Range)? _shownStats;
        private (bool Draggable, bool Acting, bool Move, bool Attack)? _interaction;
        private bool _hasMovePose;
        private int _moveSiblingIndex;
        private Vector3 _movePosition;

        private void Awake()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
        }

        public void Setup(BattleScreenUI screen, UnitData unit, int row, int col, bool draggable,
            bool isActingUnit = false, bool isLegalMoveTarget = false, bool isLegalAttackTarget = false)
        {
            _screen = screen;
            Row = row;
            Col = col;
            equipmentArtToggle?.SetPeer(mutatorArtToggle);
            mutatorArtToggle?.SetPeer(equipmentArtToggle);
            Refresh(unit, draggable, isActingUnit, isLegalMoveTarget, isLegalAttackTarget);
        }

        internal void Refresh(UnitData unit, bool draggable, bool acting, bool move, bool attack)
        {
            bool occupantChanged = !_hasBinding || Unit != unit;
            var face = (unit?.Art, unit?.Name, unit?.Equipment, unit?.Mutator, unit?.IsHero == true);
            if (occupantChanged)
            {
                ClearDrag();
                if (Unit != null)
                {
                    BattleSkillEffect.Clear(transform, BattleSkillEffect.Kind.Heal);
                    BattleSkillEffect.Clear(transform, BattleSkillEffect.Kind.Drill);
                }
                Unit = unit;
            }
            if (occupantChanged || _face != face)
            {
                _face = face;
                BindFace(unit);
            }
            RefreshSkillsText(unit, occupantChanged);
            RefreshCombatStats();
            RefreshInteraction(draggable, acting, move, attack);
            _hasBinding = true;
        }

        private void BindFace(UnitData unit)
        {
            RevertEquipmentPreview();
            if (artImage != null)
            {
                artImage.sprite = _face.Art;
                SetVisible(artImage.gameObject, unit != null);
            }
            CardType? hostType = unit == null ? (CardType?)null : _face.IsHero ? CardType.Hero : CardType.Unit;
            equipmentArtToggle?.Configure(_face.Equipment, _screen?.GameConfig, hostType);
            mutatorArtToggle?.Configure(_face.Mutator, _screen?.GameConfig, hostType);
            if (nameText == null) return;

            nameText.text = _face.Name ?? string.Empty;
            Transform title = nameText.transform.parent;
            SetVisible(title != null && title != transform ? title.gameObject : nameText.gameObject, unit != null);
        }

        private void RefreshSkillsText(UnitData unit, bool occupantChanged)
        {
            if (skillsText == null) return;
            if (unit == null)
            {
                if (occupantChanged)
                {
                    _abilities?.Clear();
                    skillsText.text = string.Empty;
                    SetVisible(skillsText.gameObject, false);
                }
                return;
            }

            _abilities ??= new HashSet<string>();
            if (!occupantChanged && _isAviation == unit.IsAviation && _abilities.SetEquals(unit.Abilities)) return;
            RevertEquipmentPreview();
            _isAviation = unit.IsAviation;
            _abilities.Clear();
            _abilities.UnionWith(unit.Abilities);
            string text = _screen?.GameConfig?.FormatUnitFace(unit.Abilities, unit.IsAviation) ?? string.Empty;
            skillsText.text = text;
            SetVisible(skillsText.gameObject, text.Length != 0);
        }

        private void RefreshInteraction(bool draggable, bool acting, bool move, bool attack)
        {
            var state = (draggable, acting, move, attack);
            if (_interaction == state) return;
            _interaction = state;
            if (!draggable) ClearDrag();
            _draggable = draggable;
            Color color = attack ? LegalAttackColor : move ? LegalMoveColor : CellColor;
            if (background != null && background.color != color) background.color = color;
            SetVisible(attackIcon, attack);
            SetVisible(actingHighlight, acting);
        }

        private static void SetVisible(GameObject visual, bool visible)
        {
            if (visual != null && visual.activeSelf != visible) visual.SetActive(visible);
        }

        public void RefreshCombatStats()
        {
            if (statsRow == null) return;
            UnitData unit = Unit;
            SetVisible(statsRow, unit != null);
            if (unit == null)
            {
                _shownStats = null;
                return;
            }

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
                // The battle's attack range (weather included), as target legality reads it.
                slot5 = _screen != null ? _screen.GetDisplayedAttackRange(unit) : unit.Range;
            }

            var stats = (Attack: slot1, Defense: slot2, Hp: unit.HitPointsCurrent,
                MaxHp: unit.HitPointsMax, Move: unit.MoveMax, Range: slot5);
            var old = _shownStats;
            if (old == stats) return;
            RevertEquipmentPreview();
            _shownStats = stats;
            if (attackStatText != null && old?.Attack != stats.Attack) attackStatText.text = stats.Attack.ToString();
            if (defenseStatText != null && old?.Defense != stats.Defense) defenseStatText.text = stats.Defense.ToString();
            if (hpStatText != null && (old?.Hp != stats.Hp || old?.MaxHp != stats.MaxHp))
                hpStatText.text = $"{stats.Hp}/{stats.MaxHp}";
            if (moveStatText != null && old?.Move != stats.Move) moveStatText.text = stats.Move.ToString();
            if (rangeStatText != null && old?.Range != stats.Range) rangeStatText.text = stats.Range.ToString();
        }

        // Same split as the strategic map (HexSelectionController): left click only ever
        // inspects, right click is the one that acts. Right click reaches OnCellClicked
        // regardless of whether Unit is null (an empty cell can be a legal move destination) —
        // BattleScreenUI itself decides whether it amounts to a legal move/attack for whoever's
        // current; anything else is a no-op, same as clicking an irrelevant cell always having
        // been harmless.
        public void OnPointerClick(PointerEventData eventData)
        {
            if (UIFocusUtility.IsGameplayInputBlocked || (Unit != null && Unit.HitPointsCurrent <= 0)) return;
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
        public void OnPointerExit(PointerEventData eventData) => RevertEquipmentPreview();

        private void RevertEquipmentPreview()
        {
            equipmentArtToggle?.Revert();
            mutatorArtToggle?.Revert();
        }

        // The arrangement drag ghost follows the pointer above the board.
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
            if (UIFocusUtility.IsGameplayInputBlocked) return;
            if (IsDragging || !_draggable || Unit == null)
                return;
            _ghost = CreateGhost();
            if (_ghost == null)
                return;
            IsDragging = true;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)_rootCanvas.transform, eventData.position, eventData.pressEventCamera, out Vector2 localPoint);
            _ghost.anchoredPosition = localPoint - ((RectTransform)_rootCanvas.transform).rect.center;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (UIFocusUtility.IsGameplayInputBlocked) return;
            if (!IsDragging || _ghost == null)
                return;
            UIUtility.ApplyScreenDelta(_ghost, eventData, _rootCanvas);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!IsDragging)
                return;
            ClearDrag();
            if (!UIFocusUtility.IsGameplayInputBlocked)
                _screen?.TryDropOnCell(this, eventData.position);
        }

        private void ClearDrag()
        {
            IsDragging = false;
            if (_ghost != null)
            {
                _ghost.gameObject.SetActive(false);
                Destroy(_ghost.gameObject);
            }
            _ghost = null;
        }

        private void OnDisable()
        {
            ClearDrag();
            RestoreMovePose();
            RevertEquipmentPreview();
        }

        // The screen suspends layout during the slide. Restore this fixed slot on completion
        // or cancellation, before the screen applies the new occupants and resumes layout.
        public IEnumerator AnimateMoveTo(RectTransform destination, float duration)
        {
            if (Unit == null || destination == null)
                yield break;

            Vector3 startPos = rectTransform.position;
            Vector3 endPos = destination.position;
            _movePosition = rectTransform.anchoredPosition3D;
            _moveSiblingIndex = rectTransform.GetSiblingIndex();
            _hasMovePose = true;
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

        internal void RestoreMovePose()
        {
            if (!_hasMovePose) return;
            _hasMovePose = false;
            rectTransform.SetSiblingIndex(_moveSiblingIndex);
            rectTransform.anchoredPosition3D = _movePosition;
        }
    }
}

