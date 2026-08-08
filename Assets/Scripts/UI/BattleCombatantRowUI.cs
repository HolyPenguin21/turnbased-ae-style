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
        public void SetDice(bool[] dice)
        {
            if (dice == null)
                return;

            if (diceContainer != null && diceSlotPrefab != null && _diceSlots.Count != dice.Length)
            {
                UIListUtility.DestroyAndClear(_diceSlots);
                foreach (bool hit in dice)
                {
                    DiceSlotUI slot = Instantiate(diceSlotPrefab, diceContainer);
                    slot.PlayRoll(hit);
                    _diceSlots.Add(slot);
                }
            }
            else
            {
                for (int i = 0; i < dice.Length && i < _diceSlots.Count; i++)
                    if (_lastDice == null || i >= _lastDice.Length || _lastDice[i] != dice[i])
                        _diceSlots[i].PlayRoll(dice[i]);
            }
            _lastDice = (bool[])dice.Clone();

            if (successText != null)
            {
                int successes = 0;
                foreach (bool hit in dice)
                    if (hit) successes++;
                successText.text = successes.ToString();
            }
        }
    }
}
