using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  IMMEDIATE-STRIKE LOAN (2026-11-19, owner; log 2026-11-19 Halden T7-T12)
    // ===========================================================================================
    //  An army walking a routine return leg (LifecycleReturnPolicy.IsRoutineReturn: an Economy
    //  builder going home after its build, a Raid or Attack recovery walk) is claimed by that leg,
    //  so ActiveDefence's ImmediateOpportunity never saw it: Halden's escort (hero + Coil
    //  Marksman) built (-6,5), walked home past a visible 4.7-power scout and the scout razed the
    //  site three turns later. Owner rule: after the build (turn lost) the army strikes a weak
    //  threat it can reach this turn (turn lost), then goes on — strike again, take a new build
    //  (Economy may take a ReturnBuilder), or walk home.
    //
    //  The leg LENDS its only army for the turn: when ActiveDefenceObjectiveEvaluator
    //  .ImmediateStrikeWith proves that this army alone can START an immediate fight now (the
    //  planner's own AssessImmediate with the army pinned), the leg is suspended (StrikeLoan) and
    //  leaves the active set, so the same admission sees a free army and ActiveDefence owns the
    //  fight. The leg resumes as soon as no immediate strike holds the army (after the fight,
    //  the next turn, or this turn when the planner chose another defender); an army any other
    //  operation took meanwhile retires the leg. A leg is lent at most once per turn, so a strike
    //  the planner did not fund never flips the leg back and forth within the turn.
    //  The same loan serves an outbound build whose own site an enemy now stands on
    //  (EconomyContinuity): strike first, build on the next turn.
    // ===========================================================================================
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsStrikeLoan(MissionIntent intent) =>
            intent != null && intent.Status == IntentStatus.Suspended
            && intent.Suspended == SuspendReason.StrikeLoan;

        // A leg that owns exactly one army: no convoy, gather member or air wing rides on it.
        private static bool HoldsOnlyItsMover(MissionIntent intent) =>
            intent?.PreferredMoverArmyId != null
            && !GroundCombatLegs.HeldGroundSupportArmyIds(intent).Any()
            && !GroundCombatLegs.HeldAirSupportArmyIds(intent).Any();

        // Does `intent` name `armyId` as one of its armies — its mover, its ActiveDefence primary or
        // a support/wing it holds (the claim sources of MissionActorPolicy, read without the live
        // validity check, so an army is never resumed under two owners)?
        private static bool NamesArmy(MissionIntent intent, int armyId) =>
            intent != null && (intent.PreferredMoverArmyId == armyId
                || intent.ActiveDefence?.PrimaryArmyId == armyId
                || GroundCombatLegs.HeldGroundSupportArmyIds(intent).Contains(armyId)
                || GroundCombatLegs.HeldAirSupportArmyIds(intent).Contains(armyId));

        // Lends `intent`'s army when it can start an immediate fight now against an objective
        // `accept` admits (null = any). Suspends the intent; the caller drops it from the pass.
        internal static bool TryLendToImmediateStrike(PlayerSetupData player, WorldSnapshot snap,
            MissionIntent intent, ActiveResolution pass, Func<ActiveDefenceObjective, bool> accept,
            string why)
        {
            if (player == null || snap?.Self?.Armies == null || !HoldsOnlyItsMover(intent)
                || intent.Status != IntentStatus.Active)
                return false;
            int armyId = intent.PreferredMoverArmyId.Value;
            int turn = snap.TurnNumber;
            if (LifecycleReturnPolicy.LentThisTurn(player, intent.IntentKey, turn))
                return false;
            ArmySnapshot army = snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == armyId);
            if (army == null || !army.IsStructuralRaidActor || army.CurrentMovement <= 0)
                return false;
            pass.ImmediateObjectives ??= ActiveDefenceObjectiveEvaluator.EnumerateImmediate(snap);
            if (pass.ImmediateObjectives.Count == 0)
                return false;
            List<MissionIntent> active = pass.State.All.Where(i => i != null
                && i.Status == IntentStatus.Active).ToList();
            ActiveDefenceObjective target = ActiveDefenceObjectiveEvaluator.ImmediateStrikeWith(snap,
                armyId, pass.ImmediateObjectives, pass.ActorClaims,
                ActiveDefenceObjectiveEvaluator.WithdrawingArmyIds(active), active, accept);
            if (target == null)
                return false;
            intent.Status = IntentStatus.Suspended;
            intent.Suspended = SuspendReason.StrikeLoan;
            LifecycleReturnPolicy.RecordStrikeLoan(player, intent.IntentKey, turn);
            pass.StrikeLoansThisPass.Add(intent.IntentKey);
            AiDebugLog.Write($"[AI][V2][StrikeLoan] lend intent={intent.IntentKey} actor=#{armyId} "
                + $"enemy={target.Target.EnemyArmyId.ToString(CultureInfo.InvariantCulture)}"
                + $"@({target.Target.LastKnownHex.Q},{target.Target.LastKnownHex.R}) reason={why}");
            return true;
        }

        // After the domain owners resolved the pass: settle the loans of earlier passes, then lend
        // the routine return legs that can strike now.
        internal static void SettleStrikeLoans(PlayerSetupData player, WorldSnapshot snap,
            ActiveResolution pass)
        {
            int turn = snap?.TurnNumber ?? 0;
            foreach (MissionIntent lent in pass.State.All.Where(i => IsStrikeLoan(i)
                && !pass.StrikeLoansThisPass.Contains(i.IntentKey)).ToList())
            {
                int armyId = lent.PreferredMoverArmyId ?? -1;
                MissionIntent holder = pass.Active.FirstOrDefault(i => i != lent && NamesArmy(i, armyId));
                // The strike of this turn still owns the army: the leg waits for it.
                if (holder?.ActiveDefence?.InterceptPurpose == ActiveDefenceInterceptPurpose.ImmediateOpportunity)
                    continue;
                if (holder != null)
                {
                    // Another operation (or a longer ActiveDefence task) took the army while it
                    // was lent: the walk is over, never a leg suspended for good.
                    AiDebugLog.Write($"[AI][V2][StrikeLoan] retire intent={lent.IntentKey} actor=#{armyId} "
                        + $"taken_by={holder.IntentKey}");
                    RetireOutcomeIntent(pass.State, lent, null, turn);
                    continue;
                }
                lent.Status = IntentStatus.Active;
                lent.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][StrikeLoan] resume intent={lent.IntentKey} actor=#{armyId}");
                ResolveDomainOperation(player, snap, lent, pass);
            }
            foreach (MissionIntent leg in pass.Active.Where(i => LifecycleReturnPolicy.IsRoutineReturn(i)
                && !pass.Dead.Contains(i.IntentKey)).ToList())
                if (TryLendToImmediateStrike(player, snap, leg, pass, null, "routine_return"))
                    pass.Active.Remove(leg);
        }
    }
}
