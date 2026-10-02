#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Players;
using Game.Turns;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class EliminatedPlayerTests
    {
        [Test]
        public void EliminatedPlayersAreOutOfTheRound()
        {
            var a = new PlayerSetupData { Nickname = "A" };
            var b = new PlayerSetupData { Nickname = "B", IsEliminated = true };
            var c = new PlayerSetupData { Nickname = "C" };
            List<PlayerSetupData> active = InitiativeRules.ActivePlayers(new[] { a, b, null, c });
            Assert.That(active, Is.EqualTo(new[] { a, c }));
            Assert.That(InitiativeRules.ActivePlayers(null), Is.Empty);
        }

        [Test]
        public void SurvivorsKeepTheirRankApWhenSomeoneIsOut()
        {
            // Rank is the index in the ACTIVE order: with the defeated player gone the last
            // survivor is rank 1 (10 AP), not rank 2.
            var a = new PlayerSetupData(); var gone = new PlayerSetupData { IsEliminated = true };
            var c = new PlayerSetupData();
            List<PlayerSetupData> active = InitiativeRules.ActivePlayers(new[] { a, gone, c });
            Assert.That(InitiativeRules.ApForRank(active.IndexOf(c)), Is.EqualTo(InitiativeRules.ApForRank(1)));
        }
    }
}
#endif
