using System;
using System.Collections.Generic;
using Game.Styles;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One side's row in BattleAttackPopupUI's Roll state — faction logo, the acting/defending
    // unit's name and HP, that SIDE's Fate (from its hero, if any — Fate is a shared per-side
    // resource the hero contributes, per the manual, not something the attacking/defending unit
    // card itself needs to be a hero to use) + a Spend button, and a dice-slot strip. Dice slots
    // are DiceSlotUI (see its own comment) — same shared component/sprites as DiceRowUI's turn-
    // order dice.
    public class BattleCombatantRowUI : MonoBehaviour
    {
        [SerializeField] private Image logoImage;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text hpText;
        // Hidden entirely when this side has no hero (see Setup) — matches the reference, where
        // the side without a hero shows no Fate/Spend at all, not just a greyed-out zero.
        [SerializeField] private GameObject fateRoot;
        [SerializeField] private TMP_Text fateText;
        [SerializeField] private Button spendButton;
        [SerializeField] private Transform diceContainer;
        [SerializeField] private DiceSlotUI diceSlotPrefab;
        [SerializeField] private TMP_Text successText;
        // How many dice this side's pool is about to roll (or just rolled) — set by
        // BattleAttackPopupUI.SetDicePoolSize before/at Roll time, alongside successText's own
        // after-the-roll hit count, so the player can see the pool size up front instead of only
        // inferring it by counting dice slots once they've already appeared (see the user's own
        // request).
        [SerializeField] private TMP_Text diceCountText;

        private readonly List<DiceSlotUI> _diceSlots = new List<DiceSlotUI>();
        // Last dice array SetDice was actually given — a Fate-spend reroll calls SetDice again
        // with the SAME length array, only one entry actually changed (see BattleAttackPopupUI.
        // RerollOneMiss). Diffed against this so only that one slot replays its roll animation
        // instead of the whole row spinning again on every single Spend click.
        private bool[] _lastDice;
        private UnitData _sideHero;

        public event Action SpendClicked;

        private void Awake()
        {
            if (spendButton != null)
                spendButton.onClick.AddListener(() => SpendClicked?.Invoke());
        }

        public void Setup(UnitData unit, UnitData sideHero, Sprite factionLogo)
        {
            _sideHero = sideHero;

            if (logoImage != null)
            {
                logoImage.sprite = factionLogo;
                logoImage.gameObject.SetActive(factionLogo != null);
            }
            // factionLogo is resolved per-owner by the caller (BattleAttackPopupUI.Begin/
            // BeginCaptureKill, ultimately BattleScreenUI.ResolveCatalog) — this row just shows
            // whatever it's handed. Unit type names still collide across sides though (an enemy
            // Light Infantry looks and reads identically to the player's own) — the owner's own
            // colour is what tells the two rows apart (see PlayerColorPalette); the debug
            // (You)/(Enemy) suffix this used to add on top of that has been dropped per the
            // user's own request.
            if (nameText != null)
            {
                nameText.text = unit != null ? unit.Name : string.Empty;
                nameText.color = unit?.Owner != null
                    ? PlayerColorPalette.Colors[unit.Owner.ColorIndex]
                    : Color.white;
            }
            if (hpText != null)
                hpText.text = unit != null ? $"HP: {unit.HitPointsCurrent}/{unit.HitPointsMax}" : string.Empty;

            if (fateRoot != null)
                fateRoot.SetActive(sideHero != null);
            RefreshFate();

            UIListUtility.DestroyAndClear(_diceSlots);
            _lastDice = null;
            if (successText != null)
                successText.text = string.Empty;
            if (diceCountText != null)
                diceCountText.text = string.Empty;
        }

        // Pool size is known as soon as this side's Attack/Defense (+ any bonus dice) or
        // Capture Kill pool is computed — BattleAttackPopupUI calls this at the same point it
        // sets up the rest of the row, so it's visible before Roll Die is even clicked.
        //
        // terrainBonus/constructionBonus/baseDefense (defender side only — see BattleAttackPopupUI.
        // Begin, the attacker row and Capture Kill's pool-size override both leave these at their
        // default 0): when the hex is contributing to this side's pool, the count alone doesn't
        // say why it's bigger than the unit's own stat, so the prefix spells out the breakdown
        // instead (per the user's own request) — plain "Dice: {count}" otherwise, unchanged.
        public void SetDicePoolSize(int count, int terrainBonus = 0, int constructionBonus = 0, int baseDefense = 0)
        {
            if (diceCountText == null)
                return;

            if (terrainBonus == 0 && constructionBonus == 0)
            {
                diceCountText.text = $"Dice: {count}";
                return;
            }

            var parts = new List<string>();
            if (terrainBonus != 0)
                parts.Add($"Terrain({terrainBonus})");
            if (constructionBonus != 0)
                parts.Add($"Construction({constructionBonus})");
            parts.Add($"Defence({baseDefense})");
            diceCountText.text = $"{string.Join(" + ", parts)}, Dices: {count}";
        }

        private void RefreshFate()
        {
            if (fateText != null)
                fateText.text = _sideHero != null ? $"FATE: {_sideHero.Fate}" : string.Empty;
        }

        // Called by BattleAttackPopupUI right after a Fate spend actually rerolls a die — the
        // hero's own Fate has already been decremented by then, this just repaints the number.
        public void OnFateSpent()
        {
            RefreshFate();
        }

        public void SetSpendInteractable(bool interactable)
        {
            if (spendButton != null)
                spendButton.interactable = interactable;
        }

        // First roll (or the slot count otherwise changed) rebuilds the strip from scratch and
        // spins every slot. A reroll (see BattleAttackPopupUI.RerollOneMiss) calls this again
        // with the SAME-length array where only the rerolled die actually changed — those
        // existing slots are reused and only the changed one plays its roll animation, so
        // spending Fate doesn't re-spin dice that already settled.
        //
        // rerolledIndex (optional): the slot Fate was JUST spent on, if any — a plain by-value
        // diff (below) misses a reroll that lands on the SAME hit/miss result as before (still a
        // miss, just a different miss), which used to skip the flip animation entirely even
        // though a real reroll happened (see the user's own report). Forces that one slot to
        // replay regardless of whether its value actually changed.
        // onComplete (optional): fired once every slot THIS call touched has finished flipping
        // (immediately, if none needed to animate) — same moment the success count itself is
        // revealed (see ShowResults above). BattleAttackPopupUI waits on this both for the
        // initial roll (RunRollAndDuel, before opening the Fate duel/enabling Spend) and for
        // every later Fate-spend reroll (RunHumanTurn/RunAiTurn's own _rerollAnimDone wait), so
        // nothing becomes available to click, and no side reacts, before the dice have actually
        // landed (per the user's own request, 2026-08-24).
        public void SetDice(bool[] dice, int rerolledIndex = -1, System.Action onComplete = null)
        {
            if (dice == null)
            {
                onComplete?.Invoke();
                return;
            }

            var toAnimate = new List<int>();
            if (diceContainer != null && diceSlotPrefab != null && _diceSlots.Count != dice.Length)
            {
                UIListUtility.DestroyAndClear(_diceSlots);
                for (int i = 0; i < dice.Length; i++)
                {
                    DiceSlotUI slot = Instantiate(diceSlotPrefab, diceContainer);
                    // diceSlotPrefab is wired (in the scene) to an inactive template object
                    // rather than DiceRow.prefab's always-active DiceSlot prefab asset — Unity's
                    // Instantiate carries an inactive source's activeSelf over to the clone, so
                    // without this the clone starts inactive and PlayRoll's own
                    // !gameObject.activeInHierarchy check silently skips straight to
                    // SetImmediate, no flip animation at all (see the user's own report: no
                    // animated dice in the attack popup, unlike DiceRowUI's turn-order roll).
                    slot.gameObject.SetActive(true);
                    _diceSlots.Add(slot);
                    toAnimate.Add(i);
                }
            }
            else
            {
                for (int i = 0; i < dice.Length && i < _diceSlots.Count; i++)
                    if (i == rerolledIndex || _lastDice == null || i >= _lastDice.Length || _lastDice[i] != dice[i])
                        toAnimate.Add(i);
            }
            _lastDice = (bool[])dice.Clone();

            // Success count is revealed together with the dice landing, not before — showing the
            // number while the strip is still mid-flip let a player read the outcome ahead of the
            // animation (per the user's own request, 2026-08-24: results only appear once the roll
            // animation has actually finished).
            void ShowResults()
            {
                if (successText != null)
                {
                    int successes = 0;
                    foreach (bool hit in dice)
                        if (hit) successes++;
                    successText.text = successes.ToString();
                }
                onComplete?.Invoke();
            }

            if (toAnimate.Count == 0)
            {
                ShowResults();
                return;
            }
            int pending = toAnimate.Count;
            void SlotDone()
            {
                pending--;
                if (pending == 0)
                    ShowResults();
            }
            // index/count (position within THIS call's own toAnimate list, not the whole dice
            // array) so a full first roll lands its dice one after another across the row, and a
            // later single-die Fate reroll still gets the full Fate-reroll duration to itself
            // rather than landing near-instantly as if it were still part of an N-die group.
            for (int pos = 0; pos < toAnimate.Count; pos++)
            {
                int i = toAnimate[pos];
                float duration = rerolledIndex >= 0 ? DiceSlotUI.FateRerollDuration : DiceSlotUI.FullRollDuration;
                _diceSlots[i].PlayRoll(dice[i], pos, toAnimate.Count, duration, SlotDone);
            }
        }
    }
}
