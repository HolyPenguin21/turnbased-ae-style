using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Turns;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // The Army Viewer: shows one ArmyData at a time — its unit grid, and either that army's
    // summary stats or (once a card is clicked) one unit's full detail. Every army on the same
    // hex is listed as buttons (armyButtonRow, reusing the same ArmyButtonUI/ArmyButtonRowUI as
    // the hex-side row) so the player can switch which army is shown without closing this, and
    // drag a unit card onto one of those buttons to move it there. Garrison and any player-
    // created army share this exact same modal — only the top-bar context button differs
    // (Create Army for the garrison, Rename otherwise), see OnContextButtonClicked.
    public class ArmyViewerModalUI : MonoBehaviour
    {
        // Charged once per Create Army click — matches the "administrative but not free"
        // feel the user asked for; shown on the context button's own label (see RefreshTitle)
        // when it reads "Create Army" so the cost is never a surprise.
        private const int CreateArmyApCost = 2;

        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Image factionLogo;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private Button contextButton;
        [SerializeField] private TMP_Text contextButtonLabel;
        [SerializeField] private Button closeButton;
        [SerializeField] private ArmyButtonRowUI armyButtonRow;
        [SerializeField] private Transform gridContainer;
        // Same GameObject as gridContainer — a separately typed reference purely so
        // TryDropUnit/ResolveGridSlotIndex can read the grid's own cell size/spacing/column
        // count directly instead of duplicating those numbers as separate tunables that could
        // drift out of sync with the actual layout.
        [SerializeField] private GridLayoutGroup grid;
        [SerializeField] private Image detailArt;
        [SerializeField] private TMP_Text detailText;
        [SerializeField] private RenameArmyPopupUI renamePopup;
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private FactionCardCatalog catalog;
        [SerializeField] private GameTurnController turnController;
        // Only needed for CreateArmyMarker (a freshly created army needs its own map marker —
        // see Game.Map.ArmyController) and RestackArmiesOn (moving a unit between armies here
        // can flip one empty<->non-empty, changing which marker is visible on the shared hex).
        [SerializeField] private HexSelectionController hexSelectionController;

        // Read by ArmyUnitCardUI (via the modal reference it already keeps) for
        // GameConfig.FormatAbilities — see ShowUnitDetail for this modal's own use of it.
        public GameConfig GameConfig => gameConfig;

        private readonly List<ArmyUnitCardUI> _cards = new List<ArmyUnitCardUI>();
        private ArmyData _currentArmy;
        // Set by ShowReadOnly (an enemy army, clicked for inspection only) vs. Show (the
        // player's own) — read by ArmyUnitCardUI to refuse starting a drag at all (no reorder,
        // no moving a unit into another army), and by RefreshTitle to hide the context button
        // (no Create Army / Rename on someone else's army). Detail-click (ShowUnitDetail) stays
        // available either way — inspecting stats is never blocked, only mutating actions are.
        private bool _readOnly;
        // Set by ShowLocked (a battle popup's own "browse this army" click — see
        // BattleContactPopupUI/BattleSideArmyListUI) — hides the button row entirely (including
        // its own scroll arrows), on top of everything _readOnly already blocks. Neither side of
        // a battle should be able to jump to a DIFFERENT one of that owner's armies from inside
        // this view — the battle popup's own side columns are the only way to pick which army to
        // inspect there.
        private bool _hideArmySwitcher;
        private Canvas _canvas;
        // Live drag-reorder state (see BeginReorderPreview/PreviewReorder/TryDropUnit) — a
        // scratch copy of _currentArmy.Members that's live-reordered as the card moves, so the
        // rest of the grid visibly makes room for it before the drop, the same way CardHandUI's
        // hand does. _currentArmy.Members itself is never touched until the drop actually
        // commits, so an aborted/invalid drag has nothing to undo.
        private ArmyUnitCardUI _draggingCard;
        private List<UnitData> _dragPreviewOrder;
        // Every army that's had a member dragged OUT of it (see TryDropUnit) at any point during
        // this modal's current open session — checked for actual deletion only once the modal
        // closes (see Hide), not immediately: the player might still be mid-reorganizing (empty
        // it, then partly refill it, before deciding) while switching between armies via the
        // button row, all without ever closing this. HexSelectionController.DeleteArmyIfEmptied
        // re-checks the final Members count itself, so an army that ended up non-empty again is
        // simply a no-op there.
        private readonly HashSet<ArmyData> _pendingEmptyCheck = new HashSet<ArmyData>();

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Raised whenever this modal actually closes (ESC, the close button, or code calling
        // Hide() directly) — HexSelectionController uses this to refresh the hex-side button
        // row/info panel for whichever hex is still selected, since an army created or renamed
        // from inside this modal wouldn't otherwise show up there until the player re-clicked
        // the hex (map input, and so the button row's own hex, is locked out the whole time this
        // is open — see HexSelectionController.IsInputAllowed).
        public event Action Closed;

        // Lets GameTurnController react to this modal (or its nested rename popup) opening/
        // closing instead of polling IsShowing/IsRenamePopupShowing every frame (see
        // GameTurnController.InputBlocked/CardDraggingBlocked). Fired from ActivatePanel/Hide
        // for this modal's own visibility, and relayed from renamePopup.VisibilityChanged (see
        // Awake) since CardDraggingBlocked cares about that too.
        public event Action VisibilityChanged;

        // Read by GameTurnController.CardDraggingBlocked — renaming needs card dragging (and,
        // via RtsCameraController/UIFocusUtility, WASD camera panning) switched off for its
        // duration, unlike the rest of this modal which deliberately leaves card dragging on
        // (see CardHandUI.TryDeployIntoArmyModal).
        public bool IsRenamePopupShowing => renamePopup != null && renamePopup.IsShowing;

        // Whichever army this is currently displaying — CardHandUI reads this to know which
        // army a dropped Unit/Hero card should join (see TryDeployIntoArmyModal).
        public ArmyData CurrentArmy => _currentArmy;

        // True while showing someone else's army (see ShowReadOnly) — read by ArmyUnitCardUI to
        // block dragging entirely. Also true, regardless of how this modal was opened, whenever
        // the army currently on screen is a Prison (see ArmyData.IsPrison) — its captured heroes
        // "just lie there" per the user's own spec, not draggable/interactable even from the
        // owner's own otherwise-editable modal session.
        public bool IsReadOnly => _readOnly || (_currentArmy != null && _currentArmy.IsPrison);

        // Hit-test used by CardHandUI while a card is being dragged, to tell "dropped on the
        // modal" apart from "dropped on the map behind it" — screenPosition is raw mouse/pointer
        // screen coordinates, same convention as HexSelectionController.RaycastHex.
        public bool ContainsScreenPoint(Vector2 screenPosition)
        {
            if (!IsShowing || panelRoot == null)
                return false;
            return RectTransformUtility.RectangleContainsScreenPoint((RectTransform)panelRoot.transform, screenPosition, ResolveEventCamera());
        }

        // null for a Screen Space - Overlay canvas (the norm here), the canvas's own camera
        // otherwise — same resolution rule RectTransformUtility's screen-point checks need,
        // shared by both call sites below instead of repeating the ternary in each.
        private Camera ResolveEventCamera()
        {
            return _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
        }

        // Called by CardHandUI after successfully deploying a card straight into _currentArmy
        // from outside (see TryDeployIntoArmyModal) — the grid/summary need to reflect the new
        // member without disturbing anything else about the currently open view.
        public void RefreshAfterExternalDeploy()
        {
            RefreshGrid();
            ShowArmySummary();
        }

        private void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (closeButton != null)
                closeButton.onClick.AddListener(Hide);
            if (contextButton != null)
                contextButton.onClick.AddListener(OnContextButtonClicked);
            if (renamePopup != null)
                renamePopup.VisibilityChanged += () => VisibilityChanged?.Invoke();
        }

        public void Show(ArmyData army)
        {
            _readOnly = false;
            ActivatePanel();
            SwitchTo(army);
        }

        // A precise click on another player's army marker (see
        // HexSelectionController.TryHandleArmyMarkerClick) opens the exact same modal, but
        // inspection-only — no reorder/move-between-armies drag, no Create Army/Rename. Switching
        // between that SAME owner's other armies sharing the hex (see RefreshButtonRow) still
        // works, since that's just picking which of their armies to look at, not an action.
        public void ShowReadOnly(ArmyData army)
        {
            _readOnly = true;
            ActivatePanel();
            SwitchTo(army);
        }

        // Opened from a battle popup's own side columns (see BattleContactPopupUI) — for either
        // side's army, own or enemy alike. Same restrictions as ShowReadOnly (no drag, no
        // Create Army/Rename), PLUS the button row itself is hidden — nothing here should let
        // the player wander off to inspect a DIFFERENT one of that owner's armies; that's the
        // battle popup's own side list's job, not this modal's.
        public void ShowLocked(ArmyData army)
        {
            _readOnly = true;
            _hideArmySwitcher = true;
            ActivatePanel();
            SwitchTo(army);
        }

        // Shared by every Show* variant — SetAsLastSibling matters when this is opened from
        // ON TOP of another already-open panel (e.g. BattleContactPopupUI's own side lists via
        // ShowLocked): sibling order is draw order on this project's single shared Canvas, and
        // this panel was added to the scene before those newer ones, so without forcing it back
        // to the end of the list it would render BEHIND whatever's already showing instead of
        // over it.
        private void ActivatePanel()
        {
            if (panelRoot == null)
                return;
            panelRoot.SetActive(true);
            panelRoot.transform.SetAsLastSibling();
            VisibilityChanged?.Invoke();
        }

        public void Hide()
        {
            bool wasShowing = IsShowing;
            if (panelRoot != null)
                panelRoot.SetActive(false);
            if (renamePopup != null)
                renamePopup.Hide();
            if (armyButtonRow != null)
                armyButtonRow.Hide();
            ClearGrid();

            // Only now, on close, does an army emptied out during this session (see TryDropUnit)
            // actually get torn down — see DeleteArmyIfEmptied and _pendingEmptyCheck's own
            // comment for why this can't just happen the moment it empties. Before OnArmyModalClosed
            // (Closed, below) re-selects the hex, so its button row/info panel already reflect
            // whatever got deleted.
            foreach (ArmyData army in _pendingEmptyCheck)
                hexSelectionController?.DeleteArmyIfEmptied(army);
            _pendingEmptyCheck.Clear();

            _currentArmy = null;
            _readOnly = false;
            _hideArmySwitcher = false;
            if (wasShowing)
            {
                Closed?.Invoke();
                VisibilityChanged?.Invoke();
            }
        }

        // ESC closes the rename popup first if it's open (matching a Cancel button, which it
        // doesn't otherwise have), and the whole modal on a second press — never both actions
        // on the same keypress.
        private void Update()
        {
            if (!IsShowing || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
                return;

            if (renamePopup != null && renamePopup.IsShowing)
                renamePopup.Hide();
            else
                Hide();
        }

        // Called both by the in-modal army-button row (switching view without closing) and by
        // CreateArmy (jump straight to the freshly created army).
        public void SwitchTo(ArmyData army)
        {
            _currentArmy = army;
            RefreshTitle();
            RefreshButtonRow();
            RefreshGrid();
            ShowArmySummary();
        }

        public void ShowUnitDetail(UnitData unit)
        {
            if (unit == null)
            {
                ShowArmySummary();
                return;
            }

            if (detailArt != null)
            {
                detailArt.sprite = unit.Art;
                detailArt.gameObject.SetActive(true);
            }
            if (detailText != null)
            {
                // Every stat the unit actually has, in one place — Activation cost is
                // deliberately left out (it's an army-level cost paid once per turn, not really
                // "this unit's own stat"); Command Rating only means anything for a hero (see
                // ArmyData.Capacity), so it's skipped entirely for a plain unit rather than
                // showing a meaningless number.
                string text = $"{unit.Name}\n" +
                    $"Attack {unit.Attack}\n" +
                    $"Defense {unit.Defense}\n" +
                    $"Resistance {unit.Resistance}\n" +
                    $"Range {unit.Range}\n" +
                    $"HP {unit.HitPointsCurrent}/{unit.HitPointsMax}\n" +
                    $"Move {unit.MoveCurrent}/{unit.MoveMax}\n" +
                    $"Initiative {unit.Initiative}";
                if (unit.IsHero)
                    text += $"\nCommand Rating: {unit.CommandRating}\nFate: {unit.Fate}";
                // Skills don't exist as their own system yet — Abilities is the closest
                // equivalent today (see GameConfig.FormatAbilities); once real skill data
                // exists, list it here too, same as this.
                string abilities = gameConfig != null ? gameConfig.FormatAbilities(unit.Abilities) : null;
                if (!string.IsNullOrEmpty(abilities))
                    text += $"\n{abilities}";
                detailText.text = text;
            }
        }

        // Called by ArmyUnitCardUI.OnBeginDrag, before the card starts following the pointer —
        // snapshots the current roster order as the scratch list PreviewReorder live-edits, so
        // dragging can show the eventual result without touching _currentArmy.Members until the
        // drop actually commits (see TryDropUnit).
        public void BeginReorderPreview(ArmyUnitCardUI card)
        {
            if (_currentArmy == null || card == null || card.Unit == null)
                return;
            _draggingCard = card;
            _dragPreviewOrder = new List<UnitData>(_currentArmy.Members);
        }

        // Called by ArmyUnitCardUI.OnDrag every time the card moves — re-sorts the scratch
        // order to match wherever it's currently hovering (same live-reorder idea as
        // CardHandUI.PreviewDrag for the hand) and slides every OTHER card to its new slot to
        // match (see RepositionNonDraggedCards). The dragged card's own position is left alone
        // — it's following the pointer directly (see OnDrag).
        public void PreviewReorder(ArmyUnitCardUI card, Vector2 screenPosition)
        {
            if (_dragPreviewOrder == null || card != _draggingCard || card.Unit == null)
                return;

            int? slotIndex = ResolveGridSlotIndex(screenPosition);
            if (!slotIndex.HasValue)
                return;

            int currentIndex = _dragPreviewOrder.IndexOf(card.Unit);
            if (currentIndex < 0)
                return;

            int targetIndex = ClampForHeroOrder(_dragPreviewOrder, card.Unit, slotIndex.Value);
            if (targetIndex == currentIndex)
                return;

            // Capacity is read off whichever hero ends up at the front (see
            // ArmyData.ComputeCapacity) — moving a lower-CommandRating hero into that spot can
            // shrink it below how many members are actually here. Simulate the swap first and
            // reject it outright rather than applying a reorder that would silently strand
            // members past the new (smaller) capacity — RefreshGrid only ever renders Capacity
            // slots, so anyone past that just vanishes from the grid without being removed from
            // Members.
            var candidate = new List<UnitData>(_dragPreviewOrder);
            candidate.RemoveAt(currentIndex);
            candidate.Insert(targetIndex, card.Unit);
            if (ArmyData.ComputeCapacity(candidate, _currentArmy.IsGarrison) < candidate.Count)
                return;

            _dragPreviewOrder = candidate;
            RepositionNonDraggedCards();
        }

        // Slides every OTHER card to the slot position matching _dragPreviewOrder — each card
        // keeps showing its own original content the whole time (never relabelled), it just
        // animates to a new position, exactly like CardHandUI's own hand reorder. A card whose
        // slot hasn't changed gets a no-op call (SetSlot's own "did this actually move" guard
        // skips restarting its animation).
        //
        // A member's slot is simply its own index within _dragPreviewOrder — NOT its index
        // among "everyone but the dragged unit" (that was the earlier, broken version: it
        // always renumbered every other card into one contiguous block starting at 0,
        // regardless of where the dragged unit's target slot actually was, so cards past that
        // point never moved back when the drag returned toward its start — the dragged unit's
        // own slot just needs to stay empty while it's not there, not have everyone else
        // collapse around it). RemoveAt+Insert in PreviewReorder already leaves every other
        // member sitting at exactly the index it should visually occupy; this only needs to
        // read that back. An empty capacity placeholder (Unit == null) never appears in
        // _dragPreviewOrder at all and never moves — it stays at its own fixed doc-order index,
        // which for a placeholder is already its permanent slot beyond the last member.
        private void RepositionNonDraggedCards()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                ArmyUnitCardUI c = _cards[i];
                if (c == _draggingCard)
                    continue;
                int slot = c.Unit != null ? _dragPreviewOrder.IndexOf(c.Unit) : i;
                c.SetSlot(SlotPosition(slot), animated: true);
            }
        }

        // Inverse of ResolveGridSlotIndex, using the same row-major top-left convention (see
        // the GridLayoutGroup this reads its metrics from: StartCorner UpperLeft, StartAxis
        // Horizontal) — the actual position driver now that every card's LayoutElement is
        // permanently ignoreLayout (see ArmyUnitCardUI). Matches what GridLayoutGroup itself
        // would have produced for a top-left-anchored/pivoted child, so switching between "no
        // drag happening" (this) and "mid-drag" (RepositionNonDraggedCards, same formula) never
        // visibly jumps.
        private Vector2 SlotPosition(int index)
        {
            int columns = grid != null ? Mathf.Max(1, grid.constraintCount) : 1;
            int col = index % columns;
            int row = index / columns;
            Vector2 cellSize = grid != null ? grid.cellSize : Vector2.zero;
            Vector2 spacing = grid != null ? grid.spacing : Vector2.zero;
            RectOffset padding = grid != null ? grid.padding : null;
            float left = padding != null ? padding.left : 0f;
            float top = padding != null ? padding.top : 0f;
            float x = left + col * (cellSize.x + spacing.x);
            float y = -(top + row * (cellSize.y + spacing.y));
            return new Vector2(x, y);
        }

        // Heroes are always a contiguous prefix of the roster (see ArmyData.AddMemberSorted) —
        // dragging must never break that: a hero can only land among the OTHER heroes, and a
        // regular unit can only land after all of them. `order` still contains `dragged` at its
        // current position; the clamp is computed against everyone else's counts.
        private static int ClampForHeroOrder(List<UnitData> order, UnitData dragged, int rawTarget)
        {
            int maxIndex = order.Count - 1; // after the eventual RemoveAt, Count-1 slots remain
            int heroCountExcludingDragged = order.Count(u => u != dragged && u.IsHero);
            int clamped = Mathf.Clamp(rawTarget, 0, maxIndex);
            return dragged.IsHero ? Mathf.Min(clamped, heroCountExcludingDragged) : Mathf.Max(clamped, heroCountExcludingDragged);
        }

        // Called by ArmyUnitCardUI.OnEndDrag. Two things a drop can mean: dropped on another
        // army's button (moves the unit there — checks capacity), or dropped back within the
        // grid itself (commits whatever order PreviewReorder last showed — see
        // BeginReorderPreview). Returns false (card snaps back to where it was picked up) only
        // when neither ever applied: the drag never entered this grid in the first place.
        public bool TryDropUnit(ArmyUnitCardUI card, Vector2 screenPosition)
        {
            List<UnitData> previewOrder = card == _draggingCard ? _dragPreviewOrder : null;
            _draggingCard = null;
            _dragPreviewOrder = null;

            if (_currentArmy == null || card == null || card.Unit == null)
                return false;

            ArmyButtonUI targetButton = FindButtonAt(screenPosition);
            if (targetButton != null)
            {
                ArmyData target = targetButton.Army;
                if (target == null || target == _currentArmy || target.IsPrison)
                    return false;

                if (!target.HasRoom)
                {
                    turnController?.ShowSpawnHint($"{target.Name} is full — can't move {card.Unit.Name} in.");
                    return false;
                }

                // Same orphan risk as the in-grid reorder (see PreviewReorder's own capacity
                // check) — removing this army's own front/commanding hero can shrink ITS
                // capacity below however many members are left behind.
                var remaining = new List<UnitData>(_currentArmy.Members);
                remaining.Remove(card.Unit);
                if (ArmyData.ComputeCapacity(remaining, _currentArmy.IsGarrison) < remaining.Count)
                {
                    turnController?.ShowSpawnHint($"Moving {card.Unit.Name} out would leave {_currentArmy.Name} without room for everyone else.");
                    return false;
                }

                // An army that's already spent its own ActivationApCost this turn (see
                // ArmyData.HasActivatedThisTurn) moves for free the rest of the turn — without
                // this, reinforcing it mid-turn by dragging units in from elsewhere would let
                // those members ride along for free too, bypassing the AP they'd otherwise have
                // owed as part of that same activation (see the user's own report: move a cheap
                // 1-unit army first, then bulk it up from the garrison at zero extra AP cost).
                // Only the INCOMING unit's own share is charged — target's other, already-paid-for
                // members aren't re-billed, and a target that hasn't activated yet this turn pays
                // for everyone, this unit included, on its own first move order as normal.
                PlayerRoot targetRoot = null;
                if (target.HasActivatedThisTurn)
                {
                    targetRoot = PlayerRootRegistry.FindFor(target.Owner);
                    if (targetRoot == null || !targetRoot.CanSpendActionPoints(card.Unit.ActivationApCost))
                    {
                        turnController?.ShowSpawnHint($"Not enough action points to add {card.Unit.Name} to {target.Name} ({card.Unit.ActivationApCost} AP needed — it already moved this turn).");
                        return false;
                    }
                }

                _currentArmy.Members.Remove(card.Unit);
                target.AddMemberSorted(card.Unit);
                targetRoot?.SpendActionPoints(card.Unit.ActivationApCost);
                // Neither army has a marker of its own that follows individual units around
                // (see Game.Map.ArmyController) — only whichever is each owner's visible
                // representative on the shared hex does, and this move can flip either army
                // between empty and non-empty, which changes that.
                hexSelectionController?.RestackArmiesOn(_currentArmy.Hex, null);
                // _currentArmy itself may have just lost its last member — actual deletion (see
                // DeleteArmyIfEmptied) is deferred until this modal closes, not done here, in
                // case the player isn't done reorganizing yet (see _pendingEmptyCheck).
                _pendingEmptyCheck.Add(_currentArmy);

                RefreshGrid();
                ShowArmySummary();
                return true;
            }

            if (previewOrder == null)
                return false; // dropped without ever previewing a reorder (e.g. an empty slot) — nothing to commit

            _currentArmy.Members.Clear();
            _currentArmy.Members.AddRange(previewOrder);
            RefreshGrid();
            return true;
        }

        private ArmyButtonUI FindButtonAt(Vector2 screenPosition)
        {
            if (armyButtonRow == null)
                return null;

            Camera cam = ResolveEventCamera();
            foreach (ArmyButtonUI button in armyButtonRow.Buttons)
                if (button != null && RectTransformUtility.RectangleContainsScreenPoint(button.RectTransform, screenPosition, cam))
                    return button;
            return null;
        }

        // Converts a drop's screen position into a flat slot index (row-major, matching
        // GridLayoutGroup's own default StartCorner/StartAxis) within the grid — null if the
        // drop landed outside the grid's rect entirely, so callers can tell "not a reorder"
        // apart from "reorder to slot 0".
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

        private void RefreshTitle()
        {
            if (factionLogo != null)
            {
                factionLogo.sprite = catalog != null ? catalog.logo : null;
                factionLogo.gameObject.SetActive(factionLogo.sprite != null);
            }
            if (titleText != null)
            {
                string factionName = catalog != null ? catalog.displayName : string.Empty;
                string playerName = _currentArmy?.Owner != null ? _currentArmy.Owner.Nickname : string.Empty;
                string armyName = _currentArmy != null ? _currentArmy.Name : string.Empty;
                string prefix = string.Join(" — ", new[] { factionName, playerName }.Where(s => !string.IsNullOrEmpty(s)));
                titleText.text = string.IsNullOrEmpty(armyName) ? prefix : $"{prefix} — <b>{armyName}</b>";
            }
            if (contextButtonLabel != null)
                contextButtonLabel.text = _currentArmy != null && _currentArmy.IsGarrison ? $"Create Army ({CreateArmyApCost} AP)" : "Rename";
            // No administrative actions on someone else's army — Create Army/Rename both
            // mutate it (see OnContextButtonClicked) — nor on a Prison, even the owner's own.
            if (contextButton != null)
                contextButton.gameObject.SetActive(!IsReadOnly);
        }

        // Only the CURRENT army's own owner's other armies on the same hex — not everyone else
        // sharing it too (an enemy locked in combat there, say). Switching is "look at a
        // different one of THIS army's siblings", not a way to wander into someone else's roster
        // through the back door.
        private void RefreshButtonRow()
        {
            if (armyButtonRow == null || _currentArmy == null)
                return;
            if (_hideArmySwitcher)
            {
                armyButtonRow.Hide();
                return;
            }
            // Prison goes first, per the user's own spec, and only appears at all once it holds
            // at least one captured hero — everyone else keeps ArmyRegistry's own natural
            // (registration) order, same as before this existed, rather than a full sort that
            // could otherwise reshuffle them unpredictably (List.Sort isn't stable).
            List<ArmyData> atHex = ArmyRegistry.AllAt(_currentArmy.Hex).FindAll(a => a.Owner == _currentArmy.Owner);
            var siblings = new List<ArmyData>();
            ArmyData prison = atHex.Find(a => a.IsPrison && a.Members.Count > 0);
            if (prison != null)
                siblings.Add(prison);
            foreach (ArmyData army in atHex)
                if (!army.IsPrison)
                    siblings.Add(army);
            armyButtonRow.Show(siblings, SwitchTo);
        }

        // One slot per point of Capacity, not just one per actual Member — the empty ones
        // render as faint placeholders (see ArmyUnitCardUI.Setup) so it's obvious a card can
        // be dropped there, instead of the grid just looking randomly short of a full row.
        private void RefreshGrid()
        {
            ClearGrid();
            if (gridContainer == null || gameConfig == null || gameConfig.armyUnitCardPrefab == null || _currentArmy == null)
                return;

            for (int i = 0; i < _currentArmy.Capacity; i++)
            {
                UnitData member = i < _currentArmy.Members.Count ? _currentArmy.Members[i] : null;
                ArmyUnitCardUI card = Instantiate(gameConfig.armyUnitCardPrefab, gridContainer);
                card.Setup(this, member);
                card.SetSlot(SlotPosition(i), animated: false);
                _cards.Add(card);
            }
        }

        private void ClearGrid() => UIListUtility.DestroyAndClear(_cards);

        // Default detail-panel state — the army's own aggregate stats, shown whenever nothing
        // is selected (on open, on switching army, after a drag-and-drop move). Fate Points,
        // Stealth, Experience and Battle Honors are stub text (no mechanic behind them yet);
        // Prestige is omitted entirely rather than stubbed. Leader/capacity and Movement Range
        // are real, computed from Members.
        private void ShowArmySummary()
        {
            if (detailArt != null)
                detailArt.gameObject.SetActive(false);
            if (detailText == null || _currentArmy == null)
                return;

            UnitData hero = _currentArmy.Members.FirstOrDefault(m => m.IsHero);
            string leaderLine = hero != null
                ? $"{hero.Name} Commanding ({_currentArmy.Capacity})"
                : $"No Hero Commanding ({_currentArmy.Capacity})";

            int heroCount = hero != null ? 1 : 0;
            int unitCount = _currentArmy.Members.Count - heroCount;
            string membersLine = heroCount > 0
                ? $"1 Hero and {unitCount} Unit{(unitCount == 1 ? "" : "s")}"
                : $"{unitCount} Unit{(unitCount == 1 ? "" : "s")}";

            detailText.text = $"{_currentArmy.Name}\n{leaderLine}\n{membersLine}\n" +
                "0 Fate Points\nNot Stealth Capable\n" +
                $"Movement Range: {_currentArmy.MaxMovement}\n" +
                "Experience: Green\nBattle Honors: —";
        }

        private void OnContextButtonClicked()
        {
            if (_currentArmy == null)
                return;

            if (_currentArmy.IsGarrison)
                CreateArmy();
            else
                renamePopup?.Show(_currentArmy, OnRenamed);
        }

        private void CreateArmy()
        {
            if (catalog == null || _currentArmy == null)
                return;

            PlayerRoot root = PlayerRootRegistry.FindFor(_currentArmy.Owner);
            if (root == null || !root.CanSpendActionPoints(CreateArmyApCost))
            {
                turnController?.ShowSpawnHint("Not enough action points to create a new army.");
                return;
            }
            root.SpendActionPoints(CreateArmyApCost);

            var takenNames = ArmyRegistry.AllForOwner(_currentArmy.Owner).Select(a => a.Name);
            var army = new ArmyData
            {
                Name = catalog.GetRandomArmyName(takenNames),
                Hex = _currentArmy.Hex,
                Owner = _currentArmy.Owner,
                IsGarrison = false,
            };
            ArmyRegistry.Register(army);
            // Every army needs its own map marker (see Game.Map.ArmyController) — starts
            // invisible (RestackArmiesOn never shows an empty army) until units are dragged
            // into it from the garrison.
            hexSelectionController?.CreateArmyMarker(army);
            // Stays on whatever's currently shown (the garrison, always, since that's the only
            // thing Create Army is ever clicked from) — the new army just needs to show up as a
            // button in the row, not take over the view.
            RefreshButtonRow();
        }

        private void OnRenamed()
        {
            RefreshTitle();
            RefreshButtonRow();
        }
    }
}
