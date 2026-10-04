using System.Collections.Generic;
using Game.Combat;

namespace Game.Ai.V2
{
    internal readonly struct AttackPreparationAssessment
    {
        internal readonly bool HostAvailable, PowerReady, CoversAllDefenders, CombatFeasible;
        internal readonly bool StructuralActor;
        internal readonly float CurrentPower, RequiredPower, ProjectedWinChance;
        internal readonly string Reason;
        // Capability satisfaction is separate from admission of an actor to a march. Empty / hero
        // preparation containers can be strengthened before they become structural combat actors.
        internal bool CapabilityReady => HostAvailable && PowerReady && CombatFeasible;
        internal bool Ready => CapabilityReady && StructuralActor;

        internal AttackPreparationAssessment(ArmySnapshot host, float peak,
            bool feasible, float win, bool cover)
        {
            HostAvailable = host != null;
            CurrentPower = host?.EffectiveArmyPower ?? 0f;
            RequiredPower = AttackForceReadiness.RequiredPower(peak);
            PowerReady = AttackForceReadiness.ForceReady(CurrentPower, peak);
            StructuralActor = GroundCombatActorEligibility.IsStructuralActor(host);
            CombatFeasible = feasible;
            ProjectedWinChance = win;
            CoversAllDefenders = cover;
            // Coverage is named only where it actually gated the fight (a test switch may waive it).
            Reason = !HostAvailable ? "no_primary" : !PowerReady ? "fist_below_bar"
                : !feasible ? (!cover ? "coverage_missing" : "win_below_gate")
                : !StructuralActor ? "actor_ineligible" : "ready";
        }
    }

    // No mutation, phase transitions, or actor selection. Demand, Mission and Continuity read it;
    // only Continuity uses Ready to transition the operation. Location-only opposition remains the
    // current empty known package, so this policy adds no observation gate.
    internal static class AttackPreparationReadiness
    {
        internal static AttackPreparationAssessment Assess(ArmySnapshot host, float currentPeak,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus)
        {
            if (host == null)
                return new AttackPreparationAssessment(null, currentPeak, false, 0f, false);
            bool clears = GroundCombatFeasibility.Clears(
                host.Members ?? System.Array.Empty<WorthIt.DefenderProfile>(), host.Commander,
                opposition, GroundCombatAdmissionPolicy.AttackCoverageGate, hexBonus,
                out float win, out bool cover);
            return new AttackPreparationAssessment(host, currentPeak, clears, win, cover);
        }
    }
}
