#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Characterization of the orchestration units extracted from Pipeline.RunTurn (level 0 of the
    // pipeline simplification): the values below are what the inline code produced before.
    public class AiPipelineOrchestrationUnitTests
    {
        [TestCase(null, null, false)]
        [TestCase(5, null, true)]
        [TestCase(null, 5, false)]
        [TestCase(3, 7, true)]
        [TestCase(7, 7, true)]   // a tie resumes the rebase
        [TestCase(9, 7, false)]
        public void RebaseIsResumedBeforeRecoveryUnlessRecoveryHasTheLowerId(
            int? rebaseId, int? recoveryId, bool expectedRebaseFirst) =>
            Assert.AreEqual(expectedRebaseFirst, MandatoryAviationOrder.RebaseFirst(rebaseId, recoveryId));

        [Test]
        public void NoPendingReasonsDirtyNothing()
        {
            TypedTriggerSplit s = TypedTriggerFanOut.Split(StrategicInvalidationReason.None, () => true);
            Assert.AreEqual(StrategicInvalidationReason.None, s.Consumed);
            Assert.AreEqual(0, s.DirtyAxes.Count);
        }

        [Test]
        public void ActorAloneReadmitsEconomyOnlyWhenTheBuilderArrived()
        {
            TypedTriggerSplit idle = TypedTriggerFanOut.Split(StrategicInvalidationReason.Actor, () => false);
            Assert.IsFalse(idle.DirtyAxes.Contains(DesireAxis.Economy));
            TypedTriggerSplit arrived = TypedTriggerFanOut.Split(StrategicInvalidationReason.Actor, () => true);
            Assert.IsTrue(arrived.DirtyAxes.Contains(DesireAxis.Economy));
        }

        [Test]
        public void BuilderArrivalIsNotAskedWhenEconomyHasAnotherReason()
        {
            int asked = 0;
            TypedTriggerSplit s = TypedTriggerFanOut.Split(
                StrategicInvalidationReason.Actor | StrategicInvalidationReason.Resources,
                () => { asked++; return false; });
            Assert.AreEqual(0, asked);
            Assert.IsTrue(s.DirtyAxes.Contains(DesireAxis.Economy));
        }

        [Test]
        public void ACompoundFactReachesEveryConsumerFamilyAndIsConsumedWhole()
        {
            const StrategicInvalidationReason compound =
                StrategicInvalidationReason.Hand | StrategicInvalidationReason.Infrastructure
                | StrategicInvalidationReason.Contact;
            TypedTriggerSplit s = TypedTriggerFanOut.Split(compound, () => false);
            foreach (DesireAxis axis in new[] { DesireAxis.Development, DesireAxis.Aggression })
                if ((compound & DesireAxes.InvalidationMaskFor(axis)) != StrategicInvalidationReason.None)
                    Assert.IsTrue(s.DirtyAxes.Contains(axis), axis.ToString());
            Assert.AreEqual(compound & (s.Operational | s.Strategic), s.Consumed & compound);
            Assert.AreEqual(s.Operational | s.Strategic, s.Consumed);
        }

        // ---- zero-Radar residual window (Pipeline's zeroRadarResidualWindow writers) ----------

        private static FundedEntry Funded(float value, bool commitment, int objective)
        {
            var target = new EconomyMissionTarget
            { Kind = EconomyTaskKind.BuildExtraction, TargetHex = new Game.HexGrid.HexCoord(objective, 0) };
            return new FundedEntry
            {
                IsCommitment = commitment,
                Mission = new MissionProposal
                { Kind = MissionKind.Economy, Target = target, EffectiveValue = value },
            };
        }

        [Test]
        public void ResidualWindowOpensAfterARejectionOnlyWhenEveryFundedEntryIsZeroAndNotACommitment()
        {
            Assert.IsTrue(ResidualWindowPolicy.AfterNoProvisionedTask(
                new[] { Funded(0f, false, 1), Funded(0f, false, 2) }));
            Assert.IsFalse(ResidualWindowPolicy.AfterNoProvisionedTask(
                new[] { Funded(0f, false, 1), Funded(0.5f, false, 2) }), "a positive entry");
            Assert.IsFalse(ResidualWindowPolicy.AfterNoProvisionedTask(
                new[] { Funded(0f, true, 1) }), "a durable commitment");
            Assert.IsFalse(ResidualWindowPolicy.AfterNoProvisionedTask(
                new FundedEntry[] { null }), "a null entry never opens it");
        }

        [Test]
        public void ResidualWindowAfterASettledTaskIgnoresOnlyTheTaskThatJustRan()
        {
            FundedEntry ran = Funded(3f, true, 1);
            StableMissionKey ranKey = StableMissionKey.For(ran.Mission);
            Assert.IsTrue(ResidualWindowPolicy.AfterSettledTask(new[] { ran }, ranKey));
            Assert.IsTrue(ResidualWindowPolicy.AfterSettledTask(new[] { ran, Funded(0f, false, 2) }, ranKey));
            Assert.IsFalse(ResidualWindowPolicy.AfterSettledTask(new[] { ran, Funded(1f, false, 2) }, ranKey),
                "unfinished positive allocation");
            Assert.IsFalse(ResidualWindowPolicy.AfterSettledTask(new[] { ran, Funded(0f, true, 2) }, ranKey),
                "unfinished commitment");
            Assert.IsFalse(ResidualWindowPolicy.AfterSettledTask(new FundedEntry[] { null }, ranKey));
        }

        // ---- lifecycle returns: which proposals wait for the first Phase B round -----------------

        [Test]
        public void OnlyDeferrableReturnsThatDidNotWaitLastTurnAreSelectedToWait()
        {
            var player = new Game.Players.PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var site = new Game.HexGrid.HexCoord(3, 3);
            (MissionIntent intent, MissionProposal proposal) Make(EconomyTaskKind kind, int builder)
            {
                var target = new EconomyMissionTarget { Kind = kind, TargetHex = site, BuilderArmyId = builder };
                var p = new MissionProposal { Kind = MissionKind.Economy, Target = target };
                return (new MissionIntent
                {
                    Kind = MissionKind.Economy, Status = IntentStatus.Active, IntentKey = MissionIntentKey.For(p),
                    Objective = new EconomyIntent { Kind = kind, TargetHex = site, BuilderArmyId = builder },
                }, p);
            }
            var ret = Make(EconomyTaskKind.ReturnBuilder, 7);
            var build = Make(EconomyTaskKind.BuildExtraction, 8);
            var intents = new[] { ret.intent, build.intent };
            var all = new[] { ret.proposal, build.proposal };
            try
            {
                var first = LifecycleReturnPolicy.SelectWaiting(all, intents, player, 5);
                Assert.AreEqual(1, first.Count);
                Assert.AreSame(ret.proposal, first[0]);
                LifecycleReturnPolicy.RecordWait(player, MissionIntentKey.For(ret.proposal), 5);
                Assert.AreEqual(1, LifecycleReturnPolicy.SelectWaiting(all, intents, player, 5).Count,
                    "later passes of the same turn still wait");
                Assert.AreEqual(0, LifecycleReturnPolicy.SelectWaiting(all, intents, player, 6).Count,
                    "waited last turn: goes now");
                Assert.AreEqual(1, LifecycleReturnPolicy.SelectWaiting(all, intents, player, 7).Count);
            }
            finally { LifecycleReturnPolicy.ClearAll(); }
        }

        // ---- loop bounds: moving counters must not widen them (าว ง3.1) --------------------------

        [Test]
        public void TheTurnLoopBoundsKeepTheirBaselineValues()
        {
            Assert.AreEqual(96, AiConfigV2.maxMidTurnStepsPerTurn);
            Assert.AreEqual(2, AiConfigV2.maxMidTurnNoProgressCycles);
            Assert.AreEqual(1, AiConfigV2.maxEndOfTurnTempoReruns, "management rounds = reruns + 1");
            Assert.AreEqual(3, AiConfigV2.maxReallocIterations);
            Assert.AreEqual(0.25f, AiConfigV2.lifecycleReturnHomeThreatSeverity);
        }
    }
}
#endif
