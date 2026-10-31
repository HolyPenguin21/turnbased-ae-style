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
        // One encounter snapshot shared by damage, tactical AI and defence presentation.
        public int DefenderBuildingDefenseBonus { get; }

        internal BattleEngine Engine;
        internal AbilityMagnitudes EncounterMagnitudes = AbilityMagnitudes.Default;
        internal bool FinalizationApplied, Acknowledged;
        internal BattleEncounterFinalization Finalization;
        public int Round { get; internal set; } = 1;
        private List<UnitData> _turnOrder = new List<UnitData>();
        public IReadOnlyList<UnitData> TurnOrder => _turnOrder.AsReadOnly();
        internal List<UnitData> MutableTurnOrder { get => _turnOrder; set => _turnOrder = value ?? new List<UnitData>(); }
        public int TurnIndex { get; internal set; }
        public ArmyData RetreatingArmy { get; internal set; }

        public BattleState(HexCoord battleHex, ArmyData attacker, ArmyData defender, BattleGrid grid, int battleSeed)
        {
            BattleHex = battleHex;
            Attacker = attacker;
            Defender = defender;
            Grid = grid;
            BattleSeed = battleSeed;
            DefenderBuildingDefenseBonus = DisruptionRules.BuildingDefenseAtEncounterStart(
                battleHex, defender, attacker);
            BattleEncounterCoordinator.RegisterEncounter(this);
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
