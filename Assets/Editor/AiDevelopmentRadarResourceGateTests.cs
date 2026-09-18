#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Cards;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiDevelopmentRadarResourceGateTests
    {
        private static WorldSnapshot Snapshot(DevelopmentReadiness development) => new WorldSnapshot
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
                Threats = Array.Empty<AssetThreatSnapshot>(),
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
    }
}
#endif
