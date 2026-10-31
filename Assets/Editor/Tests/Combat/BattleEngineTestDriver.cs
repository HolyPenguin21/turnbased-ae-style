#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Units;

namespace Game.EditorTests
{
    // Legacy mechanics fixtures intentionally hit in artificial orders. Construct their
    // private fixture state here; production command protocol has separate real-command tests.
    internal static class BattleEngineTestDriver
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static void Queue(BattleState state, List<UnitData> units) => typeof(BattleState).GetProperty("MutableTurnOrder", Hidden).SetValue(state, units);
        internal static void Index(BattleState state, int index) => typeof(BattleState).GetProperty("TurnIndex", Hidden).SetValue(state, index);
        internal static void Retreat(BattleState state, Game.Map.ArmyData army) => typeof(BattleState).GetProperty("RetreatingArmy", Hidden).SetValue(state, army);
        internal static void NextRound(BattleEngine engine)
        {
            typeof(BattleState).GetProperty("Round", Hidden).SetValue(engine.State, engine.State.Round + 1);
            typeof(BattleEngine).GetProperty("Phase", Hidden).SetValue(engine, BattleCommandPhase.RoundReady);
        }
        internal static BattleAttackApplication ApplyHit(BattleEngine engine, UnitData actor, UnitData target, int damage) =>
            (BattleAttackApplication)typeof(BattleEngine).GetMethod("ApplyGroundHit", Hidden).Invoke(engine, new object[] { actor, target, damage });
        internal static BattleAttackApplication ResolveHit(BattleEngine engine, UnitData actor, UnitData target,
            BattleChallengeRollResult roll, UnitData attackerHero = null, UnitData defenderHero = null, bool isCounterattack = false)
        {
            var request = new BattleAttackRequest();
            typeof(BattleAttackRequest).GetField("Engine", Hidden).SetValue(request, engine);
            typeof(BattleAttackRequest).GetProperty("Attacker", Hidden).SetValue(request, actor);
            typeof(BattleAttackRequest).GetProperty("Target", Hidden).SetValue(request, target);
            typeof(BattleAttackRequest).GetProperty("IsCounterattack", Hidden).SetValue(request, isCounterattack);
            typeof(BattleEngine).GetProperty("Phase", Hidden).SetValue(engine, BattleCommandPhase.AwaitingAttack);
            typeof(BattleEngine).GetField("_attackRequest", Hidden).SetValue(engine, request);
            typeof(BattleEngine).GetField("_activeAttackPlan", Hidden).SetValue(engine, null);
            typeof(BattleEngine).GetField("_activeExplosion", Hidden).SetValue(engine, null);
            return engine.ResolveAndApplyGroundAttack(actor, target, roll, attackerHero, defenderHero, isCounterattack);
        }
    }
}
#endif
