#!/usr/bin/env python3
"""One-shot, exact-owner AI V2 Production calibration patch. No gameplay rule changes."""
from pathlib import Path
import re

EVALUATOR = Path('Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs')
BUILDER = Path('Assets/Scripts/Ai/V2/Materialization/MaterializationCandidateBuilder.cs')
DEMAND = Path('Assets/Scripts/Ai/V2/Strategy/Demand/AxisDemand.cs')


def replace_once(text, old, new, label):
    count = text.count(old)
    assert count == 1, f'{label}: expected exactly one location, found {count}'
    print(f'PASS {label}')
    return text.replace(old, new, 1)


def regex_once(text, pattern, new, label):
    result, count = re.subn(pattern, new, text)
    assert count == 1, f'{label}: expected exactly one location, found {count}'
    print(f'PASS {label}')
    return result


def patch_demand(text):
    start_marker = '        internal static float Normalized(AxisDemand demand)\n'
    end_marker = '        internal static float Bonus(AxisDemand demand) =>\n'
    assert text.count(start_marker) == text.count(end_marker) == 1
    start = text.index(start_marker)
    end = text.index(end_marker, start)
    original = text[start:end]
    assert 'bool legacyDevelopment = demand.RequestingAxis == DesireAxis.Development;' in original
    assert 'AiConfigV2.taskScoreUrgencyRampLo' in original
    replacement = (
        '        internal static float Normalized(AxisDemand demand)\n'
        '        {\n'
        '            if (demand == null)\n'
        '                return 0f;\n'
        '            if (demand.RequestingAxis != DesireAxis.Development)\n'
        '                return NormalizedWorldValue(demand.Value);\n'
        '            return Mathf.Clamp01((demand.Value - AiConfigV2.stratHoldUrgencyRampLo)\n'
        '                / Mathf.Max(0.01f,\n'
        '                    AiConfigV2.stratHoldUrgencyRampHi - AiConfigV2.stratHoldUrgencyRampLo));\n'
        '        }\n\n'
        '        // Verified AGG/RCN resource blocks carry the same migrated world TaskScore.Value.\n'
        '        // Keep both urgency consumers on this one existing scale adapter.\n'
        '        internal static float NormalizedWorldValue(float value) =>\n'
        '            Mathf.Clamp01((value - AiConfigV2.taskScoreUrgencyRampLo)\n'
        '                / Mathf.Max(0.01f,\n'
        '                    AiConfigV2.taskScoreUrgencyRampHi - AiConfigV2.taskScoreUrgencyRampLo));\n\n'
    )
    print('PASS demand urgency and blockade normalization share one owner')
    return text[:start] + replacement + text[end:]


def patch_evaluator(text):
    text = regex_once(text,
        r'Curves\.Ramp\(demand\.Value,\s*AiConfigV2\.stratHoldUrgencyRampLo,\s*AiConfigV2\.stratHoldUrgencyRampHi\)',
        'DemandUrgencyPolicy.Normalized(demand)',
        'Production emergency floor uses per-axis demand urgency')
    text = regex_once(text,
        r'float urgency = Mathf\.Clamp01\(\s*\(block\.DemandValue - AiConfigV2\.stratHoldUrgencyRampLo\)\s*/ Mathf\.Max\(0\.01f,\s*AiConfigV2\.stratHoldUrgencyRampHi - AiConfigV2\.stratHoldUrgencyRampLo\)\);',
        'float urgency = DemandUrgencyPolicy.NormalizedWorldValue(block.DemandValue);',
        'verified AGG/RCN resource protection uses world task scale')
    text = replace_once(text,
        '            bd.SynergyValue = traits * 0.5f + equipmentUpgrade + ec.Synergy + ec.GlobalSynergy;',
        '            // EquipmentUpgrade already prices this exact delta in RoleFitCore.\n'
        '            bd.SynergyValue = traits * 0.5f\n'
        '                + (role == IntendedRole.EquipmentUpgrade ? 0f : equipmentUpgrade)\n'
        '                + ec.Synergy + ec.GlobalSynergy;',
        'EquipmentUpgrade does not double-count its marginal improvement')
    text = replace_once(text,
        '            System.Func<ResourceType, float> spendableResource = null)\n'
        '        {\n'
        '            var bd = new StrategicUseScoreBreakdown();\n'
        '            IntendedRole role = RoleForCapability(demand.Capability, PlanBaseDef(plan));',
        '            System.Func<ResourceType, float> spendableResource = null,\n'
        '            PlayerSetupData player = null)\n'
        '        {\n'
        '            var bd = new StrategicUseScoreBreakdown();\n'
        '            IntendedRole role = RoleForCapability(demand.Capability, PlanBaseDef(plan));',
        'Phase A accepts owner for already-existing canonical resource valuation')
    text = replace_once(text,
        '            bd.ResourceEfficiency = -ResourceCost(plan, snap, spendableResource);',
        '            bd.ResourceEfficiency = -ResourceCost(plan, snap, spendableResource, player);',
        'Phase A forwards owner to resource and residual-preservation valuation')
    return text


def patch_builder(text):
    text = replace_once(text,
        '                c.plan.Score = ScorePlanA(c.plan, demand, c.proj, inv, referenceMoveMax,\n'
        '                    hasCompetingHeroDemand, snap, witnessedUsefulApDemand, projectedLegalFillers);',
        '                c.plan.Score = ScorePlanA(c.plan, demand, c.proj, inv, referenceMoveMax,\n'
        '                    hasCompetingHeroDemand, snap, witnessedUsefulApDemand, projectedLegalFillers,\n'
        '                    player, root, ctx);',
        'Phase A candidate forwards exact player/root/context')
    text = replace_once(text,
        '            float? witnessedUsefulApDemand, int projectedLegalFillers)\n'
        '        {\n'
        '            StrategicCardUseCandidate cand = StrategicCardEvaluator.ScoreForDemand(\n'
        '                p, demand, projected, inv, referenceMoveMax, hasCompetingHeroDemand, snap,\n'
        '                witnessedUsefulApDemand, projectedLegalFillers);',
        '            float? witnessedUsefulApDemand, int projectedLegalFillers,\n'
        '            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)\n'
        '        {\n'
        '            StrategicCardUseCandidate cand = StrategicCardEvaluator.ScoreForDemand(\n'
        '                p, demand, projected, inv, referenceMoveMax, hasCompetingHeroDemand, snap,\n'
        '                witnessedUsefulApDemand, projectedLegalFillers,\n'
        '                type => StrategicSpendability.SpendableAmount(player, root, ctx, type), player);',
        'Phase A prices owner-aware spendable resources without new cost owner')
    return text


texts = {
    DEMAND: patch_demand(DEMAND.read_text(encoding='utf-8')),
    EVALUATOR: patch_evaluator(EVALUATOR.read_text(encoding='utf-8')),
    BUILDER: patch_builder(BUILDER.read_text(encoding='utf-8')),
}
# Finish every guarded check BEFORE writing any file: no partial source changes.
assert texts[DEMAND].count('internal static float NormalizedWorldValue(') == 1
assert texts[EVALUATOR].count('DemandUrgencyPolicy.Normalized(demand)') == 1
assert texts[EVALUATOR].count('DemandUrgencyPolicy.NormalizedWorldValue(block.DemandValue)') == 1
assert texts[BUILDER].count('ScorePlanA(') == 2
assert 'type => StrategicSpendability.SpendableAmount(player, root, ctx, type), player);' in texts[BUILDER]
for path, content in texts.items():
    path.write_text(content, encoding='utf-8')
print('PASS all four source defects corrected in existing owner methods')
