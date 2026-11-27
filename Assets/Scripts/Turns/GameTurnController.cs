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
using Game.Progression;
using Game.Terrain;
using Game.UI;
using Game.Units;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

namespace Game.Turns
{
    // Owns the overall turn loop: phase 1 (tokens-off for turn order, redone every turn) then
    // phase 2 (each player takes their turn, in that order, human or AI alike — an AI "turn"
    // just passes after a short pause for now, since there's nothing for it to actually
    // decide yet). When the last real player finishes, phase 1 runs again for the next turn.
    //
    // The tokens-off only ever sees GameSession.Players — real configured players, human or AI.
    // The neutral faction (PlayerRoot.Create(null, "Neutral") in CitadelSetupController) was
    // never added to that list, so it's excluded from the roll automatically. It still gets a
    // fixed last slot in phase 2 (index == CurrentTurnOrder.Count, represented as `null`
    // everywhere a real player would be) — same pass-after-a-pause treatment as AI, since it
    // has no actions of its own yet either.
    public partial class GameTurnController : MonoBehaviour
    {
        [SerializeField] private TurnOrderPopupUI turnOrderPopup;
        [SerializeField] private Button endTurnButton;
        [SerializeField] private float aiStepDelay = 0.5f;

        // Spectator controls for AI-only matches. The button is a normal scene element placed
        // beside the game-menu button and wired here through the Inspector. A pause request never
        // freezes a coroutine mid-command: V2 reaches PauseAtAiActionBoundary only after the current
        // atomic action has completed, then exposes a read-only inspection window until Play is pressed.
        [SerializeField] private Button aiObserverPauseButton;
        private bool _aiObserverMatch;
        private bool _aiObserverPauseRequested;
        private bool _aiObserverPauseEngaged;
        // The open game menu holds the spectated AI at its next action boundary, like Pause,
        // without touching the spectator's own Pause/Play choice.
        private bool _aiObserverMenuHold;
        private bool AiObserverHoldRequested => _aiObserverPauseRequested || _aiObserverMenuHold;
        public bool IsAiObserverMatch => _aiObserverMatch;
        public bool IsAiObserverPaused => _aiObserverPauseEngaged;
        // Map inspection belongs to the spectator's own Pause; a hold by the open game menu
        // alone stops the AI without opening the map for clicks.
        public bool IsAiObserverInspectionMode => _aiObserverMatch && _aiObserverPauseEngaged
            && _aiObserverPauseRequested && !IsCombatPresentationActive;

        // Dev-only: follows the acting AI's own fog of war and shows its hand/resources
        // in matches with a human. AI-only matches enable this view automatically.
        // Does not affect the independent full-map reveal override below.
        [SerializeField] private bool debugWatchAiTurns;

        // Dev-only, independent of debugWatchAiTurns above: reveals the ENTIRE map (every hex,
        // every army/building marker, via VisionSystem.DebugRevealAll — see that property's own
        // comment) regardless of whose turn it is or what the current viewer has actually
        // explored. Lets the whole roster/garrison composition be inspected (clicking any now-
        // visible army marker still opens the normal read-only ArmyViewerModalUI — see
        // HexSelectionController.TryHandleEnemyArmyMarkerClick) without switching whose honest
        // vision debugWatchAiTurns shows. Off by default; Editor Inspector checkbox only, no
        // in-game UI, same convention as debugWatchAiTurns.
        [SerializeField] private bool debugRevealFullMap;

        private void OnValidate()
        {
            Game.Map.VisionSystem.DebugRevealAll = debugRevealFullMap;
        }

        // Only needed for the start-of-turn resource collection below (citadel hex lookup +
        // the citadel yield bonus) — everything else in this controller is pure turn
        // sequencing and doesn't touch the map/config at all.
        [SerializeField] private HexMap map;
        [SerializeField] private GameConfig gameConfig;

        // Its own panel, shown/hidden together with cardHand (see CardHandUI.Show/Hide) — no
        // activation calls of its own. Kept only for the AI-watch debug view.
        [SerializeField] private ResourceBarUI resourceBar;

        // CardHandPanel: the hand plus endTurnButton; also drives resourceBar. Hidden until the
        // game actually starts — see BeginGame.
        [SerializeField] private CardHandUI cardHand;
        // Gameplay-side deck source for pre-turn Initiative resource-demand analysis. Existing
        // scenes predate this field, so BeginGame bootstraps it once from CardHandUI when the
        // serialized reference has not been wired yet; from this point the Initiative module
        // is independent of the UI object.
        [SerializeField] private StartingDeckCatalog startingDeckCatalog;

        // AI turn visualization (see Game.Ai.AiTurnController) — the camera pans to whatever the
        // AI is doing and armyViewerModal below opens read-only on whichever army it's acting
        // with, so an AI turn looks the same to watch as a human's would.
        [SerializeField] private RtsCameraController cameraController;
        [SerializeField] private HexSelectionController hexSelectionController;
        [SerializeField] private ArmyRadioChatterController armyRadioChatter;

        // Single shared modal for both the every-turn handoff ("Current turn: X" / "Your turn,
        // X" + Confirm — gates the human's own map input, see HexSelectionController.
        // IsInputAllowed) and blocking "can't do that" hints (e.g. tried to deploy a card on a
        // hex with no Barracks) shown by CardHandUI. While a hint is up, both map input
        // (HexSelectionController.IsInputAllowed) and card dragging (CardHandUI.CanDragCards)
        // stop, the same way a turn handoff does, until dismissed.
        [SerializeField] private PopupPanelUI popupPanel;
        [SerializeField] private GameMenuPanelUI gameMenu;

