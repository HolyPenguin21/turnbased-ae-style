using System;
using System.Collections.Generic;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One side's row in BattleAttackPopupUI's Roll state — faction logo, the acting/defending
    // unit's name and HP, that SIDE's Fate (from its hero, if any — Fate is a shared per-side
    // resource the hero contributes, per the manual, not something the attacking/defending unit
    // card itself needs to be a hero to use) + a Spend button, and a dice-slot strip. Dice
    // rendering copies DiceRowUI's own established "no art yet, just '1'/'X' text per slot"
    // convention (see its own comment, and ChallengeResult's — this is exactly the reuse it
    // anticipated) rather than inventing new dice art.
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
        [SerializeField] private TMP_Text diceSlotPrefab;
        [SerializeField] private TMP_Text successText;

        private readonly List<TMP_Text> _diceSlots = new List<TMP_Text>();
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
            if (nameText != null)
                nameText.text = unit != null ? unit.Name : string.Empty;
            if (hpText != null)
                hpText.text = unit != null ? $"HP: {unit.HitPointsCurrent}/{unit.HitPointsMax}" : string.Empty;

            if (fateRoot != null)
                fateRoot.SetActive(sideHero != null);
            RefreshFate();

            UIListUtility.DestroyAndClear(_diceSlots);
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

        // Rebuilds the dice strip from scratch every call (reroll or first roll alike) — same
        // "destroy and rebuild" convention used everywhere else in the battle screen rather than
        // patching individual slots in place.
        public void SetDice(bool[] dice)
        {
            UIListUtility.DestroyAndClear(_diceSlots);
            if (diceContainer != null && diceSlotPrefab != null && dice != null)
                foreach (bool hit in dice)
                {
                    TMP_Text slot = Instantiate(diceSlotPrefab, diceContainer);
                    slot.text = hit ? "1" : "X";
                    _diceSlots.Add(slot);
                }

            if (successText != null && dice != null)
            {
                int successes = 0;
                foreach (bool hit in dice)
                    if (hit) successes++;
                successText.text = successes.ToString();
            }
        }
    }
}
