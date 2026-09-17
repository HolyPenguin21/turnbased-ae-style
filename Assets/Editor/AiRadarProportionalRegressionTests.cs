#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Regression coverage for the Radar §Task A/B/C rewrite (proportional
    // EffectiveValue = BaseValue * axisCount * weight, the removed BaseValue fallback in
    // ResourceAllocator.RankValue, and the single global admission order in Pack()).
    // Companion to AiRadarAdmissionRegressionTests.cs, which covers the pre-existing
    // Economy-lane-stays-radar-blind behaviour this rewrite must not disturb.
    public class AiRadarProportionalRegressionTests
    {
        private const float Tol = 0.01f;

        private static Radar RadarOf(params (DesireAxis axis, float weight)[] weights)
        {
            var r = new Radar();
            foreach (DesireAxis a in DesireAxes.All)
                r.Weight[a] = 0f;
            foreach ((DesireAxis axis, float weight) w in weights)
                r.Weight[w.axis] = w.weight;
            return r;
        }

        // Phase A uses the SAME proportional preference, without mutating demand.Value
        // or the intrinsic Play - Hold + urgency decision score.
        [Test]
        public void PhaseAArbitration_WeightsOnlyTheCompetitivePriority()
        {
            Radar radar = RadarOf((DesireAxis.Economy, 0f), (DesireAxis.Development, 1f));
            var economy = new DemandState { Demand = new AxisDemand { RequestingAxis = DesireAxis.Economy, Value = 30f } };
            var development = new DemandState { Demand = new AxisDemand { RequestingAxis = DesireAxis.Development, Value = 10f } };
            var cand = new DemandCandidate(null, 0f, 10f, 0f, 10f);
            Assert.That(MaterializationPortfolioSolver.ArbitrationScore(new PhaseACandidate(economy, cand), radar), Is.EqualTo(0f).Within(Tol));
            Assert.That(MaterializationPortfolioSolver.ArbitrationScore(new PhaseACandidate(development, cand), radar), Is.EqualTo(40f).Within(Tol));
            Assert.That(MaterializationPortfolioSolver.ArbitrationScore(new PhaseACandidate(economy, cand), Radar.Even()), Is.EqualTo(10f).Within(Tol));
            Assert.That(economy.Demand.Value, Is.EqualTo(30f));
            Assert.That(cand.DecisionScore, Is.EqualTo(10f));
        }

        // ---- Task A — pure formula, all six math scenarios from the spec's §4 table ----------

        [Test]
        public void EvenRadar_PreservesOriginalRatio()
        {
            Radar even = Radar.Even(); // 25/25/25/25
            Assert.That(30f * RadarValueScale.For(even, DesireAxis.Economy), Is.EqualTo(30f).Within(Tol));
            Assert.That(10f * RadarValueScale.For(even, DesireAxis.Aggression), Is.EqualTo(10f).Within(Tol));
        }

        [Test]
        public void ProportionalCompensation_25vs75_TurnsThirtyTenIntoThirtyThirty()
        {
            Radar radar = RadarOf((DesireAxis.Economy, 0.25f), (DesireAxis.Aggression, 0.75f));
            Assert.That(30f * RadarValueScale.For(radar, DesireAxis.Economy), Is.EqualTo(30f).Within(Tol));
            Assert.That(10f * RadarValueScale.For(radar, DesireAxis.Aggression), Is.EqualTo(30f).Within(Tol));
        }

        [Test]
        public void EqualWeights_50vs50_ScalesBothByTwoKeepingThreeToOneRatio()
        {
            Radar radar = RadarOf((DesireAxis.Economy, 0.50f), (DesireAxis.Aggression, 0.50f));
            float eco = 30f * RadarValueScale.For(radar, DesireAxis.Economy);
            float agg = 10f * RadarValueScale.For(radar, DesireAxis.Aggression);
            Assert.That(eco, Is.EqualTo(60f).Within(Tol));
            Assert.That(agg, Is.EqualTo(20f).Within(Tol));
            Assert.That(eco / agg, Is.EqualTo(3f).Within(Tol));
        }

        [Test]
        public void ZeroWeight_ScalesToExactlyZero_NoFloor()
        {
            Radar radar = RadarOf((DesireAxis.Economy, 0f), (DesireAxis.Aggression, 1f));
            Assert.That(30f * RadarValueScale.For(radar, DesireAxis.Economy), Is.EqualTo(0f).Within(Tol));
            Assert.That(7f * RadarValueScale.For(radar, DesireAxis.Aggression), Is.EqualTo(28f).Within(Tol));
        }

        [Test]
        public void AlmostZeroWeight_1v99_MatchesLinearProjection()
        {
            Radar radar = RadarOf((DesireAxis.Economy, 0.01f), (DesireAxis.Aggression, 0.99f));
            Assert.That(30f * RadarValueScale.For(radar, DesireAxis.Economy), Is.EqualTo(1.2f).Within(Tol));
            Assert.That(10f * RadarValueScale.For(radar, DesireAxis.Aggression), Is.EqualTo(39.6f).Within(Tol));
        }

        [Test]
        public void HighWeight_KeepsScalingPastTheOldQuarterCeiling()
        {
            // The old formula saturated at weight >= 1/axisCount (25%): 25%, 50% and 75% all
            // produced the same x1 coefficient. The new formula must keep distinguishing them.
            Radar r25 = RadarOf((DesireAxis.Economy, 0.25f));
            Radar r50 = RadarOf((DesireAxis.Economy, 0.50f));
            Radar r75 = RadarOf((DesireAxis.Economy, 0.75f));
            float s25 = RadarValueScale.For(r25, DesireAxis.Economy);
            float s50 = RadarValueScale.For(r50, DesireAxis.Economy);
            float s75 = RadarValueScale.For(r75, DesireAxis.Economy);
            Assert.That(s25, Is.EqualTo(1f).Within(Tol));
            Assert.That(s50, Is.EqualTo(2f).Within(Tol));
            Assert.That(s75, Is.EqualTo(3f).Within(Tol));
            Assert.That(s50, Is.GreaterThan(s25));
            Assert.That(s75, Is.GreaterThan(s50));
        }

        [Test]
        public void MultiAxisContribution_EqualContributions_BlendsTheirScalesEvenly()
        {
            // Contributions Economy=1, Aggression=1 at weights 25% / 75% -> mean of scale(0.25)=1
            // and scale(0.75)=3 is 2: a x2 coefficient on the mission, per the spec's worked example.
            Radar radar = RadarOf((DesireAxis.Economy, 0.25f), (DesireAxis.Aggression, 0.75f));
            var m = new MissionProposal { BaseValue = 10f };
            m.Axes.Value[DesireAxis.Economy] = 1f;
            m.Axes.Value[DesireAxis.Aggression] = 1f;
            Assert.That(RadarValueScale.For(radar, m), Is.EqualTo(2f).Within(Tol));
        }

        [Test]
        public void MissingAxis_IsNeutralButExplicitZeroWeightIsNot()
        {
            Radar radar = RadarOf((DesireAxis.Recon, 0f), (DesireAxis.Economy, 1f));
            var mission = new MissionProposal { BaseValue = 12f };
            Assert.That(RadarValueScale.For(radar, mission), Is.EqualTo(1f).Within(Tol));
            mission.Axes.Value[DesireAxis.Recon] = -1f;
            Assert.That(RadarValueScale.For(radar, mission), Is.EqualTo(1f).Within(Tol));
            mission.Axes.Value[DesireAxis.Recon] = 1f;
            Assert.That(RadarValueScale.For(radar, mission), Is.EqualTo(0f).Within(Tol));
        }

        [Test]
        public void PhaseA_ColdNewDemandDefersButActiveEconomyAndRaidSupportDoNot()
        {
            Radar cold = RadarOf((DesireAxis.Recon, 1f),
                (DesireAxis.Economy, 0f), (DesireAxis.Aggression, 0f));
            var extraction = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = new HexCoord(2, 1),
            };
            Assert.That(StrategicPhaseA.ShouldDeferFreshZeroRadarDemand(cold, extraction, null), Is.True);
            Assert.That(StrategicPhaseA.ShouldDeferFreshZeroRadarDemand(Radar.Even(), extraction, null), Is.False);
            var activeBuild = new MissionIntent
            {
                Kind = MissionKind.Economy, Status = IntentStatus.Active,
                Objective = new EconomyIntent { Kind = EconomyTaskKind.BuildExtraction,
                    TargetHex = new HexCoord(2, 1) },
            };
            Assert.That(StrategicPhaseA.ShouldDeferFreshZeroRadarDemand(cold, extraction,
                new[] { activeBuild }), Is.False, "an existing build commitment retains its protection");

            MissionIntentKey raidKey = MissionIntentKey.ForRaid(
                RaidTargetRef.ForEventGuard(new HexCoord(7, 0)));
            var reinforcement = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                ConsumerIntentKey = raidKey,
            };
            Assert.That(StrategicPhaseA.ShouldDeferFreshZeroRadarDemand(cold, reinforcement, null), Is.True);
            var activeRaid = new MissionIntent
            {
                Kind = MissionKind.Raid, Status = IntentStatus.Active, IntentKey = raidKey,
            };
            Assert.That(StrategicPhaseA.ShouldDeferFreshZeroRadarDemand(cold, reinforcement,
                new[] { activeRaid }), Is.False, "reinforcement belongs to an active Raid, not a new cold-axis task");
        }

        // ---- Task B — the EffectiveValue==0 -> BaseValue fallback bug, through the REAL allocator

        [Test]
        public void Allocator_ZeroEffectiveValueNeverFallsBackToBaseValue()
        {
            // Extraction: BaseValue 30, radar 0% -> EffectiveValue 0. Raid: BaseValue 7, radar
            // 100% -> EffectiveValue 28. Only 1 AP: the pre-fix RankValue fallback would have
            // silently ranked Extraction at its BaseValue (30) and funded it over Raid (28).
            Radar radar = RadarOf((DesireAxis.Economy, 0f), (DesireAxis.Aggression, 1f));
            var player = new PlayerSetupData { Nickname = "RadarZeroFallback" };
            var snapshot = new WorldSnapshot { TurnNumber = 1, Self = new SelfSnapshot { ActionPoints = 1 } };

            MissionProposal extraction = EconomyProposal(30f, new HexCoord(1, 0), radar, etaTurns: 1);
            MissionProposal raid = RaidProposal(7f, new HexCoord(9, 0), radar);

            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar,
                new List<MissionProposal> { extraction, raid }, new List<Commitment>(), player);
            TentativeAllocation allocation = session.Pack();

            Assert.That(allocation.Funded.Count, Is.EqualTo(1));
            Assert.That(allocation.Funded[0].Mission.Kind, Is.EqualTo(MissionKind.Raid),
                "a zero EffectiveValue proposal must never outrank a positive one via a BaseValue fallback");
        }

        [Test]
        public void Allocator_ZeroWeightTaskStillFundableFromResidualBudget()
        {
            // Same shape, but with enough AP for BOTH: a zero competitive priority must not be
            // treated as "forbidden" — it should still be funded once nothing else needs the AP.
            Radar radar = RadarOf((DesireAxis.Economy, 0f), (DesireAxis.Aggression, 1f));
            var player = new PlayerSetupData { Nickname = "RadarZeroResidual" };
            var snapshot = new WorldSnapshot { TurnNumber = 1, Self = new SelfSnapshot { ActionPoints = 2 } };

            MissionProposal extraction = EconomyProposal(30f, new HexCoord(1, 0), radar, etaTurns: 1);
            MissionProposal raid = RaidProposal(7f, new HexCoord(9, 0), radar);

            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar,
                new List<MissionProposal> { extraction, raid }, new List<Commitment>(), player);
            TentativeAllocation allocation = session.Pack();

            Assert.That(allocation.Funded.Count, Is.EqualTo(2),
                "a zero-weight axis is a zero PRIORITY, not a ban — it must still use leftover AP");
        }

        // ---- Task C — global comparison across the whole admitted pool, not just lane heads -----

        [Test]
        public void Allocator_GlobalComparisonSurfacesTheHigherValueSameLaneCandidate()
        {
            // Reproduces the spec's synthetic example: Economy A completes this turn (BaseValue 8,
            // same-turn bonus -> local AdmissionRank 16) sits ahead of Economy B (BaseValue 12, no
            // bonus) in the OLD per-lane queue, so the old head-only merge would compare A(8) against
            // Scout C(10), fund C, and never even look at B(12) before the 1 AP ran out.
            Radar radar = Radar.Even(); // scale == 1 on every axis, so EffectiveValue == BaseValue here.
            var player = new PlayerSetupData { Nickname = "RadarGlobalQueue" };
            var snapshot = new WorldSnapshot { TurnNumber = 1, Self = new SelfSnapshot { ActionPoints = 1 } };

            MissionProposal a = EconomyProposal(8f, new HexCoord(1, 0), radar, etaTurns: 0);
            MissionProposal b = EconomyProposal(12f, new HexCoord(4, 0), radar, etaTurns: 1);
            var c = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(9, 0) },
                BaseValue = 10f,
                LocalAdmissionScore = 10f,
                Requirements = OneAp(),
            };
            c.Axes.Value[DesireAxis.Recon] = 1f;
            c.EffectiveValue = c.BaseValue * RadarValueScale.For(radar, c);

            Assert.That(MissionAdmissionPolicy.AdmissionRank(a), Is.EqualTo(16f).Within(Tol));
            Assert.That(a.EffectiveValue, Is.EqualTo(8f).Within(Tol));
            Assert.That(b.EffectiveValue, Is.EqualTo(12f).Within(Tol));

            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar,
                new List<MissionProposal> { a, b, c }, new List<Commitment>(), player);
            TentativeAllocation allocation = session.Pack();

            Assert.That(allocation.Funded.Count, Is.EqualTo(1));
            Assert.That(allocation.Funded[0].Mission, Is.SameAs(b),
                "B has the highest cross-lane EffectiveValue and must not be hidden behind A's local admission bonus");
        }

        [Test]
        public void Allocator_DeterministicTieBreakOnEqualEffectiveValue()
        {
            Radar radar = Radar.Even();
            var player = new PlayerSetupData { Nickname = "RadarTieBreak" };
            var snapshot = new WorldSnapshot { TurnNumber = 1, Self = new SelfSnapshot { ActionPoints = 1 } };

            MissionProposal a = EconomyProposal(10f, new HexCoord(1, 0), radar, etaTurns: 1);
            MissionProposal b = EconomyProposal(10f, new HexCoord(4, 0), radar, etaTurns: 1);

            AllocationSession session1 = ResourceAllocator.BeginTurn(snapshot, radar,
                new List<MissionProposal> { a, b }, new List<Commitment>(), player);
            MissionProposal winner1 = session1.Pack().Funded[0].Mission;

            AllocationSession session2 = ResourceAllocator.BeginTurn(snapshot, radar,
                new List<MissionProposal> { b, a }, new List<Commitment>(), player);
            MissionProposal winner2 = session2.Pack().Funded[0].Mission;

            Assert.That(winner1, Is.SameAs(winner2),
                "equal EffectiveValue must resolve to the same winner regardless of input order");
        }

        private static MissionProposal EconomyProposal(float baseValue, HexCoord hex, Radar radar, int etaTurns)
        {
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Economy,
                Target = new EconomyMissionTarget { Kind = EconomyTaskKind.BuildExtraction, TargetHex = hex },
                BaseValue = baseValue,
                LocalAdmissionScore = baseValue,
                Requirements = new MissionRequirements { ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f, EtaTurns = etaTurns },
            };
            proposal.Axes.Value[DesireAxis.Economy] = 1f;
            proposal.EffectiveValue = proposal.BaseValue * RadarValueScale.For(radar, proposal);
            return proposal;
        }

        private static MissionProposal RaidProposal(float baseValue, HexCoord hex, Radar radar)
        {
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Raid,
                Target = new RaidMissionTarget { Target = RaidTargetRef.ForEventGuard(hex), DestinationHex = hex },
                BaseValue = baseValue,
                LocalAdmissionScore = baseValue,
                Requirements = OneAp(),
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposal.EffectiveValue = proposal.BaseValue * RadarValueScale.For(radar, proposal);
            return proposal;
        }

        private static MissionRequirements OneAp() => new MissionRequirements
        {
            ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f, EtaTurns = 1,
        };
    }
}
#endif
