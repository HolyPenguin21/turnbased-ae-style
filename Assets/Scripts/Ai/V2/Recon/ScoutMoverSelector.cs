using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT MOVER SELECTOR  (Assignment-stage low-level actor enumeration primitive)
    // ===========================================================================================
    //  ReconAssignmentPlanner (the ONE Assignment/eligibility owner, spec Level 5) is built on top
    //  of this — its EligibleMovers/StructuralCandidates/HasStructuralCandidate facade forwards
    //  here, and BuildCandidates/MeasureCapacity call Eligible directly. No other layer (Mission,
    //  Demand, Provisioning's diagnostics, the capability-pool registry) may call this type
    //  directly any more — go through ReconAssignmentPlanner's facade instead, so "what counts as
    //  eligible" never forks into a second copy.
    //
    //  ELIGIBILITY (own armies only — WorldSnapshot.Self.Armies)
    //    A fielded solo Recce (AiArmyRoles.IsSoloRecce), not a prison, not air, with members, that
    //    can still act THIS turn (CurrentMovement > 0 — a spent scout is not fundable work for the
    //    current allocation cycle, whatever its ETA), and is not in `excludeArmyIds` (movers
    //    locked by an earlier provisioning pass this turn). For a stealth-Required mission it must
    //    also be already hidden OR still able to slip into stealth before its first move
    //    (CanEnterStealth && !HasActivatedThisTurn) — a visible, already-activated scout is not a
    //    valid executor at all (parity with V1's hard exclusion).
    // ===========================================================================================
    // The unit the assignment solver actually packs (build-order step 6b): a concrete mover PLUS
    // the concrete hex it would execute from. Explore -> ExecutionHex == FocusHex, DetectionRisk
    // and StandOff are 0 (the strategic risk already lives in ScoutMissionTarget.DetectionRisk /
    // MissionLayer's LocalAdmissionScore and must not be double-counted in the solver). Surveil ->
    // ExecutionHex is the first CURRENTLY-EXECUTABLE vantage from SurveilVantageSelector, with its
    // own vantage-specific DetectionRisk / StandOff.
    public readonly struct ScoutExecutionCandidate
    {
        public readonly ArmySnapshot Army;
        public readonly HexCoord ExecutionHex;
        public readonly int EffActivationAp;
        public readonly int EtaTurns;          // mover -> ExecutionHex
        public readonly int Distance;          // mover -> ExecutionHex
        public readonly float DetectionRisk;   // vantage-specific; 0 for Explore
        public readonly int StandOff;          // Distance(ExecutionHex, FocusHex); 0 for Explore
        public readonly bool AlreadyHidden;
        public readonly float RequiredAp;      // EffActivationAp + (stealth transition if Required && !hidden)

        public ScoutExecutionCandidate(ArmySnapshot army, HexCoord executionHex, int effActivationAp,
            int etaTurns, int distance, float detectionRisk, int standOff, bool alreadyHidden, float requiredAp)
        {
            Army = army;
            ExecutionHex = executionHex;
            EffActivationAp = effActivationAp;
            EtaTurns = etaTurns;
            Distance = distance;
            DetectionRisk = detectionRisk;
            StandOff = standOff;
            AlreadyHidden = alreadyHidden;
            RequiredAp = requiredAp;
        }

        public bool IsStealthCapableMover => Army != null && (Army.IsHidden || Army.CanEnterStealth);
    }

    public static class ScoutMoverSelector
    {
        // Eligibility ONLY (no ranking / no ETA toward FocusHex — that basis is wrong for Surveil).
        // Same filter Rank applies: fielded solo Recce, not prison / air, has members, can still
        // act this turn (CurrentMovement > 0), not in excludeArmyIds, and — for a Required mission
        // — hidden or able to enter stealth before its first move.
        public static List<ArmySnapshot> Eligible(WorldSnapshot snap, ScoutMissionTarget target, ISet<int> excludeArmyIds)
        {
            var result = new List<ArmySnapshot>();
            if (snap?.Self?.Armies == null)
                return result;
            bool needStealth = target.Stealth == StealthRequirement.Required;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (a.CurrentMovement <= 0)
                    continue;
                if (excludeArmyIds != null && excludeArmyIds.Contains(a.ArmyId))
                    continue;
                if (needStealth && !(a.IsHidden || (a.CanEnterStealth && !a.HasActivatedThisTurn)))
                    continue;
                result.Add(a);
            }
            return result;
        }

        // STRUCTURAL capability probe — solo Recce that could, IN PRINCIPLE, serve a mission of
        // this stealth requirement. Deliberately ignores the turn-transient filters Eligible
        // applies (CurrentMovement > 0, the "visible + already activated" Required exclusion,
        // excludeArmyIds): their absence is "spent / contended THIS turn" (MoverContended), while
        // absence of any such executor is `NoMoverExists` — a TRANSIENT capability shortage that
        // Demand/StrategicManager may repair and therefore never starts a target cooldown. Stealth
        // capability itself remains structural for choosing whether a given Recce can serve a
        // Required mission.
        public static IEnumerable<ArmySnapshot> StructuralCandidates(WorldSnapshot snap, ScoutMissionTarget target)
        {
            if (snap?.Self?.Armies == null)
                yield break;
            bool needStealth = target.Stealth == StealthRequirement.Required;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (needStealth && !(a.IsHidden || a.StealthLevel > 0))
                    continue;
                yield return a;
            }
        }

        public static bool HasStructuralCandidate(WorldSnapshot snap, ScoutMissionTarget target) =>
            StructuralCandidates(snap, target).Any();
    }
}
