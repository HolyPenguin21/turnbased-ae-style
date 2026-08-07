using System.Collections.Generic;
using Game.Players;
using Game.Turns;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // One player's row in the turn-order popup: name + dice slots + resulting rank. Dice
    // slots are spawned fresh per SetPlayer call (not a fixed count any more — a player who
    // bought bonus dice this turn rolls more than the 3 base ones, see
    // TurnOrderResolver.DiceCountFor). No dice art yet — each slot is just text ("1" for a
    // hit, "X" for a miss) until this gets animated later.
    public class DiceRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private Transform diceContainer;
        [SerializeField] private TMP_Text diceSlotPrefab;
        [SerializeField] private TMP_Text rankText;

        private readonly List<TMP_Text> _diceSlots = new List<TMP_Text>();

        public void SetPlayer(PlayerSetupData player, int diceCount)
        {
            if (nameText != null)
                nameText.text = player.Nickname;
            if (rankText != null)
                rankText.text = string.Empty;
            SpawnSlots(diceCount);
        }

        public void ShowRoll(DiceRollResult roll)
        {
            if (roll == null)
                return;

            for (int i = 0; i < _diceSlots.Count && i < roll.Dice.Length; i++)
                _diceSlots[i].text = roll.Dice[i] ? "1" : "X";
        }

        public void ShowRank(int rank)
        {
            if (rankText != null)
                rankText.text = $"#{rank}";
        }

        private void SpawnSlots(int count)
        {
            UIListUtility.DestroyAndClear(_diceSlots);

            if (diceContainer == null || diceSlotPrefab == null)
                return;

            for (int i = 0; i < count; i++)
            {
                TMP_Text slot = Instantiate(diceSlotPrefab, diceContainer);
                slot.text = "X";
                _diceSlots.Add(slot);
            }
        }
    }
}
