using System.Collections.Generic;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Styles;
using Game.Terrain;
using Game.Turns;
using Game.UI;
using Game.Units;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Game.Map
{
    // Click-to-select a hex on the combined map mesh: raycasts against the map's ground
    // collider, converts the hit point to a hex coordinate, and looks up that hex's terrain
    // from the HexMap data component. Once an army is selected, also drives the move-order
    // flow: hovering another hex previews the pathfinder's route and cost to it, right-click
    // commits that route as a move order.
    //
    // Split across 3 files by concern, purely for size — all share this same field block and
    // the layout/positioning helpers below (ResolveArmyOffset, RestackArmiesOn, etc.), which
    // live here since every part uses them:
    //   - HexSelectionController.cs (this file): fields, lifecycle, click/selection orchestration.
    //   - HexSelectionController.Factory.cs: spawning UnitData/ArmyData/BuildingData + their markers.
    //   - HexSelectionController.Movement.cs: hover path preview, move-order issuing.
    public partial class HexSelectionController : MonoBehaviour
    {
        [SerializeField] private Camera targetCamera;
        [SerializeField] private HexMap map;
        public HexMap Map => map;
        [SerializeField] private HexShaderHighlight highlight;
        [SerializeField] private HexInfoPanelUI infoPanel;
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private GameTurnController turnController;
        // Shown for one or more owned mobile armies; click selects an army for orders.
        [SerializeField] private ArmyButtonRowUI armyButtonRow;
        [SerializeField] private ArmyViewerModalUI armyViewerModal;
        [SerializeField] private BaseViewerModalUI baseViewerModal;
        // Opened by the Research / Production contextual hex actions once their eligibility check
        // passes (see OnResearchActionClicked / OnProductionActionClicked). Shares the same
        // "map input locked out while open" treatment as the two modals above (folded into
        // GameTurnController.InputBlocked via its own VisibilityChanged).
        [SerializeField] private ResearchProductionModalUI researchProductionModal;
        // The shared Challenge popup (same scene object BattleScreenUI / AviationCombatPresenter
        // use) — reused for the Research/Production Challenge via its own BeginResearchProduction
        // entry point. Wired in the scene.
        [SerializeField] private BattleAttackPopupUI attackPopup;
        // Shown the instant a move order makes contact with an enemy army (see
        // TryIssueMoveOrder) — "To Battle" opens battleScreen directly; "Delay" parks it in
        // DelayedBattleRegistry instead (see the manual's Delay Attack, GameTurnController
        // drains this at the next turn boundary).
        [SerializeField] private BattleContactPopupUI battleContactPopup;
        [SerializeField] private BattleScreenUI battleScreen;
        // Resolves AA reactions/air strikes as an army's own move steps land — see
        // HexSelectionController.Movement.cs's own IssueMoveOrder, which wires this into
        // ArmyController.MoveAlong's resolveStepAsync hook for every mover, air or ground alike.
        [SerializeField] private AviationCombatPresenter aviationCombatPresenter;
        // Public read accessor (2026-08-26 repeat-strike spec) — AiAggressionPlanner's own
        // RepeatAirStrikeRoutine needs to invoke ResolveAirStrikeAtCurrentHex directly (an AiTask's
        // multi-turn AirStrike loitering over its target, not a move step), the one case an AI
        // routine needs this presenter without an actual move happening first.
        public AviationCombatPresenter AviationCombatPresenter => aviationCombatPresenter;
        // Small usability margin in screen pixels around the marker's projected click circle
        // (MapObjectVisual.ContainsScreenPoint). That circle's radius scales with orthographic
        // zoom, so this never turns into the old fixed 30px invisible collider at long zoom;
        // keep it a few px so it doesn't undo the tightened hit area.
        [SerializeField] private float mapMarkerClickPadding = 3f;

        // AiTurnController.MoveArmyRoutine's own wait signal — a contact-triggered fight now
        // resolves immediately instead of deferring to end of turn (see Movement.cs's own
        // onComplete comment), but battleScreen.Show() only kicks the whole fight off
        // asynchronously from there (grid combat rounds, a Hex Event's own guard fight, a
        // Capture Kill Challenge — all still the SAME battleScreen instance, and
        // BattleScreenUI.IsShowing already folds the Capture Kill popup in too). A chained
        // second/third fight on the same hex is different: BattleScreenUI.Combat.cs's own
        // ResolveHexAfterVictory resets/hides battleScreen first and, when the survivor is
        // human, re-prompts through battleContactPopup instead — so that popup has to be
        // folded in here too, or this flips false while the player still hasn't answered
        // Fight/Delay on the next fight (the project owner's own report, 2026-08-24: the
        // battle-initiation popup for a chained fight glitched and the AI kept playing its
        // turn in the background).
        // Deliberately narrower than GameTurnController.InputBlocked, which also folds in
        // armyViewerModal — the AI's own MoveArmyRoutine keeps that open (read-only) for the
        // whole duration of its own move, so waiting on InputBlocked here would deadlock against
        // the AI's own debug visualization instead of the battle it's actually meant to wait for
        // (the project owner's own report, 2026-08-16: other AI armies kept acting while a fight
        // was still playing out on screen).
        // Shared combat presentation already owned by this controller; expose its
        // encounter-completion signal to the existing AI move-operation trace.
        public BattleScreenUI BattleScreen => battleScreen;
        internal bool HasProductionTransaction => _rpTransactionActive;
        public bool IsBattleActive =>
            _revealedArmyContacts.Count > 0 ||
            (battleScreen != null && battleScreen.IsShowing) ||
            (battleContactPopup != null && battleContactPopup.IsShowing);

        private readonly List<(ArmyData Army, HexCoord Hex)> _revealedArmyContacts
            = new List<(ArmyData Army, HexCoord Hex)>();

        // The army (if any) currently playing its selected-hover animation and eligible for
        // move orders — tracked so selecting a different hex stops it, instead of leaving it
        // animating (or receiving right-click orders) forever.
        private ArmyController _selectedArmy;
        private MoveArrowMarker _pathArrow;
        private HexCoord? _lastPreviewedHover;
        private int _lastPreviewedPathingVersion = -1;
        // The hex SelectHex last showed info for — kept around purely so a re-run can be
        // triggered without re-deriving it: the Army Viewer's Closed event fires after the
        // player creates/renames an army from inside the modal, and the button row/info panel
        // for whichever hex is still selected need to reflect that without the player having
        // to click the hex again (see OnArmyModalClosed). Also covers TryHandleArmyMarkerClick's
        // shortcut straight into the modal, which never runs SelectHex itself.
        private HexCoord? _selectedHex;
        // MAP-VIS-01: remembered-visual dictionaries, the visual-memory/layout method chunk that
        // reads/writes them, and RestackArmiesOn/ReconcileHexVisualState all now live in
        // HexSelectionController.Visuals.cs (same partial class, no public API change) — see that
        // file's own class-level comment.

        private void Reset()
        {
            targetCamera = Camera.main;
        }

        private void Start()
        {
            if (highlight != null)
            {
                highlight.SetColor(TechnicalColors.HexSelection);
                if (gameConfig != null)
                    highlight.ApplyStyle(gameConfig.hexSelectionStyle);
            }
        }

        private void OnEnable()
        {
            if (turnController != null)
            {
                turnController.TurnChanging += Deselect;
                turnController.TurnChanging += OnTurnChangingForVisualMemory;
                turnController.TurnStateChanged += OnTurnStateChangedForVisibility;
            }
            if (armyViewerModal != null)
                armyViewerModal.Closed += OnArmyModalClosed;
            if (baseViewerModal != null)
                baseViewerModal.Closed += OnBaseModalClosed;
            if (researchProductionModal != null)
            {
                researchProductionModal.CreateRequested += OnResearchProductionCreateRequested;
                researchProductionModal.VisibilityChanged += OnResearchProductionVisibilityChanged;
            }
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            VisionSystem.VisibleContentChanged += OnVisibleContentChanged;
            StealthSystem.StealthChanged += OnStealthChanged;
            StealthSystem.ResidentRevealed += OnResidentRevealed;
            BuildingRegistry.VisualStateChanged += OnBuildingVisualStateChanged;

            AviationActions.ReturnedWingDeletionRequested += DeleteReturnedWing;
            ArmyRegistry.ArmyRemoved += OnArmyRemoved;
            if (!ReferenceEquals(_selectedArmy, null) && !ArmyRegistry.IsRegistered(_selectedArmy.Data)) SetSelectedArmy(null);
            RefreshAllVisibility();
        }

        private void OnDisable()
        {
            CancelResearchProduction();
            if (turnController != null)
            {
                turnController.TurnChanging -= Deselect;
                turnController.TurnChanging -= OnTurnChangingForVisualMemory;
                turnController.TurnStateChanged -= OnTurnStateChangedForVisibility;
            }
            if (armyViewerModal != null)
                armyViewerModal.Closed -= OnArmyModalClosed;
            if (baseViewerModal != null)
                baseViewerModal.Closed -= OnBaseModalClosed;
            if (researchProductionModal != null)
            {
                researchProductionModal.CreateRequested -= OnResearchProductionCreateRequested;
                researchProductionModal.VisibilityChanged -= OnResearchProductionVisibilityChanged;
            }
            VisionSystem.VisibilityChanged -= OnVisibilityChanged;
            VisionSystem.VisibleContentChanged -= OnVisibleContentChanged;
            StealthSystem.StealthChanged -= OnStealthChanged;
            StealthSystem.ResidentRevealed -= OnResidentRevealed;
            _revealedArmyContacts.Clear();
            BuildingRegistry.VisualStateChanged -= OnBuildingVisualStateChanged;

            AviationActions.ReturnedWingDeletionRequested -= DeleteReturnedWing;
            ArmyRegistry.ArmyRemoved -= OnArmyRemoved;
            SetRememberedBuildingVisualsVisible(false);
            SetRememberedArmyVisualsVisible(false);
        }

        // The map is now (potentially) rendered from a different human's perspective — see
        // VisionSystem.CurrentViewer, set by GameTurnController whenever CurrentPlayer changes
        // to a human. Every marker's visibility needs re-checking against the new viewer, not
        // just whichever hexes happen to already have a RestackArmiesOn call scheduled.
        private void OnTurnStateChangedForVisibility()
        {
            PlayerSetupData viewer = VisionSystem.CurrentViewer;
            if (HumanVisualMemory.Tracks(viewer) && turnController != null && turnController.CurrentPlayer == viewer)
                RememberCurrentlyVisibleContent(viewer);
            RefreshAllVisibility();
        }

        private void OnTurnChangingForVisualMemory()
        {
            PlayerSetupData outgoing = turnController != null ? turnController.CurrentPlayer : null;
            // Every tracked viewer (an AI in an observer match too) forgets moving enemies at its
            // own turn end, exactly like a human — otherwise observer ghosts never expired.
            if (HumanVisualMemory.Tracks(outgoing))
            {
                HumanVisualMemory.EndTurn(outgoing);
                RemoveUnrememberedArmyVisuals(outgoing);
                RefreshAllVisibility();
            }
        }

        // Only the currently-rendered viewer's own vision changing is worth a refresh here — an
        // AI's vision recomputing off-screen (once real AI logic exists) touches none of what's
        // currently drawn.
        private void OnVisibilityChanged(PlayerSetupData player)
        {
            if (HumanVisualMemory.Tracks(player))
                RememberCurrentlyVisibleContent(player);
            if (player == VisionSystem.CurrentViewer)
                RefreshAllVisibility();
        }

        // A registry change on a hex which this player can already see must still update its
        // markers and remembered snapshot, but it must not make an unchanged FOW area look like
        // a full visibility rebuild to every subscriber.
        private void OnVisibleContentChanged(PlayerSetupData player, HexCoord hex)
        {
            if (HumanVisualMemory.Tracks(player))
                RememberCurrentlyVisibleContent(player);
            if (player == VisionSystem.CurrentViewer)
                RefreshAllVisibility();
        }

        // A hidden unit toggling stealth (or a personal detection lapsing) changes what the
        // current viewer can see with no vision-radius change — refresh markers + memory.
        private void OnStealthChanged()
        {
            PlayerSetupData viewer = VisionSystem.CurrentViewer;
            if (HumanVisualMemory.Tracks(viewer))
                RememberCurrentlyVisibleContent(viewer);
            RefreshAllVisibility();
        }

        private void OnResidentRevealed(ArmyData army)
        {
            var contact = (Army: army, Hex: army.Hex);
            if (!_revealedArmyContacts.Contains(contact))
                _revealedArmyContacts.Add(contact);
        }

        private void ResolveRevealedArmyContacts()
        {
            if (_revealedArmyContacts.Count == 0 || _rpTransactionActive
                || (turnController != null && turnController.IsCombatPresentationActive)
                || (battleScreen != null && battleScreen.IsShowing)
                || (battleContactPopup != null && battleContactPopup.IsShowing)
                || Game.UI.UIFocusUtility.IsGameplayInputBlocked)
                return;

            while (_revealedArmyContacts.Count > 0)
            {
                var contact = _revealedArmyContacts[0];
                _revealedArmyContacts.RemoveAt(0);
                ArmyData army = contact.Army;
                if (!ArmyRegistry.IsRegistered(army) || !army.Hex.Equals(contact.Hex)
                    || (army.Controller != null && army.Controller.IsMoving)
                    || !BattleInitiator.CanInitiateContact(army)
                    || DelayedBattleRegistry.IsHexPending(contact.Hex)
                    || BattleInitiator.FindDefenderOnArrival(contact.Hex, army) == null)
                    continue;

                // Finish the action which revealed the unit before opening the ordinary
                // arrival contact flow; it may have just refreshed/opened one of these modals.
                Deselect();
                TryBeginBattleAt(contact.Hex, army);
                return;
            }
        }

        // Moved to HexSelectionController.Visuals.cs — see that file.

        private void ObserveMovingArmyStep(ArmyData army, HexCoord from, HexCoord to, bool completed)
        {
            if (army == null || GameSession.Players == null)
                return;

            bool startsInCurrentView = false;
            foreach (PlayerSetupData viewer in GameSession.Players)
            {
                if (!HumanVisualMemory.Tracks(viewer) || viewer == army.Owner)
                    continue;
                bool observed = VisionSystem.IsVisible(viewer, from) || VisionSystem.IsVisible(viewer, to);
                if (!observed)
                    continue;
                startsInCurrentView |= viewer == VisionSystem.CurrentViewer && VisionSystem.IsVisible(viewer, from);
                if (!completed)
                    continue;
                HumanVisualMemory.ObserveArmy(viewer, army, to);
                UpdateRememberedArmyVisual(viewer, army);
            }

            if (completed)
                RefreshAllVisibility();
            // Same stealth rule as ReconcileHexVisualState: an enemy army fully hidden from the
            // current viewer shows no marker, even while walking through their vision.
            bool hiddenFromCurrentViewer = army.Owner != VisionSystem.CurrentViewer
                && StealthSystem.ArmyFullyHiddenFrom(army, VisionSystem.CurrentViewer);
            if (army.Controller != null && army.Controller.Visual != null)
            {
                if (!completed && startsInCurrentView && !hiddenFromCurrentViewer)
                    army.Controller.Visual.SetVisible(true);
                else if (completed)
                    army.Controller.Visual.SetVisible(VisionSystem.IsVisibleToCurrentViewer(to) && !hiddenFromCurrentViewer);
            }
        }

        // Moved to HexSelectionController.Visuals.cs — see that file.

        private void OnArmyRemoved(ArmyData army)
        {
            if (!ReferenceEquals(_selectedArmy, null) && ReferenceEquals(_selectedArmy.Data, army)) SetSelectedArmy(null);
        }

        private void OnBuildingVisualStateChanged(HexCoord hex, BuildingData building)
        {
            if (GameSession.Players == null)
                return;
            foreach (PlayerSetupData viewer in GameSession.Players)
            {
                // VisualStateChanged fires before the registry change recomputes away a
                // building's own vision. Anyone still present in this old visible set really
                // witnessed the transition and should remember the resulting state immediately.
                if (!HumanVisualMemory.Tracks(viewer) || !VisionSystem.IsVisible(viewer, hex))
                    continue;
                bool exists = building != null && building.Visual != null;
                HumanVisualMemory.ObserveBuilding(viewer, hex, exists);
                if (exists)
                    UpdateRememberedBuildingVisual(viewer, hex, building.Visual);
                else
                    RemoveRememberedBuildingVisual(viewer, hex);
            }
            RefreshAllVisibility();
        }

        // Moved to HexSelectionController.Visuals.cs — see that file.

        // The modal doesn't touch the hex-side button row or ArmyInfoPanel itself while it's
        // open (map input, this row's own hex, is locked out the whole time — see
        // IsInputAllowed) — so an army created/moved from inside it (e.g. Create Army on a
        // garrison that was the hex's only army) wouldn't show up here until the player clicked
        // the hex again. Re-running SelectHex on close picks that up immediately instead.
        // preserveSelection normally stays true here (don't clobber whatever the button row has
        // selected on a multi-army hex) — EXCEPT when the modal was reached via
        // TryHandleArmyMarkerClick's precise-click shortcut on a hex with exactly one
        // non-garrison army, which jumps straight to the modal without ever going through
        // SelectHex/SetSelectedArmy. Without this, that army was left unselected until the
        // player clicked the hex a second time — see ShouldPreserveSelectionAfterModalClose.
        private void OnArmyModalClosed()
        {
            if (turnController != null && turnController.IsAiObserverInspectionMode)
            {
                SetSelectedArmy(null);
                if (_selectedHex.HasValue)
                    SelectHex(_selectedHex.Value, preserveSelection: true);
                return;
            }

            // Only the current player can ever open their own army/garrison modal in the first
            // place (see IsInputAllowed) — a member gaining/losing an r1sX Recce tag in there
            // changes that army's own vision radius (see Game.Cards.AbilityParams /
            // VisionSystem.RecomputeFor), which nothing else would
            // otherwise notice: adding/removing a unit doesn't re-register the army itself
            // (see ArmyRegistry.Register/Unregister), so vision never recomputes on its own here.
            VisionSystem.RecomputeFor(turnController?.CurrentPlayer);

            if (!_selectedHex.HasValue)
                return;

            HexCoord hex = _selectedHex.Value;
            ArmyData lastViewedArmy = armyViewerModal != null ? armyViewerModal.LastClosedSelectableArmy : null;
            if (lastViewedArmy != null && lastViewedArmy.Hex.Equals(hex)
                && ArmyRegistry.AllAt(hex).Contains(lastViewedArmy))
            {
                SelectHex(hex, preserveSelection: true);
                SelectArmyForOrders(lastViewedArmy);
                return;
            }
            SetSelectedArmy(null);
            SelectHex(hex, preserveSelection: true);
        }

        // Same reasoning as OnArmyModalClosed — an Upgrade purchased inside the Base modal
        // changes UnlockedFacilitySlots/Defense, which the hex-side info panel
        // doesn't otherwise learn about until the player re-clicks the hex.
        private void OnBaseModalClosed()
        {
            if (_selectedHex.HasValue)
                SelectHex(_selectedHex.Value, preserveSelection: true);
        }

        private void Update()
        {
            ResolveRevealedArmyContacts();
            if (targetCamera == null || map == null || gameConfig == null)
                return;

            // Hex selection is only ever the local human's to drive — allowed during citadel
            // placement (before the turn system starts) and on the human's own turn, blocked
            // during the tokens-off and any AI/Neutral turn.
            if (!IsInputAllowed())
                return;

            // Don't act through a UI click/hover (e.g. the info panel itself). Hovering a
            // button (e.g. the ArmyButtonRow) must also drop whatever move-arrow preview was
            // already showing from the map underneath it — otherwise it just freezes in place
            // (this early-out skips UpdateMovePreview entirely, so nothing would ever clear it),
            // reading as "an order will be given" while poised over a button that does something
            // else entirely.
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                if (_lastPreviewedHover.HasValue)
                {
                    _lastPreviewedHover = null;
                    HidePathPreview();
                }
                return;
            }

            HexCoord? hoverCoord = RaycastHexCached();

            bool observerInspection = turnController != null && turnController.IsAiObserverInspectionMode;
            if (!observerInspection)
            {
                UpdateMovePreview(hoverCoord);
                if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame && hoverCoord.HasValue)
                    TryIssueMoveOrder(hoverCoord.Value);
            }
            else if (_lastPreviewedHover.HasValue)
            {
                _lastPreviewedHover = null;
                HidePathPreview();
            }

            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                HandleLeftClick(hoverCoord);
        }

        // TurnNumber is still 0 before GameTurnController.BeginGame() runs — i.e. citadel
        // placement is still in progress. Once it's started, only a human's own turn (not the
        // tokens-off, not an AI/Neutral turn, both of which leave CurrentPlayer without a human
        // owner) lets the player touch the map — and even then, only after they've dismissed
        // popupPanel's Confirm button (TurnConfirmed), not the instant it becomes their turn.
        private bool IsInputAllowed()
        {
            if (Game.UI.UIFocusUtility.IsGameplayInputBlocked) return false;
            if (turnController == null)
                return true;
            if (turnController.TurnNumber == 0)
                return true;
            // turnController.InputBlocked already folds in armyViewerModal.IsShowing (see
            // GameTurnController), so map input is locked out while the modal is open without
            // needing a second check here.
            if (turnController.IsAiObserverInspectionMode)
                return !turnController.InputBlocked;
            return turnController.CurrentPlayer != null && turnController.CurrentPlayer.IsHuman
                && turnController.TurnConfirmed && !turnController.InputBlocked;
        }

        // Turn-based game, mostly-idle camera: re-running RaycastHex every single frame
        // regardless of whether anything that could change its result actually moved was pure
        // waste (see RaycastHex's own per-call cost — a plane intersection plus a
        // TryGetTerrainAt lookup, not a cheap property read). Cached against the 3 inputs that can change
        // which hex is under the cursor: the mouse's screen position, and the camera's own
        // position/zoom (RtsCameraController's WASD pan and scroll-zoom can move what's under a
        // STATIONARY cursor) — camera rotation is deliberately not checked, this project's
        // camera is fixed-angle for its whole lifetime (see RtsCameraController's own comment).
        private HexCoord? _cachedHoverCoord;
        private Vector2 _lastHoverMousePos;
        private Vector3 _lastHoverCameraPos;
        private float _lastHoverOrthoSize;
        private bool _hasCachedHover;

        private HexCoord? RaycastHexCached()
        {
            if (Mouse.current == null || targetCamera == null)
                return null;

            Vector2 mousePos = Mouse.current.position.ReadValue();
            Vector3 camPos = targetCamera.transform.position;
            float orthoSize = targetCamera.orthographicSize;

            if (_hasCachedHover && mousePos == _lastHoverMousePos
                && camPos == _lastHoverCameraPos && orthoSize == _lastHoverOrthoSize)
                return _cachedHoverCoord;

            _lastHoverMousePos = mousePos;
            _lastHoverCameraPos = camPos;
            _lastHoverOrthoSize = orthoSize;
            _hasCachedHover = true;
            _cachedHoverCoord = RaycastHex(mousePos);
            return _cachedHoverCoord;
        }

        // Screen-position overload — used by this controller's own mouse hover above, and also
        // by CardHandUI to figure out which hex a dragged card was dropped on (a card drop
        // reports its own screen position via PointerEventData, not the mouse singleton).
        public HexCoord? RaycastHex(Vector2 screenPosition)
        {
            if (targetCamera == null || map == null)
                return null;

            Ray ray = targetCamera.ScreenPointToRay(screenPosition);
            // The map is flat at Y=0 (see GameConfig's own "every scene element sits flat at
            // Y=0" comment) — a math plane intersection instead of Physics.Raycast against a
            // baked Ground collider, so hex-picking can't silently break if that collider's
            // ever missing/disabled/not regenerated (see CitadelSetupController.Update's
            // identical intersection for the citadel-placement step).
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter))
                return null;

            HexCoord coord = map.WorldToHex(ray.GetPoint(enter));
            return map.TryGetTerrainAt(coord, out _) ? coord : (HexCoord?)null;
        }

        private void HandleLeftClick(HexCoord? hoverCoord)
        {
            if (!hoverCoord.HasValue)
            {
                Deselect();
                return;
            }

            HexInteracted?.Invoke(hoverCoord.Value);

            if (Mouse.current != null)
            {
                Vector2 screenPosition = Mouse.current.position.ReadValue();
                if (TryHandleArmyMarkerClick(hoverCoord.Value, screenPosition)
                    || TryHandleBuildingMarkerClick(hoverCoord.Value, screenPosition))
                    return;
            }

            SelectHex(hoverCoord.Value);
        }

        // A precise click on the human's own army marker (not just anywhere on its hex) is a
        // shortcut straight to that marker's modal — Barracks hex always means the garrison;
        // otherwise, if there's exactly one of the human's own (non-garrison) armies there,
        // straight to that one. No Collider on the marker — this is a screen-space distance
        // check against its projected position instead, which is simpler than adding one just
        // for this. Ambiguous cases (2+ armies, or the click landed too far from the marker)
        // fall through to the normal SelectHex flow unchanged. Failing that, the same precise
        // click is tried against every OTHER owner's visible marker on the hex too (see
        // TryHandleEnemyArmyMarkerClick) — inspecting an enemy army read-only.
        private bool TryHandleArmyMarkerClick(HexCoord hex, Vector2 screenPosition)
        {
            if (armyViewerModal == null || targetCamera == null || turnController == null)
                return false;

            if (turnController.IsAiObserverInspectionMode)
            {
                foreach (ArmyData representative in ArmyRegistry.AllAt(hex))
                {
                    if (representative?.Owner == null || representative.Controller?.Visual == null
                        || !representative.Controller.Visual.IsVisible
                        || !IsMarkerHit(representative.Controller.Visual, screenPosition))
                        continue;
                    armyButtonRow?.Hide();
                    ShowArmyModalReadOnly(representative);
                    return true;
                }
                return false;
            }

            PlayerSetupData human = turnController.CurrentPlayer;
            if (human == null || !human.IsHuman)
                return false;

            // The VISIBLE one specifically (see RestackArmiesOn) — with 2+ of the human's own
            // armies sharing the hex, only one of them actually sits where the player is
            // clicking; picking whichever was simply registered first (the earlier version of
            // this) could easily hit-test against a different, currently-hidden army's stale
            // transform instead, silently failing the distance check below.
            ArmyData ownRepresentative = null;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army.Owner == human && army.Controller != null && army.Controller.Visual != null && army.Controller.Visual.IsVisible)
                { ownRepresentative = army; break; }

            if (ownRepresentative != null && IsMarkerHit(ownRepresentative.Controller.Visual, screenPosition))
            {
                List<ArmyData> ownArmies = ArmyRegistry.AllAt(hex).FindAll(a => a.Owner == human);
                ArmyData target = ResolveArmyMarkerTarget(ownArmies, GetSelectedArmy());
                if (target == null)
                    return false;
                if (ownRepresentative != target) ArmyInteracted?.Invoke(ownRepresentative);
                ShowArmyModal(target);
                return true;
            }

            return TryHandleEnemyArmyMarkerClick(hex, screenPosition, human);
        }

        private static ArmyData ResolveArmyMarkerTarget(List<ArmyData> armies, ArmyData selected)
        {
            if (selected != null && !selected.IsGarrison && !selected.IsAirfield && !selected.IsPrison
                && armies.Contains(selected))
                return selected;
            return armies.Find(a => a.IsGarrison)
                ?? armies.Find(a => !a.IsGarrison && !a.IsAirfield && !a.IsPrison);
        }

        // Same precise-click shortcut as the human's own marker above, for every other owner's
        // army sharing the hex — opens the exact same modal, but read-only (see
        // ArmyViewerModalUI.ShowReadOnly), so the player can inspect an enemy's roster without
        // being able to touch it. Only ever one visible marker per owner at a time (see
        // RestackArmiesOn), so at most one candidate can ever match here regardless of how many
        // armies that owner actually has on this hex — the read-only modal's own button row
        // (filtered to that same owner) is how the rest become reachable.
        private bool TryHandleEnemyArmyMarkerClick(HexCoord hex, Vector2 screenPosition, PlayerSetupData human)
        {
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner == human || army.Owner == null || army.Controller == null || army.Controller.Visual == null || !army.Controller.Visual.IsVisible)
                    continue;

                if (!IsMarkerHit(army.Controller.Visual, screenPosition))
                    continue;

                ShowArmyModalReadOnly(army);
                return true;
            }
            if (_rememberedArmyVisualsByHex.TryGetValue(human,
                out Dictionary<HexCoord, List<RememberedArmyVisual>> rememberedByHex)
                && rememberedByHex.TryGetValue(hex, out List<RememberedArmyVisual> rememberedAtHex))
                foreach (RememberedArmyVisual remembered in rememberedAtHex)
                {
                    if (remembered.Visual == null || !remembered.Visual.IsVisible)
                        continue;
                    if (!IsMarkerHit(remembered.Visual, screenPosition))
                        continue;
                    var siblings = new List<ArmyData>();
                    foreach (RememberedArmyVisual candidate in rememberedAtHex)
                        if (candidate.Snapshot != null && candidate.Snapshot.Owner == remembered.Snapshot.Owner)
                            siblings.Add(candidate.Snapshot);
                    if (baseViewerModal != null) baseViewerModal.Hide();
                    if (researchProductionModal != null) researchProductionModal.Hide();
                    SelectHex(hex);
                    armyButtonRow?.Hide();
                    armyViewerModal.ShowLastSeen(remembered.Snapshot, siblings);
                    return true;
                }
            return false;
        }

        private bool TryHandleBuildingMarkerClick(HexCoord hex, Vector2 screenPosition)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null && IsMarkerHit(building.Visual, screenPosition))
            {
                SelectHex(hex);
                if (turnController != null && turnController.IsAiObserverInspectionMode)
                {
                    if (building.IsBase || !building.HasTieredUnlock)
                        ShowBaseModalReadOnly(building);
                    return true;
                }

                PlayerSetupData human = turnController != null ? turnController.CurrentPlayer : null;
                // Citadel/Base and the hero-built extraction Facility share BaseViewerModalUI.
                // Foreign buildings remain inspectable only through the ordinary hex info,
                // exactly as before; clicking their visible marker simply selects that hex.
                if (human != null && building.Owner == human
                    && (building.IsBase || !building.HasTieredUnlock))
                    ShowBaseModal(building);
                return true;
            }

            PlayerSetupData viewer = VisionSystem.CurrentViewer;
            if (viewer != null
                && _rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> visuals)
                && visuals.TryGetValue(hex, out MapObjectVisual remembered)
                && IsMarkerHit(remembered, screenPosition))
            {
                // Never open a modal from the live registry for a last-seen building: it may
                // already have changed or been destroyed behind fog. SelectHex knows to render
                // only HumanVisualMemory's safe remembered information here.
                SelectHex(hex);
                return true;
            }
            return false;
        }

        private bool IsMarkerHit(MapObjectVisual visual, Vector2 screenPosition)
        {
            return visual != null
                && visual.ContainsScreenPoint(targetCamera, screenPosition, mapMarkerClickPadding);
        }

        // Shows the highlight ring + info panels for `coord` and selects whatever army (if
        // any) stands there — the guts of a hex click, factored out so an army's move order can
        // also re-run it on arrival (see TryIssueMoveOrder) and have the selection visibly
        // follow it to where it actually stopped, instead of staying behind on the hex it
        // was originally clicked from. `preserveSelection` is set by that follow-up call —
        // the destination hex may itself have 2+ armies (a fresh click there would show the
        // button row with nothing picked yet), but the army that just finished moving here
        // should stay selected rather than being reset to "none".
        private void SelectHex(HexCoord coord, bool preserveSelection = false)
        {
            TutorialHintController.Invalidate();
            _selectedHex = coord;
            map.TryGetTerrainAt(coord, out TerrainTypeEntry entry);

            Vector3 hexCenter = map.HexToWorld(coord);
            if (highlight != null)
                highlight.ShowAt(hexCenter, map.OuterRadius);

            BuildingData buildingHere = BuildingRegistry.FindAt(coord);
            PlayerSetupData owner = buildingHere?.Owner;
            bool observerInspection = turnController != null && turnController.IsAiObserverInspectionMode;
            // The bonus (if any) belongs permanently to the hex itself — stamped once when a
            // citadel was placed there (see HexResourceBonusRegistry), independent of whatever
            // building currently stands on it.
            ResourceYields effectiveYields = HexResourceBonusRegistry.EffectiveYield(entry, coord);
            if (infoPanel != null)
            {
                bool isOwn = owner != null && owner == turnController?.CurrentPlayer;

                // Independent of how many armies share this hex (that's the button-row/
                // unit-info split below) — a direct way to reach the garrison even when it's
                // the only army here, which is otherwise unreachable (see the Army-only-
                // movement plan: a lone unit sits in the garrison, which can't move, until
                // it's sorted into a real army from this modal).
                bool isOwnBarracks = isOwn && buildingHere.HasAbility(UnitAbilities.Barracks);
                bool observerBarracks = observerInspection && owner != null && buildingHere != null
                    && buildingHere.HasAbility(UnitAbilities.Barracks);
                ArmyData garrisonForButton = (isOwnBarracks || observerBarracks)
                    ? ArmyRegistry.FindGarrisonAt(coord, owner) : null;
                infoPanel.SetGarrisonButtonVisible(garrisonForButton != null,
                    () =>
                    {
                        if (observerInspection) ShowArmyModalReadOnly(garrisonForButton);
                        else ShowArmyModal(garrisonForButton);
                    });

                // Same idea, for BaseViewerModalUI — any building with IsBase set (the citadel
                // always has it, see CitadelSetupController; so does anything built from a
                // CardType.Base card, see SpawnBuilding) OR a hero-built resource site (see
                // TryBuildExtractionFacility, identified by HasTieredUnlock=false rather than a
                // separate tag) — both use the exact same modal.
                bool isOwnBase = isOwn && (buildingHere.IsBase || !buildingHere.HasTieredUnlock);
                bool observerBase = observerInspection && buildingHere != null
                    && (buildingHere.IsBase || !buildingHere.HasTieredUnlock);
                BuildingData baseForButton = (isOwnBase || observerBase) ? buildingHere : null;
                infoPanel.SetBaseButtonVisible(baseForButton != null,
                    () =>
                    {
                        if (observerInspection) ShowBaseModalReadOnly(baseForButton);
                        else ShowBaseModal(baseForButton);
                    });

                // Same idea, for Research/Production — used to be separate entries on
                // the extractor action list (RefreshResourceActionRow below), now their own fixed
                // buttons on this nav row. Eligibility itself is unchanged: ResearchProductionSystem.IsEligible.
                bool researchEligible = !observerInspection
                    && ResearchProductionSystem.IsEligible(turnController?.CurrentPlayer, coord, ResearchProductionMode.Research, out _);
                infoPanel.SetResearchButtonVisible(researchEligible, () => OnResearchActionClicked(coord));

                bool productionEligible = !observerInspection
                    && ResearchProductionSystem.IsEligible(turnController?.CurrentPlayer, coord, ResearchProductionMode.Production, out _);
                infoPanel.SetProductionButtonVisible(productionEligible, () => OnProductionActionClicked(coord));
            }

            if (observerInspection)
                infoPanel?.SetResourceActions(null, null);
            else
                RefreshResourceActionRow(coord);

            // The panel shows exactly the actions offered above, and hides itself when there are none.
            // Resource yield alone is not an available construction action.
            infoPanel?.ShowHex();

            // One button per mobile army. A lone garrison shows neither: it can't move and isn't "an army"
            // for this purpose (see the garrison button on HexInfoPanelUI for how to actually
            // reach it). Only the current player's OWN armies ever show here — an enemy army
            // sharing the hex is what the battle trigger (see Game.Combat.BattleInitiator) is
            // for, not something to select/command from this panel.
            List<ArmyData> armies = ArmyRegistry.AllAt(coord).FindAll(a => a.Owner == turnController?.CurrentPlayer);
            List<ArmyData> mobileArmies = armies.FindAll(a => !a.IsGarrison && !a.IsAirfield && !a.IsPrison
                && a.Members.Count > 0);
            ArmyData soleArmy = mobileArmies.Count == 1 ? mobileArmies[0] : null;

            if (observerInspection)
            {
                SetSelectedArmy(null);
                armyButtonRow?.Hide();
                return;
            }

            if (!preserveSelection)
                SetSelectedArmy(soleArmy?.Controller);
            RefreshArmyButtonRow(armies);
            if (preserveSelection)
                return;
            // RestackArmiesOn's own representative-for-owner pick (see its own comment) only
            // updates when it actually runs — a plain hex click never ran it before, so
            // whichever army was left visible from the last time it DID run (spawn, or a
            // previous move elsewhere) could easily not be `soleArmy` any more (e.g. after
            // dragging a unit into a different army from ArmyViewerModalUI). SetSelected's pulse
            // would then be playing on a marker whose sprites are hidden — running this now is
            // what makes the visible marker match whoever was just actually selected.
            if (soleArmy != null)
                RestackArmiesOn(coord, null);
        }

        // Split out of SelectHex so SelectArmyForOrders can refresh just the button row (to
        // show the newly picked army as selected) without re-running the rest of SelectHex —
        // which would otherwise clobber _selectedArmy right back via its own sole-army lookup.
        private void RefreshArmyButtonRow(List<ArmyData> armies)
        {
            if (armyButtonRow == null)
                return;
            // Prison is only ever reachable from inside ArmyViewerModalUI's own in-modal switcher
            // (see its RefreshButtonRow) — never selectable for a move order from here, and never
            // worth a button of its own on the hex-side row at all.
            armies = armies.FindAll(a => a.Owner == turnController?.CurrentPlayer
                && !a.IsPrison && !a.IsAirfield && !a.IsGarrison
                && a.Members.Count > 0);
            // Show even a single MOBILE army (garrison/airfield/prison
            // don't count), and never over an open army/base modal — both re-run SelectHex on
            // close, which re-evaluates this.
            int mobileArmies = armies.Count;
            bool modalShowing = (armyViewerModal != null && armyViewerModal.IsShowing)
                || (baseViewerModal != null && baseViewerModal.IsShowing);
            if (mobileArmies >= 1 && !modalShowing)
                armyButtonRow.Show(armies, OnArmyButtonClicked, GetSelectedArmy(), showStats: true,
                    presentationContext: _selectedHex);
            else
                armyButtonRow.Hide();
        }

        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        // UI selects configured definitions and retains turn/contact interaction guards.
        // Extraction legality and marginal income come from the same query as execution.
        // Unaffordable legal actions remain visible so clicking can explain their cost.
        private void RefreshResourceActionRow(HexCoord coord)
        {
            PlayerSetupData human = turnController?.CurrentPlayer;
            var actions = new List<HexActionDescriptor>();
            if (human != null && human.IsHuman && gameConfig != null && gameConfig.extractionFacilityCards != null
                && BattleInitiator.FindEnemyAt(coord, human) == null)
            {
                foreach (ResourceType type in AllResourceTypes)
                {
                    int index = (int)type;
                    CardDefinition definition = index < gameConfig.extractionFacilityCards.Length ? gameConfig.extractionFacilityCards[index] : null;
                    if (!InfrastructureActions.CanBuildExtractionSite(map, gameConfig, definition, coord, human,
                            out _, out _, requireCurrentBudget: false)) continue;
                    CardDefinition captured = definition;
                    actions.Add(new HexActionDescriptor(definition.displayName,
                        () => TryBuildExtractionFacility(captured, coord, human), captured));
                }
            }
            infoPanel?.SetResourceActions(actions, gameConfig != null ? gameConfig.resourceActionButtonPrefab : null);
        }

        // Eligibility (ResearchProductionSystem.IsEligible: on the human's own turn, an OWN
        // building on `coord` holds a Facility granting the capability — UnitAbilities.Research /
        // Production, never a Lab/Factory name or card id — no combat-capable enemy stands on the
        // hex, and one of this player's own eligible Heroes with the matching role ability
        // (Researcher / Assembler) is present in an army on this exact hex, garrison included) is
        // checked directly in SelectHex now, same spot as the Garrison/Base buttons. The two are
        // fully symmetric and can appear together; a single Hero carrying both role abilities, or
        // two different own armies each carrying one, satisfies both.
        //
        // Eligibility is re-checked here rather than trusted from the button's mere existence —
        // the hex contents could have changed between render and click. On success, the shared
        // ResearchProductionModalUI opens in the matching mode with the qualifying Hero; the
        // catalog list, faction filtering, pagination and the (still inert) Create button all
        // live inside the modal. The Research/Production action itself is the next milestone.
        internal bool CanOfferBuildTutorial(CardData card, PlayerSetupData owner, PlayerRoot root)
        {
            if (!_selectedHex.HasValue || card?.Definition == null || root == null) return false;
            var hex = _selectedHex.Value;
            return card.Definition.cardType == CardType.Base
                ? InfrastructureActions.CanFoundBase(card.Definition, hex, owner,
                    card.EffectivePlayApCost, card.EffectivePlayResourceCost, out _, map)
                : card.Definition.cardType == CardType.Facility
                    && InfrastructureActions.CanPlaceFacility(card.Definition, hex, owner,
                        card.EffectivePlayApCost, card.EffectivePlayResourceCost, out _);
        }

        internal void RefreshContextTutorials()
        {
            TutorialHintController.Withdraw(TutorialHintId.MutagenProduction);
            TutorialHintController.Withdraw(TutorialHintId.EquipmentProduction);
            if (!TutorialHintController.MapInteractionReady || !_selectedHex.HasValue || infoPanel == null) return;
            var hex = _selectedHex.Value;
            foreach (var mode in new[] { ResearchProductionMode.Research, ResearchProductionMode.Production })
            {
                var id = mode == ResearchProductionMode.Research
                    ? TutorialHintId.MutagenProduction : TutorialHintId.EquipmentProduction;
                if (TutorialHintProgress.HasSeen(id) || !CanOfferFacilityTutorial(hex, mode)) continue;
                TutorialHintController.Offer(id, infoPanel.TutorialFacilityAnchor(mode == ResearchProductionMode.Research),
                    () => this != null && TutorialHintController.MapInteractionReady
                        && _selectedHex.HasValue && _selectedHex.Value.Equals(hex)
                        && CanOfferFacilityTutorial(hex, mode));
            }
        }

        private bool CanOfferFacilityTutorial(HexCoord hex, ResearchProductionMode mode)
        {
            var human = GameSession.FindHumanPlayer();
            var building = BuildingRegistry.FindAt(hex);
            return human != null && building != null && building.Owner == human
                && building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode))
                && BattleInitiator.FindEnemyAt(hex, human) == null;
        }

        private void OnResearchActionClicked(HexCoord hex)
        {
            OpenResearchProductionModal(hex, ResearchProductionMode.Research);
        }

        private void OnProductionActionClicked(HexCoord hex)
        {
            OpenResearchProductionModal(hex, ResearchProductionMode.Production);
        }

        // --- active Research/Production transaction context (spec §13) -----------------------
        // Captured when the modal opens, re-validated (not trusted) when Create is pressed. The
        // mode alone identifies the rule set now — ResearchProductionSystem derives the Facility/
        // role abilities from it, so the two ability strings this used to also stash are gone.
        private ProductionAttempt _rpAttempt;
        private bool _rpTransactionActive;
        private HexCoord _rpHex;
        private ResearchProductionMode _rpMode;
        private UnitData _rpHero;

        private void CancelResearchProduction()
        {
            var attempt = _rpAttempt;
            bool active = _rpTransactionActive || attempt != null;
            _rpAttempt = null; _rpTransactionActive = false;
            attempt?.Dispose();
            if (!active) return;
            // Detach before popup teardown invokes its old completion. That callback cannot
            // mint, unlock a replacement attempt, or leave this cancelled picker busy.
            try { attackPopup?.Hide(); }
            finally { researchProductionModal?.SetBusy(false); }
        }

        private void OnResearchProductionVisibilityChanged()
        {
            if (researchProductionModal.IsShowing || !_rpTransactionActive) return;
            CancelResearchProduction();
        }

        private void OpenResearchProductionModal(HexCoord hex, ResearchProductionMode mode)
        {
            PlayerSetupData human = turnController?.CurrentPlayer;
            if (human == null || !human.IsHuman)
                return;
            if (!ResearchProductionSystem.IsEligible(human, hex, mode, out _))
                return;

            UnitData hero = ResearchProductionSystem.FindActor(human, hex, mode);
            if (hero == null)
                return;

            // Choosing Research reveals the participating Researcher, here and now — it's the
            // cost/consequence of deciding to Research, not of Create and not of the Challenge.
            // Order: eligibility validated -> exact Researcher found -> reveal -> store _rpHero
            // -> open modal. Only THIS hero loses Stealth (other hidden Researchers on the hex
            // stay hidden); a hidden hero still passes eligibility and still shows the Research
            // button (FindActor never filtered on IsHidden). Never rolled back: closing the modal
            // without Create, insufficient AP/resources, or a lost/cancelled Challenge all leave
            // the hero revealed. Hand capacity does not block produced output. Production is
            // intentionally not included in this rule.
            ResearchProductionSystem.ApplyResearchReveal(mode, hero);

            _rpTransactionActive = false;
            _rpHex = hex;
            _rpMode = mode;
            _rpHero = hero;

            // The three hex-side modals are mutually exclusive — same funnelling as
            // ShowArmyModal / ShowBaseModal.
            if (armyViewerModal != null) armyViewerModal.Hide();
            if (baseViewerModal != null) baseViewerModal.Hide();
            armyButtonRow?.Hide();
            researchProductionModal?.Show(mode, human, hero, ResolveFactionLogo(human));
            if (researchProductionModal != null && researchProductionModal.IsShowing)
                TutorialHintController.Learned(mode == ResearchProductionMode.Research
                    ? TutorialHintId.MutagenProduction : TutorialHintId.EquipmentProduction);
        }

        // Create pressed in the modal (spec §13). Everything here is re-validated against the
        // live world — the hex contents can change between opening the modal and clicking Create,
        // and the Challenge MUST be run by the exact Hero the modal is showing, not merely "some
        // Researcher/Assembler now on the hex". The card's AP + ResourceCost are paid immediately
        // before the Challenge starts and are NOT refunded on a loss.
        private void OnResearchProductionCreateRequested(CardDefinition card)
        {
            // 1. Anti-double-click.
            if (_rpTransactionActive)
                return;

            // 3. Popup dependencies (checked early, before anything is spent).
            if (card == null || researchProductionModal == null || !researchProductionModal.IsShowing
                || attackPopup == null || cardHandUI == null)
                return;

            PlayerSetupData human = turnController?.CurrentPlayer;
            if (human == null || !human.IsHuman)
                return;
            PlayerRoot root = PlayerRootRegistry.FindFor(human);

            // Gameplay revalidation + canonical account + catalog + actor + affordability +
            // irreversible payment are one shared transaction with the AI headless path.
            // Produced cards may intentionally overflow the hand cap, so capacity is not part of
            // this attempt-start transaction.
            if (!ResearchProductionSystem.TryStartAttempt(human, root, _rpHero, _rpHex, _rpMode,
                    card, researchProductionModal.Catalog, out string startFail, out _rpAttempt))
            {
                turnController?.ShowSpawnHint(startFail);
                return;
            }

            // An account observer may have disabled this presenter during the completed debit.
            if (_rpAttempt == null) return;
            // Lock the presentation only after the shared transaction commits.
            _rpTransactionActive = true;
            TutorialHintController.Learned(TutorialHintId.AttemptCost);
            researchProductionModal.SetBusy(true);

            // Start the animated Challenge through its dedicated presentation entry point.
            Sprite logo = ResolveFactionLogo(human);
            var startedAttempt = _rpAttempt;
            try { attackPopup.BeginResearchProduction(_rpHero, card, _rpMode, logo, card.art,
                success => OnResearchProductionResolved(startedAttempt, success)); }
            catch { CancelResearchProduction(); throw; }
        }

        private Sprite ResolveFactionLogo(PlayerSetupData player) =>
            player != null && cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(player.Faction)?.logo
                : null;

        // 9/10. Challenge finished (the session never spent live hero Fate). On success mint
        // a produced CardData and hand it over; on failure do nothing (AP/resources stay spent).
        // Either way the modal reopens for interaction and stays open — the player closes it.
        private void OnResearchProductionResolved(ProductionAttempt attempt, bool success)
        {
            if (!ReferenceEquals(_rpAttempt, attempt)) { attempt?.Dispose(); return; }
            _rpAttempt = null;
            _rpTransactionActive = false;

            try { attempt?.CompleteToHand(success); }
            finally { researchProductionModal?.SetBusy(false); }
        }

        // Re-runs the full SelectHex pipeline for `changedHex` ONLY if it's the hex currently
        // selected — so a direct card deploy that can change the contextual hex actions (a Lab
        // onto the selected Base, a Researcher into its garrison) shows the new Research/
        // Production button immediately instead of waiting for the next hex re-click. No
        // separate "is the button visible" flag is kept anywhere; SelectHex ->
        // RefreshResourceActionRow stays the single source of truth.
        public void RefreshSelectedHexIf(HexCoord changedHex)
        {
            if (_selectedHex.HasValue && _selectedHex.Value.Equals(changedHex))
                SelectHex(_selectedHex.Value, preserveSelection: true);
        }

        // The two hex-side modals are mutually exclusive — only one of them makes sense open at
        // once (both key off the same selected hex's own building/garrison), so every place that
        // opens one closes the other first, funnelled through these two rather than repeating the
        // Hide-then-Show pairing at each call site.
        public event System.Action<ArmyData> ArmyInteracted;
        // Actual map clicks only; SelectHex also runs during automatic UI refreshes.
        public event System.Action<HexCoord> HexInteracted;

        private void ShowArmyModal(ArmyData army)
        {
            ArmyInteracted?.Invoke(army);
            if (baseViewerModal != null)
                baseViewerModal.Hide();
            if (researchProductionModal != null)
                researchProductionModal.Hide();
            SelectHex(army.Hex);
            armyButtonRow?.Hide();
            armyViewerModal?.Show(army);
        }

        private void ShowArmyModalReadOnly(ArmyData army)
        {
            ArmyInteracted?.Invoke(army);
            if (baseViewerModal != null)
                baseViewerModal.Hide();
            if (researchProductionModal != null)
                researchProductionModal.Hide();
            SelectHex(army.Hex);
            armyButtonRow?.Hide();
            armyViewerModal?.ShowReadOnly(army);
        }

        private void ShowBaseModalReadOnly(BuildingData building)
        {
            if (armyViewerModal != null)
                armyViewerModal.Hide();
            if (researchProductionModal != null)
                researchProductionModal.Hide();
            SelectHex(building.Hex);
            armyButtonRow?.Hide();
            baseViewerModal?.ShowReadOnly(building);
        }

        private void ShowBaseModal(BuildingData building)
        {
            if (armyViewerModal != null)
                armyViewerModal.Hide();
            if (researchProductionModal != null)
                researchProductionModal.Hide();
            SelectHex(building.Hex);
            armyButtonRow?.Hide();
            baseViewerModal?.Show(building);
        }

        // Public so BattleScreenUI can clear the hex/army info panels the instant a battle
        // opens (see its own Show) — nothing about the underlying hex/army selection state
        // means anything anymore once combat starts, and leaving it lingering behind the
        // battle screen would just be stale/confusing once it closes again.
        public void Deselect()
        {
            TutorialHintController.Invalidate();
            _selectedHex = null;
            if (highlight != null) highlight.Hide();
            if (infoPanel != null) infoPanel.Hide();
            if (armyButtonRow != null) armyButtonRow.Hide();
            if (armyViewerModal != null) armyViewerModal.Hide();
            if (baseViewerModal != null) baseViewerModal.Hide();
            if (researchProductionModal != null) researchProductionModal.Hide();
            SetSelectedArmy(null);
        }

        // The garrison always opens its modal (see HexInfoPanelUI's own garrison button, wired
        // the same way in SelectHex above) — a named army instead gets SELECTED for a move
        // order, same as clicking its marker directly would (see TryHandleArmyMarkerClick),
        // since that's the whole point of this row: picking which of several armies on one hex
        // a right-click move targets.
        private void OnArmyButtonClicked(ArmyData army)
        {
            if (army == null)
                return;
            if (army.IsGarrison || army.IsAirfield)
            {
                // Opening the garrison isn't picking an army to move — whatever named army was
                // previously selected (its button shown pressed-in/disabled) needs to let go so
                // it doesn't stay stuck "selected" with no way to un-pick it from this row.
                SetSelectedArmy(null);
                RefreshArmyButtonRow(ArmyRegistry.AllAt(army.Hex));
                ShowArmyModal(army);
                return;
            }
            ArmyInteracted?.Invoke(army);
            SelectArmyForOrders(army);
        }

        private void SelectArmyForOrders(ArmyData army)
        {
            if (army.Members.Count == 0 || army.Controller == null)
                return;

            SetSelectedArmy(army.Controller);
            SelectHex(army.Hex, preserveSelection: true);
            // The chosen army becomes the visible marker for its owner on this hex too (see
            // RestackArmiesOn's representative-selection), so a move order started from here
            // actually animates the same army the player just picked, not an arbitrary one.
            RestackArmiesOn(army.Hex, null);
            RefreshArmyButtonRow(ArmyRegistry.AllAt(army.Hex));
        }

        // The hover/pulse animation (and move-order eligibility) only apply to the current
        // player's own army — selecting an enemy's (or nobody's) just shows its info.
        private void SetSelectedArmy(ArmyController controller)
        {
            if (ReferenceEquals(_selectedArmy, controller))
                return;

            if (_selectedArmy != null)
                _selectedArmy.ResetTransform(map, ResolveArmyOffset(_selectedArmy.Data.Hex, _selectedArmy));

            _selectedArmy = IsOwnArmyOnCurrentTurn(controller) ? controller : null;

            if (_selectedArmy != null)
                _selectedArmy.SetSelected(true);

            HidePathPreview();
        }

        private bool IsOwnArmyOnCurrentTurn(ArmyController controller)
        {
            return controller != null && turnController != null && controller.Data.Owner != null
                && controller.Data.Owner == turnController.CurrentPlayer;
        }

        // ResolveArmyOffset/NonEmptyArmiesAt/ToWorldOffset/RestackArmiesOn/ReconcileHexVisualState/
        // RefreshAllVisibility/DistinctOwners/FindOwnerAt/VisibleForLayout/BuildingKnownToViewer
        // moved to HexSelectionController.Visuals.cs — see that file's own class-level comment.
    }
}




