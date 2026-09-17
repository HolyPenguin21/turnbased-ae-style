#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiRadarAdmissionRegressionTests
    {
        [Test]
        public void EconomyLane_ChangingRadarDoesNotReverseTheSameTwoTasks()
        {
            // Intrinsic 12 > intrinsic 10 + urgency 1. The old admission implementation
            // ranked them 4.2 and 4.5 when Economy radar was cold, reversing the lane.
            Assert.That(FundedEconomyKind(Radar.Even()), Is.EqualTo(EconomyTaskKind.BuildExtraction));

            var cold = new Radar();
            cold.Weight[DesireAxis.Recon] = 1f;
            cold.Weight[DesireAxis.Economy] = 0f;
            // Radar model #2 (proportional) — a cold axis scales to EXACTLY zero, not a 0.35 floor.
            // Both proposals collapse to EffectiveValue 0 here; the allocator's tie-break then falls
            // back to the (still radar-blind) within-lane AdmissionRank, so the lane order itself
            // is unchanged — this is the property under test, not the zero-scale value.
            Assert.That(RadarValueScale.For(cold, DesireAxis.Economy), Is.EqualTo(0f).Within(0.0001f));
            Assert.That(FundedEconomyKind(cold), Is.EqualTo(EconomyTaskKind.BuildExtraction),
                "Radar must not change order inside the Economy lane");
        }

        [Test]
        public void CrossLane_ChangingRadarStillChangesSharedApCompetition()
        {
            Assert.That(FundedCrossLaneKind(Radar.Even()), Is.EqualTo(MissionKind.Economy));

            var cold = new Radar();
            cold.Weight[DesireAxis.Recon] = 1f;
            cold.Weight[DesireAxis.Economy] = 0f;
            Assert.That(FundedCrossLaneKind(cold), Is.EqualTo(MissionKind.Scout),
                "the common allocator must continue comparing Radar-scaled EffectiveValue");
        }

        private static EconomyTaskKind FundedEconomyKind(Radar radar)
        {
            var player = new PlayerSetupData { Nickname = "RadarEconomyLane" };
            var snapshot = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot { ActionPoints = 1 },
            };
            MissionProposal extraction = Economy(EconomyTaskKind.BuildExtraction,
                new HexCoord(1, 0), intrinsic: 12f, urgency: 0f, radar: radar);
            MissionProposal expansion = Economy(EconomyTaskKind.FoundBase,
                new HexCoord(4, 0), intrinsic: 10f, urgency: 1f, radar: radar);
            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar,
                new List<MissionProposal> { extraction, expansion }, new List<Commitment>(), player);
            TentativeAllocation allocation = session.Pack();
            Assert.That(allocation.Funded.Count, Is.EqualTo(1));
            Assert.That(allocation.Deferred.Count, Is.GreaterThanOrEqualTo(1));
            return ((EconomyMissionTarget)allocation.Funded.Single().Mission.Target).Kind;
        }

        private static MissionKind FundedCrossLaneKind(Radar radar)
        {
            var player = new PlayerSetupData { Nickname = "RadarCrossLane" };
            var snapshot = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot { ActionPoints = 1 },
            };
            MissionProposal economy = Economy(EconomyTaskKind.BuildExtraction,
                new HexCoord(1, 0), intrinsic: 12f, urgency: 0f, radar: radar);
            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget
                {
                    Kind = ScoutTargetKind.Explore,
                    FocusHex = new HexCoord(7, 0),
                },
                BaseValue = 11f,
                LocalAdmissionScore = 11f,
                Requirements = OneAp(),
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;
            scout.EffectiveValue = scout.BaseValue * RadarValueScale.For(radar, scout);

            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar,
                new List<MissionProposal> { economy, scout }, new List<Commitment>(), player);
            TentativeAllocation allocation = session.Pack();
            Assert.That(allocation.Funded.Count, Is.EqualTo(1));
            return allocation.Funded.Single().Mission.Kind;
        }

        private static MissionProposal Economy(EconomyTaskKind kind, HexCoord hex,
            float intrinsic, float urgency, Radar radar)
        {
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Economy,
                Target = new EconomyMissionTarget { Kind = kind, TargetHex = hex },
                BaseValue = intrinsic,
                LocalAdmissionScore = intrinsic + urgency,
                Requirements = OneAp(),
            };
            proposal.Axes.Value[DesireAxis.Economy] = 1f;
            proposal.EffectiveValue = proposal.BaseValue * RadarValueScale.For(radar, proposal);
            return proposal;
        }

        private static MissionRequirements OneAp() => new MissionRequirements
        {
            ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
            EtaTurns = 1,
        };
    }
}
#endif
