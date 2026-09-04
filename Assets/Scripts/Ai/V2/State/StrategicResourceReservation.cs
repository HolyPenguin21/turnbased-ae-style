using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC RESOURCE RESERVATION  (Strategy V2 — AI-MGR-02, spec §6/§7/§8)
    // ===========================================================================================
    //  An EXPLICIT, owner + reason tagged hold on the shared resource pool. It replaces every
    //  hidden "Phase B just returns early and N AP are silently preserved" path. After this change
    //  a resource is in exactly one of three states:
    //    · really spent,
    //    · covered by an ACTIVE reservation here (owner + reason + amount + expiration known), or
    //    · free for end-of-turn tempo arbitration.
    //
    //      SpendableResource(r) = TotalResource(r) - Σ ActiveReservations(r)
    //
    //  Works for every strategic resource, not only AP (spec §6). Reservations are IDEMPOTENT by
    //  (Owner, Reason, Resource): a repeated Phase-B / reaction pass upserts the same row instead
    //  of stacking duplicates (spec §8).
    //
    //  Lifecycle rule (spec §7): if the pass that owns a reservation is Suppressed / NoAction /
    //  Invalidated / Skipped, the reservation is released IMMEDIATELY (regardless of its nominal
    //  ExpirationStage) and end-of-turn tempo spending runs again the same turn. Nothing may
    //  survive turn end (spec §8 — there is no legitimate cross-turn reservation in this task).
    // ===========================================================================================

    public enum StrategicReservedResource { ActionPoints, Human, Energy, Materials, Tech }

    // Why a resource is being held back. Extension point — StrategicReactionPass is the only
    // current owner. A future late AP/Energy-costing V2 stage adds its reason here instead of
    // reviving a hidden fixed floor (see the retired surplus*Reserve note in AiConfigV2).
    public enum StrategicReservationReason { StrategicReactionPass }

    // The stage by which the reservation is guaranteed gone in the normal (non-aborted) flow.
    public enum StrategicReservationExpiry { EndOfPhaseB, EndOfReaction, EndOfTurn }

    public sealed class StrategicResourceReservation
    {
        public string Owner;
        public StrategicReservationReason Reason;
        public StrategicReservedResource Resource;
        public float Amount;
        public StrategicReservationExpiry ExpirationStage;

        public override string ToString() =>
            $"{Owner}:{Reason} {Amount.ToString("0.##", CultureInfo.InvariantCulture)} {Resource} (exp {ExpirationStage})";
    }

    // Per-player, turn-scoped. Keyed by turn the same way StrategicInterruptRegistry is, so a
    // stale entry from a previous turn reads as empty rather than leaking.
    internal static class StrategicResourceReservationLedger
    {
        private sealed class Entry
        {
            public int Turn;
            public readonly List<StrategicResourceReservation> Reservations =
                new List<StrategicResourceReservation>();
        }

        private static readonly Dictionary<PlayerSetupData, Entry> ByPlayer =
            new Dictionary<PlayerSetupData, Entry>();

        public static void BeginTurn(PlayerSetupData player, int turn)
        {
            if (player == null) return;
            ByPlayer[player] = new Entry { Turn = turn };
        }

        // Idempotent by (Owner, Reason, Resource): a repeat upserts the amount/expiry, never a
        // second row (spec §8 — "duplicate reservation for same owner/reason" is forbidden).
        // Amount <= 0 removes the row entirely.
        public static void Upsert(PlayerSetupData player, int turn, StrategicResourceReservation r)
        {
            if (player == null || r == null) return;
            Entry e = GetOrReset(player, turn);
            StrategicResourceReservation existing = e.Reservations.FirstOrDefault(
                x => x.Owner == r.Owner && x.Reason == r.Reason && x.Resource == r.Resource);
            if (r.Amount <= 0f)
            {
                if (existing != null)
                {
                    e.Reservations.Remove(existing);
                    AiDebugLog.Write($"[AI][V2] reservation 0 -> drop {existing}; active [{DebugLine(player, turn)}]");
                }
                return;
            }
            if (existing != null)
            {
                if (Mathf.Approximately(existing.Amount, r.Amount) && existing.ExpirationStage == r.ExpirationStage)
                    return;
                existing.Amount = r.Amount;
                existing.ExpirationStage = r.ExpirationStage;
                AiDebugLog.Write($"[AI][V2] reservation ~ {existing}; active [{DebugLine(player, turn)}]");
                return;
            }
            e.Reservations.Add(r);
            AiDebugLog.Write($"[AI][V2] reservation + {r}; active [{DebugLine(player, turn)}]");
        }

        public static float Active(PlayerSetupData player, int turn, StrategicReservedResource res)
            => Active(player, turn, res, (string)null);

        // AI-MGR-02 round 7 (P1) — `ignoreOwner` excludes a caller's OWN reservation from the sum by
        // its EXACT Owner key (not by the shared Reason), so a pass can re-check "would this still be
        // affordable if MY hold weren't there" without tearing its reservation down (which would let
        // another action grab the freed resource), AND two reaction owners that share
        // Reason=StrategicReactionPass cannot shadow each other's revalidation. Used by the reaction
        // feasibility probe / re-probe (StrategicReactionPass §P1).
        public static float Active(PlayerSetupData player, int turn, StrategicReservedResource res,
            string ignoreOwner)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return 0f;
            float sum = 0f;
            foreach (StrategicResourceReservation r in e.Reservations)
                if (r.Resource == res && (ignoreOwner == null || r.Owner != ignoreOwner))
                    sum += Mathf.Max(0f, r.Amount);
            return sum;
        }

        // SpendableResource = TotalResource - Σ ActiveReservations(resource). Generic over every
        // strategic resource (spec §6).
        public static float Spendable(PlayerSetupData player, int turn, StrategicReservedResource res, float total)
            => Mathf.Max(0f, total - Active(player, turn, res));

        // As Spendable, but excluding the caller's own reservation by its EXACT Owner key (P1).
        // See Active(…, ignoreOwner).
        public static float SpendableExcludingOwner(PlayerSetupData player, int turn, StrategicReservedResource res,
            float total, string ignoreOwner)
            => Mathf.Max(0f, total - Active(player, turn, res, ignoreOwner));

        public static float SpendableAp(PlayerSetupData player, int turn, float totalAp) =>
            Spendable(player, turn, StrategicReservedResource.ActionPoints, totalAp);

        public static StrategicReservedResource Map(ResourceType t) => t switch
        {
            ResourceType.Human => StrategicReservedResource.Human,
            ResourceType.Energy => StrategicReservedResource.Energy,
            ResourceType.Materials => StrategicReservedResource.Materials,
            _ => StrategicReservedResource.Tech,
        };

        // Immediate release for a Suppressed / NoAction / Invalidated / Skipped owning pass.
        public static bool ReleaseByReason(PlayerSetupData player, int turn, StrategicReservationReason reason)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return false;
            int removed = e.Reservations.RemoveAll(r => r.Reason == reason);
            if (removed > 0)
                AiDebugLog.Write($"[AI][V2] reservation - released {removed} ({reason}); "
                    + $"active [{DebugLine(player, turn)}]");
            return removed > 0;
        }

        // Normal end-of-stage expiry.
        public static bool ExpireStage(PlayerSetupData player, int turn, StrategicReservationExpiry stage)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return false;
            int removed = e.Reservations.RemoveAll(r => r.ExpirationStage == stage);
            if (removed > 0)
                AiDebugLog.Write($"[AI][V2] reservation - expired {removed} at {stage}; "
                    + $"active [{DebugLine(player, turn)}]");
            return removed > 0;
        }

        public static bool HasAny(PlayerSetupData player, int turn) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e) && e.Turn == turn
            && e.Reservations.Count > 0;

        // spec §8 — nothing may survive turn end. Called at the very end of the AI turn: logs and
        // force-clears anything still standing (a leak — an owner that never released).
        public static void AssertClearAtTurnEnd(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return;
            if (e.Reservations.Count > 0)
            {
                AiDebugLog.Write($"[AI][V2][ERROR] reservation leak at turn end — [{DebugLine(player, turn)}] "
                    + "not released by its owner; force-clearing");
                e.Reservations.Clear();
            }
        }

        public static string DebugLine(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn
                || e.Reservations.Count == 0)
                return "none";
            return string.Join(", ", e.Reservations.Select(r => r.ToString()));
        }

        private static Entry GetOrReset(PlayerSetupData player, int turn)
        {
            if (!ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
            {
                e = new Entry { Turn = turn };
                ByPlayer[player] = e;
            }
            return e;
        }
    }
}