        // While the Army Viewer is open, map clicks need to stay locked out (folded into
        // InputBlocked below, same as popupPanel) — but card dragging must NOT be, since
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
        [SerializeField] private TradeModalUI tradeModal;
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
        // turn-based, these flip on discrete open/close actions a handful of times per turn
        // at most, so there was never a reason for every reader (this controller's own Update,
        // HexSelectionController, CardHandUI) to re-derive them from 4-5 live property reads
        // every single frame. The public surface is unchanged — still plain bool properties —
        // so nothing reading InputBlocked/CardDraggingBlocked needed to change.
        private bool _inputBlocked;
        private bool _cardDraggingBlocked;
        private bool _otherTurnPopupSuspended;
        private PlayerSetupData _suspendedOtherTurnPlayer;
        public bool InputBlocked => _inputBlocked;

        // Renaming an army additionally blocks card dragging (see ArmyViewerModalUI.
        // IsRenamePopupShowing) — dragging a card over the modal while the player is mid-text-
        // entry doesn't make sense, unlike the rest of the modal which deliberately leaves
        // dragging on. The battle popups block dragging outright — neither one is a place cards
        // can ever be dropped.
        public bool CardDraggingBlocked => _cardDraggingBlocked;

        // Fired whenever InputBlocked/CardDraggingBlocked's cached bool actually flips — lets
        // CardHandUI (and anything else) subscribe instead of polling either every frame.
        public event Action<bool> InputBlockedChanged;
        public event Action<bool> CardDraggingBlockedChanged;

        // Re-derives both cached bools from the underlying popups' current IsShowing/
        // IsRenamePopupShowing state — called once up front (OnEnable) and again every time one
        // of them raises VisibilityChanged, never on a timer/every frame.
        public bool IsCombatPresentationActive =>
            (battleContactPopup != null && battleContactPopup.IsShowing)
            || (battleScreen != null && battleScreen.IsShowing)
            || (aviationAttackPopup != null && aviationAttackPopup.IsShowing)
            || (aaChoicePopup != null && aaChoicePopup.IsShowing);

        private bool IsMapModalShowing => UIFocusUtility.Blocks(ModalBlock.Map) || IsCombatPresentationActive;

        // The non-interactive "other player's turn" banner leaves the map visible.
        // Every actual modal/confirmation hides cosmetic radio messages immediately.
        public bool IsArmyRadioMatchActive => !_gameOver && isActiveAndEnabled;
        public bool CanPresentArmyRadio => !_gameOver && !IsAiObserverPaused && !IsMapModalShowing && CurrentPlayer != null
            && (!CurrentPlayer.IsHuman || TurnConfirmed)
            && (popupPanel == null || !popupPanel.IsShowing || popupPanel.IsOtherTurnShowing);

        private void RecomputeBlockedState()
        {
            bool combatShowing = IsCombatPresentationActive;

            // A strategic turn notification cannot cover the human's tactical controls.
            // Suspend only ShowForOther, never a hint or a human turn confirmation. Set the
            // suspension before Hide: its visibility event re-enters this method synchronously.
            if (_otherTurnPopupSuspended && (_gameOver || CurrentPlayer != _suspendedOtherTurnPlayer))
            {
                _otherTurnPopupSuspended = false;
                _suspendedOtherTurnPlayer = null;
            }
            if (combatShowing && popupPanel != null && popupPanel.IsOtherTurnShowing)
            {
                _otherTurnPopupSuspended = true;
                _suspendedOtherTurnPlayer = CurrentPlayer;
                popupPanel.Hide();
            }
            else if (!combatShowing && _otherTurnPopupSuspended
                && popupPanel != null && !popupPanel.IsShowing)
            {
                _otherTurnPopupSuspended = false;
                _suspendedOtherTurnPlayer = null;
                if (!_gameOver && !IsAiObserverPaused
                    && (CurrentPlayer == null || !CurrentPlayer.IsHuman))
                    popupPanel.ShowForOther(CurrentPlayer, _otherTurnActivity);
            }

            bool newInputBlocked = IsMapModalShowing || (popupPanel != null && popupPanel.IsShowing);
            bool newCardDraggingBlocked = UIFocusUtility.Blocks(ModalBlock.Hand) || combatShowing
                || (popupPanel != null && popupPanel.IsOtherTurnShowing);

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
            // The Army/Base Viewer scroll their own lists with the wheel; the map under them must not zoom.
            if (cameraController != null)
                cameraController.SetZoomBlocked((armyViewerModal != null && armyViewerModal.IsShowing)
                    || (baseViewerModal != null && baseViewerModal.IsShowing));
            RefreshEndTurnInteractable();
            RefreshAiObserverPauseButton();
            if (!combatShowing)
                ShowNextAviationMessage();
        }

        public void ShowSpawnHint(string message)
        {
            popupPanel?.ShowHint(message);
        }

        public int TurnNumber { get; private set; }
        public List<PlayerSetupData> CurrentTurnOrder { get; private set; }

        // Null outside of phase 2, and during Neutral's slot — matches "no real player is
        // acting right now". HexSelectionController checks this to decide whether a unit's
        // selection animation is allowed to play (only the current player's own units).
        public PlayerSetupData CurrentPlayer { get; private set; }

        // False until the human confirms the shared popupPanel and the opening greeting /
        // turn-start AA have finished — HexSelectionController won't allow map input until this is true,
        // even once CurrentPlayer is already the human. Meaningless (left false) for AI/Neutral
        // turns, which never check it.
        public bool TurnConfirmed { get; private set; }

        internal Button TutorialEndTurnButton => endTurnButton;
        internal bool TutorialMessageShowing => popupPanel != null && popupPanel.IsShowing;

        // Fired right as control passes to the next actor (human, AI, or Neutral) — whatever
        // was selected on the map belongs to the outgoing turn and shouldn't carry over.
        public event Action TurnChanging;

        // Fired once per full turn cycle, right as TurnNumber increments — for UI (ResourceBarUI's
        // turn counter) that only needs to refresh on a new turn instead of polling every frame.
        public event Action<int> TurnStarted;

