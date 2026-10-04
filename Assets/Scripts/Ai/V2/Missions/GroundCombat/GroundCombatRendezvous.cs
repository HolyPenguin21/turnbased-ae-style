using Game.HexGrid;

namespace Game.Ai.V2
{
    // Where a committed Attack primary and an existing support meet. The answer is a hex ON the
    // primary's own route to the target (AiV2Util.TravelRoute, the route TravelCost measures), so
    // the primary only ever moves toward the target: its progress is never negative and it never
    // backtracks to fetch a support. The support pays its own travel to that hex; the primary may
    // hold there for at most `maxWaitTurns`. No such hex — the support is too far behind, or
    // cannot reach the route — means the reinforcement is not operationally useful.
    internal readonly struct RendezvousPlan
    {
        internal readonly HexCoord Hex;
        internal readonly int PrimaryCost, SupportCost, PrimaryTurns, SupportTurns, RouteIndex;
        internal int WaitTurns => System.Math.Max(0, SupportTurns - PrimaryTurns);

        internal RendezvousPlan(HexCoord hex, int primaryCost, int supportCost, int primaryTurns,
            int supportTurns, int routeIndex)
        {
            Hex = hex;
            PrimaryCost = primaryCost;
            SupportCost = supportCost;
            PrimaryTurns = primaryTurns;
            SupportTurns = supportTurns;
            RouteIndex = routeIndex;
        }

        internal string Describe() =>
            $"rendezvous=({Hex.Q},{Hex.R}) primaryTurns={PrimaryTurns} supportTurns={SupportTurns} "
            + $"wait={WaitTurns} supportCost={SupportCost} primary backtrack=0 (on its route)";
    }

    internal static class GroundCombatRendezvous
    {
        // The meeting hex on `primary`'s route to `targetHex` (the target itself excluded: the
        // handoff happens before the assault step). Least primary wait first (tempo), then the
        // support's cheapest walk, then the hex nearest the target. `keep` — the rendezvous
        // already agreed: while it is still on the route and within the wait bound it stays, so
        // the two armies never chase a meeting hex that shifts every pass.
        internal static RendezvousPlan? SelectForward(WorldSnapshot snap, ArmySnapshot primary,
            ArmySnapshot support, HexCoord targetHex, int maxWaitTurns, out string why,
            HexCoord? keep = null)
        {
            why = null;
            if (primary == null || support == null)
            {
                why = "primary or support missing";
                return null;
            }
            HexPath route = AiV2Util.TravelRoute(snap, primary, targetHex);
            if (route?.Hexes == null || route.Hexes.Count == 0)
            {
                why = $"primary #{primary.ArmyId} has no route to ({targetHex.Q},{targetHex.R})";
                return null;
            }

            RendezvousPlan? best = null;
            int reachable = 0;
            for (int i = 0; i < route.Hexes.Count; i++)
            {
                HexCoord hex = route.Hexes[i];
                if (hex.Equals(targetHex))
                    break;
                int supportCost = support.Hex.Equals(hex) ? 0 : AiV2Util.TravelCost(snap, support, hex);
                if (supportCost == int.MaxValue)
                    continue;
                reachable++;
                int primaryCost = i == 0 ? 0 : AiV2Util.TravelCost(snap, primary, hex);
                if (primaryCost == int.MaxValue)
                    continue;
                var plan = new RendezvousPlan(hex, primaryCost, supportCost,
                    i == 0 ? 0 : AiV2Util.TurnsToCover(primary, primaryCost),
                    supportCost == 0 ? 0 : AiV2Util.TurnsToCover(support, supportCost), i);
                if (plan.WaitTurns > maxWaitTurns)
                    continue;
                if (keep.HasValue && hex.Equals(keep.Value))
                    return plan;
                if (best == null || Better(plan, best.Value))
                    best = plan;
            }
            if (best == null)
                why = reachable == 0
                    ? $"support #{support.ArmyId} cannot reach the primary's route to the target"
                    : $"support #{support.ArmyId} cannot meet primary #{primary.ArmyId} on its route "
                        + $"to the target within {maxWaitTurns} turn(s) of waiting (no non-retreat rendezvous)";
            return best;
        }

        private static bool Better(RendezvousPlan a, RendezvousPlan b)
        {
            if (a.WaitTurns != b.WaitTurns) return a.WaitTurns < b.WaitTurns;
            if (a.SupportCost != b.SupportCost) return a.SupportCost < b.SupportCost;
            return a.RouteIndex > b.RouteIndex;
        }
    }
}
