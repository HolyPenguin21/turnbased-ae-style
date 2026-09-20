using System.Collections.Generic;
using Game.Combat;

namespace Game.Ai.V2
{
    // ARCH-02 §29 — the raid combat-feasibility check, split out of GroundCombatAssemblyPlanner. Wraps the
    // shared WorthIt estimator: "does this attacker roster cover every defender AND clear the win
    // bar". Pure read; no plan construction, no objective value. Bodies verbatim.
    internal static class GroundCombatFeasibility
    {
        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out float win, out bool cover) =>
            Clears(attackers, defenders, AiConfigV2.raidMinViableWinChance, out win, out cover);

        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float minWinChance,
            out float win, out bool cover)
        {
            // Perf pre-filter — see AiConfigV2.raidPowerRatioPreFilter for the calibration this
            // cutoff is based on. Skips the 25-trial Monte-Carlo WinChance entirely for a matchup
            // whose raw aggregate power ratio is already far below anything that has ever cleared
            // raidMinViableWinChance; everything else still runs the real estimator unchanged.
            if (defenders.Count > 0)
            {
                float attackerPower = PowerSum(attackers);
                float defenderPower = PowerSum(defenders);
                if (defenderPower > 0f
                    && attackerPower / defenderPower < AiConfigV2.raidPowerRatioPreFilter)
                {
                    win = 0f;
                    cover = false;
                    return false;
                }
            }

            cover = WorthIt.CanDamageAll(attackers, defenders);
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            return cover && win >= minWinChance;
        }

        private static float PowerSum(IReadOnlyList<WorthIt.DefenderProfile> profiles)
        {
            float sum = 0f;
            if (profiles == null)
                return sum;
            for (int i = 0; i < profiles.Count; i++)
            {
                WorthIt.DefenderProfile p = profiles[i];
                sum += p.Attack + p.Defense + p.HitPoints + 0.25f * p.Initiative;
            }
            return sum;
        }

    }
}