        // Fired whenever CurrentPlayer or TurnConfirmed changes — lets CardHandUI's
        // CanDragCards-dependent UI (the draw button) react instead of re-checking both every
        // frame just to notice a turn handoff.
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
        private readonly HashSet<PlayerSetupData> _matchGreetingsShown = new HashSet<PlayerSetupData>();
        // Presentation must not consume Unity's gameplay random sequence.
        private readonly System.Random _greetingRandom = new System.Random();
        private bool _turnConfirmationInProgress;

        // Set once at most one player still holds their own starting citadel — two paths lead
        // here now: OnBuildingDestroyed (a building's StructurePoints actually reaching 0 — still
        // unreachable, per the user's own "building damage deferred" call) and, newly reachable,
        // EliminatePlayer via BeginPlayerTurn's own citadel-recapture buffer check below (a
        // starting citadel CAPTURED — ownership changed by BattleScreenUI.Combat.cs's
        // HandleBuildingOnArmyDefeat — rather than destroyed; see the user's own Siege spec).
        // Blocks any further turn advancement once true.
        private bool _gameOver;
        private bool exiting;
        public StrategicTurnPhase Phase {get;private set;}

        internal CardHandUI SceneCardHand => cardHand;
        internal ArmyViewerModalUI SceneArmyViewer => armyViewerModal;

        // Scene loading completes later. Disable the outgoing scene before its static world
        // is cleared: owners cancel movement/transactions in OnDisable and Unity stops their
        // coroutines. PlayerRoot data stays alive for the final statistics snapshot.
        internal void PrepareForExit()
        {
            if(exiting)return;exiting=true;
            if (tradeModal != null) tradeModal.Hide();GameSession.BeginExit();Phase=StrategicTurnPhase.Exiting;
            _gameOver = true;
            TurnConfirmed = false;
            CurrentPlayer = null;
            _inputBlocked = true;
            _cardDraggingBlocked = true;
            StopAllCoroutines();
            cameraController?.CancelPan();
            Game.Setup.MatchStateInventory.CancelWorldOperations();
            hexSelectionController?.CancelForExit();battleScreen?.CancelForExit();cardHand?.CancelForExit();
            popupPanel?.Hide();eventChoicePopup?.Hide();eventRewardPopup?.Hide();battleContactPopup?.Hide();
            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
                root.SetActive(false);
        }

        public event System.Action<ParticipantResult> ParticipantFinished;
        internal event Action MatchResultReady;
        private MatchResultOperation matchResult;
        private long matchResultGeneration;
        internal MatchResultOperation ResultOperation => GameSession.IsCurrent(matchResultGeneration)
            && GameSession.Stage != MatchSessionStage.Prepared && matchResult?.MatchId == GameSession.MatchId ? matchResult : null;

        private bool RecordResult(MatchResultOperation operation)
        {
            if (ResultOperation != null) return false;
            matchResultGeneration = GameSession.Generation;
            matchResult = operation;
            operation.Advance();
            return true;
        }
        internal bool RetryPendingMatchResult()
        {
            var result = ResultOperation;
            if (result?.Current.Stage != MatchResultStage.Retry) return false;
            result.Advance();
            if (ResultOperation != null) MatchResultReady?.Invoke();
            return true;
        }

        public event System.Action<Game.Campaign.CompletedMatchResult> MatchFinished;
        // Non-campaign account reward rule (owner D2): one reward per match, defeat only when
        // the last living human falls. The campaign keeps its own branch below (one human).
        private readonly Game.Progression.MatchAccountOutcome _accountOutcome = new Game.Progression.MatchAccountOutcome();
        private void PublishOutcome(PlayerSetupData player, Game.Progression.MatchOutcome outcome)
        {
            if (player == null || !player.IsHuman || !GameSession.RewardsEligible || (debugWatchAiTurns && GameSession.CampaignContext == null)) return;
            var result = new ParticipantResult(GameSession.MatchId, player, outcome);
            bool recorded = GameSession.CampaignContext == null && !string.IsNullOrWhiteSpace(result.MatchId)
                && RecordResult(MatchResultOperation.Open(gameConfig, MatchResultOrigin.Game, participant: result));
            ParticipantFinished?.Invoke(result);
            if (recorded && ResultOperation != null) MatchResultReady?.Invoke();
        }
        private void PublishOutcome(Game.Progression.ParticipantResult? result)
        {
            if (result.HasValue) PublishOutcome(result.Value.Player, result.Value.Outcome);
        }

        private void OnEnable()
        {
            UIFocusUtility.BlockingChanged += RecomputeBlockedState;
            PlayerDecisionRouter.Handoff = this;
            // OnValidate already applies this on every Inspector edit, but that never fires on a
            // plain scene load/Play Mode entry with the checkbox left untouched — this covers
            // that startup case too.
            Game.Map.VisionSystem.DebugRevealAll = debugRevealFullMap;
            BuildingRegistry.BuildingDestroyed += OnBuildingDestroyed;
            if (gameMenu != null) gameMenu.VisibilityChanged += RecomputeBlockedState;
            if (popupPanel != null) popupPanel.VisibilityChanged += RecomputeBlockedState;
            if (popupPanel != null) popupPanel.Hidden += ShowNextAviationMessage;
            if (armyViewerModal != null) armyViewerModal.VisibilityChanged += RecomputeBlockedState;
            if (baseViewerModal != null) baseViewerModal.VisibilityChanged += RecomputeBlockedState;
            if (tradeModal != null) tradeModal.VisibilityChanged += RecomputeBlockedState;
            if (researchProductionModal != null) researchProductionModal.VisibilityChanged += RecomputeBlockedState;
            if (battleContactPopup != null) battleContactPopup.VisibilityChanged += RecomputeBlockedState;
            if (battleScreen != null) battleScreen.VisibilityChanged += RecomputeBlockedState;
            Game.Ai.AiActivityFeed.Reported += OnAiActivityReported;
            if (eventChoicePopup != null) eventChoicePopup.VisibilityChanged += RecomputeBlockedState;
            if (eventRewardPopup != null) eventRewardPopup.VisibilityChanged += RecomputeBlockedState;
            if (aviationAttackPopup != null) aviationAttackPopup.VisibilityChanged += RecomputeBlockedState;
            if (aaChoicePopup != null) aaChoicePopup.VisibilityChanged += RecomputeBlockedState;
            RecomputeBlockedState();
            if (gameConfig != null)
            {
                var rewards = GetComponent<CollectionRewardUI>();
                if (rewards == null) rewards = gameObject.AddComponent<CollectionRewardUI>();
                rewards.Configure(this, gameConfig);
            }
        }

