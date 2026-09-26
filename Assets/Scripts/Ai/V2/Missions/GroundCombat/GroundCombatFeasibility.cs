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
        // THE ground-combat feasibility check: does this attacker (roster + its commander) cover
        // every known defending body and clear `minWinChance` against the opposition — every
        // defending army its own battle, strongest first, with its own commander
        // (WorthIt.EstimateSequential). A single-army opposition is one ordinary battle.
        internal static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            WorthIt.SideCommander attackerCommander, IReadOnlyList<WorthIt.DefendingArmy> opposition,
            float minWinChance, float defenderHexDefenseBonus, out float win, out bool cover)
        {
            List<WorthIt.DefenderProfile> defenders = WorthIt.UnitsOf(opposition);
            // Perf pre-filter — see AiConfigV2.raidPowerRatioPreFilter for the calibration this
            // cutoff is based on. Skips the Monte-Carlo estimate entirely for a matchup whose raw
            // aggregate power ratio is already far below anything that has ever cleared
            // raidMinViableWinChance; everything else still runs the real estimator unchanged.
            // The calibration only holds for a gate that high: a lower one (Attack's floor) always
            // runs the estimator.
            if (defenders.Count > 0 && minWinChance >= AiConfigV2.raidMinViableWinChance)
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
                : WorthIt.EstimateSequential(attackers, attackerCommander, opposition,
                    defenderHexDefenseBonus).WinChance;
            return cover && win >= minWinChance;
        }

        // THE numeric "power needed to clear these defenders" every Aggression lane sizes a
        // shortage by: the defenders' effective power with `defenderHexDefenseBonus` folded into
        // their Defense (the same reading Clears gives the estimator) times the shared margin.
        // Callers pass the SAME bonus they pass Clears — 0f for a field battle, the known site
        // defence for an assault on a structure — so a power figure never answers a different
        // fight than the estimator it stands in for.
        internal static float RequiredPower(IReadOnlyList<WorthIt.DefenderProfile> defenders,
            float defenderHexDefenseBonus) =>
            System.Math.Max(1f, AiPower.EffectiveArmyPowerFromProfiles(defenders,
                System.Math.Max(0f, defenderHexDefenseBonus)) * AiConfigV2.raidCombatPowerMargin);

        private static float PowerSum(IReadOnlyList<WorthIt.DefenderProfile> profiles)
        {
            float sum = 0f;
            if (profiles == null)
                return sum;
            for (int i = 0; i < profiles.Count; i++)
            {
                WorthIt.DefenderProfile p = profiles[i];
                sum += WorthIt.CombatValue(p);
            }
            return sum;
        }

    }
}
