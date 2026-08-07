using System;
using Game.Combat;
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

        private UnitData _attacker;
        private UnitData _defender;
        private UnitData _attackerHero;
        private UnitData _defenderHero;
        private bool[] _attackerDice;
        private bool[] _defenderDice;
        private int _resultDamage;
        private bool _resultDied;
        private Action<int, bool> _onResolved;
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

        // attackerHero/defenderHero are that SIDE's hero if present (may be null) — Fate is a
        // per-side resource the hero contributes, not something the attacking/defending unit card
        // itself needs to be a hero to use (see BattleCombatantRowUI's own comment); the caller
        // (BattleScreenUI) already has the grid to look these up from.
        public void Begin(UnitData attacker, UnitData attackerHero, UnitData defender, UnitData defenderHero,
            Sprite factionLogo, Action<int, bool> onResolved, Action<UnitData, AiThoughtCategory, string> onAiThought = null)
        {
            _attacker = attacker;
            _defender = defender;
            _attackerHero = attackerHero;
            _defenderHero = defenderHero;
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
            attackerRow?.SetSpendInteractable(false);
            defenderRow?.SetSpendInteractable(false);
            if (rollButton != null)
                rollButton.interactable = true;
            if (acceptButton != null)
                acceptButton.interactable = false;
        }

        private void OnRollClicked()
        {
            if (_phase != Phase.NotRolled)
                return;

            ChallengeResult result = ChallengeResolver.Resolve(_attacker.Attack, _defender.Defense);
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

        // A side can only actually DECIDE anything (spend Fate or consciously accept) if it has a
        // hero to spend Fate from AND is the local human — an AI side or a hero-less side has
        // nothing to weigh, so its phase resolves itself immediately (see the two Begin*Phase
        // methods below).
        private static bool CanDecide(UnitData hero) => hero != null && hero.Owner != null && hero.Owner.IsHuman;

        private void BeginDefenderPhase()
        {
            _phase = Phase.DefenderDeciding;
            attackerRow?.SetSpendInteractable(false);

            if (!CanDecide(_defenderHero))
            {
                RunAiFateSpend(_defenderHero, isDefender: true);
                BeginAttackerPhase();
                return;
            }
            defenderRow?.SetSpendInteractable(_defenderHero.Fate > 0 && HasMiss(_defenderDice));
            if (acceptButton != null)
                acceptButton.interactable = true;
        }

        private void BeginAttackerPhase()
        {
            _phase = Phase.AttackerDeciding;
            defenderRow?.SetSpendInteractable(false);

            if (!CanDecide(_attackerHero))
            {
                RunAiFateSpend(_attackerHero, isDefender: false);
                ResolveDamage();
                return;
            }
            attackerRow?.SetSpendInteractable(_attackerHero.Fate > 0 && HasMiss(_attackerDice));
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
            while (hero.Fate > 0 && BattleAi.ShouldSpendFate(_attackerDice, _defenderDice, hero.Fate, isDefender))
            {
                bool rerolled = isDefender ? RerollOneMiss(ref _defenderDice) : RerollOneMiss(ref _attackerDice);
                if (!rerolled)
                    break;
                hero.Fate--;
                if (isDefender)
                {
                    defenderRow?.SetDice(_defenderDice);
                    defenderRow?.OnFateSpent();
                }
                else
                {
                    attackerRow?.SetDice(_attackerDice);
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
                ResolveDamage();
        }

        private void OnDefenderSpend()
        {
            if (_phase != Phase.DefenderDeciding || _defenderHero == null || _defenderHero.Fate <= 0)
                return;
            if (!RerollOneMiss(ref _defenderDice))
                return;
            _defenderHero.Fate--;
            defenderRow?.SetDice(_defenderDice);
            defenderRow?.OnFateSpent();
            defenderRow?.SetSpendInteractable(_defenderHero.Fate > 0 && HasMiss(_defenderDice));
        }

        private void OnAttackerSpend()
        {
            if (_phase != Phase.AttackerDeciding || _attackerHero == null || _attackerHero.Fate <= 0)
                return;
            if (!RerollOneMiss(ref _attackerDice))
                return;
            _attackerHero.Fate--;
            attackerRow?.SetDice(_attackerDice);
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
        private static bool RerollOneMiss(ref bool[] dice)
        {
            if (dice == null)
                return false;
            for (int i = 0; i < dice.Length; i++)
                if (!dice[i])
                {
                    dice[i] = ChallengeResolver.RollDice(1)[0];
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
            _defender.HitPointsCurrent = Mathf.Max(0, _defender.HitPointsCurrent - damage);
            bool died = _defender.HitPointsCurrent <= 0;

            _resultDamage = damage;
            _resultDied = died;
            ShowResult(damage, died);
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
            Action<int, bool> callback = _onResolved;
            _onResolved = null;
            callback?.Invoke(_resultDamage, _resultDied);
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
