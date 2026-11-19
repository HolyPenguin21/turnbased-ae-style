using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Timing — when a ground roster arrives (the route execution walks) and whether it beats a
    // threat's deadline; CanReachNow is "arrives this own turn".
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        // THE proof that a fight can START now: the Combat-profile route's entering-hex costs (the
        // enemy hex included — arrival is the attack) fit the CURRENT movement, and no single hex
        // exceeds the roster's MaxMovement. Geometry, MaxMovement, ETA or "next turn" prove nothing.
        // `currentMovement` / `maxMovement` are the figures of the roster that will really march.
        internal static bool CanReachNow(HexMap map, PlayerSetupData owner, HexCoord from,
            HexCoord target, int currentMovement, int maxMovement, out int routeCost)
        {
            routeCost = int.MaxValue;
            if (map == null || owner == null || currentMovement <= 0 || maxMovement <= 0
                || from.Equals(target))
                return false;
            return WalkGroundRoute(map, owner, from, target, currentMovement, maxMovement,
                fastestByTurns: false, out routeCost) == 0;
        }

        internal static bool CanReachNow(WorldSnapshot snap, ArmySnapshot actor, HexCoord target,
            out int routeCost, int? currentMovement = null, int? maxMovement = null)
        {
            routeCost = int.MaxValue;
            if (actor == null || snap == null)
                return false;
            int current = currentMovement ?? actor.CurrentMovement;
            int max = maxMovement ?? actor.MaxMovement;
            if (ReferenceEquals(snap.Map, null))
            {
                // Synthetic fixtures have no map: one cost point per hex of the straight line.
                if (current <= 0 || actor.Hex.Equals(target)) return false;
                routeCost = HexGridMath.Distance(actor.Hex, target);
                return routeCost <= current;
            }
            return CanReachNow(snap.Map, snap.Observer, actor.Hex, target, current, max, out routeCost);
        }

        // ETA=0 means arrival THIS own turn; a future equal ETA is unsafe because the enemy can
        // act before our next turn (GroundArrivalEta).
        internal static int ArrivalEta(WorldSnapshot snap, ArmySnapshot actor, HexCoord destination)
        {
            if (actor == null) return int.MaxValue;
            if (ReferenceEquals(snap?.Map, null) && !actor.Hex.Equals(destination)
                && actor.ReachableOwnBaseHexes?.Contains(destination) != true) return int.MaxValue;
            return GroundArrivalEta(snap, actor.Hex, destination, actor.CurrentMovement, actor.MaxMovement);
        }

        // THE arrival time of a ground roster: the route execution really walks
        // (AiTurnController.FindAffordableStep searches turn-optimally), so every deadline —
        // intercept, regroup, withdrawal, relief, march — is judged on that same route.
        // 2026-11-19 (owner, audit D3): regroup and withdrawal used the cheapest-cost route,
        // which can need a turn more than the walk the army actually makes.
        internal static int GroundArrivalEta(WorldSnapshot snap, HexCoord from, HexCoord destination,
            int currentMovement, int maxMovement) =>
            WalkGroundRoute(snap?.Map, snap?.Observer, from, destination, currentMovement,
                maxMovement, fastestByTurns: true, out _);

        // THE walk of a Combat-profile ground route (the route GroundCombatLegStep.Transit moves
        // on): the owner turns to arrive — 0 is THIS own turn — packing the actual entering-hex
        // costs into MP budgets (a total-cost division underestimates routes whose terrain steps
        // cannot consume the last MP of a turn), and the route's MP cost. int.MaxValue: no legal
        // route, or a single step the roster's MaxMovement cannot pay. `fastestByTurns` — the
        // turn-optimal route instead of the cheapest one. No map (fixtures): a straight line at
        // one MP per hex.
        private static int WalkGroundRoute(HexMap map, PlayerSetupData owner, HexCoord from,
            HexCoord destination, int currentMovement, int maxMovement, bool fastestByTurns,
            out int routeCost)
        {
            routeCost = int.MaxValue;
            if (from.Equals(destination)) { routeCost = 0; return 0; }
            if (maxMovement <= 0) return int.MaxValue;
            bool noMap = ReferenceEquals(map, null);
            HexPath route = noMap
                ? AiV2Util.StraightLine(from, destination)
                : fastestByTurns
                    ? SafeStepPathing.FindSafePathByTurns(map, owner, from,
                        destination, currentMovement, maxMovement, SafeRouteProfile.Combat)
                    : SafeStepPathing.FindSafePath(map, owner, from,
                        destination, maxMovement, SafeRouteProfile.Combat);
            if (route == null || route.Hexes.Count < 2) return int.MaxValue;
            int turns = 0, total = 0, remaining = Math.Max(0, fastestByTurns
                ? Math.Min(currentMovement, maxMovement) : currentMovement);
            for (int i = 1; i < route.Hexes.Count; i++)
            {
                int cost = 1;
                if (!noMap && !MovementRules.TryGetStepCost(map, route.Hexes[i - 1], route.Hexes[i],
                        MovementProfile.Ground, out cost))
                    return int.MaxValue;
                if (cost > maxMovement) return int.MaxValue;
                if (cost > remaining) { turns++; remaining = maxMovement; }
                remaining -= cost;
                total += cost;
            }
            routeCost = total;
            return turns;
        }

        internal static bool CanArriveBeforeThreat(WorldSnapshot snap, ArmySnapshot actor,
            HexCoord destination, int? enemyEta, out int eta)
        {
            eta = ArrivalEta(snap, actor, destination);
            return eta != int.MaxValue && enemyEta.HasValue && eta < enemyEta.Value;
        }

        // Shared by response selection, actor admission, assigned binding and continuity.
        // Our own live roster projection is the same one already used to price the proposal.
        internal static bool CanInterceptPlanBeforeThreat(WorldSnapshot snap,
            ActiveDefenceMissionTarget target, GroundCombatAssemblyPlan plan)
        {
            if (plan?.Feasible != true || snap?.Self?.Armies == null) return false;
            ArmySnapshot host = snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == plan.BaseArmyId);
            if (host == null) return false;
            int current = GroundCombatAssemblyPlanner.ProjectedCurrentMovement(snap, plan) ?? host.CurrentMovement;
            int maximum = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan) ?? host.MaxMovement;
            return CanInterceptRosterBeforeThreat(snap, host.Hex, target.LastKnownHex,
                target.EnemyEta, current, maximum, out _);
        }

        // Final-roster seam used again before any provisioning mutation. Unknown is uncertainty;
        // arrival at the same future ETA is too late under the existing conservative turn order.
        internal static bool CanInterceptRosterBeforeThreat(WorldSnapshot snap, HexCoord from,
            HexCoord enemyHex, int? enemyEta, int currentMovement, int maxMovement, out int arrivalEta)
        {
            arrivalEta = GroundArrivalEta(snap, from, enemyHex, currentMovement, maxMovement);
            return arrivalEta != int.MaxValue && enemyEta.HasValue && arrivalEta < enemyEta.Value;
        }
    }
}
