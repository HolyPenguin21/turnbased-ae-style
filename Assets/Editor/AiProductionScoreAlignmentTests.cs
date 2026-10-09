#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using Game.Combat;
using Game.Units;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
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
            MissionStepResult outcome = ledger.FinalizeSteps().Single();
            Assert.That(outcome.MissionKind, Is.EqualTo(MissionKind.Development));
            Assert.That(outcome.DevelopmentFacts().HasDevelopmentPayload, Is.True);
            Assert.That(outcome.DevelopmentFacts().DevelopmentTarget.Hero, Is.SameAs(hero));
            Assert.That(outcome.MoverArmyId, Is.EqualTo(7));
            Assert.That(outcome.MadeProgress, Is.True);
            Assert.That(outcome.Disposition, Is.EqualTo(MissionStepDisposition.Progress));
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
            var equipment = new CardDefinition { cardType = CardType.Equipment,
                apCost = 3, resourceCost = new ResourceCost { energy = 4 } };
            opportunity.Card = equipment;
            plan.Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true,
                SuccessChance = opportunity.SuccessChance };
            plan.GeneratedEquipmentDef = equipment;
            float resourceCost = StrategicCardEvaluator.StrategicResourceCostValue(plan.ResCost, snapshot);
            // No recipient => no WorthIt matchup witness: value = delta x persistence, applied ONCE.
            float expected = 0.75f * 2f * AiConfigV2.equipmentUpgradePersistence
                - resourceCost - ActionPrice.ToCardScore(3f)
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
            var equipment = new CardDefinition { cardType = CardType.Equipment, apCost = 2 };
            opportunity.Card = equipment;
            plan.Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true,
                SuccessChance = opportunity.SuccessChance };
            plan.GeneratedEquipmentDef = equipment;
            float useful = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            opportunity.ExpectedGain = 0f;
            float zeroGain = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            Assert.That(zeroGain, Is.LessThan(0f),
                "Production cannot turn a zero-benefit equipment attachment into a useful action");
            Assert.That(useful - zeroGain,
                Is.EqualTo(0.8f * AiConfigV2.equipmentUpgradePersistence).Within(0.0001f),
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
        public void LiveEquipmentCombatDeltaSeparatesMobilityFromPenetration()
        {
            var primary = new UnitData {
                Attack = 1, Defense = 2, Initiative = 2,
                HitPointsCurrent = 8, HitPointsMax = 8,
            };
            var snap = new WorldSnapshot {
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] {
                    new ArmySnapshot { Members = new[] {
                        new WorthIt.DefenderProfile(4, false, attack: 6, hitPoints: 8, initiative: 2),
                    } },
                } },
            };
            var mobility = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.MoveMax, 3);
            var weapon = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Attack, 20);
            var moveDelta = StrategicCardEvaluator.EquipmentDeltaParts(mobility, primary, snap);
            Assert.That(moveDelta.Combat, Is.Zero);
            Assert.That(moveDelta.Tactical, Is.GreaterThan(0f));
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(weapon, primary, snap).Combat, Is.GreaterThan(0f));
            snap.TrueWorld.EnemyArmies = Array.Empty<ArmySnapshot>();
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(weapon, primary, snap).Combat, Is.EqualTo(AiEquipmentTestMath.IntrinsicAttack(20, initiative: 2)).Within(0.0001f),
                "No defender or deck benchmark means no witnessed combat delta (the efficiency value stands, no fabricated threat)");
            Assert.That(primary.Attack, Is.EqualTo(1));
            Assert.That(primary.Equipment, Is.Null);
        }

        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void GeneratedAttachmentCostsOnlyChargeTheSuccessfulFollowupAtItsChance(AttachmentSlot slot)
        {
            var host = new CardData(AttachmentSlotTests.Host());
            host.Definition.apCost = 3;
            host.Definition.resourceCost = new ResourceCost { energy = 2 };
            var equipment = AttachmentSlotTests.Attachment(slot);
            equipment.apCost = 1;
            equipment.activationApCost = 1;
            equipment.resourceCost = new ResourceCost { materials = 3 };
            var generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true };
            var snap = PrepSnapshot(true, true);
            var inv = new CapabilityInventory();
            var demand = new AxisDemand { Capability = CapabilityKind.FieldCombatPower,
                RequestingAxis = DesireAxis.Aggression, DesiredAmount = 1 };
            var projected = EquipmentSystem.Project(host.Definition, null, null, equipment).Abilities;
            var plan = MaterializationPlanFactory.MakeGeneratedPlan(
                MaterializationChainKind.GenerateAttachDeploy, demand, generation, host, 0, true,
                new PlacementOption(PrepSite, DeploymentKind.Garrison, new ArmyData { IsGarrison = true }),
                projected);
            float fullAp = plan.ApCost;
            float fullCost = ActionPrice.ToCardScore(fullAp)
                + StrategicCardEvaluator.StrategicResourceCostValue(plan.ResCost, snap);
            float attemptCost = ActionPrice.ToCardScore(1f)
                + StrategicCardEvaluator.StrategicResourceCostValue(equipment.resourceCost, snap);
            float penalties = AiConfigV2.stratChainGenerationStepPenalty + AiConfigV2.stratChainAttachStepPenalty;
            foreach (float chance in new[] { 0f, 0.5f, 1f })
            {
                generation.SuccessChance = chance;
                var phaseA = StrategicCardEvaluator.ScoreForDemand(plan, demand, plan.ExpectedTraits,
                    inv, host.Definition.moveMax, false, snap);
                var phaseB = StrategicCardEvaluator.ScoreSurplus(plan, inv, false, false, null, projected, snap);
                float expected = -(chance * fullCost + (1f - chance) * attemptCost + penalties);
                Assert.That(phaseA.Breakdown.ResourceEfficiency, Is.EqualTo(expected).Within(1e-5f));
                Assert.That(phaseB.Breakdown.ResourceEfficiency, Is.EqualTo(expected).Within(1e-5f));
                Assert.That(plan.ApCost, Is.EqualTo(fullAp), "Expected pricing must not reduce the funded chain");
                Assert.That(plan.ResCost.energy, Is.EqualTo(2));
                Assert.That(plan.ResCost.materials, Is.EqualTo(3));
            }

            var op = new DevelopmentOpportunity { Card = equipment, Generation = generation,
                RecipientCard = host, SuccessChance = 0.5f };
            generation.SuccessChance = op.SuccessChance;
            var upgrade = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(op, generation, DesireAxis.Development);
            float ready = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(op, upgrade, snap, null, null, null);
            Assert.That(ready, Is.EqualTo(-(attemptCost + ActionPrice.ToCardScore(0.5f) + penalties)).Within(1e-5f),
                "READY funds creation only, but prices the same success-contingent attachment");
            Assert.That(upgrade.ApCost, Is.EqualTo(1f));
            Assert.That(upgrade.DeferredAttachmentAp, Is.EqualTo(1f));
        }

        // ---- Unified Research/Production preparation scoring (operator = a hero card; the site =
        // infrastructure; the fixed Researcher/Assembler value comes from the ability only).
        private static readonly HexCoord PrepSite = new HexCoord(3, -1);

        private static WorldSnapshot PrepSnapshot(bool researchPath, bool productionPath,
            float economicSecurity = 0.5f) => new WorldSnapshot
        {
            TurnNumber = 3,
            Self = new SelfSnapshot
            {
                Stockpile = new ResourceBundle { Human = 10f, Energy = 10f, Materials = 10f, Tech = 10f },
                PerTurnIncome = new ResourceBundle { Human = 1f, Energy = 1f, Materials = 1f, Tech = 1f },
                Hand = Array.Empty<CardData>(), Deck = Array.Empty<CardDefinition>(),
                Armies = Array.Empty<ArmySnapshot>(),
            },
            Development = new DevelopmentReadiness
            {
                ResearchPreparationViable = researchPath, ProductionPreparationViable = productionPath,
            },
            Economy = new EconomyStanding
            {
                PerType = Array.Empty<EconomyResourceStanding>(), EconomicSecurity = economicSecurity,
            },
        };

        private static CardDefinition PrepHero(string[] abilities, bool supportTag = false) => new CardDefinition
        {
            cardType = CardType.Hero, authoredKey = "prep-hero", commandRating = 0,
            grantedAbilities = abilities.ToList(),
            unitTypeTags = supportTag ? new System.Collections.Generic.List<UnitTypeTag> { UnitTypeTag.Support }
                : new System.Collections.Generic.List<UnitTypeTag>(),
        };

        private static (StrategicCardUseCandidate demand, StrategicCardUseCandidate surplus) ScoreOperatorBothPhases(
            CardDefinition def, ResearchProductionMode mode, WorldSnapshot snap)
        {
            var card = new CardData(def);
            var garrison = new ArmyData { IsGarrison = true, Hex = PrepSite };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development, Capability = CapabilityKind.DevelopmentOperator,
                DesiredAmount = 1, TargetHex = PrepSite, DevelopmentOperatorMode = mode,
            };
            var abilities = MaterializationChainMatching.EffectiveAbilities(def, null, null);
            MaterializationPlan plan = MaterializationPlanFactory.MakeExistingPlan(
                MaterializationChainKind.Direct, demand, card, 0, null, -1,
                new PlacementOption(PrepSite, DeploymentKind.Garrison, garrison), abilities);
            var inv = new CapabilityInventory { AvailableHeroes = 3 };   // no scarce-hero floor
            return (
                StrategicCardEvaluator.ScoreForDemand(plan, demand, plan.ExpectedTraits, inv, def.moveMax,
                    false, snap),
                StrategicCardEvaluator.ScoreSurplus(plan, inv, false, true, null, abilities, snap));
        }

        [TestCase(ResearchProductionMode.Production, "Assembler")]
        [TestCase(ResearchProductionMode.Research, "Researcher")]
        public void OperatorFixedSkillIsTheSameAbilityValueInBothPhasesAndOnlyFromTheAbility(
            ResearchProductionMode mode, string ability)
        {
            WorldSnapshot snap = PrepSnapshot(true, true);
            var plain = ScoreOperatorBothPhases(PrepHero(new string[0]), mode, snap);
            var skilled = ScoreOperatorBothPhases(PrepHero(new[] { ability }), mode, snap);
            Assert.That(skilled.demand.IntendedRole, Is.EqualTo(IntendedRole.Development));
            Assert.That(skilled.demand.Breakdown.RoleFit - plain.demand.Breakdown.RoleFit,
                Is.EqualTo(AiConfigV2.developmentOperatorSkillValue).Within(0.0001f),
                "Phase A pays the Assembler/Researcher ability exactly once, at the one coefficient");
            Assert.That(skilled.surplus.IntendedRole, Is.EqualTo(IntendedRole.Development));
            Assert.That(skilled.surplus.Breakdown.RoleFit - plain.surplus.Breakdown.RoleFit,
                Is.EqualTo(AiConfigV2.developmentOperatorSkillValue).Within(0.0001f),
                "Phase B pays the SAME value for the same deployment: no 1.1 infrastructure overwrite");
            Assert.That(skilled.demand.Breakdown.ResourceEfficiency,
                Is.EqualTo(plain.demand.Breakdown.ResourceEfficiency).Within(0.0001f),
                "The ability changes the benefit, never the current price");
            Assert.That(skilled.surplus.Breakdown.ResourceEfficiency,
                Is.EqualTo(skilled.demand.Breakdown.ResourceEfficiency).Within(0.0001f),
                "Both phases charge the same current price for the same card and place");
        }

        [Test]
        public void SupportTagAloneEarnsNoProductionValueButTheSkillWithoutTheTagDoes()
        {
            WorldSnapshot snap = PrepSnapshot(true, true);
            var plain = ScoreOperatorBothPhases(PrepHero(new string[0]), ResearchProductionMode.Production, snap);
            var tagged = ScoreOperatorBothPhases(PrepHero(new string[0], supportTag: true),
                ResearchProductionMode.Production, snap);
            Assert.That(tagged.surplus.Breakdown.RoleFit, Is.EqualTo(plain.surplus.Breakdown.RoleFit).Within(0.0001f));
            Assert.That(tagged.surplus.IntendedRole, Is.Not.EqualTo(IntendedRole.Development));
            var skilledUntagged = ScoreOperatorBothPhases(PrepHero(new[] { UnitAbilities.Assembler }, supportTag: false),
                ResearchProductionMode.Production, snap);
            Assert.That(skilledUntagged.surplus.Breakdown.RoleFit,
                Is.GreaterThan(plain.surplus.Breakdown.RoleFit + 0.29f));
        }

        [Test]
        public void HeroCarryingBothOperatorSkillsIsPaidForTheDeploymentOnce()
        {
            WorldSnapshot snap = PrepSnapshot(true, true);
            var one = ScoreOperatorBothPhases(PrepHero(new[] { UnitAbilities.Assembler }),
                ResearchProductionMode.Production, snap);
            var both = ScoreOperatorBothPhases(PrepHero(new[] { UnitAbilities.Assembler, UnitAbilities.Researcher }),
                ResearchProductionMode.Production, snap);
            Assert.That(both.demand.Breakdown.RoleFit, Is.EqualTo(one.demand.Breakdown.RoleFit).Within(0.0001f));
            Assert.That(both.surplus.Breakdown.RoleFit, Is.EqualTo(one.surplus.Breakdown.RoleFit).Within(0.0001f));
        }

        [Test]
        public void NoCompatiblePreparationPathInventsNoDevelopmentUse()
        {
            var noPath = ScoreOperatorBothPhases(PrepHero(new[] { UnitAbilities.Assembler }),
                ResearchProductionMode.Production, PrepSnapshot(researchPath: true, productionPath: false));
            var plain = ScoreOperatorBothPhases(PrepHero(new string[0]),
                ResearchProductionMode.Production, PrepSnapshot(researchPath: true, productionPath: false));
            Assert.That(noPath.surplus.Breakdown.RoleFit, Is.EqualTo(plain.surplus.Breakdown.RoleFit).Within(0.0001f),
                "A Production operator with only a Research path has no Development use in Phase B");
            Assert.That(noPath.surplus.IntendedRole, Is.Not.EqualTo(IntendedRole.Development));
        }

        [Test]
        public void ResearchProductionSiteIgnoresEconomicInsecurityWhileOtherFacilitiesKeepIt()
        {
            var factory = new CardData(new CardDefinition
            {
                cardType = CardType.Facility, authoredKey = "f",
                grantedAbilities = new System.Collections.Generic.List<string> { UnitAbilities.Production },
            });
            var other = new CardData(new CardDefinition
            {
                cardType = CardType.Facility, authoredKey = "other",
                grantedAbilities = new System.Collections.Generic.List<string>(),
            });
            float Site(CardData c, float security) => StrategicCardEvaluator.ScoreNonCombat(
                NonCombatRole.Facility, c, PrepSnapshot(true, true, security), null, null, 0f).NetScore;
            Assert.That(Site(factory, 0.05f), Is.EqualTo(Site(factory, 0.95f)).Within(0.0001f),
                "A Laboratory/Factory earns no income: economic security must not change its intrinsic value");
            Assert.That(Site(factory, 0.5f), Is.EqualTo(AiConfigV2.nonCombatFacilityValue).Within(0.0001f),
                "Its intrinsic value is the single infrastructure value, minus a free play's price");
            Assert.That(Site(other, 0.05f), Is.GreaterThan(Site(other, 0.95f)),
                "A non-Development facility keeps its existing insecurity-scaled value");
        }

        // ---- Phase A arbitration: a Development step competes with chains on ONE weighted score.
        private static float ChainBid(float decision, DesireAxis axis, Radar radar) =>
            MaterializationPortfolioSolver.ArbitrationScore(new PhaseACandidate(
                new DemandState { Demand = new AxisDemand { RequestingAxis = axis } },
                new DemandCandidate(new MaterializationPlan(), 0f, decision, 0f, decision)), radar);

        private static Radar Weights(float development, float aggression)
        {
            var radar = new Radar();
            foreach (DesireAxis a in DesireAxes.All) radar.Weight[a] = 0f;
            radar.Weight[DesireAxis.Development] = development;
            radar.Weight[DesireAxis.Aggression] = aggression;
            return radar;
        }

        [Test]
        public void DevelopmentStepBeatsAWeakerUnitAndYieldsToAStrongerOneWithoutAHardPriority()
        {
            Radar even = Radar.Even();
            float step = MaterializationPortfolioSolver.InfrastructureArbitrationScore(0.5f, DesireAxis.Development, even);
            Assert.That(step, Is.GreaterThan(ChainBid(0.3f, DesireAxis.Aggression, even)),
                "A feasible, better Development step is not parked behind a worse unit");
            Assert.That(step, Is.LessThan(ChainBid(0.8f, DesireAxis.Aggression, even)),
                "A better unit is not displaced by the Development step either: no reverse hard priority");
        }

        [Test]
        public void OnlyTheRadarWeightFlipsTheWinnerAndIsAppliedExactlyOnce()
        {
            Radar unitHeavy = Weights(development: 0.1f, aggression: 0.4f);
            Radar developmentHeavy = Weights(development: 0.4f, aggression: 0.1f);
            float intrinsicStep = 0.5f, intrinsicUnit = 0.5f;
            Assert.That(MaterializationPortfolioSolver.InfrastructureArbitrationScore(intrinsicStep, DesireAxis.Development, unitHeavy),
                Is.LessThan(ChainBid(intrinsicUnit, DesireAxis.Aggression, unitHeavy)));
            Assert.That(MaterializationPortfolioSolver.InfrastructureArbitrationScore(intrinsicStep, DesireAxis.Development, developmentHeavy),
                Is.GreaterThan(ChainBid(intrinsicUnit, DesireAxis.Aggression, developmentHeavy)));
            Assert.That(
                MaterializationPortfolioSolver.InfrastructureArbitrationScore(intrinsicStep, DesireAxis.Development, developmentHeavy)
                / MaterializationPortfolioSolver.InfrastructureArbitrationScore(intrinsicStep, DesireAxis.Development, unitHeavy),
                Is.EqualTo(0.4f / 0.1f).Within(0.001f),
                "The radar enters the final comparison once, linearly: no second multiplication or added desire");
        }

        [Test]
        public void ZeroDevelopmentRadarAndNonPositiveStepCannotWinByTheRadar()
        {
            Radar zero = Weights(development: 0f, aggression: 0.5f);
            Assert.That(MaterializationPortfolioSolver.InfrastructureArbitrationScore(5f, DesireAxis.Development, zero), Is.Zero);
            Assert.That(MaterializationPortfolioSolver.InfrastructureArbitrationScore(-0.2f, DesireAxis.Development, Weights(1f, 0f)),
                Is.LessThan(0f), "A radar weight never turns a negative action into a positive one");
        }

        [Test]
        public void GeneratedOperatorStepKeepsTheChallengeUndiscountedAndExcludesTheLaterDeployment()
        {
            WorldSnapshot snap = PrepSnapshot(true, true);
            CardDefinition def = PrepHero(new[] { UnitAbilities.Assembler });
            def.apCost = 2; def.activationApCost = 1; def.resourceCost = new ResourceCost(human: 1);
            var garrison = new ArmyData { IsGarrison = true, Hex = PrepSite };
            var inv = new CapabilityInventory { AvailableHeroes = 3 };
            float Score(float chance) => DevelopmentPreparationScorer.GeneratedOperator(
                new GenerationStep { CardDef = def, SuccessChance = chance, CardKey = "k", UseKey = "u" },
                PrepSite, ResearchProductionMode.Production, garrison, snap, inv, null, null);

            float expectedChallenge = ActionPrice.ToCardScore(2f)
                + StrategicCardEvaluator.StrategicResourceCostValue(def.resourceCost, snap)
                + AiConfigV2.stratChainGenerationStepPenalty;
            Assert.That(Score(0f), Is.EqualTo(-expectedChallenge).Within(0.0001f),
                "A failed Challenge keeps the full payment and none of the benefit; the minted card's later activation AP is not part of this step");
            Assert.That(Score(1f), Is.GreaterThan(Score(0.5f)));
            Assert.That(Score(0.5f), Is.GreaterThan(Score(0f)),
                "The operator's benefit is contingent on success; the payment is not discounted");
        }

        [Test]
        public void PreparationStepsAreAdmittedOnlyWhileTheirCardScoreIsPositive()
        {
            WorldSnapshot snap = PrepSnapshot(true, true);
            var garrison = new ArmyData { IsGarrison = true, Hex = PrepSite };
            var inv = new CapabilityInventory { AvailableHeroes = 3 };
            CardDefinition cheap = PrepHero(new[] { UnitAbilities.Assembler });
            CardDefinition dear = PrepHero(new[] { UnitAbilities.Assembler });
            dear.apCost = 30;
            Assert.That(DevelopmentPreparationScorer.HandOperator(new CardData(cheap), 0, PrepSite,
                ResearchProductionMode.Production, garrison, snap, inv, null, null), Is.GreaterThan(0f));
            Assert.That(DevelopmentPreparationScorer.HandOperator(new CardData(dear), 0, PrepSite,
                ResearchProductionMode.Production, garrison, snap, inv, null, null), Is.LessThan(0f));
            var factory = new CardData(new CardDefinition
            {
                cardType = CardType.Facility, authoredKey = "f",
                grantedAbilities = new System.Collections.Generic.List<string> { UnitAbilities.Production },
            });
            Assert.That(DevelopmentPreparationScorer.Facility(factory, 2, new ResourceCost(materials: 1),
                snap, inv, null, null, null), Is.GreaterThan(0f));
            Assert.That(DevelopmentPreparationScorer.Facility(factory, 30, new ResourceCost(materials: 1),
                snap, inv, null, null, null), Is.LessThan(0f),
                "Capacity upgrade + placement are priced as today's step, so an unpayable-in-value stage is not played");
        }
    }
}
#endif