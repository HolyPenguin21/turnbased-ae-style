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
        // P1-6, AI V2 economy/aggression audit 2026-09-21 — CurrentMovement > 0 is a FRESH-admission
        // gate only: this method is the enumeration source for a new Assault/ActiveDefence candidate
        // and for Reinforcement support candidates (GroundCombatAssemblyPlanner.Plan's `eligible`
        // list, ReinforcementSupportCandidates), so a 0-MP army must never be nominated here — it
        // was observed assigned by PrepareGroundCombatAssignments and then rejected by Provisioning
        // as unfit in the same pass. A DURABLE incumbent's continuation re-test
        // (GroundCombatAssemblyPlanner.PlanForArmyAtThreshold, reached via PlanForArmy /
        // GroundCombatAdmissionRegistry's Hard-Raid continuation branch) resolves its actor directly
        // by ArmyId and never calls this method, so an already-owned multi-turn mission still waits
        // for MP next turn instead of losing its actor here.
        internal static List<ArmySnapshot> EligibleReadyArmies(WorldSnapshot snap, ISet<int> excludeArmyIds) =>
            snap.Self.Armies
                .Where(a => a != null && a.IsStructuralRaidActor && a.CurrentMovement > 0
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId)))
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();
    }
}
