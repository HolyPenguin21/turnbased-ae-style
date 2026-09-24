#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using Game.Combat;
using Game.Units;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class AiProductionScoreAlignmentTests
    {
        [Test]
        public void RemoteOperatorDemandBecomesOneConcreteDevelopmentMission()
        {
            var hero = new UnitData { IsHero = true };
            var site = new HexCoord(4, -2);
            var opportunity = new DevelopmentOpportunity
            {
                FacilityHex = site, Mode = ResearchProductionMode.Production,
                PreparationExistingHero = hero, PreparationSourceArmyId = 9,
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.DevelopmentOperator,
                DevOpportunity = opportunity, Value = 19f,
            };
            var proposals = DevelopmentMissionPlanner.Propose(null,
                Array.Empty<MissionIntent>(), new[] { demand, demand });
            Assert.That(proposals, Has.Count.EqualTo(1),
                "One operator demand must not duplicate the same hero/facility mission");
            MissionProposal mission = proposals.Single();
            Assert.That(mission.Kind, Is.EqualTo(MissionKind.Development));
            Assert.That(mission.BaseValue, Is.EqualTo(19f),
                "The radar-independent investment value must be passed through unchanged");
            Assert.That(mission.PreferredMoverArmyId, Is.EqualTo(9));
            var target = (DevelopmentMissionTarget)mission.Target;
            Assert.That(target.Hero, Is.SameAs(hero));
            Assert.That(target.HeroKey, Is.EqualTo(GenerationSource.StableHeroKey(hero)));
            Assert.That(MissionIntentKey.For(mission).Kind, Is.EqualTo(MissionKind.Development));
            Assert.That(StableMissionKey.For(mission).Kind, Is.EqualTo(MissionKind.Development));
            Assert.That(MissionAdmissionPolicy.LaneFor(mission), Is.EqualTo(ExecutionLane.Development));
        }

        [Test]
        public void DevelopmentContinuationKeepsOriginalHeroAndPreventsDuplicateActorAssignment()
        {
            var hero = new UnitData { IsHero = true };
            var site = new HexCoord(4, -2);
            var existing = new MissionIntent
            {
                Kind = MissionKind.Development, Funding = CommitmentTier.Soft,
                Status = IntentStatus.Active, PreferredMoverArmyId = 9,
                Objective = new DevelopmentIntent
                {
                    FacilityHex = site, Mode = ResearchProductionMode.Research,
                    Hero = hero, HeroKey = GenerationSource.StableHeroKey(hero),
                    IntrinsicValue = 8f,
                },
            };
            existing.IntentKey = MissionIntentKey.For(existing);
            var refreshed = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.DevelopmentOperator,
                DevOpportunity = new DevelopmentOpportunity
                {
                    FacilityHex = site, Mode = ResearchProductionMode.Research,
                    PreparationExistingHero = hero, PreparationSourceArmyId = 10,
                },
                Value = 70f,
            };
            var proposals = DevelopmentMissionPlanner.Propose(null,
                new[] { existing }, new[] { refreshed });
            Assert.That(proposals, Has.Count.EqualTo(1),
                "A fresh candidate must not replace an existing site's durable operator");
            MissionProposal ongoing = proposals.Single();
            Assert.That(ongoing.FromDurableIntent, Is.True);
            Assert.That(ongoing.PreferredMoverArmyId, Is.EqualTo(9));
            Assert.That(ongoing.BaseValue, Is.EqualTo(8f));
            Assert.That(MissionIntentKey.For(ongoing), Is.EqualTo(existing.IntentKey));
            Assert.That(MissionAdmissionPolicy.Conflicts(ongoing, new MissionProposal
            {
                Kind = MissionKind.Development,
                Target = new DevelopmentMissionTarget
                {
                    FacilityHex = new HexCoord(6, -2),
                    Mode = ResearchProductionMode.Production, Hero = hero,
                    SourceArmyId = 10,
                },
            }), Is.True, "One physical Hero cannot serve two different facilities");
        }

        [Test]
        public void DevelopmentExecutionLedgerKeepsTheExactOperatorPayload()
        {
            var hero = new UnitData { IsHero = true };
            var target = new DevelopmentMissionTarget
            {
                FacilityHex = new HexCoord(5, -2),
                Mode = ResearchProductionMode.Production,
                Hero = hero, HeroKey = GenerationSource.StableHeroKey(hero),
                SourceArmyId = 7,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Development, Target = target,
                BaseValue = 14f,
            };
            var pm = new ProvisionedMission
            {
                Mission = proposal, Kind = MissionKind.Development,
                DevelopmentTarget = target, MoverArmyId = 7,
                Key = StableMissionKey.For(proposal),
            };
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { proposal });
            ledger.RecordProvisionSuccess(proposal, pm);
            ledger.RecordExecution(new ExecutionResult
            {
                Key = pm.Key, Source = pm, StepsMoved = 1,
                ActualActorArmyId = 7, StopReason = ExecutionStopReason.StepCompleted,
            });
            MissionTurnOutcome outcome = ledger.Finalize().Single();
            Assert.That(outcome.MissionKind, Is.EqualTo(MissionKind.Development));
            Assert.That(outcome.HasDevelopmentPayload, Is.True);
            Assert.That(outcome.DevelopmentTarget.Hero, Is.SameAs(hero));
            Assert.That(outcome.MoverArmyId, Is.EqualTo(7));
            Assert.That(outcome.MadeProgress, Is.True);
            Assert.That(outcome.Outcome, Is.EqualTo(ExecutionOutcome.ProductiveStop));
        }

        [Test]
        public void MaterializationValue_ConvertsPowerToCardUnitsAndChargesCanonicalPlanCosts()
        {
            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            var opportunity = new DevelopmentOpportunity
            {
                SuccessChance = 0.75f,
                ExpectedGain = powerUnit * 2f,
            };
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachUpgrade,
                ApCost = 3f,
                ResCost = new ResourceCost { energy = 4 },
            };
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Energy = 20f },
                    PerTurnIncome = new ResourceBundle { Energy = 1f },
                    Hand = new[]
                    {
                        new CardData(new CardDefinition
                        {
                            resourceCost = new ResourceCost { energy = 10 },
                        }),
                    },
                    Deck = Array.Empty<CardDefinition>(),
                },
            };
            var equipment = new CardDefinition { cardType = CardType.Equipment };
            opportunity.Card = equipment;
            plan.Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true };
            plan.GeneratedEquipmentDef = equipment;
            float resourceCost = StrategicCardEvaluator.StrategicResourceCostValue(plan.ResCost, snapshot);
            float expected = 0.75f * 2f
                - resourceCost - 3f * AiConfigV2.stratCardApCostWeight
                - AiConfigV2.stratChainGenerationStepPenalty
                - AiConfigV2.stratChainAttachStepPenalty;
            float actual = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, snapshot, null, null, null);
            Assert.That(actual, Is.EqualTo(expected).Within(0.0001f));
            Assert.That(actual, Is.LessThan(expected + 0.0001f),
                "Development's lifetime persistence multiplier must not be applied a second time in card competition");
        }

        [Test]
        public void MaterializationValue_RewardsOnlyMarginalStrength()
        {
            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            var opportunity = new DevelopmentOpportunity
            {
                SuccessChance = 0.8f, ExpectedGain = powerUnit,
            };
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachUpgrade,
                ApCost = 2f,
            };
            var equipment = new CardDefinition { cardType = CardType.Equipment };
            opportunity.Card = equipment;
            plan.Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true };
            plan.GeneratedEquipmentDef = equipment;
            float useful = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            opportunity.ExpectedGain = 0f;
            float zeroGain = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            Assert.That(zeroGain, Is.LessThan(0f),
                "Production cannot turn a zero-benefit equipment attachment into a useful action");
            Assert.That(useful - zeroGain, Is.EqualTo(0.8f).Within(0.0001f),
                "Only the expected marginal gain separates a useful attachment from a useless one");
        }

        [Test]
        public void ConcreteChainResourceCostIgnoresAResourceTheChainDoesNotConsume()
        {
            var chainCost = new ResourceCost { energy = 4, materials = 3, tech = 0 };
            WorldSnapshot Make(float tech) => new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle
                    {
                        Human = 12f, Energy = 12f, Materials = 8f, Tech = tech,
                    },
                    PerTurnIncome = new ResourceBundle
                    {
                        Human = 1f, Energy = 1f, Materials = 1f, Tech = 0f,
                    },
                    Hand = new[]
                    {
                        new CardData(new CardDefinition
                        {
                            resourceCost = new ResourceCost { energy = 6, materials = 5, tech = 20 },
                        }),
                    },
                    Deck = Array.Empty<CardDefinition>(),
                },
            };

            float noTech = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(0f));
            float abundantTech = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(100f));

            Assert.That(noTech, Is.EqualTo(abundantTech).Within(0.0001f),
                "A resource with zero cost in the concrete production chain must not create a false operational penalty");
        }

        [Test]
        public void ConcreteChainResourceCostRespondsToScarcityOfAResourceItActuallyConsumes()
        {
            var chainCost = new ResourceCost { energy = 4 };
            WorldSnapshot Make(float energy) => new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Energy = energy, Tech = 100f },
                    PerTurnIncome = new ResourceBundle { Energy = 0f, Tech = 10f },
                    Hand = new[]
                    {
                        new CardData(new CardDefinition
                        {
                            resourceCost = new ResourceCost { energy = 20 },
                        }),
                    },
                    Deck = Array.Empty<CardDefinition>(),
                },
            };

            float scarceEnergy = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(1f));
            float abundantEnergy = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(100f));

            Assert.That(scarceEnergy, Is.GreaterThan(abundantEnergy),
                "Scarcity of a resource actually consumed by the chain must raise its canonical opportunity cost");
        }

        [Test]
        public void GeneratedUnitCannotBeMistakenForAnExistingCardUpgrade()
        {
            var unit = new CardDefinition { cardType = CardType.Unit };
            var generation = new GenerationStep
            {
                CardDef = unit, ProducesEquipment = false, CardKey = "unit",
            };
            var opportunity = new DevelopmentOpportunity
            {
                Card = unit, Generation = generation,
                RecipientCard = new CardData(new CardDefinition { cardType = CardType.Unit }),
                SuccessChance = 1f, ExpectedGain = 99f,
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.CardUpgrade,
                DevOpportunity = opportunity,
            };
            Assert.That(MaterializationPlanFactory.MakeDevelopmentUpgradePlan(demand), Is.Null,
                "A new combat body must use GenerateDeploy, not attach-to-existing-host valuation");
            var invalid = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachUpgrade,
                GeneratedEquipmentDef = unit, Generation = generation,
            };
            Assert.That(StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, invalid, null, null, null, null), Is.EqualTo(float.NegativeInfinity));

            var equipment = new CardDefinition { cardType = CardType.Equipment, activationApCost = 1 };
            opportunity.Card = equipment;
            generation.CardDef = equipment;
            generation.ProducesEquipment = true;
            MaterializationPlan valid = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(demand);
            Assert.That(valid, Is.Not.Null);
            Assert.That(valid.GeneratedEquipmentDef, Is.SameAs(equipment));
            Assert.That(valid.GeneratedBaseDef, Is.Null);
        }

        [Test]
        public void EquipmentMatchupRequiresActualWorthItImprovement()
        {
            var primary = new UnitData
            {
                Attack = 1, Defense = 2, Initiative = 2,
                HitPointsCurrent = 8, HitPointsMax = 8,
            };
            var guards = new[]
            {
                new WorthIt.DefenderProfile(defense: 4, hasCeramicArmor: false,
                    attack: 6, hitPoints: 8, initiative: 2),
            };
            var moveOnly = new EquipmentGrant();
            moveOnly.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.MoveMax, amount = 3,
            });
            Assert.That(DevelopmentOpportunityEvaluator.ImprovesGroundCombatOutcome(
                primary, new[] { primary }, moveOnly, guards), Is.False,
                "Mobility alone cannot claim a WorthIt combat improvement against known guards");
            var weapon = new EquipmentGrant();
            weapon.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Attack, amount = 20,
            });
            Assert.That(DevelopmentOpportunityEvaluator.ImprovesGroundCombatOutcome(
                primary, new[] { primary }, weapon, guards), Is.True,
                "A proven improvement in the recipient army's combat outcome counts");
            Assert.That(DevelopmentOpportunityEvaluator.ImprovesGroundCombatOutcome(
                primary, new[] { primary }, weapon, Array.Empty<WorthIt.DefenderProfile>()), Is.False,
                "An unobserved enemy cannot justify speculative equipment");
            Assert.That(primary.Attack, Is.EqualTo(1),
                "Projection must never mutate the living army before generation/attachment");
            Assert.That(primary.Equipment, Is.Null);
        }

    }
}
#endif
