#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Pins how Continuity writes a Scout outcome's payload into the durable ScoutIntent (create,
    // advance, absorb) and how ActorCommitments re-derives an off-list incumbent's stealth
    // requirement — the behaviour the recon refactor must keep while it merges those copies.
    public class AiReconContinuityPayloadTests
    {
        [TearDown]
        public void ClearState() => MissionIntentRegistry.Clear();

        [Test]
        public void FreshRefresh_CreatesAnUnfundedRoleFromThePayload()
        {
            var player = new PlayerSetupData { Nickname = "Recon payload" };
            MissionTurnOutcome o = Outcome(ScoutTargetKind.Refresh, new HexCoord(5, 1), mover: 10);
            o.ScoutRequiresStealth = true;

            MissionContinuityLayer.ReconcileStep(player, 4, o);

            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(o.IntentKey, out MissionIntent i), Is.True);
            Assert.That(i.Funding, Is.EqualTo(CommitmentTier.None));
            Assert.That(i.PreferredMoverArmyId, Is.EqualTo(10));
            Assert.That(i.Scout.Kind, Is.EqualTo(ScoutTargetKind.Refresh));
            Assert.That(i.Scout.FocusHex, Is.EqualTo(new HexCoord(5, 1)));
            Assert.That(i.Scout.RequiresStealth, Is.True);
            Assert.That(i.Scout.TrackedArmyId, Is.Null);
        }

        [Test]
        public void FreshSurveil_CreatesASoftRoleWithItsContactAndBaseline()
        {
            var player = new PlayerSetupData { Nickname = "Recon payload" };
            MissionTurnOutcome o = Outcome(ScoutTargetKind.Surveil, new HexCoord(6, 6), mover: 10);
            o.TrackedArmyId = 99;
            o.BaselineObservedTurn = 3;
            o.IntentKey = new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil, 99, 0, 0);

            MissionContinuityLayer.ReconcileStep(player, 4, o);

            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(o.IntentKey, out MissionIntent i), Is.True);
            Assert.That(i.Funding, Is.EqualTo(CommitmentTier.Soft));
            Assert.That(i.Scout.TrackedArmyId, Is.EqualTo(99));
            Assert.That(i.Scout.BaselineObservedTurn, Is.EqualTo(3));
        }

        [Test]
        public void ContinuingRole_TakesTheProvisionedStealthRequirement()
        {
            var player = new PlayerSetupData { Nickname = "Recon payload" };
            HexCoord focus = new HexCoord(4, 3);
            MissionIntent incumbent = AiReconAuditBugTests.Incumbent(focus, preferredMover: 10);
            MissionIntentRegistry.GetOrCreate(player).Put(incumbent);
            MissionTurnOutcome o = Outcome(ScoutTargetKind.Explore, focus, mover: 10);
            o.ScoutRequiresStealth = true;

            MissionContinuityLayer.ReconcileStep(player, 4, o);

            Assert.That(incumbent.Scout.RequiresStealth, Is.True);
            Assert.That(incumbent.Scout.FocusHex, Is.EqualTo(focus));
            Assert.That(incumbent.LastProgressTurn, Is.EqualTo(4));
        }

        [Test]
        public void SurveilRoleAbsorbingAnExplore_DropsItsContactAndSoftFunding()
        {
            var player = new PlayerSetupData { Nickname = "Recon payload" };
            var surveil = new MissionIntent
            {
                Kind = MissionKind.Scout, Funding = CommitmentTier.Soft, Status = IntentStatus.Active,
                Objective = new ScoutIntent
                {
                    Kind = ScoutTargetKind.Surveil, FocusHex = new HexCoord(6, 6),
                    TrackedArmyId = 99, BaselineObservedTurn = 3, RequiresStealth = true,
                },
                PreferredMoverArmyId = 10,
            };
            surveil.IntentKey = MissionIntentKey.For(surveil);
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            state.Put(surveil);
            MissionTurnOutcome o = Outcome(ScoutTargetKind.Explore, new HexCoord(2, 2), mover: 10);

            MissionContinuityLayer.ReconcileStep(player, 4, o);

            Assert.That(state.TryGet(o.IntentKey, out MissionIntent absorbed), Is.True);
            Assert.That(absorbed, Is.SameAs(surveil));
            Assert.That(absorbed.Scout.Kind, Is.EqualTo(ScoutTargetKind.Explore));
            Assert.That(absorbed.Scout.TrackedArmyId, Is.Null);
            Assert.That(absorbed.Scout.RequiresStealth, Is.False);
            Assert.That(absorbed.Funding, Is.EqualTo(CommitmentTier.None));
        }

        // An incumbent whose Explore focus has left the frozen objective list is re-derived from the
        // snapshot: an exposed focus requires stealth, so only a stealth-capable scout keeps the claim.
        [Test]
        public void OffListExposedExplore_ClaimsOnlyAStealthCapableScout()
        {
            var player = new PlayerSetupData { Nickname = "Recon payload" };
            var enemy = new PlayerSetupData { Nickname = "Enemy" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(player, 11, focus);
            ((List<ArmySnapshot>)snap.Self.Armies)[1].CanEnterStealth = true;   // #20
            snap.Known = new KnownSnapshot
            {
                EnemySightings = new List<AiMapMemory.KnownEnemySighting>
                {
                    new AiMapMemory.KnownEnemySighting(new HexCoord(5, 3), enemy, "raiders", 3, 6f, 6f,
                        null, seenTurn: 11, armyId: 77),
                },
            };
            MissionIntent onTen = AiReconAuditBugTests.Incumbent(focus, preferredMover: 10);
            MissionIntent onTwenty = AiReconAuditBugTests.Incumbent(new HexCoord(3, 4), preferredMover: 20);

            ActorCommitments c = ActorCommitments.FromIntents(new[] { onTen, onTwenty }, snap,
                new List<ReconObjective>());

            Assert.That(c.IsArmyClaimed(10), Is.False, "a visible scout cannot hold an exposed Explore");
            Assert.That(c.IsArmyClaimed(20), Is.True);
        }

        private static MissionTurnOutcome Outcome(ScoutTargetKind kind, HexCoord focus, int mover)
        {
            var target = new ScoutMissionTarget { Kind = kind, FocusHex = focus };
            var proposal = new MissionProposal { Kind = MissionKind.Scout, Target = target, BaseValue = 5f };
            return new MissionTurnOutcome
            {
                AttemptKey = StableMissionKey.For(proposal),
                IntentKey = MissionIntentKey.For(proposal),
                Proposal = proposal,
                MissionKind = MissionKind.Scout,
                Outcome = ExecutionOutcome.ProductiveStop,
                MadeProgress = true,
                StepsMoved = 1,
                HasScoutPayload = true,
                MoverArmyId = mover,
                ScoutKind = kind,
                FocusHex = focus,
            };
        }
    }
}
#endif
