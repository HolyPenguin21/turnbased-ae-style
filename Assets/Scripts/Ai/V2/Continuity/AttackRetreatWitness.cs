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
    // Continuity retreat edge and read by the Attack planner; the planner's re-check reports its
    // revision, applied after the portfolio build. No manager, no second bank.
    internal sealed class AttackRetreatWitness
    {
        public AttackTargetRef Target;
        public int OwnArmyId;
        public int EnemyArmyId;
        public HexCoord EnemyHex;
        // Fingerprints of the whole contact (including observed Fate) and our roster, as observed
        // when we withdrew (refreshed whenever the fight is re-evaluated and still unwinnable).
        public int EnemyFingerprint;
        public int OwnFingerprint;
        public int Turn;
        public string Reason;

        internal static int OwnFingerprintOf(ArmySnapshot army) =>
            army == null ? 0 : AttackTacticalOpportunity.CombatFingerprint(army.Members) * 31
                + AttackTacticalOpportunity.CommanderFingerprint(army.Commander);

        // Does a withdrawal witness still forbid a fresh march on `target` with `army`? Re-asks the
        // shared estimator when either side's observed inputs changed. A pure read: the change the
        // answer implies for the witness (cleared when the enemy is gone from knowledge or the fight
        // now clears the voluntary-fight bar, refreshed while it stays unwinnable) is returned in
        // `revision`, and the caller hands it to Apply after planning.
        internal static bool Blocks(IMissionIntentView state, WorldSnapshot snap, AttackTargetRef target,
            ArmySnapshot army, out string why, out RetreatWitnessRevision revision)
        {
            why = null;
            revision = default;
            if (state == null || snap?.Known == null || !target.HasValue
                || !state.TryGetRetreatWitness(target, out AttackRetreatWitness w))
                return false;
            AiMapMemory.KnownEnemySighting? enemy = null;
            foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings
                ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                if (s.ArmyId == w.EnemyArmyId) { enemy = s; break; }
            if (!enemy.HasValue)
            {
                revision = RetreatWitnessRevision.Clear(target,
                    $"enemy #{w.EnemyArmyId} is no longer known");
                return false;
            }
            List<WorthIt.DefendingArmy> opposition = AttackTacticalOpportunity.OppositionOn(snap, enemy.Value.Hex);
            int enemyFp = AttackTacticalOpportunity.ContactFingerprint(opposition);
            int ownFp = OwnFingerprintOf(army);
            if (enemyFp == w.EnemyFingerprint && ownFp == w.OwnFingerprint)
            {
                why = "unchanged_obstacle";
                return true;
            }
            // Something changed: the answer comes from the estimator, not from the change itself.
            float bonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, snap.Map, enemy.Value.Hex);
            AttackTacticalOpportunity.ClearsContact(army.Members.ToList(), army.Commander, opposition,
                bonus, out float win, out _);
            bool clears = win >= GroundCombatAdmissionPolicy.AttackLocalWinChanceGate;
            if (clears)
            {
                revision = RetreatWitnessRevision.Clear(target,
                    $"win vs #{w.EnemyArmyId} is now {win:0.00} (fingerprint changed)");
                return false;
            }
            revision = RetreatWitnessRevision.Refresh(target, enemyFp, ownFp, enemy.Value.Hex);
            why = $"still_unwinnable win={win:0.00}";
            return true;
        }

        // The one writer of the planner's witness answers (Continuity owns the witness).
        internal static void Apply(MissionIntentState state, IEnumerable<RetreatWitnessRevision> revisions)
        {
            if (state == null || revisions == null)
                return;
            foreach (RetreatWitnessRevision r in revisions)
                Apply(state, r);
        }

        internal static void Apply(MissionIntentState state, RetreatWitnessRevision r)
        {
            if (state == null || !r.Target.HasValue)
                return;
            if (r.Kind == RetreatWitnessRevisionKind.Clear)
            {
                if (!state.TryGetRetreatWitness(r.Target, out _))
                    return;
                state.ClearRetreatWitness(r.Target);
                AiDebugLog.Write($"[AI][V2][Attack][Retreat] witness for {r.Target.DiagnosticLabel} cleared: "
                    + r.Reason);
            }
            else if (r.Kind == RetreatWitnessRevisionKind.Refresh
                && state.TryGetRetreatWitness(r.Target, out AttackRetreatWitness w))
            {
                w.EnemyFingerprint = r.EnemyFingerprint;
                w.OwnFingerprint = r.OwnFingerprint;
                w.EnemyHex = r.EnemyHex;
            }
        }
    }

    internal enum RetreatWitnessRevisionKind { None, Clear, Refresh }

    // What a witness check found should change in the witness: the planner reports it, Continuity
    // applies it (AttackRetreatWitness.Apply).
    internal readonly struct RetreatWitnessRevision
    {
        internal readonly AttackTargetRef Target;
        internal readonly RetreatWitnessRevisionKind Kind;
        internal readonly int EnemyFingerprint;
        internal readonly int OwnFingerprint;
        internal readonly HexCoord EnemyHex;
        internal readonly string Reason;

        private RetreatWitnessRevision(AttackTargetRef target, RetreatWitnessRevisionKind kind,
            int enemyFingerprint, int ownFingerprint, HexCoord enemyHex, string reason)
        {
            Target = target;
            Kind = kind;
            EnemyFingerprint = enemyFingerprint;
            OwnFingerprint = ownFingerprint;
            EnemyHex = enemyHex;
            Reason = reason;
        }

        internal static RetreatWitnessRevision Clear(AttackTargetRef target, string reason) =>
            new RetreatWitnessRevision(target, RetreatWitnessRevisionKind.Clear, 0, 0, default, reason);

        internal static RetreatWitnessRevision Refresh(AttackTargetRef target, int enemyFingerprint,
            int ownFingerprint, HexCoord enemyHex) =>
            new RetreatWitnessRevision(target, RetreatWitnessRevisionKind.Refresh, enemyFingerprint,
                ownFingerprint, enemyHex, null);
    }
}
