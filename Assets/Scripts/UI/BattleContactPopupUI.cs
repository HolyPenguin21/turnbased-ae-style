using System;
using System.Collections.Generic;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Shown the moment a moving army makes contact with an enemy combat-capable army (see
    // HexSelectionController.TryIssueMoveOrder), and again — in its read-only "informational"
    // form (see ShowResolved) — once a delayed battle actually starts at the next turn boundary
    // (see GameTurnController). One column per participant army (usually 2, but never hardcoded
    // to exactly that — a neutral army could join later), each showing that side's faction
    // logo, its commanding hero if any, and a short summary — see BattleParticipantColumnUI.
    //
    // Flanking those, on the outer left/right, one more column per SIDE (not per army) lists
    // every army that side's owner has anywhere on the map (see ArmyButtonRowUI, same
    // button-per-army component the hex-side row already uses) — so the player can see what
    // else could be brought in before deciding to fight now or delay (see the manual's Delay
    // Attack). Clicking one opens it via ArmyViewerModalUI.ShowLocked — own army or enemy's
    // alike, fully read-only, no switching to a different one of that owner's armies from
    // inside that view (that's this row's job, not the modal's).
    public class BattleContactPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private Transform columnContainer;
        [SerializeField] private BattleParticipantColumnUI columnPrefab;
        [SerializeField] private Button fightButton;
        [SerializeField] private TMP_Text fightButtonLabel;
        [SerializeField] private Button delayButton;
        // Resolved per-army via ResolveCatalog(army.Owner) — same StartingDeckCatalog.GetCatalog
        // per-owner pattern ArmyViewerModalUI already uses, so a neutral defender (Faction.Neutral,
        // see CitadelSetupController's own _neutralPlayer) gets CardCatalog_Neutral's own logo
        // instead of whichever single faction used to sit in this field regardless of army (see
        // the user's own report: a neutral defender's logo wasn't showing up here).
        [SerializeField] private StartingDeckCatalog startingDeckCatalog;
        // The outer flanking columns — whichever owner appears FIRST among participants gets
        // leftArmyList, the next DISTINCT owner gets rightArmyList (see Populate). Hidden
        // entirely if there aren't two distinct owners to show.
        [SerializeField] private ArmyButtonRowUI leftArmyList;
        [SerializeField] private ArmyButtonRowUI rightArmyList;
        [SerializeField] private ArmyViewerModalUI armyViewerModal;
        // For the terrain/building defense preview on the defending column(s) — see Populate.
        [SerializeField] private HexMap map;

        private readonly List<BattleParticipantColumnUI> _columns = new List<BattleParticipantColumnUI>();

        // True only for the exact stretch between this popup hiding itself for a side-list click
        // (see ShowSideList) and that same modal closing again — armyViewerModal is one shared
        // instance reused all over the map (HexSelectionController's own army-icon clicks included),
        // so its Closed event fires for plenty of opens this popup had nothing to do with (e.g. the
        // player just inspecting their own army on a hex, long after Fight/Delay was already
        // resolved and this popup properly Hide()-d). Without this guard, OnArmyModalClosed used to
        // reactivate panelRoot for ANY of those unrelated closes — flipping the GameObject back on
        // without ever repopulating it, so it showed empty/stale content (see the user's own report:
        // no army buttons on either side, because Hide() had already hidden those lists and nothing
        // in the stray reactivation ever called Populate/ShowSideList again).
        private bool _hiddenForSideListModal;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Owner is never null for a real army (see PlayerSetupData/CitadelSetupController's own
        // _neutralPlayer — even a neutral army has a real owner profile, just Faction.Neutral) —
        // this only returns null when startingDeckCatalog has no catalog registered for that
        // faction, same fallback ArmyViewerModalUI.ResolveCatalog uses.
        private FactionCardCatalog ResolveCatalog(PlayerSetupData owner) =>
            owner != null && startingDeckCatalog != null ? startingDeckCatalog.GetCatalog(owner.Faction) : null;

        // Lets GameTurnController react to this popup opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked/CardDraggingBlocked). Only
        // fires on an actual flip — see SetPanelActive, the one place panelRoot's active state
        // is ever touched from here on.
        public event Action VisibilityChanged;

        private void SetPanelActive(bool active)
        {
            if (panelRoot == null || panelRoot.activeSelf == active)
                return;
            panelRoot.SetActive(active);
            VisibilityChanged?.Invoke();
        }

        private void Awake()
        {
            if (armyViewerModal != null)
                armyViewerModal.Closed += OnArmyModalClosed;
        }

        private void OnDestroy()
        {
            if (armyViewerModal != null)
                armyViewerModal.Closed -= OnArmyModalClosed;
        }

        // Only reacts if THIS popup is the reason the modal was open right now — see
        // _hiddenForSideListModal's own comment for why that check is required.
        private void OnArmyModalClosed()
        {
            if (!_hiddenForSideListModal)
                return;
            _hiddenForSideListModal = false;
            SetPanelActive(true);
            SetActionButtonsInteractable(true);
        }

        private void SetActionButtonsInteractable(bool interactable)
        {
            if (fightButton != null)
                fightButton.interactable = interactable;
            if (delayButton != null)
                delayButton.interactable = interactable;
        }

        // The interactive form — first contact, still this same turn. Both actions available:
        // fight now, or delay until every player has passed (see the manual's Delay Attack).
        // `observer` is the human this specific encounter is being presented to — see
        // ShowSideList's own comment for why this must come from the caller's
        // BattleEncounterContext (BattleEncounterCoordinator.ResolveHumanObserver in the
        // delayed/queued paths) rather than the global VisionSystem.CurrentViewer, which in a
        // hot-seat game may currently belong to a different human than whoever this popup is
        // actually opening for.
        public void Show(HexCoord hex, List<ArmyData> participants, PlayerSetupData observer, Action onFight, Action onDelay)
        {
            Populate(hex, participants, observer);
            // A defender with no combat-capable unit left (hero-only army/garrison) isn't fought
            // in the Tactical Battle Module at all — onFight routes straight into a Capture/Kill
            // Challenge sequence (see the callers' own targetHeroOnly branch / BattleScreenUI.
            // BeginCaptureKillEncounter). Label the button for what it actually does in that case.
            bool captureOnly = participants != null && participants.Count > 1
                && !BattleInitiator.IsCombatCapable(participants[1]);
            if (fightButtonLabel != null)
                fightButtonLabel.text = captureOnly ? "Capture" : "To Battle";

            if (fightButton != null)
            {
                fightButton.gameObject.SetActive(true);
                fightButton.onClick.RemoveAllListeners();
                fightButton.onClick.AddListener(() => { Hide(); onFight?.Invoke(); });
            }
            if (delayButton != null)
            {
                delayButton.gameObject.SetActive(true);
                delayButton.onClick.RemoveAllListeners();
                delayButton.onClick.AddListener(() => { Hide(); onDelay?.Invoke(); });
            }
        }

        // The informational form — a delayed battle is actually starting now, at the turn
        // boundary: every player has already passed, so there's nothing left to decide, just a
        // single acknowledgement before the (placeholder) battle screen opens.
        public void ShowResolved(HexCoord hex, List<ArmyData> participants, PlayerSetupData observer, Action onContinue)
        {
            Populate(hex, participants, observer);
            if (fightButtonLabel != null)
                fightButtonLabel.text = "Continue";

            if (fightButton != null)
            {
                fightButton.gameObject.SetActive(true);
                fightButton.onClick.RemoveAllListeners();
                fightButton.onClick.AddListener(() => { Hide(); onContinue?.Invoke(); });
            }
            if (delayButton != null)
                delayButton.gameObject.SetActive(false);
        }

        private void Populate(HexCoord hex, List<ArmyData> participants, PlayerSetupData observer)
        {
            SetPanelActive(true);
            // Fresh state every time this popup opens — a prior session could conceivably have
            // left these disabled if the army modal was still open when something else force-
            // closed this popup (shouldn't normally happen, but this costs nothing to guard).
            SetActionButtonsInteractable(true);
            // participants[0] is always the attacker/mover (see GameTurnController's own
            // delayed-battle resolution, which relies on the same ordering) — everyone else
            // here is defending this hex against it.
            string attackerName = participants != null && participants.Count > 0 ? participants[0].Name : "?";
            string defenderName = participants != null && participants.Count > 1 ? participants[1].Name : "?";
            if (titleText != null)
                titleText.text = $"({hex.Q}, {hex.R}) - {attackerName} attacks {defenderName}";

            UIListUtility.DestroyAndClear(_columns);
            if (columnContainer != null && columnPrefab != null && participants != null)
                for (int i = 0; i < participants.Count; i++)
                {
                    ArmyData army = participants[i];
                    bool isDefender = i != 0;
                    // Same terrain/Base-building defense bonus Ground Combat itself will apply
                    // (see BattleScreenUI.Combat.cs's BeginAttack) — previewed here on the
                    // defending side(s) so it's visible before Fight/Delay is even decided, not
                    // just once the roll happens.
                    int terrainDefMod = 0;
                    int buildingDefMod = 0;
                    if (isDefender && !AviationRules.IsAirArmy(army))
                    {
                        if (map != null && map.TryGetTerrainAt(army.Hex, out TerrainTypeEntry terrain))
                            terrainDefMod = terrain.defenseModifier;
                        BuildingData building = BuildingRegistry.FindAt(army.Hex);
                        if (building != null && building.IsBase)
                            buildingDefMod = building.Defense;
                    }

                    BattleParticipantColumnUI column = Instantiate(columnPrefab, columnContainer);
                    column.Setup(army, ResolveCatalog(army.Owner), isDefender, terrainDefMod, buildingDefMod);
                    _columns.Add(column);
                }

            PopulateSideLists(hex, participants, observer);
        }

        // Every distinct owner among participants, in the order it first appears — usually just
        // the mover (participants[0]'s owner) and the defender it made contact with, matching
        // "left" and "right" — see the two fields' own comments for what happens with more (or
        // fewer) than two.
        private void PopulateSideLists(HexCoord hex, List<ArmyData> participants, PlayerSetupData observer)
        {
            var owners = new List<PlayerSetupData>();
            if (participants != null)
                foreach (ArmyData army in participants)
                    if (!owners.Contains(army.Owner))
                        owners.Add(army.Owner);

            ShowSideList(leftArmyList, hex, owners.Count > 0 ? owners[0] : null, participants, observer);
            ShowSideList(rightArmyList, hex, owners.Count > 1 ? owners[owners.Count - 1] : null, participants, observer);
        }

        // Only that owner's armies actually ON this hex — not every army they have anywhere on
        // the map (see the user's own correction: this lists who's actually available to commit
        // to THIS fight, not the owner's whole roster). STEALTH-COMBAT-01 §7: stealth-aware —
        // BattleEncounterVisibility.VisibleEncounterArmiesAt, not a raw ArmyRegistry.AllAt scan,
        // so a fully-hidden non-participant army of this owner never leaks into the roster; the
        // armies actually fighting (participants, already revealed by BattleEncounterCoordinator
        // by the time this popup ever opens) always show regardless. `observer` is whichever
        // human THIS ENCOUNTER is being presented to (see Show/ShowResolved's own comment) — NOT
        // the global VisionSystem.CurrentViewer, which in a hot-seat delayed battle can already
        // belong to a different human than whoever this popup is actually opening for.
        private void ShowSideList(ArmyButtonRowUI list, HexCoord hex, PlayerSetupData owner, List<ArmyData> participants,
            PlayerSetupData observer)
        {
            if (list == null)
                return;
            if (owner == null)
            {
                list.Hide();
                return;
            }
            List<ArmyData> armies = BattleEncounterVisibility.VisibleEncounterArmiesAt(
                hex, owner, observer, participants);
            list.Show(armies, army =>
            {
                // Hidden outright (not just the buttons disabled) — the army modal opening ON
                // TOP still let clicks reach the side-list buttons underneath before, which
                // this closes off along with the visual overlap (see OnArmyModalClosed for the
                // matching re-show once the modal closes again — gated on _hiddenForSideListModal
                // so only THIS specific open/close pair triggers it).
                SetPanelActive(false);
                _hiddenForSideListModal = true;
                armyViewerModal?.ShowLocked(army);
                SetActionButtonsInteractable(false);
            });
        }

        public void Hide()
        {
            SetPanelActive(false);
            leftArmyList?.Hide();
            rightArmyList?.Hide();
            // Whatever this popup's own interaction was doing is over now (Fight/Delay clicked,
            // or force-closed) — any modal-close event from here on is unrelated to it.
            _hiddenForSideListModal = false;
        }
    }
}
