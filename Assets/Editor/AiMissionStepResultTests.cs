#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiMissionStepResultTests
    {
        [TestCase(ProvisionFailureKind.TargetSatisfied, MissionStepDisposition.Completed)]
        [TestCase(ProvisionFailureKind.TargetInvalidated, MissionStepDisposition.Waiting)]
        [TestCase(ProvisionFailureKind.NoExecutableStep, MissionStepDisposition.Waiting)]
        [TestCase(ProvisionFailureKind.DestinationUnreachable, MissionStepDisposition.Waiting)]
        [TestCase(ProvisionFailureKind.MoverContended, MissionStepDisposition.Waiting)]
        [TestCase(ProvisionFailureKind.NoMoverExists, MissionStepDisposition.Waiting)]
        [TestCase(ProvisionFailureKind.EnvelopeTooSmall, MissionStepDisposition.Waiting)]
        [TestCase(ProvisionFailureKind.AssemblyInfeasible, MissionStepDisposition.PermanentFailure)]
        public void ProvisioningMappingPreservesExistingScoutSemantics(ProvisionFailureKind failure, MissionStepDisposition expected)
        {
            var m = new MissionProposal { Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(2, 3) } };
            var facts = new MissionStepFacts { Proposal = m,
                PendingFailure = new ProvisionFailure(failure, ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, "test") };
            MissionStepResult result = MissionStepResultPolicy.Normalize(StableMissionKey.For(m), facts);
            Assert.That(result.Disposition, Is.EqualTo(expected));
            Assert.That(result.ProvisionFailureKindValue, Is.EqualTo(failure));
        }

        [TestCase(ExecutionStopReason.ReachedGoal, true, false, false, false, MissionStepDisposition.Completed)]
        [TestCase(ExecutionStopReason.ReachedGoal, true, true, false, false, MissionStepDisposition.Progress)]
        [TestCase(ExecutionStopReason.ReachedGoal, true, false, true, false, MissionStepDisposition.Completed)]
        [TestCase(ExecutionStopReason.BattleStarted, false, false, false, true, MissionStepDisposition.Waiting)]
        [TestCase(ExecutionStopReason.BattleStarted, false, false, false, false, MissionStepDisposition.Progress)]
        [TestCase(ExecutionStopReason.NoSafeStep, false, false, false, false, MissionStepDisposition.Waiting)]
        [TestCase(ExecutionStopReason.MoverLost, false, false, false, false, MissionStepDisposition.Invalidated)]
        [TestCase(ExecutionStopReason.TargetInvalidated, false, false, false, false, MissionStepDisposition.Waiting)]
        public void ExecutionMappingPreservesWaypointInterruptionAndNoOp(ExecutionStopReason stop, bool reached,
            bool roleContinues, bool staleNoOp, bool blockedBeforeMovement, MissionStepDisposition expected)
        {
            MissionStepFacts facts = AiLifecycleParityTests.Facts(MissionKind.Scout);
            facts.Execution = new ExecutionResult { StopReason = stop, ReachedGoal = reached,
                DurableRoleContinues = roleContinues, StaleNoOp = staleNoOp, BlockedBeforeMovement = blockedBeforeMovement };
            Assert.That(MissionStepResultPolicy.Normalize(StableMissionKey.For(facts.Proposal), facts).Disposition,
                Is.EqualTo(expected));
        }

        [Test]
        public void ReplanIsAWaitingProjectionWithoutRetiringTheOperation()
        {
            MissionStepFacts facts = AiLifecycleParityTests.Facts(MissionKind.Scout);
            facts.Execution = new ExecutionResult { StopReason = ExecutionStopReason.TargetInvalidated, NeedsReplan = true };
            var result = MissionStepResultPolicy.Normalize(StableMissionKey.For(facts.Proposal), facts);
            Assert.That(result.Disposition, Is.EqualTo(MissionStepDisposition.Replan));
            Assert.That(result.IsBlocked, Is.True);
            Assert.That(result.Disposition, Is.Not.EqualTo(MissionStepDisposition.PermanentFailure));
        }

        [Test]
        public void CompletedRaidReturnLegDoesNotCompleteDurableCampaign()
        {
            MissionStepFacts facts = AiLifecycleParityTests.Facts(MissionKind.Raid);
            facts.Provisioned = null;
            facts.Proposal.Target = new RaidMissionTarget { Phase = RaidMissionPhase.SupportReturn };
            facts.PendingFailure = new ProvisionFailure(ProvisionFailureKind.TargetSatisfied,
                ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, "already home");
            var result = MissionStepResultPolicy.Normalize(StableMissionKey.For(facts.Proposal), facts);
            Assert.That(result.Disposition, Is.EqualTo(MissionStepDisposition.Progress));
            Assert.That(result.ObjectiveSatisfied, Is.False);
        }
    }

    public sealed class AiLifecycleParityTests
    {
        [TestCase(MissionKind.Scout)]
        [TestCase(MissionKind.Economy)]
        [TestCase(MissionKind.Raid)]
        [TestCase(MissionKind.Attack)]
        [TestCase(MissionKind.ActiveDefence)]
        [TestCase(MissionKind.Development)]
        public void ProductiveMovementKeepsOriginalProgressCostAndActorFacts(MissionKind kind)
        {
            MissionStepFacts facts = Facts(kind);
            facts.Execution = new ExecutionResult { StopReason = ExecutionStopReason.StepCompleted,
                StepsMoved = 1, ApSpent = 2, ActualActorArmyId = 0, FinalHex = new HexCoord(2, 3) };
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { facts.Proposal });
            ledger.RecordProvisionSuccess(facts.Proposal, facts.Provisioned);
            facts.Execution.Key = StableMissionKey.For(facts.Proposal);
            ledger.RecordExecution(facts.Execution);
            var result = ledger.FinalizeSteps()[0];
            Assert.That(result.Disposition, Is.EqualTo(MissionStepDisposition.Progress));
            Assert.That(result.MadeProgress, Is.True);
            Assert.That(result.ApSpent, Is.EqualTo(2));
            Assert.That(result.MoverArmyId, Is.EqualTo(0));
            Assert.That(result.FinalHex, Is.EqualTo(new HexCoord(2, 3)));
        }

        [Test]
        public void AttackAndRaidHandoffFactsSurviveNormalization()
        {
            foreach (MissionKind kind in new[] { MissionKind.Attack, MissionKind.Raid })
            {
                MissionStepFacts facts = Facts(kind);
                facts.Execution = new ExecutionResult { StopReason = ExecutionStopReason.StepCompleted,
                    ReinforcementHandoffAttempted = true, AttackIntermediateCaptured = true,
                    AttackOpportunisticStrike = true, AttackCaptureHadBattle = true, RaidRefitSucceeded = true,
                    AirSupportStrikeSucceeded = true };
                var result = MissionStepResultPolicy.Normalize(StableMissionKey.For(facts.Proposal), facts);
                Assert.That(result.GroundFacts().ReinforcementHandoffAttempted, Is.True);
                if (kind == MissionKind.Attack)
                {
                    Assert.That(result.AttackFacts().AttackIntermediateCaptured, Is.True);
                    Assert.That(result.AttackFacts().AttackOpportunisticStrike, Is.True);
                    Assert.That(result.AttackFacts().AttackCaptureHadBattle, Is.True);
                }
                else
                {
                    Assert.That(result.RaidFacts().RaidRefitSucceeded, Is.True);
                    Assert.That(result.RaidFacts().RaidAirSupportStrikeSucceeded, Is.True);
                }
            }
        }

        internal static MissionStepFacts Facts(MissionKind kind)
        {
            var proposal = new MissionProposal { Kind = kind,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore } };
            var provisioned = new ProvisionedMission { Mission = proposal, Kind = kind, MoverArmyId = 7,
                ScoutKind = ScoutTargetKind.Explore, RaidPhase = RaidMissionPhase.Assault,
                AttackTarget = new AttackMissionTarget { Phase = AttackMissionPhase.Assault } };
            return new MissionStepFacts { Proposal = proposal, Provisioned = provisioned };
        }
    }
}
#endif
