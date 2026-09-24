using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC RESOURCE RESERVATION  (spec §6/§7/§8)
    // ===========================================================================================
    //  An EXPLICIT, owner + reason + resource tagged hold on the shared physical pool. DesireAxis
    //  never keys a row and therefore never creates an Economy/Recon/etc. resource wallet. There is no
    //  hidden "Phase B just returns early and N AP are silently preserved" path: a resource is in
    //  exactly one of three states:
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
    //  survive turn end (spec §8 — there is no legitimate cross-turn reservation).
    // ===========================================================================================

    public enum StrategicReservedResource { ActionPoints, Human, Energy, Materials, Tech }

    // Why a resource is being held back. Economy distinguishes a replaceable deferred choice from
    // a provisioned completion, so changing sites cannot stack alternatives or erase committed AP.
    public enum StrategicReservationReason
    {
        StrategicReactionPass,
        EconomyDeferredBuild,
        EconomyBuildCompletion,
    }

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

            // EconomyDeferredBuild means the build is a durable multi-turn obligation whose
            // persistent H/E/M/T must survive Phase B, but whose completion is NOT executable in
            // the current turn. AP is turn-local execution capacity and therefore has no legal
            // deferred state. Completion AP is owned exclusively by EconomyBuildCompletion after
            // Provisioning has proved that this concrete actor can finish now. Treat an attempted
            // deferred AP write as a zero-upsert so an older row is removed rather than leaked.
            if (r.Reason == StrategicReservationReason.EconomyDeferredBuild
                && r.Resource == StrategicReservedResource.ActionPoints)
                r.Amount = 0f;

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

        // `ignoreOwner` excludes a caller's OWN reservation from the sum by
        // its EXACT Owner key (not by the shared Reason), so a pass can re-check "would this still be
        // affordable if MY hold weren't there" without tearing its reservation down (which would let
        // another action grab the freed resource), AND two reaction owners that share
        // Reason=StrategicReactionPass cannot shadow each other's revalidation. Used by the reaction
        // feasibility probe / re-probe (StrategicReactionPass §P1).
        // `ignoreReason` additionally drops every row of that reason, whoever owns it. Its one
        // legitimate use is StrategicSpendability.FitsSpendableForEconomyCompletion: a build that
        // completes NOW is senior to other builds' EconomyDeferredBuild holds, which by definition
        // cannot complete this turn and exist only to shield H/E/M/T from non-Economy spending.
        public static float Active(PlayerSetupData player, int turn, StrategicReservedResource res,
            string ignoreOwner, StrategicReservationReason? ignoreReason = null)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return 0f;
            float sum = 0f;
            foreach (StrategicResourceReservation r in e.Reservations)
                if (r.Resource == res && (ignoreOwner == null || r.Owner != ignoreOwner)
                    && (ignoreReason == null || r.Reason != ignoreReason.Value))
                    sum += Mathf.Max(0f, r.Amount);
            return sum;
        }

        // SpendableResource = TotalResource - Σ ActiveReservations(resource). Generic over every
        // strategic resource (spec §6).
        public static float Spendable(PlayerSetupData player, int turn, StrategicReservedResource res, float total)
            => Mathf.Max(0f, total - Active(player, turn, res));

        // As Spendable, but excluding the caller's own reservation by its EXACT Owner key.
        // See Active(…, ignoreOwner).
        public static float SpendableExcludingOwner(PlayerSetupData player, int turn, StrategicReservedResource res,
            float total, string ignoreOwner, StrategicReservationReason? ignoreReason = null)
            => Mathf.Max(0f, total - Active(player, turn, res, ignoreOwner, ignoreReason));

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

        // Multiple independent Economy deliveries can be active at once, so this reason is not a
        // single mutually-exclusive global slot. With an
        // explicit owner, this call touches ONLY that owner's own rows for the given reason and
        // never another operation's hold; passing owner=null is the one legitimate whole-reason
        // reset (see ClearDeferredEconomyResources, the once-per-turn full rebuild). For the SAME
        // owner, writing the deferred stage is an explicit lifecycle downgrade: that actor
        // can no longer complete this turn, so its OWN old EconomyBuildCompletion rows (including
        // AP) must disappear immediately while the durable mission remains protected by the
        // deferred H/E/M/T rows written next — a different owner's completion hold is untouched.
        public static void ReplaceReasonOwner(PlayerSetupData player, int turn,
            StrategicReservationReason reason, string owner, bool replaceOwnerRows = false)
        {
            if (player == null) return;
            Entry e = GetOrReset(player, turn);
            bool ownerScoped = !string.IsNullOrEmpty(owner);
            int downgraded = 0;
            if (reason == StrategicReservationReason.EconomyDeferredBuild
                && replaceOwnerRows && ownerScoped)
            {
                downgraded = e.Reservations.RemoveAll(r => r.Owner == owner
                    && r.Reason == StrategicReservationReason.EconomyBuildCompletion);
            }
            int removed = e.Reservations.RemoveAll(r => r.Reason == reason
                && (string.IsNullOrEmpty(owner) || r.Owner == owner));
            if (downgraded > 0)
                AiDebugLog.Write($"[AI][V2] reservation - downgraded {downgraded} completion row(s) "
                    + $"to deferred owner={owner}; active [{DebugLine(player, turn)}]");
            if (removed > 0)
                AiDebugLog.Write($"[AI][V2] reservation - replaced {removed} ({reason}) "
                    + $"owner={owner ?? "none"}; active [{DebugLine(player, turn)}]");
        }

        // Read-only enumeration of real completion owners, never a second reservation ledger.
        // Snapshot the keys before the lifecycle owner modifies its own rows.
        internal static IReadOnlyList<string> CompletionOwners(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return System.Array.Empty<string>();
            return e.Reservations.Where(r => r.Reason == StrategicReservationReason.EconomyBuildCompletion)
                .Select(r => r.Owner).Where(owner => !string.IsNullOrEmpty(owner))
                .Distinct().OrderBy(owner => owner).ToList();
        }

        public static bool HasOwnerReason(PlayerSetupData player, int turn, string owner,
            StrategicReservationReason reason) =>
            player != null && !string.IsNullOrEmpty(owner)
            && ByPlayer.TryGetValue(player, out Entry e) && e.Turn == turn
            && e.Reservations.Any(r => r.Owner == owner && r.Reason == reason);

        // Deliberately UNUSED by policy code. "Does anybody hold this reason" is not a valid
        // question for a per-owner obligation: it would let one Economy build's completion suppress
        // another build's protection. Kept only as a ledger-inspection
        // primitive (diagnostics/tests); every lifecycle decision must use HasOwnerReason.
        public static bool HasReason(PlayerSetupData player, int turn,
            StrategicReservationReason reason) => player != null
            && ByPlayer.TryGetValue(player, out Entry e) && e.Turn == turn
            && e.Reservations.Any(r => r.Reason == reason);

        public static bool OwnerReasonMatches(PlayerSetupData player, int turn, string owner,
            StrategicReservationReason reason, Game.Cards.ResourceCost cost, float ap)
        {
            if (player == null || string.IsNullOrEmpty(owner)
                || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return false;
            List<StrategicResourceReservation> rows = e.Reservations
                .Where(r => r.Owner == owner && r.Reason == reason).ToList();
            float Expected(StrategicReservedResource resource) => resource switch
            {
                // Deferred Economy may protect only persistent H/E/M/T. Even if an older caller
                // still passes its eventual follow-up AP for comparison, that AP is not a legal
                // reservation until the reason transitions to EconomyBuildCompletion.
                StrategicReservedResource.ActionPoints =>
                    reason == StrategicReservationReason.EconomyDeferredBuild
                        ? 0f : Mathf.Max(0f, ap),
                StrategicReservedResource.Human => Mathf.Max(0, cost?.Get(ResourceType.Human) ?? 0),
                StrategicReservedResource.Energy => Mathf.Max(0, cost?.Get(ResourceType.Energy) ?? 0),
                StrategicReservedResource.Materials => Mathf.Max(0, cost?.Get(ResourceType.Materials) ?? 0),
                _ => Mathf.Max(0, cost?.Get(ResourceType.Tech) ?? 0),
            };
            foreach (StrategicReservedResource resource in System.Enum.GetValues(
                         typeof(StrategicReservedResource)))
            {
                float expected = Expected(resource);
                float actual = rows.Where(r => r.Resource == resource).Sum(r => r.Amount);
                if (!Mathf.Approximately(expected, actual))
                    return false;
            }
            return rows.Count == rows.Select(r => r.Resource).Distinct().Count();
        }

        public static bool ReleaseByOwner(PlayerSetupData player, int turn, string owner)
        {
            if (player == null || string.IsNullOrEmpty(owner)
                || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return false;
            int removed = e.Reservations.RemoveAll(r => r.Owner == owner);
            if (removed > 0)
                AiDebugLog.Write($"[AI][V2] reservation - released {removed} owner={owner}; "
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

        // Order-stable key of every row held for one reason — an admission-fingerprint input
        // (see Pipeline's Economy fingerprint), not a spend query.
        internal static string ReasonDigest(PlayerSetupData player, int turn,
            StrategicReservationReason reason)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return string.Empty;
            return string.Join(";", e.Reservations.Where(r => r.Reason == reason)
                .Select(r => r.ToString()).OrderBy(x => x, System.StringComparer.Ordinal));
        }

        public static string DebugLine(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn
                || e.Reservations.Count == 0)
                return "ownerAwarePhysical=none";
            return "ownerAwarePhysical=[" + string.Join(", ",
                e.Reservations.Select(r => r.ToString())) + "]";
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
