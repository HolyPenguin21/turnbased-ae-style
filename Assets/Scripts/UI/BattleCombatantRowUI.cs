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
            // Only one faction's art exists in this project right now (see factionLogo — the
            // SAME sprite is passed for both rows regardless of actual owner, per BattleScreenUI's
            // own identical `catalog` comment), and unit type names collide across sides too (an
            // enemy Light Infantry looks and reads identically to the player's own) — the owner's
            // own colour is what tells the two rows apart (see PlayerColorPalette); the debug
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
        public void SetDicePoolSize(int count)
        {
            if (diceCountText != null)
                diceCountText.text = $"Dice: {count}";
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
        // (immediately, if none needed to animate) — BattleAttackPopupUI's roll/duel coroutine
        // awaits this before letting Accept/Spend become clickable again or handing the turn to
        // the other side (see the user's own report: the Accept button, and the AI's own counter-
        // spend, used to happen mid-flip or even before the flip was ever seen at all).
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

            if (successText != null)
            {
                int successes = 0;
                foreach (bool hit in dice)
                    if (hit) successes++;
                successText.text = successes.ToString();
            }

            if (toAnimate.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }
            int pending = toAnimate.Count;
            void SlotDone()
            {
                pending--;
                if (pending == 0)
                    onComplete?.Invoke();
            }
            foreach (int i in toAnimate)
                _diceSlots[i].PlayRoll(dice[i], SlotDone);
        }
    }
}
