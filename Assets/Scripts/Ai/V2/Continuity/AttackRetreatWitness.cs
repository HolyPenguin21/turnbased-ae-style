using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // 2026-10-08 — the minimal fact that keeps an Attack that withdrew from an unwinnable hostile
    // army from marching straight back out against the same unchanged obstacle. A fixed-duration
    // cooldown cannot say "wait for reinforcement": time passes, the enemy stays. This record names
    // WHAT we withdrew from (the enemy army and the observed inputs of that fight) and WHAT we had
    // (our own combat inputs); a new march is allowed only after the shared estimator, asked again
    // on the facts now known, no longer says "below the voluntary-fight bar" — never because a turn
    // number, AP or a timer changed.
    //
    // Plain domain state of MissionIntentState (one record per Attack target), written by the
    // Continuity retreat edge and read by the Attack planner. No manager, no second bank.
    internal sealed class AttackRetreatWitness
    {
        public AttackTargetRef Target;
        public int OwnArmyId;
        public int EnemyArmyId;
        public HexCoord EnemyHex;
        // CombatFingerprint of the whole package on the enemy's hex, and of our roster, as observed
        // when we withdrew (refreshed whenever the fight is re-evaluated and still unwinnable).
        public int EnemyFingerprint;
        public int OwnFingerprint;
        public int Turn;
        public string Reason;

        internal static int OwnFingerprintOf(ArmySnapshot army) =>
            army == null ? 0 : AttackTacticalOpportunity.CombatFingerprint(army.Members) * 31
                + (army.Commander.Present ? army.Commander.Initiative * 7 + army.Commander.Fate : 0);

        // Does a withdrawal witness still forbid a fresh march on `target` with `army`? Re-asks the
        // shared estimator when either side's observed inputs changed; clears the witness when the
        // enemy is gone from knowledge or the fight now clears the voluntary-fight bar.
        internal static bool Blocks(MissionIntentState state, WorldSnapshot snap, AttackTargetRef target,
            ArmySnapshot army, out string why)
        {
            why = null;
            if (state == null || snap?.Known == null || !target.HasValue
                || !state.TryGetRetreatWitness(target, out AttackRetreatWitness w))
                return false;
            AiMapMemory.KnownEnemySighting? enemy = null;
            foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings
                ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                if (s.ArmyId == w.EnemyArmyId) { enemy = s; break; }
            if (!enemy.HasValue)
            {
                state.ClearRetreatWitness(target);
                AiDebugLog.Write($"[AI][V2][Attack][Retreat] witness for {target.DiagnosticLabel} cleared: "
                    + $"enemy #{w.EnemyArmyId} is no longer known");
                return false;
            }
            List<WorthIt.DefendingArmy> opposition = AttackTacticalOpportunity.OppositionOn(snap, enemy.Value.Hex);
            int enemyFp = AttackTacticalOpportunity.CombatFingerprint(WorthIt.UnitsOf(opposition));
            int ownFp = OwnFingerprintOf(army);
            if (enemyFp == w.EnemyFingerprint && ownFp == w.OwnFingerprint)
            {
                why = "unchanged_obstacle";
                return true;
            }
            // Something changed: the answer comes from the estimator, not from the change itself.
            float bonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, snap.Map, enemy.Value.Hex);
            bool clears = GroundCombatFeasibility.Clears(army.Members.ToList(), army.Commander, opposition,
                GroundCombatAdmissionPolicy.AttackLocalWinChanceGate, bonus, out float win, out _,
                requireCoverage: false);
            if (clears)
            {
                state.ClearRetreatWitness(target);
                AiDebugLog.Write($"[AI][V2][Attack][Retreat] witness for {target.DiagnosticLabel} cleared: "
                    + $"win vs #{w.EnemyArmyId} is now {win:0.00} (fingerprint changed)");
                return false;
            }
            w.EnemyFingerprint = enemyFp;
            w.OwnFingerprint = ownFp;
            w.EnemyHex = enemy.Value.Hex;
            why = $"still_unwinnable win={win:0.00}";
            return true;
        }
    }
}
