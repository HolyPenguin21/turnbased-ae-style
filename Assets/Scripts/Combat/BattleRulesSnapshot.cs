using Game.Map;

namespace Game.Combat
{
    // Authored/environment rules freeze at construction: a battle keeps the weather of its hex
    // at the moment it starts until it ends (map browsing, repeated attacks and tactical rounds
    // never change it).
    public readonly struct BattleRulesSnapshot
    {
        public readonly AbilityMagnitudes Magnitudes;
        public readonly int TerrainDefenseBonus;
        public readonly int BuildingDefenseBonus;
        public readonly BattleEnvironment Environment;
        private BattleRulesSnapshot(AbilityMagnitudes magnitudes, int terrain, int building, BattleEnvironment environment)
        { Magnitudes = magnitudes; TerrainDefenseBonus = terrain; BuildingDefenseBonus = building; Environment = environment; }
        internal static BattleRulesSnapshot Capture(BattleState state, AbilityMagnitudes magnitudes, HexMap map)
        {
            BattleEnvironment environment = BattleEnvironment.At(state.BattleHex, map);
            // Defender-side only (BattleRules.GetTotalDefenseBonus); an air defender gets none.
            // A sandstorm removes both hex parts once; Disruption may already have zeroed the
            // building part — 0 stays 0, never negative.
            int terrain = environment.InSandstorm || Game.Aviation.AviationRules.IsAirArmy(state.Defender) ? 0
                : BattleRules.TerrainDefenseBonus(state.BattleHex, map);
            int building = environment.InSandstorm ? 0 : state.DefenderBuildingDefenseBonus;
            return new BattleRulesSnapshot(magnitudes, terrain, building, environment);
        }
    }
}
