using System;
using System.Collections;
using System.Collections.Generic;
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
        [SerializeField] private float minAiPassDelay = 0.5f;
        [SerializeField] private float maxAiPassDelay = 1f;

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
        // Same "map input locked out entirely" treatment as the other modals above — see
        // HexSelectionController's own battleContactPopup/battleScreen fields (this controller
        // additionally drives their read-only ShowResolved form for delayed battles — see
        // BeginNewTurn/ResolveDelayedBattlesThen).
        [SerializeField] private BattleContactPopupUI battleContactPopup;
        [SerializeField] private BattleScreenUI battleScreen;

        public bool InputBlocked => (spawnHintPopup != null && spawnHintPopup.IsShowing)
            || (armyViewerModal != null && armyViewerModal.IsShowing)
            || (baseViewerModal != null && baseViewerModal.IsShowing)
            || (battleContactPopup != null && battleContactPopup.IsShowing)
            || (battleScreen != null && battleScreen.IsShowing);

        // Renaming an army additionally blocks card dragging (see ArmyViewerModalUI.
        // IsRenamePopupShowing) — dragging a card over the modal while the player is mid-text-
        // entry doesn't make sense, unlike the rest of the modal which deliberately leaves
        // dragging on. The battle popups block dragging outright — neither one is a place cards
        // can ever be dropped.
        public bool CardDraggingBlocked => (spawnHintPopup != null && spawnHintPopup.IsShowing)
            || (armyViewerModal != null && armyViewerModal.IsRenamePopupShowing)
            || (battleContactPopup != null && battleContactPopup.IsShowing)
            || (battleScreen != null && battleScreen.IsShowing);

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

        private int _currentPlayerIndex;

        // Set once a starting citadel is destroyed (see OnBuildingDestroyed) — blocks any
        // further turn advancement. No combat system exists yet to ever actually trigger this,
        // same "wired for correctness, currently unreachable" status as BaseViewerModalUI's own
        // Repair button.
        private bool _gameOver;

        private void OnEnable()
        {
            BuildingRegistry.BuildingDestroyed += OnBuildingDestroyed;
        }

        private void OnDisable()
        {
            BuildingRegistry.BuildingDestroyed -= OnBuildingDestroyed;
        }

        // The win condition: destroying a player's starting citadel (see
        // BuildingData.IsStartingCitadel — a later-built "Concord Citadel" card doesn't carry
        // this flag, so losing one of those doesn't end anything). Once at most one player still
        // has theirs standing, the game stops advancing.
        private void OnBuildingDestroyed(BuildingData building)
        {
            if (_gameOver || building == null || !building.IsStartingCitadel || building.Owner == null)
                return;

            ShowSpawnHint($"{building.Owner.Nickname}'s citadel has fallen — {building.Owner.Nickname} is defeated.");

            if (GameSession.Players == null)
                return;
            var survivors = new List<PlayerSetupData>();
            foreach (PlayerSetupData player in GameSession.Players)
            {
                bool hasCitadel = false;
                foreach (BuildingData candidate in BuildingRegistry.AllBuildings())
                    if (candidate.IsStartingCitadel && candidate.Owner == player) { hasCitadel = true; break; }
                if (hasCitadel)
                    survivors.Add(player);
            }
            if (survivors.Count > 1)
                return;

            _gameOver = true;
            ShowSpawnHint(survivors.Count == 1 ? $"{survivors[0].Nickname} wins!" : "Draw — no citadels remain.");
        }

        // Enter ends the human's turn whenever the button itself would currently accept a
        // click — active AND interactable, so this can't fire during an AI/Neutral pass or
        // before the button's own listener is wired up for the new current player.
        private void Update()
        {
            // Kept in sync every frame rather than only at the moments TurnConfirmed/
            // InputBlocked themselves change — InputBlocked can flip on/off mid-turn (the
            // pre-battle contact popup, the battle screen), and nothing else re-evaluates this
            // once that happens. The player must not be able to end the turn — by click or by
            // the Enter shortcut below — while either is up: a battle needs actually deciding
            // (fight/delay) or acknowledging before the turn can move on.
            if (endTurnButton != null && endTurnButton.gameObject.activeInHierarchy)
                endTurnButton.interactable = TurnConfirmed && !InputBlocked;

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
            OnEndTurnClicked();
        }

        // Called once, right after every player has placed their citadel.
        public void BeginGame()
        {
            if (resourceBar != null)
                resourceBar.Show();
            if (cardHand != null)
                cardHand.Show();
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
            BeginNewTurn();
        }

        // Every player has just passed (Neutral's own fixed last slot in phase 2 — see
        // BeginPlayerTurn) — exactly when the manual's Delay Attack says a delayed battle
        // actually starts. Drained here, one at a time, before the new round's dice-off gets a
        // chance to run — see ResolveDelayedBattlesThen.
        private void BeginNewTurn()
        {
            if (_gameOver)
                return;

            if (DelayedBattleRegistry.HasAny)
            {
                StartCoroutine(ResolveDelayedBattlesThen(ProceedWithNewTurn));
                return;
            }

            ProceedWithNewTurn();
        }

        private IEnumerator ResolveDelayedBattlesThen(Action onDone)
        {
            while (DelayedBattleRegistry.HasAny)
            {
                PendingBattle battle = DelayedBattleRegistry.TakeNext();

                // Participants were captured back when Delay was chosen — anything can have
                // happened to that same pairing since (one side fought and won/retreated through
                // an unrelated direct contact, an army was destroyed elsewhere, etc.). Re-check
                // it's still a genuine, current fight on this hex before reopening the battle
                // screen for it — otherwise a stale delay could resurface a fight the player
                // already resolved a completely different way, on the very hex they just left.
                if (!IsStillAGenuineBattle(battle))
                    continue;

                bool acknowledged = false;
                if (battleContactPopup != null)
                    battleContactPopup.ShowResolved(battle.Hex, battle.Participants, () => acknowledged = true);
                else
                    acknowledged = true;
                yield return new WaitUntil(() => acknowledged);

                bool closed = false;
                if (battleScreen != null)
                    battleScreen.Show(battle.Hex, battle.Participants, () => closed = true);
                else
                    closed = true;
                yield return new WaitUntil(() => closed);
            }
            onDone();
        }

        // Both original participants must still be sitting on the delayed hex, still combat-
        // capable, and still opposing owners — anything less means this exact confrontation
        // isn't real any more (see ResolveDelayedBattlesThen's own comment).
        private static bool IsStillAGenuineBattle(PendingBattle battle)
        {
            if (battle?.Participants == null || battle.Participants.Count < 2)
                return false;
            foreach (ArmyData army in battle.Participants)
                if (army == null || !army.Hex.Equals(battle.Hex) || !BattleInitiator.IsCombatCapable(army))
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

            if (endTurnButton != null)
                endTurnButton.interactable = false;
            if (turnInfoPopup != null)
                turnInfoPopup.Hide();

            if (turnOrderPopup == null || GameSession.Players == null || GameSession.Players.Count == 0)
                return;

            CollectResourceIncome();

            foreach (PlayerSetupData player in GameSession.Players)
                PlayerRootRegistry.FindFor(player)?.ResetBonusInitiativeDice();
            // Placeholder AI logic (see InitiativeDiceAI) — decides before the popup is shown,
            // so an AI's purchase is already reflected the first time the player sees it, not
            // bought live while they watch.
            InitiativeDiceAI.BuyDiceForAll(GameSession.Players);

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
        // on it to be fully exploited (see BuildingData.CollectedAmount).
        private void CollectResourceIncome()
        {
            if (map == null || gameConfig == null)
                return;

            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner == null || !map.TryGetTerrainAt(building.Hex, out TerrainTypeEntry entry))
                    continue;

                PlayerRoot root = PlayerRootRegistry.FindFor(building.Owner);
                if (root == null)
                    continue;

                ResourceYields hexYield = HexResourceCalculator.GetEffectiveYield(entry, HexResourceBonusRegistry.GetBonus(building.Hex));
                if (!hexYield.HasAnyYield)
                    continue;

                foreach (ResourceType type in AllResourceTypes)
                {
                    int collected = Mathf.Min(building.CollectedAmount(type), hexYield.Get(type));
                    if (collected > 0)
                        root.AddResource(type, collected);
                }
            }
        }

        private void OnTurnOrderResolved(List<PlayerSetupData> order)
        {
            CurrentTurnOrder = order;

            AllocateActionPoints(order);
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
                    root.ActionPoints = ActionPointsForRank(i, order.Count);
            }
        }

        // Two-player games are a special case (10/6) rather than the normal 10/8/6+ taper.
        private static int ActionPointsForRank(int rank, int playerCount)
        {
            if (playerCount == 2)
                return rank == 0 ? 10 : 6;
            if (rank == 0)
                return 10;
            return rank == 1 ? 8 : 6;
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
                StartCoroutine(PassAfterDelay(BeginNewTurn));
                return;
            }

            PlayerSetupData player = CurrentTurnOrder[index];
            CurrentPlayer = player;
            ReplenishMoveForOwner(player);

            if (player.IsHuman)
            {
                if (turnInfoPopup != null)
                    turnInfoPopup.ShowForHuman(player, OnTurnConfirmed);
            }
            else
            {
                if (turnInfoPopup != null)
                    turnInfoPopup.ShowForOther(player);
                StartCoroutine(PassAfterDelay(AdvanceToNextPlayer));
            }
        }

        // Fired by TurnInfoPopupUI's Confirm button — the only thing that actually lets the
        // human touch the map (and end their turn) this turn — see
        // HexSelectionController.IsInputAllowed.
        private void OnTurnConfirmed()
        {
            TurnConfirmed = true;
            if (endTurnButton != null)
                endTurnButton.interactable = true;
            if (turnInfoPopup != null)
                turnInfoPopup.Hide();
        }

        // Restores move points for every unit belonging to whoever's turn is starting —
        // player is null for Neutral's slot, matching ArmyData.Owner for any Neutral armies.
        // Units have no registry of their own any more (see Game.Map.ArmyController) — reached
        // only through the armies that contain them.
        private static void ReplenishMoveForOwner(PlayerSetupData player)
        {
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                foreach (UnitData unit in army.Members)
                    unit.ReplenishMoveForNewTurn();
                // Activation is tracked per-ARMY, not per-unit (see ArmyData.
                // HasActivatedThisTurn) — a unit never moves on its own.
                army.HasActivatedThisTurn = false;
            }
        }

        // Stands in for "the AI/Neutral thought about it and had nothing to do" — a short
        // random pause before passing, instead of an instant skip, until real decisions exist.
        private IEnumerator PassAfterDelay(Action onDone)
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(minAiPassDelay, maxAiPassDelay));
            onDone();
        }

        private void OnEndTurnClicked()
        {
            AdvanceToNextPlayer();
        }

        private void AdvanceToNextPlayer()
        {
            BeginPlayerTurn(_currentPlayerIndex + 1);
        }
    }
}
