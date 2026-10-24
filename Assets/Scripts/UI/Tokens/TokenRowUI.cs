using System.Collections.Generic;
using Game.Players;
using Game.Styles;
using Game.Turns;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    // One player's row in the turn-order popup: name + tokens slots + resulting rank. Tokens
    // slots are spawned fresh per SetPlayer call (not a fixed count any more — a player who
    // bought bonus tokens this turn rolls more than the 3 base ones, see
    // TurnOrderResolver.TokenCountFor). Each slot is a TokenSlotUI (see its own comment) —
    // Token_1.png/Token_2.png swapped via a coin-flip spin instead of the old
    // "1"/"X" text placeholder.
    public class TokenRowUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private Transform tokenContainer;
        [SerializeField] private TokenSlotUI tokenSlotPrefab;
        [SerializeField] private TMP_Text rankText;

        private readonly List<TokenSlotUI> _tokenSlots = new List<TokenSlotUI>();

        public void SetPlayer(PlayerSetupData player, int tokenCount)
        {
            if (nameText != null)
            {
                nameText.text = player.Nickname;
                nameText.color = PlayerColorPalette.Colors[player.ColorIndex];
            }
            if (rankText != null)
                rankText.text = string.Empty;
            SpawnSlots(tokenCount);
        }

        // onComplete (optional): fired once every slot in this row has finished flipping
        // (immediately, if there's nothing to animate) — TurnOrderPopupUI waits on this across
        // every row before revealing rank/enabling Continue, so nobody sees the outcome before
        // the tokens actually land (per the user's own request, 2026-08-24).
        public void ShowRoll(TokenRollResult roll, System.Action onComplete = null)
        {
            if (roll == null)
            {
                onComplete?.Invoke();
                return;
            }

            // index/count so the whole row's tokens land one after another but the ROW as a whole
            // still finishes in TokenSlotUI's fixed full-roll duration regardless of tokens count.
            int count = Mathf.Min(_tokenSlots.Count, roll.Tokens.Length);
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
                _tokenSlots[i].PlayRoll(roll.Tokens[i], i, count, SlotDone);
        }

        public void ShowRank(int rank)
        {
            if (rankText != null)
                rankText.text = $"#{rank}";
        }

        private void SpawnSlots(int count)
        {
            UIUtility.DestroyAndClear(_tokenSlots);

            if (tokenContainer == null || tokenSlotPrefab == null)
                return;

            for (int i = 0; i < count; i++)
            {
                TokenSlotUI slot = Instantiate(tokenSlotPrefab, tokenContainer);
                Game.Audio.SceneUIAudioBinder.BindCreatedRoot(slot);
                _tokenSlots.Add(slot);
            }
        }
    }
}
