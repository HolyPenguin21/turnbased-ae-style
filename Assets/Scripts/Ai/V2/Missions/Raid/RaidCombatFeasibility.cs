using System.Collections.Generic;
using Game.Combat;

namespace Game.Ai.V2
{
    // ARCH-02 §29 — the raid combat-feasibility check, split out of RaidAssemblyPlanner. Wraps the
    // shared WorthIt estimator: "does this attacker roster cover every defender AND clear the win
    // bar". Pure read; no plan construction, no objective value. Bodies verbatim.
    internal static class RaidCombatFeasibility
    {
        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out float win, out bool cover) =>
            Clears(attackers, defenders, AiConfigV2.raidMinViableWinChance, out win, out cover);

        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float minWinChance,
            out float win, out bool cover)
        {
            cover = WorthIt.CanDamageAll(attackers, defenders);
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            return cover && win >= minWinChance;
        }

    }
}
