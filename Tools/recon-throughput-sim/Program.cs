using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Ai.V2.Initiative;
using Game.HexGrid;
using Game.Players;

namespace ReconThroughputSim
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Scenario01_DarkMapHighSecondJobWantsTwoLanes();
            Scenario02_WeakSecondJobDoesNotBecomeProductionTarget();
            Scenario03_LateMapDoesNotBuySecondScout();
            Scenario04_OnePhysicalScoutCannotFundTwoReconJobs();
            Scenario05_TwoPhysicalScoutsMayFundTwoSeparatedJobs();
            Scenario06_SpatialConflictStillWinsWithTwoScouts();
            Scenario07_StrandedCapacityRelaxesSurplusThreshold();
            Scenario08_NearReserveKeepsConservativeSurplusThreshold();
            Scenario09_InitiativeLabelsHighLeftoverAsNonApBottleneck();
            Scenario10_InitiativeLabelsRealStarvationAsApLimited();

            Console.WriteLine();
            Console.WriteLine($"recon-throughput-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Scenario01_DarkMapHighSecondJobWantsTwoLanes()
        {
            WorldSnapshot snap = Snap(0.96f, Array.Empty<ArmySnapshot>());
            var jobs = new List<ReconObjective>
            {
                Explore(H(8, 4), 65f),
                Explore(H(10, 3), 63f),
            };
            Check("01 dark map + valuable second job desires two Recon lanes",
                ReconConcurrencyPolicy.DesiredTotal(snap, jobs) == 2);
        }

        private static void Scenario02_WeakSecondJobDoesNotBecomeProductionTarget()
        {
            WorldSnapshot snap = Snap(0.96f, Array.Empty<ArmySnapshot>());
            var jobs = new List<ReconObjective>
            {
                Explore(H(8, 4), 65f),
                Explore(H(10, 3), 35f),
            };
            Check("02 weak marginal objective desires one Recon lane",
                ReconConcurrencyPolicy.DesiredTotal(snap, jobs) == 1);
        }

        private static void Scenario03_LateMapDoesNotBuySecondScout()
        {
            WorldSnapshot snap = Snap(0.20f, Array.Empty<ArmySnapshot>());
            var jobs = new List<ReconObjective>
            {
                Explore(H(8, 4), 65f),
                Explore(H(10, 3), 65f),
            };
            Check("03 mostly explored map desires one Recon lane",
                ReconConcurrencyPolicy.DesiredTotal(snap, jobs) == 1);
        }

        private static void Scenario04_OnePhysicalScoutCannotFundTwoReconJobs()
        {
            PlayerSetupData me = Player("S4");
            WorldSnapshot snap = Snap(0.9f, new[] { Scout(me, 11, H(9, 4)) });
            MissionProposal a = ScoutProposal(snap, H(6, 4));
            MissionProposal b = ScoutProposal(snap, H(10, 4));
            Check("04 one eligible physical scout makes separated Recon pair conflict",
                MissionAdmissionPolicy.Conflicts(a, b));
        }

        private static void Scenario05_TwoPhysicalScoutsMayFundTwoSeparatedJobs()
        {
            PlayerSetupData me = Player("S5");
            WorldSnapshot snap = Snap(0.9f, new[]
            {
                Scout(me, 21, H(9, 4)),
                Scout(me, 22, H(8, 4)),
            });
            MissionProposal a = ScoutProposal(snap, H(5, 4));
            MissionProposal b = ScoutProposal(snap, H(11, 4));
            Check("05 two eligible physical scouts permit two separated Recon jobs",
                !MissionAdmissionPolicy.Conflicts(a, b));
        }

        private static void Scenario06_SpatialConflictStillWinsWithTwoScouts()
        {
            PlayerSetupData me = Player("S6");
            WorldSnapshot snap = Snap(0.9f, new[]
            {
                Scout(me, 31, H(9, 4)),
                Scout(me, 32, H(8, 4)),
            });
            MissionProposal a = ScoutProposal(snap, H(8, 3));
            MissionProposal b = ScoutProposal(snap, H(9, 3));
            Check("06 original Explore separation conflict is preserved",
                MissionAdmissionPolicy.Conflicts(a, b));
        }

        private static void Scenario07_StrandedCapacityRelaxesSurplusThreshold()
        {
            SurplusAdmission a = SurplusAdmissionPolicy.EvaluateFromSlack(8f, 1f);
            Check("07 high stranded AP/resources lowers threshold enough for observed util=0.45",
                a.EffectiveThreshold <= 0.45f && a.EffectiveThreshold >= SurplusAdmissionPolicy.ThresholdFloor);
        }

        private static void Scenario08_NearReserveKeepsConservativeSurplusThreshold()
        {
            SurplusAdmission a = SurplusAdmissionPolicy.EvaluateFromSlack(0f, 0f);
            Check("08 no slack keeps configured base surplus threshold",
                Math.Abs(a.EffectiveThreshold - AiConfigV2.surplusUtilityThreshold) < 0.0001f);
        }

        private static void Scenario09_InitiativeLabelsHighLeftoverAsNonApBottleneck()
        {
            InitiativeAnalyticsHistory.Clear();
            PlayerSetupData me = Player("S9");
            InitiativeAnalyticsHistory.Record(me,
                new InitiativeTurnRecord(5, 10, 2, 8, 1, 0, false));
            string label = InitiativeBottleneckDiagnostics.Describe(me, new PreTurnCapacityAnalysis());
            Check("09 high leftover AP is diagnosed as stranded/non-AP capacity",
                label.StartsWith("ap-stranded/non-ap-or-demand-limited", StringComparison.Ordinal));
        }

        private static void Scenario10_InitiativeLabelsRealStarvationAsApLimited()
        {
            InitiativeAnalyticsHistory.Clear();
            PlayerSetupData me = Player("S10");
            InitiativeAnalyticsHistory.Record(me,
                new InitiativeTurnRecord(5, 5, 5, 0, 2, 1, false));
            string label = InitiativeBottleneckDiagnostics.Describe(me, new PreTurnCapacityAnalysis());
            Check("10 unactivated army at AP floor is diagnosed as AP-limited",
                label.StartsWith("ap-limited", StringComparison.Ordinal));
        }

        private static WorldSnapshot Snap(float dark, IReadOnlyList<ArmySnapshot> armies) =>
            new WorldSnapshot
            {
                TurnNumber = 5,
                Self = new SelfSnapshot
                {
                    Armies = armies,
                    BaseHexes = new[] { H(9, 4) },
                    ActionPoints = 10,
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    ExplorableUnknownFrac = dark,
                    VisitedHexSet = new HashSet<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                },
                Threat = new ThreatModel(),
                Known = new KnownSnapshot(),
            };

        private static ReconObjective Explore(HexCoord h, float value) => new ReconObjective
        {
            Kind = ReconObjectiveKind.Explore,
            FocusHex = h,
            BaseValue = value,
            Stealth = StealthRequirement.None,
            DetectionRisk = 0f,
            FreshNeighbors = 4,
            DistanceFromBase = 2,
        };

        private static ArmySnapshot Scout(PlayerSetupData owner, int id, HexCoord hex) => new ArmySnapshot
        {
            ArmyId = id,
            Owner = owner,
            Hex = hex,
            IsGarrison = false,
            IsPrison = false,
            IsAir = false,
            MemberCount = 1,
            IsSoloRecce = true,
            CurrentMovement = 2,
            MaxMovement = 2,
            ActivationApCost = 1,
            HasActivatedThisTurn = false,
            IsHidden = false,
            CanEnterStealth = false,
            EffectiveVisionRadius = 2,
            Members = Array.Empty<WorthIt.DefenderProfile>(),
        };

        private static MissionProposal ScoutProposal(WorldSnapshot snap, HexCoord h)
        {
            var target = new ScoutMissionTarget
            {
                Kind = ScoutTargetKind.Explore,
                FocusHex = h,
                Stealth = StealthRequirement.None,
            };
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, target);
            var p = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = target,
                BaseValue = 60f,
                LocalAdmissionScore = 60f,
                Requirements = new MissionRequirements
                {
                    MoverKnown = cost.MoverKnown,
                    ApMinimum = cost.ApMinimum,
                    ApDesired = cost.ApDesired,
                    ApMaximum = cost.ApMaximum,
                },
            };
            p.Axes.Value[DesireAxis.Recon] = 1f;
            return p;
        }

        private static PlayerSetupData Player(string name) => new PlayerSetupData
        {
            Nickname = name,
            IsHuman = false,
            IsNeutral = false,
        };

        private static HexCoord H(int q, int r) => new HexCoord(q, r);

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("FAIL " + name);
            }
        }
    }
}
