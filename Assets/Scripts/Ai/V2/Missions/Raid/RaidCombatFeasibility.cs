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
            cover = ProfilesCoverAll(attackers, defenders);
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            return cover && win >= minWinChance;
        }

        private static bool ProfilesCoverAll(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders)
        {
            if (defenders == null || defenders.Count == 0) return true;
            if (attackers == null || attackers.Count == 0) return false;
            foreach (WorthIt.DefenderProfile def in defenders)
            {
                bool covered = false;
                foreach (WorthIt.DefenderProfile atk in attackers)
                    if (WorthIt.CanDamage(atk.Attack, def, 0f)) { covered = true; break; }
                if (!covered) return false;
            }
            return true;
        }
    }
}
