using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §24/§25/§47/§61/§70/§71 — ATTACK CONTINUITY.
    //
    //  A mechanical partial of MissionContinuityLayer, not a second continuity owner: intents still
    //  live in the one MissionIntentState, are keyed by the one MissionIntentKey, and are reaped by
    //  the same rules. This file holds only the Attack lane's own lifecycle answers.
    // ===========================================================================================
    internal static partial class MissionContinuityLayer
    {
        // Returns false when the intent must be retired. `success` distinguishes "we took the
        // Base" (§8: the army STAYS there, mission claim released, Housekeeping stabilises) from
        // "this operation is over for another reason".
        internal static bool ResolveAttackIntent(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, AttackIntent a, out bool success)
        {
            success = false;
            if (a == null || !a.Target.HasValue)
            {
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — no attack objective");
                return false;
            }

            // ---- §25/§61 target status, from honest knowledge only -----------------------------
            AttackObjectiveEvaluator.AttackTargetStatus status =
                AttackObjectiveEvaluator.EvaluateTarget(snap, a.Target);
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Captured)
            {
                success = true;
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} COMPLETE — "
                    + $"{a.Target.DiagnosticLabel} captured; army #{a.PrimaryArmyId} stays in place, "
                    + "claim released for Housekeeping");
                return false;
            }
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Invalidated)
            {
                // §7/§25 — a new owner on the same hex is a NEW objective, never a silent retarget
                // of this operation. Retire and let the next pass enumerate it fresh.
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — "
                    + $"{a.Target.DiagnosticLabel} is no longer a hostile Attack structure under its "
                    + "expected owner");
                return false;
            }

            // ---- the primary must still exist as a real ground force ---------------------------
            if (a.Phase != AttackMissionPhase.SupportReturn)
            {
                if (!a.PrimaryArmyId.HasValue
                    || !RaidPrimaryActorAlive(snap, a.PrimaryArmyId.Value))
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — primary "
                        + $"#{a.PrimaryArmyId} is no longer a structural ground actor in phase {a.Phase}");
                    return false;
                }
            }

            bool supportLostThisPass = false;
            if (a.SupportArmyId.HasValue
                && (a.Phase == AttackMissionPhase.Reinforcement
                    || a.Phase == AttackMissionPhase.SupportReturn)
                && !RaidSupportActorAlive(snap, a.SupportArmyId.Value))
            {
                int lostSupportId = a.SupportArmyId.Value;
                a.SupportArmyId = null;
                a.SupportReturnHex = null;
                a.ReinforcementRequestedTurn = -1;
                supportLostThisPass = true;
                if (a.Phase == AttackMissionPhase.SupportReturn)
                    a.Phase = AttackMissionPhase.Assault;
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} support #{lostSupportId} "
                    + $"lost; binding released, phase={a.Phase}");
            }

            // ---- walking-home legs: arrival is the end of the leg ------------------------------
            if (a.Phase == AttackMissionPhase.SupportReturn)
            {
                if (!a.SupportArmyId.HasValue)
                {
                    a.SupportReturnHex = null;
                    a.Phase = AttackMissionPhase.Assault;
                    return true;
                }
                // The leg was entered by an execution fact (a full/full swap); THIS is where the
                // destination gets chosen, through the one own-Base selection owner.
                if (!a.SupportReturnHex.HasValue)
                    a.SupportReturnHex = SelectReturnBase(snap, player, a.SupportArmyId);
                ArmySnapshot support = snap?.Self?.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == a.SupportArmyId.Value);
                // No own base to send it to must never wedge the operation (same rule as below).
                if (support == null || !a.SupportReturnHex.HasValue
                    || support.Hex.Equals(a.SupportReturnHex.Value))
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} support "
                        + $"#{a.SupportArmyId} released after SupportReturn");
                    a.SupportArmyId = null;
                    a.SupportReturnHex = null;
                    a.Phase = AttackMissionPhase.Assault;
                }
                else if (!ReturnBaseStillValid(snap, player, a.SupportArmyId, a.SupportReturnHex))
                {
                    HexCoord? replacement = SelectReturnBase(snap, player, a.SupportArmyId);
                    if (replacement == null)
                    {
                        // No home for the support must never wedge the operation: release it and
                        // let the primary carry on being re-evaluated.
                        AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} support "
                            + $"#{a.SupportArmyId} has no reachable home base — released");
                        a.SupportArmyId = null;
                        a.SupportReturnHex = null;
                        a.Phase = AttackMissionPhase.Assault;
                    }
                    else
                    {
                        a.SupportReturnHex = replacement;
                        intent.StallTurns = 0;
                    }
                }
                return true;
            }

            if (a.Phase == AttackMissionPhase.RecoveryReturn)
            {
                if (!a.RecoveryBaseHex.HasValue
                    || !ReturnBaseStillValid(snap, player, a.PrimaryArmyId, a.RecoveryBaseHex))
                {
                    // §47 — best reachable OWN base, chosen by the one owner. Never a hardcoded
                    // starting Citadel.
                    HexCoord? replacement = SelectReturnBase(snap, player, a.PrimaryArmyId);
                    if (replacement == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — "
                            + "recovery base lost and no replacement own base exists");
                        return false;
                    }
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} recovery base retargeted "
                        + $"to ({replacement.Value.Q},{replacement.Value.R})");
                    a.RecoveryBaseHex = replacement;
                    intent.StallTurns = 0;
                }
                ArmySnapshot recovering = snap?.Self?.Armies?.FirstOrDefault(x => x != null
                    && a.PrimaryArmyId.HasValue && x.ArmyId == a.PrimaryArmyId.Value);
                if (recovering != null && a.RecoveryBaseHex.HasValue
                    && recovering.Hex.Equals(a.RecoveryBaseHex.Value))
                {
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — primary "
                        + $"#{a.PrimaryArmyId} reached its recovery base; the operation is over and "
                        + "the army is free for a fresh decision");
                    return false;
                }
                return true;
            }

            // ---- §24 the Assault / Reinforcement decision --------------------------------------
            bool clears = AttackPrimaryClearsTarget(snap, a);
            if (clears)
            {
                if (a.Phase != AttackMissionPhase.Assault)
                    AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase "
                        + $"{a.Phase} -> Assault (primary #{a.PrimaryArmyId} clears the site)");
                a.Phase = AttackMissionPhase.Assault;
                a.ReinforcementRequestedTurn = -1;
                return true;
            }

            // The primary cannot take the site. Reinforcement is the answer while there is any
            // prospect of one; only a started operation with no prospect withdraws (§47).
            if (a.Phase != AttackMissionPhase.Reinforcement)
            {
                a.Phase = AttackMissionPhase.Reinforcement;
                a.ReinforcementRequestedTurn = -1;
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase -> Reinforcement "
                    + $"(primary #{a.PrimaryArmyId} does not clear {a.Target.DiagnosticLabel})");
                return true;
            }

            bool reinforcementPossible = a.SupportArmyId.HasValue
                || AttackSupportCandidateExists(snap, a);
            if (reinforcementPossible)
                return true;

            // A vanished support is fresh shortage evidence. Keep this operation in
            // Reinforcement for the rest of the current settle pass so Demand can re-emit the
            // existing FieldCombatPower request. On the next reconciliation, if neither delivery
            // nor an existing support candidate appeared, the ordinary RecoveryReturn fallback
            // below remains authoritative.
            if (supportLostThisPass)
                return true;

            if (!a.OperationStarted)
            {
                // Nothing has been spent physically yet — this was simply not a viable objective.
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — not viable and the "
                    + "operation never started");
                return false;
            }

            HexCoord? recovery = SelectReturnBase(snap, player, a.PrimaryArmyId);
            if (recovery == null)
            {
                AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} retired — no reinforcement "
                    + "prospect and no own base to withdraw to");
                return false;
            }
            a.Phase = AttackMissionPhase.RecoveryReturn;
            a.RecoveryBaseHex = recovery;
            a.SupportArmyId = null;
            AiDebugLog.Write($"[AI][V2][Attack] {intent.IntentKey} phase -> RecoveryReturn "
                + $"({recovery.Value.Q},{recovery.Value.R}); operation no longer viable");
            return true;
        }

        // Does the bound primary, on its own, still clear the target site? The SAME shared estimator
        // and the SAME honest hex-defence read the mission layer used, at the bounded continuation
        // floor a started operation is entitled to.
        private static bool AttackPrimaryClearsTarget(WorldSnapshot snap, AttackIntent a)
        {
            if (!a.PrimaryArmyId.HasValue)
                return false;
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                AttackObjectiveEvaluator.KnownSiteDefenders(snap, a.Target.Hex);
            // Continuity is snapshot-pure and has no map, so terrain is not in this read; the
            // remembered structural defence still is. The mission/provisioning layers, which do have
            // the map, apply the full bonus before anything is actually funded or executed.
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                snap, defenders, a.PrimaryArmyId.Value,
                a.OperationStarted
                    ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                    : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                hexBonus);
            if (plan.Feasible)
            {
                a.ProjectedWinChance = plan.ProjectedWinChance;
                a.CoversAllDefenders = plan.CoversAllDefenders;
            }
            return plan.Feasible;
        }

        // §41/§46 — is there any EXISTING free army whose merge would improve the primary's odds?
        // The shared kernel answers it; a "no" here is what makes the shortage a real Demand.
        private static bool AttackSupportCandidateExists(WorldSnapshot snap, AttackIntent a)
        {
            if (!a.PrimaryArmyId.HasValue)
                return false;
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                AttackObjectiveEvaluator.KnownSiteDefenders(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, a.Target.Hex);
            return GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(snap,
                a.PrimaryArmyId.Value, defenders, null, hexBonus).Count > 0;
        }

        // §70 — a durable intent is created only once the operation has REALLY begun (a step taken
        // or a battle fought), never on a bare candidate enumeration.
        internal static void CreateAttackIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            AttackMissionTarget t = o.AttackTarget;
            var payload = new AttackIntent
            {
                Target = t.Target,
                Phase = t.Phase,
                OperationStarted = true,
                PrimaryArmyId = t.PrimaryArmyId ?? o.MoverArmyId,
                SupportArmyId = t.SupportArmyId,
                RecoveryBaseHex = t.RecoveryBaseHex,
                SupportReturnHex = t.SupportReturnHex,
                ProjectedWinChance = t.ProjectedWinChance,
                CoversAllDefenders = t.CoversAllDefenders,
                // §17 — the operation may well have BEGUN with its side strike, in which case the
                // intent is born having already spent this turn's one diversion. Anything else
                // would let the very first turn take two.
                LastOpportunisticStrikeTurn = o.AttackOpportunisticStrike
                    ? turn : t.OpportunisticStrikeTurn,
            };
            MissionIntent intent = NewIntent(o, turn, MissionKind.Attack, CommitmentTier.Hard, payload);
            RetireCompletedRaidFallbackForActor(state, payload.PrimaryArmyId,
                "fresh Attack admitted");
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Attack] continuity — "
                + $"[{AiV2Trace.FormatCorrelation(o.Proposal)}] {intent.IntentKey} created "
                + $"(Hard attack on {t.Target.DiagnosticLabel}, primary #{payload.PrimaryArmyId})");
        }

        // §71 — a started operation is NOT re-pointed at a slightly better-scoring target. This is
        // the only place Attack applies continuity protection, and it is a statement about identity,
        // not a score comparison: while the bound target is still valid and the operation is still
        // viable, a fresh candidate simply competes for a DIFFERENT intent next time this one ends.
        internal static bool AttackIntentIsProtected(MissionIntent intent) =>
            intent?.Attack != null && intent.Attack.OperationStarted
            && intent.Status == IntentStatus.Active;
    }
}
