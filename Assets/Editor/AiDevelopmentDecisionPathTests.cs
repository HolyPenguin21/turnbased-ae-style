#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Integrates the existing pure decision stages. This deliberately does not claim to
    // exercise live GenerationSource affordability, Challenge, or Unity movement.
    public sealed class AiDevelopmentDecisionPathTests
    {
        private static WorldSnapshot Snapshot(float tech, float energy,
            CardDefinition equipment, CardData recipient)
        {
            return new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    BaseHexes = Array.Empty<HexCoord>(),
                    Armies = Array.Empty<ArmySnapshot>(),
                    Hand = new[] { recipient },
                    Deck = Array.Empty<CardDefinition>(),
                    TotalPower = 1f,
                    TotalMilitaryPotential = 1f,
                    Stockpile = new ResourceBundle
                    {
                        Human = 12f, Energy = energy, Materials = 8f, Tech = tech,
                    },
                    PerTurnIncome = new ResourceBundle
                    {
                        Human = 1f, Energy = 1f, Materials = 1f,
                    },
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
                Development = new DevelopmentReadiness
                {
                    Offerings = new[]
                    {
                        new DevelopmentOffering
                        {
                            Card = equipment, ProducesEquipment = true, SuccessChance = 1f,
                        },
                    },
                    BestSuccessChance = 1f,
                    UpgradeTargetCount = 1,
                    AnyFacilityWithHero = true,
                    DevPathViable = true,
                    SurplusFraction = WorldAnalysis.DevelopmentRadarSurplus(
                        investmentSurplus: 0f, hasExecutableOffering: true),
                    ProductionSupport = 1f,
                },
            };
        }

        private static (float desire, AxisDemand demand, MaterializationPlan plan, float score)
            Evaluate(float tech, float energy, CardDefinition equipment, CardData recipient)
        {
            WorldSnapshot snapshot = Snapshot(tech, energy, equipment, recipient);
            float desire = StrategyLayer.Evaluate(snapshot, new AiRadarState())
                .Desires.Raw[DesireAxis.Development];
            var generation = new GenerationStep
            {
                CardDef = equipment,
                CardKey = "production:equipment",
                ProducesEquipment = true,
                SuccessChance = 1f,
            };
            var opportunity = new DevelopmentOpportunity
            {
                Card = equipment,
                Generation = generation,
                ProducesEquipment = true,
                RecipientKind = DevRecipientKind.HandCard,
                RecipientCard = recipient,
                RecipientLabel = "hand:host",
                SuccessChance = 1f,
                ExpectedGain = 10f,
                BaseValue = 8f,
                ProductionSupport = 1f,
            };
            MethodInfo method = typeof(DemandLayer).GetMethod("DevelopmentDemands",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var demands = (IEnumerable<AxisDemand>)method.Invoke(null, new object[]
            {
                snapshot, null, new[] { opportunity }, Array.Empty<AxisDemand>(),
                Array.Empty<MissionIntent>(), null, null, null,
            });
            AxisDemand demand = demands.Single(d => d.Capability == CapabilityKind.CardUpgrade);
            MaterializationPlan plan = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(demand);
            Assert.That(plan, Is.Not.Null);
            float score = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, snapshot, null, null, null);
            return (desire, demand, plan, score);
        }

        [Test]
        public void ZeroTechCannotChangeReadyEnergyMaterialsDecisionPath()
        {
            var equipment = new CardDefinition
            {
                cardType = CardType.Equipment,
                resourceCost = new ResourceCost { energy = 4, materials = 3, tech = 0 },
            };
            var recipient = new CardData(new CardDefinition { cardType = CardType.Unit });
            var noTech = Evaluate(0f, 12f, equipment, recipient);
            var abundantTech = Evaluate(100f, 12f, equipment, recipient);

            Assert.That(noTech.desire, Is.GreaterThan(0f));
            Assert.That(noTech.desire, Is.EqualTo(abundantTech.desire).Within(0.0001f));
            Assert.That(noTech.demand.RequestingAxis, Is.EqualTo(DesireAxis.Development));
            Assert.That(noTech.demand.Value, Is.EqualTo(abundantTech.demand.Value).Within(0.0001f));
            Assert.That(noTech.plan.ResCost.energy, Is.EqualTo(4));
            Assert.That(noTech.plan.ResCost.materials, Is.EqualTo(3));
            Assert.That(noTech.plan.ResCost.tech, Is.Zero);
            Assert.That(noTech.score, Is.EqualTo(abundantTech.score).Within(0.0001f),
                "An unused resource must not affect ready Production's radar, demand, plan or canonical card score");
        }

        [Test]
        public void ConsumedEnergyScarcityChangesFinalScoreWithoutChangingChainCost()
        {
            var equipment = new CardDefinition
            {
                cardType = CardType.Equipment,
                resourceCost = new ResourceCost { energy = 4, materials = 3 },
            };
            var recipient = new CardData(new CardDefinition { cardType = CardType.Unit });
            var scarce = Evaluate(0f, 1f, equipment, recipient);
            var abundant = Evaluate(0f, 100f, equipment, recipient);

            Assert.That(scarce.plan.ResCost.energy, Is.EqualTo(abundant.plan.ResCost.energy));
            Assert.That(scarce.plan.ResCost.materials, Is.EqualTo(abundant.plan.ResCost.materials));
            Assert.That(scarce.score, Is.LessThan(abundant.score),
                "The canonical scorer must price scarcity of Energy actually consumed by the chain");
            // This constructed snapshot tests valuation, not whether the live game may pay the
            // scarce chain. GenerationSource and StrategicSpendability own that separate gate.
        }
    }
}
#endif
