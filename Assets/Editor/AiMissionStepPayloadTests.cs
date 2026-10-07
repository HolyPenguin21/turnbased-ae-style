#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiMissionStepPayloadTests
    {
        private sealed class NewDomainPayload : IMissionStepPayload { public string Fact; }

        [Test]
        public void NewDomainPayloadDoesNotRequireAnotherGenericOutcomeField()
        {
            var payload = new NewDomainPayload { Fact = "domain-specific evidence" };
            var result = new MissionStepResult<NewDomainPayload>(
                new MissionIntentKey((MissionKind)99, 0, 3, 0, 0), MissionStepDisposition.Waiting, payload);
            Assert.That(result.Payload, Is.SameAs(payload));
            Assert.That(result.GetPayload<NewDomainPayload>().Fact, Is.EqualTo("domain-specific evidence"));
            Assert.That(result.Disposition, Is.EqualTo(MissionStepDisposition.Waiting));
            Assert.That(result.GetPayload<RaidStepPayload>(), Is.Null);
        }

        [Test]
        public void EmptyCompatibilityReadsDoNotCreatePayloadsOrInventNullableIdentityZero()
        {
            var outcome = new MissionTurnOutcome();
            Assert.That(outcome.HasRaidPayload, Is.False);
            Assert.That(outcome.RaidPrimaryArmyId, Is.Null);
            Assert.That(outcome.RaidSupportArmyId, Is.Null);
            Assert.That(outcome.RaidAirSupportArmyId, Is.Null);
            Assert.That(outcome.RaidAirSupportLandingHex, Is.Null);
            Assert.That(outcome.EconomyLoanSource, Is.Null);
            Assert.That(outcome.GetPayload<RaidStepPayload>(), Is.Null);
            Assert.That(outcome.GetPayload<EconomyStepPayload>(), Is.Null);
        }

        [Test]
        public void LegacyAndTypedRaidFactsShareOneValueIncludingActorZeroAndNull()
        {
            var outcome = new MissionTurnOutcome { HasRaidPayload = true, RaidPrimaryArmyId = 0,
                RaidPhase = RaidMissionPhase.Reinforcement, RaidAirSupportLandingHex = new HexCoord(1, 2) };
            var payload = outcome.GetPayload<RaidStepPayload>();
            Assert.That(payload.RaidPrimaryArmyId, Is.EqualTo(0));
            payload.RaidPrimaryArmyId = null;
            Assert.That(outcome.RaidPrimaryArmyId, Is.Null);
            outcome.RaidSupportArmyId = 8;
            Assert.That(payload.RaidSupportArmyId, Is.EqualTo(8));
            payload.RaidAirSupportLandingHex = null;
            Assert.That(outcome.RaidAirSupportLandingHex, Is.Null);
            Assert.That(outcome.GetPayload<RaidStepPayload>(), Is.SameAs(payload));
        }

        [Test]
        public void TypedEconomyLoanPreservesMissingIdentityAndUpdatesLegacyProjection()
        {
            var key = new MissionIntentKey(MissionKind.Scout, 0, 0, 2, 3);
            var outcome = new MissionTurnOutcome();
            outcome.SetPayload(new EconomyStepPayload { HasEconomyPayload = true, EconomyLoanSource = key });
            Assert.That(outcome.EconomyLoanSource, Is.EqualTo(key));
            outcome.EconomyLoanSource = null;
            Assert.That(outcome.GetPayload<EconomyStepPayload>().EconomyLoanSource, Is.Null);
            Assert.That(outcome.EconomyLoanSource, Is.Null);
        }

        [TestCase(MissionKind.Scout)]
        [TestCase(MissionKind.Raid)]
        [TestCase(MissionKind.Attack)]
        [TestCase(MissionKind.ActiveDefence)]
        [TestCase(MissionKind.Economy)]
        [TestCase(MissionKind.Development)]
        public void NormalizationRetainsDomainSpecificExecutionEvidence(MissionKind kind)
        {
            var proposal = new MissionProposal { Kind = kind };
            var provisioned = new ProvisionedMission { Mission = proposal, Kind = kind, MoverArmyId = 0 };
            var ledger = new MissionOutcomeLedger(); ledger.RegisterProposals(new[] { proposal });
            ledger.RecordProvisionSuccess(proposal, provisioned);
            ledger.RecordExecution(new ExecutionResult { Key = StableMissionKey.For(proposal), Source = provisioned,
                StopReason = ExecutionStopReason.StepCompleted, StepsMoved = 1,
                DurableRoleContinues = true, EconomyDeliveryReady = true, EconomyHolding = true,
                DevelopmentDeliveryReady = true, AirSupportStrikeSucceeded = true,
                ReinforcementHandoffAttempted = true, AttackOpportunisticStrike = true });
            var result = ledger.Finalize()[0];
            Assert.That(result.MoverArmyId, Is.EqualTo(0));
            switch (kind)
            {
                case MissionKind.Scout:
                    Assert.That(result.GetPayload<ReconStepPayload>().DurableRoleContinues, Is.True); break;
                case MissionKind.Raid:
                    Assert.That(result.GetPayload<RaidStepPayload>().RaidAirSupportStrikeSucceeded, Is.True);
                    Assert.That(result.GetPayload<GroundCombatStepPayload>().ReinforcementHandoffAttempted, Is.True); break;
                case MissionKind.Attack:
                    Assert.That(result.GetPayload<AttackStepPayload>().AirSupportStrikeSucceeded, Is.True);
                    Assert.That(result.GetPayload<AttackStepPayload>().AttackOpportunisticStrike, Is.True); break;
                case MissionKind.ActiveDefence:
                    Assert.That(result.GetPayload<ActiveDefenceStepPayload>().HasActiveDefencePayload, Is.True); break;
                case MissionKind.Economy:
                    Assert.That(result.GetPayload<EconomyStepPayload>().DeliveryReady, Is.True);
                    Assert.That(result.GetPayload<EconomyStepPayload>().Holding, Is.True); break;
                case MissionKind.Development:
                    Assert.That(result.GetPayload<DevelopmentStepPayload>().DeliveryReady, Is.True); break;
            }
        }
    }
}
#endif
