using System;
using System.Collections.Generic;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
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
        // Only one faction is fully set up in this project right now (see CardHandUI/
        // ArmyViewerModalUI's own single `catalog` field) — every column's logo comes from here
        // regardless of which side it's showing, same as everywhere else that needs one.
        [SerializeField] private FactionCardCatalog catalog;
        // The outer flanking columns — whichever owner appears FIRST among participants gets
        // leftArmyList, the next DISTINCT owner gets rightArmyList (see Populate). Hidden
        // entirely if there aren't two distinct owners to show.
        [SerializeField] private ArmyButtonRowUI leftArmyList;
        [SerializeField] private ArmyButtonRowUI rightArmyList;
        [SerializeField] private ArmyViewerModalUI armyViewerModal;

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
            if (panelRoot != null)
                panelRoot.SetActive(true);
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
        public void Show(HexCoord hex, List<ArmyData> participants, Action onFight, Action onDelay)
        {
            Populate(hex, participants);
            if (fightButtonLabel != null)
                fightButtonLabel.text = "To Battle";

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
        public void ShowResolved(HexCoord hex, List<ArmyData> participants, Action onContinue)
        {
            Populate(hex, participants);
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

        private void Populate(HexCoord hex, List<ArmyData> participants)
        {
            if (panelRoot != null)
                panelRoot.SetActive(true);
            // Fresh state every time this popup opens — a prior session could conceivably have
            // left these disabled if the army modal was still open when something else force-
            // closed this popup (shouldn't normally happen, but this costs nothing to guard).
            SetActionButtonsInteractable(true);
            if (titleText != null)
                titleText.text = $"Battle at ({hex.Q}, {hex.R})";

            UIListUtility.DestroyAndClear(_columns);
            if (columnContainer != null && columnPrefab != null && participants != null)
                foreach (ArmyData army in participants)
                {
                    BattleParticipantColumnUI column = Instantiate(columnPrefab, columnContainer);
                    column.Setup(army, catalog);
                    _columns.Add(column);
                }

            PopulateSideLists(hex, participants);
        }

        // Every distinct owner among participants, in the order it first appears — usually just
        // the mover (participants[0]'s owner) and the defender it made contact with, matching
        // "left" and "right" — see the two fields' own comments for what happens with more (or
        // fewer) than two.
        private void PopulateSideLists(HexCoord hex, List<ArmyData> participants)
        {
            var owners = new List<PlayerSetupData>();
            if (participants != null)
                foreach (ArmyData army in participants)
                    if (!owners.Contains(army.Owner))
                        owners.Add(army.Owner);

            ShowSideList(leftArmyList, hex, owners.Count > 0 ? owners[0] : null);
            ShowSideList(rightArmyList, hex, owners.Count > 1 ? owners[owners.Count - 1] : null);
        }

        // Only that owner's armies actually ON this hex — not every army they have anywhere on
        // the map (see the user's own correction: this lists who's actually available to commit
        // to THIS fight, not the owner's whole roster).
        private void ShowSideList(ArmyButtonRowUI list, HexCoord hex, PlayerSetupData owner)
        {
            if (list == null)
                return;
            if (owner == null)
            {
                list.Hide();
                return;
            }
            List<ArmyData> armies = ArmyRegistry.AllAt(hex).FindAll(a => a.Owner == owner && a.Members.Count > 0);
            list.Show(armies, army =>
            {
                // Hidden outright (not just the buttons disabled) — the army modal opening ON
                // TOP still let clicks reach the side-list buttons underneath before, which
                // this closes off along with the visual overlap (see OnArmyModalClosed for the
                // matching re-show once the modal closes again — gated on _hiddenForSideListModal
                // so only THIS specific open/close pair triggers it).
                if (panelRoot != null)
                    panelRoot.SetActive(false);
                _hiddenForSideListModal = true;
                armyViewerModal?.ShowLocked(army);
                SetActionButtonsInteractable(false);
            });
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
            leftArmyList?.Hide();
            rightArmyList?.Hide();
            // Whatever this popup's own interaction was doing is over now (Fight/Delay clicked,
            // or force-closed) — any modal-close event from here on is unrelated to it.
            _hiddenForSideListModal = false;
        }
    }
}
