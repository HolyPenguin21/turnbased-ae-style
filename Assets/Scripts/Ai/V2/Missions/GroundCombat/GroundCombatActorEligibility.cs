using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ARCH-02 §29/§30/§59 — Raid actor eligibility. The structural predicate itself is a frozen
    // snapshot fact: WorldAnalysis computes ArmySnapshot.IsStructuralRaidActor once at scan time
    // (it may read the live domain), and every layer reads that field — no re-derivation from live
    // ArmyRegistry state, and no upward dependency from Analysis / State onto this Missions type.
    // What lives here is only the Missions-specific mover ORDERING for the assembly solver.
    internal static class GroundCombatActorEligibility
    {
        // Free, structurally-eligible ground combat armies for this cycle, mobility-first:
        // already-activated / cheaper activation first, then the least powerful sufficient host
        // (avoids feeding an already-winning raid into an ever larger, ever more expensive stack).
        //
        // CurrentMovement > 0 is a FRESH-admission gate only: this method is the enumeration source
        // for a new Assault/ActiveDefence candidate and for Reinforcement support candidates
        // (GroundCombatAssemblyPlanner.Plan's `eligible` list, ReinforcementSupportCandidates), so
        // a 0-MP army must never be nominated here — PrepareGroundCombatAssignments would assign it
        // and Provisioning would reject it as unfit in the same pass. A DURABLE incumbent's
        // continuation re-test (GroundCombatAssemblyPlanner.PlanForArmyAtThreshold, reached via
        // PlanForArmy / GroundCombatAdmissionRegistry's Hard-Raid continuation branch) resolves its
        // actor directly by ArmyId and never calls this method, so an already-owned multi-turn
        // mission still waits for MP next turn instead of losing its actor here.
        internal static List<ArmySnapshot> EligibleReadyArmies(WorldSnapshot snap, ISet<int> excludeArmyIds) =>
            EligibleArmies(snap, excludeArmyIds, requireMovementNow: true);

        // `requireMovementNow: false` is the capability view (Demand): an army whose MP is spent
        // this turn is still physical capability — it moves again next turn — so it must count as
        // temporary contention, never as a shortage to produce against. Never used to nominate.
        internal static List<ArmySnapshot> EligibleArmies(WorldSnapshot snap, ISet<int> excludeArmyIds,
            bool requireMovementNow) =>
            snap.Self.Armies
                .Where(a => a != null && a.IsStructuralRaidActor
                            && (!requireMovementNow || a.CurrentMovement > 0)
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId)))
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();
    }
}