        private void OnDisable()
        {
            UIFocusUtility.BlockingChanged -= RecomputeBlockedState;
            if (ReferenceEquals(PlayerDecisionRouter.Handoff, this)) PlayerDecisionRouter.Handoff = null;
            BuildingRegistry.BuildingDestroyed -= OnBuildingDestroyed;
            if (gameMenu != null) gameMenu.VisibilityChanged -= RecomputeBlockedState;
            if (popupPanel != null) popupPanel.VisibilityChanged -= RecomputeBlockedState;
            if (popupPanel != null) popupPanel.Hidden -= ShowNextAviationMessage;
            if (armyViewerModal != null) armyViewerModal.VisibilityChanged -= RecomputeBlockedState;
            if (baseViewerModal != null) baseViewerModal.VisibilityChanged -= RecomputeBlockedState;
            if (tradeModal != null) tradeModal.VisibilityChanged -= RecomputeBlockedState;
            if (researchProductionModal != null) researchProductionModal.VisibilityChanged -= RecomputeBlockedState;
            if (battleContactPopup != null) battleContactPopup.VisibilityChanged -= RecomputeBlockedState;
            if (battleScreen != null) battleScreen.VisibilityChanged -= RecomputeBlockedState;
            Game.Ai.AiActivityFeed.Reported -= OnAiActivityReported;
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
            if (!StrategicTurnEffects.Eliminate(player, hexSelectionController)) return;
            long generation = GameSession.Generation;
            // A human falling while another human still lives publishes nothing (hotseat): the
            // reward screen returns to the menu and would end the match for the survivor.
            if (GameSession.CampaignContext == null)
                PublishOutcome(_accountOutcome.OnEliminated(GameSession.MatchId, player, GameSession.Players));
            if (!GameSession.IsCurrent(generation)) return;

            ShowSpawnHint($"{player.Nickname}'s citadel has fallen - {player.Nickname} is defeated.");

            if (GameSession.Players == null)
                return;
            List<PlayerSetupData> survivors = GameSession.Players.FindAll(p => !p.IsEliminated);
            if (survivors.Count > 1)
                return;

            _gameOver = true;Phase=StrategicTurnPhase.Finished;
            foreach (var participant in GameSession.Players) PlayerRootRegistry.FindFor(participant)?.CloseTradeTurn();
            if (tradeModal != null) tradeModal.Hide();
            // Result/reward subscribers can return to another scene synchronously. Finish
            // metrics before publishing the result so exit cleanup cannot label it stopped.
            Game.Ai.AiMatchStats.RecordGameOver(survivors.Count == 1 ? survivors[0] : null);
            var finalResult = new Game.Campaign.CompletedMatchResult(GameSession.MatchId,
                survivors.Count == 1 ? survivors[0].Faction : Faction.None, player.Faction, survivors.Count == 0);
            GameSession.CompleteMatch(finalResult);
            var campaignContext = GameSession.CampaignContext;
            bool campaignResult = campaignContext != null && RecordResult(
                MatchResultOperation.Open(gameConfig, MatchResultOrigin.Game, campaignContext, finalResult));
            MatchFinished?.Invoke(finalResult);
            if (!GameSession.IsCurrent(generation)) return;
            if (campaignContext != null)
            {
                if (finalResult.Draw) PublishOutcome(GameSession.FirstHuman, MatchOutcome.Draw);
                else if (player.IsHuman) PublishOutcome(player, MatchOutcome.Defeat);
                else PublishOutcome(survivors[0], MatchOutcome.Victory);
                if (campaignResult && GameSession.IsCurrent(generation)) MatchResultReady?.Invoke();
            }
            else if (finalResult.Draw && !_accountOutcome.HasPublished) PublishOutcome(GameSession.FirstHuman, MatchOutcome.Draw);
            else PublishOutcome(_accountOutcome.OnMatchFinished(GameSession.MatchId, survivors));

            if (GameSession.IsCurrent(generation))
                ShowSpawnHint(survivors.Count == 1 ? $"{survivors[0].Nickname} wins!" : "Draw - no citadels remain.");
        }

        // Set true by CardHandUI for the duration of the human's equipment-attach mode (see
        // BeginAttachMode). That mode is deliberately non-blocking — the player keeps navigating
        // the map and clicking cards — so it does NOT feed InputBlocked; the one thing it must
        // gate is ending the turn with a half-finished attach still pending. Human-only: attach
        // mode can't start on an AI turn (CardHandUI.CanDragCards) and CardHandUI clears it on
        // TurnChanging, so this can never be stuck true when an AI player is acting.
        private bool _attachModeActive;

        public void SetAttachModeActive(bool active)
        {
            if (_attachModeActive == active)
                return;
            _attachModeActive = active;
            RefreshEndTurnInteractable();
        }

        // Kept in sync via TurnStateChanged/InputBlockedChanged instead of every frame — the
        // player must not be able to end the turn while either is up: a battle needs actually
        // deciding (fight/delay) or acknowledging before the turn can move on. Same for a pending
        // equipment attach (_attachModeActive) — it must be completed or cancelled first.
        private void RefreshEndTurnInteractable()
        {
            // No activeInHierarchy guard: the button is hidden with CardHandPanel while a battle
            // screen is open, and must come back with the right state when it reshows.
            if (endTurnButton != null)
                endTurnButton.interactable = TurnConfirmed && !InputBlocked && !_attachModeActive;
        }

