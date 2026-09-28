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
    //  obligation computed live (unpaid air-recovery activation, the next step of a continuing
    //  Hard operation — see StrategicSpendability.DerivedHolds). Every "how much may THIS spender
    //  use" question is
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
        AirRecovery,            // unpaid activation of airborne wings that must return / rebase
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
        // Owner keys of the derived claims. They never match a ledger owner or a SpendAuthority.
        internal const string AirRecoveryOwner = "derived:air-recovery";
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
            StrategicReservedResource resource, SpendAuthority authority)
        {
            float held = 0f;
            foreach (ResourceClaim c in claims)
                if (c.Resource == resource && !MayDrawOn(authority, c))
                    held += Mathf.Max(0f, c.Amount);
            return Mathf.Max(0f, physical - held);
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

        // Every claim of this turn. `only` limits the list to one resource, so a query for
        // Materials does not pay for the live AP/Energy obligation scans.
        internal static List<ResourceClaim> Claims(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, StrategicReservedResource? only = null)
        {
            var claims = new List<ResourceClaim>();
            if (player == null || ctx == null)
                return claims;
            foreach (StrategicResourceReservation r in
                     StrategicResourceReservationLedger.LiveRows(player, ctx.TurnNumber))
                if (only == null || r.Resource == only.Value)
                    claims.Add(new ResourceClaim(r.Owner, KindOf(r.Reason), r.Resource, r.Amount));

            bool wantAp = only == null || only.Value == StrategicReservedResource.ActionPoints;
            bool wantEnergy = only == null || only.Value == StrategicReservedResource.Energy;
            if ((!wantAp && !wantEnergy) || root == null)
                return claims;
            (float recoveryAp, int recoveryEnergy, float continuationAp) =
                StrategicSpendability.DerivedHolds(player, root, ctx, includeContinuation: wantAp);
            if (wantAp && recoveryAp > 0f)
                claims.Add(new ResourceClaim(AirRecoveryOwner, ResourceClaimKind.AirRecovery,
                    StrategicReservedResource.ActionPoints, recoveryAp));
            if (wantEnergy && recoveryEnergy > 0)
                claims.Add(new ResourceClaim(AirRecoveryOwner, ResourceClaimKind.AirRecovery,
                    StrategicReservedResource.Energy, recoveryEnergy));
            if (wantAp && continuationAp > 0f)
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
