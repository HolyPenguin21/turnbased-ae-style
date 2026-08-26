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
        // Resolves AA reactions/air strikes as an army's own move steps land — see
        // HexSelectionController.Movement.cs's own IssueMoveOrder, which wires this into
        // ArmyController.MoveAlong's resolveStepAsync hook for every mover, air or ground alike.
        [SerializeField] private AviationCombatPresenter aviationCombatPresenter;
        // Small usability margin around the marker's real projected SpriteRenderer bounds.
        // The bounds themselves scale with orthographic zoom (MapObjectVisual.ContainsScreenPoint),
        // so this never turns into the old fixed 30px invisible collider at long zoom.
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
        public bool IsBattleActive =>
            (battleScreen != null && battleScreen.IsShowing) ||
            (battleContactPopup != null && battleContactPopup.IsShowing);

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
        // Separate snapshots are required per human viewer: the live building visual may be
        // recoloured or destroyed while hidden, but each human must keep seeing exactly the
        // marker they last observed until that hex enters their vision again.
        private readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, MapObjectVisual>> _rememberedBuildingVisuals =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, MapObjectVisual>>();
        private sealed class RememberedArmyVisual
        {
            public HexCoord Hex;
            public ArmyData Snapshot;
            public MapObjectVisual Visual;
        }
        private readonly Dictionary<PlayerSetupData, Dictionary<int, RememberedArmyVisual>> _rememberedArmyVisuals =
            new Dictionary<PlayerSetupData, Dictionary<int, RememberedArmyVisual>>();
        private readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, List<RememberedArmyVisual>>> _rememberedArmyVisualsByHex =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, List<RememberedArmyVisual>>>();

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
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            VisionSystem.VisibleContentChanged += OnVisibleContentChanged;
            BuildingRegistry.VisualStateChanged += OnBuildingVisualStateChanged;
            RefreshAllVisibility();
        }

        private void OnDisable()
        {
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
            VisionSystem.VisibilityChanged -= OnVisibilityChanged;
            VisionSystem.VisibleContentChanged -= OnVisibleContentChanged;
            BuildingRegistry.VisualStateChanged -= OnBuildingVisualStateChanged;
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
            if (viewer != null && viewer.IsHuman && turnController != null && turnController.CurrentPlayer == viewer)
                RememberCurrentlyVisibleContent(viewer);
            RefreshAllVisibility();
        }

        private void OnTurnChangingForVisualMemory()
        {
            PlayerSetupData outgoing = turnController != null ? turnController.CurrentPlayer : null;
            if (outgoing != null && outgoing.IsHuman)
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
            if (player != null && player.IsHuman)
                RememberCurrentlyVisibleContent(player);
            if (player == VisionSystem.CurrentViewer)
                RefreshAllVisibility();
        }

        // A registry change on a hex which this player can already see must still update its
        // markers and remembered snapshot, but it must not make an unchanged FOW area look like
        // a full visibility rebuild to every subscriber.
        private void OnVisibleContentChanged(PlayerSetupData player, HexCoord hex)
        {
            if (player != null && player.IsHuman)
                RememberCurrentlyVisibleContent(player);
            if (player == VisionSystem.CurrentViewer)
                RefreshAllVisibility();
        }

        private void RememberCurrentlyVisibleContent(PlayerSetupData viewer)
        {
            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(viewer))
            {
                List<ArmyData> enemies = ArmyRegistry.AllAt(hex)
                    .FindAll(army => army.Owner != viewer && army.Owner != null && !army.Owner.IsNeutral
                        && BattleInitiator.IsEngageable(army));
                HumanVisualMemory.ReconcileVisibleHex(viewer, hex, enemies.ConvertAll(army => army.Id));
                foreach (ArmyData army in enemies)
                {
                    HumanVisualMemory.ObserveArmy(viewer, army, hex);
                    UpdateRememberedArmyVisual(viewer, army, hex);
                }

                BuildingData building = BuildingRegistry.FindAt(hex);
                bool exists = building != null && building.Visual != null;
                HumanVisualMemory.ObserveBuilding(viewer, hex, exists);
                if (exists)
                    UpdateRememberedBuildingVisual(viewer, hex, building.Visual);
                else
                    RemoveRememberedBuildingVisual(viewer, hex);
            }
            RemoveUnrememberedArmyVisuals(viewer);
        }

        private void ObserveMovingArmyStep(ArmyData army, HexCoord from, HexCoord to, bool completed)
        {
            if (army == null || GameSession.Players == null)
                return;

            bool startsInCurrentView = false;
            foreach (PlayerSetupData viewer in GameSession.Players)
            {
                if (viewer == null || !viewer.IsHuman || viewer == army.Owner)
                    continue;
                bool observed = VisionSystem.IsVisible(viewer, from) || VisionSystem.IsVisible(viewer, to);
                if (!observed)
                    continue;
                startsInCurrentView |= viewer == VisionSystem.CurrentViewer && VisionSystem.IsVisible(viewer, from);
                if (!completed)
                    continue;
                HumanVisualMemory.ObserveArmy(viewer, army, to);
                UpdateRememberedArmyVisual(viewer, army, to);
            }

            if (completed)
                RefreshAllVisibility();
            if (army.Controller != null && army.Controller.Visual != null)
            {
                if (!completed && startsInCurrentView)
                    army.Controller.Visual.SetVisible(true);
                else if (completed)
                    army.Controller.Visual.SetVisible(VisionSystem.IsVisibleToCurrentViewer(to));
            }
        }

        private void UpdateRememberedArmyVisual(PlayerSetupData viewer, ArmyData source, HexCoord hex)
        {
            if (source?.Controller?.Visual == null
                || !HumanVisualMemory.TryGetArmySighting(viewer, source.Id, out HumanVisualMemory.ArmySighting sighting))
                return;
            if (!_rememberedArmyVisuals.TryGetValue(viewer, out Dictionary<int, RememberedArmyVisual> visuals))
            {
                visuals = new Dictionary<int, RememberedArmyVisual>();
                _rememberedArmyVisuals[viewer] = visuals;
            }
            bool alreadyRemembered = visuals.TryGetValue(source.Id, out RememberedArmyVisual remembered);
            if (alreadyRemembered && remembered != null && !remembered.Hex.Equals(hex))
            {
                HexCoord vacatedHex = remembered.Hex;
                RemoveRememberedArmyFromHexIndex(viewer, remembered);
                // Whatever else is still remembered on the hex this army just moved away from
                // (in this viewer's memory) may now need a simpler layout without it.
                ReapplyRememberedLayout(viewer, vacatedHex);
            }
            if (!alreadyRemembered || remembered == null || remembered.Visual == null)
            {
                MapObjectVisual visual = Instantiate(source.Controller.Visual, source.Controller.transform.position,
                    source.Controller.transform.rotation, transform);
                visual.name = source.Controller.Visual.name + " (Last Seen)";
                remembered = new RememberedArmyVisual { Visual = visual };
                visuals[source.Id] = remembered;
            }
            remembered.Hex = hex;
            remembered.Snapshot = sighting.Army;
            int terrainDefense = 0;
            if (map != null && map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain) && terrain != null)
                terrainDefense = terrain.defenseModifier;
            BuildingData observedBuilding = BuildingRegistry.FindAt(hex);
            int constructionDefense = observedBuilding != null && observedBuilding.IsBase ? observedBuilding.Defense : 0;
            remembered.Snapshot.VisualSnapshotConstructionDefense = constructionDefense;
            remembered.Snapshot.VisualSnapshotDefenseBonus = terrainDefense + constructionDefense;
            remembered.Visual.transform.rotation = source.Controller.transform.rotation;
            remembered.Visual.transform.localScale = source.Controller.transform.localScale;
            remembered.Visual.CopyAppearanceFrom(source.Controller.Visual);
            FactionCardCatalog ownerCatalog = source.Owner != null && cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(source.Owner.Faction)
                : null;
            if (ownerCatalog != null && ownerCatalog.armyIcon != null)
                remembered.Visual.SetIcon(ownerCatalog.armyIcon);
            remembered.Visual.SetVisible(false);
            AddRememberedArmyToHexIndex(viewer, remembered);
            // Position last, after the hex index is up to date — see ReapplyRememberedLayout's
            // own comment for why a remembered building+army pair is safe to lay out for real
            // instead of collapsing to centre (per the project owner's own 2026-08-26 follow-up).
            ReapplyRememberedLayout(viewer, hex);
        }

        // Computes the SAME per-owner offset layout the live map uses (HexObjectLayout), but from
        // what THIS viewer has actually remembered TOGETHER for `hex` — never from the live/
        // current occupants, which may include something this viewer never actually confirmed.
        // A remembered building-ghost and army-ghost(s) for the same hex are always written
        // together, from the very same moment of real vision (see
        // RememberCurrentlyVisibleContent/OnBuildingVisualStateChanged) — an army that only moves
        // onto the hex AFTER fog falls never gets a remembered entry at all, so a pair that DOES
        // coexist here already reflects a single confirmed snapshot. Laying that pair out with
        // real offsets instead of forcing them into one blob therefore discloses nothing beyond
        // what's already being shown — unlike the live map, where a lone army arriving on a hex
        // whose building this viewer hasn't personally confirmed yet still must NOT get an offset
        // (that would announce the building's presence for free); this method only ever runs for
        // already-remembered content, so that original concern doesn't apply here.
        private void ReapplyRememberedLayout(PlayerSetupData viewer, HexCoord hex)
        {
            if (map == null || gameConfig == null)
                return;

            MapObjectVisual buildingVisual = null;
            if (_rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> buildingVisuals))
                buildingVisuals.TryGetValue(hex, out buildingVisual);
            bool hasBuilding = buildingVisual != null;

            var owners = new List<PlayerSetupData>();
            List<RememberedArmyVisual> atHex = null;
            if (_rememberedArmyVisualsByHex.TryGetValue(viewer, out Dictionary<HexCoord, List<RememberedArmyVisual>> byHex))
                byHex.TryGetValue(hex, out atHex);
            if (atHex != null)
                foreach (RememberedArmyVisual remembered in atHex)
                    if (remembered.Snapshot != null && !owners.Contains(remembered.Snapshot.Owner))
                        owners.Add(remembered.Snapshot.Owner);

            HexObjectLayout.Result layout = HexObjectLayout.Resolve(gameConfig, hasBuilding, owners);

            if (hasBuilding)
                buildingVisual.transform.position = map.HexToWorld(hex) + ToWorldOffset(layout.BuildingOffset);
            if (atHex != null)
                foreach (RememberedArmyVisual remembered in atHex)
                {
                    if (remembered.Visual == null || remembered.Snapshot == null)
                        continue;
                    int index = owners.IndexOf(remembered.Snapshot.Owner);
                    Vector2 offset = index >= 0 ? layout.ArmyOffsets[index] : Vector2.zero;
                    remembered.Visual.transform.position = map.HexToWorld(hex) + ToWorldOffset(offset);
                }
        }

        private void AddRememberedArmyToHexIndex(PlayerSetupData viewer, RememberedArmyVisual remembered)
        {
            if (!_rememberedArmyVisualsByHex.TryGetValue(viewer,
                out Dictionary<HexCoord, List<RememberedArmyVisual>> byHex))
            {
                byHex = new Dictionary<HexCoord, List<RememberedArmyVisual>>();
                _rememberedArmyVisualsByHex[viewer] = byHex;
            }
            if (!byHex.TryGetValue(remembered.Hex, out List<RememberedArmyVisual> atHex))
            {
                atHex = new List<RememberedArmyVisual>();
                byHex[remembered.Hex] = atHex;
            }
            if (!atHex.Contains(remembered))
                atHex.Add(remembered);
        }

        private void RemoveRememberedArmyFromHexIndex(PlayerSetupData viewer, RememberedArmyVisual remembered)
        {
            if (!_rememberedArmyVisualsByHex.TryGetValue(viewer,
                out Dictionary<HexCoord, List<RememberedArmyVisual>> byHex)
                || !byHex.TryGetValue(remembered.Hex, out List<RememberedArmyVisual> atHex))
                return;
            atHex.Remove(remembered);
            if (atHex.Count == 0)
                byHex.Remove(remembered.Hex);
            if (byHex.Count == 0)
                _rememberedArmyVisualsByHex.Remove(viewer);
        }

        private void RemoveUnrememberedArmyVisuals(PlayerSetupData viewer)
        {
            if (!_rememberedArmyVisuals.TryGetValue(viewer, out Dictionary<int, RememberedArmyVisual> visuals))
                return;
            var stale = new List<int>();
            foreach (int armyId in visuals.Keys)
                if (!HumanVisualMemory.TryGetArmySighting(viewer, armyId, out _))
                    stale.Add(armyId);
            foreach (int armyId in stale)
            {
                RememberedArmyVisual remembered = visuals[armyId];
                HexCoord vacatedHex = remembered.Hex;
                RemoveRememberedArmyFromHexIndex(viewer, remembered);
                visuals.Remove(armyId);
                if (remembered.Visual != null)
                    Destroy(remembered.Visual.gameObject);
                // Whatever else is still remembered at this hex (a building ghost, another
                // owner's army ghost) may now need to fall back to a simpler layout without this
                // one — e.g. a lone remembered building re-centres once its only companion army
                // ghost is gone.
                ReapplyRememberedLayout(viewer, vacatedHex);
            }
        }

        private void RefreshRememberedArmyVisuals()
        {
            var shownGroups = new HashSet<(HexCoord hex, PlayerSetupData owner)>();
            foreach (KeyValuePair<PlayerSetupData, Dictionary<int, RememberedArmyVisual>> entry in _rememberedArmyVisuals)
            {
                RemoveUnrememberedArmyVisuals(entry.Key);
                foreach (RememberedArmyVisual remembered in entry.Value.Values)
                {
                    if (remembered.Visual == null)
                        continue;
                    bool visible = entry.Key == VisionSystem.CurrentViewer
                        && !VisionSystem.IsVisible(entry.Key, remembered.Hex)
                        && remembered.Snapshot != null
                        && shownGroups.Add((remembered.Hex, remembered.Snapshot.Owner));
                    remembered.Visual.SetVisible(visible);
                }
            }
        }

        private void SetRememberedArmyVisualsVisible(bool visible)
        {
            foreach (Dictionary<int, RememberedArmyVisual> visuals in _rememberedArmyVisuals.Values)
                foreach (RememberedArmyVisual remembered in visuals.Values)
                    if (remembered.Visual != null)
                        remembered.Visual.SetVisible(visible);
        }

        // VisualStateChanged fires for both a capture (building != null, already re-owned) and a
        // destruction (building == null — see BuildingRegistry.Unregister) — either way, any
        // IsAirfield army still sitting on this hex under a DIFFERENT owner than the building's
        // new one (or any owner at all, once the building is simply gone) has lost the base that
        // gave it a right to be here. Its stored aircraft go back to their own owner's hand
        // rather than just disappearing; an actual airborne IsAirArmy above the hex is untouched
        // (AviationRules.IsAirfield already excludes it).
        private void ReturnStaleAirfieldsAt(HexCoord hex, BuildingData building)
        {
            var staleAirfields = new List<ArmyData>();
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (AviationRules.IsAirfield(army) && (building == null || army.Owner != building.Owner))
                    staleAirfields.Add(army);
            foreach (ArmyData army in staleAirfields)
                AviationActions.ReturnStoredAircraftToDeck(army, this);
        }

        private void OnBuildingVisualStateChanged(HexCoord hex, BuildingData building)
        {
            ReturnStaleAirfieldsAt(hex, building);
            if (GameSession.Players == null)
                return;
            foreach (PlayerSetupData viewer in GameSession.Players)
            {
                // VisualStateChanged fires before the registry change recomputes away a
                // building's own vision. Anyone still present in this old visible set really
                // witnessed the transition and should remember the resulting state immediately.
                if (viewer == null || !viewer.IsHuman || !VisionSystem.IsVisible(viewer, hex))
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

        private void UpdateRememberedBuildingVisual(PlayerSetupData viewer, HexCoord hex, MapObjectVisual source)
        {
            if (!_rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> visuals))
            {
                visuals = new Dictionary<HexCoord, MapObjectVisual>();
                _rememberedBuildingVisuals[viewer] = visuals;
            }
            if (!visuals.TryGetValue(hex, out MapObjectVisual snapshot) || snapshot == null)
            {
                snapshot = Instantiate(source, source.transform.position, source.transform.rotation, transform);
                snapshot.name = source.name + " (Last Seen)";
                visuals[hex] = snapshot;
            }
            snapshot.transform.rotation = source.transform.rotation;
            snapshot.transform.localScale = source.transform.localScale;
            snapshot.CopyAppearanceFrom(source);
            snapshot.SetVisible(false);
            // Position last — see ReapplyRememberedLayout's own comment for why a remembered
            // building+army pair is safe to lay out for real instead of collapsing to centre.
            ReapplyRememberedLayout(viewer, hex);
        }

        private void RemoveRememberedBuildingVisual(PlayerSetupData viewer, HexCoord hex)
        {
            if (!_rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> visuals)
                || !visuals.TryGetValue(hex, out MapObjectVisual snapshot))
                return;
            visuals.Remove(hex);
            if (snapshot != null)
            {
                snapshot.SetVisible(false);
                Destroy(snapshot.gameObject);
            }
            // The building ghost is gone — any remembered army(ies) still at this hex must fall
            // back to whatever layout fits without it (e.g. a lone remembered army re-centres).
            ReapplyRememberedLayout(viewer, hex);
        }

        private void RefreshRememberedBuildingVisual(HexCoord hex)
        {
            foreach (KeyValuePair<PlayerSetupData, Dictionary<HexCoord, MapObjectVisual>> entry in _rememberedBuildingVisuals)
            {
                if (!entry.Value.TryGetValue(hex, out MapObjectVisual snapshot) || snapshot == null)
                    continue;
                bool visible = entry.Key == VisionSystem.CurrentViewer
                    && HumanVisualMemory.IsBuildingKnown(entry.Key, hex)
                    && !VisionSystem.IsVisible(entry.Key, hex);
                snapshot.SetVisible(visible);
            }
        }

        private void SetRememberedBuildingVisualsVisible(bool visible)
        {
            foreach (Dictionary<HexCoord, MapObjectVisual> visuals in _rememberedBuildingVisuals.Values)
                foreach (MapObjectVisual snapshot in visuals.Values)
                    if (snapshot != null)
                        snapshot.SetVisible(visible);
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
            // Only the current player can ever open their own army/garrison modal in the first
            // place (see IsInputAllowed) — a member gaining/losing UnitAbilities.Recce in there
            // changes that army's own HasRecce (see ArmyData), which nothing else would
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
            SelectHex(hex, preserveSelection: ShouldPreserveSelectionAfterModalClose(hex));
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
                BuildingData building = BuildingRegistry.FindAt(hex);
                bool hasBarracks = building != null && building.Owner == human && building.HasAbility(UnitAbilities.Barracks);
                if (hasBarracks)
                {
                    ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex, human);
                    if (garrison == null)
                        return false;
                    // The garrison marker also stands in for a non-empty airfield (see
                    // RestackArmiesOn) — when that's the ONLY thing actually stored here (garrison
                    // itself empty), the click should open the airfield, not an empty garrison. A
                    // non-empty garrison always wins over the airfield regardless.
                    ArmyData target = garrison;
                    if (garrison.Members.Count == 0)
                    {
                        ArmyData airfield = AviationRules.FindAirfieldAt(hex, human);
                        if (airfield != null && airfield.Members.Count > 0)
                            target = airfield;
                    }
                    // This shortcut never runs SelectHex (it jumps straight to the modal instead
                    // of the usual highlight/info-panel flow) — _selectedHex still needs to be
                    // tracked so OnArmyModalClosed knows which hex's button row to refresh once
                    // the player closes it.
                    _selectedHex = hex;
                    ShowArmyModal(target);
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

                if (!IsMarkerHit(army.Controller.Visual, screenPosition))
                    continue;

                _selectedHex = hex;
                armyViewerModal.ShowReadOnly(army);
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
                    _selectedHex = hex;
                    var siblings = new List<ArmyData>();
                    foreach (RememberedArmyVisual candidate in rememberedAtHex)
                        if (candidate.Snapshot != null && candidate.Snapshot.Owner == remembered.Snapshot.Owner)
                            siblings.Add(candidate.Snapshot);
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
                // Terrain is always shown (see VisionSystem's own comment: the map itself is
                // never fogged, only its content) — but a hex outside the current viewer's
                // vision shows none of what's actually on it right now, own building/army aside
                // (owning it is what grants the vision in the first place, so it's never hidden
                // from its own owner).
                bool contentVisible = owner == VisionSystem.CurrentViewer || VisionSystem.IsVisibleToCurrentViewer(coord);
                bool buildingKnown = HumanVisualMemory.IsBuildingKnown(VisionSystem.CurrentViewer, coord);
                string ownerName = contentVisible ? owner?.Nickname : "Unknown";
                // Never use the live registry to distinguish "still there" from "destroyed"
                // outside vision — doing so would leak the exact hidden change the remembered
                // marker is meant to conceal. The current UI intentionally exposes no detailed
                // building snapshot text, so both live-hidden and last-seen-only read Unknown.
                string buildingName = contentVisible ? buildingHere?.Name : (buildingKnown ? "Unknown" : null);
                // Resource yield is the one exception: terrain-derived and unchanging, so it's
                // remembered in two tiers instead of hiding the instant vision leaves — merely
                // having seen the hex (even from a neighbor's vision radius, never physically
                // stood on) reveals which types it yields with the amount shown as "?"; having
                // actually had an army/building stand on it reveals the exact amount (see
                // VisionSystem.HasEverSeenByCurrentViewer vs. IsVisitedByCurrentViewer).
                bool amountsKnown = contentVisible || VisionSystem.IsVisitedByCurrentViewer(coord);
                bool resourceVisible = amountsKnown || VisionSystem.IsVisibleToCurrentViewer(coord) || VisionSystem.HasEverSeenByCurrentViewer(coord);
                ResourceYields shownYields = resourceVisible ? effectiveYields : null;
                infoPanel.ShowHex(col, row, entry, ownerName, buildingName, shownYields, amountsKnown);

                bool isOwn = owner != null && owner == turnController?.CurrentPlayer;

                // Independent of how many armies share this hex (that's the button-row/
                // unit-info split below) — a direct way to reach the garrison even when it's
                // the only army here, which is otherwise unreachable (see the Army-only-
                // movement plan: a lone unit sits in the garrison, which can't move, until
                // it's sorted into a real army from this modal).
                bool isOwnBarracks = isOwn && buildingHere.HasAbility(UnitAbilities.Barracks);
                ArmyData garrisonForButton = isOwnBarracks ? ArmyRegistry.FindGarrisonAt(coord, owner) : null;
                infoPanel.SetGarrisonButtonVisible(garrisonForButton != null, () => ShowArmyModal(garrisonForButton));

                // Same idea, for BaseViewerModalUI — any building with IsBase set (the citadel
                // always has it, see CitadelSetupController; so does anything built from a
                // CardType.Base card, see SpawnBuilding) OR a hero-built resource site (see
                // TryBuildExtractionFacility, identified by HasTieredUnlock=false rather than a
                // separate tag) — both use the exact same modal.
                bool isOwnBase = isOwn && (buildingHere.IsBase || !buildingHere.HasTieredUnlock);
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
                    string ability = UnitAbilities.CollectAbilityFor(type);
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
            SelectArmyForOrders(army);
        }

        private void SelectArmyForOrders(ArmyData army)
        {
            if (army.Members.Count == 0 || army.Controller == null)
                return;

            SetSelectedArmy(army.Controller);
            RefreshArmyIcon(army);
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

        // Restack also refreshes this, but selection and the first movement frame can happen
        // before its visual pass. Apply the composition-derived icon at both boundaries.
        private void RefreshArmyIcon(ArmyData army)
        {
            if (army?.Controller?.Visual == null || army.Owner == null || cardHandUI == null || cardHandUI.StartingDeckCatalog == null)
                return;
            FactionCardCatalog catalog = cardHandUI.StartingDeckCatalog.GetCatalog(army.Owner.Faction);
            if (catalog == null)
                return;
            Sprite icon = AviationRules.IsAirArmy(army) && catalog.airArmyIcon != null ? catalog.airArmyIcon : catalog.armyIcon;
            if (icon != null)
                army.Controller.Visual.SetIcon(icon);
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
            return ArmyRegistry.AllAt(hex).FindAll(a => a.Members.Count > 0 && !a.IsPrison && !a.IsAirfield);
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

            // Airfields have no independent marker. When one stores aircraft, the owner's garrison
            // marker represents that container, exactly as it represents ground cards — even when
            // the garrison itself is empty (nothing ever staged there), so it still needs a slot in
            // armiesHere below to get laid out and shown at all.
            var airfieldOwners = new HashSet<PlayerSetupData>(ArmyRegistry.AllAt(hex)
                .FindAll(a => a.IsAirfield && a.Members.Count > 0).ConvertAll(a => a.Owner));
            foreach (PlayerSetupData owner in airfieldOwners)
            {
                if (armiesHere.Exists(a => a.Owner == owner))
                    continue;
                ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex, owner);
                if (garrison != null)
                    armiesHere.Add(garrison);
            }

            List<PlayerSetupData> distinctOwners = DistinctOwners(armiesHere);

            // An army that just dropped to zero members (its last unit dragged out, see
            // CardHandUI/ArmyViewerModalUI's own RestackArmiesOn calls) drops out of armiesHere
            // above from this point on — nobody else ever tells its marker to hide, so it must
            // be done explicitly here, once, right as that happens (see the loop after this
            // method's main one below). Excludes anything just added to armiesHere above (an empty
            // garrison standing in for its owner's non-empty airfield) — that one must stay shown,
            // not be force-hidden right back down by this same-turn sweep.
            List<ArmyData> emptiedHere = ArmyRegistry.AllAt(hex).FindAll(a => a.Members.Count == 0 && !armiesHere.Contains(a));

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

            // Airfields have no independent marker (see airfieldOwners above, computed early
            // enough to already have added the representative garrison to armiesHere itself).
            // When one stores aircraft, the owner's garrison marker represents that container,
            // exactly as it represents ground cards.
            foreach (PlayerSetupData owner in airfieldOwners)
            {
                ArmyData garrison = armiesHere.Find(a => a.Owner == owner && a.IsGarrison);
                if (garrison != null)
                    representativeForOwner[owner] = garrison;
            }

            for (int i = 0; i < armiesHere.Count; i++)
            {
                ArmyData army = armiesHere[i];
                ArmyController controller = army.Controller;
                if (controller == null)
                    continue;

                // A neutral army never moves on its own (see SpawnNeutralArmy/SpawnEventGuard —
                // both place it once and never relocate it again), so once a viewer has actually
                // seen it, remembering it there can never go stale the way remembering a real
                // player's army would — same "remembered once seen" exception the resource row
                // already gets (see VisionSystem.HasEverSeenByCurrentViewer, MapResourceDisplay).
                bool everSeenNeutral = army.Owner != null && army.Owner.IsNeutral && VisionSystem.HasEverSeenByCurrentViewer(hex);
                bool visible = (representativeForOwner[army.Owner] == army || controller.IsMoving)
                    && (army.Owner == VisionSystem.CurrentViewer || VisionSystem.IsVisibleToCurrentViewer(hex)
                        || everSeenNeutral);
                if (controller.Visual != null)
                {
                    controller.Visual.SetVisible(visible);
                    if (visible)
                    {
                        FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                            ? cardHandUI.StartingDeckCatalog.GetCatalog(army.Owner.Faction)
                            : null;
                        if (ownerCatalog != null)
                        {
                            Sprite icon = (AviationRules.IsAirArmy(army) || (army.IsGarrison && airfieldOwners.Contains(army.Owner))) && ownerCatalog.airArmyIcon != null
                                ? ownerCatalog.airArmyIcon : ownerCatalog.armyIcon;
                            if (icon != null)
                                controller.Visual.SetIcon(icon);
                        }
                    }
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
                {
                    building.Visual.transform.position = map.HexToWorld(hex) + ToWorldOffset(layout.BuildingOffset);
                    building.Visual.SetVisible(building.Owner == VisionSystem.CurrentViewer || VisionSystem.IsVisibleToCurrentViewer(hex));
                }
            }

            RefreshRememberedBuildingVisual(hex);
        }

        // Re-runs the visibility half of RestackArmiesOn (army/building marker show/hide) for
        // EVERY occupied hex on the map, without touching anyone's actual world position —
        // subscribed to VisionSystem.VisibilityChanged and GameTurnController.TurnStateChanged
        // (see OnEnable) since either can change what VisionSystem.CurrentViewer can currently
        // see: the mover's own vision expanding step-by-step during a move (see
        // HexSelectionController.Movement.cs's HandleVisionStep), or the map simply now being
        // rendered from a different human's perspective after the turn passed to them.
        private void RefreshAllVisibility()
        {
            if (map == null)
                return;
            var hexes = new HashSet<HexCoord>(ArmyRegistry.AllOccupiedHexes());
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
                hexes.Add(building.Hex);
            // Include every snapshot, not only the current viewer's known hexes. Otherwise a
            // destroyed-building snapshot belonging to the previous hot-seat viewer is never
            // visited (there is no live registry entry left to contribute its hex) and remains
            // incorrectly visible after perspective switches.
            foreach (Dictionary<HexCoord, MapObjectVisual> visuals in _rememberedBuildingVisuals.Values)
                foreach (HexCoord hex in visuals.Keys)
                    hexes.Add(hex);
            foreach (HexCoord hex in hexes)
                RestackArmiesOn(hex, null);
            RefreshRememberedArmyVisuals();
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
        // each player's own citadel coordinates, now that IsBase buildings can exist anywhere a
        // Base card gets played, not just on the starting citadel hex.
        private static PlayerSetupData FindOwnerAt(HexCoord coord)
        {
            return BuildingRegistry.FindAt(coord)?.Owner;
        }
    }
}