        // The same physical Enter press that just dismissed popupPanel's "Your turn, X"
        // popup — Unity's own Submit action fires on Enter too, straight to whichever Selectable
        // is currently selected, entirely separate from PopupPanelUI's own Update (that one
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
            UIFocusUtility.KeepFocusWithinModal();
            if (UIFocusUtility.IsGameplayShortcutBlocked) return;
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
            Game.Audio.GameAudioManager.Instance?.PlayClick();
            OnEndTurnClicked();
        }

        // Called once, right after every player has placed their citadel.
        internal void InitializeNewMatchState()
        {
            if (startingDeckCatalog == null && gameConfig != null) startingDeckCatalog = gameConfig.collectionDeckCatalog;
            if (startingDeckCatalog == null && cardHand != null) startingDeckCatalog = cardHand.StartingDeckCatalog;
            GameSession.InitializeNewMatchHands(startingDeckCatalog, cardHand != null ? cardHand.StartingHandSize : 7,
                cardHand != null ? cardHand.MaxHandSize : 10);
        }
        private bool matchStarted;
        private long matchGeneration;
        public void BeginGame()
        {
            if (matchStarted) return;
            InitializeNewMatchState();
            matchStarted = true; matchGeneration = GameSession.Generation;
            GameSession.MarkRunning();Phase=StrategicTurnPhase.PreparingRound;
            InitializeMatchPresentation();
            MapObservationSystem.ObserveAllViewers();
            TurnNumber = 0;
            _completedTurns.Clear();
            _matchGreetingsShown.Clear();
            _turnConfirmationInProgress = false;
            _accountOutcome.Reset();
            matchResult = null;
            Game.Ai.AiMatchStats.BeginMatch(GameSession.Players);
            ConfigureStealthTurnProviders();
            // The match's weather is fixed before the first tick and any AI reasoning.
            if (!SandstormState.IsInitialized)
                SandstormState.Initialize(GameSession.ResolveWeather(), map);
            // Before round 1's income and initiative: the supply is spendable from the first purchase.
            GameSession.CampaignContext?.GrantStartingSupply(GameSession.Players);
            BeginNewTurn();
        }

        private void InitializeMatchPresentation()
        {
            armyRadioChatter?.Configure(this, cameraController != null ? cameraController.GetComponent<Camera>() : Camera.main,
                cardHand != null ? cardHand.GetComponent<RectTransform>() : null, gameConfig != null ? gameConfig.armyRadioChatter : null, hexSelectionController);
            _aiObserverMatch = GameSession.Players != null
                && GameSession.Players.Count > 0
                && !GameSession.HasHuman;
            HumanVisualMemory.ObserverMatch = _aiObserverMatch;
            _aiObserverPauseRequested = false;
            _aiObserverPauseEngaged = false;
            _aiObserverMenuHold = false;
            ConfigureAiObserverPauseButton();

            if (gameMenu != null) gameMenu.gameObject.SetActive(true);
            // One trigger for the hand, the end-turn button (both under CardHandPanel) and the
            // resource bar (its own panel, toggled by CardHandUI.Show).
            if (cardHand != null)
                cardHand.Show();
            // Existing scenes already carry the same deck catalog on CardHandUI. Copy it once
            // into the gameplay-owned field when the new direct reference has not been assigned;
            // from this point the Initiative module is independent of the UI object.
            if (startingDeckCatalog == null && cardHand != null)
                startingDeckCatalog = cardHand.StartingDeckCatalog;
            // Visible with CardHandPanel (see cardHand.Show above) — only its interactable
            // state changes from here on (see BeginPlayerTurn/OnTurnConfirmed).
            if (endTurnButton != null)
            {
                endTurnButton.interactable = false;
                Game.UI.UIUtility.ResetRuntimeListeners(endTurnButton);
                endTurnButton.onClick.AddListener(OnEndTurnClicked);
            }
        }

        private void ConfigureStealthTurnProviders()
        {
            // StealthSystem is otherwise turn-controller-agnostic (the stealth sim drives it
            // by hand) — hand it the live completed-turn count now that a real game is starting.
            Game.Map.StealthSystem.CompletedTurnsProvider = CompletedTurnsFor;
            // A detection scored during the observer's OWN turn must last through the end of
            // their NEXT turn — their completed-turn count isn't bumped until the current turn
            // ends, so MarkDetected needs to know "is this observer the one acting right now".
            Game.Map.StealthSystem.ObserverTakingTurnProvider = p => p != null && p == CurrentPlayer;
        }

        // Every player has just passed (Neutral's own fixed last slot in phase 2 — see
        // BeginPlayerTurn) — exactly when the manual's Delay Attack says a delayed battle
        // actually starts. Drained here, one at a time, before the new round's tokens-off gets
        // a chance to run — see ResolveDelayedBattlesThen. Always starts the coroutine, even
        // with an empty DelayedBattleRegistry — ResolveDelayedBattlesThen's own loop already
        // falls back to TryFindNextContestedBattle in that case, which is the ONLY place that
        // sweeps for a leftover contested hex nobody ever explicitly Delayed (e.g. a hero that
        // Escaped a Capture Kill Challenge started via direct Fight contact — the hunter army
        // just stays parked on its hex, never queued in DelayedBattleRegistry at all). Gating
        // the coroutine itself behind HasAny, like this used to, skipped that fallback sweep
        // entirely whenever no EXPLICIT delay was pending, leaving a stuck pair like that
        // coexisting forever — the enemy-present check in RefreshResourceActionRow then keeps
        // that hex's own extraction Facility action permanently unavailable, since nothing
        // ever forces the standoff to a conclusion (see the user's own report).
        private void BeginNewTurn()
        {
            if (_gameOver)
                return;

            Phase=StrategicTurnPhase.ResolvingDelayedBattles;
            // BeginPlayerTurn's own ShowForOther(null) ("Current turn: Neutral") is still up
            // from Neutral's pass and has no Confirm button to dismiss it — left showing, it
            // sits on top of (or blocks input behind) whatever ResolveDelayedBattlesThen is
            // about to show for a drained battle, hanging the game on a popup nothing can
            // click through. ProceedWithNewTurn hides it too, but that only runs once every
            // delayed battle has already drained, which is too late for this.
            if (popupPanel != null)
                popupPanel.Hide();

            StartCoroutine(ResolveDelayedBattlesThen(ProceedWithNewTurn));
        }

