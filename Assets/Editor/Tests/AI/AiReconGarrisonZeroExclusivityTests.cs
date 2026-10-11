#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Task 4 regression: a garrison whose own ArmyId happens to be 0 is still exactly one real
    // Recce. Two Explore missions each offering "extract from garrison #0 into a different free
    // shell (#20 / #21)" must never BOTH be picked — only one shell can actually receive the one
    // sparable garrison Recce.
    public class AiReconGarrisonZeroExclusivityTests
    {
        private static ArmySnapshot GarrisonShell(int shellArmyId, int sourceGarrisonArmyId) => new ArmySnapshot
        {
            ArmyId = shellArmyId,
            IsGarrison = false,
            RequiresGarrisonExtraction = true,
            MaxMovement = 3,
            CurrentMovement = 3,
        };

        private static ScoutExecutionCandidate ExtractionCandidate(int shellArmyId, int sourceGarrisonArmyId,
            float requiredAp) => new ScoutExecutionCandidate(
            army: GarrisonShell(shellArmyId, sourceGarrisonArmyId),
            executionHex: new HexCoord(1, 1), effActivationAp: 1, etaTurns: 1, distance: 1,
            detectionRisk: 0f, standOff: 0, alreadyHidden: false, requiredAp: requiredAp,
            executorKind: ScoutExecutorKind.Ground,
            sourceGarrisonArmyId: sourceGarrisonArmyId, materializationArmyId: shellArmyId);

        private static MissionProposal Mission(HexCoord focus)
        {
            var mission = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget
                {
                    FocusHex = focus,
                    Kind = ScoutTargetKind.Explore,
                    Stealth = StealthRequirement.None,
                },
                Requirements = new MissionRequirements(),
            };
            return mission;
        }

        private static FundedEntry Entry(MissionProposal mission, float ap) =>
            new FundedEntry
            {
                Mission = mission,
                Tentative = new ResourceVector(ap),
                PhysicalDraw = ResourceVector.Zero,
            };

        [Test]
        public void TwoDistantExploreMissions_ShareGarrisonZero_OnlyOneGetsTheRecce()
        {
            // Far enough apart that the ground-target proximity filter never suppresses either.
            var missionA = Mission(new HexCoord(0, 0));
            var missionB = Mission(new HexCoord(50, 0));
            var open = new List<FundedEntry> { Entry(missionA, 5f), Entry(missionB, 5f) };
            var cands = new List<List<ScoutExecutionCandidate>>
            {
                new List<ScoutExecutionCandidate> { ExtractionCandidate(shellArmyId: 20, sourceGarrisonArmyId: 0, requiredAp: 1f) },
                new List<ScoutExecutionCandidate> { ExtractionCandidate(shellArmyId: 21, sourceGarrisonArmyId: 0, requiredAp: 1f) },
            };

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFromCandidates(
                open, cands, airEnergyBudget: 0f, airActorCap: 0);

            int assignedCount = 0;
            foreach (var kvp in result.Assigned)
                assignedCount++;
            Assert.That(assignedCount, Is.EqualTo(1),
                "garrison #0 has exactly one sparable Recce — both destination shells must not both be granted a mission");
        }

        [Test]
        public void TwoGarrisons_TwoShells_BothCanBeAssigned()
        {
            // Sanity: distinct source garrisons (positive ids here, but the same logic covers a
            // positive id alongside id 0) must still both be assignable — the fix must not become
            // an over-broad reservation.
            var missionA = Mission(new HexCoord(0, 0));
            var missionB = Mission(new HexCoord(50, 0));
            var open = new List<FundedEntry> { Entry(missionA, 5f), Entry(missionB, 5f) };
            var cands = new List<List<ScoutExecutionCandidate>>
            {
                new List<ScoutExecutionCandidate> { ExtractionCandidate(shellArmyId: 20, sourceGarrisonArmyId: 0, requiredAp: 1f) },
                new List<ScoutExecutionCandidate> { ExtractionCandidate(shellArmyId: 21, sourceGarrisonArmyId: 5, requiredAp: 1f) },
            };

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFromCandidates(
                open, cands, airEnergyBudget: 0f, airActorCap: 0);

            int assignedCount = 0;
            foreach (var kvp in result.Assigned)
                assignedCount++;
            Assert.That(assignedCount, Is.EqualTo(2));
        }

        [Test]
        public void OrdinaryFieldArmyCandidate_NeverTriggersGarrisonSourceReservation()
        {
            // A normal field-army candidate (RequiresGarrisonExtraction == false) must never add
            // ANY id to the source-garrison reservation set, regardless of its own ArmyId — this
            // is what "an ordinary field army with id 0 must not be mistaken for a garrison" means
            // at the unit the fix actually changed: hasGarrisonSource must come from the existing
            // RequiresGarrisonExtraction fact, never from the actor's own id or its sign.
            var fieldArmy = new ArmySnapshot
            {
                ArmyId = 0, IsGarrison = false, RequiresGarrisonExtraction = false,
                MaxMovement = 3, CurrentMovement = 3,
            };
            var fieldCandidate = new ScoutExecutionCandidate(
                army: fieldArmy, executionHex: new HexCoord(0, 0), effActivationAp: 1, etaTurns: 1,
                distance: 1, detectionRisk: 0f, standOff: 0, alreadyHidden: false, requiredAp: 1f,
                executorKind: ScoutExecutorKind.Ground);
            Assert.That(fieldCandidate.RequiresGarrisonExtraction, Is.False);
            Assert.That(fieldCandidate.ActorKey, Is.EqualTo(0));

            // A single mission offering only this field-army candidate must still be assignable —
            // it is a plain, unreserved actor, not a phantom garrison-source claim.
            var missionA = Mission(new HexCoord(0, 0));
            var open = new List<FundedEntry> { Entry(missionA, 5f) };
            var cands = new List<List<ScoutExecutionCandidate>> { new List<ScoutExecutionCandidate> { fieldCandidate } };

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFromCandidates(
                open, cands, airEnergyBudget: 0f, airActorCap: 0);

            int assignedCount = 0;
            foreach (var kvp in result.Assigned)
                assignedCount++;
            Assert.That(assignedCount, Is.EqualTo(1));
        }
    }
}
#endif
