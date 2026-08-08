using System.Collections.Generic;
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
        [SerializeField] private HexShaderHighlight highlight;
        [SerializeField] private HexInfoPanelUI infoPanel;
        [SerializeField] private ArmyInfoPanelUI armyInfoPanel;
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private GameTurnController turnController;
        // Shown instead of armyInfoPanel whenever the selected hex has 2+ armies on it (see
        // SelectHex) — click opens armyViewerModal for that army.
        [SerializeField] private ArmyButtonRowUI armyButtonRow;
        [SerializeField] private ArmyViewerModalUI armyViewerModal;
        [SerializeField] private BaseViewerModalUI baseViewerModal;
        // Up to 4 "build an extraction Facility" buttons, shown next to Garrison/Base — see
        // RefreshResourceActionRow.
        [SerializeField] private ResourceActionRowUI resourceActionRow;
        // Shown the instant a move order makes contact with an enemy army (see
        // TryIssueMoveOrder) — "To Battle" opens battleScreen directly; "Delay" parks it in
        // DelayedBattleRegistry instead (see the manual's Delay Attack, GameTurnController
        // drains this at the next turn boundary).
        [SerializeField] private BattleContactPopupUI battleContactPopup;
        [SerializeField] private BattleScreenUI battleScreen;
        // How close (in screen pixels) a click needs to land to a friendly army marker to
        // count as clicking the marker itself rather than just the hex it's on — see
        // TryHandleArmyMarkerClick. No Collider on the marker prefab; a screen-space distance
        // check is simpler than adding one just for this.
        [SerializeField] private float armyMarkerClickRadius = 30f;

        // The army (if any) currently playing its selected-hover animation and eligible for
        // move orders — tracked so selecting a different hex stops it, instead of leaving it
        // animating (or receiving right-click orders) forever.
        private ArmyController _selectedArmy;
        private MoveArrowMarker _pathArrow;
        private HexCoord? _lastPreviewedHover;
        // The hex SelectHex last showed info for — kept around purely so a re-run can be
        // triggered without re-deriving it: the Army Viewer's Closed event fires after the
        // player creates/renames an army from inside the modal, and the button row/info panel
        // for whichever hex is still selected need to reflect that without the player having
        // to click the hex again (see OnArmyModalClosed). Also covers TryHandleArmyMarkerClick's
        // shortcut straight into the modal, which never runs SelectHex itself.
        private HexCoord? _selectedHex;

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
                turnController.TurnChanging += Deselect;
            if (armyViewerModal != null)
                armyViewerModal.Closed += OnArmyModalClosed;
            if (baseViewerModal != null)
                baseViewerModal.Closed += OnBaseModalClosed;
        }

        private void OnDisable()
        {
            if (turnController != null)
                turnController.TurnChanging -= Deselect;
            if (armyViewerModal != null)
                armyViewerModal.Closed -= OnArmyModalClosed;
            if (baseViewerModal != null)
                baseViewerModal.Closed -= OnBaseModalClosed;
        }

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
            if (_selectedHex.HasValue)
                SelectHex(_selectedHex.Value, preserveSelection: ShouldPreserveSelectionAfterModalClose(_selectedHex.Value));
        }

        // False only when the hex has exactly one non-garrison army and it isn't already what's
        // selected — i.e. there's an obvious, unambiguous army to select and nothing has claimed
        // it yet. True in every other case (0 or 2+ armies, or the sole army is already
        // selected) so this never fights the button-row-driven multi-army selection.
        private bool ShouldPreserveSelectionAfterModalClose(HexCoord hex)
        {
            List<ArmyData> armies = ArmyRegistry.AllAt(hex);
            ArmyData soleArmy = armies.Count == 1 && !armies[0].IsGarrison ? armies[0] : null;
            if (soleArmy == null)
                return true;
            return _selectedArmy != null && _selectedArmy.Data == soleArmy;
        }

        // Same reasoning as OnArmyModalClosed — an Upgrade purchased inside the Base modal
        // changes UnlockedFacilitySlots/Defense/Resistance, which the hex-side info panel
        // doesn't otherwise learn about until the player re-clicks the hex.
        private void OnBaseModalClosed()
        {
            if (_selectedHex.HasValue)
                SelectHex(_selectedHex.Value, preserveSelection: true);
        }

        private void Update()
        {
            if (targetCamera == null || map == null || gameConfig == null)
                return;

            // Hex selection is only ever the local human's to drive — allowed during citadel
            // placement (before the turn system starts) and on the human's own turn, blocked
            // during the dice-off and any AI/Neutral turn.
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

            UpdateMovePreview(hoverCoord);

            if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame && hoverCoord.HasValue)
                TryIssueMoveOrder(hoverCoord.Value);

            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                HandleLeftClick(hoverCoord);
        }

        // TurnNumber is still 0 before GameTurnController.BeginGame() runs — i.e. citadel
        // placement is still in progress. Once it's started, only a human's own turn (not the
        // dice-off, not an AI/Neutral turn, both of which leave CurrentPlayer without a human
        // owner) lets the player touch the map — and even then, only after they've dismissed
        // TurnInfoPopupUI's Confirm button (TurnConfirmed), not the instant it becomes their turn.
        private bool IsInputAllowed()
        {
            if (turnController == null)
                return true;
            if (turnController.TurnNumber == 0)
                return true;
            // turnController.InputBlocked already folds in armyViewerModal.IsShowing (see
            // GameTurnController), so map input is locked out while the modal is open without
            // needing a second check here.
            return turnController.CurrentPlayer != null && turnController.CurrentPlayer.IsHuman
                && turnController.TurnConfirmed && !turnController.InputBlocked;
        }

        // Turn-based game, mostly-idle camera: re-running the Physics.Raycast every single
        // frame regardless of whether anything that could change its result actually moved was
        // pure waste (see RaycastHex's own per-call cost — a real physics query against the
        // ground mesh, not a cheap property read). Cached against the 3 inputs that can change
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
            if (targetCamera == null || map == null || gameConfig == null)
                return null;

            Ray ray = targetCamera.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 1000f, gameConfig.groundLayerMask))
                return null;

            HexCoord coord = map.WorldToHex(hit.point);
            return map.TryGetTerrainAt(coord, out _) ? coord : (HexCoord?)null;
        }

        private void HandleLeftClick(HexCoord? hoverCoord)
        {
            if (!hoverCoord.HasValue)
            {
                Deselect();
                return;
            }

            if (Mouse.current != null && TryHandleArmyMarkerClick(hoverCoord.Value, Mouse.current.position.ReadValue()))
                return;

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

            if (ownRepresentative != null && Vector2.Distance(targetCamera.WorldToScreenPoint(ownRepresentative.Controller.transform.position), screenPosition) <= armyMarkerClickRadius)
            {
                BuildingData building = BuildingRegistry.FindAt(hex);
                bool hasBarracks = building != null && building.Owner == human && building.HasAbility(BuildingAbilities.Barracks);
                if (hasBarracks)
                {
                    ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex, human);
                    if (garrison == null)
                        return false;
                    // This shortcut never runs SelectHex (it jumps straight to the modal instead
                    // of the usual highlight/info-panel flow) — _selectedHex still needs to be
                    // tracked so OnArmyModalClosed knows which hex's button row to refresh once
                    // the player closes it.
                    _selectedHex = hex;
                    ShowArmyModal(garrison);
                    return true;
                }

                if (ownRepresentative.IsGarrison)
                    return false; // an empty/unreachable garrison off its own Barracks hex — nothing to open here
                // Whichever of the player's own armies is actually the one being clicked —
                // 2+ sharing the hex is no longer ambiguous now that the modal's own button row
                // (see ArmyViewerModalUI.RefreshButtonRow) can switch to the others from here.
                _selectedHex = hex;
                ShowArmyModal(ownRepresentative);
                return true;
            }

            return TryHandleEnemyArmyMarkerClick(hex, screenPosition, human);
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

                Vector2 markerScreenPos = targetCamera.WorldToScreenPoint(army.Controller.transform.position);
                if (Vector2.Distance(markerScreenPos, screenPosition) > armyMarkerClickRadius)
                    continue;

                _selectedHex = hex;
                armyViewerModal.ShowReadOnly(army);
                return true;
            }
            return false;
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
            _selectedHex = coord;
            map.TryGetTerrainAt(coord, out TerrainTypeEntry entry);

            Vector3 hexCenter = map.HexToWorld(coord);
            if (highlight != null)
                highlight.ShowAt(hexCenter, map.OuterRadius);

            (int col, int row) = coord.ToOffset();
            BuildingData buildingHere = BuildingRegistry.FindAt(coord);
            PlayerSetupData owner = buildingHere?.Owner;
            // The bonus (if any) belongs permanently to the hex itself — stamped once when a
            // citadel was placed there (see HexResourceBonusRegistry), independent of whatever
            // building currently stands on it.
            ResourceYields effectiveYields = HexResourceCalculator.GetEffectiveYield(entry, HexResourceBonusRegistry.GetBonus(coord));
            if (infoPanel != null)
            {
                infoPanel.ShowHex(col, row, entry, owner?.Nickname, buildingHere?.Name, effectiveYields);

                bool isOwn = owner != null && owner == turnController?.CurrentPlayer;

                // Independent of how many armies share this hex (that's the button-row/
                // unit-info split below) — a direct way to reach the garrison even when it's
                // the only army here, which is otherwise unreachable (see the Army-only-
                // movement plan: a lone unit sits in the garrison, which can't move, until
                // it's sorted into a real army from this modal).
                bool isOwnBarracks = isOwn && buildingHere.HasAbility(BuildingAbilities.Barracks);
                ArmyData garrisonForButton = isOwnBarracks ? ArmyRegistry.FindGarrisonAt(coord, owner) : null;
                infoPanel.SetGarrisonButtonVisible(garrisonForButton != null, () => ShowArmyModal(garrisonForButton));

                // Same idea, for BaseViewerModalUI — any building tagged Base (the citadel
                // always is, see CitadelSetupController; so is anything built from a
                // CardType.Base card, see SpawnBuilding) OR a hero-built resource site (see
                // TryBuildExtractionFacility, identified by HasTieredUnlock=false rather than a
                // separate tag) — both use the exact same modal.
                bool isOwnBase = isOwn && (buildingHere.HasAbility(BuildingAbilities.Base) || !buildingHere.HasTieredUnlock);
                BuildingData baseForButton = isOwnBase ? buildingHere : null;
                infoPanel.SetBaseButtonVisible(baseForButton != null, () => ShowBaseModal(baseForButton));
            }

            RefreshResourceActionRow(coord, buildingHere, effectiveYields);

            // 2+ armies (or a garrison sharing the hex with a named army) on this hex — replace
            // the brief army-info panel with one button per army instead. A lone garrison shows
            // neither: it can't move and isn't "an army" for this purpose (see
            // ArmyInfoPanelUI/the garrison button on HexInfoPanelUI for how to actually reach
            // it). Only a hex with exactly one non-garrison army gets the plain info-panel +
            // hover/pulse-animation treatment. Only the current player's OWN armies ever show
            // here — an enemy army sharing the hex is what the battle trigger (see Game.Combat.
            // BattleInitiator) is for, not something to select/command from this panel.
            List<ArmyData> armies = ArmyRegistry.AllAt(coord).FindAll(a => a.Owner == turnController?.CurrentPlayer);
            RefreshArmyButtonRow(armies);

            ArmyData soleArmy = armies.Count == 1 && !armies[0].IsGarrison ? armies[0] : null;

            if (armyInfoPanel != null)
            {
                if (soleArmy != null)
                    armyInfoPanel.ShowArmy(soleArmy);
                else
                    armyInfoPanel.Hide();
            }

            if (preserveSelection)
                return;

            SetSelectedArmy(soleArmy?.Controller);
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
            armies = armies.FindAll(a => !a.IsPrison);
            if (armies.Count >= 2)
                armyButtonRow.Show(armies, OnArmyButtonClicked, GetSelectedArmy(), showStats: true);
            else
                armyButtonRow.Hide();
        }

        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        // Up to 4 "build an extraction Facility" buttons — one per resource type `coord`
        // actually yields that ISN'T ALREADY FULLY COLLECTED (building's own baked-in ability +
        // whatever Facilities are already placed, see BuildingData.CollectedAmount — e.g. a
        // citadel alone already fully covers a 1-of-each hex, only a richer hex still needs a
        // button), and that doesn't already have a Facility built for it (only one per resource
        // type per building — see HasFacilityWithAbility; closing the remaining gap on a richer
        // hex is done by upgrading that Facility, not building a second one) — while one of the
        // current player's own Hero armies stands here. Independent of who owns whatever
        // building already sits on the hex being shown elsewhere — TryBuildExtractionFacility
        // itself rejects a foreign building silently, same as any other irrelevant target.
        private void RefreshResourceActionRow(HexCoord coord, BuildingData buildingHere, ResourceYields effectiveYields)
        {
            if (resourceActionRow == null)
                return;

            PlayerSetupData human = turnController?.CurrentPlayer;
            var actions = new List<(ResourceType type, CardDefinition definition)>();
            // No building here — regardless of who it belongs to — can be worked while a
            // combat-capable enemy army stands on the hex (see Game.Combat.BattleInitiator).
            if (human != null && human.IsHuman && gameConfig.extractionFacilityCards != null
                && (buildingHere == null || buildingHere.Owner == human)
                && BattleInitiator.FindEnemyAt(coord, human) == null
                && HasOwnHeroArmyAt(coord, human))
            {
                foreach (ResourceType type in AllResourceTypes)
                {
                    int yield = effectiveYields.Get(type);
                    if (yield <= 0)
                        continue;
                    string ability = BuildingAbilities.CollectAbilityFor(type);
                    if (buildingHere != null && buildingHere.HasFacilityWithAbility(ability))
                        continue;
                    int alreadyCollected = buildingHere != null ? buildingHere.CollectedAmount(type) : 0;
                    if (alreadyCollected >= yield)
                        continue;
                    int index = (int)type;
                    CardDefinition definition = index < gameConfig.extractionFacilityCards.Length ? gameConfig.extractionFacilityCards[index] : null;
                    if (definition == null)
                        continue;
                    actions.Add((type, definition));
                }
            }

            if (actions.Count > 0)
                resourceActionRow.Show(actions, type => TryBuildExtractionFacility(gameConfig.extractionFacilityCards[(int)type], coord, human));
            else
                resourceActionRow.Hide();
        }

        // The two hex-side modals are mutually exclusive — only one of them makes sense open at
        // once (both key off the same selected hex's own building/garrison), so every place that
        // opens one closes the other first, funnelled through these two rather than repeating the
        // Hide-then-Show pairing at each call site.
        private void ShowArmyModal(ArmyData army)
        {
            if (baseViewerModal != null)
                baseViewerModal.Hide();
            armyViewerModal?.Show(army);
        }

        private void ShowBaseModal(BuildingData building)
        {
            if (armyViewerModal != null)
                armyViewerModal.Hide();
            baseViewerModal?.Show(building);
        }

        // Public so BattleScreenUI can clear the hex/army info panels the instant a battle
        // opens (see its own Show) — nothing about the underlying hex/army selection state
        // means anything anymore once combat starts, and leaving it lingering behind the
        // battle screen would just be stale/confusing once it closes again.
        public void Deselect()
        {
            _selectedHex = null;
            if (highlight != null) highlight.Hide();
            if (infoPanel != null) infoPanel.Hide();
            if (armyInfoPanel != null) armyInfoPanel.Hide();
            if (armyButtonRow != null) armyButtonRow.Hide();
            if (armyViewerModal != null) armyViewerModal.Hide();
            if (baseViewerModal != null) baseViewerModal.Hide();
            if (resourceActionRow != null) resourceActionRow.Hide();
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
            if (army.IsGarrison)
            {
                // Opening the garrison isn't picking an army to move — whatever named army was
                // previously selected (its button shown pressed-in/disabled) needs to let go so
                // it doesn't stay stuck "selected" with no way to un-pick it from this row.
                SetSelectedArmy(null);
                RefreshArmyButtonRow(ArmyRegistry.AllAt(army.Hex));
                ShowArmyModal(army);
                return;
            }
            SelectArmyForOrders(army);
        }

        private void SelectArmyForOrders(ArmyData army)
        {
            if (army.Members.Count == 0 || army.Controller == null)
                return;

            SetSelectedArmy(army.Controller);
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
            if (_selectedArmy == controller)
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

        // Where a given army should sit on `hex`, resolved from every other non-empty army
        // currently there too (see HexObjectLayout) — not a fixed per-army corner any more.
        // forArmy doesn't need to already be registered at `hex`: the mover sets Data.Hex before
        // calling this, so it usually already shows up in ArmyRegistry.AllAt(hex) on its own,
        // but it's added explicitly if not, so it still gets a real slot instead of defaulting
        // to zero. Only ONE marker is ever positioned per owner (see RestackArmiesOn) — this
        // resolves that shared slot regardless of which of an owner's armies asks for it.
        private Vector3 ResolveArmyOffset(HexCoord hex, ArmyController forArmy)
        {
            if (gameConfig == null || map == null)
                return Vector3.zero;

            bool hasBuilding = FindOwnerAt(hex) != null;
            List<ArmyData> armiesHere = NonEmptyArmiesAt(hex);
            if (!armiesHere.Contains(forArmy.Data))
                armiesHere.Add(forArmy.Data);

            List<PlayerSetupData> distinctOwners = DistinctOwners(armiesHere);
            int index = distinctOwners.IndexOf(forArmy.Data.Owner);

            HexObjectLayout.Result layout = HexObjectLayout.Resolve(gameConfig, hasBuilding, distinctOwners);
            return ToWorldOffset(layout.ArmyOffsets[index]);
        }

        // Armies with zero members don't exist for layout/visibility purposes — a freshly
        // created army (see CreateArmyMarker) or one just emptied out by ArmyViewerModalUI's
        // drag-between-armies flow is invisible until/unless it has at least one unit again.
        private static List<ArmyData> NonEmptyArmiesAt(HexCoord hex)
        {
            // IsPrison excluded outright, non-empty or not — it never gets a marker of its own
            // (see ArmyData.IsPrison's own comment), so it must never become an owner's
            // "representative" army here either, or a real army (e.g. that owner's garrison)
            // would lose its marker to a Prison that was never meant to have one at all.
            return ArmyRegistry.AllAt(hex).FindAll(a => a.Members.Count > 0 && !a.IsPrison);
        }

        // Every HexObjectLayout offset is in hex-radius units (x = left/right, y = world Z) —
        // this converts one to an actual world-space offset from a hex's centre, shared by
        // every call site below instead of repeating the same Vector2->Vector3 math each time.
        private Vector3 ToWorldOffset(Vector2 layoutOffset)
        {
            return new Vector3(layoutOffset.x, 0f, layoutOffset.y) * map.OuterRadius;
        }

        // Snaps every OTHER non-empty army resting on `hex`, plus its building marker (if any),
        // to match its freshly resolved layout — needed because an army's or building's correct
        // offset can depend on who else shares its hex (e.g. two different owners' armies sit
        // mirrored left/right of centre, or a building re-centres once the last army sharing its
        // hex leaves), so one army arriving or leaving can change where something already
        // settled there needs to sit too. `exclude` is whichever army is mid-move right now and
        // already positions itself via its own MoveAlong. Public: CardHandUI and
        // ArmyViewerModalUI both call this directly after changing an army's membership in ways
        // that can flip its visibility (first member ever added, or emptied out completely).
        public void RestackArmiesOn(HexCoord hex, ArmyController exclude)
        {
            if (gameConfig == null || map == null)
                return;

            List<ArmyData> armiesHere = NonEmptyArmiesAt(hex);
            bool hasBuilding = FindOwnerAt(hex) != null;
            List<PlayerSetupData> distinctOwners = DistinctOwners(armiesHere);

            // An army that just dropped to zero members (its last unit dragged out, see
            // CardHandUI/ArmyViewerModalUI's own RestackArmiesOn calls) drops out of armiesHere
            // above from this point on — nobody else ever tells its marker to hide, so it must
            // be done explicitly here, once, right as that happens (see the loop after this
            // method's main one below).
            List<ArmyData> emptiedHere = ArmyRegistry.AllAt(hex).FindAll(a => a.Members.Count == 0);

            HexObjectLayout.Result layout = HexObjectLayout.Resolve(gameConfig, hasBuilding, distinctOwners);

            // One player can have several armies sharing a hex (garrison + one or more named
            // armies), but only one marker per owner is ever actually shown there — the rest
            // stay hidden behind it rather than stacking (see ArmyData.Controller; a unit has no
            // marker of its own at all any more, only its army does). Normally whichever army is
            // encountered first, but the currently selected one (if it's here) always wins
            // instead — so picking a different army via the hex-side button row (see
            // SelectArmyForOrders) actually shows, and later animates, THAT army, not an
            // arbitrary one. An army that's mid-move stays visible regardless (it's the thing the
            // player is watching travel), even if it isn't the representative one.
            var representativeForOwner = new Dictionary<PlayerSetupData, ArmyData>();
            foreach (ArmyData army in armiesHere)
                if (!representativeForOwner.ContainsKey(army.Owner))
                    representativeForOwner[army.Owner] = army;
            if (_selectedArmy != null && armiesHere.Contains(_selectedArmy.Data))
                representativeForOwner[_selectedArmy.Data.Owner] = _selectedArmy.Data;

            for (int i = 0; i < armiesHere.Count; i++)
            {
                ArmyData army = armiesHere[i];
                ArmyController controller = army.Controller;
                if (controller == null)
                    continue;

                bool visible = representativeForOwner[army.Owner] == army || controller.IsMoving;
                if (controller.Visual != null)
                {
                    controller.Visual.SetVisible(visible);
                    if (visible)
                        controller.Visual.SetIcon(gameConfig.armyIconSprite);
                }

                if (controller == exclude || controller.IsMoving)
                    continue;
                Vector2 ownerOffset = layout.ArmyOffsets[distinctOwners.IndexOf(army.Owner)];
                controller.transform.position = map.HexToWorld(hex) + ToWorldOffset(ownerOffset);
            }

            // Only ever hides — never deletes. This runs for EVERY membership change on this
            // hex, including a brand-new army that was just created and hasn't been given its
            // first member yet (see ArmyViewerModalUI.CreateArmy), which is empty by design and
            // must not be torn down for it. Actually deleting an army that's been emptied OUT
            // (as opposed to never filled) only ever happens where a member is actually removed
            // — see DeleteArmyIfEmptied, called explicitly from there instead.
            foreach (ArmyData army in emptiedHere)
                if (army.Controller != null && army.Controller.Visual != null)
                    army.Controller.Visual.SetVisible(false);

            if (hasBuilding)
            {
                BuildingData building = BuildingRegistry.FindAt(hex);
                if (building != null && building.Visual != null)
                    building.Visual.transform.position = map.HexToWorld(hex) + ToWorldOffset(layout.BuildingOffset);
            }
        }

        // HexObjectLayout lays out one slot per distinct owner, not one per army — same-owner
        // armies on a hex collapse to a single visible marker (see RestackArmiesOn), so the
        // layout must never see an owner twice or it falls through to the "not designed yet"
        // fallback (e.g. a building + 2 same-owner armies would wrongly stack at hex centre
        // instead of keeping the building's corner offset).
        private static List<PlayerSetupData> DistinctOwners(List<ArmyData> armies)
        {
            var result = new List<PlayerSetupData>(armies.Count);
            foreach (ArmyData army in armies)
                if (!result.Contains(army.Owner))
                    result.Add(army.Owner);
            return result;
        }

        // Whoever owns the building at this hex (citadel or a player-built Base alike) — there's
        // still no territory/zone-of-control system, so "owns this hex" and "owns the building
        // here" are the same question. Delegates to BuildingRegistry rather than only matching
        // each player's own citadel coordinates, now that BuildingAbilities.Base buildings can
        // exist anywhere a Base card gets played, not just on the starting citadel hex.
        private static PlayerSetupData FindOwnerAt(HexCoord coord)
        {
            return BuildingRegistry.FindAt(coord)?.Owner;
        }
    }
}
