using System;
using Game.Economy;
using Game.HexGrid;

namespace Game.Map
{
    // Physical map rule: which single resource a hex's bonus most favours (null when the hex
    // carries no resource bonus at all). Extracted verbatim from the former
    // Game.Ai.AiEconomyPlanner.DominantResourceType (ARCH-01) — a read of the hex bonus table,
    // no AI weighting.
    public static class HexResourceProfile
    {
        public static ResourceType? DominantResourceType(HexCoord hex)
        {
            ResourceYields bonus = HexResourceBonusRegistry.GetBonus(hex);
            if (bonus == null)
                return null;

            ResourceType best = ResourceType.Human;
            int bestAmount = 0;
            foreach (ResourceType type in (ResourceType[])Enum.GetValues(typeof(ResourceType)))
            {
                int amount = bonus.Get(type);
                if (amount > bestAmount)
                {
                    bestAmount = amount;
                    best = type;
                }
            }
            return bestAmount > 0 ? best : (ResourceType?)null;
        }
    }
}
