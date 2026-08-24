using System.Collections.Generic;
using Game.Players;
using Game.Styles;
using Game.Turns;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // One player's row in the turn-order popup: name + dice slots + resulting rank. Dice
    // slots are spawned fresh per SetPlayer call (not a fixed count any more — a player who
    // bought bonus dice this turn rolls more than the 3 base ones, see
    // TurnOrderResolver.DiceCountFor). Each slot is a DiceSlotUI (see its own comment) —
    // DiceFace_Hit.png/DiceFace_Miss.png swapped via a coin-flip spin instead of the old
    // "1"/"X" text placeholder.
    public class DiceRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private Transform diceContainer;
        [SerializeField] private DiceSlotUI diceSlotPrefab;
        [SerializeField] private TMP_Text rankText;

        private readonly List<DiceSlotUI> _diceSlots = new List<DiceSlotUI>();

        public void SetPlayer(PlayerSetupData player, int diceCount)
        {
            if (nameText != null)
            {
                nameText.text = player.Nickname;
                nameText.color = PlayerColorPalette.Colors[player.ColorIndex];
            }
            if (rankText != null)
                rankText.text = string.Empty;
            SpawnSlots(diceCount);
        }

        // onComplete (optional): fired once every slot in this row has finished flipping
        // (immediately, if there's nothing to animate) — TurnOrderPopupUI waits on this across
        // every row before revealing rank/enabling Continue, so nobody sees the outcome before
        // the dice actually land (per the user's own request, 2026-08-24).
        public void ShowRoll(DiceRollResult roll, System.Action onComplete = null)
        {
            if (roll == null)
            {
                onComplete?.Invoke();
                return;
            }

            // index/count so the whole row's dice land one after another but the ROW as a whole
            // still finishes in DiceSlotUI's fixed GroupDuration regardless of dice count.
            int count = Mathf.Min(_diceSlots.Count, roll.Dice.Length);
            if (count == 0)
            {
                onComplete?.Invoke();
                return;
            }
            int pending = count;
            void SlotDone()
            {
                pending--;
                if (pending == 0)
                    onComplete?.Invoke();
            }
            for (int i = 0; i < count; i++)
                _diceSlots[i].PlayRoll(roll.Dice[i], i, count, SlotDone);
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
                _diceSlots.Add(Instantiate(diceSlotPrefab, diceContainer));
        }
    }
}
