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
    // Per the user's own spec: the defender always gets the first say (Defender's Prerogative);
    // declining there hands the turn to the attacker rather than ending the duel outright — the
    // duel only actually resolves once BOTH sides have declined back-to-back (two declines in a
    // row, not just one), so either side can still react to the other's decline before it's
    // final. If NEITHER side has any Fate to spend at all, the whole duel phase is skipped and
    // the result shows immediately off the raw roll. A side with no hero, no Fate, or no miss
    // left to reroll on ITS OWN turn auto-declines after a short delay instead of forcing a human
    // to click Accept on a turn with nothing to decide (see RunHumanTurn/RunAiTurn). Every turn's
    // own reroll animation is awaited before the NEXT turn (or Resolve) begins, and Accept/Spend
    // are locked out for that whole window — see the user's own report: the AI's own reactive
    // reroll used to resolve invisibly in the same frame as the human's own Spend/Accept click,
    // so a tied roll the player could see with their own eyes still lost right in front of them.
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
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private BattleCombatantRowUI attackerRow;
        [SerializeField] private BattleCombatantRowUI defenderRow;
        [SerializeField] private Button rollButton;
        [SerializeField] private Button acceptButton;
        // Player-facing "auto-press Roll Die for me" checkbox (Autoroll_Toggle, under
        // rollStateRoot) — persisted across sessions via PlayerPrefs (see Awake/
        // OnAutorollToggleChanged), separate from AutoRollIfNoHuman's own AI-vs-AI gate below:
        // this fires even on a human's own turn once they've opted in.
        [SerializeField] private Toggle autorollToggle;
        private const string AutorollPrefKey = "BattleAttackPopup.AutorollEnabled";
        // How long the AI "thinks" before accepting a roll instead of spending Fate on it (see
        // RunAiTurn) — without this it used to resolve in the same frame the roll landed, which
        // read as the AI (most visibly the defender, exercising Defender's Prerogative on the
        // fresh first roll) accepting suspiciously fast, per the user's own report.
        [SerializeField] private float aiAcceptDelay = 0.5f;
        // How long before Roll Die auto-presses itself when neither side is human (see
        // AutoRollIfNoHuman) — otherwise an AI-vs-AI or AI-vs-neutral encounter just sits on
        // Phase.NotRolled forever, since rollButton only ever fires from an explicit click.
        [SerializeField] private float aiRollDelay = 0.5f;
        // How long a resolved result screen (Ground Combat / Capture Kill / a bare Announcement)
        // stays up before auto-acknowledging itself when neither side is human (see
        // AutoCloseResultIfNoHuman) — same reasoning as aiRollDelay above.
        [SerializeField] private float aiResultCloseDelay = 0.5f;

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
        // Guards OnOkClicked against a same-frame double-fire the same way _phase's own
        // NotRolled->Resolved transition already does for a fresh Roll/duel — but a bare
        // ShowAnnouncement (see its own comment) never leaves Phase.Resolved, so _phase alone
        // can't tell "already acknowledged" apart from "still showing" for that case. Reset
        // false by every entry point that puts up a new closeable screen (Begin, ShowAnnouncement
        // — BeginCaptureKill goes through Begin), set true the first time OnOkClicked actually
        // processes it.
        private bool _okAlreadyHandled;

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
        // OnStartRoundClicked), only get attacked. Feeds FateDuelAi.ShouldSpendFate's own Fate-
        // conservation rule for that case (see RunAiTurn).
        private bool _defenderIsRetreating;
        // GroundCombat only — terrain modifier + (Base-tagged building's own Defense), folded
        // straight into the SAME roll as any other Ground Combat attack rather than a separate
        // manual-style Siege Challenge (see BattleScreenUI.Combat.cs's BeginAttack, the only
        // caller that ever sets this to non-zero). Never applied to the attacker's own pool.
        // Set in Begin as defenderTerrainBonus + defenderConstructionBonus (kept as a single sum
        // here since roll math only cares about the total; the two components are only split out
        // for BattleCombatantRowUI's own dice-count breakdown text).
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
            if (autorollToggle != null)
            {
                autorollToggle.SetIsOnWithoutNotify(PlayerPrefs.GetInt(AutorollPrefKey, 0) != 0);
                autorollToggle.onValueChanged.AddListener(OnAutorollToggleChanged);
            }
        }

        private static void OnAutorollToggleChanged(bool isOn)
        {
            PlayerPrefs.SetInt(AutorollPrefKey, isOn ? 1 : 0);
            PlayerPrefs.Save();
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
        // Attack / defender.Defense + defenderTerrainBonus + defenderConstructionBonus) matches
        // exactly what OnRollClicked itself rolls against, just surfaced before Roll Die is even
        // clicked (see the user's own request to see each side's pool size up front, not just its
        // post-roll success count).
        public void Begin(UnitData attacker, UnitData attackerHero, UnitData defender, UnitData defenderHero,
            Sprite factionLogo, Action<int, bool> onResolved, Action<UnitData, AiThoughtCategory, string> onAiThought = null,
            bool defenderIsRetreating = false, int defenderTerrainBonus = 0, int defenderConstructionBonus = 0,
            int? attackerPoolSize = null, int? defenderPoolSize = null)
        {
            int defenderBonusDice = defenderTerrainBonus + defenderConstructionBonus;
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
            _okAlreadyHandled = false;
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
            // Reset to the Ground Combat title on every fresh Begin — BeginCaptureKill (below)
            // overrides it right after calling this, since it reuses this same shell (see the
            // user's own request: the title should read "Capture/Kill Challenge" specifically
            // for that flow, not the generic one this popup defaults to).
            if (titleText != null)
                titleText.text = "GROUND COMBAT";

            attackerRow?.Setup(attacker, attackerHero, factionLogo);
            defenderRow?.Setup(defender, defenderHero, factionLogo);
            attackerRow?.SetDicePoolSize(attackerPoolSize ?? (attacker?.Attack ?? 0));
            defenderRow?.SetDicePoolSize(defenderPoolSize ?? ((defender?.Defense ?? 0) + defenderBonusDice),
                defenderTerrainBonus, defenderConstructionBonus, defender?.Defense ?? 0);
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (rollButton != null)
                rollButton.interactable = true;
            if (acceptButton != null)
                acceptButton.interactable = false;

            if (NoHumanInvolved || IsAutorollEnabled)
                StartCoroutine(AutoRollIfNoHuman());
        }

        // The Autoroll_Toggle checkbox (see Awake/OnAutorollToggleChanged) — lets a human player
        // opt into the same auto-press-Roll-Die behavior AutoRollIfNoHuman already gives an
        // AI-vs-AI encounter (see NoHumanInvolved's own call site in Begin), so they don't have to
        // click Roll Die themselves every single Ground Combat/Capture Kill challenge.
        private bool IsAutorollEnabled => autorollToggle != null && autorollToggle.isOn;

        private static bool IsHumanSide(UnitData unit) => unit != null && unit.Owner != null && unit.Owner.IsHuman;

        // Neither current side needs to actually look at anything here before it happens — an
        // AI-vs-AI or AI-vs-neutral encounter (no human on either side), same population this
        // popup's own auto-roll/auto-close behavior targets. Reads the live _attacker/_defender
        // fields rather than taking parameters so ShowAnnouncement (no attacker/defender of its
        // own — see its own comment) can reuse the exact same check off whatever the last real
        // challenge on this popup instance set them to.
        private bool NoHumanInvolved => !IsHumanSide(_attacker) && !IsHumanSide(_defender);

        // Nobody human needs to look at this roll before it happens (see NoHumanInvolved) — an
        // AI-vs-AI or AI-vs-neutral encounter would otherwise just sit on Phase.NotRolled forever,
        // since rollButton only ever gets pressed by an explicit click. Short delay purely for
        // visual pacing, same reasoning as aiAcceptDelay.
        private IEnumerator AutoRollIfNoHuman()
        {
            if (aiRollDelay > 0f)
                yield return new WaitForSeconds(aiRollDelay);
            if (_phase == Phase.NotRolled)
                OnRollClicked();
        }

        // Same "nobody human needs to look at this" gate as AutoRollIfNoHuman, for whichever
        // result screen just went up (Ground Combat, Capture Kill, or a bare Announcement) — an
        // AI-vs-AI/AI-vs-neutral encounter would otherwise sit on Phase.Resolved forever waiting
        // for a click nobody's there to make. OnOkClicked's own _okAlreadyHandled guard makes this
        // safe even if a human elsewhere in the scene somehow also clicks Ok around the same time.
        private IEnumerator AutoCloseResultIfNoHuman()
        {
            if (aiResultCloseDelay > 0f)
                yield return new WaitForSeconds(aiResultCloseDelay);
            if (_phase == Phase.Resolved && !_okAlreadyHandled)
                OnOkClicked();
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

            // The manual: "the target hero receives a dice pool equal to his fate" — his FULL
            // Fate stat, not whatever he happens to have left after spending some earlier this
            // same battle (see the user's own correction; RunRollAndDuel below reads the same
            // FateMax for the actual roll).
            Begin(hunterFace, hunterHero, targetHero, targetHero, factionLogo, null, onAiThought,
                attackerPoolSize: _hunterDicePool, defenderPoolSize: targetHero?.FateMax ?? 0);
            _kind = ChallengeKind.CaptureKill;
            if (titleText != null)
                titleText.text = "CAPTURE/KILL CHALLENGE";
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
            // Never goes through Begin (no attacker/defender roll of its own) — the ONE other
            // place that puts up a fresh closeable screen, so it needs its own reset of the
            // OnOkClicked re-entrancy guard (see _okAlreadyHandled's own comment: _phase alone
            // can't do this job here, since it never leaves Resolved across an Announcement).
            _okAlreadyHandled = false;
            // Resolved, not NotRolled/InProgress — this IS the result screen already, there's no
            // roll to wait for; OnOkClicked's own guard expects Resolved to mean "a result is up".
            _phase = Phase.Resolved;

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

            if (NoHumanInvolved)
                StartCoroutine(AutoCloseResultIfNoHuman());
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
                // FateMax, not the current Fate — same rule as BeginCaptureKill's own
                // defenderPoolSize (this is the actual roll, that was just the pre-roll preview).
                _targetDicePoolSize = _defenderHero?.FateMax ?? 0;
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

        // Whether `hero` has any Fate left to POSSIBLY spend at all — used only to decide whether
        // the whole duel phase is worth entering in the first place (see RunDuel's own skip
        // check), not whether THIS turn specifically has anything to do (that's canSpend/
        // shouldSpend inside RunHumanTurn/RunAiTurn, which also need a miss on the dice).
        private static bool HasFateToSpend(UnitData hero) => hero != null && hero.Fate > 0;

        // The Fate duel itself. Defender's Prerogative: the defender always goes first. Declining
        // (Accept, or an AI/no-Fate side auto-declining — see RunHumanTurn/RunAiTurn) hands the
        // turn to the OTHER side rather than ending the duel — that side still gets its own look
        // at the roll and a chance to spend, since the dice it would be reacting to just changed.
        // Tracked per side (defenderDone/attackerDone) rather than a single "two declines in a
        // row" counter: a side is done the instant ITS OWN turn ends (RunHumanTurn/RunAiTurn
        // always end in a decline, whether chosen or forced by having nothing left to spend), and
        // is only reopened when the OTHER side's turn actually spent Fate — declining changes
        // nothing, so it must never reopen anyone. The duel ends once both sides are done. Per
        // the user's own report: with a plain "two declines in a row" counter, a defender with no
        // hero (permanently nothing to decide, e.g. an army without a hero) still counted as a
        // fresh decline every time it was asked, so its second forced auto-decline — after the
        // attacker had already spent, reconsidered, and explicitly declined — wrongly earned the
        // attacker one more redundant round against dice that hadn't changed at all.
        private IEnumerator RunDuel()
        {
            if (!HasFateToSpend(_defenderHero) && !HasFateToSpend(_attackerHero))
            {
                // Neither side has any Fate left to spend, so there's no Spend-or-Accept turn for
                // anyone to take — but resolving with zero pause right after the dice land reads as
                // the roll being skipped entirely. Same beat as RunHumanTurn/RunAiTurn's own
                // auto-decline (aiAcceptDelay) so this case doesn't feel instant/broken.
                if (aiAcceptDelay > 0f)
                    yield return new WaitForSeconds(aiAcceptDelay);
                yield break;
            }

            bool defenderDone = false;
            bool attackerDone = false;
            bool isDefenderTurn = true;
            while (!defenderDone || !attackerDone)
            {
                if (isDefenderTurn ? defenderDone : attackerDone)
                {
                    isDefenderTurn = !isDefenderTurn;
                    continue;
                }

                yield return RunTurn(isDefenderTurn);
                if (isDefenderTurn)
                    defenderDone = true;
                else
                    attackerDone = true;
                if (_turnSpent)
                {
                    if (isDefenderTurn)
                        attackerDone = false;
                    else
                        defenderDone = false;
                }
                isDefenderTurn = !isDefenderTurn;
            }
        }

        // One side's whole decision window, which can include SEVERAL Spends before it concludes
        // (see the user's own example: defender Spend → Accept; attacker Spend, Spend-again,
        // *then* effectively done) — a single Spend does NOT hand the turn to the other side by
        // itself anymore; only Accept (human) or the AI running out of reasons to keep going does.
        // AI evaluates and acts on its own (see RunAiTurn); a human side with nothing to Spend on
        // auto-declines instead of forcing an empty click (see RunHumanTurn's own canSpend gate).
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

                // Nothing this side could possibly do this turn (no hero, no Fate, or no miss
                // left to reroll) — auto-decline after a short beat instead of forcing a click on
                // a decision that isn't actually one (mirrors RunAiTurn's own aiAcceptDelay pause).
                if (!canSpend)
                {
                    if (aiAcceptDelay > 0f)
                        yield return new WaitForSeconds(aiAcceptDelay);
                    break;
                }

                _defenderTurnActive = isDefenderTurn;
                _humanSpent = false;
                _humanDeclined = false;
                _awaitingHumanDecision = true;
                if (isDefenderTurn)
                    defenderRow?.SetSpendInteractable(true);
                else
                    attackerRow?.SetSpendInteractable(true);
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
        // more than once), just decided via FateDuelAi.ShouldSpendFate instead of a click. Per the
        // user's own correction: the AI isn't "obligated" to keep rerolling — it evaluates the
        // CURRENT dice fresh before EVERY reroll (same as a human deciding again after each one)
        // and stops the instant ShouldSpendFate says it's no longer worth it, or the instant a
        // reroll it just made comes back a miss again (see the stop-on-failed-reroll check below);
        // getting a turn at all only happens because the other side just spent, per RunDuel.
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
                bool shouldSpend = hero != null && hero.Fate > 0 && HasMiss(ownDice) && FateDuelAi.ShouldSpendFate(
                    _attackerDice, _defenderDice, hero.Fate, isDefenderTurn,
                    _attacker, _defender, Magnitudes,
                    isRetreating, defendingUnitHp, _kind == ChallengeKind.CaptureKill);
                if (!shouldSpend)
                {
                    if (aiAcceptDelay > 0f)
                        yield return new WaitForSeconds(aiAcceptDelay);
                    break;
                }

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

                // Universal stop-on-failed-reroll (per the user's own spec): this specific reroll
                // just came back a miss again — this side is done trying Fate for the rest of
                // THIS duel turn, even with Fate still left and even if ShouldSpendFate would say
                // to keep going against the fresh dice; re-evaluating right after proving unlucky
                // isn't what "keep trying" means here. Checked on the slot RerollOneMiss actually
                // touched (rerolledIndex), not the whole array, since other slots may still hold
                // pre-existing (already-resolved) misses.
                bool[] postRerollDice = isDefenderTurn ? _defenderDice : _attackerDice;
                if (!postRerollDice[rerolledIndex])
                    break;
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
            // UnitAbilities.CriticalDamage/Hyperkinetic/Pyrokinetic/CeramicArmor — see
            // ChallengeResult.ApplyAbilityModifiers for the fixed order (x2 multiplier, then the
            // Hyperkinetic/Pyrokinetic bonuses, then CeramicArmor's flat reduction last so it
            // always comes off the already-boosted total) — shared with FateDuelAi/
            // BattleTargetSelector so the AI's own predictions always match the damage actually
            // dealt here.
            // wasHit reads the RAW dice roll, not the ability-adjusted damage below — a hit that
            // CeramicArmor reduces all the way to 0 is still a hit, not a miss (see ShowResult).
            bool wasHit = result.Damage > 0;
            int damage = ChallengeResult.ApplyAbilityModifiers(result.Damage, _attacker, _defender,
                Magnitudes, out List<string> appliedAbilities);

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

        // Public so BattleScreenUI.ConsiderAiRetreat can feed the same tunable magnitudes into
        // BattleAi.AssessRetreat's damage projection — one UnitAbilityCatalog reference for the
        // whole battle screen instead of wiring a second copy onto BattleScreenUI itself.
        public float CriticalDamageMultiplier => abilityCatalog != null ? abilityCatalog.criticalDamageMultiplier : 2f;
        public int CeramicArmorReduction => abilityCatalog != null ? abilityCatalog.ceramicArmorReduction : 1;
        private int BerserkAttackGain => abilityCatalog != null ? abilityCatalog.berserkAttackGain : 1;
        private int BerserkDefenseLoss => abilityCatalog != null ? abilityCatalog.berserkDefenseLoss : 1;
        public int HyperkineticBonusDamage => abilityCatalog != null ? abilityCatalog.hyperkineticBonusDamage : 2;
        public int PyrokineticBonusDamage => abilityCatalog != null ? abilityCatalog.pyrokineticBonusDamage : 2;

        // Bundles the four properties above — see AbilityMagnitudes' own comment for why this
        // exists; every call site that used to read Critical/Hyperkinetic/Ceramic(/Pyrokinetic)
        // individually now just reads this once.
        public AbilityMagnitudes Magnitudes => new AbilityMagnitudes(
            CriticalDamageMultiplier, HyperkineticBonusDamage, CeramicArmorReduction, PyrokineticBonusDamage);

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
                Sprite attackerArt = _attacker != null
                    ? (_attacker.DetailArt != null ? _attacker.DetailArt : _attacker.Art)
                    : null;
                resultArtImage.sprite = attackerArt;
                resultArtImage.gameObject.SetActive(attackerArt != null);
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

            if (NoHumanInvolved)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        private void ShowResult(int damage, bool died, bool wasHit, List<string> appliedAbilities)
        {
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
            {
                Sprite attackerArt = _attacker.DetailArt != null ? _attacker.DetailArt : _attacker.Art;
                resultArtImage.sprite = attackerArt;
                resultArtImage.gameObject.SetActive(attackerArt != null);
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

            if (NoHumanInvolved)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        private void OnOkClicked()
        {
            // Guards against a same-frame double-fire: Space (see Update's own WasSpacePressed
            // check) can land on the SAME frame the EventSystem's own Submit action already
            // routes to okButton.onClick because okButton is the currently-selected UI element —
            // both then call this. A CaptureKill chain (see BattleScreenUI.Combat.cs's
            // RunNextCaptureKillChallenge) reopens this exact popup for the NEXT hero synchronously
            // inside the first call's own callback, so without this guard the stray second call
            // used to fire against that freshly-opened NEXT challenge instead of a no-op — Hide()
            // closing it early and _captureKillOutcome/_resultDamage (still holding the PREVIOUS
            // challenge's stale values, since the next one hasn't rolled yet) resolving it on the
            // spot, corrupting the next result in the chain (see the user's own report: the
            // Capture/Kill Challenge result popup's second message duplicated/broken). Every
            // Resolve* site sets _phase = Resolved right before showing this result; the reopen
            // above always resets it back to NotRolled via Begin, so a stray second call usually
            // finds the wrong phase and bails here instead — EXCEPT a plain ShowAnnouncement
            // (e.g. "The enemy retreats." after a hero escapes its Capture Kill Challenge), which
            // never leaves Phase.Resolved (see its own comment), so that stray second call used
            // to sail straight through this check and re-fire the announcement's own callback a
            // second time, showing the same line again (the user's own report of a "parasitic"
            // duplicate message). _okAlreadyHandled catches that case too, since it's reset fresh
            // by every entry point that shows something new, not just the ones that also change
            // _phase.
            if (_phase != Phase.Resolved || _okAlreadyHandled)
                return;
            _okAlreadyHandled = true;
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
