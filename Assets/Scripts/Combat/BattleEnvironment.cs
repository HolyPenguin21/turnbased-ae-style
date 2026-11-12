using Game.HexGrid;
using Game.Map;

namespace Game.Combat
{
    // The weather of one battle, decided by the BATTLE hex (never the attacker's origin or an
    // army's preview position) and frozen with the encounter's rules (BattleRulesSnapshot). A
    // hex under any storm is one storm: overlapping storms never double a penalty. A region
    // without storms is simply the default (no modifier) through the same context.
    //   - the defender's hex bonuses (terrain + own building) are 0 — units' own Defense,
    //     equipment, abilities, HP and Fate are untouched;
    //   - every unit's attack Range is reduced by 1, never below 1 (BattleRules.EffectiveAttackRange).
    public readonly struct BattleEnvironment : System.IEquatable<BattleEnvironment>
    {
        public static readonly BattleEnvironment Clear = default;
        public static readonly BattleEnvironment Sandstorm = new BattleEnvironment(true);

        public readonly bool InSandstorm;

        private BattleEnvironment(bool inSandstorm) { InSandstorm = inSandstorm; }

        public static BattleEnvironment At(HexCoord battleHex, SandstormSnapshot weather) =>
            weather != null && weather.Contains(battleHex) ? Sandstorm : Clear;

        // The map's published weather when a map is at hand; otherwise the match's weather owner
        // (the same snapshot, published to the map by SandstormState).
        public static BattleEnvironment At(HexCoord battleHex, HexMap map) =>
            At(battleHex, map != null ? map.Weather : SandstormState.Snapshot);

        public static BattleEnvironment At(HexCoord battleHex) => At(battleHex, SandstormState.Snapshot);

        // The hex part of a defender's protection under this weather.
        public int HexDefense(int terrainBonus, int buildingBonus) =>
            InSandstorm ? 0 : terrainBonus + buildingBonus;

        public float HexDefense(float hexBonus) => InSandstorm ? 0f : hexBonus;

        public int AttackRange(int currentRange) => BattleRules.EffectiveAttackRange(currentRange, this);

        public bool Equals(BattleEnvironment other) => InSandstorm == other.InSandstorm;
        public override bool Equals(object obj) => obj is BattleEnvironment other && Equals(other);
        public override int GetHashCode() => InSandstorm ? 1 : 0;
        public override string ToString() => InSandstorm ? "sandstorm" : "clear";
    }
}
