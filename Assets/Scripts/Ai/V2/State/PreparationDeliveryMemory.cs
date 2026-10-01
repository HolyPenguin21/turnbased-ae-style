using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // 2026-10-01 — Phase A's own answer to "can a held card land in this preparation host": when
    // Phase A, on the host's pinned FieldCombatPower demand, finds no feasible chain while a card
    // is in hand, that card is no WAIT witness for the host this turn and the next
    // (AggressionDemandEvaluator.PreparationHostCardSource). One source of truth instead of a
    // second, looser rule in the witness (Cassia T21-T23: WAIT on Dust Runner that Phase A never
    // played). Turn-scoped player memory; nothing survives two turns.
    internal static class PreparationDeliveryMemory
    {
        private sealed class Entry
        {
            public int Turn;
            public readonly HashSet<string> CardKeys = new HashSet<string>();
        }

        private static readonly Dictionary<(PlayerSetupData, int), Entry> ByHost =
            new Dictionary<(PlayerSetupData, int), Entry>();

        internal static void MarkNoChain(PlayerSetupData player, int hostArmyId, int turn,
            IEnumerable<string> heldCardKeys)
        {
            if (player == null)
                return;
            if (!ByHost.TryGetValue((player, hostArmyId), out Entry e) || e.Turn != turn)
                ByHost[(player, hostArmyId)] = e = new Entry { Turn = turn };
            foreach (string k in heldCardKeys ?? System.Array.Empty<string>())
                if (k != null)
                    e.CardKeys.Add(k);
        }

        internal static bool NoChainRecently(PlayerSetupData player, int hostArmyId, int turn,
            string cardKey) =>
            player != null && cardKey != null
            && ByHost.TryGetValue((player, hostArmyId), out Entry e)
            && turn - e.Turn <= 1 && turn >= e.Turn && e.CardKeys.Contains(cardKey);

        // Admission-fingerprint digest of the verdicts still in force for `turn`.
        internal static string Digest(PlayerSetupData player, int turn)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<(PlayerSetupData, int), Entry> kv in ByHost)
                if (kv.Key.Item1 == player && turn >= kv.Value.Turn && turn - kv.Value.Turn <= 1)
                {
                    var keys = new List<string>(kv.Value.CardKeys);
                    keys.Sort(System.StringComparer.Ordinal);
                    parts.Add($"{kv.Key.Item2}@{kv.Value.Turn}:{string.Join(",", keys)}");
                }
            parts.Sort(System.StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        internal static void Clear() => ByHost.Clear();
    }
}
