using Game.Terrain;

namespace Game.Economy
{
    // Combines what a hex's terrain produces with whatever permanent bonus was stamped onto
    // that hex (see Game.Map.HexResourceBonusRegistry — a citadel's own bonus belongs to the
    // hex the player chose, not to the citadel's continued presence there, so callers resolve
    // the bonus from that registry and hand it in rather than this recomputing it from a
    // building lookup). Just the "what would this hex yield right now" calculation, used for
    // both display and actual per-turn collection.
    public static class HexResourceCalculator
    {
        public static ResourceYields GetEffectiveYield(TerrainTypeEntry terrain, ResourceYields bonus)
        {
            ResourceYields baseYield = terrain?.resourceYields ?? new ResourceYields();
            return bonus != null ? baseYield.Add(bonus) : baseYield;
        }
    }
}