        private void ProceedWithNewTurn()
        {
            if (_gameOver)
                return;

            Phase=StrategicTurnPhase.PreparingRound;
            TurnNumber++;
            // One weather tick per FULL round: after the previous round's delayed battles
            // (ResolveDelayedBattlesThen ran before this), before every TurnStarted subscriber,
            // the round's preparation and the players' turns - so all of them see the new storms.
            SandstormState.AdvanceRound(map, TurnNumber);
            TurnStarted?.Invoke(TurnNumber);
            if(_gameOver||!GameSession.IsCurrent(matchGeneration))return;
            Game.Ai.AiMatchStats.OnRoundStarted(TurnNumber);
            CurrentPlayer = null; // no one's turn during the tokens-off itself
            TurnStateChanged?.Invoke();

            if (endTurnButton != null)
                endTurnButton.interactable = false;
            if (popupPanel != null)
                popupPanel.Hide();

            if (turnOrderPopup == null || GameSession.Players == null || GameSession.Players.Count == 0)
                return;

            // A defeated player is out of the game's economy and order from the next round on: no
            // income, no initiative tokens, no slot in the turn order (so a survivor's rank - and the
            // AP that rank pays - is never pushed down by someone who cannot play). Their armies
            // stay where they stand: attackable, but nothing ever moves them again (their turn
            // never runs, they never retreat - see BattleScreenUI.TryAssessSideRetreat).
            List<PlayerSetupData> activePlayers = StrategicTurnEffects.PrepareRound(map, gameConfig, GameSession.Players);

            // Initiative has one implementation now. Every AI plans from the same immutable
            // pre-purchase state, then all paid purchases are applied before the human sees
            // the popup. There is no V1/random/free fallback and no strategy-version flag.
            Game.Ai.V2.Initiative.InitiativeCoordinatorV2.PlanAndApplyForAll(
                activePlayers, map, startingDeckCatalog, TurnNumber);

            Phase=StrategicTurnPhase.AwaitingOrder;
            long generation = matchGeneration;
            bool resolved = false;
            turnOrderPopup.Show(activePlayers, order =>
            {
                if (resolved || _gameOver || Phase!=StrategicTurnPhase.AwaitingOrder || !GameSession.IsCurrent(generation)) return;
                resolved = true; OnTurnOrderResolved(order);
            });
        }

        // The physical allocation is owned by IncomeProjection; this turn controller only
        // performs the live credits in exactly the allocator's building-first/army-owner order.
        // The existing gameConfig guard, collection-before-Produce and initiative order remain.

        private void OnTurnOrderResolved(List<PlayerSetupData> order)
        {
            CurrentTurnOrder = order;

            StrategicTurnEffects.ApplyTurnOrder(order, GameSession.Players);
            BeginPlayerTurn(0);
        }

        private void BeginPlayerTurn(int index)
        {
            if (_gameOver)
                return;

            Phase=StrategicTurnPhase.StartingPlayer;
            // Expiry forgets enemies out of view; what is still visible is observed again at
            // once, so a later loss of vision during other turns cannot erase a stationary army.
            HumanVisualMemory.EndTurn(CurrentPlayer);
            MapObservationSystem.ObserveVisible(CurrentPlayer);
            TurnChanging?.Invoke();
            _otherTurnActivity = null;
            _currentPlayerIndex = index;
            TurnConfirmed = false;
            if (endTurnButton != null)
                endTurnButton.interactable = false;

            if (index >= CurrentTurnOrder.Count)
            {
                CurrentPlayer = null;Phase=StrategicTurnPhase.NeutralTurn;
                StrategicTurnEffects.BeginPlayer(null);
                if (popupPanel != null && !_aiObserverMatch)
                    popupPanel.ShowForOther(null);
                TurnStateChanged?.Invoke();
                StartCoroutine(PassAfterDelay(BeginNewTurn));
                return;
            }

            PlayerSetupData player = CurrentTurnOrder[index];
            if (!player.IsEliminated && StrategicTurnEffects.StartingCitadelLost(player))
                EliminatePlayer(player);
            if (_gameOver) return;
            if (player.IsEliminated) { BeginPlayerTurn(index + 1); return; }

            if (player.Control == PlayerControl.RemoteHuman)
                throw new NotSupportedException("A remote player's turn needs the network layer.");
            CurrentPlayer = player;
            StrategicTurnEffects.BeginPlayer(player);
            // Hotseat: the next human's fog is applied only AFTER their curtain is confirmed
            // (risk P3); until then the previous picture stays on screen.
            bool handoff = player.IsHuman && IsHotseat;
            if (!handoff && (player.IsHuman || debugWatchAiTurns || _aiObserverMatch))
                VisionSystem.CurrentViewer = player;
            TurnStateChanged?.Invoke();

            if (player.IsHuman)
            {
                Phase=StrategicTurnPhase.AwaitingConfirmation;
                cardHand?.HideAiHandDebug();
                resourceBar?.HideRootDebug();
                long generation = matchGeneration;
                Action confirm = () => { if (GameSession.IsCurrent(generation) && CurrentPlayer == player) OnTurnConfirmed(); };
                if (handoff)
                    ShowHandoffCurtain(player, TurnHandoffText(player), confirm);
                else if (popupPanel != null)
                    popupPanel.ShowForHuman(player, confirm);
            }
            else
            {
                Phase=StrategicTurnPhase.ExternalTurn;
                StealthSystem.TakeDetectionNotices(player);
                if (popupPanel != null && !_aiObserverMatch)
                    popupPanel.ShowForOther(player);
                if ((debugWatchAiTurns || _aiObserverMatch) && cardHand != null)
                    cardHand.ShowAiHandDebug(AiHandRegistry.GetOrCreate(player, cardHand.StartingDeckCatalog, cardHand.StartingHandSize));
                if (debugWatchAiTurns || _aiObserverMatch)
                    resourceBar?.ShowRootDebug(PlayerRootRegistry.FindFor(player));
                AiTurnContext ctx = AiTurnContext.From(cameraController, map, hexSelectionController,
                    cardHand, aiStepDelay, gameConfig, TurnNumber,
                    researchProductionModal != null ? researchProductionModal.Catalog : null,
                    _aiObserverMatch ? PauseAtAiActionBoundary : null);
                StartCoroutine(RunAiTurnAfterTurnStartAntiAir(player, ctx));
            }
        }

