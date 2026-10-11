#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiAttackIntermediateBaseTests
    {
        private static readonly PlayerSetupData Us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly PlayerSetupData Red = new PlayerSetupData { Nickname = "Red", ColorIndex = 2 };
        private static readonly PlayerSetupData Blue = new PlayerSetupData { Nickname = "Blue", ColorIndex = 3 };
        private static readonly HexCoord Base = new HexCoord(2, 0);
        private static readonly AttackTargetRef Main = AttackTargetRef.For(new HexCoord(6, 0), Red, AttackTargetKind.Citadel);
        private static readonly AttackTargetRef Local = AttackTargetRef.For(Base, Blue, AttackTargetKind.Base);

        [SetUp] public void Setup() { ArmyRegistry.Clear(); MissionIntentRegistry.Clear(); }
        [TearDown] public void Cleanup() { ArmyRegistry.Clear(); MissionIntentRegistry.Clear(); }

        [Test]
        public void UnknownCitadel_StillAllowsSafeObservedBaseOfAnotherPlayer()
        {
            var snap = Snapshot(); var attack = Attack(); var candidate = Candidate();
            Assert.That(AttackIntermediateBasePolicy.Select(snap, attack, new[] { candidate }), Is.SameAs(candidate));
            Assert.That(attack.Target, Is.EqualTo(Main));
            Assert.That(attack.IntermediateTarget.HasValue, Is.False, "selection is pure; only execution pins it");
        }

        [Test]
        public void MainDefenderStrength_DoesNotChangeLocalFightDecision()
        {
            var snap = Snapshot(); var candidate = Candidate();
            var first = AttackIntermediateBasePolicy.Select(snap, Attack(), new[] { candidate });
            var main = new AttackObjective { Target = Main, Opposition = new[] {
                new WorthIt.DefendingArmy(new[] { Body(1000, 1000, 1000) }, default) } };
            Assert.That(AttackIntermediateBasePolicy.Select(snap, Attack(), new[] { main, candidate }), Is.SameAs(first));
        }

        [TestCase(5)] [TestCase(0)]
        public void StaleOrUnstampedBase_IsNotTreatedAsObservedEmpty(int seen)
        {
            var snap = Snapshot(seen); Assert.That(AttackIntermediateBasePolicy.Select(snap, Attack(), new[] { Candidate() }), Is.Null);
        }

        [Test]
        public void StaleDefenderPackage_IsRejectedEvenWithFreshBuilding()
        {
            var snap = Snapshot(); snap.Known.EnemySightings = new[] {
                new AiMapMemory.KnownEnemySighting(Base, Blue, "guard", 1, 1, 1,
                    new[] { Body(1, 1, 2) }, seenTurn: 5, armyId: 30, isGarrison: true) };
            Assert.That(AttackIntermediateBasePolicy.Select(snap, Attack(), new[] { Candidate() }), Is.Null);
        }

        [Test]
        public void AllBaseDefendersAndStructuralDefence_UseStrictSharedEstimator()
        {
            var candidate = Candidate(); candidate.Opposition = new[] {
                new WorthIt.DefendingArmy(new[] { Body(1, 1, 2) }, default),
                new WorthIt.DefendingArmy(new[] { Body(1000, 1000, 1000) }, default) };
            Assert.That(AttackIntermediateBasePolicy.Select(Snapshot(), Attack(), new[] { candidate }), Is.Null);
        }

        [Test]
        public void IntermediateGate_IsTheLocalGateWhileMainAttackPolicyStaysUnchanged()
        {
            // 2026-10-08: an optional Base needs 0.40 and no defender coverage; Raid / ActiveDefence
            // keep 0.80 / 0.55 (AiAttackFieldContactTests.Policy_ThresholdAndCoverageAreIndependent)
            var proposal = Proposal(local: true);
            Assert.That(GroundCombatAdmissionPolicy.AssaultGate(proposal, 7), Is.EqualTo(GroundCombatAdmissionPolicy.AttackIntermediateBaseWinChanceGate));
            Assert.That(GroundCombatAdmissionPolicy.AssaultCoverage(proposal), Is.False);
            Assert.That(GroundCombatAdmissionPolicy.AssaultGate(Proposal(local: false), 7), Is.EqualTo(GroundCombatAdmissionPolicy.AttackCoverageGate));
        }

        [TestCase(6, 2, 4, 3, 3, true)]
        [TestCase(6, 3, 5, 3, 3, true)]
        [TestCase(6, 4, 4, 3, 3, false)]
        [TestCase(6, 3, 7, 3, 3, false)]
        [TestCase(6, 2, 6, 3, 3, false)]
        [TestCase(int.MaxValue, 2, 4, 3, 3, true)]
        [TestCase(6, 2, int.MaxValue, 3, 3, false)]
        public void RouteRules_KeepDetoursLocalAndRequireOnwardRoute(int direct, int contact, int onward, int remaining, int max, bool allowed) =>
            Assert.That(AttackIntermediateBasePolicy.RouteAllows(direct, contact, onward, remaining, max), Is.EqualTo(allowed));

        [Test]
        public void CompletedDiversionCap_AlsoBlocksAnotherBaseThisTurn()
        {
            var attack = Attack(); attack.LastOpportunisticStrikeTurn = 6;
            Assert.That(AttackIntermediateBasePolicy.Select(Snapshot(), attack, new[] { Candidate() }), Is.Null);
        }

        [Test]
        public void PreparationAndSupport_DoNotDivertToBases()
        {
            var attack = Attack(); attack.Phase = AttackMissionPhase.Gather;
            Assert.That(AttackIntermediateBasePolicy.Select(Snapshot(), attack, new[] { Candidate() }), Is.Null);
        }

        [Test]
        public void LocalFundingKeys_AreDistinctButDurableOperationIdentityIsUnchanged()
        {
            var main = Proposal(false); var local = Proposal(true);
            Assert.That(StableMissionKey.For(local), Is.Not.EqualTo(StableMissionKey.For(main)));
            Assert.That(MissionIntentKey.For(local), Is.EqualTo(MissionIntentKey.For(main)));
            var changed = (AttackMissionTarget)local.Target;
            changed.IntermediateTarget = AttackTargetRef.For(Base, Red, AttackTargetKind.Base);
            Assert.That(StableMissionKey.ForAttack(changed), Is.Not.EqualTo(StableMissionKey.For(local)));
        }

        [Test]
        public void ActualLocalProgress_PinsBaseWithoutReplacingMainTargetOrPrimary()
        {
            var intent = Intent(); MissionIntentRegistry.GetOrCreate(Us).Put(intent);
            var outcome = Outcome(); outcome.GroundFactsForWrite().OperationStarted = true; outcome.MadeProgress = true;
            outcome.StepsMoved = 1;
            MissionContinuityLayer.ReconcileStep(Us, 6, outcome);
            Assert.That(intent.Attack.IntermediateTarget, Is.EqualTo(Local));
            Assert.That(intent.Attack.Target, Is.EqualTo(Main));
            Assert.That(intent.Attack.PrimaryArmyId, Is.EqualTo(7));
        }

        [TestCase(false)] [TestCase(true)]
        public void LocalCapture_PreservesMainOperationAndClearsPin(bool battle)
        {
            var intent = Intent(); intent.Attack.IntermediateTarget = Local;
            MissionIntentRegistry.GetOrCreate(Us).Put(intent);
            var outcome = Outcome(); outcome.GroundFactsForWrite().OperationStarted = true; outcome.MadeProgress = true;
            outcome.AttackFactsForWrite().AttackOpportunisticStrike = true;
            outcome.AttackFactsForWrite().AttackIntermediateCaptured = true;
            outcome.AttackFactsForWrite().AttackCaptureHadBattle = battle;
            MissionContinuityLayer.ReconcileStep(Us, 6, outcome);
            Assert.That(MissionIntentRegistry.GetOrCreate(Us).All.Single().Status, Is.EqualTo(IntentStatus.Active));
            Assert.That(intent.Attack.Target, Is.EqualTo(Main));
            Assert.That(intent.Attack.IntermediateTarget.HasValue, Is.False);
            Assert.That(intent.Attack.LastOpportunisticStrikeTurn, Is.EqualTo(6));
            Assert.That(intent.Attack.RefitBaseHex, Is.EqualTo(Base));
            Assert.That(intent.Attack.RefitCaptureTurn, Is.EqualTo(6));
            Assert.That(intent.Attack.RefitBattleStopTurn, Is.EqualTo(battle ? 6 : -1));
        }

        [Test]
        public void ChangedLocalOwner_DoesNotRetireMainOperation()
        {
            var intent = Intent(); MissionIntentRegistry.GetOrCreate(Us).Put(intent);
            var outcome = Outcome(); outcome.Fail();
            outcome.Disposition = MissionStepDisposition.PermanentFailure;
            MissionContinuityLayer.ReconcileStep(Us, 6, outcome);
            Assert.That(MissionIntentRegistry.GetOrCreate(Us).All.Single().Status, Is.EqualTo(IntentStatus.Active));
            Assert.That(intent.Attack.Target, Is.EqualTo(Main));
        }

        [TestCase(false, 2f)] [TestCase(true, 0f)]
        public void Planner_UnknownMainUsesLocalSiteAndOnlyActualActivationAp(bool activated, float ap)
        {
            var snap = Snapshot(); snap.Self.AttackPeak = 100f;
            var actor = snap.Self.Armies[0]; actor.HasActivatedThisTurn = activated; actor.ActivationApCost = 2;
            Red.CitadelHexQ = Main.Hex.Q; Red.CitadelHexR = Main.Hex.R;
            snap.TrueWorld = new TrueWorldSnapshot { Opponents = new[] { new OpponentSnapshot { Player = Red } } };
            var proposals = new List<MissionProposal>();
            AggressionMissionLayer.AppendAttack(snap, new[] { Intent() }, new HashSet<int> { 7 },
                proposals, null, new Dictionary<MissionIntentKey, string>());
            var proposal = proposals.Single(p => p.Target is AttackMissionTarget t && t.Phase == AttackMissionPhase.Assault);
            var target = (AttackMissionTarget)proposal.Target;
            Assert.That(target.Target, Is.EqualTo(Main)); Assert.That(target.AssaultTarget, Is.EqualTo(Local));
            Assert.That(proposal.Requirements.ApMinimum, Is.EqualTo(ap));
            Assert.That(proposal.Requirements.ApMaximum, Is.EqualTo(ap));
            Assert.That(proposal.Requirements.HumanDesired + proposal.Requirements.EnergyDesired
                + proposal.Requirements.MaterialsDesired + proposal.Requirements.TechDesired, Is.Zero);
            Assert.That(MissionIntentKey.For(proposal), Is.EqualTo(MissionIntentKey.ForAttack(Main)));
        }

        [Test]
        public void FreshBuildingWithMissingDefenderRoster_IsRejected()
        {
            var snap = Snapshot(); snap.Known.EnemySightings = new[] {
                new AiMapMemory.KnownEnemySighting(Base, Blue, "guard", 3, 5, 10,
                    null, seenTurn: 6, armyId: 30, isGarrison: true) };
            Assert.That(AttackIntermediateBasePolicy.Select(snap, Attack(), new[] { Candidate() }), Is.Null);
        }

        [Test]
        public void RepeatedSelection_DoesNotReuseSafeResultAfterDefendersChange()
        {
            WorthIt.BeginEstimateCacheScope();
            try
            {
                var candidate = Candidate(); var snap = Snapshot();
                Assert.That(AttackIntermediateBasePolicy.Select(snap, Attack(), new[] { candidate }), Is.SameAs(candidate));
                candidate.Opposition = new[] { new WorthIt.DefendingArmy(new[] { Body(1000, 1000, 1000) }, default) };
                Assert.That(AttackIntermediateBasePolicy.Select(snap, Attack(), new[] { candidate }), Is.Null);
            }
            finally { WorthIt.EndEstimateCacheScope(); }
        }

        [Test]
        public void AdmissionCache_TracksExactMovementAndLocalStrikeLifecycle()
        {
            var snap = Snapshot(); var intent = Intent(); MissionIntentRegistry.GetOrCreate(Us).Put(intent);
            string first = AggressionAdmission.Fingerprint(snap, Us);
            snap.Self.Armies[0].CurrentMovement = 1;
            string lessMovement = AggressionAdmission.Fingerprint(snap, Us);
            Assert.That(lessMovement, Is.Not.EqualTo(first), "positive MP alone cannot prove contact is still reachable");
            intent.Attack.IntermediateTarget = Local;
            string pinned = AggressionAdmission.Fingerprint(snap, Us);
            Assert.That(pinned, Is.Not.EqualTo(lessMovement));
            intent.Attack.LastOpportunisticStrikeTurn = 6;
            Assert.That(AggressionAdmission.Fingerprint(snap, Us), Is.Not.EqualTo(pinned));
        }

        [Test]
        public void LocalAssaultAllocation_CannotSpendOtherOwnersBankHold()
        {
            var snap = Snapshot(); snap.Self.ActionPoints = 3;
            var proposal = Proposal(true); proposal.BaseValue = 10;
            proposal.Requirements = new MissionRequirements { ApMinimum = 2, ApDesired = 2, ApMaximum = 2 };
            proposal.Axes.Value[DesireAxis.Aggression] = 1;
            StrategicResourceReservationLedger.BeginTurn(Us, 6);
            try
            {
                StrategicResourceReservationLedger.Upsert(Us, 6, new StrategicResourceReservation {
                    Owner = "build", Reason = StrategicReservationReason.EconomyBuildCompletion,
                    Resource = StrategicReservedResource.ActionPoints, Amount = 2,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn });
                var commitments = new List<Commitment> { new Commitment { Mission = proposal, Tier = CommitmentTier.Hard } };
                var session = ResourceAllocator.BeginTurn(snap, Radar.Even(), new List<MissionProposal>(), commitments, Us);
                Assert.That(session.Pack().Funded.Any(f => f.Mission == proposal), Is.False);
                Assert.That(StrategicResourceReservationLedger.Active(Us, 6, StrategicReservedResource.ActionPoints), Is.EqualTo(2));
                StrategicResourceReservationLedger.ReleaseByOwner(Us, 6, "build");
                var funded = ResourceAllocator.BeginTurn(snap, Radar.Even(), new List<MissionProposal>(), commitments, Us).Pack();
                Assert.That(funded.Funded.Single(f => f.Mission == proposal).Tentative.Ap, Is.EqualTo(2));
                Assert.That(funded.PhysicalFunded.Human + funded.PhysicalFunded.Energy
                    + funded.PhysicalFunded.Materials + funded.PhysicalFunded.Tech, Is.Zero);
            }
            finally { StrategicResourceReservationLedger.ClearAll(); }
        }

        private static WorthIt.DefenderProfile Body(float atk, float def, float hp) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, 4, null, hp);
        private static AttackObjective Candidate() => new AttackObjective {
            Target = Local, Opposition = new[] { new WorthIt.DefendingArmy(new[] { Body(1, 1, 2) }, default) } };
        private static AttackIntent Attack() => new AttackIntent {
            Target = Main, Phase = AttackMissionPhase.Assault, PrimaryArmyId = 7, OperationStarted = true, AssaultStarted = true };
        private static MissionIntent Intent() => new MissionIntent {
            IntentKey = MissionIntentKey.ForAttack(Main), Kind = MissionKind.Attack, Status = IntentStatus.Active,
            Funding = CommitmentTier.Hard, Objective = Attack() };
        private static MissionProposal Proposal(bool local) => new MissionProposal {
            Kind = MissionKind.Attack, PreferredMoverArmyId = 7, FromDurableIntent = true, DurableFundingTier = CommitmentTier.Hard,
            Target = new AttackMissionTarget { Phase = AttackMissionPhase.Assault, Target = Main,
                IntermediateTarget = local ? Local : AttackTargetRef.None, PrimaryArmyId = 7, ForceCommitted = true,
                DestinationHex = local ? Local.Hex : Main.Hex } };
        private static MissionStepResult Outcome() { var proposal = Proposal(true); return new MissionStepResult {
            Proposal = proposal, AttemptKey = StableMissionKey.For(proposal), IntentKey = MissionIntentKey.For(proposal),
            MissionKind = MissionKind.Attack,
            MoverArmyId = 7, Disposition = MissionStepDisposition.Progress, WasCommitment = true }
            .WithPayload(new AttackStepPayload { HasAttackPayload = true, AttackTarget = (AttackMissionTarget)proposal.Target }); }
        private static WorldSnapshot Snapshot(int seen = 6) {
            var roster = new[] { Body(30, 20, 30), Body(30, 20, 30) };
            return new WorldSnapshot { TurnNumber = 6, Observer = Us,
                Self = new SelfSnapshot { Armies = new[] { new ArmySnapshot { ArmyId = 7, Owner = Us,
                    Hex = new HexCoord(0, 0), CurrentMovement = 3, MaxMovement = 3, MemberCount = 2,
                    IsStructuralRaidActor = true, Members = roster, EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(roster) } },
                    BaseHexes = Array.Empty<HexCoord>() },
                Known = new KnownSnapshot { Buildings = new[] { new AiMapMemory.KnownBuilding(Base, Blue, false, null,
                    isBase: true, defense: 1, seenTurn: seen) }, EnemySightings = Array.Empty<AiMapMemory.KnownEnemySighting>() } };
        }
    }
}
#endif
