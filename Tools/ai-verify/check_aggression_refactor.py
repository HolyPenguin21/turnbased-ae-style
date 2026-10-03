#!/usr/bin/env python3
"""Source equivalence/ownership checks for the Aggression refactor; NOT a C# test runner.

Run from a checkout containing the baseline:
  python3 Tools/ai-verify/check_aggression_refactor.py --baseline bf12b8c6bf3f072b577738e11aad84bba6350afa
"""
from pathlib import Path
import argparse
import re
import subprocess

REPO = Path(__file__).resolve().parents[2]
V2 = REPO / 'Assets/Scripts/Ai/V2'

def method(source, name):
    m = re.search(r'^        (?:public|private|internal)[^\n]*\b' + name + r'\s*\(', source, re.M)
    assert m, 'missing method: ' + name
    brace = source.find('\n        {', m.end())
    arrow = source.find('=>', m.end())
    if arrow >= 0 and (brace < 0 or arrow < brace):
        end = source.index(';', arrow) + 1
    else:
        end = source.index('\n        }', brace) + len('\n        }')
    return source[m.start():end]

def canonical(source):
    source = re.sub(r'//[^\n]*', '', source)
    source = source.replace('private static', 'internal static')
    source = re.sub(r'\bAggressionObjectiveEvaluator\b', 'RaidObjectiveEvaluator', source)
    source = re.sub(r'\bAggressionObjective\b', 'RaidObjective', source)
    source = source.replace('TaskExecutor.ApplyReinforcementHandoff', 'GroundCombatReinforcementTransaction.ApplyReinforcementHandoff')
    source = re.sub(r'(?<!\.)\bApplyReinforcementHandoff\(', 'GroundCombatReinforcementTransaction.ApplyReinforcementHandoff(', source)
    source = source.replace('AiMapMemoryOpposition(', 'AiV2Util.KnownPrimaryOppositionLive(')
    source = source.replace('FindRaidSighting(', 'RaidObjectiveEvaluator.FindSightingLive(')
    source = source.replace('AttackObjectiveEvaluator.PreparationStagingBase',
                            'AttackPreparationPolicy.PreparationStagingBase')
    for name in ['ReturnBaseStillValid', 'KeepOrReselectHome', 'StagingBase', 'SelectReturnBase']:
        source = source.replace('MissionContinuityLayer.' + name, 'AiReturnBasePolicy.' + name)
    for name in ['Readiness', 'ForceReady', 'MobilizationOpen', 'MobilizationRawOpen', 'FieldStrikeForceReady']:
        source = source.replace('AttackObjectiveEvaluator.' + name, 'AttackForceReadiness.' + name)
    source = re.sub(r'0\.80f \* ((?:snap\.Self|self|session\.Snapshot\.Self)\.AttackPeak)',
                    r'AttackForceReadiness.RequiredPower(\1)', source)
    return re.sub(r'\s+', '', source.lstrip('\ufeff'))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--baseline', default='bf12b8c6bf3f072b577738e11aad84bba6350afa')
    args = parser.parse_args()
    cache = {}
    def baseline(path):
        if path not in cache:
            cache[path] = subprocess.check_output(['git', '-C', str(REPO), 'show',
                args.baseline + ':Assets/Scripts/Ai/V2/' + path], text=True)
        return cache[path]

    groups = {
        'Execution/TaskExecutor.cs': {
            'Execution/RaidExecutor.cs': ['RunRaid', 'RunRaidStep', 'RunRaidStepCore',
                'RunRaidAirSupportStep', 'RunRaidReturnStep', 'RunRaidReinforcementStep', 'FinishRaid'],
            'Execution/ActiveDefenceExecutor.cs': ['RunActiveDefence', 'RunActiveDefenceStep',
                'RunActiveDefenceStepCore', 'FinishActiveDefence'],
            'Execution/GroundCombatReinforcementTransaction.cs': ['ApplyReinforcementHandoff'],
        },
        'Continuity/MissionContinuityLayer.cs': {
            'Missions/AiReturnBasePolicy.cs': ['ReturnBaseStillValid', 'KeepOrReselectHome',
                'StagingBase', 'SelectReturnBase', 'BaseCollectedAmount',
                'BaseHasDevelopmentInfrastructure', 'BaseOwnPowerAt', 'BaseThreatSeverityAt'],
        },
        'Missions/AggressionMissionPlanner.cs': {
            'Missions/AggressionMissionPlanner.ActiveDefence.cs': ['AppendActiveDefence',
                'AppendActiveDefenceIntercept', 'BuildActiveDefenceWithdrawal'],
            'Missions/AggressionMissionPlanner.Raid.cs': ['ReturnCandidate', 'ReinforcementCandidate',
                'AirSupportCandidate', 'ToCandidate', 'BuildProposal', 'RaidTargetSitsOnHostileStructure'],
        },
        'Missions/AggressionMissionPlanner.Attack.cs': {
            'Missions/AggressionMissionPlanner.Attack.Gather.cs': ['TryAppendFreshAttackGather',
                'AppendAttackGather', 'BuildAttackGatherLeg'],
        },
        'Missions/GroundCombat/GroundCombatAssemblyPlanner.cs': {
            'Missions/GroundCombat/GroundCombatAssemblyPlanner.Projection.cs': ['ProjectedRoster',
                'ProjectedActivationApCost', 'ProjectedMaxMovement', 'ProjectedRosterOrNull',
                'LiveArmy', 'NonAviationProfiles', 'ProfileCombatValue'],
            'Missions/GroundCombat/GroundCombatAssemblyPlanner.Gather.cs': ['PlanGather', 'PlanGatherForHost'],
            'Missions/GroundCombat/GroundCombatAssemblyPlanner.Preparation.cs': ['PlanPreparationAssembly',
                'AssembleSameHex', 'FinishAssembly'],
            'Missions/GroundCombat/GroundCombatAssemblyPlanner.Reinforcement.cs': ['ReinforcementSupportCandidates',
                'SupportImprovesPrimary'],
        },
        'Strategy/Demand/AggressionDemandEvaluator.cs': {
            'Strategy/Demand/GroundCombatDemandPolicy.cs': ['RequiredSitePower', 'BoundPrimaryShortage',
                'CanDeliverIndependentFieldArmy', 'HandFieldCards', 'GroundGenerationOffers'],
            'Strategy/Demand/AggressionDemandEvaluator.ActiveDefence.cs': ['BuildActiveDefenceDemands'],
        },
    }
    checked = 0
    for old, targets in groups.items():
        for new, names in targets.items():
            current = (V2 / new).read_text(encoding='utf-8-sig')
            for name in names:
                assert canonical(method(baseline(old), name)) == canonical(method(current, name)), \
                    'unexpected implementation change: ' + new + ':' + name
                checked += 1

    scripts = list((REPO / 'Assets/Scripts').rglob('*.cs'))
    all_code = '\n'.join(p.read_text(encoding='utf-8-sig') for p in scripts)
    assert not re.search(r'\bAggressionObjective(?:Evaluator)?\b', all_code), 'old Raid alias survived'
    assert not re.search(r'0\.80f\s*\*\s*[^\n,;]*AttackPeak', all_code), 'duplicated Attack formula'
    force = (V2 / 'Foundation/AttackForceReadiness.cs').read_text()
    assert 'currentDeckPeakPower > 0f && attackArmyPower > RequiredPower(currentDeckPeakPower)' in force
    assert force.count('0.80f *') == 1, 'force formula has multiple owners'
    generic = (V2 / 'Execution/TaskExecutor.cs').read_text()
    for name in ['RunRaid', 'RunRaidStepCore', 'RunActiveDefence', 'ApplyReinforcementHandoff']:
        assert not re.search(r'^        (?:internal|private|public) static [^\n]*\b' + name + r'\(', generic, re.M)
    attack = (V2 / 'Strategy/Objectives/AttackObjectiveEvaluator.cs').read_text()
    assert 'Enumerate(' not in method(attack, 'ForTrackedTarget'), 'tracked lookup rediscovers targets'
    prep = (V2 / 'Strategy/Demand/AggressionDemandEvaluator.Attack.cs').read_text()
    shortage = method(prep, 'PreparationHostShortage')
    assert 'decision=DEFER {at}preparation_host_missing' in shortage
    assert shortage.index('preparation_host_missing') < shortage.index('IsPreparationHost(')
    garrison = (V2 / 'Strategy/Demand/AggressionDemandEvaluator.Garrison.cs').read_text()
    assert 'ConsumerMissionKind = MissionKind.Attack' not in garrison
    assert 'ConsumerPurpose = CapabilityConsumerPurpose.HeldBaseGarrison' in garrison
    orchestrator = (V2 / 'Missions/AggressionMissionPlanner.cs').read_text()
    assert orchestrator.index('AppendRaid(') < orchestrator.index('AppendActiveDefence(') < orchestrator.index('AppendAttack(')
    subprocess.run(['git', '-C', str(REPO), 'diff', '--check'], check=True)
    print(f'PASS: {checked} transferred method implementations match baseline (owner renames normalized).')
    print('PASS: threshold owner, strict inequality, tracked lookup, missing-host ordering, consumer identity, executor routing.')
    print('This checks source equivalence; it does not compile C# or execute NUnit/Unity.')

if __name__ == '__main__':
    main()