        // Turn-start AA (AviationCombatPresenter.ResolveTurnStartAntiAir) and the own-building
        // contact (ResolveOwnBuildingContacts) resolve before the AI plans, so the turn starts
        // from the post-AA, post-battle world.
        private IEnumerator RunAiTurnAfterTurnStartAntiAir(PlayerSetupData player, AiTurnContext ctx)
        {
            AviationCombatPresenter presenter = hexSelectionController != null
                ? hexSelectionController.AviationCombatPresenter : null;
            if (presenter != null)
                yield return presenter.ResolveTurnStartAntiAir(player);
            if (_gameOver)
                yield break;
            yield return ResolveOwnBuildingContacts(player);
            if (_gameOver || CurrentPlayer != player)
                yield break;
            long generation=matchGeneration;bool completed=false;
            StartCoroutine(AiTurnController.RunTurn(player,ctx,()=>
            {
                if(completed||_gameOver||CurrentPlayer!=player||!GameSession.IsCurrent(generation))return;
                completed=true;AdvanceToNextPlayer();
            }));
        }

        // First acknowledge the greeting, then resolve the existing turn-start AA prompts.
        // Map input and End Turn unlock only after both finish.
        private void OnTurnConfirmed()
        {
            PlayerSetupData player = CurrentPlayer;
            if (_gameOver || player == null || !player.IsHuman || TurnConfirmed || _turnConfirmationInProgress)
                return;
            _turnConfirmationInProgress = true;Phase=StrategicTurnPhase.OpeningTurn;
            AviationCombatPresenter presenter = hexSelectionController != null
                ? hexSelectionController.AviationCombatPresenter : null;
            if (popupPanel != null)
                popupPanel.Hide();
            StartCoroutine(ResolveTurnStartThenConfirm(presenter, player));
        }

        private IEnumerator ResolveTurnStartThenConfirm(AviationCombatPresenter presenter, PlayerSetupData player)
        {
            try
            {
                if (TryShowMatchGreeting(player))
                {
                    while (!_gameOver && CurrentPlayer == player && eventRewardPopup != null && eventRewardPopup.IsShowing)
                        yield return null;
                    if (_gameOver || CurrentPlayer != player)
                        eventRewardPopup?.Hide();
                }
                if (_gameOver || CurrentPlayer != player)
                    yield break;
                if (presenter != null && AntiAirRules.CollectTurnStartOpportunities(player).Count > 0)
                    yield return presenter.ResolveTurnStartAntiAir(player);
                if (!_gameOver && CurrentPlayer == player)
                    yield return ResolveOwnBuildingContacts(player);
                if (!_gameOver && CurrentPlayer == player)
                    CompleteTurnConfirmation();
            }
            finally { _turnConfirmationInProgress = false; }
        }

        private bool TryShowMatchGreeting(PlayerSetupData player)
        {
            if (player == null || !player.IsHuman || CompletedTurnsFor(player) != 0
                || _matchGreetingsShown.Contains(player) || eventRewardPopup == null
                || startingDeckCatalog == null || GameSession.Players == null)
                return false;

            var opponents = GameSession.Players.Where(p => p != null && p != player
                && !p.IsNeutral && !p.IsEliminated).ToList();
            while (opponents.Count > 0)
            {
                int index = _greetingRandom.Next(opponents.Count);
                var opponent = opponents[index];
                FactionCardCatalog catalog = startingDeckCatalog.GetCatalog(opponent.Faction);
                opponents.RemoveAt(index);
                if (catalog == null || catalog.logo == null || catalog.matchGreetings == null || catalog.cards == null)
                    continue;
                var messages = catalog.matchGreetings.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
                var heroes = catalog.ForType(CardType.Hero).Where(c => c.detailArt != null || c.art != null).ToList();
                if (messages.Count == 0 || heroes.Count == 0)
                    continue;
                CardDefinition hero = startingDeckCatalog.ResolveAiLeader(opponent.Faction, opponent.AiDeckName);
                if (hero == null || (hero.detailArt == null && hero.art == null))
                    hero = heroes[_greetingRandom.Next(heroes.Count)];
                eventRewardPopup.ShowMessage(hero.detailArt != null ? hero.detailArt : hero.art,
                    catalog.logo, messages[_greetingRandom.Next(messages.Count)]);
                if (!eventRewardPopup.IsShowing)
                    return false;
                _matchGreetingsShown.Add(player);
                return true;
            }
            return false;
        }

        private void CompleteTurnConfirmation()
        {
            PlayerRootRegistry.FindFor(CurrentPlayer)?.BeginTradeTurn();
            TurnConfirmed = true;Phase=StrategicTurnPhase.Ready;
            _turnConfirmedFrame = Time.frameCount;
            // OnTurnConfirmed already hid the handoff. Do not hide a report that may have
            // opened since then, and respect any still-open modal when enabling End Turn.
            if (CurrentPlayer != null)
            {
                if (_pendingAviationMessages.TryGetValue(CurrentPlayer, out List<string> messages))
                {
                    _pendingAviationMessages.Remove(CurrentPlayer);
                    foreach (string message in messages)
                        _aviationMessageQueue.Enqueue(message);
                }
                foreach (string notice in StealthSystem.TakeDetectionNotices(CurrentPlayer))
                {
                    _aviationMessageQueue.Enqueue(notice);
                }
            }
            ShowNextAviationMessage();
            RefreshEndTurnInteractable();
            TurnStateChanged?.Invoke();
        }

