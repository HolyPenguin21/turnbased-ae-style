using System.Collections.Generic;
using Game.Cards;
using Game.Players;

namespace Game.Ai.V2
{
    // 2026-11-15 (late-game perf, owner option A) — per-recipient reuse of the expensive
    // equipment efficiency core (EquipmentUpgradeValuation.ScoreEquipmentDelta) across the
    // Development verdict stores of one turn.
    //
    // The verdict store (DevelopmentOpportunityEvaluator.RecipientEvaluationMemo) is keyed by ONE
    // world string, so any action dropped every (equipment, recipient) delta: 12-22 times a turn,
    // ~2500 deltas each. Here an entry is valid while what ITS computation read is unchanged:
    //   * CORE entry — the shared core key (DevelopmentAdmission.RecipientDeltaCoreKey: knowledge,
    //     own rosters, deck and hand, map/routes, air visibility, weather) and the recipient's own
    //     stamp (DevelopmentAdmission.RecipientDeltaStamp: its army, members, route state, purpose);
    //   * FULL entry — the computation read something outside those (MarkFull: the effect
    //     registry, the hero operator forecast, a Scout route estimate) and is valid only under
    //     the same complete verdict key, exactly as the verdict store itself.
    // Which kind an entry is comes from the reads of its own computation (read frames), never
    // from a guess about the equipment. The cheap wrappers that read volatile state (recipient
    // priority, slot weight, purpose label, pending-hand coverage) are applied live by callers.
    // Only consulted inside a keyed verdict-store scope on that scope's snapshot; elsewhere every
    // call computes fresh, as before.
    internal static class EquipmentDeltaCache
    {
        // The scope facts a keyed verdict store carries (set by RecipientEvaluationMemo.StoreFor).
        internal sealed class ScopeKeys
        {
            internal PlayerSetupData Player;
            internal WorldSnapshot Snap;
            internal string FullKey;
            internal string CoreKey;
            // Recipient stamps of this store: everything a stamp reads is part of FullKey.
            internal readonly Dictionary<object, string> Stamps = new Dictionary<object, string>();
        }

        internal sealed class Entry
        {
            internal string CoreRef;      // CORE entry: interned core key instance
            internal string Stamp;        // CORE entry: recipient stamp
            internal string FullRef;      // FULL entry: the verdict key instance it was priced under
            internal object Inventory;    // FULL entry that read the inventory (operator forecast)
            internal EquipmentUpgradeValuation.EquipmentDelta Delta;
        }

        // The CORE-valid part of an entry's computation (CorePartSlot), kept even when the entry
        // itself is FULL.
        internal sealed class Part
        {
            internal string CoreRef;
            internal string Stamp;
            internal object Value;
        }

        // 2026-11-28 (match Tessek/Halden perf: Equip.EffDelta 25 s of 31 s) — a FULL entry
        // dies on every verdict-key change although usually only its registry tail read
        // non-core state: the efficiency utility before it reads the same facts a CORE entry
        // reads. The computation in progress offers that part through this slot: reused when
        // the core key and the recipient stamp match and nothing marked FULL before it, and
        // stored only when nothing marked FULL up to its end. Passed explicitly down the one
        // scorer chain, never found through ambient state.
        internal sealed class CorePartSlot
        {
            internal PlayerCache Cache;
            internal (CardDefinition, object, bool) Key;
            internal string CoreRef;
            internal string Stamp;
            internal int Frame;
        }

        internal sealed class PlayerCache
        {
            internal int Turn;
            internal readonly Dictionary<(CardDefinition, object, bool), Entry> Entries =
                new Dictionary<(CardDefinition, object, bool), Entry>();
            internal readonly Dictionary<(CardDefinition, object, bool), Part> Parts =
                new Dictionary<(CardDefinition, object, bool), Part>();
            internal readonly List<string> RecentCores = new List<string>();
        }

        private static readonly Dictionary<PlayerSetupData, PlayerCache> s_players =
            new Dictionary<PlayerSetupData, PlayerCache>();

        // Read frames of the computations in progress (innermost last). A mark reaches every
        // enclosing frame: an outer delta that priced an inner one read what the inner read.
        private struct Frame { internal bool Full; internal bool Inventory; }
        [System.ThreadStatic] private static List<Frame> s_frames;

        // A new match (MatchStateInventory, via ClearRecipientEvaluationCache) or a test.
        internal static void Clear()
        {
            s_players.Clear();
            s_frames?.Clear();
        }

        // The computation read something outside the core key and the recipient stamp.
        // `inventory`: it also read the CapabilityInventory instance (operator forecast).
        internal static void MarkFull(bool inventory = false)
        {
            if (s_frames == null) return;
            for (int i = 0; i < s_frames.Count; i++)
                s_frames[i] = new Frame { Full = true, Inventory = s_frames[i].Inventory || inventory };
        }

