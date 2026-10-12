#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using Game.Players;
using Game.Turns;
using Game.UI;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class TurnOrderTimingTests
    {
        [Test]
        public void DiceDurations_UseFullRollAndShortFateReroll()
        {
            Assert.That(DiceSlotUI.FullRollDuration, Is.EqualTo(1f));
            Assert.That(DiceSlotUI.FateRerollDuration, Is.EqualTo(0.5f));
        }

        [Test]
        public void ResolveRecordedRolls_RecordsEveryTieBreakRound()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var b = new PlayerSetupData { Nickname = "B" };
            var c = new PlayerSetupData { Nickname = "C" };
            var scripted = new Queue<Dictionary<PlayerSetupData, DiceRollResult>>(new[]
            {
                new Dictionary<PlayerSetupData, DiceRollResult>
                {
                    [a] = Roll(true, false, false),
                    [b] = Roll(true, false, false),
                    [c] = Roll(false, false, false),
                },
                new Dictionary<PlayerSetupData, DiceRollResult>
                {
                    [a] = Roll(true, false, false),
                    [b] = Roll(true, false, false),
                },
                new Dictionary<PlayerSetupData, DiceRollResult>
                {
                    [a] = Roll(true, true, false),
                    [b] = Roll(false, false, false),
                },
            });

            MethodInfo method = typeof(TurnOrderResolver).GetMethod("ResolveRecordedRolls",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var resolution = (TurnOrderResolution)method.Invoke(null,
                new object[] { new List<PlayerSetupData> { a, b, c }, scripted });

            Assert.That(resolution.Order, Is.EqualTo(new[] { a, b, c }));
            Assert.That(resolution.Rounds, Has.Count.EqualTo(3));
            Assert.That(resolution.Rounds[0].Rolls.Keys, Is.EquivalentTo(new[] { a, b, c }));
            Assert.That(resolution.Rounds[1].Rolls.Keys, Is.EquivalentTo(new[] { a, b }));
            Assert.That(resolution.Rounds[2].Rolls.Keys, Is.EquivalentTo(new[] { a, b }));
            Assert.That(resolution.FinalRolls[a].Score, Is.EqualTo(2));
            Assert.That(resolution.FinalRolls[b].Score, Is.Zero);
            Assert.That(resolution.FinalRolls[c].Score, Is.Zero);
        }

        private static DiceRollResult Roll(params bool[] dice) => new DiceRollResult(dice);
    }
}
#endif
