#if UNITY_INCLUDE_TESTS
using System;
using System.Security.Cryptography;
using System.Text;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiLifecycleParityMatrixTests
    {
        // Frozen from the actual master 706b1bbd result boundary, not from the refactor.
        [Test]
        public void RecordedFactMatrixMatchesOriginalMaster()
        {
            Assert.That(LegacyFingerprint(), Is.EqualTo("88C62EC22801E3E8127D1F2E15205FB3978A32FBA292484249A1E3EABAAECE8F"));
        }

        public static string LegacyFingerprint()
        {
            var rows = new StringBuilder();
            foreach (MissionKind kind in new[] { MissionKind.Scout, MissionKind.Economy, MissionKind.Raid,
                MissionKind.Attack, MissionKind.ActiveDefence, MissionKind.Development })
            {
                foreach (ProvisionFailureKind failure in Enum.GetValues(typeof(ProvisionFailureKind)))
                {
                    MissionProposal proposal = Proposal(kind);
                    var ledger = new MissionOutcomeLedger(); ledger.RegisterProposals(new[] { proposal });
                    ledger.RecordProvisionFailure(proposal, new ProvisionFailure(failure,
                        ProvisionDisposition.RetryNextTurn, ProvisionRequirement.Zero, "parity"));
                    Append(rows, ledger.FinalizeSteps()[0]);
                }
                foreach (ExecutionStopReason stop in Enum.GetValues(typeof(ExecutionStopReason)))
                for (int flags = 0; flags < 64; ++flags)
                {
                    MissionProposal proposal = Proposal(kind);
                    var ledger = new MissionOutcomeLedger(); ledger.RegisterProposals(new[] { proposal });
                    var provisioned = new ProvisionedMission { Mission = proposal, Kind = kind, MoverArmyId = 7,
                        RaidPhase = RaidMissionPhase.Assault,
                        AttackTarget = new AttackMissionTarget { Phase = AttackMissionPhase.Assault } };
                    ledger.RecordProvisionSuccess(proposal, provisioned);
                    ledger.RecordExecution(new ExecutionResult { Key = StableMissionKey.For(proposal), Source = provisioned,
                        StopReason = stop, ReachedGoal = (flags & 1) != 0, DurableRoleContinues = (flags & 2) != 0,
                        BlockedBeforeMovement = (flags & 4) != 0, NeedsReplan = (flags & 8) != 0,
                        StepsMoved = (flags & 16) != 0 ? 1 : 0, EconomyPrepared = (flags & 32) != 0,
                        EconomyDeliveryReady = flags % 3 == 0, EconomyHolding = flags % 7 == 0,
                        EnteredStealth = flags % 5 == 0, InfrastructureChanged = flags % 11 == 0,
                        CombatChanged = flags % 13 == 0, StaleNoOp = flags % 17 == 0,
                        ReinforcementHandoffAttempted = flags % 19 == 0, AttackIntermediateCaptured = flags % 23 == 0,
                        AttackOpportunisticStrike = flags % 29 == 0, AttackCaptureHadBattle = flags % 31 == 0,
                        AirSupportStrikeSucceeded = flags % 37 == 0, RaidRefitSucceeded = flags % 41 == 0,
                        ApSpent = flags % 4, ActualActorArmyId = 0, FinalHex = new HexCoord(2, 3) });
                    Append(rows, ledger.FinalizeSteps()[0]);
                }
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(rows.ToString()))).Replace("-", "");
        }

        private static MissionProposal Proposal(MissionKind kind)
        {
            object target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(2, 3) };
            if (kind == MissionKind.Raid) target = new RaidMissionTarget { Phase = RaidMissionPhase.Assault };
            else if (kind == MissionKind.Attack) target = new AttackMissionTarget { Phase = AttackMissionPhase.Assault };
            else if (kind == MissionKind.Economy) target = new EconomyMissionTarget { Kind = EconomyTaskKind.FoundBase };
            else if (kind == MissionKind.Development) target = new DevelopmentMissionTarget();
            else if (kind == MissionKind.ActiveDefence) target = new ActiveDefenceMissionTarget();
            return new MissionProposal { Kind = kind, Target = target };
        }

        // The frozen fingerprints encode the original four-state execution outcome ordinal.
        private static int LegacyOutcome(MissionStepResult r) =>
            r.Disposition == MissionStepDisposition.Completed ? 0
            : r.Disposition == MissionStepDisposition.Progress ? 1
            : r.IsBlocked ? 2 : 3;

        private static void Append(StringBuilder rows, MissionStepResult outcome)
        {
            // These are the original externally visible facts, including all currently carried domain flags.
            rows.Append(LegacyOutcome(outcome)).Append('|').Append(outcome.ObjectiveSatisfied).Append('|')
                .Append(outcome.ObjectiveSatisfiedExternally).Append('|').Append(outcome.Disposition == MissionStepDisposition.PermanentFailure).Append('|')
                .Append(outcome.MadeProgress).Append('|').Append(outcome.StepsMoved).Append('|')
                .Append(outcome.ApSpent).Append('|').Append(outcome.MoverArmyId).Append('|').Append(outcome.FinalHex).Append('|')
                .Append(outcome.GroundFacts().OperationStarted).Append('|').Append(outcome.GroundFacts().ReinforcementHandoffAttempted).Append('|')
                .Append(outcome.AttackFacts().AttackIntermediateCaptured).Append('|').Append(outcome.AttackFacts().AttackCaptureHadBattle).Append('|')
                .Append(outcome.AttackFacts().AttackOpportunisticStrike).Append('|').Append(outcome.RaidFacts().RaidRefitSucceeded).Append('|')
                .Append(outcome.RaidFacts().RaidAirSupportStrikeSucceeded).Append('|').Append(outcome.EconomyFacts().EconomyBuildCompleted).Append('|')
                .Append(outcome.RaidFacts().HasRaidPayload).Append('|').Append(outcome.AttackFacts().HasAttackPayload).Append('|')
                .Append(outcome.EconomyFacts().HasEconomyPayload).Append('|').Append(outcome.DevelopmentFacts().HasDevelopmentPayload).Append('|')
                .Append(outcome.DefenceFacts().HasActiveDefencePayload).Append('|').Append(outcome.ReconFacts().HasScoutPayload).Append('\n');
        }
    }
}
#endif