        private static PlayerCache For(PlayerSetupData player, int turn)
        {
            if (!s_players.TryGetValue(player, out PlayerCache cache) || cache.Turn != turn)
                s_players[player] = cache = new PlayerCache { Turn = turn };
            return cache;
        }

        // One instance per distinct core text, so a valid CORE entry compares by reference.
        internal static string InternCore(PlayerSetupData player, int turn, string core)
        {
            if (player == null || core == null) return core;
            PlayerCache cache = For(player, turn);
            foreach (string known in cache.RecentCores)
                if (string.Equals(known, core, System.StringComparison.Ordinal))
                    return known;
            cache.RecentCores.Add(core);
            if (cache.RecentCores.Count > 8) cache.RecentCores.RemoveAt(0);
            return core;
        }

        internal static EquipmentUpgradeValuation.EquipmentDelta GetOrCompute(ScopeKeys scope,
            WorldSnapshot snap, object inventory, CardDefinition equipment, object recipient, bool forecastHero,
            System.Func<string> stamp,
            System.Func<CorePartSlot, EquipmentUpgradeValuation.EquipmentDelta> compute)
        {
            if (scope == null || scope.Player == null || scope.CoreKey == null || snap == null
                || !ReferenceEquals(scope.Snap, snap) || equipment == null || recipient == null)
                return compute(null);
            if (!scope.Stamps.TryGetValue(recipient, out string currentStamp))
                scope.Stamps[recipient] = currentStamp = stamp();
            if (currentStamp == null)
                return compute(null);

            PlayerCache cache = For(scope.Player, snap.TurnNumber);
            var key = (equipment, recipient, forecastHero);
            if (cache.Entries.TryGetValue(key, out Entry entry))
            {
                bool valid = entry.FullRef != null
                    ? ReferenceEquals(entry.FullRef, scope.FullKey)
                        && (entry.Inventory == null || ReferenceEquals(entry.Inventory, inventory))
                    : ReferenceEquals(entry.CoreRef, scope.CoreKey)
                        && string.Equals(entry.Stamp, currentStamp, System.StringComparison.Ordinal);
                if (valid)
                {
                    if (entry.FullRef != null) MarkFull(entry.Inventory != null);
                    return entry.Delta;
                }
            }

            var slot = new CorePartSlot { Cache = cache, Key = key, CoreRef = scope.CoreKey, Stamp = currentStamp };
            EquipmentUpgradeValuation.EquipmentDelta delta = Compute(compute, slot, out bool full, out bool inv);
            cache.Entries[key] = full
                ? new Entry { FullRef = scope.FullKey, Inventory = inv ? inventory : null, Delta = delta }
                : new Entry { CoreRef = scope.CoreKey, Stamp = currentStamp, Delta = delta };
            return delta;
        }

        private static EquipmentUpgradeValuation.EquipmentDelta Compute(
            System.Func<CorePartSlot, EquipmentUpgradeValuation.EquipmentDelta> compute, CorePartSlot slot,
            out bool full, out bool inventory)
        {
            s_frames ??= new List<Frame>();
            s_frames.Add(default);
            int at = s_frames.Count - 1;
            slot.Frame = at;
            EquipmentUpgradeValuation.EquipmentDelta delta;
            try { delta = compute(slot); }
            finally
            {
                Frame frame = s_frames[at];
                s_frames.RemoveAt(at);
                full = frame.Full;
                inventory = frame.Inventory;
            }
            // What this computation read, every enclosing computation read too.
            if (full) MarkFull(inventory);
            slot.Frame = -1;
            return delta;
        }

        // The slot's computation has read nothing outside the core key and the stamp so far.
        private static bool CoreSoFar(CorePartSlot slot) =>
            slot?.Cache != null && s_frames != null && slot.Frame >= 0 && slot.Frame < s_frames.Count
            && !s_frames[slot.Frame].Full;

        internal static bool TryGetCorePart<T>(CorePartSlot slot, out T value)
        {
            value = default;
            if (!CoreSoFar(slot) || !slot.Cache.Parts.TryGetValue(slot.Key, out Part part)
                || !ReferenceEquals(part.CoreRef, slot.CoreRef)
                || !string.Equals(part.Stamp, slot.Stamp, System.StringComparison.Ordinal)
                || !(part.Value is T typed))
                return false;
            value = typed;
            return true;
        }

        internal static void StoreCorePart(CorePartSlot slot, object value)
        {
            if (CoreSoFar(slot))
                slot.Cache.Parts[slot.Key] = new Part { CoreRef = slot.CoreRef, Stamp = slot.Stamp, Value = value };
        }
    }
}
