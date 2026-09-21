#!/usr/bin/env python3
"""One-shot, exact-anchor patch. The workflow removes this helper from the resulting code commit."""
from pathlib import Path
import uuid


def change(filename, old, new):
    path = Path(filename)
    before = path.read_text(encoding='utf-8')
    count = before.count(old)
    if count != 1:
        raise RuntimeError(f'{filename}: expected one exact edit anchor, found {count}: {old[:90]!r}')
    path.write_text(before.replace(old, new, 1), encoding='utf-8')
    print('PATCHED', filename)


battle_ui = 'Assets/Scripts/UI/BattleScreenUI.cs'
change(battle_ui,
       '        public event Action VisibilityChanged;\n',
       '        public event Action VisibilityChanged;\n'
       '        // The authoritative terminal outcome of ONE actual encounter. Consumers listen only\n'
       '        // for the duration of their own movement operation; chained and hero-only fights\n'
       '        // publish individually before their participant objects can be unregistered.\n'
       '        public event Action<ArmyData, ArmyData> EncounterResolved;\n')

battle_combat = 'Assets/Scripts/UI/BattleScreenUI.Combat.cs'
change(battle_combat,
       '                HandleBuildingOnArmyDefeat(hunterArmy, targetArmy);\n'
       '                hexSelectionController?.DeleteArmyIfEmptied(targetArmy);',
       '                HandleBuildingOnArmyDefeat(hunterArmy, targetArmy);\n'
       '                // Hero-only challenges never use OnBattleOutcomeAcknowledged: publish their\n'
       '                // concrete participant pair and resolved roster here, before unregistering.\n'
       '                EncounterResolved?.Invoke(hunterArmy, targetArmy);\n'
       '                hexSelectionController?.DeleteArmyIfEmptied(targetArmy);')
change(battle_combat,
       '            ResetBattlePanel();\n\n            // DeleteArmyIfEmptied',
       '            // An encounter result is attributed to its OWN participants at this terminal\n'
       '            // edge, never inferred from unrelated changes in the global army registry.\n'
       '            EncounterResolved?.Invoke(_attacker, _defender);\n'
       '            ResetBattlePanel();\n\n            // DeleteArmyIfEmptied')

hex_controller = 'Assets/Scripts/Map/HexSelectionController.cs'
change(hex_controller,
       '        public bool IsBattleActive =>\n',
       '        // Shared combat presentation already owned by this controller; expose its\n'
       '        // encounter-completion signal to the existing AI move-operation trace.\n'
       '        public BattleScreenUI BattleScreen => battleScreen;\n'
       '        public bool IsBattleActive =>\n')

ai = 'Assets/Scripts/Ai/AiTurnController.cs'
change(ai,
       '        public bool EnteredStealthThisStep;  // a solo Recce slipped into stealth before this move (V2 step-7 "made progress" signal)\n',
       '        public bool EnteredStealthThisStep;  // a solo Recce slipped into stealth before this move (V2 step-7 "made progress" signal)\n'
       '        private readonly HashSet<int> _destroyedInOwnBattle = new HashSet<int>();\n\n'
       '        // Capture the terminal result of this precise encounter. No ArmyRegistry lookup:\n'
       '        // neither a third-party kill elsewhere nor an unrelated chained battle can\n'
       '        // manufacture proof that a particular opponent died in OUR battle.\n'
       '        public void RecordResolvedEncounter(int movingArmyId, ArmyData attacker, ArmyData defender)\n'
       '        {\n'
       '            if (attacker == null || defender == null) return;\n'
       '            if (attacker.Id == movingArmyId && defender.Members.Count == 0)\n'
       '                _destroyedInOwnBattle.Add(defender.Id);\n'
       '            if (defender.Id == movingArmyId && attacker.Members.Count == 0)\n'
       '                _destroyedInOwnBattle.Add(attacker.Id);\n'
       '        }\n\n'
       '        public bool WasDestroyedInOwnBattle(int enemyArmyId) =>\n'
       '            _destroyedInOwnBattle.Contains(enemyArmyId);\n')
change(ai,
       '            AiDebugLog.Write($"[AI] {player.Nickname}: \\"{army.Name}\\" (movement=',
       '            // Subscribe only after this move passes its legality gate. Unsubscribe on\n'
       '            // normal completion, early coroutine disposal and exception alike. The\n'
       '            // gameplay BattleScreenUI is the only authority for participant outcomes.\n'
       '            BattleScreenUI battleScreenForTrace = trace != null ? ctx.HexSelection?.BattleScreen : null;\n'
       '            Action<ArmyData, ArmyData> observeEncounter = null;\n'
       '            if (battleScreenForTrace != null)\n'
       '            {\n'
       '                observeEncounter = (attacker, defender) =>\n'
       '                    trace.RecordResolvedEncounter(army.Id, attacker, defender);\n'
       '                battleScreenForTrace.EncounterResolved += observeEncounter;\n'
       '            }\n'
       '            try\n'
       '            {\n'
       '            AiDebugLog.Write($"[AI] {player.Nickname}: \\"{army.Name}\\" (movement=')
