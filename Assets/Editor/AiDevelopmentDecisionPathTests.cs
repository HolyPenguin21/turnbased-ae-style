#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Integrates the existing pure decision stages. This deliberately does not claim to
    // exercise live GenerationSource affordability, Challenge, or Unity movement.
    public sealed class AiDevelopmentDecisionPathTests
    {
        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void EnumeratedRecipientsKeepTheFourthFallbackForTheSharedPortfolio(AttachmentSlot slot)
        {
            var ownerObject = new GameObject("development-recipient-test");
            try
            {
                var root = ownerObject.AddComponent<PlayerRoot>();
                root.ActionPoints = 100;
                var player = new PlayerSetupData();
                var hand = new AiHandData(null, default, 0);
                var hosts = Enumerable.Range(0, 4)
                    .Select(_ => new CardData(AttachmentSlotTests.Host(hero: true))).ToArray();
                foreach (var host in hosts) hand.AddCard(host);
                var attachment = AttachmentSlotTests.Attachment(slot, EquipmentStat.Fate, 1);
                attachment.apCost = 1; attachment.activationApCost = 1;
                var generation = new GenerationStep
                {
                    CardDef = attachment, ProducesEquipment = true, CardKey = "primary", SuccessChance = 1f,
                };
                var opportunities = DevelopmentOpportunityEvaluator.EquipmentOpportunities(
                    ResearchProductionMode.Production, default, attachment, 1f, generation,
                    null, new CapabilityInventory(), player, root, hand, out _);
                Assert.That(opportunities, Has.Count.EqualTo(4));
                var demand = new DemandState { Ordinal = 0, Demand = new AxisDemand
                    { RequestingAxis = DesireAxis.Development, Capability = CapabilityKind.CardUpgrade } };
                var options = new Dictionary<DemandState, List<DemandCandidate>>
                {
                    [demand] = opportunities.Select(op =>
                    {
                        var plan = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(
                            op, generation, DesireAxis.Development);
                        float value = StrategicCardEvaluator.EquipmentUpgradeValue(op);
                        return new DemandCandidate(plan, 0, value, 0, value);
                    }).ToList(),
                };
                // Three valuable deployment chains occupy the first three physical hosts. The
                // fourth is below the normal Top-K=3 boundary but must remain selectable.
                for (int i = 0; i < 3; i++)
                {
                    var state = new DemandState { Ordinal = i + 1, Demand = new AxisDemand
                        { RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.Hero } };
                    var plan = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.Direct,
                        state.Demand, hosts[i], i, null, -1,
                        new PlacementOption(new HexCoord(i, 0), DeploymentKind.NewArmy, null),
                        EquipmentSystem.EffectiveAbilities(hosts[i]));
                    options[state] = new List<DemandCandidate> { new DemandCandidate(plan, 0, 100, 0, 100) };
                }
                var chosen = MaterializationPortfolioSolver.BestInjectiveAssignment(
                    options, root, player, null, hand, 1);
                Assert.That(chosen, Has.Count.EqualTo(4));
                Assert.That(chosen[demand].Plan.UpgradeTargetCard, Is.SameAs(hosts[3]));
                Assert.That(root.ActionPoints, Is.EqualTo(100), "Planning must not debit the live bank");
                Assert.That(hosts.All(h => h.Equipment == null && h.Mutator == null), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(ownerObject); }
        }

        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void RecipientEnumerationRefreshesGainAndSlotLegalityAfterAnAttachment(AttachmentSlot slot)
        {
            var ownerObject = new GameObject("development-refresh-test");
            try
            {
                var root = ownerObject.AddComponent<PlayerRoot>(); root.ActionPoints = 20;
                var player = new PlayerSetupData();
                var hand = new AiHandData(null, default, 0);
                var host = new CardData(AttachmentSlotTests.Host(hero: true)); hand.AddCard(host);
                var output = AttachmentSlotTests.Attachment(slot, EquipmentStat.Fate,
                    slot == AttachmentSlot.Mutator ? 8 : 2, replace: slot == AttachmentSlot.Mutator);
                output.activationApCost = 1;
                List<DevelopmentOpportunity> Enumerate() => DevelopmentOpportunityEvaluator.EquipmentOpportunities(
                    ResearchProductionMode.Production, default, output, 1f, null,
                    null, new CapabilityInventory(), player, root, hand, out _);
                var before = Enumerate().Single();
                var otherSlot = slot == AttachmentSlot.Equipment ? AttachmentSlot.Mutator : AttachmentSlot.Equipment;
                var other = ResearchProductionSystem.MintCard(
                    AttachmentSlotTests.Attachment(otherSlot, EquipmentStat.Fate,
                        otherSlot == AttachmentSlot.Mutator ? 8 : 2, replace: otherSlot == AttachmentSlot.Mutator));
                Assert.That(EquipmentSystem.TryAttach(other, host, root, out _), Is.True);
                var after = Enumerate();
                if (slot == AttachmentSlot.Equipment)
                    Assert.That(after, Is.Empty, "A later-slot override erased the entire gain");
                else
                    Assert.That(after.Single().ExpectedGain, Is.LessThan(before.ExpectedGain),
                        "The other slot changed the baseline: the old marginal gain must not survive");
                Assert.That(EquipmentSystem.TryAttach(ResearchProductionSystem.MintCard(output), host, root, out _), Is.True);
                Assert.That(Enumerate(), Is.Empty, "A newly occupied destination slot must be excluded");
            }
            finally { UnityEngine.Object.DestroyImmediate(ownerObject); }
        }

        [Test]
        public void SignedUpgradeDeltaSurvivesMatchupAmplification()
        {
            var host = new CardData(new CardDefinition { cardType = CardType.Unit });
            var delta = new StrategicCardEvaluator.EquipmentDelta(0.6f, -0.7f);
            var op = new DevelopmentOpportunity
            {
                RecipientCard = host, MatchupFit = 1f,
                ExpectedGain = delta.Total * AiConfigV2.combatPowerPerBodyEstimate,
                TacticalGain = delta.Tactical * AiConfigV2.combatPowerPerBodyEstimate,
            };
            Assert.That(op.ExpectedGain, Is.LessThan(0f));
            Assert.That(StrategicCardEvaluator.EquipmentUpgradeValue(op),
                Is.EqualTo(StrategicCardEvaluator.EquipmentUpgradeValue(delta, 1f, true)).Within(0.0001f));
            Assert.That(StrategicCardEvaluator.EquipmentUpgradeValue(op), Is.GreaterThan(0f));
        }

        [Test]
        public void PortfolioUsesARecipientFallbackInsteadOfDiscardingAnUpgrade()
        {
            var attachment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Fate, 1);
            attachment.apCost = 1; attachment.activationApCost = 1;
            var definition = AttachmentSlotTests.Host(hero: true);
            var first = new CardData(definition); var second = new CardData(definition);
            MaterializationPlan Plan(string source, CardData recipient) =>
                MaterializationPlanFactory.MakeDevelopmentUpgradePlan(new DevelopmentOpportunity
                {
                    Card = attachment, RecipientCard = recipient,
                }, new GenerationStep { CardDef = attachment, ProducesEquipment = true, CardKey = source },
                    DesireAxis.Development);
            var a = new DemandState { Ordinal = 0, Demand = new AxisDemand
                { RequestingAxis = DesireAxis.Development, Capability = CapabilityKind.CardUpgrade } };
            var b = new DemandState { Ordinal = 1, Demand = new AxisDemand
                { RequestingAxis = DesireAxis.Development, Capability = CapabilityKind.CardUpgrade } };
            var preferred = Plan("a", first); var fallback = Plan("a", second); var only = Plan("b", first);
            var options = new Dictionary<DemandState, List<DemandCandidate>>
            {
                [a] = new List<DemandCandidate>
                {
                    new DemandCandidate(preferred, 0, 10, 0, 10),
                    new DemandCandidate(fallback, 0, 9, 0, 9),
                },
                [b] = new List<DemandCandidate> { new DemandCandidate(only, 0, 10, 0, 10) },
            };
            var chosen = MaterializationPortfolioSolver.BestInjectiveAssignment(options,
                null, null, null, null, 2);
            Assert.That(chosen, Has.Count.EqualTo(2));
            Assert.That(chosen[a].Plan, Is.SameAs(fallback));
            Assert.That(chosen[b].Plan, Is.SameAs(only));
            Assert.That(MaterializationPortfolioSolver.EstimateLegalApWorkload(options.ToDictionary(x => x.Key,
                x => x.Value.Select(c => (c.Plan, c.FollowupAp)).ToList()), null, null, null, null, 2),
                Is.EqualTo(fallback.ApCost + only.ApCost));
            a.Ordinal = 1; b.Ordinal = 0; options[a].Reverse();
            var reordered = MaterializationPortfolioSolver.BestInjectiveAssignment(options,
                null, null, null, null, 2);
            Assert.That(reordered[a].Plan, Is.SameAs(fallback));
            Assert.That(reordered[b].Plan, Is.SameAs(only));
        }

        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void ExistingRapidReactionDoesNotInventActivationSavings(AttachmentSlot slot)
        {
            var definition = AttachmentSlotTests.Host(hero: true);
            definition.grantedAbilities.Add(UnitAbilities.RapidReaction);
            var host = new CardData(definition);
            var attachment = AttachmentSlotTests.Attachment(slot, EquipmentStat.Attack, 20);
            var delta = StrategicCardEvaluator.EquipmentDeltaParts(attachment, host);
            Assert.That(EquipmentSystem.Project(host).Stats[EquipmentStat.ActivationApCost], Is.Zero);
            Assert.That(delta.Total, Is.Zero.Within(0.0001f),
                "Hero Attack has no combat value, and an already-free activation cannot become cheaper");
            Assert.That(host.Equipment, Is.Null);
            Assert.That(host.Mutator, Is.Null);
        }

        [Test]
        public void SameNamedPhysicalRecipientsHaveDistinctStablePlanKeys()
        {
            var host = AttachmentSlotTests.Host(hero: true);
            var attachment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Fate, 1);
            var g = new GenerationStep { CardDef = attachment, ProducesEquipment = true, CardKey = "source" };
            DevelopmentOpportunity Op(CardData card) => new DevelopmentOpportunity
            {
                Card = attachment, Generation = g, RecipientCard = card, RecipientLabel = "hand:copy",
            };
            var first = Op(new CardData(host));
            var second = Op(new CardData(host));
            string firstKey = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(first, g, DesireAxis.Development).StableKey;
            string secondKey = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(second, g, DesireAxis.Development).StableKey;
            Assert.That(firstKey, Is.Not.EqualTo(secondKey));
            Assert.That(MaterializationPlanFactory.MakeDevelopmentUpgradePlan(first, g, DesireAxis.Development).StableKey,
                Is.EqualTo(firstKey));
        }

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
                    Threats = new[] { AiDevelopmentRadarResourceGateTests.UncoveredBaseThreat() },
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
                WorldTaskScore = new TaskScore(forceAmplification: 8f),
            };
            // The READY CardUpgrade demand exactly as DemandLayer.Development shapes it.
            var devScore = new TaskScore();
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.CardUpgrade,
                DesiredAmount = 1,
                WorldTaskScore = devScore,
                Value = devScore.Value,
                DevOpportunity = opportunity,
            };
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
