using System;
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
    // button whose meaning depends on whose decision phase it currently is (see Phase) — matches
    // the reference, which only ever shows one Accept button even though the manual's "Defender's
    // Prerogative" is a two-phase decision (defender decides first, then attacker). A side with no
    // hero (no Fate to spend) or that isn't the local human is auto-accepted the instant its phase
    // starts — there's nothing for it to decide (see CanDecide).
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

        private enum Phase { NotRolled, DefenderDeciding, AttackerDeciding, Resolved }
        private Phase _phase;

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
        // conservation rule for that case (see RunAiFateSpend).
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
            attackerRow?.SetDice(_attackerDice);
            defenderRow?.SetDice(_defenderDice);
            if (rollButton != null)
                rollButton.interactable = false;

            FireRollThought();
            BeginDefenderPhase();
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
        // RunAiFateSpend). NOT the same question as "should this phase pause for Accept" any
        // more (see the two Begin*Phase methods below) — a human side with no hero (e.g. a
        // CaptureKill hunter army with no hero of its own — see BeginCaptureKill's own note)
        // still has nothing to SPEND, but still clicked Roll Die and still needs to see the
        // result and click Accept themselves.
        private static bool CanSpend(UnitData hero) => hero != null && hero.Owner != null && hero.Owner.IsHuman;

        private void BeginDefenderPhase()
        {
            _phase = Phase.DefenderDeciding;
            attackerRow?.SetSpendInteractable(false);

            // Paused for Accept whenever the DEFENDING UNIT itself (not its hero, which may not
            // exist — see CanSpend's own note) belongs to the local human — an AI-owned unit has
            // nobody to click Accept, so it auto-resolves via RunAiFateSpend instead. Checking
            // the unit's own Owner rather than CanDecide(_defenderHero) is what fixes a human
            // hunter's Capture Kill Challenge (or a human's own hero-less defender in Ground
            // Combat) instantly resolving without ever showing the roll — see the user's own
            // report.
            if (IsAiSide(_defender))
            {
                RunAiFateSpend(_defenderHero, isDefender: true);
                BeginAttackerPhase();
                return;
            }
            defenderRow?.SetSpendInteractable(CanSpend(_defenderHero) && _defenderHero.Fate > 0 && HasMiss(_defenderDice));
            if (acceptButton != null)
                acceptButton.interactable = true;
        }

        private void BeginAttackerPhase()
        {
            _phase = Phase.AttackerDeciding;
            defenderRow?.SetSpendInteractable(false);

            // Same reasoning as BeginDefenderPhase's own comment — paused for Accept whenever
            // the ATTACKING UNIT's owner is human, regardless of whether _attackerHero even
            // exists (always null for a CaptureKill hunter — see BeginCaptureKill).
            if (IsAiSide(_attacker))
            {
                RunAiFateSpend(_attackerHero, isDefender: false);
                Resolve();
                return;
            }
            attackerRow?.SetSpendInteractable(CanSpend(_attackerHero) && _attackerHero.Fate > 0 && HasMiss(_attackerDice));
            if (acceptButton != null)
                acceptButton.interactable = true;
        }

        // AI path for Defender's/Attacker's Prerogative — mirrors the human Spend button
        // (OnDefenderSpend/OnAttackerSpend) exactly, just driven by BattleAi.ShouldSpendFate in a
        // loop instead of a click, so it can spend more than one Fate when each one still matters
        // (see BattleAi.ShouldSpendFate's own re-evaluation against the current dice each time).
        private void RunAiFateSpend(UnitData hero, bool isDefender)
        {
            if (hero == null || hero.Fate <= 0)
                return;
            bool hadMiss = HasMiss(isDefender ? _defenderDice : _attackerDice);
            bool spent = false;
            bool isRetreating = isDefender && _defenderIsRetreating;
            int defendingUnitHp = _defender != null ? _defender.HitPointsCurrent : int.MaxValue;
            while (hero.Fate > 0 && BattleAi.ShouldSpendFate(_attackerDice, _defenderDice, hero.Fate, isDefender, isRetreating, defendingUnitHp, _kind == ChallengeKind.CaptureKill))
            {
                bool rerolled = isDefender
                    ? RerollOneMiss(ref _defenderDice, out int rerolledIndex)
                    : RerollOneMiss(ref _attackerDice, out rerolledIndex);
                if (!rerolled)
                    break;
                hero.Fate--;
                if (isDefender)
                {
                    defenderRow?.SetDice(_defenderDice, rerolledIndex);
                    defenderRow?.OnFateSpent();
                }
                else
                {
                    attackerRow?.SetDice(_attackerDice, rerolledIndex);
                    attackerRow?.OnFateSpent();
                }
                spent = true;
            }
            if (hadMiss)
            {
                UnitData actingUnit = isDefender ? _defender : _attacker;
                UnitData opposingUnit = isDefender ? _attacker : _defender;
                _onAiThought?.Invoke(actingUnit, spent ? AiThoughtCategory.FateSpendWorthIt : AiThoughtCategory.FateSpendSkip, opposingUnit?.Name);
            }
        }

        private void OnAcceptClicked()
        {
            if (_phase == Phase.DefenderDeciding)
                BeginAttackerPhase();
            else if (_phase == Phase.AttackerDeciding)
                Resolve();
        }

        private void Resolve()
        {
            if (_kind == ChallengeKind.CaptureKill)
                ResolveCaptureKill();
            else
                ResolveDamage();
        }

        private void OnDefenderSpend()
        {
            if (_phase != Phase.DefenderDeciding || _defenderHero == null || _defenderHero.Fate <= 0)
                return;
            if (!RerollOneMiss(ref _defenderDice, out int rerolledIndex))
                return;
            _defenderHero.Fate--;
            defenderRow?.SetDice(_defenderDice, rerolledIndex);
            defenderRow?.OnFateSpent();
            defenderRow?.SetSpendInteractable(_defenderHero.Fate > 0 && HasMiss(_defenderDice));
        }

        private void OnAttackerSpend()
        {
            if (_phase != Phase.AttackerDeciding || _attackerHero == null || _attackerHero.Fate <= 0)
                return;
            if (!RerollOneMiss(ref _attackerDice, out int rerolledIndex))
                return;
            _attackerHero.Fate--;
            attackerRow?.SetDice(_attackerDice, rerolledIndex);
            attackerRow?.OnFateSpent();
            attackerRow?.SetSpendInteractable(_attackerHero.Fate > 0 && HasMiss(_attackerDice));
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
            int damage = result.Damage;

            // UnitAbilities.CriticalDamage/CeramicArmor — the manual's fixed-value damage
            // modifiers (pg. 40/43): a x2 multiplier applied first (the attacker's own doing),
            // then a flat reduction (the defender's), same order any other "multiply then
            // subtract flat armor" stat stack would apply in. Both gated on damage > 0 — a miss
            // has nothing for either to modify.
            if (damage > 0 && _attacker.HasAbility(UnitAbilities.CriticalDamage))
                damage = Mathf.RoundToInt(damage * CriticalDamageMultiplier);
            // UnitAbilities.Hyperkinetic — flat bonus specifically against Armored-tagged
            // targets, gated on the same "already a hit" check as every other modifier here.
            // An attacker-side bonus, so it's grouped with CriticalDamage above rather than
            // CeramicArmor's defender-side reduction right below.
            if (damage > 0 && _attacker.HasAbility(UnitAbilities.Hyperkinetic) && _defender.TypeTags.Contains(UnitTypeTag.Armored))
                damage += HyperkineticBonusDamage;
            if (damage > 0 && _defender.HasAbility(UnitAbilities.CeramicArmor))
                damage = Mathf.Max(0, damage - CeramicArmorReduction);

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
            ShowResult(damage, died);
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

        private void ShowResult(int damage, bool died)
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
                string hitLine = damage > 0 ? "Hit!" : "Miss";
                string outcomeLine = died ? "\nThe target was destroyed." : string.Empty;
                resultSummaryText.text = $"Target ID: {_defender.Name}\nHit Assessment: {hitLine}\nDamage Assessment: {damage} Damage{outcomeLine}";
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