change(ai,
       '            yield return WaitStep(ctx);\n        }\n\n        // Internal, not private — every Level-1 category planner',
       '            yield return WaitStep(ctx);\n            }\n'
       '            finally\n'
       '            {\n'
       '                if (observeEncounter != null)\n'
       '                    battleScreenForTrace.EncounterResolved -= observeEncounter;\n'
       '            }\n'
       '        }\n\n        // Internal, not private — every Level-1 category planner')

executor = 'Assets/Scripts/Ai/V2/Execution/TaskExecutor.cs'
change(executor,
       '            bool destroyedInOurBattle = trace.BattleOccurred\n'
       '                && !ArmyRegistry.AllOccupiedHexes().SelectMany(ArmyRegistry.AllAt)\n'
       '                    .Any(a => a != null && a.Id == enemyId && a.Owner != null\n'
       '                        && a.Owner != player && !a.Owner.IsNeutral);',
       '            bool destroyedInOurBattle = trace.BattleOccurred\n'
       '                && trace.WasDestroyedInOwnBattle(enemyId);')
change(executor,
       '            // canonical gameplay operation this step just performed. Consulting the physical\n'
       '            // registry is legitimate HERE and only here: our own army actually fought, so whether\n'
       '            // the target survived that battle is a confirmed result of our own action, not hidden\n'
       '            // knowledge. With no battle, this step proves nothing about the enemy\'s existence —\n',
       '            // canonical gameplay operation this step just performed. The encounter-resolved\n'
       '            // trace supplies BOTH the specific participant identity and its terminal fate;\n'
       '            // a global registry sweep cannot prove either one, even if some battle occurred.\n'
       '            // With no battle, this step proves nothing about the enemy\'s existence —\n')

path = Path('Assets/Editor/AiActiveDefenceBattleProofTests.cs')
if path.exists():
    raise RuntimeError('Regression test path already exists')
path.write_text('''#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiActiveDefenceBattleProofTests
    {
        [Test]
        public void TargetDestroyedInOurEncounter_IsConfirmed()
        {
            var mover = new ArmyData();
            var target = new ArmyData();
            var trace = new AiMoveExecutionTrace();
            trace.RecordResolvedEncounter(mover.Id, mover, target);
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.True);
        }

        [Test]
        public void TargetSurvivesOurEncounter_IsNotConfirmed()
        {
            var mover = new ArmyData();
            var target = new ArmyData();
            target.Members.Add(new UnitData());
            var trace = new AiMoveExecutionTrace();
            trace.RecordResolvedEncounter(mover.Id, mover, target);
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.False);
        }

        [Test]
        public void OtherEnemyDiesInOurFight_DoesNotConfirmTarget()
        {
            var mover = new ArmyData();
            var other = new ArmyData();
            var target = new ArmyData();
            var trace = new AiMoveExecutionTrace();
            trace.RecordResolvedEncounter(mover.Id, mover, other);
            Assert.That(trace.WasDestroyedInOwnBattle(other.Id), Is.True);
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.False);
        }

        [Test]
        public void TargetDiesElsewhereWhileWeFightSomeoneElse_IsNotConfirmed()
        {
            var mover = new ArmyData();
            var other = new ArmyData();
            var target = new ArmyData();
            var thirdParty = new ArmyData();
            var trace = new AiMoveExecutionTrace();
            trace.RecordResolvedEncounter(mover.Id, mover, other);
            trace.RecordResolvedEncounter(thirdParty.Id, thirdParty, target);
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.False);
        }

        [Test]
        public void NoOwnEncounter_NoProofEvenWhenTargetIsEmpty()
        {
            var target = new ArmyData();
            var trace = new AiMoveExecutionTrace();
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.False);
        }

        [Test]
        public void ChainedEncounter_MatchesOnlyResolvedTarget()
        {
            var mover = new ArmyData();
            var first = new ArmyData();
            var target = new ArmyData();
            var trace = new AiMoveExecutionTrace();
            trace.RecordResolvedEncounter(mover.Id, mover, first);
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.False);
            trace.RecordResolvedEncounter(target.Id, target, mover);
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.False);
            trace.RecordResolvedEncounter(mover.Id, mover, target);
            Assert.That(trace.WasDestroyedInOwnBattle(target.Id), Is.True);
        }
    }
}
''', encoding='utf-8')
Path(str(path) + '.meta').write_text('fileFormatVersion: 2\nguid: ' + uuid.uuid4().hex + '\n', encoding='utf-8')
print('PATCHED', path, '(6 tests)')

# Guard against the exact regression: the previous registry-based condition must be absent.
assert 'bool destroyedInOurBattle = trace.BattleOccurred\n                && !ArmyRegistry' not in Path(executor).read_text(encoding='utf-8')
print('ALL EXACT-ANCHOR PATCHES APPLIED')
