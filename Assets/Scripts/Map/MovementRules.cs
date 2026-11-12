using System.Collections.Generic;
using Game.HexGrid;
using Game.Terrain;

namespace Game.Map
{
    public enum MovementProfile { Ground, Air }

    // ===========================================================================================
    //  The ONE movement-cost rule (route search, forecasts, the real move and the arrow):
    //    base   = Ground ? max(1, terrain(to).moveCost) : 1
    //    storm  = the SOURCE hex is under any storm ? 1 : 0   (overlapping storms count once)
    //    step   = base + storm
    //  Entry legality is the existing terrain rule (HexMap.CanEnter): a storm never makes
    //  blocking ground terrain passable. Off-map or non-adjacent steps are illegal. Activation
    //  AP/Energy, AA risk and avoid-the-enemy heuristics are NOT movement points and never enter
    //  this cost.
    // ===========================================================================================
    public static class MovementRules
    {
        public const int StormExitPenalty = 1;

        public static MovementProfile ProfileOf(ArmyData army) =>
            Game.Aviation.AviationRules.IsAirArmy(army) ? MovementProfile.Air : MovementProfile.Ground;

        public static MovementProfile ProfileOf(bool airborne) => airborne ? MovementProfile.Air : MovementProfile.Ground;

        // The shared arithmetic once legality is settled (used by the flat search grid as well).
        public static int StepCost(TerrainTypeEntry target, bool sourceInStorm, MovementProfile profile)
        {
            int baseCost = profile == MovementProfile.Air ? 1 : System.Math.Max(1, target != null ? target.moveCost : 1);
            return baseCost + (sourceInStorm ? StormExitPenalty : 0);
        }

        public static bool CanEnter(HexMap map, HexCoord hex, MovementProfile profile) =>
            map != null && map.CanEnter(hex, profile == MovementProfile.Air);

        public static bool TryGetStepCost(HexMap map, HexCoord from, HexCoord to, MovementProfile profile,
            SandstormSnapshot environment, out int mp)
        {
            mp = 0;
            if (map == null || HexGridMath.Distance(from, to) != 1
                || !map.TryGetTerrainAt(from, out _) || !CanEnter(map, to, profile)
                || !map.TryGetTerrainAt(to, out TerrainTypeEntry target))
                return false;
            mp = StepCost(target, (environment ?? SandstormSnapshot.Clear).Contains(from), profile);
            return true;
        }

        // Current weather on that map.
        public static bool TryGetStepCost(HexMap map, HexCoord from, HexCoord to, MovementProfile profile, out int mp) =>
            TryGetStepCost(map, from, to, profile, map?.Weather, out mp);

        // Sum of the route's real transitions — never its hex count. A one-hex route costs 0;
        // an empty, broken or illegal route is rejected; overflow never makes it cheap.
        public static bool TryGetRouteCost(HexMap map, IReadOnlyList<HexCoord> route, MovementProfile profile,
            SandstormSnapshot environment, out int mp)
        {
            mp = 0;
            if (map == null || route == null || route.Count == 0) return false;
            if (!CanEnter(map, route[0], profile)) return false;
            long total = 0;
            for (int i = 1; i < route.Count; i++)
            {
                if (!TryGetStepCost(map, route[i - 1], route[i], profile, environment, out int step)) return false;
                total += step;
                if (total > int.MaxValue) return false;
            }
            mp = (int)total;
            return true;
        }

        public static bool TryGetRouteCost(HexMap map, IReadOnlyList<HexCoord> route, MovementProfile profile, out int mp) =>
            TryGetRouteCost(map, route, profile, map?.Weather, out mp);

        // Cost of every transition of the route in order (the aviation calendar walks these).
        public static bool TryGetStepCosts(HexMap map, IReadOnlyList<HexCoord> route, MovementProfile profile,
            SandstormSnapshot environment, List<int> costs)
        {
            costs?.Clear();
            if (map == null || route == null || route.Count == 0 || costs == null || !CanEnter(map, route[0], profile)) return false;
            for (int i = 1; i < route.Count; i++)
            {
                if (!TryGetStepCost(map, route[i - 1], route[i], profile, environment, out int step))
                {
                    costs.Clear();
                    return false;
                }
                costs.Add(step);
            }
            return true;
        }

        // The route cost for a mover, or int.MaxValue when unusable. Convenience for callers
        // comparing against a budget.
        public static int RouteCostOrMax(HexMap map, IReadOnlyList<HexCoord> route, MovementProfile profile) =>
            TryGetRouteCost(map, route, profile, out int mp) ? mp : int.MaxValue;
    }
}
