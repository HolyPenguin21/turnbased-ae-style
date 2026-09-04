using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §29/§30 — the canonical Raid actor eligibility owner. Replaces the misleading
    // RaidAssemblyPlanner.IsReadyRaidActor, which despite its name only ever checked STRUCTURAL
    // eligibility (never live movement). "Ready" is gone; the predicate is
    // IsStructuralRaidActor and it means exactly that. The win-chance-gated "can start now" /
    // "can continue" decisions live on RaidAssemblyPlanner.Plan / .PlanForArmy (RaidAdmissionPolicy).
    internal static class RaidActorEligibility
    {
        // ONE structural Raid actor predicate shared by Strategy diagnostics, Demand capability
        // inventory and final Provisioning. Garrison is reserve/potential power, never a mover.
        // Deliberately structural, NOT `CurrentMovement > 0`: a Hard-raid incumbent that already
        // made productive progress earlier in the same turn is still the correct actor. The live
        // Provisioning seam rejects a spent actor as MoverContended/RetryNextTurn; treating zero
        // remaining movement here as ineligible poisons a valid multi-turn raid with a structural
        // cooldown.
        internal static bool IsStructuralRaidActor(ArmySnapshot a)
        {
            if (a == null || a.IsPrison || a.IsAir || a.IsGarrison || a.IsSoloRecce || a.MemberCount <= 0)
                return false;

            PlayerSetupData owner = a.Owner;
            if (owner == null)
                return false;
            ArmyData live = ArmyRegistry.AllForOwner(owner).FirstOrDefault(x => x != null && x.Id == a.ArmyId);
            return live != null && !live.IsPrison && !live.IsGarrison && !live.IsAirfield && !live.IsAirArmy
                && !AiArmyRoles.IsSoloRecce(live) && !AiArmyRoles.IsSoloHeroAwaitingEscort(live)
                && live.Members.Count > 0;
        }

        // Free, structurally-eligible ground combat armies for this cycle, mobility-first:
        // already-activated / cheaper activation first, then the least powerful sufficient host
        // (avoids feeding an already-winning raid into an ever larger, ever more expensive stack).
        internal static List<ArmySnapshot> EligibleReadyArmies(WorldSnapshot snap, ISet<int> excludeArmyIds) =>
            snap.Self.Armies
                .Where(a => a != null && IsStructuralRaidActor(a)
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId)))
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();
    }
}
