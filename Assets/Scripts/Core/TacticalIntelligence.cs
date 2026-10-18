using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Core
{
    // Read-only intelligence rules. No saved/cache state and no gameplay RNG consumption:
    // the match identity, player slots and observer's personal turn reproduce the same report.
    public static class TacticalIntelligence
    {
        public const int FieldCount = 16;

        public static bool HasAccess(PlayerSetupData observer) =>
            observer != null && !observer.IsNeutral && !observer.IsEliminated
            && IncomeProjection.CountInPlayAbilitySources(observer, UnitAbilities.TacticalIntelligence) > 0;

        public static bool IsGroundObserver(ArmyData army, PlayerSetupData observer) =>
            army != null && army.Owner == observer && !army.IsPrison && !army.IsGarrison
            && !army.IsAirfield && !army.IsAirArmy && !army.IsCarrierContainer
            && army.Members.Any(u => u != null && u.HitPointsCurrent > 0
                && !u.IsAviation && !u.IsPrisoner && !u.IsSummoned);

        public static int VisibleFieldCount(PlayerSetupData observer, PlayerSetupData enemy,
            IEnumerable<ArmyData> armies)
        {
            if (observer == null || enemy == null || observer == enemy || observer.IsEliminated
                || enemy.IsNeutral || enemy.IsEliminated
                || !enemy.CitadelHexQ.HasValue || !enemy.CitadelHexR.HasValue || armies == null)
                return 0;
            var citadel = new HexCoord(enemy.CitadelHexQ.Value, enemy.CitadelHexR.Value);
            int distance = 4;
            foreach (ArmyData army in armies)
                if (IsGroundObserver(army, observer))
                    distance = System.Math.Min(distance, HexGridMath.Distance(army.Hex, citadel));
            return (4 - distance) * 4;
        }

        public static int RevealMask(string matchId, int observerSlot, int enemySlot,
            long personalTurn, int visibleFields)
        {
            if (visibleFields <= 0) return 0;
            if (visibleFields >= FieldCount) return 0xffff;
            unchecked
            {
                uint seed = 2166136261;
                foreach (char c in matchId ?? string.Empty) seed = (seed ^ c) * 16777619;
                seed = (seed ^ (uint)observerSlot) * 16777619;
                seed = (seed ^ (uint)enemySlot) * 16777619;
                seed = (seed ^ (uint)personalTurn) * 16777619;
                seed = (seed ^ (uint)(personalTurn >> 32)) * 16777619;
                if (seed == 0) seed = 1;
                int[] order = new int[FieldCount];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                for (int i = order.Length - 1; i > 0; i--)
                {
                    seed ^= seed << 13;
                    seed ^= seed >> 17;
                    seed ^= seed << 5;
                    int j = (int)(seed % (uint)(i + 1));
                    int value = order[i]; order[i] = order[j]; order[j] = value;
                }
                int mask = 0;
                for (int i = 0; i < visibleFields; i++) mask |= 1 << order[i];
                return mask;
            }
        }
    }
}
