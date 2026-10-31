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

        // Capture map presentation while both army markers still exist and the visible
        // representative/FOW decision is authoritative. The combat engine can remove their
        // units and markers long before the battle result popup is acknowledged.
        private ArmyDestructionEffect.Appearance _attackerAppearance;
        private ArmyDestructionEffect.Appearance _defenderAppearance;
        private bool _attackerDestructionPending;
        private bool _defenderDestructionPending;

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
            _attackerAppearance = ArmyDestructionEffect.Capture(attacker?.Controller?.Visual);
            _defenderAppearance = ArmyDestructionEffect.Capture(defender?.Controller?.Visual);
            DefenderBuildingDefenseBonus = DisruptionRules.BuildingDefenseAtEncounterStart(
                battleHex, defender, attacker);
            BattleEncounterCoordinator.RegisterEncounter(this);
        }

        // Called only after a real battle deletion has committed, not for normal transfers or
        // an empty garrison that the map intentionally keeps registered.
        internal void QueueDestroyedArmy(ArmyData army)
        {
            if (army == null || ArmyRegistry.IsRegistered(army) || army.Members.Count != 0)
                return;
            if (ReferenceEquals(army, Attacker)) _attackerDestructionPending = true;
            else if (ReferenceEquals(army, Defender)) _defenderDestructionPending = true;
        }

        // The outcome popup may outlive the real ArmyController by several seconds. Start the
        // visual on the map only AFTER dismissing that popup; no delay to combat finalization.
        internal void PresentDestroyedArmies()
        {
            if (_attackerDestructionPending) ArmyDestructionEffect.Play(_attackerAppearance);
            if (_defenderDestructionPending) ArmyDestructionEffect.Play(_defenderAppearance);
            _attackerDestructionPending = _defenderDestructionPending = false;
            _attackerAppearance = _defenderAppearance = null;
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
