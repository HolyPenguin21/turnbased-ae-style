using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Combat
{
    // Authoritative mutable state for one tactical ground encounter. UI owns presentation only;
    // BattleEngine owns mutations/round progression against this object.
    public sealed class BattleState
    {
        public HexCoord BattleHex { get; }
        public ArmyData Attacker { get; }
        public ArmyData Defender { get; }
        public BattleGrid Grid { get; }
        public int BattleSeed { get; }

        public int Round { get; set; } = 1;
        public List<UnitData> TurnOrder { get; set; } = new List<UnitData>();
        public int TurnIndex { get; set; }
        public ArmyData RetreatingArmy { get; set; }

        public BattleState(HexCoord battleHex, ArmyData attacker, ArmyData defender, BattleGrid grid, int battleSeed)
        {
            BattleHex = battleHex;
            Attacker = attacker;
            Defender = defender;
            Grid = grid;
            BattleSeed = battleSeed;
        }

        public ArmyData OwningArmy(UnitData unit)
        {
            if (unit == null)
                return null;
            if (Attacker != null && Attacker.Members.Contains(unit))
                return Attacker;
            if (Defender != null && Defender.Members.Contains(unit))
                return Defender;
            return null;
        }

        public ArmyData OpposingArmy(ArmyData army) =>
            army == Attacker ? Defender : army == Defender ? Attacker : null;
    }
}