        private IEnumerator PassAfterDelay(Action onDone)
        {
            yield return new WaitForSeconds(aiStepDelay);
            if (_aiObserverMatch)
                yield return PauseAtAiActionBoundary();
            if(!_gameOver&&GameSession.IsCurrent(matchGeneration))onDone();
        }

        private void OnEndTurnClicked()
        {
            if (!TurnConfirmed || InputBlocked || _attachModeActive || UIFocusUtility.ClosedThisFrame
                || UIFocusUtility.Blocks(ModalBlock.EndTurn)) return;
            Game.UI.TutorialHintController.Complete(Game.UI.TutorialHintId.EndTurn);
            AdvanceToNextPlayer();
        }

        private void AdvanceToNextPlayer()
        {
            if(_gameOver||(Phase!=StrategicTurnPhase.Ready&&Phase!=StrategicTurnPhase.ExternalTurn))return;
            Phase=StrategicTurnPhase.EndingPlayer;
            var aviationMessages = StrategicTurnEffects.EndPlayer(CurrentPlayer, hexSelectionController);
            if (aviationMessages.Count > 0)
            {
                // Append: NotifyPlayer may already hold reports for this player.
                if (_pendingAviationMessages.TryGetValue(CurrentPlayer, out List<string> held)) held.AddRange(aviationMessages);
                else _pendingAviationMessages[CurrentPlayer] = aviationMessages;
            }

            if (CurrentPlayer != null)
            {
                _completedTurns[CurrentPlayer] = CompletedTurnsFor(CurrentPlayer) + 1;
                Game.Map.StealthSystem.PurgeExpiredFor(CurrentPlayer);
            }

            BeginPlayerTurn(_currentPlayerIndex + 1);
        }

        private void ConfigureAiObserverPauseButton()
        {
            if (aiObserverPauseButton == null)
                return;

            Game.UI.UIUtility.ResetRuntimeListeners(aiObserverPauseButton);
            aiObserverPauseButton.onClick.AddListener(OnAiObserverPauseClicked);
            aiObserverPauseButton.gameObject.SetActive(_aiObserverMatch);
            RefreshAiObserverPauseButton();
        }

        private void OnAiObserverPauseClicked()
        {
            if (!_aiObserverMatch || IsCombatPresentationActive || _gameOver)
                return;

            _aiObserverPauseRequested = !_aiObserverPauseRequested;
            if (!_aiObserverPauseRequested && _aiObserverPauseEngaged)
            {
                _aiObserverPauseEngaged = false;
                TurnStateChanged?.Invoke();
            }
            RefreshAiObserverPauseButton();
        }

        private IEnumerator PauseAtAiActionBoundary()
        {
            if (!_aiObserverMatch || !AiObserverHoldRequested || _gameOver)
                yield break;

            _aiObserverPauseEngaged = true;
            if (popupPanel != null && popupPanel.IsOtherTurnShowing)
                popupPanel.Hide();
            hexSelectionController?.Deselect();
            TurnStateChanged?.Invoke();
            RefreshAiObserverPauseButton();

            while (_aiObserverMatch && AiObserverHoldRequested && !_gameOver)
                yield return null;

            if (_aiObserverPauseEngaged)
            {
                _aiObserverPauseEngaged = false;
                TurnStateChanged?.Invoke();
                RefreshAiObserverPauseButton();
            }
        }

        public void SetAiObserverMenuHold(bool held)
        {
            _aiObserverMenuHold = held && _aiObserverMatch;
        }

        private void RefreshAiObserverPauseButton()
        {
            if (aiObserverPauseButton == null)
                return;

            aiObserverPauseButton.gameObject.SetActive(_aiObserverMatch);
            aiObserverPauseButton.interactable = _aiObserverMatch && !_gameOver && !IsCombatPresentationActive;
            string label = _aiObserverPauseRequested ? ">" : "||";
            TMP_Text tmp = aiObserverPauseButton.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
                tmp.text = label;
            else
            {
                Text legacy = aiObserverPauseButton.GetComponentInChildren<Text>(true);
                if (legacy != null)
                    legacy.text = label;
            }
        }

        // General phrase for what the current AI/Neutral player is doing (Game.Ai.AiActivityFeed:
        // planning, developing, exploring, fighting...) — no army, place or outcome. Shown under
        // "Current turn: X"; cleared when the next player's turn begins.
        private string _otherTurnActivity;

        private void OnAiActivityReported(Game.Ai.AiActivity activity)
        {
            if (_gameOver || CurrentPlayer == null || CurrentPlayer.IsHuman)
                return;
            string phrase = Game.Ai.AiActivityFeed.Phrase(activity);
            if (phrase == _otherTurnActivity)
                return;
            _otherTurnActivity = phrase;
            // Never over a hint or a human confirmation; a suspended banner picks it up on reshow.
            if (popupPanel != null && popupPanel.IsOtherTurnShowing)
                popupPanel.ShowForOther(CurrentPlayer, _otherTurnActivity);
        }

        private void ShowNextAviationMessage()
        {
            // Never over combat or the opening greeting. RecomputeBlockedState resumes the
            // queue once the blocking popup closes.
            if (_gameOver || popupPanel == null || popupPanel.IsShowing || _otherTurnPopupSuspended
                || (CurrentPlayer != null && CurrentPlayer.IsHuman && !TurnConfirmed)
                // Hotseat: the current human's reports wait while the screen is lent to another.
                || (CurrentPlayer != null && CurrentPlayer.IsHuman && LocalSeat.IsManaged && !LocalSeat.Is(CurrentPlayer))
                || (eventRewardPopup != null && eventRewardPopup.IsShowing)
                || IsAiObserverPaused || IsCombatPresentationActive || _aviationMessageQueue.Count == 0)
                return;
            popupPanel.ShowHint(_aviationMessageQueue.Dequeue());
        }
    }
}


