using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cameras;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.UI;
using Game.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.Turns
{
    // Owns the overall turn loop: phase 1 (dice-off for turn order, redone every turn) then
    // phase 2 (each player takes their turn, in that order, human or AI alike — an AI "turn"
    // just passes after a short pause for now, since there's nothing for it to actually
    // decide yet). When the last real player finishes, phase 1 runs again for the next turn.
    //
    // The dice-off only ever sees GameSession.Players — real configured players, human or AI.
    // The neutral faction (PlayerRoot.Create(null, "Neutral") in CitadelSetupController) was
    // never added to that list, so it's excluded from the roll automatically. It still gets a
    // fixed last slot in phase 2 (index == CurrentTurnOrder.Count, represented as `null`
    // everywhere a real player would be) — same pass-after-a-pause treatment as AI, since it
    // has no actions of its own yet either.
    public class GameTurnController : MonoBehaviour
    {
        [SerializeField] private TurnOrderPopupUI turnOrderPopup;
        [SerializeField] private Button endTurnButton;
        [SerializeField] private float aiStepDelay = 0.5f;

        // Dev-only: one switch for watching an AI turn play out. When on it (a) makes the fog
        // overlay follow whichever AI is currently acting instead of staying on the last human's
        // view (see BeginPlayerTurn), showing exactly that AI's own visible/explored/unseen hexes
        // (VisionSystem.CurrentViewer, read through the same per-player Visible/Visited/EverSeen
        // sets AiMapMemory itself reads — this does NOT touch VisionSystem.DebugRevealAll, which
        // stays an independent, separately-controlled reveal-everything override so this debug
        // view shows honestly what the AI could see, not the whole map), and shows that AI's own
        // hand/resource debug panels; and (b) writes one line per stealth-detection challenge to
        // Logs/AiDebug.log / the Console (StealthSystem.DebugLog — observer, hidden unit, hex,
        // spot vs hide dice/hits, outcome; never player-facing, stealth design §3/§9). Off by
        // default; Editor Inspector checkbox only, no in-game UI.
        [SerializeField] private bool debugWatchAiTurns;

        private void OnValidate()
        {
            Game.Map.StealthSystem.DebugLog = debugWatchAiTurns;
        }

        // Only needed for the start-of-turn resource collection below (citadel hex lookup +
        // the citadel yield bonus) — everything else in this controller is pure turn
        // sequencing and doesn't touch the map/config at all.
        [SerializeField] private HexMap map;
        [SerializeField] private GameConfig gameConfig;

        // Hidden until the game actually starts — see BeginGame.
        [SerializeField] private ResourceBarUI resourceBar;

        // Shown every player-turn transition; gates the human's own map input — see
        // HexSelectionController.IsInputAllowed.
        [SerializeField] private TurnInfoPopupUI turnInfoPopup;

        // Hidden until the game actually starts — see BeginGame. Same trigger as resourceBar.
        [SerializeField] private CardHandUI cardHand;
        // Gameplay-side deck source for pre-turn Initiative resource-demand analysis. Existing
        // scenes predate this field, so BeginGame bootstraps it once from CardHandUI when the
        // serialized reference has not been wired yet; Initiative itself never reaches into UI.
        [SerializeField] private StartingDeckCatalog startingDeckCatalog;

        // AI turn visualization (see Game.Ai.AiTurnController) — the camera pans to whatever the
        // AI is doing and armyViewerModal below opens read-only on whichever army it's acting
        // with, so an AI turn looks the same to watch as a human's would.
        [SerializeField] private RtsCameraController cameraController;
        [SerializeField] private HexSelectionController hexSelectionController;

        // Blocking "can't do that" hint (e.g. tried to deploy a card on a hex with no
        // Barracks) — shown by CardHandUI. While it's up, both map input
        // (HexSelectionController.IsInputAllowed) and card dragging (CardHandUI.CanDragCards)
        // stop, the same way a turn handoff does, until dismissed.
        [SerializeField] private SpawnHintPopupUI spawnHintPopup;

        // While the Army Viewer is open, map clicks need to stay locked out (folded into
        // InputBlocked below, same as spawnHintPopup) — but card dragging must NOT be, since
        // dragging a Unit/Hero card from hand onto the open modal's grid is exactly how it
        // deploys straight into that army (see CardHandUI.TryPlayCard). Hence the separate,
        // narrower CardDraggingBlocked below instead of just reusing InputBlocked everywhere.
        [SerializeField] private ArmyViewerModalUI armyViewerModal;
        // Same "map input locked out, card dragging stays on" treatment as armyViewerModal —
        // dragging a Facility card onto the open modal is how it deploys into a slot (see
        // CardHandUI.TryDeployIntoBaseModal). No rename popup of its own, so it never needs to
        // extend CardDraggingBlocked the way armyViewerModal's does.
        [SerializeField] private BaseViewerModalUI baseViewerModal;
        // Same "map input locked out while open, card dragging left on" treatment as
        // armyViewerModal — the Research / Production catalog picker (see
        // HexSelectionController's Research/Production hex actions). No card-drop target of its
        // own, so it only feeds InputBlocked, not CardDraggingBlocked.
        [SerializeField] private ResearchProductionModalUI researchProductionModal;
        // Same "map input locked out entirely" treatment as the other modals above — see
        // HexSelectionController's own battleContactPopup/battleScreen fields (this controller
        // additionally drives their read-only ShowResolved form for delayed battles — see
        // BeginNewTurn/ResolveDelayedBattlesThen).
        [SerializeField] private BattleContactPopupUI battleContactPopup;
        [SerializeField] private BattleScreenUI battleScreen;
        // Same "map input locked out entirely, never a valid card-drop target" treatment as
        // battleContactPopup/battleScreen above — see EventChoicePopupUI/EventRewardPopupUI's own
        // comments (HexSelectionController.Events.cs owns showing/hiding both).
        [SerializeField] private EventChoicePopupUI eventChoicePopup;
        [SerializeField] private EventRewardPopupUI eventRewardPopup;
        // Same "map input locked out entirely, never a valid card-drop target" treatment —
        // Game.Aviation.AviationCombatPresenter opens these directly for an AA reaction/air
        // strike, outside of battleScreen entirely, so they need their own entry here instead of
        // riding along on battleScreen's own VisibilityChanged.
        [SerializeField] private BattleAttackPopupUI aviationAttackPopup;
        [SerializeField] private AaChoicePopupUI aaChoicePopup;

        // Both cached and recomputed only when one of the underlying popups' own
        // VisibilityChanged fires (see OnEnable/RecomputeBlockedState) — this game is
        // turn-based, these flip on discrete open/close actions a handful of times per turn at
        // most, so there was never a reason for every reader (this controller's own Update,
        // HexSelectionController, CardHandUI) to re-derive them from 4-5 live property reads
        // every single frame. The public surface is unchanged — still plain bool properties —
        // so nothing reading InputBlocked/CardDraggingBlocked needed to change.
        private bool _inputBlocked;
        private bool _cardDraggingBlocked;
        public bool InputBlocked => _inputBlocked;

        // Renaming an army additionally blocks card dragging (see ArmyViewerModalUI.
        // IsRenamePopupShowing) — dragging a card over the modal while the player is mid-text-
        // entry doesn't make sense, unlike the rest of the modal which deliberately leaves
        // dragging on. The battle popups block dragging outright — neither one is a place cards
        // can ever be dropped.
        public bool CardDraggingBlocked => _cardDraggingBlocked;

        // Fired whenever InputBlocked/CardDraggingBlocked's cached value actually flips — lets
        // CardHandUI (and anything else) subscribe instead of polling either every frame.
        public event Action<bool> InputBlockedChanged;
        public event Action<bool> CardDraggingBlockedChanged;

        // Re-derives both cached bools from the underlying popups' current IsShowing/
        // IsRenamePopupShowing state — called once up front (OnEnable) and again every time one
        // of them raises VisibilityChanged, never on a timer/every frame.
        private void RecomputeBlockedState()
        {
            bool newInputBlocked = (spawnHintPopup != null && spawnHintPopup.IsShowing)
                || (armyViewerModal != null && armyViewerModal.IsShowing)
                || (baseViewerModal != null && baseViewerModal.IsShowing)
                || (researchProductionModal != null && researchProductionModal.IsShowing)
                || (battleContactPopup != null && battleContactPopup.IsShowing)
                || (battleScreen != null && battleScreen.IsShowing)
                || (eventChoicePopup != null && eventChoicePopup.IsShowing)
                || (eventRewardPopup != null && eventRewardPopup.IsShowing)
                || (aviationAttackPopup != null && aviationAttackPopup.IsShowing)
                || (aaChoicePopup != null && aaChoicePopup.IsShowing);
            bool newCardDraggingBlocked = (spawnHintPopup != null && spawnHintPopup.IsShowing)
                || (armyViewerModal != null && armyViewerModal.IsRenamePopupShowing)
                // Research/Production picker is NOT a card drop-target (unlike Army/Base Viewer,
                // which take Unit/Hero and Facility drops respectively), so the hand must be
                // fully locked out — no draw, no drag, no play — while it's open, same as the
                // battle/event modals.
                || (researchProductionModal != null && researchProductionModal.IsShowing)
                || (battleContactPopup != null && battleContactPopup.IsShowing)
                || (battleScreen != null && battleScreen.IsShowing)
                || (eventChoicePopup != null && eventChoicePopup.IsShowing)
                || (eventRewardPopup != null && eventRewardPopup.IsShowing)
                || (aviationAttackPopup != null && aviationAttackPopup.IsShowing)
                || (aaChoicePopup != null && aaChoicePopup.IsShowing);

            if (newInputBlocked != _inputBlocked)
            {
                _inputBlocked = newInputBlocked;
                InputBlockedChanged?.Invoke(_inputBlocked);
            }
            if (newCardDraggingBlocked != _cardDraggingBlocked)
            {
                _cardDraggingBlocked = newCardDraggingBlocked;
                CardDraggingBlockedChanged?.Invoke(_cardDraggingBlocked);
            }
            RefreshEndTurnInteractable();
        }

        public void ShowSpawnHint(string message)
        {
            spawnHintPopup?.Show(message);
        }

        public int TurnNumber { get; private set; }
        public List<PlayerSetupData> CurrentTurnOrder { get; private set; }

        // Null outside of phase 2, and during Neutral's slot — matches "no real player is
        // acting right now". HexSelectionController checks this to decide whether a unit's
        // selection animation is allowed to play (only the current player's own units).
        public PlayerSetupData CurrentPlayer { get; private set; }

        // False from the moment a turn starts until the human clicks Confirm on
        // TurnInfoPopupUI — HexSelectionController won't allow map input until this is true,
        // even once CurrentPlayer is already the human. Meaningless (left false) for AI/Neutral
        // turns, which never check it.
        public bool TurnConfirmed { get; private set; }

        // Fired right as control passes to the next actor (human, AI, or Neutral) — whatever
        // was selected on the map belongs to the outgoing turn and shouldn't carry over.
        public event Action TurnChanging;

        // Fired once per full turn cycle, right as TurnNumber increments — for UI (ResourceBarUI's
        // turn counter) that only needs to refresh on a new turn instead of polling every frame.
        public event Action<int> TurnStarted;

        // Fired whenever CurrentPlayer or TurnConfirmed changes — lets CardHandUI's
        // CanDragCards-dependent UI (the draw button) react instead of re-checking both every
        // frame just to notice a turn handoff or the human confirming their turn.
        public event Action TurnStateChanged;

        private int _currentPlayerIndex;

        // How many of THIS player's own turns have fully ended so far this game — incremented
        // in AdvanceToNextPlayer for the outgoing CurrentPlayer. Not derivable from
        // TurnNumber: turn order is re-rolled every round, so "the observer's next turn" is
        // not "current round + 1". Game.Map.StealthSystem compares a detection's snapshot of
        // this against the live value to decide when a personal detection lapses (design §4).
        private readonly Dictionary<PlayerSetupData, long> _completedTurns = new Dictionary<PlayerSetupData, long>();
        public long CompletedTurnsFor(PlayerSetupData player)
            => player != null && _completedTurns.TryGetValue(player, out long n) ? n : 0L;

        private readonly Dictionary<PlayerSetupData, List<string>> _pendingAviationMessages = new Dictionary<PlayerSetupData, List<string>>();
        private readonly Queue<string> _aviationMessageQueue = new Queue<string>();

        // Set once at most one player still holds their own starting citadel — two paths lead
        // here now: OnBuildingDestroyed (a building's StructurePoints actually reaching 0 — still
        // unreachable, per the user's own "building damage deferred" call) and, newly reachable,
        // EliminatePlayer via BeginPlayerTurn's own citadel-recapture buffer check below (a
        // starting citadel CAPTURED — ownership changed by BattleScreenUI.Combat.cs's
        // HandleBuildingOnArmyDefeat — rather than destroyed; see the user's own Siege spec).
        // Blocks any further turn advancement once true.
        private bool _gameOver;

        private void OnEnable()
        {
            // OnValidate already applies this on every Inspector edit, but that never fires on a
            // plain scene load/Play Mode entry with the checkbox left untouched — this covers
            // that startup case too.
            StealthSystem.DebugLog = debugWatchAiTurns;
            BuildingRegistry.BuildingDestroyed += OnBuildingDestroyed;
            if (spawnHintPopup != null) spawnHintPopup.VisibilityChanged += RecomputeBlockedState;
            if (spawnHintPopup != null) spawnHintPopup.Hidden += ShowNextAviationMessage;
            if (armyViewerModal != null) armyViewerModal.VisibilityChanged += RecomputeBlockedState;
            if (baseViewerModal != null) baseViewerModal.VisibilityChanged += RecomputeBlockedState;
            if (researchProductionModal != null) researchProductionModal.VisibilityChanged += RecomputeBlockedState;
            if (battleContactPopup != null) battleContactPopup.VisibilityChanged += RecomputeBlockedState;
            if (battleScreen != null) battleScreen.VisibilityChanged += RecomputeBlockedState;
            if (eventChoicePopup != null) eventChoicePopup.VisibilityChanged += RecomputeBlockedState;
            if (eventRewardPopup != null) eventRewardPopup.VisibilityChanged += RecomputeBlockedState;
            if (aviationAttackPopup != null) aviationAttackPopup.VisibilityChanged += RecomputeBlockedState;
            if (aaChoicePopup != null) aaChoicePopup.VisibilityChanged += RecomputeBlockedState;
            RecomputeBlockedState();
        }

        private void OnDisable()
        {
            BuildingRegistry.BuildingDestroyed -= OnBuildingDestroyed;
            if (spawnHintPopup != null) spawnHintPopup.VisibilityChanged -= RecomputeBlockedState;
            if (spawnHintPopup != null) spawnHintPopup.Hidden -= ShowNextAviationMessage;
            if (armyViewerModal != null) armyViewerModal.VisibilityChanged -= RecomputeBlockedState;
            if (baseViewerModal != null) baseViewerModal.VisibilityChanged -= RecomputeBlockedState;
            if (researchProductionModal != null) researchProductionModal.VisibilityChanged -= RecomputeBlockedState;
            if (battleContactPopup != null) battleContactPopup.VisibilityChanged -= RecomputeBlockedState;
            if (battleScreen != null) battleScreen.VisibilityChanged -= RecomputeBlockedState;
            if (eventChoicePopup != null) eventChoicePopup.VisibilityChanged -= RecomputeBlockedState;
            if (eventRewardPopup != null) eventRewardPopup.VisibilityChanged -= RecomputeBlockedState;
            if (aviationAttackPopup != null) aviationAttackPopup.VisibilityChanged -= RecomputeBlockedState;
            if (aaChoicePopup != null) aaChoicePopup.VisibilityChanged -= RecomputeBlockedState;
        }

        // The win condition: destroying a player's starting citadel (see
        // BuildingData.IsStartingCitadel — a later-built "Concord Citadel" card doesn't carry
        // this flag, so losing one of those doesn't end anything). Still unreachable today —
        // see BuildingRegistry.Unregister's own comment, nothing destroys a building's
        // StructurePoints yet, per the user's own "building damage deferred" call.
        // EliminatePlayer is the actually-reachable path now, via BeginPlayerTurn's own
        // citadel-recapture buffer check below.
        private void OnBuildingDestroyed(BuildingData building)
        {
            if (_gameOver || building == null || !building.IsStartingCitadel || building.Owner == null)
                return;
            EliminatePlayer(building.Owner);
        }

        // Shared elimination consequences: releases the player's own Prison contents right now
        // (see ReleasePrisoners — their empire is gone, nowhere left to hold captives), announces
        // it, and re-checks whether the game itself is over. Called from OnBuildingDestroyed
        // above (still unreachable) and from BeginPlayerTurn's own citadel-recapture buffer
        // check (the actually-reachable path today, per the user's own Siege spec: a captured
        // starting citadel doesn't eliminate its owner outright, only if it's STILL not theirs
        // again by the start of their own next turn).
        private void EliminatePlayer(PlayerSetupData player)
        {
            if (player == null || player.IsEliminated)
                return;
            player.IsEliminated = true;

            if (player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue)
                ReleasePrisoners(player, new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value));

            ShowSpawnHint($"{player.Nickname}'s citadel has fallen — {player.Nickname} is defeated.");

            if (GameSession.Players == null)
                return;
            List<PlayerSetupData> survivors = GameSession.Players.FindAll(p => !p.IsEliminated);
            if (survivors.Count > 1)
                return;

            _gameOver = true;
            ShowSpawnHint(survivors.Count == 1 ? $"{survivors[0].Nickname} wins!" : "Draw — no citadels remain.");
        }

        // True once `player` no longer owns the building at their own fixed starting-citadel hex
        // — covers both a captured citadel (ownership flipped, see BattleScreenUI.Combat.cs's
        // HandleBuildingOnArmyDefeat) and the degenerate "no building there at all" case. Checked
        // fresh every time this player's own turn is about to start (see BeginPlayerTurn) rather
        // than cached — that's what gives the user's own "buffer" its actual length: a capture
        // during ANY other turn still leaves the full stretch until this player's own next turn
        // to retake it.
        private static bool StartingCitadelLost(PlayerSetupData player)
        {
            if (!player.CitadelHexQ.HasValue || !player.CitadelHexR.HasValue)
                return false;
            var hex = new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value);
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building == null || building.Owner != player;
        }

        // The answer to "what happens to prisoners when the empire holding them is destroyed" —
        // there's no "citadel changes hands without being destroyed" mechanic in this project
        // (destroying a starting citadel eliminates its owner outright, see OnBuildingDestroyed's
        // own comment), so this is the one place a captured hero (see BattleScreenUI.Combat.cs's
        // TryImprison) can ever leave a Prison again: back to whoever it was
        // UnitData.CapturedFrom, landing in THEIR garrison — same "deployed cards land in the
        // garrison first" convention CardHandUI.TryPlayCard already uses. A prisoner whose
        // original owner has ALSO since been eliminated (no citadel hex on record any more, or no
        // garrison found there) just stays discarded — nowhere left to send it.
        //
        // Same "wired for correctness, currently unreachable" status as the rest of this event
        // chain — BuildingRegistry.Unregister (which raises BuildingDestroyed at all) has no
        // caller yet, since nothing can destroy a building's StructurePoints today.
        private static void ReleasePrisoners(PlayerSetupData defeatedOwner, HexCoord citadelHex)
        {
            ArmyData prison = ArmyRegistry.AllAt(citadelHex).Find(a => a.IsPrison && a.Owner == defeatedOwner);
            if (prison == null || prison.Members.Count == 0)
                return;

            foreach (UnitData hero in new List<UnitData>(prison.Members))
            {
                PlayerSetupData originalOwner = hero.CapturedFrom;
                prison.Members.Remove(hero);
                if (originalOwner == null || !originalOwner.CitadelHexQ.HasValue || !originalOwner.CitadelHexR.HasValue)
                    continue;

                var originalCitadelHex = new HexCoord(originalOwner.CitadelHexQ.Value, originalOwner.CitadelHexR.Value);
                ArmyData garrison = ArmyRegistry.FindGarrisonAt(originalCitadelHex, originalOwner);
                if (garrison == null)
                    continue;

                hero.Owner = originalOwner;
                hero.IsPrisoner = false;
                hero.CapturedFrom = null;
                garrison.AddMemberSorted(hero);
            }
        }

        // Kept in sync via TurnStateChanged/InputBlockedChanged instead of every frame — the
        // player must not be able to end the turn while either is up: a battle needs actually
        // deciding (fight/delay) or acknowledging before the turn can move on.
        private void RefreshEndTurnInteractable()
        {
            if (endTurnButton != null && endTurnButton.gameObject.activeInHierarchy)
                endTurnButton.interactable = TurnConfirmed && !InputBlocked;
        }

        // The same physical Enter press that just dismissed TurnInfoPopupUI's "Your turn, X"
        // popup — Unity's own Submit action fires on Enter too, straight to whichever Selectable
        // is currently selected, entirely separate from TurnInfoPopupUI's own Update (that one
        // only polls Space, see its own comment) — must never ALSO end the turn below in that
        // same frame. OnTurnConfirmed flips endTurnButton.interactable to true synchronously as
        // part of handling that Submit, so without this guard the Enter-key poll right below sees
        // an already-interactable button and a still-true wasPressedThisFrame for the very same
        // keypress, ending the turn before the player ever got to act on it (see the user's own
        // report: one Enter both dismissed the turn-start message and skipped the turn outright).
        private int _turnConfirmedFrame = -1;

        // Enter ends the human's turn whenever the button itself would currently accept a
        // click — active AND interactable, so this can't fire during an AI/Neutral pass or
        // before the button's own listener is wired up for the new current player. Only the
        // keyboard poll itself has to stay per-frame (Unity's Input System has no "was this key
        // pressed" event to subscribe to) — the button's own interactable state above no longer
        // does.
        private void Update()
        {
            if (endTurnButton == null || !endTurnButton.gameObject.activeInHierarchy || !endTurnButton.interactable)
                return;
            if (Keyboard.current == null)
                return;
            if (!Keyboard.current.enterKey.wasPressedThisFrame && !Keyboard.current.numpadEnterKey.wasPressedThisFrame)
                return;
            // Typing an army's new name (RenameArmyPopupUI) can easily include a space or land
            // on Enter — this must never also end the turn. Checked only once Enter is
            // actually confirmed pressed, not every frame — the EventSystem/GetComponent
            // lookup isn't free.
            if (UIFocusUtility.IsTextFieldFocused())
                return;
            if (_turnConfirmedFrame == Time.frameCount)
                return;
            OnEndTurnClicked();
        }

        // Called once, right after every player has placed their citadel.
        public void BeginGame()
        {
            if (resourceBar != null)
                resourceBar.Show();
            if (cardHand != null)
                cardHand.Show();
            // Existing scenes already carry the same deck catalog on CardHandUI. Copy it once
            // into the gameplay-owned field when the new direct reference has not been assigned;
            // from this point the Initiative module is independent of the UI object.
            if (startingDeckCatalog == null && cardHand != null)
                startingDeckCatalog = cardHand.StartingDeckCatalog;
            // Shown once, same trigger as resourceBar, and never hidden again — only its
            // interactable state changes from here on (see BeginPlayerTurn/OnTurnConfirmed).
            if (endTurnButton != null)
            {
                endTurnButton.gameObject.SetActive(true);
                endTurnButton.interactable = false;
                endTurnButton.onClick.RemoveAllListeners();
                endTurnButton.onClick.AddListener(OnEndTurnClicked);
            }
            TurnNumber = 0;
            _completedTurns.Clear();
            // StealthSystem is otherwise turn-controller-agnostic (the stealth sim drives it
            // by hand) — hand it the live completed-turn count now that a real game is starting.
            Game.Map.StealthSystem.CompletedTurnsProvider = CompletedTurnsFor;
            // A detection scored during the observer's OWN turn must last through the end of
            // their NEXT turn — their completed-turn count isn't bumped until the current turn
            // ends, so MarkDetected needs to know "is this observer the one acting right now".
            Game.Map.StealthSystem.ObserverTakingTurnProvider = p => p != null && p == CurrentPlayer;
            BeginNewTurn();
        }

        // Every player has just passed (Neutral's own fixed last slot in phase 2 — see
        // BeginPlayerTurn) — exactly when the manual's Delay Attack says a delayed battle
        // actually starts. Drained here, one at a time, before the new round's dice-off gets a
        // chance to run — see ResolveDelayedBattlesThen. Always starts the coroutine, even with
        // an empty DelayedBattleRegistry — ResolveDelayedBattlesThen's own loop already falls
        // back to TryFindNextContestedBattle in that case, which is the ONLY place that sweeps
        // for a leftover contested hex nobody ever explicitly Delayed (e.g. a hero that Escaped
        // a Capture Kill Challenge started via direct Fight contact — the hunter army just stays
        // parked on its hex, never queued in DelayedBattleRegistry at all). Gating the coroutine
        // itself behind HasAny, like this used to, skipped that fallback sweep entirely whenever
        // no EXPLICIT delay was pending, leaving a stuck pair like that coexisting forever — the
        // enemy-present check in RefreshResourceActionRow then keeps that hex's own extraction
        // Facility action permanently unavailable, since nothing ever forces the standoff to a
        // conclusion (see the user's own report).
        private void BeginNewTurn()
        {
            if (_gameOver)
                return;

            StartCoroutine(ResolveDelayedBattlesThen(ProceedWithNewTurn));
        }

        private IEnumerator ResolveDelayedBattlesThen(Action onDone)
        {
            while (true)
            {
                // Once every EXPLICITLY delayed battle has drained, sweep the whole map for
                // anything still left contested — per the user's own call, "no stealth yet"
                // means two different-owner armies can only ever coexist on a hex TEMPORARILY,
                // never past this method. Most such leftovers are a SECOND attacker that walked
                // onto a hex whose target was already reserved for one of the battles just
                // resolved above (see DelayedBattleRegistry.IsHexPending's own callers) — now
                // that the reservation's cleared, this is what actually forces their fight too,
                // still within this same turn-boundary pass. Looped (not just checked once) since
                // resolving ANY battle here can just as easily reveal another.
                if (!DelayedBattleRegistry.HasAny)
                {
                    if (!TryFindNextContestedBattle(out HexCoord contestedHex, out List<ArmyData> contestedParticipants))
                        break;
                    DelayedBattleRegistry.Add(new PendingBattle { Hex = contestedHex, Participants = contestedParticipants });
                }

                PendingBattle battle = DelayedBattleRegistry.TakeNext();

                // Participants were captured back when Delay was chosen — anything can have
                // happened to that same pairing since (one side fought and won/retreated through
                // an unrelated direct contact, an army was destroyed elsewhere, etc.). Re-check
                // it's still a genuine, current fight on this hex before reopening the battle
                // screen for it — otherwise a stale delay could resurface a fight the player
                // already resolved a completely different way, on the very hex they just left.
                if (!IsStillAGenuineBattle(battle))
                    continue;

                // STEALTH-COMBAT-01: this delayed/contested pairing is a committed encounter the
                // instant it's drained here — reveal right now, before ShowResolved (or a direct
                // Show()/BeginCaptureKillEncounter for a no-human pairing) below ever displays it.
                Game.Combat.BattleEncounterCoordinator.PrepareCommittedEncounter(battle.Hex, battle.Participants,
                    battle.Participants.Count > 0 ? battle.Participants[0].Owner : null);

                // ShowResolved only makes sense when a human is actually there to click its own
                // "Continue" button — same human-only gating every other contact point in this
                // project already applies (see HexSelectionController.Movement.cs's own onFight/
                // onDelay branch). Ordinary AI-vs-Neutral contact no longer reaches this drain at
                // all (it fights immediately on contact now — see that same branch's own comment),
                // but TryFindNextContestedBattle's own fallback sweep just above can still surface
                // a leftover pairing with no human on either side (its own comment: e.g. a hero
                // that Escaped a Capture Kill Challenge). Without this check that used to wait on a
                // click nobody was ever going to make — the exact same hang, just through a rarer
                // door.
                bool acknowledged = false;
                bool anyHuman = battle.Participants.Any(a => a.Owner != null && a.Owner.IsHuman);
                if (battleContactPopup != null && anyHuman)
                    battleContactPopup.ShowResolved(battle.Hex, battle.Participants, () => acknowledged = true);
                else
                    acknowledged = true;
                yield return new WaitUntil(() => acknowledged);

                bool closed = false;
                // Same hero-only branch as HexSelectionController.Movement.cs's own contact
                // handling — nothing for a Ground Combat round to do against a target with no
                // units, so this goes straight to a Capture Kill Challenge sequence instead (see
                // BattleScreenUI.BeginCaptureKillEncounter). Participants[0] is always the
                // original mover/hunter — see IsStillAGenuineBattle's own comment.
                bool targetHeroOnly = battle.Participants.Count > 1 && !BattleInitiator.IsCombatCapable(battle.Participants[1]);
                if (battleScreen != null && targetHeroOnly)
                    battleScreen.BeginCaptureKillEncounter(battle.Participants[0], battle.Participants[1], () => closed = true);
                else if (battleScreen != null)
                    battleScreen.Show(battle.Hex, battle.Participants, () => closed = true);
                else
                    closed = true;
                yield return new WaitUntil(() => closed);
            }
            onDone();
        }

        // The end-of-drain sweep ResolveDelayedBattlesThen falls back on once
        // DelayedBattleRegistry is empty — scans every occupied hex for a still-unresolved
        // conflict (armies of different owners, per the user's own "this applies to every
        // battle, not just building ones" call) and hands back a fresh PendingBattle-shaped
        // pairing for it, same as if the player had chosen Delay on it directly. `mover` (always
        // Participants[0]) must be combat-capable to match IsStillAGenuineBattle's own
        // requirement — a hex where every engageable army present is hero-only (nobody able to
        // hunt, see BattleInitiator.IsEngageable's own note) is left alone rather than returned
        // here, same as it already is everywhere else in this project; otherwise this would loop
        // forever trying to "resolve" a pairing nothing can ever actually fight. The defending
        // side (`other`) is picked via BattleInitiator.FindEnemyAt rather than a hand-rolled
        // "first engageable enemy" scan (2026-08-21 fix, consistency follow-up to FindEnemyAt's
        // own strongest-defender fix) — a multi-army hex left with several unresolved contests at
        // once (e.g. a delayed battle plus a second, uninvolved attacker that also landed there)
        // should keep offering up its strongest remaining defender each time this sweep asks, not
        // whichever one the registry happens to enumerate first.
        private static bool TryFindNextContestedBattle(out HexCoord hex, out List<ArmyData> participants)
        {
            foreach (HexCoord candidateHex in ArmyRegistry.AllOccupiedHexes())
            {
                List<ArmyData> armies = ArmyRegistry.AllAt(candidateHex);
                ArmyData mover = null;
                foreach (ArmyData candidate in armies)
                    if (BattleInitiator.IsCombatCapable(candidate)) { mover = candidate; break; }
                if (mover == null)
                    continue;

                ArmyData other = BattleInitiator.FindEnemyAt(candidateHex, mover.Owner);
                if (other == null)
                    continue;

                hex = candidateHex;
                participants = new List<ArmyData> { mover, other };
                return true;
            }
            hex = default;
            participants = null;
            return false;
        }

        // Both original participants must still be sitting on the delayed hex and still opposing
        // owners — anything less means this exact confrontation isn't real any more (see
        // ResolveDelayedBattlesThen's own comment). Participants[0] (the original mover/hunter,
        // see HexSelectionController.Movement.cs's own convention) additionally still needs real
        // units of its own — a hero-only army can't fight OR hunt (see BattleInitiator.
        // IsEngageable's own note) — but the OTHER side only needs IsEngageable: a target that's
        // since been ground down to hero-only is still a genuine Capture Kill Challenge target
        // (see ResolveDelayedBattlesThen's own branch), just not a Ground Combat one any more.
        private static bool IsStillAGenuineBattle(PendingBattle battle)
        {
            if (battle?.Participants == null || battle.Participants.Count < 2)
                return false;
            if (!BattleInitiator.IsCombatCapable(battle.Participants[0]) || !battle.Participants[0].Hex.Equals(battle.Hex))
                return false;
            foreach (ArmyData army in battle.Participants)
                if (army == null || !army.Hex.Equals(battle.Hex) || !BattleInitiator.IsEngageable(army))
                    return false;
            ArmyData first = battle.Participants[0];
            for (int i = 1; i < battle.Participants.Count; i++)
                if (battle.Participants[i].Owner == first.Owner)
                    return false;
            return true;
        }

        private void ProceedWithNewTurn()
        {
            if (_gameOver)
                return;

            TurnNumber++;
            TurnStarted?.Invoke(TurnNumber);
            CurrentPlayer = null; // no one's turn during the dice-off itself
            TurnStateChanged?.Invoke();

            if (endTurnButton != null)
                endTurnButton.interactable = false;
            if (turnInfoPopup != null)
                turnInfoPopup.Hide();

            if (turnOrderPopup == null || GameSession.Players == null || GameSession.Players.Count == 0)
                return;

            CollectResourceIncome();

            foreach (PlayerSetupData player in GameSession.Players)
                PlayerRootRegistry.FindFor(player)?.ResetBonusInitiativeDice();

            // Initiative has one implementation now. Every AI plans from the same immutable
            // pre-purchase state, then all paid purchases are applied before the human sees the
            // popup. There is no V1/random/free fallback and no strategy-version flag at this
            // round-boundary decision point.
            Game.Ai.V2.Initiative.InitiativeCoordinatorV2.PlanAndApplyForAll(
                GameSession.Players, map, startingDeckCatalog, TurnNumber);

            turnOrderPopup.Show(GameSession.Players, OnTurnOrderResolved);
        }

        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        // Every building on the map collects its own hex's yield now — right at the start of
        // the turn, before the initiative roll, so the buying step (and the roll itself) sees
        // this turn's income, not last turn's. A hex's total yield already folds in whatever
        // permanent bonus was stamped onto it (see HexResourceBonusRegistry — a citadel's own
        // bonus belongs to the hex the player chose, not to the citadel's continued presence);
        // every building (citadel or a hero-built extraction Facility/resource site) only ever
        // COLLECTS 1 unit per resource type it has a matching CollectX ability for, capped by
        // whatever the hex actually offers — a rich hex needs real extraction Facilities built
        // on it to be fully exploited (see BuildingData.CollectedAmount). Whatever headroom is
        // left after the building's own cut then goes to any army SITTING on the hex with a
        // matching CollectX unit (see CollectArmyIncomeAt) — so a hex needs no building at all
        // for an army alone to work it, and iterates every occupied hex too, not just built ones.
        private void CollectResourceIncome()
        {
            if (map == null || gameConfig == null)
                return;

            HashSet<HexCoord> hexes = new HashSet<HexCoord>();
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
                hexes.Add(building.Hex);
            foreach (HexCoord hex in ArmyRegistry.AllOccupiedHexes())
                hexes.Add(hex);

            foreach (HexCoord hex in hexes)
            {
                if (!map.TryGetTerrainAt(hex, out TerrainTypeEntry entry))
                    continue;

                ResourceYields hexYield = HexResourceCalculator.GetEffectiveYield(entry, HexResourceBonusRegistry.GetBonus(hex));
                if (!hexYield.HasAnyYield)
                    continue;

                BuildingData building = BuildingRegistry.FindAt(hex);
                PlayerRoot buildingRoot = building?.Owner != null ? PlayerRootRegistry.FindFor(building.Owner) : null;

                foreach (ResourceType type in AllResourceTypes)
                {
                    int hexAmount = hexYield.Get(type);
                    if (hexAmount <= 0)
                        continue;

                    int remaining = hexAmount;

                    if (buildingRoot != null)
                    {
                        int buildingCollected = Mathf.Min(building.CollectedAmount(type), remaining);
                        if (buildingCollected > 0)
                        {
                            buildingRoot.AddResource(type, buildingCollected);
                            remaining -= buildingCollected;
                        }
                    }

                    if (remaining > 0)
                        CollectArmyIncomeAt(hex, type, remaining);
                }
            }
        }

        // Mirrors BuildingData.CollectedAmount, but for whatever headroom is left of the hex's
        // yield once the building there (if any) already took its own cut — a unit with a
        // matching CollectX ability contributes 1, same rate as a citadel/facility's own baked-in
        // ability, but purely for as long as its army is actually parked on the hex; nothing here
        // is ever cached across turns, so the contribution vanishes the instant the army leaves
        // or the unit's removed/killed. Grouped and credited per ARMY OWNER rather than always
        // going to the building's owner, since the whole point is letting a player with no
        // building on the hex at all still collect via a unit alone (see the user's own two
        // examples: a bare hex, and a partially-worked one with a Facility already on it). An
        // owner whose army shares the hex with an engageable enemy army gets nothing this turn —
        // "no stealth yet" (see ResolveDelayedBattlesThen) only forces a fight between COMBAT-
        // CAPABLE armies, so two hero-only armies of different owners can still coexist on a hex
        // indefinitely, which is exactly the "shared with an enemy" case this guard exists for.
        private static void CollectArmyIncomeAt(HexCoord hex, ResourceType type, int remaining)
        {
            string ability = UnitAbilities.CollectAbilityFor(type);

            foreach (IGrouping<PlayerSetupData, ArmyData> ownerArmies in ArmyRegistry.AllAt(hex).GroupBy(a => a.Owner))
            {
                if (remaining <= 0)
                    break;

                PlayerSetupData owner = ownerArmies.Key;
                if (owner == null)
                    continue;

                ArmyData enemy = BattleInitiator.FindEnemyAt(hex, owner);
                if (enemy != null)
                    continue;

                int unitCount = ownerArmies.Sum(army => army.Members.Count(unit => unit.HasAbility(ability)));
                if (unitCount <= 0)
                    continue;

                PlayerRoot root = PlayerRootRegistry.FindFor(owner);
                if (root == null)
                    continue;

                int granted = Mathf.Min(unitCount, remaining);
                root.AddResource(type, granted);
                remaining -= granted;
            }
        }

        private void OnTurnOrderResolved(List<PlayerSetupData> order)
        {
            CurrentTurnOrder = order;

            // Public record of what everyone visibly ended up buying this round — the ONLY
            // opponent information next round's Initiative AI is allowed to use (see
            // InitiativePublicHistory). Taken now, after the roll, before next turn's
            // ResetBonusInitiativeDice wipes the counts.
            foreach (PlayerSetupData player in GameSession.Players)
            {
                PlayerRoot historyRoot = PlayerRootRegistry.FindFor(player);
                if (historyRoot != null)
                    InitiativePublicHistory.RecordFinalBonusDice(player, historyRoot.BonusInitiativeDice);
            }

            AllocateActionPoints(order);
            GrantPrisonBonusActionPoints(order);
            GrantApBonusActionPoints(order);
            BeginPlayerTurn(0);
        }

        // AP for this turn, by initiative rank — replaces whatever was left over from last
        // turn rather than adding to it. Neutral never appears in `order` so it never gets AP.
        private static void AllocateActionPoints(List<PlayerSetupData> order)
        {
            for (int i = 0; i < order.Count; i++)
            {
                PlayerRoot root = PlayerRootRegistry.FindFor(order[i]);
                if (root != null)
                {
                    int rankAp = InitiativeRules.ApForRank(i);
                    root.ActionPoints = rankAp;
                    root.SetLastApFromInitiative(rankAp);
                }
            }
        }

        // Manual's own "Bonuses For Captured Heroes" (pg. 11): recurring every turn, for as long
        // as a hero sits in that player's Prison (see ArmyData.IsPrison / BattleScreenUI.
        // Combat.cs's TryImprison) — added ON TOP of AllocateActionPoints's own by-rank replace
        // above, not folded into ActionPointsForRank, which has nothing to do with prisoners.
        // 2 AP per hero rather than the manual's own 3, per the user's own explicit call.
        private const int PrisonBonusActionPointsPerHero = 2;

        private static void GrantPrisonBonusActionPoints(List<PlayerSetupData> order)
        {
            foreach (PlayerSetupData player in order)
            {
                PlayerRoot root = PlayerRootRegistry.FindFor(player);
                if (root == null)
                    continue;
                int prisonerCount = ArmyRegistry.AllForOwner(player).Where(a => a.IsPrison).Sum(a => a.Members.Count);
                int bonus = PrisonBonusActionPointsPerHero * prisonerCount;
                root.SetLastApFromPrisonBonus(bonus);
                if (bonus > 0)
                    root.ActionPoints += bonus;
            }
        }

        // UnitAbilities.ApBonus (new skill, works on any card type): +2 AP on this player's turn
        // per carrier actually IN PLAY — a member of one of their own (non-Prison) armies, or a
        // Base/Facility they own — added on top of AllocateActionPoints's by-rank base, same
        // "on top" treatment as GrantPrisonBonusActionPoints just above. Prison armies are
        // skipped for the SAME reason GrantPrisonBonusActionPoints only ever reads them: a
        // captured hero (TryImprison sets hero.Owner to its CAPTOR) sits in the captor's own
        // Prison army, not free to act for them — it already earns the captor a separate,
        // ability-independent prison bonus, it shouldn't also earn them this skill's bonus just
        // for having originally carried it.
        private const int ApBonusPerSource = 2;

        private static void GrantApBonusActionPoints(List<PlayerSetupData> order)
        {
            foreach (PlayerSetupData player in order)
            {
                PlayerRoot root = PlayerRootRegistry.FindFor(player);
                if (root == null)
                    continue;

                int sources = 0;
                List<string> breakdown = new List<string>();
                foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
                {
                    if (army.IsPrison)
                        continue;
                    foreach (UnitData unit in army.Members)
                    {
                        if (!unit.HasAbility(UnitAbilities.ApBonus))
                            continue;
                        sources++;
                        breakdown.Add($"{unit.Name} +{ApBonusPerSource}");
                    }
                }
                foreach (BuildingData building in BuildingRegistry.AllBuildings())
                {
                    if (building.Owner != player)
                        continue;
                    if (building.HasAbility(UnitAbilities.ApBonus))
                    {
                        sources++;
                        breakdown.Add($"Base at ({building.Hex.Q},{building.Hex.R}) +{ApBonusPerSource}");
                    }
                    foreach (FacilityData facility in building.FacilitySlots)
                        if (facility != null && facility.HasAbility(UnitAbilities.ApBonus))
                        {
                            sources++;
                            breakdown.Add($"{facility.Name} at ({building.Hex.Q},{building.Hex.R}) +{ApBonusPerSource}");
                        }
                }

                int bonus = ApBonusPerSource * sources;
                root.SetLastApFromApBonus(bonus);
                root.SetLastApBonusSources(string.Join(", ", breakdown));
                if (bonus > 0)
                    root.ActionPoints += bonus;
            }
        }

        private void BeginPlayerTurn(int index)
        {
            if (_gameOver)
                return;

            TurnChanging?.Invoke();
            _currentPlayerIndex = index;
            TurnConfirmed = false;
            // Re-enabled only once TurnConfirmed (human, after dismissing TurnInfoPopupUI) —
            // see OnTurnConfirmed. Button itself stays visible the whole time, this is the only
            // state that changes.
            if (endTurnButton != null)
                endTurnButton.interactable = false;

            // Past the last real player — Neutral's fixed last slot.
            if (index >= CurrentTurnOrder.Count)
            {
                CurrentPlayer = null;
                ReplenishMoveForOwner(null);
                if (turnInfoPopup != null)
                    turnInfoPopup.ShowForOther(null);
                TurnStateChanged?.Invoke();
                StartCoroutine(PassAfterDelay(BeginNewTurn));
                return;
            }

            PlayerSetupData player = CurrentTurnOrder[index];

            // The buffer window from the user's own Siege spec: a captured starting citadel
            // doesn't eliminate its owner the instant it changes hands, only if they still
            // haven't retaken that exact hex by the moment their OWN next turn would otherwise
            // begin (see StartingCitadelLost/EliminatePlayer above). Checked before CurrentPlayer
            // is even assigned, so an eliminated player is never treated as "acting" even
            // momentarily.
            if (!player.IsEliminated && StartingCitadelLost(player))
                EliminatePlayer(player);
            if (player.IsEliminated)
            {
                AdvanceToNextPlayer();
                return;
            }

            CurrentPlayer = player;
            ReplenishMoveForOwner(player);
            // The map is rendered from whichever human's turn it currently is (see
            // VisionSystem.CurrentViewer) — a hot-seat game can have several human players
            // sharing one screen, so this has to track CurrentPlayer, not "the" human (there
            // isn't always exactly one). Left untouched on an AI/Neutral turn — no screen to
            // switch to, so the last human's own view stays up rather than going blank — unless
            // debugWatchAiTurns opts into watching that AI's own vision instead.
            if (player.IsHuman || debugWatchAiTurns)
                VisionSystem.CurrentViewer = player;
            TurnStateChanged?.Invoke();

            if (player.IsHuman)
            {
                cardHand?.HideAiHandDebug();
                resourceBar?.HideRootDebug();
                if (turnInfoPopup != null)
                    turnInfoPopup.ShowForHuman(player, OnTurnConfirmed);
            }
            else
            {
                // An AI detector never gets the SpawnHintPopupUI (that's human turn-start
                // only, via OnTurnConfirmed) — drop its queued detection notices here so they
                // don't accumulate forever.
                StealthSystem.TakeDetectionNotices(player);
                if (turnInfoPopup != null)
                    turnInfoPopup.ShowForOther(player);
                // debugWatchAiTurns' hand/resources half (see ShowAiHandDebug/ShowRootDebug's
                // own comments) — shown before RunTurn starts so both are already visible for the
                // very first decision; both stay live for the rest of the turn off their own
                // change events (AiHandData.HandChanged, PlayerRoot.ResourcesChanged) rather than
                // any per-step push from RunTurn.
                if (debugWatchAiTurns && cardHand != null)
                    cardHand.ShowAiHandDebug(AiHandRegistry.GetOrCreate(player, cardHand.StartingDeckCatalog, cardHand.StartingHandSize));
                if (debugWatchAiTurns)
                    resourceBar?.ShowRootDebug(PlayerRootRegistry.FindFor(player));
                AiTurnContext ctx = AiTurnContext.From(cameraController, map, hexSelectionController,
                    cardHand, aiStepDelay, gameConfig, TurnNumber,
                    researchProductionModal != null ? researchProductionModal.Catalog : null);
                StartCoroutine(AiTurnController.RunTurn(player, ctx, AdvanceToNextPlayer));
            }
        }

        // Fired by TurnInfoPopupUI's Confirm button — the only thing that actually lets the
        // human touch the map (and end their turn) this turn — see
        // HexSelectionController.IsInputAllowed.
        private void OnTurnConfirmed()
        {
            TurnConfirmed = true;
            _turnConfirmedFrame = Time.frameCount;
            if (endTurnButton != null)
                endTurnButton.interactable = true;
            if (turnInfoPopup != null)
                turnInfoPopup.Hide();
            if (CurrentPlayer != null)
            {
                bool anyQueued = false;
                if (_pendingAviationMessages.TryGetValue(CurrentPlayer, out List<string> messages))
                {
                    _pendingAviationMessages.Remove(CurrentPlayer);
                    foreach (string message in messages)
                        _aviationMessageQueue.Enqueue(message);
                    anyQueued |= messages.Count > 0;
                }
                // Stealth-detection announcements — shown to (and only to) the player who
                // rolled the successful detection, right AFTER the aviation damage messages
                // and through the same one-at-a-time SpawnHintPopupUI queue. The hidden
                // unit's owner is still told nothing (design §4/§16).
                foreach (string notice in StealthSystem.TakeDetectionNotices(CurrentPlayer))
                {
                    _aviationMessageQueue.Enqueue(notice);
                    anyQueued = true;
                }
                if (anyQueued)
                    ShowNextAviationMessage();
            }
            TurnStateChanged?.Invoke();
        }

        // Restores move points for every unit belonging to whoever's turn is starting — player
        // is null for Neutral's slot, matching ArmyData.Owner for any Neutral armies. Units have
        // no registry of their own any more (see Game.Map.ArmyController) — reached only through
        // the armies that contain them. Hero Fate is deliberately NOT touched here any more — it
        // refills as soon as each battle ends now (see UnitData.ReplenishFateForNewBattle/
        // BattleScreenUI.Combat.cs's OnBattleOutcomeAcknowledged), not per strategic turn.
        private static void ReplenishMoveForOwner(PlayerSetupData player)
        {
            // AA availability is turn-scoped the same way move points are (see AntiAirState's
            // own comment) — reset alongside everything else this owner's fresh turn restores.
            AntiAirState.ResetForOwner(player);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                foreach (UnitData unit in army.Members)
                {
                    unit.ReplenishMoveForNewTurn();
                    if (unit.IsAviation)
                        unit.HasAirAttackedThisTurn = false;
                }
                // Activation is tracked per-ARMY, not per-unit (see ArmyData.
                // HasActivatedThisTurn) — a unit never moves on its own.
                army.HasActivatedThisTurn = false;
            }
        }

        // Stands in for "the AI/Neutral thought about it and had nothing to do" — the same short
        // fixed pacing step used by ordinary strategic AI actions.
        private IEnumerator PassAfterDelay(Action onDone)
        {
            yield return new WaitForSeconds(aiStepDelay);
            onDone();
        }

        private void OnEndTurnClicked()
        {
            AdvanceToNextPlayer();
        }

        private void AdvanceToNextPlayer()
        {
            var aviationMessages = AviationTurnLifecycle.ResolveEndOfTurn(CurrentPlayer, hexSelectionController);
            if (aviationMessages.Count > 0)
                _pendingAviationMessages[CurrentPlayer] = aviationMessages;

            // The outgoing player's turn has now fully ended — bump their completed-turn
            // count and lapse any personal detections that were only valid "through the end
            // of this player's next turn" (see _completedTurns / StealthSystem).
            if (CurrentPlayer != null)
            {
                _completedTurns[CurrentPlayer] = CompletedTurnsFor(CurrentPlayer) + 1;
                Game.Map.StealthSystem.PurgeExpiredFor(CurrentPlayer);
            }

            BeginPlayerTurn(_currentPlayerIndex + 1);
        }

        private void ShowNextAviationMessage()
        {
            if (spawnHintPopup == null || spawnHintPopup.IsShowing || _aviationMessageQueue.Count == 0)
                return;
            spawnHintPopup.Show(_aviationMessageQueue.Dequeue());
        }
    }
}
