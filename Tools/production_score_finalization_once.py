#!/usr/bin/env python3
"""One-shot guarded relocation of upgrade card scoring and output-type validation."""
from pathlib import Path

DEV = Path('Assets/Scripts/Ai/V2/Strategy/Objectives/DevelopmentOpportunityEvaluator.cs')
SCORE = Path('Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs')
BUILD = Path('Assets/Scripts/Ai/V2/Materialization/MaterializationCandidateBuilder.cs')
FACTORY = Path('Assets/Scripts/Ai/V2/Materialization/MaterializationPlanFactory.cs')
TEST = Path('Assets/Editor/AiProductionScoreAlignmentTests.cs')


def once(text, before, after, label):
    count = text.count(before)
    assert count == 1, f'{label}: expected exactly one anchor, found {count}'
    print('PASS', label)
    return text.replace(before, after, 1)


def patch_dev(text):
    start = '        // Cross-lane calibration: Development.Ev is expressed in persistent AiPower units\n'
    end = '        // Best legal recipient for an Equipment offering. Hand Unit/Hero cards + own on-map units,\n'
    assert text.count(start) == text.count(end) == 1
    prefix, rest = text.split(start, 1)
    removed, suffix = rest.split(end, 1)
    assert removed.count('internal static float MaterializationValue(') == 1
    return prefix + end + suffix


def patch_score(text):
    marker = '        // -----------------------------------------------------------------------------------------\n        //  PHASE A — a chain closing an explicit AxisDemand. The demand pins the primary role.\n'
    method = '''        // Generated Equipment strengthens an EXISTING host: price its marginal signed gain,
        // never the host's total combat power. Development.Ev remains the separate owner of
        // prerequisite investment ranking, expressed in persistent AiPower units. Phase A and
        // other card chains share this evaluator's ONE AP/resource/chain cost function.
        // Research and Production labels never decide output type: use the actual card definition.
        internal static float ScoreGeneratedEquipmentUpgrade(DevelopmentOpportunity op,
            MaterializationPlan plan, WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx)
        {
            if (op == null || op.Card == null || op.Card.cardType != CardType.Equipment
                || plan == null || plan.Kind != MaterializationChainKind.GenerateAttachUpgrade
                || !object.ReferenceEquals(plan.GeneratedEquipmentDef, op.Card)
                || !object.ReferenceEquals(plan.Generation?.CardDef, op.Card))
                return float.NegativeInfinity;

            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            float marginalBenefit = op.SuccessChance * op.ExpectedGain * op.ProductionSupport / powerUnit;
            float displacedAlternative = op.AlternativeValue / powerUnit;
            System.Func<ResourceType, float> spendable = root == null ? null
                : (System.Func<ResourceType, float>)(type =>
                    StrategicSpendability.SpendableAmount(player, root, ctx, type));
            return marginalBenefit - displacedAlternative - ResourceCost(plan, snap, spendable, player);
        }

'''
    return once(text, marker, method + marker, 'single strategic card scoring owner')


def patch_build(text):
    return once(text,
        'upgrade.Score = DevelopmentOpportunityEvaluator.MaterializationValue(',
        'upgrade.Score = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(',
        'builder routes upgrade card valuation to canonical scorer')


def patch_factory(text):
    return once(text,
        '''            if (g == null || g.CardDef == null)
                return null;

            ResourceCost rc = g.CardDef.resourceCost;''',
        '''            // Only a generated Equipment card can upgrade an existing recipient.
            // Unit/Hero generation is a different materialization outcome, never an upgrade.
            if (g?.CardDef == null || g.CardDef.cardType != CardType.Equipment
                || !g.ProducesEquipment || !object.ReferenceEquals(g.CardDef, op.Card)
                || (op.RecipientCard == null && op.RecipientUnit == null))
                return null;

            ResourceCost rc = g.CardDef.resourceCost;''',
        'factory enforces output-type and recipient invariant')


