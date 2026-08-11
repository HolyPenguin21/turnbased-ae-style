using System;
using System.Collections;
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // The "Ground Combat" popup (two references the user supplied: the roll/spend screen and the
    // result screen) — one component, two states swapped via rollStateRoot/resultStateRoot.
    //
    // Roll state: both sides' BattleCombatantRowUI, a Roll Die button, and a single shared Accept
    // button whose meaning depends on whose turn it currently is in the Fate duel (see RunDuel).
    // Per the user's own spec: the defender always gets the first say (Defender's Prerogative),
    // and every single Spend immediately hands the "spend again or end the challenge" decision to
    // the OTHER side — this alternates for as long as either side keeps spending, and only
    // resolves once two decisions in a row go by with nobody spending (both sides having had a
    // fair, undisturbed last look). A side with no hero (no Fate to spend) or that isn't the local
    // human still gets its turn — the AI evaluates via BattleAi.ShouldSpendFate, a human still has
    // to explicitly click Accept even with nothing to Spend — but never spends more than once
    // before yielding the decision back (see RunTurn). Every turn's own reroll animation is
    // awaited before the NEXT turn (or Resolve) begins, and Accept/Spend are locked out for that
    // whole window — see the user's own report: the AI's own reactive reroll used to resolve
    // invisibly in the same frame as the human's own Spend/Accept click, so a tied roll the player
    // could see with their own eyes still lost right in front of them.
    //
    // Result state: attacker's own art (standing in for the original's cinematic "scan" insert),
    // a text summary, and the target's outcome with a DESTROYED stamp if it died.
    public class BattleAttackPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        // Tunable magnitudes for CriticalDamage/CeramicArmor/Berserk (see ResolveDamage) — same
        // direct-reference pattern as BattleScreenUI's own FactionCardCatalog field. Every
        // lookup below falls back to the manual's own default numbers if this isn't assigned.
        [SerializeField] private UnitAbilityCatalog abilityCatalog;

        [Header("Roll State")]
        [SerializeField] private GameObject rollStateRoot;
        [SerializeField] private BattleCombatantRowUI attackerRow;
        [SerializeField] private BattleCombatantRowUI defenderRow;
        [SerializeField] private Button rollButton;
        [SerializeField] private Button acceptButton;

        [Header("Result State")]
        [SerializeField] private GameObject resultStateRoot;
        [SerializeField] private Image resultArtImage;
        [SerializeField] private TMP_Text resultSummaryText;
        [SerializeField] private Image resultTargetArtImage;
        [SerializeField] private TMP_Text resultTargetNameText;
        [SerializeField] private TMP_Text resultTargetHpText;
        [SerializeField] private GameObject destroyedStamp;
        [SerializeField] private Button okButton;

        private enum Phase { NotRolled, InProgress, Resolved }
        private Phase _phase;

        // ---- Fate duel state (see RunDuel) — valid only while _phase == InProgress and only
        // meaningful during a human turn (RunHumanTurn); AI turns never touch these, they just run
        // RunAiTurn synchronously-within-the-coroutine and read the result straight back. ----
        private bool _awaitingHumanDecision;
        // Which side's turn is currently awaiting a human click — guards OnDefenderSpend/
        // OnAttackerSpend so a stray click from the SIDE THAT ISN'T CURRENTLY DECIDING (its
        // button should be non-interactable anyway, but a click already queued the frame the
        // button was disabled is still possible) can't sneak a reroll in out of turn.
        private bool _defenderTurnActive;
        private bool _humanSpent;
        private bool _humanDeclined;
        // True for the window between a reroll being kicked off (human or AI) and its flip
        // animation actually landing — see RunTurn/RunHumanTurn/RunAiTurn's own WaitUntil on this.
        private bool _rerollAnimating;
        // Whatever the turn that just ran (RunTurn) actually did — read by RunDuel right after
        // `yield return RunTurn(...)` returns, since a coroutine can't hand back an ordinary
        // return value.
        private bool _turnSpent;

        // Which win condition Resolve() applies once both sides have accepted — every Challenge
        // in the manual (Ground Combat, Capture Kill, and eventually Retreat/Assassination/
        // Sabotage/Sniper/...) shares this same Roll/Defender's-Prerogative/Accept shell, they
        // just differ in how dice-pool sizes are computed and what the result means. See
        // BeginCaptureKill for the second one; add further Begin*/Resolve* pairs here rather than
        // a whole new popup component per challenge type.
        private enum ChallengeKind { GroundCombat, CaptureKill, Announcement }
        private ChallengeKind _kind;

        private UnitData _attacker;
        private UnitData _defender;
        private UnitData _attackerHero;
        private UnitData _defenderHero;
        // Whether the DEFENDER's own army is the one currently retreating (see BattleScreenUI.
        // _retreatingArmy) — the attacker is never the retreating side, since a retreating army's
        // units are excluded from the turn order and can't act (see BattleScreenUI.
        // OnStartRoundClicked), only get attacked. Feeds BattleAi.ShouldSpendFate's own Fate-
        // conservation rule for that case (see RunAiTurn).
        private bool _defenderIsRetreating;
        // GroundCombat only — terrain modifier + (Base-tagged building's own Defense), folded
        // straight into the SAME roll as any other Ground Combat attack rather than a separate
        // manual-style Siege Challenge (see BattleScreenUI.Combat.cs's BeginAttack, the only
        // caller that ever sets this to non-zero). Never applied to the attacker's own pool.
        private int _defenderBonusDice;
        // CaptureKill mode only — the hunter's computed dice-pool size (see BeginCaptureKill;
        // there's no per-unit Attack stat to roll against, unlike Ground Combat) and how many
        // dice the target hero rolls (their Fate stat, per the manual: "the target hero receives
        // a dice pool equal to his fate"), captured once at OnRollClicked time so a later reroll
        // spending that same Fate down doesn't change how many dice were actually in this roll.
        // Outcome itself (see ResolveCaptureKill) compares actual successes only, not this pool
        // size — per the user's own call, dropping the manual's separate "capture threshold".
        private int _hunterDicePool;
        private int _targetDicePoolSize;
        private CaptureKillOutcome _captureKillOutcome;
        private bool[] _attackerDice;
        private bool[] _defenderDice;
        private int _resultDamage;
        private bool _resultDied;
        private Action<int, bool> _onResolved;
        private Action<CaptureKillOutcome> _onCaptureKillResolved;
        private Action _onAnnouncementAcknowledged;
        private Action<UnitData, AiThoughtCategory, string> _onAiThought;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
            if (rollButton != null)
                rollButton.onClick.AddListener(OnRollClicked);
            if (acceptButton != null)
                acceptButton.onClick.AddListener(OnAcceptClicked);
            if (okButton != null)
                okButton.onClick.AddListener(OnOkClicked);
            if (attackerRow != null)
                attackerRow.SpendClicked += OnAttackerSpend;
            if (defenderRow != null)
                defenderRow.SpendClicked += OnDefenderSpend;
        }

        // Space as a shortcut for Ok, per the user's own request — gated on resultStateRoot
        // specifically (not just IsShowing), so Space during the Roll state doesn't accidentally
        // fire Ok before Accept/Roll even have anything to do with it; Ok is the only button
        // live once the Result state is showing, same as BattleArrangePopupUI's own version of
        // this.
        private void Update()
        {
            if (IsShowing && resultStateRoot != null && resultStateRoot.activeSelf && UIFocusUtility.WasSpacePressed())
                OnOkClicked();
        }

        // attackerHero/defenderHero are that SIDE's hero if present (may be null) — Fate is a
        // per-side resource the hero contributes, not something the attacking/defending unit card
        // itself needs to be a hero to use (see BattleCombatantRowUI's own comment); the caller
        // (BattleScreenUI) already has the grid to look these up from.
        // attackerPoolSize/defenderPoolSize (optional): only BeginCaptureKill needs to override
        // these — its pools are _hunterDicePool/the target hero's own Fate, nothing to do with
        // Attack/Defense (see BeginCaptureKill's own comment). Ground Combat's default (attacker.
        // Attack / defender.Defense + defenderBonusDice) matches exactly what OnRollClicked
        // itself rolls against, just surfaced before Roll Die is even clicked (see the user's own
        // request to see each side's pool size up front, not just its post-roll success count).
        public void Begin(UnitData attacker, UnitData attackerHero, UnitData defender, UnitData defenderHero,
            Sprite factionLogo, Action<int, bool> onResolved, Action<UnitData, AiThoughtCategory, string> onAiThought = null,
            bool defenderIsRetreating = false, int defenderBonusDice = 0, int? attackerPoolSize = null, int? defenderPoolSize = null)
        {
            // Stops a still-running RunRollAndDuel/RunDuel coroutine from a PREVIOUS Begin() on
            // this same (pooled/reused) popup instance — otherwise its delayed WaitUntil callbacks
            // could fire against the fields this call is about to overwrite.
            StopAllCoroutines();

            _kind = ChallengeKind.GroundCombat;
            _attacker = attacker;
            _defender = defender;
            _attackerHero = attackerHero;
            _defenderHero = defenderHero;
            _defenderIsRetreating = defenderIsRetreating;
            _defenderBonusDice = defenderBonusDice;
            _onResolved = onResolved;
            _onAiThought = onAiThought;
            _attackerDice = null;
            _defenderDice = null;
            _phase = Phase.NotRolled;
            _awaitingHumanDecision = false;
            _humanSpent = false;
            _humanDeclined = false;
            _rerollAnimating = false;

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            if (rollStateRoot != null)
                rollStateRoot.SetActive(true);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(false);

            attackerRow?.Setup(attacker, attackerHero, factionLogo);
            defenderRow?.Setup(defender, defenderHero, factionLogo);
            attackerRow?.SetDicePoolSize(attackerPoolSize ?? (attacker?.Attack ?? 0));
            defenderRow?.SetDicePoolSize(defenderPoolSize ?? ((defender?.Defense ?? 0) + defenderBonusDice));
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (rollButton != null)
                rollButton.interactable = true;
            if (acceptButton != null)
                acceptButton.interactable = false;
        }

        // The manual's "Capture Kill Challenge" (pg. 24) — same Roll/Defender's-Prerogative/
        // Accept shell as Ground Combat (see Begin, reused verbatim here), but the dice pools and
        // the win condition are entirely different, so this sets them up itself instead of going
        // through Begin's Attack/Defense-stat plumbing:
        //   - Hunter's pool = 1 + (hunterArmy's own non-hero unit count / 2). The manual also adds
        //     "highest observation strength in hex" — Observation/Recce doesn't exist in this
        //     project yet (see BattleInitiator's own Stealth note), so that term is 0 for now.
        //   - Target's pool = the hunted hero's own Fate stat (see the manual: "The target hero
        //     receives a dice pool equal to his fate").
        //   - The hunter side gets a Fate row exactly when the hunting army itself has a hero
        //     along for the ride — that hero's own Fate, same as any Ground Combat attacker (per
        //     the user's own call: no separate "Bounty Hunter" skill gate, unlike the manual's
        //     own rule — this project doesn't have that skill and isn't waiting on it). A
        //     hunting army with no hero at all still has nothing to spend (attackerHero stays
        //     null, same as before), so its Attacker phase still just auto-resolves.
        //   - The CALLER (BattleScreenUI.Combat.cs) is responsible for checking that the hunting
        //     army actually has at least one non-hero unit before calling this at all — a hero
        //     hunting alone requires a skill (e.g. the manual's own "Hunter") this project doesn't
        //     have yet either; see the user's own note to add that as a future task once such
        //     hero skills exist.
        public void BeginCaptureKill(ArmyData hunterArmy, UnitData targetHero, Sprite factionLogo,
            Action<CaptureKillOutcome> onResolved, Action<UnitData, AiThoughtCategory, string> onAiThought = null)
        {
            UnitData hunterHero = hunterArmy?.Members.Find(m => m.IsHero);
            UnitData hunterFace = hunterHero ?? hunterArmy?.Members.Find(m => !m.IsHero);
            int hunterUnits = hunterArmy?.Members.FindAll(m => !m.IsHero).Count ?? 0;
            _hunterDicePool = 1 + hunterUnits / 2;
            _onCaptureKillResolved = onResolved;

            Begin(hunterFace, hunterHero, targetHero, targetHero, factionLogo, null, onAiThought,
                attackerPoolSize: _hunterDicePool, defenderPoolSize: targetHero?.Fate ?? 0);
            _kind = ChallengeKind.CaptureKill;
        }

        // A plain single-screen announcement — no roll, no dice, no attacker/defender rows —
        // reusing just this popup's Result state (rollStateRoot skipped entirely) for a message
        // that isn't really a Challenge at all, e.g. "Your army retreats." after a hero-only
        // army's Capture Kill Challenge ends in Escaped (see BattleScreenUI.Combat.cs's
        // HandleCaptureKillOutcome) — same panel the user asked for (BattleAttackPopupUI's own
        // ResultStateRoot) rather than a brand new popup for what's a one-line acknowledgement.
        public void ShowAnnouncement(string message, Action onAcknowledged)
        {
            _kind = ChallengeKind.Announcement;
            _onAnnouncementAcknowledged = onAcknowledged;

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
                resultArtImage.gameObject.SetActive(false);
            if (resultTargetArtImage != null)
                resultTargetArtImage.gameObject.SetActive(false);
            if (resultTargetNameText != null)
                resultTargetNameText.text = string.Empty;
            if (resultTargetHpText != null)
                resultTargetHpText.text = string.Empty;
            if (destroyedStamp != null)
                destroyedStamp.SetActive(false);
            if (resultSummaryText != null)
                resultSummaryText.text = message;
        }

        private void OnRollClicked()
        {
            if (_phase != Phase.NotRolled)
                return;
            _phase = Phase.InProgress;
            if (rollButton != null)
                rollButton.interactable = false;
            StartCoroutine(RunRollAndDuel());
        }

        // Rolls, reveals both rows (awaiting their flip animations — see the user's own report:
        // Accept used to unlock before the dice had even visibly landed), then runs the Fate duel
        // (see RunDuel) and only resolves once it's actually over.
        private IEnumerator RunRollAndDuel()
        {
            ChallengeResult result;
            if (_kind == ChallengeKind.CaptureKill)
            {
                _targetDicePoolSize = _defenderHero?.Fate ?? 0;
                result = ChallengeResolver.Resolve(_hunterDicePool, _targetDicePoolSize);
            }
            else
            {
                result = ChallengeResolver.Resolve(_attacker.Attack, _defender.Defense + _defenderBonusDice);
            }
            _attackerDice = result.AttackerDice;
            _defenderDice = result.DefenderDice;

            bool attackerAnimating = attackerRow != null;
            bool defenderAnimating = defenderRow != null;
            attackerRow?.SetDice(_attackerDice, -1, () => attackerAnimating = false);
            defenderRow?.SetDice(_defenderDice, -1, () => defenderAnimating = false);
            yield return new WaitUntil(() => !attackerAnimating && !defenderAnimating);

            FireRollThought();
            yield return RunDuel();
            Resolve();
        }

        // First-look reaction to the fresh roll, before any Fate is spent — fires for whichever
        // side(s) are AI-controlled, independent of whether that side even has a hero (a hero-less
        // side still "feels" about its own roll, it just can't act on it with Fate).
        private void FireRollThought()
        {
            int damage = new ChallengeResult(_attackerDice, _defenderDice).Damage;
            if (IsAiSide(_attacker))
                _onAiThought?.Invoke(_attacker, damage > 0 ? AiThoughtCategory.GoodRoll : AiThoughtCategory.BadRoll, _defender?.Name);
            if (IsAiSide(_defender))
                _onAiThought?.Invoke(_defender, damage > 0 ? AiThoughtCategory.BadRoll : AiThoughtCategory.GoodRoll, _attacker?.Name);
        }

        private static bool IsAiSide(UnitData unit) => unit != null && unit.Owner != null && !unit.Owner.IsHuman;

        // Whether a Spend button should ever be interactable for `hero` — needs both a hero AND
        // Fate to spend from a human's own hand (an AI side spends automatically instead, see
        // RunAiTurn). NOT the same question as "should this turn pause for Accept" — a human side
        // with no hero (e.g. a CaptureKill hunter army with no hero of its own — see
        // BeginCaptureKill's own note) still has nothing to SPEND, but still clicked Roll Die and
        // still needs to see the result and click Accept themselves.
        private static bool CanSpend(UnitData hero) => hero != null && hero.Owner != null && hero.Owner.IsHuman;

        // The Fate duel itself — per the user's own spec, worked through by example:
        //   "первый рол 3:2. Именно защитник решает начинать ли перебросы. Если перебросы начаты,
        //    то закончить их может тот, кто их не делал в раунде переброса: если защищающийся
        //    нажал Spend, то атакующий решает — делать переброс или заканчивать челлендж (если
        //    защитник тоже решил нажать Spend, право закончить челлендж передаётся защищающемуся)."
        // Two different endings, depending on whether a Spend has happened yet AT ALL:
        //   - Before the first Spend: both sides still get their own untouched first look (matches
        //     the old two-phase design) — a decline here just hands the still-fresh roll to the
        //     OTHER side; only once BOTH have declined with nothing having changed does it resolve.
        //   - Once at least one Spend has happened: every subsequent turn is a REACTION to that
        //     spend, and belongs to whichever side did NOT just spend — a decline there is final
        //     (resolves immediately, per the quote above), while a spend hands the "end or
        //     continue" decision right back to the other side, and so on for as long as either
        //     side keeps going.
        private IEnumerator RunDuel()
        {
            bool anySpendYet = false;
            bool isDefenderTurn = true;
            int consecutiveDeclinesBeforeAnySpend = 0;
            while (true)
            {
                yield return RunTurn(isDefenderTurn);
                if (_turnSpent)
                {
                    anySpendYet = true;
                    isDefenderTurn = !isDefenderTurn;
                    continue;
                }
                if (anySpendYet)
                    yield break; // a reactive decline ends the duel on the spot
                if (++consecutiveDeclinesBeforeAnySpend >= 2)
                    yield break; // both sides had their untouched first look and neither wanted it
                isDefenderTurn = !isDefenderTurn;
            }
        }

        // One side's whole decision window, which can include SEVERAL Spends before it concludes
        // (see the user's own example: defender Spend → Accept; attacker Spend, Spend-again,
        // *then* effectively done) — a single Spend does NOT hand the turn to the other side by
        // itself anymore; only Accept (human) or the AI running out of reasons to keep going does.
        // AI evaluates and acts on its own (see RunAiTurn); a human side always gets an explicit
        // Accept click, even with nothing to Spend on, to conclude — same as the old two-phase
        // design's own "still needs to see the result and click Accept" behavior, just now able to
        // Spend more than once first.
        private IEnumerator RunTurn(bool isDefenderTurn)
        {
            UnitData actingUnit = isDefenderTurn ? _defender : _attacker;
            if (IsAiSide(actingUnit))
            {
                yield return RunAiTurn(isDefenderTurn);
                yield break;
            }
            yield return RunHumanTurn(isDefenderTurn);
        }

        // Loops Spend-or-Accept for as long as this side keeps spending — each successful Spend
        // re-offers the SAME choice (Spend again / Accept) rather than ending the turn, per the
        // user's own report: Accept used to silently mean "give up the whole challenge" the moment
        // ANY Spend had happened, when it should just mean "I'm done deciding for THIS turn."
        // _turnSpent (read by RunDuel once this returns) is true iff at least one Spend landed
        // ANYWHERE in this turn, not just the last loop iteration.
        private IEnumerator RunHumanTurn(bool isDefenderTurn)
        {
            bool spentThisTurn = false;
            while (true)
            {
                UnitData hero = isDefenderTurn ? _defenderHero : _attackerHero;
                bool[] ownDice = isDefenderTurn ? _defenderDice : _attackerDice;
                bool canSpend = CanSpend(hero) && hero.Fate > 0 && HasMiss(ownDice);

                _defenderTurnActive = isDefenderTurn;
                _humanSpent = false;
                _humanDeclined = false;
                _awaitingHumanDecision = true;
                if (isDefenderTurn)
                    defenderRow?.SetSpendInteractable(canSpend);
                else
                    attackerRow?.SetSpendInteractable(canSpend);
                if (acceptButton != null)
                    acceptButton.interactable = true;

                yield return new WaitUntil(() => _humanSpent || _humanDeclined);

                _awaitingHumanDecision = false;
                defenderRow?.SetSpendInteractable(false);
                attackerRow?.SetSpendInteractable(false);
                if (acceptButton != null)
                    acceptButton.interactable = false;

                if (_humanDeclined)
                    break;

                spentThisTurn = true;
                yield return new WaitUntil(() => !_rerollAnimating);
                // Loop back — same side, same turn, offered Spend-or-Accept again against the
                // now-updated dice.
            }
            _turnSpent = spentThisTurn;
        }

        // AI path for a whole duel turn — mirrors the human Spend/Accept loop exactly (may spend
        // more than once), just decided via BattleAi.ShouldSpendFate instead of a click. Per the
        // user's own correction: the AI isn't "obligated" to keep rerolling — it evaluates the
        // CURRENT dice fresh before EVERY reroll (same as a human deciding again after each one)
        // and stops the instant ShouldSpendFate says it's no longer worth it; getting a turn at
        // all only happens because the other side just spent, per RunDuel.
        private IEnumerator RunAiTurn(bool isDefenderTurn)
        {
            UnitData hero = isDefenderTurn ? _defenderHero : _attackerHero;
            bool hadInitialMiss = HasMiss(isDefenderTurn ? _defenderDice : _attackerDice);
            bool spentThisTurn = false;

            while (true)
            {
                bool[] ownDice = isDefenderTurn ? _defenderDice : _attackerDice;
                bool isRetreating = isDefenderTurn && _defenderIsRetreating;
                int defendingUnitHp = _defender != null ? _defender.HitPointsCurrent : int.MaxValue;
                bool shouldSpend = hero != null && hero.Fate > 0 && HasMiss(ownDice) && BattleAi.ShouldSpendFate(
                    _attackerDice, _defenderDice, hero.Fate, isDefenderTurn,
                    _attacker, _defender, CriticalDamageMultiplier, HyperkineticBonusDamage, CeramicArmorReduction,
                    isRetreating, defendingUnitHp, _kind == ChallengeKind.CaptureKill);
                if (!shouldSpend)
                    break;

                bool rerolled = isDefenderTurn
                    ? RerollOneMiss(ref _defenderDice, out int rerolledIndex)
                    : RerollOneMiss(ref _attackerDice, out rerolledIndex);
                if (!rerolled)
                    break;
                hero.Fate--;
                spentThisTurn = true;

                bool animating = isDefenderTurn ? defenderRow != null : attackerRow != null;
                if (isDefenderTurn)
                {
                    defenderRow?.SetDice(_defenderDice, rerolledIndex, () => animating = false);
                    defenderRow?.OnFateSpent();
                }
                else
                {
                    attackerRow?.SetDice(_attackerDice, rerolledIndex, () => animating = false);
                    attackerRow?.OnFateSpent();
                }
                yield return new WaitUntil(() => !animating);
                // Loop back — re-evaluate from scratch against the now-updated dice before
                // deciding whether another reroll is still worth it.
            }

            if (hadInitialMiss)
            {
                UnitData acting = isDefenderTurn ? _defender : _attacker;
                UnitData opposing = isDefenderTurn ? _attacker : _defender;
                _onAiThought?.Invoke(acting, spentThisTurn ? AiThoughtCategory.FateSpendWorthIt : AiThoughtCategory.FateSpendSkip, opposing?.Name);
            }
            _turnSpent = spentThisTurn;
        }

        private void OnAcceptClicked()
        {
            if (!_awaitingHumanDecision)
                return;
            _humanDeclined = true;
            defenderRow?.SetSpendInteractable(false);
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
        }

        private void Resolve()
        {
            _phase = Phase.Resolved;
            if (_kind == ChallengeKind.CaptureKill)
                ResolveCaptureKill();
            else
                ResolveDamage();
        }

        private void OnDefenderSpend()
        {
            if (!_awaitingHumanDecision || !_defenderTurnActive || _defenderHero == null || _defenderHero.Fate <= 0)
                return;
            if (!RerollOneMiss(ref _defenderDice, out int rerolledIndex))
                return;
            _defenderHero.Fate--;
            defenderRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
            _rerollAnimating = true;
            defenderRow?.SetDice(_defenderDice, rerolledIndex, () => _rerollAnimating = false);
            defenderRow?.OnFateSpent();
            _humanSpent = true;
        }

        private void OnAttackerSpend()
        {
            if (!_awaitingHumanDecision || _defenderTurnActive || _attackerHero == null || _attackerHero.Fate <= 0)
                return;
            if (!RerollOneMiss(ref _attackerDice, out int rerolledIndex))
                return;
            _attackerHero.Fate--;
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
            _rerollAnimating = true;
            attackerRow?.SetDice(_attackerDice, rerolledIndex, () => _rerollAnimating = false);
            attackerRow?.OnFateSpent();
            _humanSpent = true;
        }

        private static bool HasMiss(bool[] dice)
        {
            if (dice == null)
                return false;
            foreach (bool hit in dice)
                if (!hit)
                    return true;
            return false;
        }

        // Rerolls the first still-missed die found (no per-die selection UI — matches the
        // reference's single Spend button, not a click-a-die-to-target interaction).
        // rerolledIndex (out): which slot actually got rerolled, so the caller can force THAT
        // slot's flip animation regardless of whether the new value happens to match the old one
        // (see BattleCombatantRowUI.SetDice's own comment — a plain before/after value diff
        // can't tell "unchanged" apart from "rerolled back to the same result").
        private static bool RerollOneMiss(ref bool[] dice, out int rerolledIndex)
        {
            rerolledIndex = -1;
            if (dice == null)
                return false;
            for (int i = 0; i < dice.Length; i++)
                if (!dice[i])
                {
                    dice[i] = ChallengeResolver.RollDice(1)[0];
                    rerolledIndex = i;
                    return true;
                }
            return false;
        }

        // Manual: "the number of success rolls for the defender is subtracted from the number of
        // success rolls for the attacker" — one-directional, only the defender ever takes damage
        // in a Ground Combat challenge (no retaliation in this pass).
        private void ResolveDamage()
        {
            _phase = Phase.Resolved;
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;

            var result = new ChallengeResult(_attackerDice, _defenderDice);
            // UnitAbilities.CriticalDamage/Hyperkinetic/CeramicArmor — see ChallengeResult.
            // ApplyAbilityModifiers for the fixed order (x2 multiplier, then the Hyperkinetic
            // bonus, then CeramicArmor's flat reduction last so it always comes off the
            // already-boosted total) — shared with BattleAi.ShouldSpendFate so the AI's
            // Fate-spend prediction always matches the damage actually dealt here.
            // wasHit reads the RAW dice roll, not the ability-adjusted damage below — a hit that
            // CeramicArmor reduces all the way to 0 is still a hit, not a miss (see ShowResult).
            bool wasHit = result.Damage > 0;
            int damage = ChallengeResult.ApplyAbilityModifiers(result.Damage, _attacker, _defender,
                CriticalDamageMultiplier, HyperkineticBonusDamage, CeramicArmorReduction,
                out List<string> appliedAbilities);

            _defender.HitPointsCurrent = Mathf.Max(0, _defender.HitPointsCurrent - damage);
            bool died = _defender.HitPointsCurrent <= 0;

            // UnitAbilities.Berserk (pg. 40): "+1 Attack and -1 Def each time it is hit... for
            // the duration of the battle" — applied directly to the live stats (BattleGrid/
            // BattleTurnOrder/every other Challenge already just reads UnitData.Attack/Defense,
            // so there's no separate "effective stat" layer to thread through instead).
            // BerserkStacks records how much was added so BattleScreenUI.Combat.cs's
            // FinishBattleEnd can revert it once the battle's over — a permanent buff was never
            // the intent, just a same-battle snowball.
            if (damage > 0 && _defender.HasAbility(UnitAbilities.Berserk))
            {
                _defender.Attack += BerserkAttackGain;
                _defender.Defense -= BerserkDefenseLoss;
                _defender.BerserkStacks++;
            }

            _resultDamage = damage;
            _resultDied = died;
            ShowResult(damage, died, wasHit, appliedAbilities);
        }

        private float CriticalDamageMultiplier => abilityCatalog != null ? abilityCatalog.criticalDamageMultiplier : 2f;
        private int CeramicArmorReduction => abilityCatalog != null ? abilityCatalog.ceramicArmorReduction : 1;
        private int BerserkAttackGain => abilityCatalog != null ? abilityCatalog.berserkAttackGain : 1;
        private int BerserkDefenseLoss => abilityCatalog != null ? abilityCatalog.berserkDefenseLoss : 1;
        private int HyperkineticBonusDamage => abilityCatalog != null ? abilityCatalog.hyperkineticBonusDamage : 2;

        // Purely the rolled successes decide this — per the user's own call, dropping the
        // manual's separate "capture threshold" (comparing the hunter's successes against the
        // target's full original dice-pool size) since that let a clean win still resolve as a
        // kill for reasons the player can't see on the dice themselves:
        //   Escaped  — attacker successes < defender successes.
        //   Killed   — attacker successes == defender successes (a bare, even win).
        //   Captured — attacker successes > defender successes (a clean win).
        // No HP/damage involved, unlike ResolveDamage.
        private void ResolveCaptureKill()
        {
            _phase = Phase.Resolved;
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;

            var result = new ChallengeResult(_attackerDice, _defenderDice);
            CaptureKillOutcome outcome;
            if (result.AttackerSuccesses < result.DefenderSuccesses)
                outcome = CaptureKillOutcome.Escaped;
            else if (result.AttackerSuccesses > result.DefenderSuccesses)
                outcome = CaptureKillOutcome.Captured;
            else
                outcome = CaptureKillOutcome.Killed;

            _captureKillOutcome = outcome;
            ShowCaptureKillResult(outcome);
        }

        private void ShowCaptureKillResult(CaptureKillOutcome outcome)
        {
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
            {
                resultArtImage.sprite = _attacker?.Art;
                resultArtImage.gameObject.SetActive(_attacker?.Art != null);
            }
            if (resultSummaryText != null)
            {
                string outcomeLine = outcome switch
                {
                    CaptureKillOutcome.Escaped => "The hero evades the hunters and remains free.",
                    CaptureKillOutcome.Captured => "The hero is captured!",
                    _ => "The hero is killed while attempting to escape!",
                };
                resultSummaryText.text = $"Capture Kill Challenge\nTarget: {_defender?.Name}\n{outcomeLine}";
            }
            if (resultTargetArtImage != null)
            {
                resultTargetArtImage.sprite = _defender?.Art;
                resultTargetArtImage.gameObject.SetActive(_defender?.Art != null);
            }
            if (resultTargetNameText != null)
                resultTargetNameText.text = _defender?.Name;
            if (resultTargetHpText != null)
                resultTargetHpText.text = outcome == CaptureKillOutcome.Escaped && _defender != null
                    ? $"HP: {_defender.HitPointsCurrent}/{_defender.HitPointsMax}"
                    : string.Empty;
            // Captured is not a death — only Killed earns the Destroyed stamp (see the user's
            // own report: a 3:2 win that resolved as Captured still showed Destroyed here).
            if (destroyedStamp != null)
                destroyedStamp.SetActive(outcome == CaptureKillOutcome.Killed);
        }

        private void ShowResult(int damage, bool died, bool wasHit, List<string> appliedAbilities)
        {
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
            {
                resultArtImage.sprite = _attacker.Art;
                resultArtImage.gameObject.SetActive(_attacker.Art != null);
            }
            if (resultSummaryText != null)
            {
                string hitLine = wasHit ? "Hit!" : "Miss";
                string outcomeLine = died ? "\nThe target was destroyed." : string.Empty;
                // Only shown when an ability actually changed the outcome (e.g. CeramicArmor
                // absorbing an otherwise-landed hit down to 0) — a plain unmodified hit/miss
                // gets no extra line.
                string skillsLine = appliedAbilities != null && appliedAbilities.Count > 0
                    ? $"\nAffected by Skill{(appliedAbilities.Count > 1 ? "s" : string.Empty)}: {string.Join(", ", appliedAbilities)}"
                    : string.Empty;
                resultSummaryText.text = $"Attacker ID: {_attacker.Name}\nTarget ID: {_defender.Name}\nHit Assessment: {hitLine}\nDamage Assessment: {damage} Damage{outcomeLine}{skillsLine}";
            }
            if (resultTargetArtImage != null)
            {
                resultTargetArtImage.sprite = _defender.Art;
                resultTargetArtImage.gameObject.SetActive(_defender.Art != null);
            }
            if (resultTargetNameText != null)
                resultTargetNameText.text = _defender.Name;
            if (resultTargetHpText != null)
                resultTargetHpText.text = $"HP: {_defender.HitPointsCurrent}/{_defender.HitPointsMax}";
            if (destroyedStamp != null)
                destroyedStamp.SetActive(died);
        }

        private void OnOkClicked()
        {
            Hide();
            if (_kind == ChallengeKind.CaptureKill)
            {
                Action<CaptureKillOutcome> callback = _onCaptureKillResolved;
                _onCaptureKillResolved = null;
                callback?.Invoke(_captureKillOutcome);
            }
            else if (_kind == ChallengeKind.Announcement)
            {
                Action callback = _onAnnouncementAcknowledged;
                _onAnnouncementAcknowledged = null;
                callback?.Invoke();
            }
            else
            {
                Action<int, bool> callback = _onResolved;
                _onResolved = null;
                callback?.Invoke(_resultDamage, _resultDied);
            }
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
