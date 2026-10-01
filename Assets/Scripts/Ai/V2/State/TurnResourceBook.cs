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
        Reaction,               // the bounded reaction budget + envelope held through Phase B
        OperationContinuation,  // unpaid activation of a continuing Hard operation's next leg
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
            StrategicReservationReason.EconomyBuildCompletion => ResourceClaimKind.EconomyCompletion,
            _ => ResourceClaimKind.Reaction,
        };

        // THE table: which claims a spender may use as if they were free.
        internal static bool MayDrawOn(SpendAuthority authority, ResourceClaim claim)
        {
            // Its own hold (a builder hero serving its build, a reaction re-probing its envelope).
            if (authority.Owner != null && claim.Owner == authority.Owner)
                return true;
            // A build that completes now is senior to other builds' deferred holds, which by
            // definition cannot complete this turn and only shield H/E/M/T from other spending.
            if (authority.EconomyCompletesNow && claim.Kind == ResourceClaimKind.EconomyDeferred)
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
            var heldByOwner = new Dictionary<string, float>();
            foreach (ResourceClaim c in claims)
            {
                if (c.Resource != resource || MayDrawOn(authority, c))
                    continue;
                string key = c.Owner ?? string.Empty;
                heldByOwner.TryGetValue(key, out float held);
                heldByOwner[key] = held + Mathf.Max(0f, c.Amount);
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

        // The ledger rows of `turn` as claims, without the live derived claim.
        internal static List<ResourceClaim> LedgerClaims(PlayerSetupData player, int turn,
            StrategicReservedResource? only = null)
        {
            var claims = new List<ResourceClaim>();
            foreach (StrategicResourceReservation r in
                     StrategicResourceReservationLedger.LiveRows(player, turn))
                if (only == null || r.Resource == only.Value)
                    claims.Add(new ResourceClaim(r.Owner, KindOf(r.Reason), r.Resource, r.Amount));
            return claims;
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
            return Free(physical, Claims(player, root, ctx, resource), resource, authority);
        }
    }
}
