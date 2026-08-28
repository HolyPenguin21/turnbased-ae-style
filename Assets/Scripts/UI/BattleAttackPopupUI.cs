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
    // to click Accept on a turn with nothing to decide (see RunHumanTurn/RunAiTurn). Every roll's
    // outcome (success counts, who gets to Spend/Accept next) is only shown/opened once that
    // roll's own flip animation has actually landed — the initial roll via RunRollAndDuel's own
    // wait, and every later Fate-spend reroll inside the duel via RunHumanTurn/RunAiTurn's own
    // wait on _rerollAnimDone (both per the user's own request, 2026-08-24).
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
        // AutoCloseResultIfNoHuman) — same reasoning as aiRollDelay above. Also doubles as the delay for
        // autoCloseResultToggle's own human-opted-in case (see IsAutoCloseResultEnabled) — a
        // human who turned that on still wants a glance at the result, not zero.
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
        // Player-facing "auto-press Ok for me" checkbox (Autoroll_Toggle, under
        // Challenge_Popup > ChallengeResultRoot) — same principle as rollStateRoot's own
        // autorollToggle (persisted via PlayerPrefs, opts a human into the same auto-advance
        // AutoCloseResultIfNoHuman already gives an AI-vs-AI encounter), just for the Result
        // state instead of the Roll state: this dismisses a resolved Ground Combat/Capture Kill/
        // Announcement screen automatically instead of making the player click Ok every time.
        [SerializeField] private Toggle autoCloseResultToggle;
        private const string AutoCloseResultPrefKey = "BattleAttackPopup.AutoCloseResultEnabled";

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
        // Whatever the turn that just ran (RunTurn) actually did — read by RunDuel right after
        // `yield return RunTurn(...)` returns, since a coroutine can't hand back an ordinary
        // return value.
        private bool _turnSpent;
        // Set false right before a Fate-spend reroll's SetDice call, true once that die's own
        // flip animation lands (or immediately if the row is missing — nothing to wait for).
        // Both RunHumanTurn and RunAiTurn wait on this before re-opening Spend/Accept (human) or
        // re-evaluating whether to keep spending (AI) — every reroll is gated the same way the
        // very first roll is (see RunRollAndDuel), not just that first one (per the user's own
        // follow-up request, 2026-08-24: there had been confusing-looking cases from Spend
        // re-opening while the just-rerolled die was still visibly mid-flip).
        private bool _rerollAnimDone;

        // Which win condition Resolve() applies once both sides have accepted — every Challenge
        // in the manual (Ground Combat, Capture Kill, and eventually Retreat/Assassination/
        // Sabotage/Sniper/...) shares this same Roll/Defender's-Prerogative/Accept shell, they
        // just differ in how dice-pool sizes are computed and what the result means. See
        // BeginCaptureKill for the second one; add further Begin*/Resolve* pairs here rather than
        // a whole new popup component per challenge type.
        private enum ChallengeKind { GroundCombat, CaptureKill, Announcement, ResearchProduction }
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
        // GroundCombat only — the ACTUAL dice-pool sizes the roll uses, resolved once in Begin
        // (attackerPoolSize/defenderPoolSize ?? the plain Attack/Defense+bonus default) and read
        // back by RunRollAndDuel instead of recomputing from _attacker.Attack/_defender.Defense
        // directly. Must stay in lockstep with what attackerRow/defenderRow.SetDicePoolSize was
        // just told to DISPLAY in Begin — see the bug this fixed: a hero defender's displayed
        // pool is its FateMax (defenderPoolSize, see BeginAttack's own "target hero" comment),
        // but the roll used to always fall back to _defender.Defense regardless, which is 0 for
        // every hero card (attack/defenseRating are both 0 in the hero cards' own data — heroes
        // fight via Fate, not a Defense stat). Rolling 0 dice for the defender meant no dice
        // slots at all for that side and a duel forced to whatever the attacker alone rolled,
        // even though the row up top still correctly showed "Dice: {FateMax}" (the project
        // owner's own report: "у героя не крутятся кубики... количество кубиков написано, но
        // визуально их нет").
        private int _attackerDicePoolSize;
        private int _defenderDicePoolSize;
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

        // ---- Research/Production Challenge state (see BeginResearchProduction) — ENTIRELY
        // separate from the Ground Combat / Capture Kill duel plumbing above. Only ever touched
        // on the ResearchProduction code path; the shared Begin/RunDuel/Resolve never read these.
        // The defender is a CardDefinition, never a fake UnitData (per the spec). ----
        private UnitData _rpHero;
        private CardDefinition _rpCard;
        private int _rpRequiredSuccesses;
        private bool[] _rpDice;
        // hero.Fate as it was the instant BeginResearchProduction ran — restored verbatim once
        // the Challenge ends, win or lose, so a Research/Production attempt never permanently
        // costs Fate (spec §3/§15). NOT hero.FateMax — a hero that walked in on 2/4 leaves on
        // 2/4, and battle Fate replenishment (ReplenishFateForNewBattle) is never called here.
        private int _rpFateSnapshot;
        private bool _rpActive;
        private bool _rpResultSuccess;
        private Action<bool> _onResearchProductionResolved;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Lets GameTurnController fold this popup into InputBlocked the same way every other
        // map-level popup already is — needed now that Game.Aviation.AviationCombatPresenter can
        // open this popup directly for an AA reaction/air strike, outside of BattleScreenUI
        // entirely (which already covers its own nested Begin calls via its own VisibilityChanged).
        public event Action VisibilityChanged;

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
            if (autoCloseResultToggle != null)
            {
                autoCloseResultToggle.SetIsOnWithoutNotify(PlayerPrefs.GetInt(AutoCloseResultPrefKey, 0) != 0);
                autoCloseResultToggle.onValueChanged.AddListener(OnAutoCloseResultToggleChanged);
            }
        }

        private static void OnAutorollToggleChanged(bool isOn)
        {
            PlayerPrefs.SetInt(AutorollPrefKey, isOn ? 1 : 0);
            PlayerPrefs.Save();
        }

        private static void OnAutoCloseResultToggleChanged(bool isOn)
        {
            PlayerPrefs.SetInt(AutoCloseResultPrefKey, isOn ? 1 : 0);
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
            Sprite attackerLogo, Sprite defenderLogo, Action<int, bool> onResolved, Action<UnitData, AiThoughtCategory, string> onAiThought = null,
            bool defenderIsRetreating = false, int defenderTerrainBonus = 0, int defenderConstructionBonus = 0,
            int? attackerPoolSize = null, int? defenderPoolSize = null)
        {
            int defenderBonusDice = defenderTerrainBonus + defenderConstructionBonus;
            // Stops a still-running RunRollAndDuel/RunDuel coroutine from a PREVIOUS Begin() on
            // this same (pooled/reused) popup instance — otherwise its delayed WaitUntil callbacks
            // could fire against the fields this call is about to overwrite.
            StopAllCoroutines();
            // If a Research/Production Challenge was somehow torn down without resolving, put its
            // hero's Fate back before this popup is reused for anything else (spec §15).
            CleanupResearchProduction();

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

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
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

            _attackerDicePoolSize = attackerPoolSize ?? (attacker?.Attack ?? 0);
            _defenderDicePoolSize = defenderPoolSize ?? ((defender?.Defense ?? 0) + defenderBonusDice);

            attackerRow?.Setup(attacker, attackerHero, attackerLogo);
            defenderRow?.Setup(defender, defenderHero, defenderLogo);
            attackerRow?.SetDicePoolSize(_attackerDicePoolSize);
            defenderRow?.SetDicePoolSize(_defenderDicePoolSize,
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

        // The ChallengeResultRoot checkbox's own read — same principle as IsAutorollEnabled
        // above, just gating AutoCloseResultIfNoHuman's three call sites (ShowResult/
        // ShowCaptureKillResult/ShowAnnouncement) instead of Begin's Roll-Die auto-press.
        private bool IsAutoCloseResultEnabled => autoCloseResultToggle != null && autoCloseResultToggle.isOn;

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
            // No pacing beat at all when neither side is human (see NoHumanInvolved) — only the
            // IsAutorollEnabled human-opted-in case still waits aiRollDelay, per the user's own
            // request (2026-08-24) to stop stalling a purely AI/neutral fight just to be readable
            // to a spectator; aiRollDelay itself is untouched for that still-human case.
            if (!NoHumanInvolved && aiRollDelay > 0f)
                yield return new WaitForSeconds(aiRollDelay);
            if (_phase == Phase.NotRolled)
                OnRollClicked();
        }

        // Same "nobody human needs to look at this" gate as AutoRollIfNoHuman, for whichever
        // result screen just went up (Ground Combat, Capture Kill, or a bare Announcement) — an
        // AI-vs-AI/AI-vs-neutral encounter would otherwise sit on Phase.Resolved forever waiting
        // for a click nobody's there to make. Also started when IsAutoCloseResultEnabled (see its
        // own comment) even with a human on both sides — same opt-in idea as IsAutorollEnabled,
        // just for dismissing the result instead of pressing Roll Die. OnOkClicked's own
        // _okAlreadyHandled guard makes this safe even if a human elsewhere in the scene somehow
        // also clicks Ok around the same time.
        private IEnumerator AutoCloseResultIfNoHuman()
        {
            // Same NoHumanInvolved carve-out as AutoRollIfNoHuman above (2026-08-24) — only the
            // IsAutoCloseResultEnabled human-opted-in case still gets the readable aiResultCloseDelay
            // pause; a battle with nobody human in it at all resolves instantly instead.
            if (!NoHumanInvolved && aiResultCloseDelay > 0f)
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
        public void BeginCaptureKill(ArmyData hunterArmy, UnitData targetHero, Sprite hunterLogo, Sprite targetLogo,
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
            Begin(hunterFace, hunterHero, targetHero, targetHero, hunterLogo, targetLogo, null, onAiThought,
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
            CleanupResearchProduction();
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
            VisibilityChanged?.Invoke();
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

            if (NoHumanInvolved || IsAutoCloseResultEnabled)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        // ============================ Research / Production Challenge ============================
        // A new, separate entry point (per the spec): it does NOT go through Begin(), never runs
        // RunRollAndDuel/RunDuel/Resolve, and never touches Ground Combat / Capture Kill / Aviation
        // rules. Attacker = the producing Hero, rolling a fixed pool of 5 (see ChallengeResolver.
        // RollDice) with unchanged per-die success probability. Defender = the chosen card: a
        // fixed CardDefinition.fate guaranteed successes, no dice, no Fate. Win = attacker
        // successes >= card.fate. The Fate the Hero spends here is temporary — restored to its
        // pre-Challenge value on the way out (RestoreResearchProductionFate).
        //
        // The caller (HexSelectionController) has already: revalidated eligibility, checked hand
        // capacity, and PAID the card's ResourceCost. This method only runs the roll and reports
        // success/failure through onResolved.
        public void BeginResearchProduction(UnitData hero, CardDefinition card, ResearchProductionMode mode,
            Sprite heroLogo, Sprite cardLogo, Action<bool> onResolved)
        {
            StopAllCoroutines();
            // A prior R/P state that never resolved (should not happen — the modal blocks input
            // for the whole Challenge — but be safe, per spec §15).
            CleanupResearchProduction();

            _kind = ChallengeKind.ResearchProduction;
            _rpHero = hero;
            _rpCard = card;
            _rpRequiredSuccesses = card != null ? Mathf.Max(0, card.fate) : 0;
            _rpFateSnapshot = hero != null ? hero.Fate : 0;
            _rpActive = true;
            _rpResultSuccess = false;
            _rpDice = null;
            _onResearchProductionResolved = onResolved;

            // The Hero is a real UnitData and a legitimate attacker (only the DEFENDER must not
            // be a fake unit). Setting _attacker lets NoHumanInvolved / the AI-thought hooks read
            // correctly; _defender stays null (there is no defender unit).
            _attacker = hero;
            _defender = null;
            _attackerHero = hero;
            _defenderHero = null;
            _attackerDice = null;
            _defenderDice = null;
            _phase = Phase.InProgress;
            _okAlreadyHandled = false;
            _awaitingHumanDecision = false;
            _humanSpent = false;
            _humanDeclined = false;

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
            if (rollStateRoot != null)
                rollStateRoot.SetActive(true);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(false);
            if (titleText != null)
                titleText.text = mode == ResearchProductionMode.Research
                    ? "RESEARCH CHALLENGE" : "PRODUCTION CHALLENGE";

            attackerRow?.Setup(hero, hero, heroLogo);
            attackerRow?.SetDicePoolSize(5);
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetupCardDefender(card != null ? card.displayName : string.Empty, cardLogo);

            if (rollButton != null)
                rollButton.interactable = false;
            if (acceptButton != null)
                acceptButton.interactable = false;

            StartCoroutine(RunResearchProductionChallenge());
        }

        // Defender shows its fixed successes up front; attacker rolls 5; then the attacker (only)
        // gets a Spend-or-Accept loop. Spend, per spec §2: if a miss exists → reroll it (existing
        // RerollOneMiss, unchanged); otherwise, if still short of target → append and roll ONE new
        // die (overflow). Loop ends on Accept, on Fate == 0, or when there's no miss AND the
        // target is already met.
        private IEnumerator RunResearchProductionChallenge()
        {
            defenderRow?.SetFixedSuccesses(_rpRequiredSuccesses);

            _rpDice = ChallengeResolver.RollDice(5);
            bool animDone = attackerRow == null;
            attackerRow?.SetDice(_rpDice, onComplete: () => animDone = true);
            yield return new WaitUntil(() => animDone);

            BattleDebugLog.Write($"[RollDiag] ResearchProduction: {_rpHero?.Name} -> {_rpCard?.displayName} " +
                $"target={_rpRequiredSuccesses}, roll={BattleDebugLog.DiceString(_rpDice)} ({CountHits(_rpDice)} hits)");

            while (true)
            {
                int successes = CountHits(_rpDice);
                bool canSpend = _rpHero != null && _rpHero.Fate > 0
                    && (HasMiss(_rpDice) || successes < _rpRequiredSuccesses);
                if (!canSpend)
                    break;

                _awaitingHumanDecision = true;
                _defenderTurnActive = false;
                _humanSpent = false;
                _humanDeclined = false;
                attackerRow?.SetSpendInteractable(true);
                if (acceptButton != null)
                    acceptButton.interactable = true;

                yield return new WaitUntil(() => _humanSpent || _humanDeclined);

                _awaitingHumanDecision = false;
                attackerRow?.SetSpendInteractable(false);
                if (acceptButton != null)
                    acceptButton.interactable = false;

                if (_humanDeclined)
                    break;

                // OnResearchProductionSpend already mutated _rpDice + Fate and kicked the anim —
                // wait for it to land before re-offering Spend/Accept (same gate as the duel).
                yield return new WaitUntil(() => _rerollAnimDone);
            }

            ResolveResearchProduction();
        }

        // The attacker's Spend for a Research/Production Challenge — reached from OnAttackerSpend,
        // which forks here on _kind. Miss present → single reroll (RerollOneMiss, shared/unchanged).
        // No miss but still short of target → overflow: append + roll one new die.
        private void OnResearchProductionSpend()
        {
            if (!_awaitingHumanDecision || _rpHero == null || _rpHero.Fate <= 0)
                return;

            bool hasMiss = HasMiss(_rpDice);
            int successes = CountHits(_rpDice);
            if (!hasMiss && successes >= _rpRequiredSuccesses)
                return; // nothing Fate can do

            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
            _rpHero.Fate--;
            _rerollAnimDone = attackerRow == null;

            if (hasMiss)
            {
                RerollOneMiss(ref _rpDice, out int rerolledIndex);
                attackerRow?.SetDice(_rpDice, rerolledIndex, () => _rerollAnimDone = true);
                attackerRow?.OnFateSpent();
                BattleDebugLog.Write($"[RPChallenge] {_rpHero.Name} spent Fate (remaining={_rpHero.Fate}), " +
                    $"rerolled slot {rerolledIndex} -> {_rpDice[rerolledIndex]}");
            }
            else
            {
                bool hit = ChallengeResolver.RollDice(1)[0];
                var grown = new bool[_rpDice.Length + 1];
                System.Array.Copy(_rpDice, grown, _rpDice.Length);
                grown[grown.Length - 1] = hit;
                _rpDice = grown;
                attackerRow?.SetDicePoolSize(_rpDice.Length);
                attackerRow?.AppendDie(hit, () => _rerollAnimDone = true);
                attackerRow?.OnFateSpent();
                BattleDebugLog.Write($"[RPChallenge] {_rpHero.Name} spent Fate (remaining={_rpHero.Fate}), " +
                    $"added overflow die #{_rpDice.Length} -> {hit}");
            }
            _humanSpent = true;
        }

        private void ResolveResearchProduction()
        {
            _phase = Phase.Resolved;
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;

            int successes = CountHits(_rpDice);
            _rpResultSuccess = successes >= _rpRequiredSuccesses;

            // Central, unconditional Fate restore — success or failure, before the result screen
            // or any callback (spec §3/§15).
            RestoreResearchProductionFate();

            BattleDebugLog.Write($"[ResolveDiag] ResearchProduction {_rpHero?.Name} -> {_rpCard?.displayName}: " +
                $"{successes} successes vs required {_rpRequiredSuccesses} -> {(_rpResultSuccess ? "SUCCESS" : "FAILURE")}; " +
                $"hero Fate restored to {_rpFateSnapshot}");

            ShowResearchProductionResult(_rpResultSuccess, successes);
        }

        private void ShowResearchProductionResult(bool success, int successes)
        {
            if (rollStateRoot != null)
                rollStateRoot.SetActive(false);
            if (resultStateRoot != null)
                resultStateRoot.SetActive(true);

            if (resultArtImage != null)
            {
                Sprite art = _rpHero != null
                    ? (_rpHero.DetailArt != null ? _rpHero.DetailArt : _rpHero.Art)
                    : null;
                resultArtImage.sprite = art;
                resultArtImage.gameObject.SetActive(art != null);
            }
            if (resultSummaryText != null)
            {
                string verdict = success
                    ? $"{_rpHero?.Name} completes {_rpCard?.displayName}."
                    : $"{_rpHero?.Name} fails to complete {_rpCard?.displayName}.";
                resultSummaryText.text = $"{verdict}\nSuccesses: {successes} / {_rpRequiredSuccesses}";
            }
            if (resultTargetArtImage != null)
            {
                resultTargetArtImage.sprite = _rpCard != null ? _rpCard.art : null;
                resultTargetArtImage.gameObject.SetActive(_rpCard != null && _rpCard.art != null);
            }
            if (resultTargetNameText != null)
                resultTargetNameText.text = _rpCard != null ? _rpCard.displayName : string.Empty;
            if (resultTargetHpText != null)
                resultTargetHpText.text = string.Empty;
            if (destroyedStamp != null)
                destroyedStamp.SetActive(false);

            if (NoHumanInvolved || IsAutoCloseResultEnabled)
                StartCoroutine(AutoCloseResultIfNoHuman());
        }

        // R/P ONLY. Puts the Hero's Fate back to its pre-Challenge value. Idempotent (the
        // snapshot isn't cleared, but re-assigning the same value is harmless) and gated so it
        // can NEVER run for a Ground Combat / Capture Kill / Aviation resolve (spec §15/§33).
        private void RestoreResearchProductionFate()
        {
            if (_kind != ChallengeKind.ResearchProduction && !_rpActive)
                return;
            if (_rpHero != null)
                _rpHero.Fate = _rpFateSnapshot;
        }

        // THE single teardown/finalization path for a Research/Production Challenge — used by
        // BOTH the normal Result -> OK flow (OnOkClicked) and every abnormal exit: a forced
        // Hide() from elsewhere, this popup being grabbed for another Challenge (Begin /
        // BeginCaptureKill / ShowAnnouncement / a fresh BeginResearchProduction), any other
        // teardown of an active R/P state. Idempotent: the pending callback is captured-then-
        // nulled and _rpActive cleared on the first call, so any later call (e.g. Hide()
        // running right after OnOkClicked already finalized) is a no-op — the R/P callback can
        // never fire twice.
        //
        //   - stops the running R/P coroutine (roll / reroll waits),
        //   - restores the Hero's Fate to its pre-Challenge snapshot (spec §3/§15) — always,
        //     success or failure,
        //   - clears this popup's R/P state,
        //   - invokes the R/P callback exactly once with `success`.
        //
        // Does NOT refund the card's ResourceCost — that was paid by HexSelectionController
        // before the Challenge and is deliberately never returned (spec §4). An interrupted
        // Challenge is finalized with success == false, so the caller
        // (HexSelectionController.OnResearchProductionResolved) mints no card and unwinds its
        // own _rpTransactionActive / _rpPendingCard and the modal's Busy state.
        private void FinalizeResearchProduction(bool success)
        {
            if (!_rpActive && _onResearchProductionResolved == null)
                return;

            if (_kind == ChallengeKind.ResearchProduction)
                StopAllCoroutines();

            RestoreResearchProductionFate();

            Action<bool> callback = _onResearchProductionResolved;
            _onResearchProductionResolved = null;
            _rpActive = false;
            _rpResultSuccess = success;
            _awaitingHumanDecision = false;
            _rpHero = null;
            _rpCard = null;
            _rpDice = null;

            callback?.Invoke(success);
        }

        // Popup-reuse call sites (Begin / BeginCaptureKill via Begin / ShowAnnouncement /
        // BeginResearchProduction): a reused popup abandons any in-flight R/P Challenge as a
        // failure — no card minted, Fate restored, callback fired exactly once.
        private void CleanupResearchProduction() => FinalizeResearchProduction(false);

        private static int CountHits(bool[] dice)
        {
            if (dice == null)
                return 0;
            int hits = 0;
            foreach (bool hit in dice)
                if (hit) hits++;
            return hits;
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

        // Rolls, reveals both rows, then runs the Fate duel (see RunDuel) and resolves once it's
        // actually over — waits for both rows' own flip animation to actually land before opening
        // the duel (per the user's own request, 2026-08-24: Spend/Accept shouldn't become
        // available, and an AI shouldn't react, before the dice are visibly done spinning).
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
                // _attackerDicePoolSize/_defenderDicePoolSize (set in Begin), NOT
                // _attacker.Attack/_defender.Defense directly — a targeted hero's pool is its
                // FateMax (see BeginAttack's defenderPoolSize), which must roll exactly as many
                // dice as the row above already displayed.
                result = ChallengeResolver.Resolve(_attackerDicePoolSize, _defenderDicePoolSize);
            }
            _attackerDice = result.AttackerDice;
            _defenderDice = result.DefenderDice;

            BattleDebugLog.Write($"[RollDiag] {_kind}: {_attacker?.Name} ({_attacker?.Owner?.Nickname}) " +
                $"vs {_defender?.Name} ({_defender?.Owner?.Nickname}) -> " +
                $"attacker={BattleDebugLog.DiceString(_attackerDice)} ({result.AttackerSuccesses} hits), " +
                $"defender={BattleDebugLog.DiceString(_defenderDice)} ({result.DefenderSuccesses} hits), rawDamage={result.Damage}");

            // Wait for the initial roll's own flip animation to actually land before anything
            // downstream happens — the success counts, the Fate duel, and Spend/Accept becoming
            // interactable all used to fire the instant the dice were rolled, which let a human
            // click Spend (or an AI decide) while the strip was still visibly mid-flip (per the
            // user's own request, 2026-08-24). Every later Fate-spend reroll inside the duel gets
            // the same treatment via RunHumanTurn/RunAiTurn's own wait on _rerollAnimDone.
            bool attackerAnimDone = attackerRow == null;
            bool defenderAnimDone = defenderRow == null;
            attackerRow?.SetDice(_attackerDice, onComplete: () => attackerAnimDone = true);
            defenderRow?.SetDice(_defenderDice, onComplete: () => defenderAnimDone = true);
            // Nobody human is watching this roll land (see NoHumanInvolved) — don't gate the duel
            // on the flip animation actually finishing, per the user's own request (2026-08-24) to
            // stop pacing an AI-vs-AI/AI-vs-neutral fight for a spectator's benefit. SetDice already
            // ran above so _attackerDice/_defenderDice are the real values either way.
            if (!NoHumanInvolved)
                yield return new WaitUntil(() => attackerAnimDone && defenderAnimDone);

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
                // auto-decline (aiAcceptDelay) so this case doesn't feel instant/broken — except
                // when nobody human is in this fight at all (2026-08-24), where instant IS the goal.
                if (!NoHumanInvolved && aiAcceptDelay > 0f)
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

                // Diagnostics for the project owner's own report (Ground Combat, 2026-08-26):
                // the duel sometimes ends before the defender gets to react to/make a reroll.
                // Nothing in RunDuel/RunHumanTurn's own structure looked wrong on inspection —
                // this side always opens Spend/Accept first per Defender's Prerogative — so this
                // logs the exact moment the window opens (and OnAcceptClicked logs the moment it
                // closes) rather than guessing at a fix blind; compare timestamps/sequence next
                // time this reproduces.
                BattleDebugLog.Write($"[FateDuelDiag] Spend/Accept OPEN for {(isDefenderTurn ? "defender" : "attacker")} " +
                    $"{hero?.Name} (fate={hero?.Fate}, dice={BattleDebugLog.DiceString(ownDice)})");

                yield return new WaitUntil(() => _humanSpent || _humanDeclined);

                _awaitingHumanDecision = false;
                defenderRow?.SetSpendInteractable(false);
                attackerRow?.SetSpendInteractable(false);
                if (acceptButton != null)
                    acceptButton.interactable = false;

                if (_humanDeclined)
                    break;

                // Wait for the reroll's own flip animation to land before offering Spend-or-
                // Accept again — otherwise the next Spend became available while the die just
                // spent on was still visibly mid-flip (see _rerollAnimDone's own comment).
                yield return new WaitUntil(() => _rerollAnimDone);

                spentThisTurn = true;
                // Same side, same turn, offered Spend-or-Accept again against the now-landed dice.
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
                BattleDebugLog.Write($"[FateDuelDiag] {(isDefenderTurn ? "defender" : "attacker")} " +
                    $"{(isDefenderTurn ? _defender?.Name : _attacker?.Name)} (hero {hero?.Name}, fate={hero?.Fate ?? 0}, " +
                    $"isRetreating={isRetreating}, defendingUnitHp={defendingUnitHp}): " +
                    $"attacker={BattleDebugLog.DiceString(_attackerDice)} defender={BattleDebugLog.DiceString(_defenderDice)} " +
                    $"-> shouldSpend={shouldSpend}");
                if (!shouldSpend)
                {
                    // Skipped entirely for a battle with no human in it at all (2026-08-24) — same
                    // NoHumanInvolved carve-out as RunDuel's own no-Fate-to-spend beat above.
                    if (!NoHumanInvolved && aiAcceptDelay > 0f)
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
                BattleDebugLog.Write($"[FateDuelDiag] {(isDefenderTurn ? "defender" : "attacker")} {hero.Name} spent Fate " +
                    $"(remaining={hero.Fate}), rerolled slot {rerolledIndex} -> " +
                    $"{(isDefenderTurn ? _defenderDice : _attackerDice)[rerolledIndex]}");

                _rerollAnimDone = isDefenderTurn ? defenderRow == null : attackerRow == null;
                if (isDefenderTurn)
                {
                    defenderRow?.SetDice(_defenderDice, rerolledIndex, () => _rerollAnimDone = true);
                    defenderRow?.OnFateSpent();
                }
                else
                {
                    attackerRow?.SetDice(_attackerDice, rerolledIndex, () => _rerollAnimDone = true);
                    attackerRow?.OnFateSpent();
                }
                // Wait for this reroll's own flip animation to land before deciding whether to
                // keep going — matches RunHumanTurn's own reroll gate (see _rerollAnimDone's own
                // comment): the AI shouldn't react to a die that isn't visibly done spinning yet.
                // Skipped when nobody human is in this fight (2026-08-24) — same reasoning as
                // RunRollAndDuel's own initial-roll anim gate.
                if (!NoHumanInvolved)
                    yield return new WaitUntil(() => _rerollAnimDone);

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
            // See RunHumanTurn's own OPEN log — the matching close, for the same diagnostics.
            BattleDebugLog.Write($"[FateDuelDiag] Accept clicked for {(_defenderTurnActive ? "defender" : "attacker")} " +
                $"{(_defenderTurnActive ? _defenderHero?.Name : _attackerHero?.Name)}");
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
            _rerollAnimDone = defenderRow == null;
            defenderRow?.SetDice(_defenderDice, rerolledIndex, () => _rerollAnimDone = true);
            defenderRow?.OnFateSpent();
            _humanSpent = true;
            BattleDebugLog.Write($"[FateDuelDiag] defender {_defenderHero.Name} (human) spent Fate " +
                $"(remaining={_defenderHero.Fate}), rerolled slot {rerolledIndex} -> {_defenderDice[rerolledIndex]}");
        }

        private void OnAttackerSpend()
        {
            if (_kind == ChallengeKind.ResearchProduction)
            {
                OnResearchProductionSpend();
                return;
            }
            if (!_awaitingHumanDecision || _defenderTurnActive || _attackerHero == null || _attackerHero.Fate <= 0)
                return;
            if (!RerollOneMiss(ref _attackerDice, out int rerolledIndex))
                return;
            _attackerHero.Fate--;
            attackerRow?.SetSpendInteractable(false);
            if (acceptButton != null)
                acceptButton.interactable = false;
            _rerollAnimDone = attackerRow == null;
            attackerRow?.SetDice(_attackerDice, rerolledIndex, () => _rerollAnimDone = true);
            attackerRow?.OnFateSpent();
            _humanSpent = true;
            BattleDebugLog.Write($"[FateDuelDiag] attacker {_attackerHero.Name} (human) spent Fate " +
                $"(remaining={_attackerHero.Fate}), rerolled slot {rerolledIndex} -> {_attackerDice[rerolledIndex]}");
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
                // Defense never drops below 1 from Berserk — per the user's own call, a unit
                // that's been hit enough times shouldn't end up rolling defense dice at a
                // negative count. A stack triggered while already at the floor still counts
                // towards BerserkStacks (Attack still grows), it just removes 0 Defense.
                int defenseLoss = Mathf.Min(BerserkDefenseLoss, _defender.Defense - 1);
                if (defenseLoss > 0)
                {
                    _defender.Defense -= defenseLoss;
                    _defender.BerserkDefenseLost += defenseLoss;
                }
                _defender.BerserkStacks++;
            }

            _resultDamage = damage;
            _resultDied = died;
            BattleDebugLog.Write($"[ResolveDiag] {_attacker?.Name} -> {_defender?.Name}: " +
                $"rawSuccesses(attacker={result.AttackerSuccesses},defender={result.DefenderSuccesses}) wasHit={wasHit} " +
                $"finalDamage={damage} appliedAbilities=[{(appliedAbilities != null ? string.Join(",", appliedAbilities) : string.Empty)}] " +
                $"defenderHpAfter={_defender.HitPointsCurrent}/{_defender.HitPointsMax} died={died}");
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
            BattleDebugLog.Write($"[ResolveDiag] {_attacker?.Name} (hunter) -> {_defender?.Name} (target hero): " +
                $"rawSuccesses(attacker={result.AttackerSuccesses},defender={result.DefenderSuccesses}) outcome={outcome}");
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

            if (NoHumanInvolved || IsAutoCloseResultEnabled)
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

            if (NoHumanInvolved || IsAutoCloseResultEnabled)
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

            // Research/Production reports through the shared finalization path — the SAME one a
            // forced Hide() uses. Run it BEFORE Hide() (which calls it again as a safety net)
            // so the pass with the real success/failure wins and Hide()'s call no-ops instead
            // of reporting a second time. The R/P modal itself stays open; the caller decides
            // what happens next.
            if (_kind == ChallengeKind.ResearchProduction)
            {
                FinalizeResearchProduction(_rpResultSuccess);
                Hide();
                return;
            }

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
                // ChallengeKind.ResearchProduction is handled earlier (before Hide) via
                // FinalizeResearchProduction and never reaches here.
                Action<int, bool> callback = _onResolved;
                _onResolved = null;
                callback?.Invoke(_resultDamage, _resultDied);
            }
        }

        public void Hide()
        {
            if (panelRoot == null || !panelRoot.activeSelf)
                return;
            // Any close that ISN'T the normal R/P Result -> OK (an explicit Hide() from
            // HexSelectionController, this popup being reused for another Challenge, any other
            // teardown) still has to finalize an in-flight Research/Production Challenge:
            // restore the Hero's Fate to snapshot, clear R/P state and report the attempt as a
            // FAILURE exactly once — so no card is minted and the caller unwinds its own
            // transaction (_rpTransactionActive, _rpPendingCard) and drops the modal's Busy
            // state. No-op once OnOkClicked already finalized. ResourceCost stays spent (§4).
            FinalizeResearchProduction(false);
            panelRoot.SetActive(false);
            VisibilityChanged?.Invoke();
        }
    }
}
