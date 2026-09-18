#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class AiProductionCalibrationTests
    {
        [TestCase(DesireAxis.Aggression, 5f, 0f)]
        [TestCase(DesireAxis.Aggression, 8.5f, 0.5f)]
        [TestCase(DesireAxis.Aggression, 12f, 1f)]
        [TestCase(DesireAxis.Recon, 12f, 1f)]
        [TestCase(DesireAxis.Economy, 12f, 1f)]
        [TestCase(DesireAxis.Development, 25f, 0f)]
        [TestCase(DesireAxis.Development, 42.5f, 0.5f)]
        [TestCase(DesireAxis.Development, 60f, 1f)]
        public void Urgency_RetainsWorldAndLegacyDevelopmentBands(DesireAxis axis, float value, float expected)
        {
            var demand = new AxisDemand { RequestingAxis = axis, Value = value };
            Assert.That(DemandUrgencyPolicy.Normalized(demand), Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void ProductionEmergencyFloor_UsesMigratedRaidUrgencyButKeepsActualEconomySupport()
        {
            WorldSnapshot snapshot = Snapshot();
            snapshot.Development = new DevelopmentReadiness
            {
                ProductionSupport = AiConfigV2.productionSupportMin,
            };
            var unit = new CardDefinition
            {
                cardType = CardType.Unit, attack = 3, defenseRating = 2,
                hitPoints = 4, moveMax = 3,
            };
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateDeploy,
                GeneratedBaseDef = unit,
                Generation = new GenerationStep { CardDef = unit, SuccessChance = 1f },
                FinalCapability = CapabilityKind.FieldCombatPower,
                ProjectedAbilities = Array.Empty<string>(),
                Deploy = new PlacementOption(new HexCoord(0, 0), DeploymentKind.NewArmy, null),
                ApCost = 3f,
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                Value = AiConfigV2.taskScoreUrgencyRampHi,
            };
            StrategicUseScoreBreakdown b = StrategicCardEvaluator.ScoreForDemand(
                plan, demand, TraitPreference.None, new CapabilityInventory(), 0, false, snapshot)
                .Breakdown;
            float amplifiable = Mathf.Max(0f, b.RoleFit) + Mathf.Max(0f, b.ImmediateTempo)
                + Mathf.Max(0f, b.NextTurnPotential) + Mathf.Max(0f, b.CapabilityGapValue)
                + Mathf.Max(0f, b.ForceGrowthValue) + Mathf.Max(0f, b.ThreatResponseValue)
                + Mathf.Max(0f, b.SynergyValue) + Mathf.Max(0f, b.ScarcityValue);
            Assert.That(amplifiable, Is.GreaterThan(0f), "fixture must produce a genuinely useful body");
            Assert.That(b.ProductionSupportAdjustment,
                Is.EqualTo(amplifiable * (AiConfigV2.productionSupportEmergencyFloor - 1f))
                    .Within(0.0001f));

            snapshot.Development.ProductionSupport = AiConfigV2.productionSupportMax;
            StrategicUseScoreBreakdown secure = StrategicCardEvaluator.ScoreForDemand(
                plan, demand, TraitPreference.None, new CapabilityInventory(), 0, false, snapshot)
                .Breakdown;
            Assert.That(secure.ProductionSupportAdjustment,
                Is.EqualTo(amplifiable * (AiConfigV2.productionSupportMax - 1f))
                    .Within(0.0001f), "urgent floor must not override stronger actual economy support");
        }

        [Test]
        public void VerifiedRaidTechDeficit_PreservesMarginalTechOnWorldScoreScale()
        {
            ResourceStarvationRegistry.Clear();
            try
            {
                var player = new PlayerSetupData();
                WorldSnapshot snapshot = Snapshot();
                snapshot.TurnNumber = 7;
                snapshot.Self.Stockpile = new ResourceBundle { Tech = 4f };
                snapshot.Self.PerTurnIncome = new ResourceBundle { Tech = 1f };
                var cost = new ResourceCost { tech = 2 };
                float baseline = StrategicCardEvaluator.StrategicResourceCostValue(
                    cost, snapshot, t => t == ResourceType.Tech ? 4f : 0f, player);
                ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Tech,
                    required: 6f, available: 4f, incomePerTurn: 1f,
                    demandValue: AiConfigV2.taskScoreUrgencyRampHi, turn: snapshot.TurnNumber);
                float protectedCost = StrategicCardEvaluator.StrategicResourceCostValue(
                    cost, snapshot, t => t == ResourceType.Tech ? 4f : 0f, player);
                Assert.That(protectedCost - baseline,
                    Is.EqualTo(AiConfigV2.stratResidualResourcePreservationMax * (2f / 6f))
                        .Within(0.0001f));
                Assert.That(StrategicCardEvaluator.StrategicResourceCostValue(
                    new ResourceCost { energy = 2 }, snapshot, t => 4f, player),
                    Is.EqualTo(StrategicCardEvaluator.StrategicResourceCostValue(
                        new ResourceCost { energy = 2 }, snapshot, t => 4f))
                        .Within(0.0001f), "only the specifically proven Tech shortage is protected");
            }
            finally
            {
                ResourceStarvationRegistry.Clear();
            }
        }

        [Test]
        public void PhaseAResourceValuation_UsesSpendablePoolRatherThanGrossStockpile()
        {
            WorldSnapshot snapshot = Snapshot();
            snapshot.Self.Stockpile = new ResourceBundle { Energy = 20f };
            snapshot.Self.Hand = new[]
            {
                new CardData(new CardDefinition
                {
                    cardType = CardType.Unit,
                    resourceCost = new ResourceCost { energy = 30 },
                }),
            };
            var plan = new MaterializationPlan { ApCost = 1f,
                ResCost = new ResourceCost { energy = 4 } };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                Value = 8.5f,
            };
            var inventory = new CapabilityInventory();
            // Same card and opportunity: only 6 of the 20 Energy are actually spendable.
            float gross = StrategicCardEvaluator.ScoreForDemand(plan, demand,
                TraitPreference.None, inventory, 0, false, snapshot).Breakdown.ResourceEfficiency;
            float spendable = StrategicCardEvaluator.ScoreForDemand(plan, demand,
                TraitPreference.None, inventory, 0, false, snapshot,
                spendableResource: type => type == ResourceType.Energy ? 6f : 0f)
                .Breakdown.ResourceEfficiency;
            Assert.That(spendable, Is.LessThan(gross),
                "a physically reserved resource must have the same higher opportunity cost in Phase A as in B");
        }

        [Test]
        public void EquipmentUpgradeRole_ReceivesExactDeltaInRoleFitAndNotAgainInSynergy()
        {
            WorldSnapshot snapshot = Snapshot();
            var inventory = new CapabilityInventory();
            var unit = new CardDefinition
            {
                cardType = CardType.Unit, attack = 2, defenseRating = 2,
                hitPoints = 4, moveMax = 3,
            };
            var equipment = new CardDefinition
            {
                cardType = CardType.Equipment,
                equipment = new EquipmentGrant
                {
                    statChanges = new List<EquipmentStatChange>
                    {
                        new EquipmentStatChange { stat = EquipmentStat.Attack, amount = 3 },
                    },
                },
            };
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.AttachDeploy,
                FinalCapability = CapabilityKind.FieldCombatPower,
                BaseCardInHand = new CardData(unit),
                EquipmentInHand = new CardData(equipment),
                ProjectedAbilities = Array.Empty<string>(),
                Deploy = new PlacementOption(new HexCoord(0, 0), DeploymentKind.NewArmy, null),
            };
            float delta = StrategicCardEvaluator.EquipmentUpgradeUtility(plan, snapshot, inventory);
            Assert.That(delta, Is.GreaterThan(0f), "fixture must give a real marginal improvement");
            MethodInfo roleScorer = typeof(StrategicCardEvaluator).GetMethod(
                "ScoreSurplusRole", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(roleScorer, Is.Not.Null);
            var candidate = (StrategicCardUseCandidate)roleScorer.Invoke(null, new object[]
            {
                plan, IntendedRole.EquipmentUpgrade, inventory, false, false, null,
                Array.Empty<string>(), snapshot,
                BaselineForceReadiness.Evaluate(snapshot, inventory), 0f, null, 0, null, null,
            });
            Assert.That(candidate.Breakdown.RoleFit, Is.EqualTo(delta).Within(0.0001f));
            Assert.That(candidate.Breakdown.SynergyValue, Is.Zero.Within(0.0001f),
                "EquipmentUpgrade is already credited in RoleFit; ability effects remain independent");
        }

        private static WorldSnapshot Snapshot() => new WorldSnapshot
        {
            TurnNumber = 2,
            Self = new SelfSnapshot
            {
                Armies = Array.Empty<ArmySnapshot>(),
                Hand = Array.Empty<CardData>(),
                Deck = Array.Empty<CardDefinition>(),
                Stockpile = new ResourceBundle(),
                PerTurnIncome = new ResourceBundle(),
            },
        };
    }
}
#endif
