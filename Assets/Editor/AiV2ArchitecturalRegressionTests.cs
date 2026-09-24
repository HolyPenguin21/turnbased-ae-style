#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiV2ArchitecturalRegressionTests
    {
        [SetUp]
        public void SetUp()
        {
            ResourceStarvationRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
        }

        [Test]
        public void ReconConcurrency_HasZeroLaneAndFrontierCannotBypassCanonicalValue()
        {
            var snap = new WorldSnapshot { MapKnowledge = new MapKnowledgeSnapshot
            {
                ExplorableUnknownFrac = 1f,
                Frontier = new[]
                {
                    new FrontierHexSnapshot { Hex = new HexCoord(0, 0) },
                    new FrontierHexSnapshot { Hex = new HexCoord(20, 20) },
                },
            } };
            Assert.That(ReconConcurrencyPolicy.DesiredTotal(snap, new[] { Recon(0f) }), Is.Zero);
            Assert.That(ReconConcurrencyPolicy.DesiredTotal(snap,
                new[] { Recon(12f), Recon(AiConfigV2.taskScoreUrgencyRampLo) }), Is.EqualTo(1));
        }

        [Test]
        public void ReconConcurrency_ValuableLateRefreshCanReactivateOneLane()
        {
            var snap = new WorldSnapshot { MapKnowledge = new MapKnowledgeSnapshot
            {
                ExplorableUnknownFrac = 0f,
                Frontier = new List<FrontierHexSnapshot>(),
            } };
            ReconObjective refresh = Recon(12f);
            refresh.Kind = ReconObjectiveKind.Refresh;
            Assert.That(ReconConcurrencyPolicy.DesiredTotal(snap, new[] { refresh }), Is.EqualTo(1));
        }

        [Test]
        public void StarvationPressure_OneResourceTurnAddsOneHitAndKeepsStrongestEvidence()
        {
            var player = new PlayerSetupData { Nickname = "StarvationIdempotence" };
            ResourceStarvationRegistry.DecayOncePerTurn(player, 8);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Energy,
                5f, 1f, 0f, 6f, 8);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Energy,
                9f, 1f, 0f, 11f, 8);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Energy,
                7f, 1f, 0f, 8f, 8);

            Assert.That(ResourceStarvationRegistry.Pressure(player, ResourceType.Energy),
                Is.EqualTo(AiConfigV2.starvationHitGain).Within(0.0001f));
            Assert.That(ResourceStarvationRegistry.TryGetCurrentBlock(player, ResourceType.Energy,
                8, out ResourceBlockEvidence evidence), Is.True);
            Assert.That(evidence.DemandValue, Is.EqualTo(11f));
            Assert.That(evidence.Required, Is.EqualTo(9f));
        }

        [Test]
        public void GroundCombatAdmission_DistinctAssignmentOverridesSharedPlanningWitness()
        {
            var owner = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            MissionProposal raid = Raid(101, 1);
            MissionProposal attack = Attack(owner, new HexCoord(5, 0), 1);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(raid, new[] { 1, 2 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(attack, new[] { 1, 2 });
            Assert.That(MissionAdmissionPolicy.Conflicts(raid, attack), Is.False);
        }

        [Test]
        public void GroundCombatAdmission_StillConflictsWithoutDistinctAssignment()
        {
            var owner = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            MissionProposal a = Attack(owner, new HexCoord(5, 0), 1);
            MissionProposal b = Attack(owner, new HexCoord(6, 0), 1);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(a, new[] { 1 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(b, new[] { 1 });
            Assert.That(MissionAdmissionPolicy.Conflicts(a, b), Is.True);
        }

        [TestCase(11f, 20f, MissionKind.Attack)]
        [TestCase(20f, 11f, MissionKind.Raid)]
        public void CompletedRaidFallback_DoesNotPreemptFreshGlobalAggressionChoice(
            float nextRaidValue, float attackValue, MissionKind expected)
        {
            var player = new PlayerSetupData { Nickname = "FreshAggressionChoice", ColorIndex = 1 };
            var enemy = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            MissionProposal fallback = Raid(100, 1);
            fallback.Target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Return,
                Target = RaidTargetRef.ForNeutralArmy(100),
                DestinationHex = new HexCoord(0, 0),
            };
            SetValueAndCost(fallback, 0f);

            MissionProposal nextRaid = Raid(101, 1);
            MissionProposal attack = Attack(enemy, new HexCoord(5, 0), 1);
            SetValueAndCost(nextRaid, nextRaidValue);
            SetValueAndCost(attack, attackValue);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(nextRaid, new[] { 1 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(attack, new[] { 1 });

            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot { ActionPoints = 1 },
            };
            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { fallback, nextRaid, attack },
                new List<Commitment>(), player).Pack();

            Assert.That(allocation.Funded, Has.Count.EqualTo(1));
            Assert.That(allocation.Funded[0].Mission.Kind, Is.EqualTo(expected));
        }

        [Test]
        public void TacticalAttackSignificance_ExcludesRecceAndCountsMixedCombatBodiesOnly()
        {
            var scout = new WorthIt.DefenderProfile(40f, false, null, 40f, 5f, 1,
                new[] { "r1s4" }, 5f);
            var combat = new WorthIt.DefenderProfile(4f, false,
                new[] { UnitTypeTag.Infantry }, 6f, 10f, 2);
            Assert.That(AttackTacticalOpportunity.RawCombatBodyStrength(new[] { scout }), Is.Zero);
            Assert.That(AttackTacticalOpportunity.RawCombatBodyStrength(new[] { scout, combat }),
                Is.EqualTo(10f));
        }

        private static ReconObjective Recon(float value) => new ReconObjective
        {
            Kind = ReconObjectiveKind.Explore, FocusHex = new HexCoord(1, 1), BaseValue = value,
        };

        private static MissionProposal Raid(int targetId, int preferred) => new MissionProposal
        {
            Kind = MissionKind.Raid, PreferredMoverArmyId = preferred,
            Target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Assault,
                Target = RaidTargetRef.ForNeutralArmy(targetId),
            },
        };

        private static MissionProposal Attack(PlayerSetupData owner, HexCoord hex, int preferred) =>
            new MissionProposal
            {
                Kind = MissionKind.Attack, PreferredMoverArmyId = preferred,
                Target = new AttackMissionTarget
                {
                    Phase = AttackMissionPhase.Assault,
                    Target = AttackTargetRef.For(hex, owner, AttackTargetKind.Base),
                },
            };

        private static void SetValueAndCost(MissionProposal proposal, float value)
        {
            proposal.BaseValue = value;
            proposal.LocalAdmissionScore = value;
            proposal.EffectiveValue = value;
            proposal.Requirements = new MissionRequirements
            {
                ApMinimum = 1f,
                ApDesired = 1f,
                ApMaximum = 1f,
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
        }
    }
}
#endif
