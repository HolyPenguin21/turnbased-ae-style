using System.Collections.Generic;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Turns
{
    // Placeholder decision-making for AI initiative-dice purchases — picks a random amount
    // (0-2) with no regard for resource cost yet, just to prove the buy-then-roll pipeline
    // works end to end before any real AI economy logic exists. Runs before the turn-order
    // popup is shown, so an AI's purchase is already reflected the moment the player sees it,
    // not decided live while they watch.
    public static class InitiativeDiceAI
    {
        private const int MinDice = 0;
        private const int MaxDice = 2; // inclusive

        public static void BuyDiceForAll(List<PlayerSetupData> players)
        {
            if (players == null)
                return;

            foreach (PlayerSetupData player in players)
            {
                if (player == null || player.IsHuman)
                    continue;

                PlayerRoot root = PlayerRootRegistry.FindFor(player);
                if (root == null)
                    continue;

                int amount = Random.Range(MinDice, MaxDice + 1);
                root.AddBonusInitiativeDice(amount);
            }
        }
    }
}
