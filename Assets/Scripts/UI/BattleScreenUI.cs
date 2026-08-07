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
    public class BattleScreenUI : MonoBehaviour
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

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
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
                    canRetreat, OnStartRoundClicked, OnRetreatClicked);
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
            if (aiThoughts == null || _grid == null || !_grid.TryFindPosition(actor, out int row, out _))
                return;
            UnitData sideHero = BattleTurnOrder.FindHero(_grid, BattleGrid.IsAttackerSideRow(row));
            aiThoughts.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(category, targetName, sideHero != null));
        }

        private bool IsLocalRow(int row) => _localArmy != null && (row == _localFrontRow || row == _localBackRow);

        // Orthogonally adjacent to the actor's own current cell AND within the actor's own two
        // rows or the shared neutral row — never directly into the enemy's own two rows (same
        // same-side restriction the Arrangement phase's own drag-and-drop already enforces, just
        // for a single active unit moving one step instead of free rearranging). Row/adjacency
        // helpers live on BattleGrid itself now (shared with BattleAi's own movement logic).
        private bool IsAdjacentOwnSide(UnitData actor, int row, int col)
        {
            if (actor == null || _grid == null || !_grid.TryFindPosition(actor, out int actorRow, out int actorCol))
                return false;
            if (!BattleGrid.IsOrthogonallyAdjacent(actorRow, actorCol, row, col))
                return false;
            return BattleGrid.IsAttackerSideRow(actorRow) ? (BattleGrid.IsAttackerSideRow(row) || row == BattleGrid.NeutralRow)
                                                            : (BattleGrid.IsDefenderSideRow(row) || row == BattleGrid.NeutralRow);
        }

        private void RefreshGrid()
        {
            UIListUtility.DestroyAndClear(_cells);
            if (gridContainer == null || gridCellPrefab == null || _grid == null)
                return;

            // Legal-target hints only make sense once a real round is underway (not Arranging)
            // and only for the local human's own current unit — an AI turn has no player input to
            // hint at.
            bool canAct = !_arranging && _currentActingUnit != null
                && _currentActingUnit.Owner != null && _currentActingUnit.Owner.IsHuman;
            int actorRow = -1, actorCol = -1;
            if (canAct)
                _grid.TryFindPosition(_currentActingUnit, out actorRow, out actorCol);

            for (int row = 0; row < BattleGrid.Rows; row++)
                for (int col = 0; col < BattleGrid.Columns; col++)
                {
                    // During Arrangement, only the local player's own two rows are shown as
                    // they really are on the grid — the opponent's side renders as empty cells
                    // regardless of what's actually placed there (see the user's own spec: the
                    // player doesn't see the enemy's cards before committing to a layout).
                    bool hideForArrangement = _arranging && !IsLocalRow(row);
                    UnitData unit = hideForArrangement ? null : _grid.Get(row, col);
                    bool draggable = _arranging && _arrangeInteractive && IsLocalRow(row) && unit != null;
                    bool isActingUnit = unit != null && unit == _currentActingUnit;

                    bool isLegalMoveTarget = canAct && unit == null && IsAdjacentOwnSide(_currentActingUnit, row, col);
                    bool isLegalAttackTarget = canAct && unit != null && !unit.IsHero && unit.Owner != _currentActingUnit.Owner
                        && BattleGrid.IsInRange(actorRow, actorCol, row, col, _currentActingUnit.Range);

                    BattleGridCellUI cell = Instantiate(gridCellPrefab, gridContainer);
                    cell.Setup(this, unit, row, col, draggable, isActingUnit, isLegalMoveTarget, isLegalAttackTarget);
                    _cells.Add(cell);
                }
        }

        // Click-to-act for the local human's current unit — no-op for anything else (Arranging
        // uses its own drag-and-drop, an AI turn has no player input, and a click on a cell that
        // isn't a legal move/attack target for the current unit is just an inspect, already
        // handled by BattleGridCellUI.OnPointerClick calling ShowUnitDetail unconditionally).
        public void OnCellClicked(BattleGridCellUI cell)
        {
            if (_arranging || _isAnimatingMove || cell == null || _currentActingUnit == null)
                return;
            if (_currentActingUnit.Owner == null || !_currentActingUnit.Owner.IsHuman)
                return;
            if (!_grid.TryFindPosition(_currentActingUnit, out int actorRow, out int actorCol))
                return;

            if (cell.Unit == null)
            {
                if (IsAdjacentOwnSide(_currentActingUnit, cell.Row, cell.Col))
                    PerformMove(actorRow, actorCol, cell.Row, cell.Col);
                return;
            }

            if (cell.Unit.IsHero || cell.Unit.Owner == _currentActingUnit.Owner)
                return;
            if (BattleGrid.IsInRange(actorRow, actorCol, cell.Row, cell.Col, _currentActingUnit.Range))
                BeginAttack(_currentActingUnit, cell.Unit);
        }

        private BattleGridCellUI FindCell(int row, int col)
        {
            foreach (BattleGridCellUI cell in _cells)
                if (cell.Row == row && cell.Col == col)
                    return cell;
            return null;
        }

        private void PerformMove(int fromRow, int fromCol, int toRow, int toCol)
        {
            if (_isAnimatingMove)
                return;
            StartCoroutine(AnimateThenMove(fromRow, fromCol, toRow, toCol));
        }

        // Fast but smooth, per the user's own spec — the moving cell's own card slides from the
        // source cell to the destination (see BattleGridCellUI.AnimateMoveTo), then the actual
        // grid swap/rebuild/turn advance happens all at once, same as before. Input is blocked
        // for the animation's short duration (see _isAnimatingMove) so a second click can't
        // queue up mid-slide. gridContainer's GridLayoutGroup is disabled for that same span —
        // otherwise it fights the manual position Lerp and, worse, reflows every other cell the
        // instant sibling order/state changes underneath it (that's what made neighbouring units
        // visibly jump) — it's re-enabled just before RefreshGrid rebuilds everything anyway.
        private IEnumerator AnimateThenMove(int fromRow, int fromCol, int toRow, int toCol)
        {
            _isAnimatingMove = true;
            BattleGridCellUI fromCell = FindCell(fromRow, fromCol);
            BattleGridCellUI toCell = FindCell(toRow, toCol);
            GridLayoutGroup layoutGroup = gridContainer != null ? gridContainer.GetComponent<GridLayoutGroup>() : null;
            if (fromCell != null && toCell != null)
            {
                if (layoutGroup != null)
                    layoutGroup.enabled = false;
                yield return StartCoroutine(fromCell.AnimateMoveTo(toCell.RectTransform, moveAnimDuration));
                if (layoutGroup != null)
                    layoutGroup.enabled = true;
            }

            _grid.Swap(fromRow, fromCol, toRow, toCol);
            _isAnimatingMove = false;
            RefreshGrid();
            EndTurn();
        }

        private void BeginAttack(UnitData attacker, UnitData defender)
        {
            if (attackPopup == null)
                return;
            bool attackerIsAttackerSide = _grid.TryFindPosition(attacker, out int aRow, out _) && BattleGrid.IsAttackerSideRow(aRow);
            UnitData attackerHero = BattleTurnOrder.FindHero(_grid, attackerIsAttackerSide);
            UnitData defenderHero = BattleTurnOrder.FindHero(_grid, !attackerIsAttackerSide);

            // New rule, per the user: an army with a unit that attacks in the Tactical Battle
            // Module loses its remaining strategic-map movement for the current player turn —
            // applied the moment the attack is declared (popup opened), regardless of the roll's
            // outcome.
            ArmyData attackerArmy = attackerIsAttackerSide ? _attacker : _defender;
            if (attackerArmy != null)
                foreach (UnitData member in attackerArmy.Members)
                    member.MoveCurrent = 0;

            attackPopup.Begin(attacker, attackerHero, defender, defenderHero, catalog != null ? catalog.logo : null,
                (damage, died) => OnAttackResolved(attacker, defender, damage, died), ShowAiThought);
        }

        private void OnAttackResolved(UnitData attacker, UnitData defender, int damage, bool defenderDied)
        {
            // A reaction from the side that TOOK the damage, only when that side is AI-controlled
            // — this is a first-person "how do I feel about this" line, not omniscient narration.
            // A death fires its own more specific UnitDied/EnemyKilled thought instead (see
            // RemoveUnit), so this only runs for a hit that didn't finish the target off.
            if (!defenderDied && damage > 0 && defender.Owner != null && !defender.Owner.IsHuman)
            {
                bool major = defender.HitPointsMax > 0 && defender.HitPointsCurrent <= defender.HitPointsMax / 3f;
                UnitData sideHero = BattleTurnOrder.FindHero(_grid, IsAttackerSide(defender));
                aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                    major ? AiThoughtCategory.DamageTakenMajor : AiThoughtCategory.DamageTakenMinor, attacker?.Name, sideHero != null));
            }

            if (defenderDied)
                RemoveUnit(defender);
            RefreshGrid();
            if (!CheckBattleEnd())
                EndTurn();
        }

        private bool IsAttackerSide(UnitData unit) => _grid.TryFindPosition(unit, out int row, out _) && BattleGrid.IsAttackerSideRow(row);

        private void RemoveUnit(UnitData unit)
        {
            // Captured before the grid cell is cleared below — needed both for the hero-side
            // lookup and because TryFindPosition can no longer find it afterward.
            bool wasAttackerSide = IsAttackerSide(unit);

            if (_grid.TryFindPosition(unit, out int row, out int col))
                _grid.Set(row, col, null);
            _attacker?.Members.Remove(unit);
            _defender?.Members.Remove(unit);

            // Whichever side `unit` belonged to reacts with UnitDied; the OTHER side (if
            // AI-controlled) gets a small EnemyKilled reaction instead — heroes never die this
            // way (never a valid attack target), so no special-casing needed there.
            UnitData deadSideHero = BattleTurnOrder.FindHero(_grid, wasAttackerSide);
            UnitData killerSideHero = BattleTurnOrder.FindHero(_grid, !wasAttackerSide);
            ArmyData deadSideArmy = wasAttackerSide ? _attacker : _defender;
            ArmyData killerSideArmy = wasAttackerSide ? _defender : _attacker;
            if (deadSideArmy?.Owner != null && !deadSideArmy.Owner.IsHuman)
                aiThoughts?.Show(deadSideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.UnitDied, unit.Name, deadSideHero != null));
            if (killerSideArmy?.Owner != null && !killerSideArmy.Owner.IsHuman)
                aiThoughts?.Show(killerSideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.EnemyKilled, unit.Name, killerSideHero != null));

            // Otherwise a unit killed mid-round could still come up "on turn" later this same
            // round (BuildOrder is only recomputed at BeginRound). Removing by index (not
            // List.Remove) so _turnIndex can be corrected if the removed entry sat before it —
            // it never IS _turnIndex itself (the current actor is always the attacker here, a
            // different unit from whichever defender just died).
            if (_turnOrder != null)
            {
                int index = _turnOrder.IndexOf(unit);
                if (index >= 0)
                {
                    _turnOrder.RemoveAt(index);
                    if (index <= _turnIndex)
                        _turnIndex--;
                }
            }
        }

        // Manual's "Battle Results": ends the instant either side has no more combat-capable
        // units (BattleInitiator.IsCombatCapable's own "at least one non-hero unit" rule) — true
        // if the battle just ended (caller should NOT also EndTurn in that case).
        private bool CheckBattleEnd()
        {
            bool attackerAlive = BattleInitiator.IsCombatCapable(_attacker);
            bool defenderAlive = BattleInitiator.IsCombatCapable(_defender);
            if (attackerAlive && defenderAlive)
                return false;

            FireBattleEndThought(_attacker, attackerAlive);
            FireBattleEndThought(_defender, defenderAlive);

            string message;
            if (_localArmy == null)
                message = attackerAlive ? "Attacker wins." : defenderAlive ? "Defender wins." : "Draw.";
            else
            {
                bool localWon = _localArmy == _attacker ? attackerAlive : defenderAlive;
                message = localWon ? "Victory!" : "Defeat!";
            }

            if (outcomePopup != null)
                outcomePopup.Show(message, OnBattleOutcomeAcknowledged);
            else
                OnBattleOutcomeAcknowledged();
            return true;
        }

        private void FireBattleEndThought(ArmyData army, bool survived)
        {
            if (army?.Owner == null || army.Owner.IsHuman)
                return;
            UnitData sideHero = BattleTurnOrder.FindHero(_grid, army == _attacker);
            aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                survived ? AiThoughtCategory.BattleWon : AiThoughtCategory.BattleLost, hasHero: sideHero != null));
        }

        private void OnBattleOutcomeAcknowledged()
        {
            // DeleteArmyIfEmptied only unregisters/destroys the ONE army's own marker — it
            // doesn't re-pick which army's marker should now represent this hex for a given
            // owner (see RestackArmiesOn's own comment). Without this, a second, untouched enemy
            // army sharing the hex (this battle only ever involved _attacker/_defender, see
            // BattleInitiator.FindEnemyAt picking just the first one) stayed hidden until the
            // player happened to reselect the hex — the same restack ArmyViewerModalUI's own
            // Hide() relies on, just done explicitly here since nothing re-selects the hex after
            // a battle closes.
            HexCoord hex = _attacker != null ? _attacker.Hex : (_defender != null ? _defender.Hex : default);

            // A normal fight-to-conclusion (not a retreat relocating one side away) leaves both
            // _attacker/_defender still on the SAME hex — if exactly one of them is still combat
            // capable and a DIFFERENT enemy army is still sitting on that hex, the manual's own
            // "two armies, one hex" case means this fight isn't actually over: the winner
            // continues straight into the next one instead of being left stuck on a hex it can
            // no longer leave (BattleInitiator.FindEnemyAt would still see the untouched army)
            // with no way to ever actually fight it (see the user's own report). Checked BEFORE
            // DeleteArmyIfEmptied below so a still-shared hex is the actual signal, not an
            // artifact of cleanup order.
            bool stillSameHex = _attacker != null && _defender != null && _attacker.Hex.Equals(_defender.Hex);
            ArmyData survivor = null;
            if (stillSameHex)
            {
                bool attackerCapable = BattleInitiator.IsCombatCapable(_attacker);
                bool defenderCapable = BattleInitiator.IsCombatCapable(_defender);
                if (attackerCapable != defenderCapable)
                    survivor = attackerCapable ? _attacker : _defender;
            }

            hexSelectionController?.DeleteArmyIfEmptied(_attacker);
            hexSelectionController?.DeleteArmyIfEmptied(_defender);
            hexSelectionController?.RestackArmiesOn(hex, null);

            ArmyData nextEnemy = survivor?.Owner != null ? BattleInitiator.FindEnemyAt(hex, survivor.Owner) : null;
            if (nextEnemy != null)
            {
                // Straight into the next fight, no separate Fight/Delay choice this time (the
                // survivor is already fully committed to this hex, there's nowhere else for it
                // to go) — same completion callback as the original Show, only actually invoked
                // once the whole chain finally ends in the Hide() below, not per-link.
                Show(hex, new List<ArmyData> { survivor, nextEnemy }, _onClosed);
                return;
            }

            Hide();
        }

        // The grace round is over — actually relocate (or destroy) the retreating army now, per
        // the user's own confirmed algorithm:
        //   - Battle hex isn't the retreating owner's own Barracks hex: aim for the nearest
        //     own-Barracks hex anywhere on the map, step one cell toward it.
        //   - Battle hex IS the retreating owner's own Barracks hex: aim for the nearest OTHER
        //     own-Barracks hex instead (can't "step toward" the hex already stood on); if none
        //     exists, step to a random free neighbor.
        //   - Whichever neighbor is picked must be a real map hex not held by an enemy
        //     combat-capable army (BattleInitiator.FindEnemyAt); if every neighbor is blocked,
        //     the manual's own "no valid retreat hex" rule applies — the army is destroyed
        //     outright, every card it contains discarded.
        private void ResolveRetreat()
        {
            ArmyData army = _retreatingArmy;
            _retreatingArmy = null;
            if (army == null)
            {
                _round++;
                BeginRound();
                return;
            }

            HexCoord battleHex = army.Hex;
            bool relocated = TryFindRetreatDestination(army, battleHex, out HexCoord destination);

            string message;
            if (relocated)
            {
                ArmyRegistry.MoveArmy(army, destination);
                hexSelectionController?.RestackArmiesOn(battleHex, null);
                hexSelectionController?.RestackArmiesOn(destination, null);
                message = _localArmy == army ? "Your army retreats." : "The enemy retreats.";
            }
            else
            {
                army.Members.Clear();
                hexSelectionController?.DeleteArmyIfEmptied(army);
                message = _localArmy == army ? "Your army is destroyed retreating!" : "The enemy army is destroyed retreating!";
            }

            if (outcomePopup != null)
                outcomePopup.Show(message, OnBattleOutcomeAcknowledged);
            else
                OnBattleOutcomeAcknowledged();
        }

        private bool TryFindRetreatDestination(ArmyData army, HexCoord battleHex, out HexCoord destination)
        {
            destination = default;
            if (map == null || army?.Owner == null)
                return false;

            BuildingData battleHexBuilding = BuildingRegistry.FindAt(battleHex);
            bool battleHexIsOwnBarracks = battleHexBuilding != null && battleHexBuilding.Owner == army.Owner
                && battleHexBuilding.HasAbility(BuildingAbilities.Barracks);

            HexCoord? target = FindNearestOwnBarracksHex(army.Owner, battleHex, excludeBattleHex: battleHexIsOwnBarracks);
            if (target.HasValue)
                return TryPickNeighborToward(army, battleHex, target.Value, out destination);

            // No Barracks hex to aim for at all (either none exist, or the only one IS the
            // battle hex itself with nowhere else useful to point at) — a random free neighbor.
            return TryPickRandomNeighbor(army, battleHex, out destination);
        }

        // This project's existing stand-in for the manual's "friendly outpost or stronghold"
        // (see HexSelectionController.DeleteArmyIfEmptied's identical Barracks-ability
        // convention) — nearest by plain hex distance, no pathfinding/obstacle-avoidance,
        // matching the user's own "just aim toward it" spec.
        private static HexCoord? FindNearestOwnBarracksHex(PlayerSetupData owner, HexCoord from, bool excludeBattleHex)
        {
            HexCoord? best = null;
            int bestDist = int.MaxValue;
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != owner || !building.HasAbility(BuildingAbilities.Barracks))
                    continue;
                if (excludeBattleHex && building.Hex.Equals(from))
                    continue;
                int dist = HexGridMath.Distance(from, building.Hex);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = building.Hex;
                }
            }
            return best;
        }

        // A single greedy step among battleHex's 6 neighbors — whichever unblocked one reduces
        // distance to target the most, not a full path. False if every neighbor is off the map
        // or held by an enemy combat-capable army.
        private bool TryPickNeighborToward(ArmyData army, HexCoord battleHex, HexCoord target, out HexCoord destination)
        {
            destination = default;
            int bestDist = int.MaxValue;
            bool found = false;
            foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
            {
                HexCoord candidate = new HexCoord(battleHex.Q + dq, battleHex.R + dr);
                if (!IsFreeRetreatHex(army, candidate))
                    continue;
                int dist = HexGridMath.Distance(candidate, target);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    destination = candidate;
                    found = true;
                }
            }
            return found;
        }

        private bool TryPickRandomNeighbor(ArmyData army, HexCoord battleHex, out HexCoord destination)
        {
            destination = default;
            var options = new List<HexCoord>();
            foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
            {
                HexCoord candidate = new HexCoord(battleHex.Q + dq, battleHex.R + dr);
                if (IsFreeRetreatHex(army, candidate))
                    options.Add(candidate);
            }
            if (options.Count == 0)
                return false;
            destination = options[UnityEngine.Random.Range(0, options.Count)];
            return true;
        }

        private bool IsFreeRetreatHex(ArmyData army, HexCoord hex)
        {
            if (map == null || !map.TryGetTerrainAt(hex, out _))
                return false;
            return BattleInitiator.FindEnemyAt(hex, army.Owner) == null;
        }

        // Resolves a drag started on `dragged` (see BattleGridCellUI.OnEndDrag) — the drop only
        // succeeds while Arranging and only onto another cell within the SAME local player's own
        // rows (may be empty or occupied; occupied just swaps the two). Anywhere else (enemy
        // rows, the neutral row, off the grid entirely) snaps back to where it was picked up
        // from by simply doing nothing here — RefreshGrid was never called, so the cell's own
        // Setup data (and therefore its rendered position/content) is untouched.
        public void TryDropOnCell(BattleGridCellUI dragged, Vector2 screenPosition)
        {
            if (!_arranging || !_arrangeInteractive || dragged == null || !IsLocalRow(dragged.Row))
                return;

            Camera cam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
            foreach (BattleGridCellUI cell in _cells)
            {
                if (cell == dragged || !IsLocalRow(cell.Row))
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(cell.RectTransform, screenPosition, cam))
                    continue;

                _grid.Swap(dragged.Row, dragged.Col, cell.Row, cell.Col);
                RefreshGrid();
                return;
            }
        }

        // Shown for whoever's currently up in the turn order by default, or any unit clicked
        // directly in the grid (see BattleGridCellUI.OnPointerClick) — same "click to inspect"
        // pattern as ArmyViewerModalUI.ShowUnitDetail, just this screen's own copy since the
        // framing here is pure combat stats, not army/capacity.
        public void ShowUnitDetail(UnitData unit)
        {
            if (detailArt != null)
            {
                detailArt.sprite = unit != null ? unit.Art : null;
                detailArt.gameObject.SetActive(unit != null);
            }
            if (detailText == null)
                return;
            if (unit == null)
            {
                detailText.text = string.Empty;
                return;
            }

            string text = $"{unit.Name}\n" +
                $"Attack {unit.Attack}\n" +
                $"Defense {unit.Defense}\n" +
                $"Resistance {unit.Resistance}\n" +
                $"Range {unit.Range}\n" +
                $"HP {unit.HitPointsCurrent}/{unit.HitPointsMax}\n" +
                $"Move {unit.MoveCurrent}/{unit.MoveMax}\n" +
                $"Initiative {unit.Initiative}";
            if (unit.IsHero)
                text += $"\nCommand Rating: {unit.CommandRating}\nFate: {unit.Fate}";
            string abilities = gameConfig != null ? gameConfig.FormatAbilities(unit.Abilities) : null;
            if (!string.IsNullOrEmpty(abilities))
                text += $"\n{abilities}";
            detailText.text = text;
        }

        // Called by OnBattleOutcomeAcknowledged once the outcome popup is dismissed — there's
        // still no separate Quit/Close (see the class comment), this is only ever reached via a
        // battle actually finishing.
        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
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
