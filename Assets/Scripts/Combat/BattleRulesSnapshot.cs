using Game.Map;

namespace Game.Combat
{
    // Authored/environment rules freeze at construction. Formation remains a live grid rule.
    public readonly struct BattleRulesSnapshot
    {
        public readonly AbilityMagnitudes Magnitudes;
        public readonly int TerrainDefenseBonus;
        public readonly int BuildingDefenseBonus;
        private BattleRulesSnapshot(AbilityMagnitudes magnitudes, int terrain, int building)
        { Magnitudes = magnitudes; TerrainDefenseBonus = terrain; BuildingDefenseBonus = building; }
        internal static BattleRulesSnapshot Capture(BattleState state, AbilityMagnitudes magnitudes, HexMap map)
        {
            int terrain = map != null && map.TryGetTerrainAt(state.BattleHex, out var entry) && entry != null ? entry.defenseModifier : 0;
            return new BattleRulesSnapshot(magnitudes, terrain, state.DefenderBuildingDefenseBonus);
        }
    }
}
