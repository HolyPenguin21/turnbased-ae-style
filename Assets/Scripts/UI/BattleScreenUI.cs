using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cameras;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Styles;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // The Tactical Battle Module screen — three blocks, per the user's own spec: the battlefield
    // (a BattleGrid, rendered as a 5x5 grid of BattleGridCellUI), the turn order (Round counter,
    // Pass button, a strip of BattleTurnOrderIconUI in initiative order — see BattleTurnOrder),
    // and the detail panel for whichever unit is currently up (or whichever was last clicked
    // directly in the grid — see ShowUnitDetail).
    //
    // Three phases per battle, per the user's own spec:
    //   Arrange  — the local human participant (if any) lays out their own side's units on the
    //              grid (drag-and-drop, see BattleGridCellUI); the opponent's cells are hidden
    //              and the initiative strip stays empty. Confirmed with Ready.
    //   RoundStart — a popup preview of both sides' rosters and effective initiative for the
    //              round about to happen, shown before EVERY round (title increments), not just
    //              the first. Retreat there gives the OTHER side one final "grace round" (see
    //              OnRetreatClicked/_retreatingArmy) before ResolveRetreat actually relocates or
    //              destroys the retreating army.
    //   Round    — full grid + initiative queue revealed. The current human unit can click an
    //              adjacent empty own-side/neutral-row cell to move, or an enemy in range to open
    //              the Ground Combat popup (see OnCellClicked/BattleAttackPopupUI) — either ends
    //              its turn (EndTurn), same as Pass. An AI-owned unit's turn still just
    //              auto-passes (no AI decision-making yet).
    //
    // No close/quit here — the user decided that belongs to a future main-menu feature, not this
    // screen (see Hide's own comment).
    //
    // Split across 4 files by concern, purely for size — all share this same field block and the
    // state-machine methods below (EndTurn, Show/Hide, BeginRound, ShowAiThought), which live
    // here since every part uses them:
    //   - BattleScreenUI.cs (this file): fields, lifecycle, round/turn state machine.
    //   - BattleScreenUI.Combat.cs: attack resolution, battle-end/chain-battle handling.
    //   - BattleScreenUI.Retreat.cs: retreat-destination resolution.
    //   - BattleScreenUI.Grid.cs: grid rendering, cell click/drag handling.
    public partial class BattleScreenUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private GameConfig gameConfig;
        // Only one faction is fully set up in this project right now (see BattleContactPopupUI's
        // own identical `catalog` field) — every turn-order icon's logo comes from here
        // regardless of whose unit it is, same as everywhere else that needs one.
        [SerializeField] private FactionCardCatalog catalog;
        // Cleared the instant a battle opens (see Show) — the hex/army info panels underneath
        // mean nothing once combat starts, and would otherwise linger stale behind this screen.
        [SerializeField] private HexSelectionController hexSelectionController;
        // Needed only for Retreat's own destination search (ResolveRetreat/TryFindRetreatDestination)
        // — validating a candidate hex is actually on the map.
        [SerializeField] private HexMap map;
        // Hidden for the duration of a battle (see Show/Hide) — playing a card onto the map
        // makes no sense behind the battle screen, and the hand would otherwise visually
        // crowd/overlap it.
        [SerializeField] private CardHandUI cardHand;
        // Panning switched off for the duration of a battle (see Show/Hide) — the strategic
        // map's view has no reason to move around underneath the battle grid.
        [SerializeField] private RtsCameraController rtsCamera;

        [Header("Arrangement / Round-start")]
        [SerializeField] private BattleArrangePopupUI arrangePopup;
        [SerializeField] private BattleRoundStartPopupUI roundStartPopup;
        [SerializeField] private Button readyButton;

        [Header("Move / Attack")]
        [SerializeField] private BattleAttackPopupUI attackPopup;
        [SerializeField] private BattleOutcomePopupUI outcomePopup;
        // Re-shown (Fight/Delay) for a second enemy army still sharing the hex once the current
        // fight resolves — see OnBattleOutcomeAcknowledged. Same instance HexSelectionController
        // shows for the very first contact on the strategic map; this class just re-uses it for a
        // chained continuation instead of instantiating its own.
        [SerializeField] private BattleContactPopupUI battleContactPopup;

        [Header("AI")]
        [SerializeField] private BattleAiThoughtsUI aiThoughts;
        // Reset on any player action during their own turn (Pass/move/attack) — past this many
        // idle seconds, the AI can offer a small "waiting" nudge (see Update).
        [SerializeField] private float aiIdleThreshold = 18f;

        [Header("Turn Order")]
        [SerializeField] private TMP_Text roundText;
        [SerializeField] private Button passButton;
        [SerializeField] private Transform turnQueueContainer;
        [SerializeField] private BattleTurnOrderIconUI turnQueueIconPrefab;

        [Header("Detail")]
        [SerializeField] private Image detailArt;
        [SerializeField] private TMP_Text detailText;

        [Header("Battlefield")]
        [SerializeField] private Transform gridContainer;
        [SerializeField] private BattleGridCellUI gridCellPrefab;

        // Beat before an AI-owned unit's turn actually acts (see AutoActAfterDelay) — purely a
        // pacing/readability delay, same idea as GameTurnController.PassAfterDelay for the
        // strategic-map turn loop.
        [SerializeField] private float aiAutoPassDelay = 0.6f;
        // Fast but smooth, per the user's own spec — same order of magnitude as
        // ArmyUnitCardUI.slotAnimDuration's own "quick but eased" convention.
        [SerializeField] private float moveAnimDuration = 0.15f;

        private Action _onClosed;
        private BattleGrid _grid;
        private ArmyData _attacker;
        private ArmyData _defender;
        // Whichever participant belongs to the local human player, if any (see Show) — the only
        // side that ever gets a visible Arrangement phase; the other side keeps whatever
        // BattleGrid.FromArmies placed it at (its own saved arrangement or the plain default).
        private ArmyData _localArmy;
        private int _localFrontRow;
        private int _localBackRow;
        // Set by OnRetreatClicked, cleared once ResolveRetreat runs — while set, the retreating
        // side's units are excluded from the acting turn order (see OnStartRoundClicked) for one
        // final "grace round" that lets the OTHER side get a last hit in before the army actually
        // leaves, per the user's own spec.
        private ArmyData _retreatingArmy;

        // A unit's actual owning army, independent of which grid ROWS it currently occupies — a
        // non-hero unit can advance across the neutral row into the opposing side's own rows
        // during the Round's movement step to reach melee range (see BattleGrid's row-layout
        // comment), so "which row group a unit sits in" stops matching "which army it belongs
        // to" the moment that happens. Grid-row lookups (BattleGrid.IsAttackerSideRow,
        // BattleTurnOrder.FindHero) are still correct for HEROES, which never leave their own
        // side's rows, and for genuinely row-relative concerns like initiative bonus/movement
        // legality — but any code deciding whose hero/Fate/ownership applies to a specific unit
        // must use this instead. Root cause of the Spend-button bug in project_battle_ai_bugs_open
        // memory: BeginAttack used to derive attackerHero/defenderHero from the attacking unit's
        // grid row, which flipped to the wrong side once that unit had moved into enemy rows.
        private ArmyData OwningArmy(UnitData unit)
        {
            if (unit == null)
                return null;
            if (_attacker != null && _attacker.Members.Contains(unit))
                return _attacker;
            if (_defender != null && _defender.Members.Contains(unit))
                return _defender;
            return null;
        }

        private static UnitData OwningHero(ArmyData army) => army?.Members.FirstOrDefault(m => m.IsHero);

        private bool _arranging;
        // True only once the "Arrange your units" intro popup has been dismissed — the grid
        // itself is already visible the instant Arrangement starts (see Show), but dragging
        // stays gated behind Ok so the player can't start rearranging while that popup is still
        // covering part of the screen.
        private bool _arrangeInteractive;
        private List<UnitData> _turnOrder;
        private int _turnIndex;
        private int _round;
        // Whoever's currently up in the turn order (see RefreshTurnOrder) — null during
        // Arrangement/Round-start, before any round has actually begun.
        private UnitData _currentActingUnit;
        private Coroutine _aiAutoPassRoutine;
        private bool _isAnimatingMove;
        private Canvas _canvas;
        // Anti-stalling counter for BattleAi.ChooseAction — how many turns in a row a given AI
        // unit has waited instead of advancing (see BattleAi's own MaxWaitStreak).
        private readonly Dictionary<UnitData, int> _aiWaitStreak = new Dictionary<UnitData, int>();
        // Seconds since the human's last action on their own turn — past aiIdleThreshold, the AI
        // offers a small idle nudge (see Update). Negative while it isn't the human's turn at all.
        private float _idleTimer = -1f;
        private bool _idleNudgeShown;
        private readonly List<BattleGridCellUI> _cells = new List<BattleGridCellUI>();
        private readonly List<BattleTurnOrderIconUI> _queueIcons = new List<BattleTurnOrderIconUI>();

        // Also true while a standalone Capture Kill Challenge is up (see
        // BeginCaptureKillEncounter) — that one deliberately never activates panelRoot at all
        // (no grid/Arrangement chrome for a hero-only encounter, per the user's own spec), but
        // GameTurnController.InputBlocked still needs to know something modal is showing.
        public bool IsShowing => (panelRoot != null && panelRoot.activeSelf) || (attackPopup != null && attackPopup.IsShowing);

        // Lets GameTurnController react to a battle opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked/CardDraggingBlocked).
        public event Action VisibilityChanged;
        // Read by BattleGridCellUI for the acting-unit ring (UIRaggedGlowUI) — settings live in
        // GameConfig rather than baked into the prefab, per the user's own spec.
        public HexHighlightStyle ActingHighlightStyle => gameConfig != null ? gameConfig.battleActingUnitHighlightStyle : null;

        private void Awake()
        {
            if (passButton != null)
                passButton.onClick.AddListener(OnPassClicked);
            if (readyButton != null)
                readyButton.onClick.AddListener(OnReadyClicked);
            _canvas = GetComponentInParent<Canvas>();
        }

        private void Update()
        {
            if (_idleTimer < 0f || _idleNudgeShown)
                return;
            _idleTimer += Time.deltaTime;
            if (_idleTimer >= aiIdleThreshold)
            {
                _idleNudgeShown = true;
                aiThoughts?.ShowIdle(BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.PlayerIdle));
            }
        }

        // Called from anywhere the human actually acts on their own turn (Pass/move/attack) —
        // resets the idle nudge so it can fire again on the NEXT stretch of inactivity.
        private void ResetIdleTimer()
        {
            _idleTimer = IsShowing && _currentActingUnit != null
                && _currentActingUnit.Owner != null && _currentActingUnit.Owner.IsHuman ? 0f : -1f;
            _idleNudgeShown = false;
        }

        public void Show(HexCoord hex, List<ArmyData> participants, Action onClosed)
        {
            _onClosed = onClosed;
            if (panelRoot != null)
                panelRoot.SetActive(true);
            VisibilityChanged?.Invoke();

            // The map underneath is about to be covered by this whole screen — whatever hex/
            // army was selected there is stale the moment combat starts (see
            // HexSelectionController.Deselect's own comment).
            hexSelectionController?.Deselect();
            cardHand?.Hide();
            rtsCamera?.SetPanningEnabled(false);

            _attacker = participants != null && participants.Count > 0 ? participants[0] : null;
            _defender = participants != null && participants.Count > 1 ? participants[1] : null;
            _grid = BattleGrid.FromArmies(_attacker, _defender);
            _round = 1;
            // A hex with a SECOND still-standing enemy army chains straight into a fresh Show()
            // for that fight (see OnBattleOutcomeAcknowledged) without ever going through Hide()
            // in between — Hide() is the only other place this gets cleared, so without this a
            // retreat already resolved (or still pending) against the FIRST army leaked into the
            // second battle: it suppressed ConsiderAiRetreat's own fresh decision every round
            // (its guard is just "_retreatingArmy != null") and, worse, made EndTurn fire
            // ResolveRetreat on that stale/already-gone army the moment the new battle's first
            // round ended — a bogus "the enemy retreats" for an army that wasn't even part of
            // this fight, while the actual current opponent's own retreat never got a chance to
            // engage the mechanic at all (see the user's own report: retreat announced again and
            // again, never actually taking effect).
            _retreatingArmy = null;
            _aiWaitStreak.Clear();

            // AI never gets a UI Arrangement phase — replace whatever FromArmies's generic
            // default just placed for any non-human side with BattleAi's own range-aware layout
            // (covers ordinary human-vs-AI and the AI-vs-AI edge case symmetrically). The
            // opposing army is passed only for its current STATS (see ArrangeArmy's own
            // comment), never its placement — both sides' arrangement happens in this same call,
            // before either one has a layout to look at.
            if (_attacker?.Owner != null && !_attacker.Owner.IsHuman)
                BattleAi.ArrangeArmy(_grid, _attacker, BattleGrid.AttackerFrontRow, BattleGrid.AttackerBackRow, _defender);
            if (_defender?.Owner != null && !_defender.Owner.IsHuman)
                BattleAi.ArrangeArmy(_grid, _defender, BattleGrid.DefenderFrontRow, BattleGrid.DefenderBackRow, _attacker);

            _localArmy = null;
            if (_attacker?.Owner != null && _attacker.Owner.IsHuman)
            {
                _localArmy = _attacker;
                _localFrontRow = BattleGrid.AttackerFrontRow;
                _localBackRow = BattleGrid.AttackerBackRow;
            }
            else if (_defender?.Owner != null && _defender.Owner.IsHuman)
            {
                _localArmy = _defender;
                _localFrontRow = BattleGrid.DefenderFrontRow;
                _localBackRow = BattleGrid.DefenderBackRow;
            }

            if (_localArmy != null && arrangePopup != null)
            {
                // The grid itself (the local player's own cards + the opponent's still-empty
                // cells) is visible right away, behind the intro popup — only actually
                // rearranging things waits for Ok (see BeginArrangement).
                _arranging = true;
                _arrangeInteractive = false;
                if (readyButton != null)
                    readyButton.gameObject.SetActive(true);
                if (passButton != null)
                    passButton.gameObject.SetActive(false);
                if (roundText != null)
                    roundText.text = string.Empty;
                UIListUtility.DestroyAndClear(_queueIcons);
                ShowUnitDetail(null);
                RefreshGrid();

                arrangePopup.Show(BeginArrangement);
            }
            else
            {
                // No visible Arrangement phase for this battle (neither side is the local human
                // participant) — make sure Ready/Pass are back in the round-loop's own default
                // state regardless of what a previous battle left them as.
                if (readyButton != null)
                    readyButton.gameObject.SetActive(false);
                if (passButton != null)
                    passButton.gameObject.SetActive(true);
                BeginRound();
            }
        }

        // Only reached once the "Arrange your units" popup's Ok has been clicked — the grid
        // was already showing (see Show), this just unlocks actually dragging things on it.
        private void BeginArrangement()
        {
            _arrangeInteractive = true;
            RefreshGrid();
        }

        // Captures the local player's final layout for next time (see ArmyData.
        // SavedArrangement), then leaves Arrangement and starts the round loop proper.
        private void OnReadyClicked()
        {
            if (!_arranging || _localArmy == null)
                return;

            _localArmy.SavedArrangement.Clear();
            foreach (UnitData member in _localArmy.Members)
                if (_grid.TryFindPosition(member, out int row, out int col))
                    _localArmy.SavedArrangement[member] = (row, col);

            _arranging = false;
            if (readyButton != null)
                readyButton.gameObject.SetActive(false);
            if (passButton != null)
                passButton.gameObject.SetActive(true);
            BeginRound();
        }

        // A fresh turn order every round — nothing yet changes a unit's Initiative mid-battle,
        // so today this just recomputes the exact same order each time, but the recompute stays
        // here (rather than caching once) since it'll matter the moment anything CAN change it
        // (a unit dying, a buff/debuff, etc.). Shown behind the Round-start preview popup every
        // round (not just the first) — see the class comment.
        private void BeginRound()
        {
            ConsiderAiRetreat();

            // Garrisons can never retreat (per the manual); a battle with no local human side
            // has nobody to click the button anyway — canRetreat covers both. Also off once the
            // AI side has already committed to its own retreat this round — only one side
            // retreats per round in this design.
            bool canRetreat = _localArmy != null && !_localArmy.IsGarrison && _retreatingArmy == null;
            if (roundStartPopup != null)
                roundStartPopup.Show(_round, _grid, _attacker, _defender, catalog != null ? catalog.logo : null,
                    canRetreat, OnStartRoundClicked, OnRetreatClicked, _retreatingArmy?.Name);
            else
                OnStartRoundClicked();
        }

        // The AI's own strategic fight/retreat call, made once per round exactly like the
        // human's own Retreat button (see BattleAi.AssessRetreat's own projection model). Only
        // runs for the single-clear-AI-side case (ordinary human-vs-AI); skipped entirely once
        // someone's already retreating this round, in round 1 (same restriction the human has),
        // or for a garrison (can never retreat).
        private void ConsiderAiRetreat()
        {
            if (_retreatingArmy != null || _round <= 1)
                return;
            ArmyData aiArmy = _localArmy == _attacker ? _defender : (_localArmy == _defender ? _attacker : null);
            if (aiArmy == null || aiArmy.Owner == null || aiArmy.Owner.IsHuman || aiArmy.IsGarrison)
                return;
            ArmyData enemyArmy = aiArmy == _attacker ? _defender : _attacker;

            BuildingData building = BuildingRegistry.FindAt(aiArmy.Hex);
            bool defendingOwnCitadel = building != null && building.Owner == aiArmy.Owner && BuildingAbilities.IsFullCitadel(building);

            BattleAi.RetreatAssessment assessment = BattleAi.AssessRetreat(aiArmy, enemyArmy, defendingOwnCitadel);
            UnitData sideHero = BattleTurnOrder.FindHero(_grid, aiArmy == _attacker);

            if (assessment.IsCitadelDefense)
            {
                aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.CitadelDefense, hasHero: sideHero != null));
            }
            else if (assessment.ShouldRetreat)
            {
                _retreatingArmy = aiArmy;
                aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.RetreatDecision, hasHero: sideHero != null));
            }
            else
            {
                aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.FightDecision, hasHero: sideHero != null));
            }
        }

        private void OnStartRoundClicked()
        {
            _turnOrder = BattleTurnOrder.BuildOrder(_grid);
            // The retreating side gets no more actions this round (see _retreatingArmy's own
            // comment) — everyone else still acts normally, giving them one last chance to hit
            // the fleeing army before ResolveRetreat actually moves/destroys it at round's end.
            if (_retreatingArmy != null)
                _turnOrder = _turnOrder.Where(u => u.Owner != _retreatingArmy.Owner).ToList();
            _turnIndex = 0;
            RefreshTurnOrder();
        }

        // Starts the "grace round" — closes the Round-start popup itself (see
        // BattleRoundStartPopupUI.OnRetreatClicked) and proceeds straight into a round exactly
        // like Начать раунд would, just with _retreatingArmy set so OnStartRoundClicked excludes
        // the retreating side from the turn order.
        private void OnRetreatClicked()
        {
            if (_localArmy == null || _localArmy.IsGarrison)
                return;
            _retreatingArmy = _localArmy;
            OnStartRoundClicked();
        }

        private void OnPassClicked()
        {
            EndTurn();
        }

        // Advances past the current unit's turn — shared by Pass, a successful Move, and a
        // resolved Attack (see OnCellClicked/OnAttackResolved), so all three funnel through the
        // same round-rollover logic instead of Pass being the only thing that ever called it.
        private void EndTurn()
        {
            if (_turnOrder == null || _turnOrder.Count == 0)
                return;
            _turnIndex++;
            if (_turnIndex >= _turnOrder.Count)
            {
                // The grace round just finished — resolve the retreat now instead of starting
                // a further round (see _retreatingArmy's own comment).
                if (_retreatingArmy != null)
                {
                    ResolveRetreat();
                    return;
                }
                _round++;
                BeginRound();
            }
            else
            {
                RefreshTurnOrder();
            }
        }

        private void RefreshTurnOrder()
        {
            if (roundText != null)
                roundText.text = $"Round {_round}";

            UIListUtility.DestroyAndClear(_queueIcons);
            if (turnQueueContainer != null && turnQueueIconPrefab != null && _turnOrder != null)
                for (int i = 0; i < _turnOrder.Count; i++)
                {
                    UnitData queued = _turnOrder[i];
                    Color ownerColor = queued.Owner != null ? PlayerColorPalette.Colors[queued.Owner.ColorIndex] : Color.white;
                    BattleTurnOrderIconUI icon = Instantiate(turnQueueIconPrefab, turnQueueContainer);
                    icon.Setup(queued, catalog != null ? catalog.logo : null, ownerColor, i == _turnIndex);
                    _queueIcons.Add(icon);
                }

            UnitData current = _turnOrder != null && _turnIndex < _turnOrder.Count ? _turnOrder[_turnIndex] : null;
            ShowUnitDetail(current);
            // Drives BattleGridCellUI's own yellow acting-unit ring — rebuilding the whole grid
            // on every Pass (not just every round) is the same "destroy and rebuild" pattern
            // already used for the queue icons above, just for the battlefield instead.
            _currentActingUnit = current;
            RefreshGrid();

            // Pass only ever acts for the CURRENT unit — an AI-owned unit's turn isn't the
            // player's to skip by hand. There's no real AI decision-making here yet, so instead
            // its turn just auto-passes itself after a beat (see AutoPassAfterDelay) rather than
            // stalling the round forever.
            bool isHumanTurn = current != null && current.Owner != null && current.Owner.IsHuman;
            if (passButton != null)
                passButton.interactable = isHumanTurn;
            ResetIdleTimer();

            if (_aiAutoPassRoutine != null)
            {
                StopCoroutine(_aiAutoPassRoutine);
                _aiAutoPassRoutine = null;
            }
            if (current != null && !isHumanTurn)
                _aiAutoPassRoutine = StartCoroutine(AutoActAfterDelay(current));
        }

        // Replaces the old auto-pass stub — after the same pacing beat, asks BattleAi what this
        // unit should do and dispatches into the exact same PerformMove/BeginAttack/OnPassClicked
        // methods the human's own clicks already use (see BattleAi.ChooseAction's own comment).
        private IEnumerator AutoActAfterDelay(UnitData actor)
        {
            yield return new WaitForSeconds(aiAutoPassDelay);
            _aiAutoPassRoutine = null;
            if (_grid == null || actor == null || actor != _currentActingUnit)
                yield break;

            BattleAi.AiAction action = BattleAi.ChooseAction(_grid, actor, _aiWaitStreak);
            ShowAiThought(actor, action.Reason, action.Target?.Name);

            switch (action.Kind)
            {
                case BattleAi.AiActionKind.Attack:
                    BeginAttack(actor, action.Target);
                    break;
                case BattleAi.AiActionKind.Move:
                    if (_grid.TryFindPosition(actor, out int fromRow, out int fromCol))
                        PerformMove(fromRow, fromCol, action.Row, action.Col);
                    else
                        OnPassClicked();
                    break;
                default:
                    OnPassClicked();
                    break;
            }
        }

        // Looks up whichever side `actor` belongs to and fires that side's own hero as the
        // narrating voice (see BattleAiThoughtsUI.Show) — never the acting unit itself.
        // targetName (optional) drops a specific OTHER unit's name into the phrase, if the
        // category has a named variant and the caller has one to give (see
        // BattleAiPhraseBank.GetRandomPhrase) — never the acting unit's own name, that's already
        // implied by whose "voice" is speaking.
        private void ShowAiThought(UnitData actor, AiThoughtCategory category, string targetName = null)
        {
            if (aiThoughts == null)
                return;
            // Owning army, not grid row — see OwningArmy's own comment. `actor` may have already
            // moved into the opposing side's rows (e.g. mid-melee), so its row is not a reliable
            // stand-in for which army's hero should be "speaking" here.
            UnitData sideHero = OwningHero(OwningArmy(actor));
            aiThoughts.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(category, targetName, sideHero != null));
        }

        // Called by OnBattleOutcomeAcknowledged once the outcome popup is dismissed — there's
        // still no separate Quit/Close (see the class comment), this is only ever reached via a
        // battle actually finishing.
        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
            VisibilityChanged?.Invoke();
            if (_aiAutoPassRoutine != null)
            {
                StopCoroutine(_aiAutoPassRoutine);
                _aiAutoPassRoutine = null;
            }
            arrangePopup?.Hide();
            roundStartPopup?.Hide();
            attackPopup?.Hide();
            outcomePopup?.Hide();
            UIListUtility.DestroyAndClear(_cells);
            UIListUtility.DestroyAndClear(_queueIcons);
            _grid = null;
            _turnOrder = null;
            _currentActingUnit = null;
            _localArmy = null;
            _retreatingArmy = null;
            _arranging = false;
            _arrangeInteractive = false;
            _aiWaitStreak.Clear();
            _idleTimer = -1f;
            _idleNudgeShown = false;
            aiThoughts?.Clear();
            cardHand?.Show();
            rtsCamera?.SetPanningEnabled(true);

            Action callback = _onClosed;
            _onClosed = null;
            callback?.Invoke();
        }
    }
}
