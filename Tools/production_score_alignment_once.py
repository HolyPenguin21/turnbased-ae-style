#!/usr/bin/env python3
"""Apply narrow score-currency alignment inside the existing Development and Phase-A owners."""
from pathlib import Path

DEV = Path('Assets/Scripts/Ai/V2/Strategy/Objectives/DevelopmentOpportunityEvaluator.cs')
BUILDER = Path('Assets/Scripts/Ai/V2/Materialization/MaterializationCandidateBuilder.cs')


def once(content, original, updated, label):
    count = content.count(original)
    assert count == 1, f'{label}: expected one owner, got {count}'
    print('PASS', label)
    return content.replace(original, updated, 1)


def patch_dev(content):
    content = once(content,
        '''            op.ProductionSupport = op.Mode == ResearchProductionMode.Production
                ? snap?.Development?.ProductionSupport ?? AiConfigV2.productionSupportMin
                : 1f;''',
        '''            // This evaluator produces Equipment in either Research or Production mode.
            // Economy support follows the produced capability, never the facility's label;
            // StrategicCardEvaluator applies this same output-type rule to Unit/Hero mints.
            op.ProductionSupport = snap?.Development?.ProductionSupport
                ?? AiConfigV2.productionSupportMin;''',
        'equipment production support independent of generation mode')
    marker = '''        // Best legal recipient for an Equipment offering. Hand Unit/Hero cards + own on-map units,
'''
    assert content.count(marker) == 1
    method = '''        // Cross-lane calibration: Development.Ev is expressed in persistent AiPower units
        // for Development objective ranking; the shared Phase-A portfolio compares *card-use*
        // utility (RoleFit-like units). Project the ALREADY-scored marginal benefit and displaced
        // alternative onto the canonical AiPower-per-body unit, then charge the very same
        // resource/AP/chain costs that StrategicCardEvaluator charges other card chains.
        // Do NOT apply devEquipmentPersistenceMultiplier here: both an ordinary deployed card
        // and attached equipment persist, and only Development's objective EV needs the longer
        // lifetime horizon to decide whether preparing its prerequisites is worthwhile.
        internal static float MaterializationValue(DevelopmentOpportunity op, MaterializationPlan plan,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            if (op == null || plan == null || plan.Kind != MaterializationChainKind.GenerateAttachUpgrade)
                return float.NegativeInfinity;
            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            float benefit = op.SuccessChance * op.ExpectedGain * op.ProductionSupport / powerUnit;
            float displacedAlternative = op.AlternativeValue / powerUnit;
            System.Func<ResourceType, float> spendable = root == null ? null
                : (System.Func<ResourceType, float>)(type =>
                    StrategicSpendability.SpendableAmount(player, root, ctx, type));
            float resourcePrice = StrategicCardEvaluator.StrategicResourceCostValue(
                plan.ResCost, snap, spendable, player);
            float apPrice = plan.ApCost * AiConfigV2.stratCardApCostWeight;
            float chainPrice = AiConfigV2.stratChainGenerationStepPenalty
                + AiConfigV2.stratChainAttachStepPenalty;
            return benefit - displacedAlternative - resourcePrice - apPrice - chainPrice;
        }

'''
    content = content.replace(marker, method + marker, 1)
    print('PASS Development objective EV -> card utility explicit single-owner adapter')
    return content


def patch_builder(content):
    content = once(content,
        '''                MaterializationPlan upgrade = candidates[0].plan;
                upgrade.Score = demand.DevOpportunity.Ev;
                float decision = upgrade.Score + DemandUrgencyPolicy.Bonus(demand);
                return new List<DemandCandidate>
                {
                    new DemandCandidate(upgrade, 0f, upgrade.Score, 0f, decision),
                };''',
        '''                MaterializationPlan upgrade = candidates[0].plan;
                // Development EV ranks/stages the opportunity in AiPower units; the
                // shared portfolio MUST compare its card-use utility against other cards.
                upgrade.Score = DevelopmentOpportunityEvaluator.MaterializationValue(
                    demand.DevOpportunity, upgrade, snap, player, root, ctx);
                float urgency = DemandUrgencyPolicy.Bonus(demand);
                float decision = upgrade.Score + urgency * GenerationChanceForDecision(upgrade);
                AiDebugLog.WriteVerbose($"[AI][V2][Dev] materialization EV={demand.DevOpportunity.Ev:0.00} "
                    + $"cardScore={upgrade.Score:0.00} urgency={urgency:0.00} "
                    + $"p={GenerationChanceForDecision(upgrade):0.00} decision={decision:0.00}");
                return new List<DemandCandidate>
                {
                    new DemandCandidate(upgrade, 0f, upgrade.Score, 0f, decision),
                };''',
        'CardUpgrade portfolio score uses shared card currency and chance-weighted urgency')
    return content

# All exact-location guards finish before the first source write.
updated = {
    DEV: patch_dev(DEV.read_text(encoding='utf-8')),
    BUILDER: patch_builder(BUILDER.read_text(encoding='utf-8')),
}
assert updated[DEV].count('internal static float MaterializationValue(') == 1
assert updated[BUILDER].count('DevelopmentOpportunityEvaluator.MaterializationValue(') == 1
assert updated[BUILDER].count('upgrade.Score = demand.DevOpportunity.Ev;') == 0
assert updated[BUILDER].count('GenerationChanceForDecision(upgrade)') == 2
for file, text in updated.items():
    file.write_text(text, encoding='utf-8')
print('PASS source changes complete (no new strategy layers or changed gameplay execution)')
