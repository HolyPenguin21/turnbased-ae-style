#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Cards;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiDevelopmentRadarResourceGateTests
    {
        // A live Attack/Defence need for Production to amplify: one enemy (power 10) threatens
        // our Base while our whole force is 1 (ForceNeedModel defensive shortfall).
        internal static AssetThreatSnapshot UncoveredBaseThreat() => new AssetThreatSnapshot
        {
            Contact = new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    ArmyId = 77, EffectiveArmyPower = 10f,
                    Members = Array.Empty<Game.Combat.WorthIt.DefenderProfile>(),
                },
            },
            Asset = new StrategicAssetSnapshot { Kind = AssetKind.Base, Value = 10f },
            Severity = 1f,
        };

        private static WorldSnapshot Snapshot(DevelopmentReadiness development,
            bool witnessedNeed = true) => new WorldSnapshot
        {
            TurnNumber = 1,
            Self = new SelfSnapshot
            {
                BaseHexes = Array.Empty<Game.HexGrid.HexCoord>(),
                Armies = Array.Empty<ArmySnapshot>(),
                Hand = Array.Empty<CardData>(),
                Deck = Array.Empty<CardDefinition>(),
                TotalPower = 1f,
                TotalMilitaryPotential = 1f,
            },
            Known = new KnownSnapshot
            {
                EnemySightings = Array.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                NeutralSightings = Array.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                EventGuards = Array.Empty<KnownEventGuardSnapshot>(),
            },
            TrueWorld = new TrueWorldSnapshot
            {
                EnemyArmies = Array.Empty<ArmySnapshot>(),
                NeutralArmies = Array.Empty<ArmySnapshot>(),
                Opponents = Array.Empty<OpponentSnapshot>(),
            },
            Economy = new EconomyStanding
            {
                PerType = Array.Empty<EconomyResourceStanding>(),
            },
            Threat = new ThreatModel
            {
                Contacts = Array.Empty<EnemyContactSnapshot>(),
                Assets = Array.Empty<StrategicAssetSnapshot>(),
                Threats = witnessedNeed
                    ? new[] { UncoveredBaseThreat() }
                    : Array.Empty<AssetThreatSnapshot>(),
            },
            Development = development,
        };

        [Test]
        public void ReadyOfferingBypassesUnrelatedFourResourceInvestmentGate()
        {
            var card = new CardDefinition { cardType = CardType.Equipment };
            var ready = new DevelopmentReadiness
            {
                Offerings = new[]
                {
                    new DevelopmentOffering { Card = card, SuccessChance = 1f },
                },
                BestSuccessChance = 1f,
                UpgradeTargetCount = 10,
                AnyFacilityWithHero = true,
                DevPathViable = true,
                SurplusFraction = WorldAnalysis.DevelopmentRadarSurplus(
                    investmentSurplus: 0f, hasExecutableOffering: true),
            };

            RadarAssessment assessed = StrategyLayer.Evaluate(Snapshot(ready), new AiRadarState());

            Assert.That(ready.SurplusFraction, Is.EqualTo(1f),
                "A READY offering already passed GenerationSource/StrategicSpendability for the resources it actually consumes");
            Assert.That(assessed.Desires.Raw[DesireAxis.Development], Is.GreaterThan(0f),
                "An unrelated empty resource must not zero Development before the legal ready chain reaches card competition");
        }

        [Test]
        public void LatentInvestmentStillUsesBroadFourResourceSurplus()
        {
            var latent = new DevelopmentReadiness
            {
                Offerings = Array.Empty<DevelopmentOffering>(),
                UpgradeTargetCount = 10,
                DevPathViable = true,
                SurplusFraction = WorldAnalysis.DevelopmentRadarSurplus(
                    investmentSurplus: 0f, hasExecutableOffering: false),
            };

            RadarAssessment assessed = StrategyLayer.Evaluate(Snapshot(latent), new AiRadarState());

            Assert.That(latent.SurplusFraction, Is.Zero,
                "Infrastructure investment keeps the coarse all-resource risk signal when no ready production chain exists");
            Assert.That(assessed.Desires.Raw[DesireAxis.Development], Is.Zero,
                "Zero broad investment headroom must still suppress purely latent Development");
        }

        private static DevelopmentReadiness ReadyOffering() => new DevelopmentReadiness
        {
            Offerings = new[]
            {
                new DevelopmentOffering
                {
                    Card = new CardDefinition { cardType = CardType.Equipment }, SuccessChance = 1f,
                },
            },
            BestSuccessChance = 1f,
            UpgradeTargetCount = 10,
            AnyFacilityWithHero = true,
            DevPathViable = true,
            SurplusFraction = 1f,
        };

        [Test]
        public void ProductionNeverCreatesANeed_NoMilitaryWitnessMeansNoDevelopment()
        {
            WorldSnapshot snapshot = Snapshot(ReadyOffering(), witnessedNeed: false);
            RadarAssessment assessed = StrategyLayer.Evaluate(snapshot, new AiRadarState());

            Assert.That(ForceNeedModel.JustifiedForceNeed(snapshot).Total, Is.Zero);
            Assert.That(assessed.Desires.Raw[DesireAxis.Development], Is.Zero,
                "full budget, a staffed facility and ten upgrade targets are not a need");
        }

        [Test]
        public void CoveredThreatIsNotANeedForProduction()
        {
            WorldSnapshot snapshot = Snapshot(ReadyOffering());
            snapshot.Self.TotalPower = 100f;
            ForceNeed need = ForceNeedModel.JustifiedForceNeed(snapshot);
            Assert.That(need.Witnessed, Is.True, "the threat is still a military witness");
            Assert.That(need.Defensive, Is.Zero, "our force already covers the reserve it demands");
            Assert.That(need.Total, Is.Zero);
        }

        private static DevelopmentReadiness IdleStock(float h, float e, float m, float t)
        {
            DevelopmentReadiness rd = ReadyOffering();
            rd.InvestmentSurplusByType.Add(Game.Economy.ResourceType.Human, h);
            rd.InvestmentSurplusByType.Add(Game.Economy.ResourceType.Energy, e);
            rd.InvestmentSurplusByType.Add(Game.Economy.ResourceType.Materials, m);
            rd.InvestmentSurplusByType.Add(Game.Economy.ResourceType.Tech, t);
            return rd;
        }

        [Test]
        public void IdleStockRaisesForceNeedEvenWhenTheThreatIsCovered()
        {
            WorldSnapshot spent = Snapshot(IdleStock(0f, 0f, 0f, 0f));
            WorldSnapshot banked = Snapshot(IdleStock(1f, 0f, 1f, 1f));
            spent.Self.TotalPower = banked.Self.TotalPower = 100f;

            Assert.That(ForceNeedModel.JustifiedForceNeed(spent).Total, Is.Zero);
            ForceNeed need = ForceNeedModel.JustifiedForceNeed(banked);
            Assert.That(need.Surplus, Is.GreaterThan(0f),
                "three resources piling up are a need even with one exhausted resource");
            Assert.That(need.Total, Is.EqualTo(need.Surplus).Within(0.0001f));
            Assert.That(need.Total, Is.LessThanOrEqualTo(AiConfigV2.forceNeedSurplusWeight + 0.0001f),
                "an idle bank alone never outranks a fight we cannot take");
        }

        [Test]
        public void IdleStockWithoutMilitaryWitnessIsStillNoNeed()
        {
            WorldSnapshot snapshot = Snapshot(IdleStock(1f, 1f, 1f, 1f), witnessedNeed: false);
            Assert.That(ForceNeedModel.JustifiedForceNeed(snapshot).Total, Is.Zero);
        }

        [Test]
        public void CombatOpportunityViability_IsOneRule()
        {
            var coverable = new CombatOpportunity(true, default, RaidTargetRef.None, null, true, 1,
                AiConfigV2.raidMinViableWinChance, 0f, true, 0f, 1, 0f, 1f, false, 0f);
            var uncovered = new CombatOpportunity(true, default, RaidTargetRef.None, null, true, 1,
                1f, 1f, false, 0f, 1, 0f, 1f, false, 0f);
            var gated = new CombatOpportunity(true, default, RaidTargetRef.None, null, true, 1,
                0f, 0f, false, 0f, 1, 0f, 1f, true, 0f);
            Assert.That(coverable.IsViable, Is.True);
            Assert.That(uncovered.IsViable, Is.False, "a fight we cannot cover is not winnable");
            Assert.That(gated.IsViable, Is.True);
        }

        [Test]
        public void DevelopmentDesireNeverExceedsTheNeedItAmplifies()
        {
            WorldSnapshot snapshot = Snapshot(ReadyOffering());
            RadarAssessment assessed = StrategyLayer.Evaluate(snapshot, new AiRadarState());
            float need = ForceNeedModel.JustifiedForceNeed(snapshot).Total;

            Assert.That(need, Is.GreaterThan(0f), "an uncovered base threat is a justified need");
            Assert.That(assessed.Breakdown.DevJustifiedNeed, Is.EqualTo(need).Within(0.0001f));
            Assert.That(assessed.Desires.Raw[DesireAxis.Development],
                Is.LessThanOrEqualTo(need + 0.0001f));
        }
    }
}
#endif
