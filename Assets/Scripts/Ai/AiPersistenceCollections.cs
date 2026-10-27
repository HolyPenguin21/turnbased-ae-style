using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Game.Players;
using Game.Combat;
using Game.Cards;
using Game.Map;

namespace Game.Ai
{
    // Collection mechanics only. Each owner explicitly names its fields and clones its values.
    // IDs come from the match writer, never from a nickname or a newly enumerated player order.
    internal static class AiPersistenceCollections
    {
        internal static IReadOnlyDictionary<K, V> Freeze<K, V>(IEnumerable<KeyValuePair<K, V>> values) =>
            new ReadOnlyDictionary<K, V>(values.ToDictionary(x => x.Key, x => x.Value));

        internal static IReadOnlyDictionary<string, TSave> Capture<TLive, TSave>(
            Dictionary<PlayerSetupData, TLive> source, Func<PlayerSetupData, string> playerId,
            Func<TLive, TSave> capture)
        {
            if (playerId == null) throw new ArgumentNullException(nameof(playerId));
            return Freeze(source.Select(x => new KeyValuePair<string, TSave>(CapturePlayerId(x.Key, playerId), capture(x.Value))));
        }

        // Resolve and validate everything before replacing the owner's dictionary.
        internal static void Restore<TSave, TLive>(Dictionary<PlayerSetupData, TLive> destination,
            IReadOnlyDictionary<string, TSave> source, Func<string, PlayerSetupData> player,
            Func<TSave, TLive> restore)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (player == null) throw new ArgumentNullException(nameof(player));
            var rebound = new Dictionary<PlayerSetupData, TLive>();
            foreach (var row in source)
            {
                PlayerSetupData owner = ResolvePlayer(row.Key, player);
                rebound.Add(owner, restore(row.Value));
            }
            destination.Clear();
            foreach (var row in rebound) destination.Add(row.Key, row.Value);
        }

        internal static string CapturePlayerId(PlayerSetupData player, Func<PlayerSetupData, string> playerId)
        {
            if (playerId == null) throw new ArgumentNullException(nameof(playerId));
            if (player == null || string.IsNullOrWhiteSpace(player.PlayerId))
                throw new ArgumentException("AI persistence requires a stable match-local player ID");
            string id = playerId(player);
            if (!string.Equals(id, player.PlayerId, StringComparison.Ordinal))
                throw new ArgumentException("AI persistence player ID must match PlayerSetupData.PlayerId");
            return id;
        }

        internal static PlayerSetupData ResolvePlayer(string id, Func<string, PlayerSetupData> player)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Missing saved AI player ID");
            PlayerSetupData resolved = player(id);
            if (resolved == null || !string.Equals(resolved.PlayerId, id, StringComparison.Ordinal))
                throw new ArgumentException("Unresolved or mismatched AI player ID: " + id);
            return resolved;
        }

        internal static CardData ResolveCard(int id, Func<int, CardData> card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            CardData resolved = card(id);
            if (resolved == null || resolved.RuntimeId != id)
                throw new ArgumentException("Unresolved or mismatched AI card ID: " + id);
            return resolved;
        }

        internal static ArmyData ResolveArmy(int id, Func<int, ArmyData> army)
        {
            if (army == null) throw new ArgumentNullException(nameof(army));
            ArmyData resolved = army(id);
            if (resolved == null || resolved.Id != id)
                throw new ArgumentException("Unresolved or mismatched AI army ID: " + id);
            return resolved;
        }

        // SideCommander predates immutable snapshots and may retain a caller-owned support list.
        internal static WorthIt.SideCommander FreezeCommander(WorthIt.SideCommander commander) =>
            !commander.Present ? default : new WorthIt.SideCommander(commander.Initiative, commander.Fate,
                commander.SupportHeroes == null ? null : FreezeList(commander.SupportHeroes));

        internal static IReadOnlyList<T> FreezeList<T>(IEnumerable<T> values) =>
            Array.AsReadOnly(values.ToArray());
    }
}
