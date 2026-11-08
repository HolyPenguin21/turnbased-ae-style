using Game.HexGrid;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  2026-11-08 (owner decision) — WHERE A RAID LEAVES ITS ARMY.
    //
    //  A won Raid leaves the formation on the raid hex until its next turn (the Return leg starts
    //  only then). The fight with the neutral is not the whole risk: a known hostile player army
    //  that reaches that hex within raidPostFightExposureMaxEnemyEta turns (1: before our next
    //  move) and beats the formation would take the raid's army — a hero included — for the
    //  raid's reward. The rule is the ImmediateOpportunity one
    //  (ActiveDefenceObjectiveEvaluator.LeavesForceExposed: honest contacts, the threat model's
    //  approach cost, can-damage, win above 1 - activeDefenceHoldWinChance) with the Raid's own
    //  horizon. One predicate, used as AcceptPlan by every Raid stage that picks a roster —
    //  readiness (Demand), the candidate score, the actor admission set and the live assault
    //  transaction — so a stronger host that holds the hex is still found, and a Raid with none
    //  waits for the hostile army to leave instead of buying power it cannot use safely.
    // ===========================================================================================
    internal static class RaidPostFightPolicy
    {
        internal static bool Accepts(WorldSnapshot snap, HexCoord raidHex, GroundCombatAssemblyPlan plan)
        {
            if (snap?.Self?.Armies == null || plan?.Feasible != true)
                return true;
            ArmySnapshot formation = GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan)
                .TryGetValue(plan.BaseArmyId, out ArmySnapshot projected) ? projected
                : System.Linq.Enumerable.FirstOrDefault(snap.Self.Armies, a => a != null && a.ArmyId == plan.BaseArmyId);
            if (formation == null
                || !ActiveDefenceObjectiveEvaluator.LeavesForceExposed(snap, formation.Members,
                    formation.Commander, raidHex, -1, out int exposerId, out float exposerWin,
                    AiConfigV2.raidPostFightExposureMaxEnemyEta))
                return true;
            AiDebugLog.WriteDeduped($"raid-exposure#{snap.Observer?.ColorIndex}#{raidHex}#{plan.BaseArmyId}",
                $"[AI][V2][Raid] decision=REJECT_HOST actor=#{plan.BaseArmyId} raidHex=({raidHex.Q},{raidHex.R}) "
                + $"reason=post_fight_exposed enemy=#{exposerId} win={exposerWin:0.00} "
                + $"(reaches within {AiConfigV2.raidPostFightExposureMaxEnemyEta} turn)");
            return false;
        }

        internal static System.Func<GroundCombatAssemblyPlan, bool> AcceptFor(WorldSnapshot snap,
            RaidMissionTarget target) =>
            candidate => Accepts(snap, target.LastKnownHex, candidate);
    }
}
