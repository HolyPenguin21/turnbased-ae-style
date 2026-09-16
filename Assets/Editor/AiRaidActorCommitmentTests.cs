#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiRaidActorCommitmentTests
    {
        private static MissionProposal Assault(int targetId = 42) => new MissionProposal
        {
            Kind = MissionKind.Raid,
            Target = new RaidMissionTarget { Target = RaidTargetRef.ForNeutralArmy(targetId) },
        };

        [Test]
        public void UnassignedRaid_DoesNotFallBackToFreeArmySearch()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    ArmyId = 9, IsStructuralRaidActor = true, MemberCount = 1,
                    CurrentMovement = 3, Members = Array.Empty<WorthIt.DefenderProfile>(),
                } },
            } };
            var session = new ProvisioningSession(snap);
            var mission = Assault();
            var claimed = new ActorCommitments();
            claimed.Claim(9); // Economy owns the only viable ground actor.
            session.SetRaidConstraints(claimed, new HashSet<int>());

            GroundCombatAssemblyPlan plan = RaidProvisioner.PlanAssignedAssault(
                session, mission, Array.Empty<WorthIt.DefenderProfile>(), out ProvisionFailure failure);

            Assert.That(plan, Is.Null);
            Assert.That(failure.Kind, Is.EqualTo(ProvisionFailureKind.MoverContended));
            Assert.That(session.ExcludedForRaid(mission), Does.Contain(9));
        }

        [Test]
        public void AssignedRaid_RejectsActorThatBecameDurablyClaimed()
        {
            var session = new ProvisioningSession(new WorldSnapshot());
            MissionProposal mission = Assault();
            session.SetRaidAssignment(new Dictionary<StableMissionKey, int>
            {
                { StableMissionKey.For(mission), 9 },
            });
            var claimed = new ActorCommitments();
            claimed.Claim(9);
            session.SetRaidConstraints(claimed, new HashSet<int>());

            GroundCombatAssemblyPlan plan = RaidProvisioner.PlanAssignedAssault(
                session, mission, Array.Empty<WorthIt.DefenderProfile>(), out ProvisionFailure failure);

            Assert.That(plan, Is.Null);
            Assert.That(failure.Kind, Is.EqualTo(ProvisionFailureKind.MoverContended));
        }

        [Test]
        public void IncumbentExemptsOnlyItsOwnActor_NotOtherMissionsOrDonors()
        {
            var session = new ProvisioningSession(new WorldSnapshot());
            MissionProposal incumbent = Assault();
            incumbent.FromDurableIntent = true;
            incumbent.PreferredMoverArmyId = 9;
            var claimed = new ActorCommitments();
            claimed.Claim(9);
            claimed.Claim(10); // Other Economy/Recon operation, including donor eligibility.
            session.SetRaidConstraints(claimed, new HashSet<int> { 11 });
            session.SetRaidAssignment(new Dictionary<StableMissionKey, int>
            {
                { StableMissionKey.For(incumbent), 9 },
                { StableMissionKey.For(Assault(43)), 13 }, // Funded Raid not yet provisioned.
            });

            HashSet<int> excluded = session.ExcludedForRaid(incumbent);
            Assert.That(excluded, Does.Not.Contain(9));
            Assert.That(excluded, Does.Contain(10));
            Assert.That(excluded, Does.Contain(11));
            Assert.That(excluded, Does.Contain(13)); // Cannot borrow another Raid's host.
            session.ClaimedArmyIds.Add(12);
            Assert.That(session.ExcludedForRaid(incumbent), Does.Contain(12));
        }

        [Test]
        public void UnassignedRaid_CannotIgnoreBatchDecisionEvenWithFreeForce()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    ArmyId = 9, IsStructuralRaidActor = true, MemberCount = 1,
                    CurrentMovement = 3, Members = Array.Empty<WorthIt.DefenderProfile>(),
                } },
            } };
            var session = new ProvisioningSession(snap);
            MissionProposal mission = Assault();
            session.SetRaidConstraints(new ActorCommitments(), new HashSet<int>());

            GroundCombatAssemblyPlan plan = RaidProvisioner.PlanAssignedAssault(
                session, mission, Array.Empty<WorthIt.DefenderProfile>(), out ProvisionFailure failure);

            Assert.That(plan, Is.Null);
            Assert.That(failure.Kind, Is.EqualTo(ProvisionFailureKind.MoverContended));
        }

        [Test]
        public void AssignedFreeArmy_RemainsPinnedAndExecutesWithoutReassignment()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    ArmyId = 9, IsStructuralRaidActor = true, MemberCount = 1,
                    CurrentMovement = 3,
                    Members = new[] { new WorthIt.DefenderProfile(
                        defense: 1f, hasCeramicArmor: false, attack: 20f,
                        hitPoints: 20f, maxHitPoints: 20f) },
                } },
            } };
            var session = new ProvisioningSession(snap);
            MissionProposal mission = Assault();
            session.SetRaidConstraints(new ActorCommitments(), new HashSet<int>());
            session.SetRaidAssignment(new Dictionary<StableMissionKey, int>
            {
                { StableMissionKey.For(mission), 9 },
            });

            GroundCombatAssemblyPlan plan = RaidProvisioner.PlanAssignedAssault(
                session, mission, Array.Empty<WorthIt.DefenderProfile>(), out _);

            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.Feasible, Is.True);
            Assert.That(plan.BaseArmyId, Is.EqualTo(9));
        }

        [Test]
        public void AdmissionExcludesCommittedActor_BeforeRaidFunding()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    ArmyId = 9, IsStructuralRaidActor = true, MemberCount = 1,
                    CurrentMovement = 3, Members = Array.Empty<WorthIt.DefenderProfile>(),
                } },
            } };
            MissionProposal mission = Assault();
            GroundCombatAdmissionRegistry.Record(mission, snap, new HashSet<int> { 9 });
            Assert.That(GroundCombatAdmissionRegistry.EligibleIds(mission), Is.EqualTo("none"));
        }
    }
}
#endif
