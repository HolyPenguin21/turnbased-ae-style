using System.Collections.Generic;
using Game.Economy;
using Game.HexGrid;

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
        }

        public static ResourceYields GetBonus(HexCoord hex)
        {
            return ByHex.TryGetValue(hex, out ResourceYields bonus) ? bonus : null;
        }
    }
}
