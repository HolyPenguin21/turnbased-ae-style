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
        // Validation (temporary, perf series): every Nth valid hit is recomputed and compared;
        // a difference is logged as [AI][Perf][DeltaCache] MISMATCH and replaces the entry.
        // 0 disables the audit.
        internal static int AuditEvery = 8;

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

        private sealed class Entry
        {
            internal string CoreRef;      // CORE entry: interned core key instance
            internal string Stamp;        // CORE entry: recipient stamp
            internal string FullRef;      // FULL entry: the verdict key instance it was priced under
            internal object Inventory;    // FULL entry that read the inventory (operator forecast)
            internal EquipmentUpgradeValuation.EquipmentDelta Delta;
        }

        private sealed class PlayerCache
        {
            internal int Turn;
            internal readonly Dictionary<(CardDefinition, object, bool), Entry> Entries =
                new Dictionary<(CardDefinition, object, bool), Entry>();
            internal readonly List<string> RecentCores = new List<string>();
        }

        private static readonly Dictionary<PlayerSetupData, PlayerCache> s_players =
            new Dictionary<PlayerSetupData, PlayerCache>();

        // Read frames of the computations in progress (innermost last). A mark reaches every
        // enclosing frame: an outer delta that priced an inner one read what the inner read.
        private struct Frame { internal bool Full; internal bool Inventory; }
        [System.ThreadStatic] private static List<Frame> s_frames;
        [System.ThreadStatic] private static int s_hits;

        internal static void Clear() => s_players.Clear();

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
            System.Func<string> stamp, System.Func<EquipmentUpgradeValuation.EquipmentDelta> compute,
            System.Func<string> label)
        {
            if (scope == null || scope.Player == null || scope.CoreKey == null || snap == null
                || !ReferenceEquals(scope.Snap, snap) || equipment == null || recipient == null)
                return compute();
            if (!scope.Stamps.TryGetValue(recipient, out string currentStamp))
                scope.Stamps[recipient] = currentStamp = stamp();
            if (currentStamp == null)
                return compute();

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
                    Game.Core.ScopeTiming.Count(entry.FullRef != null ? "DeltaCache.hitFull" : "DeltaCache.hit");
                    if (AuditEvery <= 0 || ++s_hits % AuditEvery != 0)
                        return entry.Delta;
                    Game.Core.ScopeTiming.Count("DeltaCache.audit");
                    EquipmentUpgradeValuation.EquipmentDelta fresh = Compute(compute, out _, out _);
                    if (System.Math.Abs(fresh.Combat - entry.Delta.Combat) <= Tolerance(fresh.Combat)
                        && System.Math.Abs(fresh.Tactical - entry.Delta.Tactical) <= Tolerance(fresh.Tactical))
                        return entry.Delta;
                    Game.Core.ScopeTiming.Count("DeltaCache.mismatch");
                    AiDebugLog.Write($"[AI][Perf][DeltaCache] MISMATCH equip={equipment.authoredKey} "
                        + $"recipient={label()} kind={(entry.FullRef != null ? "full" : "core")} "
                        + $"cached={entry.Delta.Combat:R}/{entry.Delta.Tactical:R} "
                        + $"fresh={fresh.Combat:R}/{fresh.Tactical:R}");
                    entry.Delta = fresh;
                    return fresh;
                }
                Game.Core.ScopeTiming.Count("DeltaCache.stale");
            }
            else
                Game.Core.ScopeTiming.Count("DeltaCache.miss");

            EquipmentUpgradeValuation.EquipmentDelta delta = Compute(compute, out bool full, out bool inv);
            cache.Entries[key] = full
                ? new Entry { FullRef = scope.FullKey, Inventory = inv ? inventory : null, Delta = delta }
                : new Entry { CoreRef = scope.CoreKey, Stamp = currentStamp, Delta = delta };
            if (full) Game.Core.ScopeTiming.Count("DeltaCache.storedFull");
            return delta;
        }

        private static float Tolerance(float value) => 1e-4f + 1e-4f * System.Math.Abs(value);

        private static EquipmentUpgradeValuation.EquipmentDelta Compute(
            System.Func<EquipmentUpgradeValuation.EquipmentDelta> compute, out bool full, out bool inventory)
        {
            s_frames ??= new List<Frame>();
            s_frames.Add(default);
            int at = s_frames.Count - 1;
            EquipmentUpgradeValuation.EquipmentDelta delta;
            try { delta = compute(); }
            finally
            {
                Frame frame = s_frames[at];
                s_frames.RemoveAt(at);
                full = frame.Full;
                inventory = frame.Inventory;
            }
            // What this computation read, every enclosing computation read too.
            if (full) MarkFull(inventory);
            return delta;
        }
    }
}
