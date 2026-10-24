using System;
using System.Collections.Generic;
using Game.Economy;
using Game.HexGrid;
using Game.Terrain;

namespace Game.Map
{
    // Permanent per-hex resource bonuses, stamped once and independent of whatever building
    // later stands there — e.g. a citadel's own bonus (see CitadelSetupController) belongs to
    // the hex the player chose, not to the citadel's continued presence there. Mirrors
    // BuildingRegistry's shape, but deliberately a separate store: a hex keeps its bonus even if
    // the building on it changes (there's no way to remove/relocate a citadel yet, but this is
    // the model that's actually intended, not just what happens to be true today).
    public static class HexResourceBonusRegistry
    {
        private static readonly Dictionary<HexCoord, ResourceYields> ByHex = new Dictionary<HexCoord, ResourceYields>();

        public static void Clear()
        {
            ByHex.Clear();
        }

        public static void Set(HexCoord hex, ResourceYields bonus)
        {
            ByHex[hex] = bonus;
            // Citadels already reveal their surroundings before post-placement resources are
            // stamped. Unchanged visibility does not fire VisibilityChanged: publish the physical
            // content write through the existing fog-filtered observation boundary instead.
            VisionSystem.NotifyContentChanged(hex);
        }

        public static ResourceYields GetBonus(HexCoord hex)
        {
            return ByHex.TryGetValue(hex, out ResourceYields bonus) ? bonus : null;
        }

        // What `hex` would yield right now: its terrain's output plus its stamped bonus. Used for
        // both display and actual per-turn collection. Ground-blocking terrain yields nothing.
        public static ResourceYields EffectiveYield(TerrainTypeEntry terrain, HexCoord hex)
            => EffectiveYield(terrain, GetBonus(hex));

        public static ResourceYields EffectiveYield(TerrainTypeEntry terrain, ResourceYields bonus)
        {
            if (terrain?.blocksGroundMovement == true) return new ResourceYields();
            ResourceYields baseYield = terrain?.resourceYields ?? new ResourceYields();
            return bonus != null ? baseYield.Add(bonus) : baseYield;
        }

        // Physical map rule: which single resource a hex's bonus most favours (null when the hex
        // carries no resource bonus at all) — a read of the bonus table, no AI weighting.
        public static ResourceType? DominantResourceType(HexCoord hex)
        {
            ResourceYields bonus = GetBonus(hex);
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
