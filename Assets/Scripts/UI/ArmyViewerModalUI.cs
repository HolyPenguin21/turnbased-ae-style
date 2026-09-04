using Game.Combat;
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Terrain;
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
        // Resolved per-owner via ResolveCatalog (GetCatalog(owner.Faction)) rather than a fixed
        // reference — this modal is shared across every player's armies, human or AI, whichever
        // faction each one picked in setup (see CardHandUI's identical per-player resolution).
        [SerializeField] private StartingDeckCatalog startingDeckCatalog;
        [SerializeField] private GameTurnController turnController;
        // Only needed for CreateArmyMarker (a freshly created army needs its own map marker —
        // see Game.Map.ArmyController) and RestackArmiesOn (moving a unit between armies here
        // can flip one empty<->non-empty, changing which marker is visible on the shared hex).
        [SerializeField] private HexSelectionController hexSelectionController;
        // Terrain lookup for ShowArmySummary's own Terrain Def line — same reference
        // HexSelectionController/BattleContactPopupUI already hold, wired separately here since
        // neither exposes it publicly.
        [SerializeField] private HexMap map;

        // Read by ArmyUnitCardUI (via the modal reference it already keeps) for
        // GameConfig.FormatAbilities — see ShowUnitDetail for this modal's own use of it.
        public GameConfig GameConfig => gameConfig;

        // Terrain + Base-building defense bonus this army's own hex grants right now — same
        // lookup as ShowArmySummary's own Terrain Def/Construction Def lines, just as one number
        // for per-unit card display (see ArmyUnitCardUI.RefreshStatsRow). Every member of
        // _currentArmy shares this same hex, so it's one value for the whole grid, not per-unit.
        public int CurrentArmyDefenseBonus =>
            _currentArmy?.VisualSnapshotDefenseBonus
            ?? (_currentArmy != null ? Mathf.RoundToInt(WorthIt.HexDefenseBonus(_currentArmy.Hex, map)) : 0);

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
        // Display-only siblings captured in the same last-seen hex. Unlike a live read-only
        // view these must never query ArmyRegistry, which may already contain newer hidden data.
        private IReadOnlyList<ArmyData> _snapshotSiblings;
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
        // Captured before Hide clears _currentArmy, so HexSelectionController can restore the
        // exact named army the player last viewed. Garrisons and read-only armies deliberately
        // leave this null: neither is a move-order selection.
        public ArmyData LastClosedSelectableArmy { get; private set; }

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

        // Set once by CardHandUI.Awake — lets a unit card's own click route into an in-progress
        // equipment attach (see CardHandUI's attach mode) without every ArmyUnitCardUI prefab
        // carrying its own hand reference.
        private CardHandUI _cardHand;
        public void SetCardHand(CardHandUI hand) => _cardHand = hand;

        // Called by ArmyUnitCardUI on a left click. True = the click was consumed by an
        // equipment attach in progress (so the card should NOT also open its detail view).
        public bool TryConsumeAttachClick(UnitData unit)
        {
            if (_cardHand == null || !_cardHand.IsAttachMode)
                return false;
            bool handled = _cardHand.TryAttachToUnit(unit);
            // The unit's ability list / stats may have changed — refresh both the grid card and
            // the detail panel for it.
            RefreshGrid();
            ShowUnitDetail(unit);
            return handled;
        }

        // Called by ArmyUnitCardUI on a right click (and from Update's Esc handling). True = an
        // attach was pending and has been cancelled (so the right click did something).
        public bool TryCancelAttach()
        {
            if (_cardHand == null || !_cardHand.IsAttachMode)
                return false;
            _cardHand.CancelAttachMode();
            return true;
        }

        private void Awake()
        {
            armyButtonRow?.SetMaxVisible(8);
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
            LastClosedSelectableArmy = null;
            _readOnly = false;
            _hideArmySwitcher = false;
            _snapshotSiblings = null;
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
            LastClosedSelectableArmy = null;
            _readOnly = true;
            _hideArmySwitcher = false;
            _snapshotSiblings = null;
            ActivatePanel();
            SwitchTo(army);
        }

        public void ShowLastSeen(ArmyData army, IReadOnlyList<ArmyData> siblings)
        {
            LastClosedSelectableArmy = null;
            _readOnly = true;
            _hideArmySwitcher = false;
            _snapshotSiblings = siblings;
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
            LastClosedSelectableArmy = null;
            _readOnly = true;
            _hideArmySwitcher = true;
            _snapshotSiblings = null;
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
            ArmyData closingArmy = _currentArmy;
            LastClosedSelectableArmy = !_readOnly && closingArmy != null && !closingArmy.IsGarrison
                && closingArmy.Members.Count > 0 ? closingArmy : null;
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
            _snapshotSiblings = null;
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

            // Esc cancels an in-progress equipment attach before it does anything to this modal.
            if (TryCancelAttach())
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
                detailArt.sprite = unit.DetailArt;
                detailArt.gameObject.SetActive(true);
            }
            if (detailText != null)
            {
                // Every stat the unit actually has, in one place — Activation cost is
                // deliberately left out (it's an army-level cost paid once per turn, not really
                // "this unit's own stat"); Command Rating only means anything for a hero (see
                // ArmyData.Capacity), so it's skipped entirely for a plain unit rather than
                // showing a meaningless number. Resistance is deliberately left out too, per the
                // user's own request.
                // Defense includes this army's own terrain/Base-building bonus — same figure it
                // would actually defend with if attacked on this hex right now (see
                // CurrentArmyDefenseBonus's own comment).
                int defenseBonus = CurrentArmyDefenseBonus;
                string defenseLine = defenseBonus != 0
                    ? $"Defense {unit.Defense + defenseBonus} ({defenseBonus:+0;-0})"
                    : $"Defense {unit.Defense}";

                string text = $"{unit.Name}\n";
                if (unit.TypeTags.Count > 0)
                    text += $"{string.Join(", ", unit.TypeTags)}\n";
                // Attack / Defense / Range are omitted for a hero card — a hero fights through
                // Command Rating / Fate / Initiative, not a per-unit combat stat block, so those
                // three numbers are meaningless noise on a hero (per the user's own request).
                if (!unit.IsHero)
                    text +=
                        $"Attack {unit.Attack}\n" +
                        $"{defenseLine}\n" +
                        $"Range {unit.Range}\n";
                text +=
                    $"HP {unit.HitPointsCurrent}/{unit.HitPointsMax}\n" +
                    $"Move {AviationRules.EffectiveMoveCurrent(unit)}/{unit.MoveMax}\n" +
                    $"Initiative {unit.Initiative}";
                if (unit.IsHero)
                    text += $"\nCommand Rating: {unit.CommandRating}\nFate: {unit.Fate}";
                // Full name + description per ability here (detail panel), as opposed to the
                // abbreviated one-line form shown on the card itself (see
                // GameConfig.FormatAbilitiesDetailed vs FormatAbilities).
                string abilities = gameConfig != null ? gameConfig.FormatAbilitiesDetailed(unit.Abilities) : null;
                if (!string.IsNullOrEmpty(abilities))
                    text += $"\n{abilities}";
                // The attached Equipment card's own name — its effect is already folded into
                // the abilities/stats above by EquipmentSystem.Apply, this just names the source.
                if (unit.Equipment != null)
                    text += $"\nEquipment: {unit.Equipment.displayName}";
                detailText.text = text;
            }
        }

        // Read by ArmyUnitCardUI.OnPointerEnter to decide whether to reveal its own Repair
        // button — read-only view (someone else's army) never offers it, same as dragging.
        public bool CanRepairUnit(UnitData unit) =>
            !IsReadOnly && _currentArmy != null && UnitRepair.IsWounded(unit) && UnitRepair.CanRepairAt(_currentArmy.Hex, _currentArmy.Owner);

        // Called by ArmyUnitCardUI's Repair button — same "afford-check, hint-on-fail, spend,
        // refresh" shape as BaseViewerModalUI.UpgradeBase/RepairBase, except the actual spend+heal
        // transaction lives in UnitRepair.TryRepair so Game.Ai.AiManagementPlanner's own repair
        // routine can call the identical logic instead of duplicating it here.
        public void RepairUnit(UnitData unit)
        {
            if (_currentArmy == null)
                return;
            PlayerRoot root = PlayerRootRegistry.FindFor(_currentArmy.Owner);
            if (!UnitRepair.TryRepair(unit, _currentArmy.Hex, root, out string failReason))
            {
                turnController?.ShowSpawnHint(failReason);
                return;
            }
            RefreshGrid();
            ShowUnitDetail(unit);
        }

        // Individual stealth actions (see Game.Map.StealthSystem) — same "query gates a
        // hover button, action spends + refreshes" shape as CanRepairUnit/RepairUnit above.
        // Entering costs 1 AP per unit; a voluntary exit by the owner is free. Only ever the
        // owner's own army (never IsReadOnly), and only a unit carrying a StealthN ability.

        public bool CanEnterStealthUnit(UnitData unit)
        {
            if (IsReadOnly || _currentArmy == null || !Game.Map.StealthSystem.CanEnterStealth(unit))
                return false;
            PlayerRoot root = PlayerRootRegistry.FindFor(_currentArmy.Owner);
            return root != null && root.CanSpendActionPoints(1);
        }

        public bool CanExitStealthUnit(UnitData unit)
            => !IsReadOnly && _currentArmy != null && unit != null && unit.IsHidden;

        public void EnterStealthUnit(UnitData unit)
        {
            if (!CanEnterStealthUnit(unit))
                return;
            PlayerRoot root = PlayerRootRegistry.FindFor(_currentArmy.Owner);
            root.SpendActionPoints(1);
            Game.Map.StealthSystem.EnterStealth(unit);
            // Trigger C — a hidden unit's state changed via the shared hex action menu; the
            // action here is not a move, so re-check detection now (design §3.C).
            Game.Map.StealthSystem.RunChecksAfterHiddenUnitAction(unit, _currentArmy.Hex, _currentArmy.Owner);
            RefreshGrid();
            ShowUnitDetail(unit);
        }

        public void ExitStealthUnit(UnitData unit)
        {
            if (!CanExitStealthUnit(unit))
                return;
            Game.Map.StealthSystem.ExitStealth(unit);
            RefreshGrid();
            ShowUnitDetail(unit);
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

            var candidate = new List<UnitData>(_dragPreviewOrder);
            candidate.RemoveAt(currentIndex);
            candidate.Insert(targetIndex, card.Unit);

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

        // A rejected drop snaps the dragged card back in ArmyUnitCardUI.OnEndDrag, but every
        // OTHER card has already had its home slot changed by RepositionNonDraggedCards. Put
        // those cards back against the real roster before discarding the scratch order, or the
        // preview layout survives visually even though no reorder was committed.
        private void CancelReorderPreview()
        {
            if (_currentArmy != null)
            {
                for (int i = 0; i < _cards.Count; i++)
                {
                    ArmyUnitCardUI c = _cards[i];
                    if (c == _draggingCard)
                        continue;
                    int slot = c.Unit != null ? _currentArmy.Members.IndexOf(c.Unit) : i;
                    if (slot >= 0)
                        c.SetSlot(SlotPosition(slot), animated: true);
                }
            }

            ClearReorderPreview();
        }

        private void ClearReorderPreview()
        {
            _draggingCard = null;
            _dragPreviewOrder = null;
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
        // BeginReorderPreview). Returns false for every rejected drop (same army, failed
        // transfer, or outside the grid): CancelReorderPreview restores the other cards, while
        // ArmyUnitCardUI.OnEndDrag snaps the dragged one back to where it was picked up.
        public bool TryDropUnit(ArmyUnitCardUI card, Vector2 screenPosition)
        {
            if (_currentArmy == null || card == null || card.Unit == null)
            {
                CancelReorderPreview();
                return false;
            }

            ArmyButtonUI targetButton = FindButtonAt(screenPosition);
            if (targetButton != null)
            {
                ArmyData target = targetButton.Army;
                if (target == null || target == _currentArmy)
                {
                    CancelReorderPreview();
                    return false;
                }

                // Same rule Game.Ai.AiManagementPlanner-driven moves use (garrison overflow
                // splits, lone-army consolidation) — pulled into ArmyActions so both this drag-
                // drop and the AI go through one shared implementation of the capacity/orphan/AP
                // rules instead of two.
                if (!ArmyActions.TransferMember(card.Unit, _currentArmy, target, hexSelectionController, out string failReason))
                {
                    turnController?.ShowSpawnHint(failReason);
                    CancelReorderPreview();
                    return false;
                }

                ClearReorderPreview();

                // _currentArmy itself may have just lost its last member — actual deletion (see
                // DeleteArmyIfEmptied) is deferred until this modal closes, not done here, in
                // case the player isn't done reorganizing yet (see _pendingEmptyCheck).
                _pendingEmptyCheck.Add(_currentArmy);

                RefreshGrid();
                ShowArmySummary();
                return true;
            }

            // A live preview may have been produced earlier in the drag, but it only becomes a
            // real reorder when the pointer is released inside the grid. Releasing elsewhere
            // cancels that preview instead of silently committing its last hovered slot.
            List<UnitData> previewOrder = card == _draggingCard ? _dragPreviewOrder : null;
            if (previewOrder == null || !ResolveGridSlotIndex(screenPosition).HasValue)
            {
                CancelReorderPreview();
                return false;
            }

            ClearReorderPreview();
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
            if (x < 0f || y < 0f || x >= rect.width || y >= rect.height)
                return null;

            int columns = Mathf.Max(1, grid.constraintCount);
            int col = Mathf.Clamp(Mathf.FloorToInt(x / (grid.cellSize.x + grid.spacing.x)), 0, columns - 1);
            int row = Mathf.Max(0, Mathf.FloorToInt(y / (grid.cellSize.y + grid.spacing.y)));
            return row * columns + col;
        }

        // Owner is null for Neutral (see GameTurnController.ReplenishMoveForOwner's own
        // comment) and for a read-only view of an army whose owner hasn't loaded a deck —
        // callers fall back to null/empty in either case rather than guessing a faction.
        private FactionCardCatalog ResolveCatalog(Game.Players.PlayerSetupData owner) =>
            owner != null && startingDeckCatalog != null ? startingDeckCatalog.GetCatalog(owner.Faction) : null;

        private void RefreshTitle()
        {
            FactionCardCatalog catalog = _currentArmy != null ? ResolveCatalog(_currentArmy.Owner) : null;
            if (factionLogo != null)
            {
                factionLogo.sprite = catalog != null ? catalog.logo : null;
                factionLogo.gameObject.SetActive(factionLogo.sprite != null);
            }
            if (titleText != null)
            {
                string armyName = _currentArmy != null ? _currentArmy.Name : string.Empty;
                // Neutral has no faction catalog to speak of — say "Neutral" instead of reading
                // one, same as before.
                string prefix;
                if (_currentArmy != null && _currentArmy.Owner == null)
                {
                    prefix = "Neutral";
                }
                else
                {
                    string factionName = catalog != null ? catalog.displayName : string.Empty;
                    string playerName = _currentArmy?.Owner != null ? _currentArmy.Owner.Nickname : string.Empty;
                    prefix = string.Join(" — ", new[] { factionName, playerName }.Where(s => !string.IsNullOrEmpty(s)));
                }
                titleText.text = string.IsNullOrEmpty(armyName) ? prefix : $"{prefix} — <b>{armyName}</b>";
            }
            if (contextButtonLabel != null)
                contextButtonLabel.text = _currentArmy != null && (_currentArmy.IsGarrison || _currentArmy.IsAirfield)
                    ? $"Create Army ({ArmyActions.CreateArmyApCost} AP)" : "Rename";
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
            if (_snapshotSiblings != null)
            {
                armyButtonRow.Show(_snapshotSiblings, SwitchTo, _currentArmy);
                return;
            }
            // Prison then Airfield then Garrison, per the user's own spec.  The airfield appears
            // only once it actually stores aircraft; Garrison follows. Everyone else keeps ArmyRegistry's
            // own natural (registration) order, same as before this existed, rather than a full
            // sort that could otherwise reshuffle them unpredictably (List.Sort isn't stable).
            List<ArmyData> atHex = ArmyRegistry.AllAt(_currentArmy.Hex).FindAll(a => a.Owner == _currentArmy.Owner);
            var siblings = new List<ArmyData>();
            ArmyData prison = atHex.Find(a => a.IsPrison && a.Members.Count > 0);
            if (prison != null)
                siblings.Add(prison);
            ArmyData airfield = atHex.Find(a => a.IsAirfield);
            if (airfield != null)
                siblings.Add(airfield);
            ArmyData garrison = atHex.Find(a => a.IsGarrison);
            if (garrison != null)
                siblings.Add(garrison);
            foreach (ArmyData army in atHex)
                if (!army.IsPrison && !army.IsAirfield && !army.IsGarrison)
                    siblings.Add(army);
            armyButtonRow.Show(siblings, SwitchTo);
        }

        // One slot per point of EffectiveCapacity, not just one per actual Member — the empty
        // ones render as faint placeholders (see ArmyUnitCardUI.Setup) so it's obvious a card
        // can be dropped there, instead of the grid just looking randomly short of a full row.
        // EffectiveCapacity, not the raw Capacity: an over-cap roster (hand-authored, or a
        // hero that died leaving more survivors than the no-hero baseline) must still show
        // every member it actually has.
        private void RefreshGrid()
        {
            ClearGrid();
            if (gridContainer == null || gameConfig == null || gameConfig.armyUnitCardPrefab == null || _currentArmy == null)
                return;

            // Individual stealth (see Game.Map.StealthSystem): when inspecting SOMEONE ELSE's
            // army, only the members this viewer can actually see are shown — a still-hidden
            // member simply isn't in the roster. The owner always sees their own full roster
            // (hidden members included, flagged by ArmyUnitCardUI).
            List<UnitData> shown = _currentArmy.Members;
            if (_readOnly)
            {
                Game.Players.PlayerSetupData viewer = Game.Map.VisionSystem.CurrentViewer;
                shown = _currentArmy.Members.Where(m => !Game.Map.StealthSystem.IsHiddenFrom(m, viewer)).ToList();
            }

            int slots = System.Math.Max(_currentArmy.EffectiveCapacity, shown.Count);
            for (int i = 0; i < slots; i++)
            {
                UnitData member = i < shown.Count ? shown[i] : null;
                ArmyUnitCardUI card = Instantiate(gameConfig.armyUnitCardPrefab, gridContainer);
                card.Setup(this, member);
                card.SetSlot(SlotPosition(i), animated: false);
                _cards.Add(card);
            }
        }

        private void ClearGrid() => UIListUtility.DestroyAndClear(_cards);

        // Default detail-panel state — the army's own aggregate stats, shown whenever nothing
        // is selected (on open, on switching army, after a drag-and-drop move).
        // Experience/Battle Honors/Prestige are declined mechanics (see MECHANICS_CHECKLIST.md
        // pt. 10) and are omitted entirely rather than stubbed. Leader/capacity, Movement Range
        // and Fate Points are real, computed from
        // Members. Terrain/Construction Def mirror BattleParticipantColumnUI's own identical
        // lookup for the in-battle version of this same info — shown here too so the player can
        // see what this army's hex would give it before it's actually attacked.
        private void ShowArmySummary()
        {
            if (detailArt != null)
                detailArt.gameObject.SetActive(false);
            if (detailText == null || _currentArmy == null)
                return;

            UnitData hero = _currentArmy.Members.FirstOrDefault(m => m.IsHero);
            string leaderLine = hero != null
                ? $"{hero.Name} Commanding ({_currentArmy.EffectiveCapacity})"
                : $"No Hero Commanding ({_currentArmy.EffectiveCapacity})";

            int heroCount = hero != null ? 1 : 0;
            int unitCount = _currentArmy.Members.Count - heroCount;
            string membersLine = heroCount > 0
                ? $"1 Hero and {unitCount} Unit{(unitCount == 1 ? "" : "s")}"
                : $"{unitCount} Unit{(unitCount == 1 ? "" : "s")}";

            int fatePoints = hero != null ? hero.FateMax : 0;

            bool isAirArmy = AviationRules.IsAirArmy(_currentArmy);
            int terrainDefMod = 0;
            if (!isAirArmy && map != null && map.TryGetTerrainAt(_currentArmy.Hex, out TerrainTypeEntry terrain))
                terrainDefMod = terrain.defenseModifier;
            int buildingDefMod = 0;
            if (!isAirArmy && _currentArmy.VisualSnapshotConstructionDefense.HasValue)
                buildingDefMod = _currentArmy.VisualSnapshotConstructionDefense.Value;
            else
            {
                BuildingData building = BuildingRegistry.FindAt(_currentArmy.Hex);
                if (!isAirArmy && building != null && building.IsBase)
                    buildingDefMod = building.Defense;
            }

            detailText.text = $"{_currentArmy.Name}\n{leaderLine}\n{membersLine}\n" +
                $"{fatePoints} Fate Points\n" +
                $"Move: {_currentArmy.CurrentMovement}/{_currentArmy.MaxMovement}\n" +
                $"Terrain Def: {terrainDefMod:+0;-0;+0}\nConstruction Def: {buildingDefMod:+0;-0;+0}";
        }

        private void OnContextButtonClicked()
        {
            if (_currentArmy == null)
                return;

            if (_currentArmy.IsGarrison || _currentArmy.IsAirfield)
                CreateArmy();
            else
                renamePopup?.Show(_currentArmy, OnRenamed);
        }

        private void CreateArmy()
        {
            if (_currentArmy == null)
                return;
            FactionCardCatalog catalog = ResolveCatalog(_currentArmy.Owner);
            if (catalog == null)
                return;

            // Every army needs its own map marker (see Game.Map.ArmyController) — starts
            // invisible (RestackArmiesOn never shows an empty army) until units are dragged
            // into it from the garrison. Null only means "couldn't afford it" here — catalog/
            // _currentArmy are already guaranteed non-null above.
            ArmyData army = ArmyActions.CreateArmy(_currentArmy.Owner, _currentArmy.Hex, catalog, hexSelectionController);
            if (army == null)
            {
                turnController?.ShowSpawnHint("Not enough action points to create a new army.");
                return;
            }

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
