using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  FORCE BASELINE REGISTRY  (strike force step 3 — P_start)
    // ===========================================================================================
    //  Player memory (cache class C): the player's whole-game force ceiling
    //  (SelfSnapshot.TotalMilitaryPotential, P_deck) as it stood on the first V2 turn. Later
    //  turns compare P_deck against it to see how much of the starting force is gone.
    //
    //  Written once per player by the pipeline right after the turn's scan; Analysis only reads
    //  it. Cleared with the other V2 registries in CitadelSetupController.
    // ===========================================================================================
    public static class ForceBaselineRegistry
    {
        private static readonly Dictionary<PlayerSetupData, float> StartPotential =
            new Dictionary<PlayerSetupData, float>();

        // The first recorded ceiling wins; later calls are no-ops.
        public static void RecordStart(PlayerSetupData player, float totalMilitaryPotential)
        {
            if (player != null && !StartPotential.ContainsKey(player))
                StartPotential[player] = totalMilitaryPotential;
        }

        public static bool TryGetStart(PlayerSetupData player, out float startPotential)
        {
            startPotential = 0f;
            return player != null && StartPotential.TryGetValue(player, out startPotential);
        }

        public static void Clear() => StartPotential.Clear();
    }
}
