using Game.Map;

namespace Game.Combat
{
    // Authored/environment rules freeze at construction.
    public readonly struct BattleRulesSnapshot
    {
        public readonly AbilityMagnitudes Magnitudes;
        public readonly int TerrainDefenseBonus;
        public readonly int BuildingDefenseBonus;
        private BattleRulesSnapshot(AbilityMagnitudes magnitudes, int terrain, int building)
        { Magnitudes = magnitudes; TerrainDefenseBonus = terrain; BuildingDefenseBonus = building; }
        internal static BattleRulesSnapshot Capture(BattleState state, AbilityMagnitudes magnitudes, HexMap map)
        {
            // Defender-side only (BattleRules.GetTotalDefenseBonus); an air defender gets none.
            int terrain = Game.Aviation.AviationRules.IsAirArmy(state.Defender) ? 0
                : BattleRules.TerrainDefenseBonus(state.BattleHex, map);
            return new BattleRulesSnapshot(magnitudes, terrain, state.DefenderBuildingDefenseBonus);
        }
    }
}
