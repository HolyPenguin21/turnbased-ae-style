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
        internal static bool IsStructuralActor(ArmySnapshot actor) =>
            actor != null && actor.IsStructuralRaidActor;

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
        // GroundCombatAdmissionRegistry's Hard-Raid continuation branch) resolves its
        // actor directly by ArmyId and never calls this method, so an already-owned multi-turn
        // mission still waits for MP next turn instead of losing its actor here.
        internal static List<ArmySnapshot> EligibleReadyArmies(WorldSnapshot snap, ISet<int> excludeArmyIds,
            bool allowHeroAssemblyHost = false) =>
            EligibleArmies(snap, excludeArmyIds, requireMovementNow: true,
                allowHeroAssemblyHost: allowHeroAssemblyHost);

        // `requireMovementNow: false` is the capability view (Demand): an army whose MP is spent
        // this turn is still physical capability — it moves again next turn — so it must count as
        // temporary contention, never as a shortage to produce against. Never used to nominate.
        // A nomination (`requireMovementNow: true`) never takes the last defender off a
        // threatened own stronghold (ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender);
        // the capability view still counts that power — it is real, it holds the stronghold.
        internal static List<ArmySnapshot> EligibleArmies(WorldSnapshot snap, ISet<int> excludeArmyIds,
            bool requireMovementNow, bool allowHeroAssemblyHost = false) =>
            snap.Self.Armies
                .Where(a => (IsStructuralActor(a) || allowHeroAssemblyHost && IsHeroAssemblyHost(a))
                            && (!requireMovementNow || a.CurrentMovement > 0)
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId))
                            && (!requireMovementNow
                                || !ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, a)))
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();

        // A lone hero is never a ready fighter. Only the same-hex assembly loop may nominate it,
        // and only a complete projected roster with a legal escort can pass that loop's gate.
        // The lone-hero field container shape (also the ActiveDefence bench shelter's mover).
        internal static bool IsHeroAssemblyHost(ArmySnapshot a) =>
            a != null && a.HasHero && a.MemberCount == 1 && !a.IsGarrison && !a.IsPrison
                && !a.IsAir && !a.IsAirfield && !a.IsSoloRecce;

        // T10 — diagnostics only: why each own structural field army is NOT in EligibleArmies,
        // by the same predicates in the same order (claimed by another operation, no MP left this
        // turn, pinned stronghold defender). Never read by a decision.
        internal static string ExclusionSummary(WorldSnapshot snap, ISet<int> excludeArmyIds,
            bool requireMovementNow)
        {
            int structural = 0, claimed = 0, spent = 0, pinned = 0;
            foreach (ArmySnapshot a in snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
            {
                if (a == null || !a.IsStructuralRaidActor)
                    continue;
                structural++;
                if (requireMovementNow && a.CurrentMovement <= 0)
                    spent++;
                else if (excludeArmyIds != null && excludeArmyIds.Contains(a.ArmyId))
                    claimed++;
                else if (requireMovementNow
                    && ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, a))
                    pinned++;
            }
            return $"fieldArmies={structural} claimed={claimed} noMp={spent} pinnedDefender={pinned}";
        }
    }
}

