using System.Collections.Generic;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  TURN RESOURCE BOOK — every claim on the physical stock in one list, one Free formula
    // ===========================================================================================
    //  A claim is either an explicit StrategicResourceReservationLedger row or a derived
    //  obligation computed live (the next step of a continuing Hard operation — see
    //  StrategicSpendability.OperationContinuationHold). Mandatory aviation holds nothing: it is
    //  settled before any card play (AviationObligations). Every "how much may THIS spender use"
    //  question is
    //
    //      Free(r, authority) = Physical(r) - Σ claims(r) the authority may not draw on
    //
    //  and "may draw on" is the one table in MayDrawOn. The book decides nothing about who SHOULD
    //  get a resource — Phase A, the allocator and Phase B still make those choices; it only
    //  answers how much each of them is allowed to take.
    // ===========================================================================================
    internal enum ResourceClaimKind
    {
        EconomyDeferred,        // a build still being delivered: shields H/E/M/T, never AP
        EconomyCompletion,      // a build Provisioning proved can finish this turn
        OperationContinuation,  // unpaid activation of a continuing Hard operation's next leg
        GroundCombatDeparture,  // activation of a fresh ground-combat mission departing this turn
    }

    internal readonly struct ResourceClaim
    {
        public readonly string Owner;
        public readonly ResourceClaimKind Kind;
        public readonly StrategicReservedResource Resource;
        public readonly float Amount;

        public ResourceClaim(string owner, ResourceClaimKind kind,
            StrategicReservedResource resource, float amount)
        {
            Owner = owner;
            Kind = kind;
            Resource = resource;
            Amount = amount;
        }

        public override string ToString() =>
            $"{Owner}:{Kind} {Amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {Resource}";
    }

    internal static class TurnResourceBook
    {
        // Owner key of the derived claim. It never matches a ledger owner; the one SpendAuthority
        // carrying it is an Attack strike-force demand (InfrastructureFulfillment.SpendAuthorityFor).
        internal const string OperationContinuationOwner = "derived:operation-continuation";

        internal static ResourceClaimKind KindOf(StrategicReservationReason reason) => reason switch
        {
            StrategicReservationReason.EconomyDeferredBuild => ResourceClaimKind.EconomyDeferred,
            StrategicReservationReason.GroundCombatDeparture => ResourceClaimKind.GroundCombatDeparture,
            _ => ResourceClaimKind.EconomyCompletion,
        };

        // THE table: which claims a spender may use as if they were free.
        internal static bool MayDrawOn(SpendAuthority authority, ResourceClaim claim)
        {
            // Its own hold (a builder hero serving its build).
            if (authority.Owner != null && claim.Owner == authority.Owner)
                return true;
            // A build that completes now is senior to other builds' deferred holds, which by
            // definition cannot complete this turn and only shield H/E/M/T from other spending.
            if (authority.EconomyCompletesNow && claim.Kind == ResourceClaimKind.EconomyDeferred)
                return true;
            // Answering a threat to own assets is senior to a fresh departure.
            if (authority.DefendsHome && claim.Kind == ResourceClaimKind.GroundCombatDeparture)
                return true;
            return false;
        }

        internal static float Free(float physical, IEnumerable<ResourceClaim> claims,
            StrategicReservedResource resource, SpendAuthority authority) =>
            Mathf.Max(0f, physical - Outstanding(claims, resource, authority));

        // Σ of the claims `authority` may not draw on, each owner's net of what that owner has
        // already drawn (`drawsByOwner` — units a planner has already given to that owner's own
        // work and subtracted from the pool). Drawing on your own hold consumes it, so the same
        // units are never counted twice: once as drawn, once as still held.
        internal static float Outstanding(IEnumerable<ResourceClaim> claims,
            StrategicReservedResource resource, SpendAuthority authority,
            IReadOnlyDictionary<string, float> drawsByOwner = null)
        {
            if (claims is IReadOnlyList<ResourceClaim> list && list.Count == 0)
                return 0f;

            Dictionary<string, float> heldByOwner = availableOwnerTotals ?? new Dictionary<string, float>();
            availableOwnerTotals = null;
            try
            {
                if (claims is IReadOnlyList<ResourceClaim> indexed)
                {
                    for (int i = 0; i < indexed.Count; i++)
                        Accumulate(heldByOwner, indexed[i], resource, authority);
                }
                else
                {
                    foreach (ResourceClaim claim in claims)
                        Accumulate(heldByOwner, claim, resource, authority);
                }

                float outstanding = 0f;
                foreach (KeyValuePair<string, float> owner in heldByOwner)
                {
                    float drawn = 0f;
                    if (drawsByOwner != null)
                        drawsByOwner.TryGetValue(owner.Key, out drawn);
                    outstanding += Mathf.Max(0f, owner.Value - Mathf.Max(0f, drawn));
                }
                return outstanding;
            }
            finally
            {
                heldByOwner.Clear();
                availableOwnerTotals = heldByOwner;
            }
        }

        // Borrow scratch storage only during the synchronous read. Keep the existing owner
        // grouping and summation order even with no draws, so fractional holds price identically.
        [System.ThreadStatic] private static Dictionary<string, float> availableOwnerTotals;
        [System.ThreadStatic] private static List<ResourceClaim> availableClaims;

        private static void Accumulate(Dictionary<string, float> totals, ResourceClaim claim,
            StrategicReservedResource resource, SpendAuthority authority)
        {
            if (claim.Resource != resource || MayDrawOn(authority, claim))
                return;
            string key = claim.Owner ?? string.Empty;
            totals.TryGetValue(key, out float held);
            totals[key] = held + Mathf.Max(0f, claim.Amount);
        }

        internal static float Physical(PlayerRoot root, StrategicReservedResource resource)
        {
            if (root == null)
                return 0f;
            return resource switch
            {
                StrategicReservedResource.ActionPoints => root.ActionPoints,
                StrategicReservedResource.Human => root.GetResource(ResourceType.Human),
                StrategicReservedResource.Energy => root.GetResource(ResourceType.Energy),
                StrategicReservedResource.Materials => root.GetResource(ResourceType.Materials),
                _ => root.GetResource(ResourceType.Tech),
            };
        }

        // The ledger rows of `turn` as claims, without the live derived claim. EconomyDeferred
        // rows are listed net of the turn's income cover once it is open
        // (EconomyReservationLifecycle.ReleaseDeferredEconomyIncomeCover).
        internal static List<ResourceClaim> LedgerClaims(PlayerSetupData player, int turn,
            StrategicReservedResource? only = null)
        {
            var claims = new List<ResourceClaim>();
            FillLedgerClaims(claims, StrategicResourceReservationLedger.LiveRows(player, turn), player, turn, only);
            return claims;
        }

        private static void FillLedgerClaims(List<ResourceClaim> claims,
            IReadOnlyList<StrategicResourceReservation> rows, PlayerSetupData player, int turn,
            StrategicReservedResource? only, bool physicalOnly = false)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                StrategicResourceReservation row = rows[i];
                if ((only == null || row.Resource == only.Value)
                    && (!physicalOnly || row.Resource != StrategicReservedResource.ActionPoints))
                    claims.Add(new ResourceClaim(row.Owner, KindOf(row.Reason), row.Resource, row.Amount));
            }
            ApplyDeferredIncomeCover(claims, player, turn);
        }

        // The cover is shared by the deferred owners of one resource in ordinal owner order; a
        // fully covered claim is dropped, exactly as the ledger drops a non-positive row.
        private static void ApplyDeferredIncomeCover(List<ResourceClaim> claims,
            PlayerSetupData player, int turn)
        {
            List<int> deferred = null;
            for (int i = 0; i < claims.Count; i++)
                if (claims[i].Kind == ResourceClaimKind.EconomyDeferred
                    && EconomyReservationLifecycle.DeferredIncomeCover(player, turn, claims[i].Resource) > 0f)
                    (deferred ??= new List<int>()).Add(i);
            if (deferred != null)
                DistributeDeferredIncomeCover(claims, deferred, player, turn);
        }

        private static void DistributeDeferredIncomeCover(List<ResourceClaim> claims, List<int> deferred,
            PlayerSetupData player, int turn)
        {
            var cover = new Dictionary<StrategicReservedResource, float>();
            deferred.Sort((a, b) => System.StringComparer.Ordinal.Compare(claims[a].Owner, claims[b].Owner));
            var drop = new List<int>();
            foreach (int i in deferred)
            {
                ResourceClaim c = claims[i];
                if (!cover.TryGetValue(c.Resource, out float left))
                    left = EconomyReservationLifecycle.DeferredIncomeCover(player, turn, c.Resource);
                float released = Mathf.Min(left, Mathf.Max(0f, c.Amount));
                cover[c.Resource] = left - released;
                float held = c.Amount - released;
                if (held <= 0f) drop.Add(i);
                else claims[i] = new ResourceClaim(c.Owner, c.Kind, c.Resource, held);
            }
            drop.Sort();
            for (int k = drop.Count - 1; k >= 0; k--)
                claims.RemoveAt(drop[k]);
        }

        // Every claim of this turn. `only` limits the list to one resource, so a query for
        // Materials does not pay for the live operation scan.
        internal static List<ResourceClaim> Claims(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, StrategicReservedResource? only = null)
        {
            if (player == null || ctx == null)
                return new List<ResourceClaim>();
            List<ResourceClaim> claims = LedgerClaims(player, ctx.TurnNumber, only);

            bool wantAp = only == null || only.Value == StrategicReservedResource.ActionPoints;
            if (!wantAp || root == null)
                return claims;
            float continuationAp = StrategicSpendability.OperationContinuationHold(player, root, ctx);
            if (continuationAp > 0f)
                claims.Add(new ResourceClaim(OperationContinuationOwner,
                    ResourceClaimKind.OperationContinuation,
                    StrategicReservedResource.ActionPoints, continuationAp));
            return claims;
        }

        // How much of `resource` this spender may use right now.
        internal static float Free(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            StrategicReservedResource resource, SpendAuthority authority)
        {
            if (root == null)
                return 0f;
            float physical = Physical(root, resource);
            if (player == null || ctx == null)
                return Mathf.Max(0f, physical);
            if (resource == StrategicReservedResource.ActionPoints)
                return Free(physical, Claims(player, root, ctx, resource), resource, authority);

            var rows = StrategicResourceReservationLedger.LiveRows(player, ctx.TurnNumber);
            if (rows.Count == 0)
                return Mathf.Max(0f, physical);

            var claims = RentClaims();
            try
            {
                FillLedgerClaims(claims, rows, player, ctx.TurnNumber, resource);
                return Free(physical, claims, resource, authority);
            }
            finally { ReturnClaims(claims); }
        }

        internal static ResourceBundle FreeResources(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, SpendAuthority authority = default)
        {
            if (root == null) return default;
            var rows = player != null && ctx != null
                ? StrategicResourceReservationLedger.LiveRows(player, ctx.TurnNumber)
                : System.Array.Empty<StrategicResourceReservation>();
            if (rows.Count == 0)
            {
                var stockpile = new ResourceBundle();
                foreach (ResourceType type in ResourceBundle.All)
                    stockpile.Add(type, Mathf.Max(0f, root.GetResource(type)));
                return stockpile;
            }

            var claims = RentClaims();
            try
            {
                FillLedgerClaims(claims, rows, player, ctx.TurnNumber, null, physicalOnly: true);
                var resources = new ResourceBundle();
                foreach (ResourceType type in ResourceBundle.All)
                    resources.Add(type, Free(root.GetResource(type), claims,
                        StrategicResourceReservationLedger.Map(type), authority));
                return resources;
            }
            finally { ReturnClaims(claims); }
        }

        private static List<ResourceClaim> RentClaims()
        {
            var claims = availableClaims ?? new List<ResourceClaim>();
            availableClaims = null;
            return claims;
        }

        private static void ReturnClaims(List<ResourceClaim> claims)
        {
            claims.Clear();
            availableClaims = claims;
        }
    }
}
