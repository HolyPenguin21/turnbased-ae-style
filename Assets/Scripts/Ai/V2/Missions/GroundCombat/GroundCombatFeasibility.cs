using System.Collections.Generic;
using Game.Combat;

namespace Game.Ai.V2
{
    // ARCH-02 §29 — the raid combat-feasibility check, split out of GroundCombatAssemblyPlanner. Wraps the
    // shared WorthIt estimator: "does this attacker roster cover every defender AND clear the win
    // bar". Pure read; no plan construction, no objective value. Bodies verbatim.
    // ATK §29 — the defender's hex defence bonus is no longer hardcoded to zero here. It is a real
    // property of the FIGHT (WorthIt folds it into every defending unit's own Defense — see
    // WorthIt.Estimate), and for an assault on a Base/Citadel it is the single largest term the
    // estimator was previously blind to. Every overload below funnels into the one implementation,
    // so there is still exactly one ground-combat feasibility owner; the bonus simply became an
    // explicit caller-supplied constraint instead of a silent 0f. Callers that genuinely fight on
    // open ground keep passing nothing and behave bit-for-bit as before.
    internal static class GroundCombatFeasibility
    {
        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out float win, out bool cover) =>
            Clears(attackers, defenders, AiConfigV2.raidMinViableWinChance, 0f, out win, out cover);

        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float minWinChance,
            out float win, out bool cover) =>
            Clears(attackers, defenders, minWinChance, 0f, out win, out cover);

        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float minWinChance,
            float defenderHexDefenseBonus, out float win, out bool cover)
        {
            // Perf pre-filter — see AiConfigV2.raidPowerRatioPreFilter for the calibration this
            // cutoff is based on. Skips the 25-trial Monte-Carlo WinChance entirely for a matchup
            // whose raw aggregate power ratio is already far below anything that has ever cleared
            // raidMinViableWinChance; everything else still runs the real estimator unchanged.
            if (defenders.Count > 0)
            {
                float attackerPower = PowerSum(attackers);
                // The bonus WorthIt will actually add to every defending unit's Defense has to be
                // in the pre-filter's defender power too, or this cheap cutoff would answer a
                // different matchup than the estimator it stands in for.
                float defenderPower = PowerSum(defenders)
                    + System.Math.Max(0f, defenderHexDefenseBonus) * defenders.Count;
                if (defenderPower > 0f
                    && attackerPower / defenderPower < AiConfigV2.raidPowerRatioPreFilter)
                {
                    win = 0f;
                    cover = false;
                    return false;
                }
            }

            cover = WorthIt.CanDamageAll(attackers, defenders, defenderHexDefenseBonus);
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, defenderHexDefenseBonus);
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
