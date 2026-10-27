using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Turns
{
    [Serializable]
    public sealed class PlayerTurnCount
    {
        public string PlayerId;
        public long CompletedTurns;
    }
    [Serializable]
    public sealed class PlayerTurnMessages
    {
        public string PlayerId;
        public string[] Messages;
    }

    // Settled human-turn position. Resuming this record must never call BeginPlayerTurn:
    // that method refills movement, AA and activation and may eliminate a player.
    [Serializable]
    public sealed class GameTurnState
    {
        public int Round;
        public string[] PlayerOrder;
        public int CurrentPlayerIndex;
        public PlayerTurnCount[] Completed;
        public string[] GreetingsShown;
        public PlayerTurnMessages[] PendingMessages;

        public void Validate(IEnumerable<string> playerIds, string humanPlayerId)
        {
            var ids = playerIds?.ToArray() ?? throw new ArgumentNullException(nameof(playerIds));
            if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != ids.Length
                || string.IsNullOrWhiteSpace(humanPlayerId) || !ids.Contains(humanPlayerId))
                throw new ArgumentException("Invalid match player identities.");
            var known = new HashSet<string>(ids, StringComparer.Ordinal);
            if (Round < 1 || PlayerOrder == null || PlayerOrder.Length == 0
                || PlayerOrder.Distinct().Count() != PlayerOrder.Length
                || PlayerOrder.Any(id => id == null || !known.Contains(id))
                || CurrentPlayerIndex < 0 || CurrentPlayerIndex >= PlayerOrder.Length
                || PlayerOrder[CurrentPlayerIndex] != humanPlayerId)
                throw new ArgumentException("Saved turn is not a settled human turn.");
            if (Completed == null || Completed.Length != ids.Length
                || Completed.Any(c => c == null || c.PlayerId == null || !known.Contains(c.PlayerId) || c.CompletedTurns < 0)
                || Completed.Select(c => c.PlayerId).Distinct().Count() != ids.Length)
                throw new ArgumentException("Invalid completed-turn history.");
            if (GreetingsShown == null || GreetingsShown.Any(id => id == null || !known.Contains(id))
                || GreetingsShown.Distinct().Count() != GreetingsShown.Length)
                throw new ArgumentException("Invalid greeting history.");
            if (PendingMessages == null || PendingMessages.Any(m => m == null || m.PlayerId == null
                    || !known.Contains(m.PlayerId) || m.Messages == null || m.Messages.Any(x => x == null))
                || PendingMessages.Select(m => m.PlayerId).Distinct().Count() != PendingMessages.Length)
                throw new ArgumentException("Invalid pending turn reports.");
        }
    }
}