def patch_test(text):
    assert text.count('DevelopmentOpportunityEvaluator.MaterializationValue(') == 4
    text = text.replace('DevelopmentOpportunityEvaluator.MaterializationValue(',
                        'StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(')
    # The two score tests construct genuine generated-equipment plans, not impossible bare plans.
    first = '            float resourceCost = StrategicCardEvaluator.StrategicResourceCostValue(plan.ResCost, snapshot);'
    second = '            float weak = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade('
    fixture = '''            var equipment = new CardDefinition { cardType = CardType.Equipment };
            opportunity.Card = equipment;
            plan.Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true };
            plan.GeneratedEquipmentDef = equipment;
'''
    text = once(text, first, fixture + first, 'cost calibration uses genuine equipment plan')
    text = once(text, second, fixture + second, 'marginal gain calibration uses genuine equipment plan')
    text = once(text, 'using System.Reflection;\n', 'using System.Reflection;\nusing System.Linq;\n',
                'test imports Linq for reflection overload selection')
    marker = '''        [Test]
        public void EquipmentSupport_UsesTheProducedCardNotResearchOrProductionMode()
'''
    newtests = '''        [Test]
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
                SuccessChance = 1f, ExpectedGain = 99f, ProductionSupport = 1f,
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
        public void GenerationSupportDependsOnUnitOrEquipmentOutputNotFactoryVsLab()
        {
            var snap = new WorldSnapshot
            {
                Development = new DevelopmentReadiness
                {
                    ProductionSupport = AiConfigV2.productionSupportMin,
                },
            };
            MethodInfo support = typeof(StrategicCardEvaluator)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(m => m.Name == "ProductionSupportAdjustment"
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[1].ParameterType == typeof(GenerationStep));
            float Evaluate(CardType type, ResearchProductionMode mode)
            {
                var score = new StrategicUseScoreBreakdown { RoleFit = 2f };
                var generation = new GenerationStep
                {
                    Mode = mode, CardDef = new CardDefinition { cardType = type },
                };
                return (float)support.Invoke(null, new object[] { score, generation, snap, 0f });
            }
            Assert.That(Evaluate(CardType.Unit, ResearchProductionMode.Research),
                Is.EqualTo(Evaluate(CardType.Unit, ResearchProductionMode.Production)).Within(0.0001f));
            Assert.That(Evaluate(CardType.Equipment, ResearchProductionMode.Research),
                Is.EqualTo(Evaluate(CardType.Unit, ResearchProductionMode.Production)).Within(0.0001f));
            Assert.That(Evaluate(CardType.Facility, ResearchProductionMode.Production), Is.Zero);
            Assert.That(Evaluate(CardType.Unit, ResearchProductionMode.Production), Is.LessThan(0f));
        }

'''
    text = once(text, marker, newtests + marker, 'output-specific production regression coverage')
    return text

# Read/check every source before touching any file; a stale branch fails closed.
updated = {
    DEV: patch_dev(DEV.read_text(encoding='utf-8')),
    SCORE: patch_score(SCORE.read_text(encoding='utf-8')),
    BUILD: patch_build(BUILD.read_text(encoding='utf-8')),
    FACTORY: patch_factory(FACTORY.read_text(encoding='utf-8')),
    TEST: patch_test(TEST.read_text(encoding='utf-8')),
}
assert updated[DEV].count('MaterializationValue(') == 0
assert updated[SCORE].count('ScoreGeneratedEquipmentUpgrade(') == 1
assert updated[BUILD].count('StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(') == 1
assert 'case MaterializationChainKind.GenerateAttachUpgrade:' in updated[SCORE]
assert 'gd.cardType != CardType.Unit && gd.cardType != CardType.Hero' in Path(
    'Assets/Scripts/Ai/V2/Materialization/MaterializationChainEnumerator.cs').read_text(encoding='utf-8')
for path, content in updated.items():
    path.write_text(content, encoding='utf-8')
print('PASS five source/test files updated, type paths remain separate')
