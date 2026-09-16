from pathlib import Path

branch = 'audit/ai-v2-taskscore-2026-09-16'
demand = Path('Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs')
s = demand.read_text()
old = '''            if (!incumbentValue.HasValue)
                return first;  // no canonical comparator; keep the existing commitment
'''
new = '''            if (!incumbentValue.HasValue)
                return refreshed;  // unknown canonical value: only the incumbent may execute
'''
assert s.count(old) == 1, 'Base null-score fallback changed'
s = s.replace(old, new)
old = '''            return challenger ?? first;
        }

        // One hysteresis/admission predicate reused by Demand, Phase A and Continuity.'''
new = '''            // An unrelated candidate must not execute while the old Base still owns its
            // card/actor. If its site is no longer offered, Continuity owns retirement.
            return challenger ?? refreshed;
        }

        // One hysteresis/admission predicate reused by Demand, Phase A and Continuity.'''
assert s.count(old) == 1, 'Base fallback changed'
s = s.replace(old, new)
demand.write_text(s)
print('HARDENED Base incumbent fallback: non-switching challenger never executable')

tests = Path('Assets/Editor/AiTaskScoreBaseSwitchRegressionTests.cs')
s = tests.read_text()
anchor = '''        [Test]
        public void Retarget_PreservesOneActorAndCard_RekeysIntentAndAttempt()'''
insert = '''        [Test]
        public void Selector_RejectsUnqualifiedRivalWhenIncumbentSiteMissingOrScoreUnknown()
        {
            var player = new PlayerSetupData();
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            try
            {
                MissionIntent incumbent = MissionContinuityLayer.BeginEconomyDelivery(
                    player, BaseDemand(card, 9, 3, 20f, 40f), 9, 1);
                AxisDemand rival = BaseDemand(card, 9, 6, 29f, 80f);
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { rival }, new[] { incumbent }), Is.Null,
                    "a rejected rival must not escape as the only executable demand");
                incumbent.Economy.IntrinsicValue = null;
                rival.Value = 90f;
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { rival }, new[] { incumbent }), Is.Null,
                    "unknown incumbent net value must not authorize any switch");
                AxisDemand incumbentSite = BaseDemand(card, 9, 3, 20f, 40f);
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { incumbentSite, rival }, new[] { incumbent }),
                    Is.SameAs(incumbentSite));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void Retarget_RejectsMidTurnMoveWithoutChangingDurableIntent()
        {
            var player = new PlayerSetupData();
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            try
            {
                MissionIntent incumbent = MissionContinuityLayer.BeginEconomyDelivery(
                    player, BaseDemand(card, 9, 3, 20f, 40f), 9, 1);
                MissionIntentKey oldKey = incumbent.IntentKey;
                StableMissionKey oldAttempt = incumbent.LastAttemptKey;
                incumbent.LastProgressTurn = 2;
                incumbent.StepsMovedTotal = 1;
                AxisDemand rival = BaseDemand(card, 9, 6, 34f, 38f);
                rival.EconomySwitchIncumbentValue = 20f;
                Assert.That(MissionContinuityLayer.TryRetargetCommittedBase(
                    player, incumbent, rival, 2), Is.False);
                Assert.That(incumbent.IntentKey, Is.EqualTo(oldKey));
                Assert.That(incumbent.LastAttemptKey, Is.EqualTo(oldAttempt));
                Assert.That(MissionIntentRegistry.GetOrCreate(player).Count, Is.EqualTo(1));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

'''
assert s.count(anchor) == 1, 'test insertion anchor changed'
s = s.replace(anchor, insert + anchor)
tests.write_text(s)
print('ADDED Base fallback and mid-turn movement regressions')

ledger = Path('Docs/ai-v2-taskscore-open-issues-2026-09-16.md')
s = ledger.read_text()
start = s.index('Status: OPEN / NOT FIXED / DRAFT PR #50.')
end = s.index('\n\nRules:', start)
s = s[:start] + '''Status: FIXES IMPLEMENTED IN DRAFT PR #50, UNITY VALIDATION PENDING. This is the SINGLE TaskScore audit ledger; `ai-v2-taskscore-audit-2026-09-16.md` contains the original stage trace and synthetic examples. Baseline `master`: `cd773013a28869caa10fd9aa177df2aadf39471b` (2026-09-16). Production fixes for F1/F4/F5: commit `7e40cb74`; obsolete scoring cleanups C1–C6: `a1755487`; Base switch F2/F3: `09167bb`, plus follow-up safeguards in the succeeding hardening commit. Unity compilation/EditMode and game replay NOT RUN. No merge until Unity validation succeeds.'''+s[end:]
s=s.replace('Status for all below is OPEN unless explicitly marked otherwise.', 'All F1–F5 and C1–C6 changes are committed in the PR; runtime/Unity validation remains OPEN.')
s=s.replace('## Confirmed behavioral defects — five', '''## Implemented versus pending

Implemented in PR: F1–F5 behavior and C1–C6 cleanup, with AP, Base income and Base switch regression sources. Pending: Unity compilation, EditMode execution and in-game turn-log replay; tests have NOT been executed. R1/R3/R4 remain evidence-gathering investigations, R2 intentionally skipped. Do not mistake GitHub Actions source-transform success or `git diff --check` for Unity test results.

## Confirmed behavioral defects — five (implemented, Unity validation pending)''')
for name in ('F1','F2','F3','F4','F5'):
    s=s.replace('**'+name+' —', '**'+name+' [IMPLEMENTED / UNITY TEST PENDING] —')
for name in ('C1','C2','C3','C4','C5','C6'):
    s=s.replace('**'+name+' —', '**'+name+' [CLEANED / UNITY TEST PENDING] —')
s=s.replace('Regression already added: `Assets/Editor/AiTaskScoreAllocatorRegressionTests.cs` (NOT RUN, expected RED).', 'Regression added: `Assets/Editor/AiTaskScoreAllocatorRegressionTests.cs` (NOT RUN after fix).')
s=s.replace('F2 and F3 both need fixing for switching to work.', 'F2/F3 changed together in `09167bb`; source tests added, execution pending.')
s=s.replace('Draft PR remains NOT READY until all F1–F5 are fixed and tested; cleanups C1–C6 should follow without blocking behavioral regressions unnecessarily.', 'All five fixes and six cleanups are implemented in draft PR #50; PR remains NOT READY until Unity compilation, EditMode tests and relevant game logs validate them.')
ledger.write_text(s)
print('UPDATED ledger status with clear pending Unity validation')

# Protect the same single source of truth and guard against lost edits.
assert 'return challenger ?? refreshed;' in demand.read_text()
assert 'TryRetargetCommittedBase' in Path('Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.cs').read_text()
assert 'EconomicHexBenefit' in Path('Assets/Scripts/Ai/V2/Evaluation/TaskScore.cs').read_text()
for path in (demand, tests, ledger):
    assert path.read_text().endswith('\n'), f'{path}: missing final newline'
print('HARDENING_SOURCE_CHECK_PASS')
