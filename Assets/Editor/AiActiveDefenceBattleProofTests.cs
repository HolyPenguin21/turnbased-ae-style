#if UNITY_INCLUDE_TESTS
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
