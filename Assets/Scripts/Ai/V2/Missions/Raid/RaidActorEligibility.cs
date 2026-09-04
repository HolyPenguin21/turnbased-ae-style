using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ARCH-02 §29/§30/§59 — Raid actor eligibility. The structural predicate itself is a frozen
    // snapshot fact: WorldAnalysis computes ArmySnapshot.IsStructuralRaidActor once at scan time
    // (it may read the live domain), and every layer reads that field — no re-derivation from live
    // ArmyRegistry state, and no upward dependency from Analysis / State onto this Missions type.
    // What lives here is only the Missions-specific mover ORDERING for the assembly solver.
    internal static class RaidActorEligibility
    {
        // Free, structurally-eligible ground combat armies for this cycle, mobility-first:
        // already-activated / cheaper activation first, then the least powerful sufficient host
        // (avoids feeding an already-winning raid into an ever larger, ever more expensive stack).
        internal static List<ArmySnapshot> EligibleReadyArmies(WorldSnapshot snap, ISet<int> excludeArmyIds) =>
            snap.Self.Armies
                .Where(a => a != null && a.IsStructuralRaidActor
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId)))
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();
    }
}
