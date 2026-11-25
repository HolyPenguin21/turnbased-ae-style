#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2.Initiative;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-25 (owner, match Grimm/Brynn/Halden) — Halden moved last in R33 and first in R34 and
    // took Brynn's Citadel before Brynn moved again. Instead of a turn-order timing model the AI
    // gets the will to win the turn order while a known enemy reaches an own stronghold soon.
    public sealed class AiInitiativeStrongholdThreatTests
    {
        [SetUp]
        public void SetUp() { ArmyRegistry.Clear(); AiMapMemory.Clear(); }

        [TearDown]
        public void TearDown() { ArmyRegistry.Clear(); AiMapMemory.Clear(); }

        private static PreTurnCapacityAnalysis Analysis(float turnOrderPressure)
        {
            var a = new PreTurnCapacityAnalysis
            {
                CurrentApPressure = 0.4f, HistoricalApPressure = 0.4f, ApPressure = 0.4f,
                TurnOrderPressure = turnOrderPressure,
            };
            for (int i = 0; i < 4; i++) { a.Available[i] = 8; a.IncomePerTurn[i] = 2; a.DeckDemand[i] = 10; }
            return a;
        }

        [Test]
        public void AStrongholdThreat_NeverBuysFewerTokens_AndMakesTheOrderWorthMore()
        {
            int[] opponents = { 6, 6 };
            InitiativePlan calm = InitiativePlanner.Plan(Analysis(0.24f), opponents);
            InitiativePlan threatened = InitiativePlanner.Plan(Analysis(1f), opponents);
            Assert.That(threatened.TokensToBuy, Is.GreaterThanOrEqualTo(calm.TokensToBuy));
            Assert.That(threatened.TokensToBuy, Is.GreaterThan(0), "a threatened Citadel is worth an earlier turn");
        }

        [Test]
        public void StrongholdThreatPressure_ReadsTheGarrisonFloorThreat()
        {
            var us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var them = new PlayerSetupData { Nickname = "Them", ColorIndex = 4 };
            var garrison = new ArmyData { Owner = us, Hex = new HexCoord(0, 0), IsGarrison = true };
            garrison.Members.Add(new UnitData { Owner = us, Attack = 2, Defense = 2, HitPointsMax = 6, HitPointsCurrent = 6 });
            ArmyRegistry.Register(garrison);
            Assert.That(PreTurnCapacityAnalysis.StrongholdThreatPressureOf(us), Is.Zero, "no known enemy");

            var bodies = new[] { new WorthIt.DefenderProfile(3f, false, attack: 6f, hitPoints: 6f) };
            AiMapMemory.RememberForTest(us, new AiMapMemory.KnownEnemySighting(new HexCoord(-3, 0), them,
                "raider", 1, 3f, 6f, bodies, seenTurn: 10, armyId: 31, maxMovement: 3));
            Assert.That(PreTurnCapacityAnalysis.StrongholdThreatPressureOf(us), Is.GreaterThan(0f));
        }
    }
}
#endif
