using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Units;

namespace Game.Ai.V2
{
    // Round 4 — WHO executes a funded Recon mission is Assignment's job for BOTH movers now.
    // Ground: exactly one live ArmySnapshot (a fielded solo Recce), matching ScoutExecutionCandidate.
    // Air adds two shapes, mirroring what AirReconPlanner used to pick independently:
    //   AirExisting — a real, already-existing air ArmySnapshot (a ready standalone wing sitting on
    //     its own airfield with no sortie task). Same ArmyId-keyed shape as Ground.
    //   AirLaunch   — a NOT-YET-EXISTING sortie: a concrete minimum aircraft subset from one owned
    //     airfield's hangar. There is no live ArmyId to key uniqueness on (the aircraft only becomes
    //     an ArmyData when Provisioning/Execution actually launches it — the same reason Raid assembly
    //     from donors needs its own ArmyData-less bookkeeping), so ScoutExecutionCandidate.ActorKey
    //     synthesises a stable per-airfield negative id for the batch solver's one-actor-per-job
    //     uniqueness constraint instead of reading Army.ArmyId.
    public enum ScoutExecutorKind { Ground, AirExisting, AirLaunch }
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
        // RECON-AIR-01 — the concrete, actor-specific Energy this candidate's first activation
        // needs. 0 for every Ground candidate (ground scouts never spend Energy to activate); a real
        // figure for AirExisting (the wing's own ActivationEnergyCost) / AirLaunch (Σ the launch
        // subset's LaunchEnergyCost), the SAME role RequiredAp already plays for AP.
        public readonly float RequiredEnergy;
        // The mission-specific AIR-01 route score Assignment already resolved for THIS candidate
        // against the bound mission target (AppendAirCandidates: Pick/PickFromStorage anchored at
        // the mission's FocusHex/vantage, then MakesGenuineProgress). 0 for Ground. This is the
        // ReconInformationValue the single strategic admission owner (ProvisioningManager.
        // AirSortieReservationAdmission -> AviationSortieReservationEvaluator) consumes — no layer
        // below Provisioning re-probes a route to re-derive it.
        public readonly float RouteScore;

        // Round 4 — executor identity. Ground candidates (and AirExisting) carry Army != null and
        // ExecutorKind defaults to Ground for every pre-round-4 call site (optional params). An
        // AirLaunch candidate carries Army == null plus the concrete airfield/subset Provisioning
        // must claim; ActorKey below is the ONLY thing the batch solver may use for actor identity.
        public readonly ScoutExecutorKind ExecutorKind;
        public readonly HexCoord AirfieldHex;                    // AirLaunch only
        public readonly IReadOnlyList<UnitData> LaunchSubset;    // AirLaunch only

        public ScoutExecutionCandidate(ArmySnapshot army, HexCoord executionHex, int effActivationAp,
            int etaTurns, int distance, float detectionRisk, int standOff, bool alreadyHidden, float requiredAp,
            ScoutExecutorKind executorKind = ScoutExecutorKind.Ground, HexCoord airfieldHex = default,
            IReadOnlyList<UnitData> launchSubset = null, float requiredEnergy = 0f, float routeScore = 0f)
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
            ExecutorKind = executorKind;
            AirfieldHex = airfieldHex;
            LaunchSubset = launchSubset;
            RequiredEnergy = requiredEnergy;
            RouteScore = routeScore;
        }

        public bool IsStealthCapableMover => Army != null && (Army.IsHidden || Army.CanEnterStealth);

        // The batch solver's one-actor-per-job identity key. A real mover (Ground / AirExisting) is
        // its own ArmyId; an AirLaunch candidate (no ArmyData yet) is a stable per-airfield synthetic
        // id, deliberately far outside the real ArmyId range, so two funded missions in the same pass
        // can never both claim the same airfield's hangar subset.
        public int ActorKey => Army != null ? Army.ArmyId : SyntheticAirfieldActorId(AirfieldHex);

        public static int SyntheticAirfieldActorId(HexCoord airfieldHex) =>
            -(2_000_000 + (airfieldHex.Q & 0xFFF) * 4096 + (airfieldHex.R & 0xFFF));
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
