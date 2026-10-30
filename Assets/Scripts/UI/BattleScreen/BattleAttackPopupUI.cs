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
    // Roll state: both sides' BattleCombatantRowUI, a Roll Token button, and a single shared Accept
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
    //
    // Files: this one (fields, lifecycle, Begin entry points), .Duel (roll + Fate duel), .Result
    // (resolution and result screen), .ResearchProduction and .Tactics.
    public partial class BattleAttackPopupUI : MonoBehaviour
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
        // Player-facing "auto-press Roll Token for me" checkbox (Autoroll_Toggle, under
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
        // How long before Roll Token auto-presses itself when neither side is human (see
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
        // ShowSecondaryAttackResult (see its own comment) never leaves Phase.Resolved, so _phase alone
        // can't tell "already acknowledged" apart from "still showing" for that case. Reset
        // false by every entry point that puts up a new closeable screen (Begin, ShowSecondaryAttackResult
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
        // Set false right before a Fate-spend reroll's SetTokens call, true once that die's own
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
        // just differ in how tokens-pool sizes are computed and what the result means. See
        // BeginCaptureKill for the second one; add further Begin*/Resolve* pairs here rather than
        // a whole new popup component per challenge type.
        private enum ChallengeKind { GroundCombat, CaptureKill, Announcement, ResearchProduction }
        private ChallengeKind _kind;

        private UnitData _attacker;
        private UnitData _defender;
        // Ground Combat only: the UnitAbilities.Bodyguard that takes a landed hit instead of
        // _defender (BattleEngine.FindInterceptingBodyguard, asked by the caller before the roll).
        private UnitData _interceptor;
        private Func<UnitData, int, IReadOnlyList<string>> _groundHitEffects;
        private UnitData _attackerHero;
        private UnitData _defenderHero;
        // Whether the DEFENDER's own army is the one currently retreating (see BattleScreenUI.
        // _retreatingArmy) — the attacker is never the retreating side, since a retreating army's
        // units are excluded from the turn order and can't act (see BattleScreenUI.
        // OnStartRoundClicked), only get attacked. Feeds FateDuelAi.ShouldSpendFate's own Fate-
        // conservation rule for that case (see RunAiTurn).
        private bool _defenderIsRetreating;
        // GroundCombat only — the ACTUAL tokens-pool sizes the roll uses, resolved once in Begin
        // (attackerPoolSize/defenderPoolSize ?? the plain Attack/Defense+bonus default) and read
        // back by RunRollAndDuel instead of recomputing from _attacker.Attack/_defender.Defense
        // directly. Must stay in lockstep with what attackerRow/defenderRow.SetTokenPoolSize was
        // just told to DISPLAY in Begin — see the bug this fixed: a hero defender's displayed
        // pool is its FateMax (defenderPoolSize, see BeginAttack's own "target hero" comment),
        // but the roll used to always fall back to _defender.Defense regardless, which is 0 for
        // every hero card (attack/defenseRating are both 0 in the hero cards' own data — heroes
        // fight via Fate, not a Defense stat). Rolling 0 tokens for the defender meant no tokens
        // slots at all for that side and a duel forced to whatever the attacker alone rolled,
        // even though the row up top still correctly showed "Tokens: {FateMax}" (the project
        // owner's own report: "у героя не крутятся кубики... количество кубиков написано, но
        // визуально их нет").
        private int _attackerTokenPoolSize;
        private int _defenderTokenPoolSize;
        // CaptureKill mode only — the hunter's computed tokens-pool size (see BeginCaptureKill;
        // there's no per-unit Attack stat to roll against, unlike Ground Combat) and how many
        // tokens the target hero rolls (their Fate stat, per the manual: "the target hero receives
        // a tokens pool equal to his fate"), captured once at OnRollClicked time so a later reroll
        // spending that same Fate down doesn't change how many tokens were actually in this roll.
        // Outcome itself (see ResolveCaptureKill) compares actual successes only, not this pool
        // size — per the user's own call, dropping the manual's separate "capture threshold".
        private int _hunterTokenPool;
        private BattleChallengeSession _challengeSession;
        // Presentation mirrors of the domain session below; the popup never mutates them directly.
        private bool[] _attackerTokens;
        private bool[] _defenderTokens;
        private int _attackerFateRemaining;
        private int _defenderFateRemaining;
        private Action<BattleChallengeRollResult> _onResolved;
        private Action<BattleChallengeRollResult> _onCaptureKillResolved;
        private Action _onAnnouncementAcknowledged;
        private Action<UnitData, AiThoughtCategory, string> _onAiThought;

        // ---- Research/Production Challenge state (see BeginResearchProduction) — ENTIRELY
        // separate from the Ground Combat / Capture Kill duel plumbing above. Only ever touched
        // on the ResearchProduction code path; the shared Begin/RunDuel/Resolve never read these.
        // The defender is a CardDefinition, never a fake UnitData (per the spec). ----
        private UnitData _rpHero;
        private CardDefinition _rpCard;
        private int _rpRequiredSuccesses;
        private bool[] _rpTokens;
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
            InitializeTacticInput();
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
            {
                Game.Audio.GameAudioManager.Instance?.PlayClick();
                OnOkClicked();
            }
        }

        // attackerHero/defenderHero are that SIDE's hero if present (may be null) — Fate is a
        // per-side resource the hero contributes, not something the attacking/defending unit card
        // itself needs to be a hero to use (see BattleCombatantRowUI's own comment); the caller
        // (BattleScreenUI) already has the grid to look these up from.
        // attackerPoolSize/defenderPoolSize (optional): only BeginCaptureKill needs to override
        // these — its pools are _hunterTokenPool/the target hero's own Fate, nothing to do with
        // Attack/Defense (see BeginCaptureKill's own comment). Ground Combat's default is attacker.
        // Attack versus defender.Defense (or hero.FateMax) + battlefield defense bonuses, matching
        // exactly what OnRollClicked itself rolls against, just surfaced before Roll Token is even
        // clicked (see the user's own request to see each side's pool size up front, not just its
        // post-roll success count).
        public void Begin(UnitData attacker, UnitData attackerHero, UnitData defender, UnitData defenderHero,
            Sprite attackerLogo, Sprite defenderLogo, Action<BattleChallengeRollResult> onResolved, Action<UnitData, AiThoughtCategory, string> onAiThought = null,
            bool defenderIsRetreating = false, int defenderTerrainBonus = 0, int defenderConstructionBonus = 0,
            int? attackerPoolSize = null, int? defenderPoolSize = null, int defenderFormationBonus = 0,
            bool automateHumanSides = false, UnitData interceptor = null, bool isCounterattack = false, bool tacticsCritical = false,
            string challengeTitle = null, Func<UnitData, int, IReadOnlyList<string>> groundHitEffects = null)
        {
            _tacticsCritical = tacticsCritical;
            CloseTacticPresentation();
            int defenderBonusTokens = defenderTerrainBonus + defenderConstructionBonus + defenderFormationBonus;
            // Stops a still-running RunRollAndDuel/RunDuel coroutine from a PREVIOUS Begin() on
            // this same (pooled/reused) popup instance — otherwise its delayed WaitUntil callbacks
            // could fire against the fields this call is about to overwrite.
            StopAllCoroutines();
            // If a Research/Production Challenge was somehow torn down without resolving, put its
            // hero's Fate back before this popup is reused for anything else (spec §15).
            CleanupResearchProduction();

            _automateHumanSides = automateHumanSides;
            _kind = ChallengeKind.GroundCombat;
            _attacker = attacker;
            _defender = defender;
            _interceptor = interceptor;
            _groundHitEffects = groundHitEffects;
            _attackerHero = attackerHero;
            _defenderHero = defenderHero;
            _attackerFateRemaining = Mathf.Max(0, attackerHero?.Fate ?? 0);
            _defenderFateRemaining = Mathf.Max(0, defenderHero?.Fate ?? 0);
            _defenderIsRetreating = defenderIsRetreating;
            _onResolved = onResolved;
            _onAiThought = onAiThought;
            _challengeSession = null;
            _attackerTokens = null;
            _defenderTokens = null;
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
                titleText.text = challengeTitle ?? (isCounterattack ? "COUNTERATTACK" : "GROUND COMBAT");

            _attackerTokenPoolSize = attackerPoolSize ?? (attacker?.Attack ?? 0);
            int defenderBaseDefense = defender == null ? 0
                : (defender.IsHero ? Mathf.Max(0, defender.FateMax) : defender.Defense);
            _defenderTokenPoolSize = defenderPoolSize ?? (defenderBaseDefense + defenderBonusTokens);

            attackerRow?.Setup(attacker, attackerHero, attackerLogo);
            defenderRow?.Setup(defender, defenderHero, defenderLogo);
            attackerRow?.SetFateDisplay(_attackerFateRemaining);
            defenderRow?.SetFateDisplay(_defenderFateRemaining);
            attackerRow?.SetTokenPoolSize(_attackerTokenPoolSize);
            defenderRow?.SetTokenPoolSize(_defenderTokenPoolSize,
                defenderTerrainBonus, defenderConstructionBonus, defenderBaseDefense, defenderFormationBonus);
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (rollButton != null)
                rollButton.interactable = true;
            if (acceptButton != null)
                acceptButton.interactable = false;

            if (RunsAutomatically || IsAutorollEnabled)
                StartCoroutine(AutoRollIfNoHuman());
        }

        // The Autoroll_Toggle checkbox (see Awake/OnAutorollToggleChanged) — lets a human player
        // opt into the same auto-press-Roll-Token behavior AutoRollIfNoHuman already gives an
        // AI-vs-AI encounter (see RunsAutomatically's own call site in Begin), so they don't have to
        // click Roll Token themselves every single Ground Combat/Capture Kill challenge.
        private bool IsAutorollEnabled => autorollToggle != null && autorollToggle.isOn;

        // The ChallengeResultRoot checkbox's own read — same principle as IsAutorollEnabled
        // above, just gating AutoCloseResultIfNoHuman's three call sites (ShowResult/
        // ShowCaptureKillResult/ShowSecondaryAttackResult) instead of Begin's Roll-Token auto-press.
        private bool IsAutoCloseResultEnabled => autoCloseResultToggle != null && autoCloseResultToggle.isOn;

        private bool _automateHumanSides;
        private static bool IsHumanSide(UnitData unit) => unit != null && unit.Owner != null && unit.Owner.IsHuman;

        // Neither current side needs to actually look at anything here before it happens — an
        // AI-vs-AI or AI-vs-neutral encounter (no human on either side), same population this
        // popup's own auto-roll/auto-close behavior targets. Reads the live _attacker/_defender
        // fields rather than taking parameters so ShowSecondaryAttackResult (no attacker/defender of its
        // own — see its own comment) can reuse the exact same check off whatever the last real
        // challenge on this popup instance set them to.
        private bool RunsAutomatically => _automateHumanSides || (!IsHumanSide(_attacker) && !IsHumanSide(_defender));

        // Nobody human needs to look at this roll before it happens (see RunsAutomatically) — an
        // AI-vs-AI or AI-vs-neutral encounter would otherwise just sit on Phase.NotRolled forever,
        // since rollButton only ever gets pressed by an explicit click. Short delay purely for
        // visual pacing, same reasoning as aiAcceptDelay.
        private IEnumerator AutoRollIfNoHuman()
        {
            // No pacing beat at all when neither side is human (see RunsAutomatically) — only the
            // IsAutorollEnabled human-opted-in case still waits aiRollDelay, per the user's own
            // request (2026-08-24) to stop stalling a purely AI/neutral fight just to be readable
            // to a spectator; aiRollDelay itself is untouched for that still-human case.
            if (!RunsAutomatically && aiRollDelay > 0f)
                yield return new WaitForSeconds(aiRollDelay);
            if (_phase == Phase.NotRolled)
                OnRollClicked();
        }

        // Same "nobody human needs to look at this" gate as AutoRollIfNoHuman, for whichever
        // result screen just went up (Ground Combat, Capture Kill, or a bare Announcement) — an
        // AI-vs-AI/AI-vs-neutral encounter would otherwise sit on Phase.Resolved forever waiting
        // for a click nobody's there to make. Also started when IsAutoCloseResultEnabled (see its
        // own comment) even with a human on both sides — same opt-in idea as IsAutorollEnabled,
        // just for dismissing the result instead of pressing Roll Token. OnOkClicked's own
        // _okAlreadyHandled guard makes this safe even if a human elsewhere in the scene somehow
        // also clicks Ok around the same time.
        private IEnumerator AutoCloseResultIfNoHuman()
        {
            // Same RunsAutomatically carve-out as AutoRollIfNoHuman above (2026-08-24) — only the
            // IsAutoCloseResultEnabled human-opted-in case still gets the readable aiResultCloseDelay
            // pause; a battle with nobody human in it at all resolves instantly instead.
            if (!RunsAutomatically && aiResultCloseDelay > 0f)
                yield return new WaitForSeconds(aiResultCloseDelay);
            if (_phase == Phase.Resolved && !_okAlreadyHandled)
                OnOkClicked();
        }

        // The manual's "Capture Kill Challenge" (pg. 24) — same Roll/Defender's-Prerogative/
        // Accept shell as Ground Combat (see Begin, reused verbatim here), but the tokens pools and
        // the win condition are entirely different, so this sets them up itself instead of going
        // through Begin's Attack/Defense-stat plumbing:
        //   - Hunter's pool = 1 + (hunterArmy's own non-hero unit count / 2). The manual also adds
        //     "highest observation strength in hex" — Observation/Recce doesn't exist in this
        //     project yet (see BattleInitiator's own Stealth note), so that term is 0 for now.
        //   - Target's pool = the hunted hero's own Fate stat (see the manual: "The target hero
        //     receives a tokens pool equal to his fate").
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
            Action<BattleChallengeRollResult> onResolved, Action<UnitData, AiThoughtCategory, string> onAiThought = null,
            bool automateHumanSides = false, bool tacticsCritical = false)
        {
            UnitData hunterHero = hunterArmy?.Members.Find(m => m.IsHero);
            UnitData hunterFace = hunterHero ?? hunterArmy?.Members.Find(m => !m.IsHero);
            int hunterUnits = hunterArmy?.Members.FindAll(m => !m.IsHero).Count ?? 0;
            _hunterTokenPool = BattleRules.CaptureKillHunterPool(hunterUnits);
            _onCaptureKillResolved = onResolved;

            // The manual: "the target hero receives a tokens pool equal to his fate" — his FULL
            // Fate stat, not whatever he happens to have left after spending some earlier this
            // same battle (see the user's own correction; RunRollAndDuel below reads the same
            // FateMax for the actual roll).
            Begin(hunterFace, hunterHero, targetHero, targetHero, hunterLogo, targetLogo, null, onAiThought,
                attackerPoolSize: _hunterTokenPool, defenderPoolSize: targetHero?.FateMax ?? 0,
                automateHumanSides: automateHumanSides, tacticsCritical: tacticsCritical);
            _kind = ChallengeKind.CaptureKill;
            if (titleText != null)
                titleText.text = "CAPTURE/KILL CHALLENGE";
        }

        public void Hide()
        {
            CloseTacticPresentation();
            _automateHumanSides = false;
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
