using System.Collections.Generic;
using Game.Map;
using Game.Units;

namespace Game.Combat
{
    // The live bodies that took part in one fight, each with the army it fought in. Closing the
    // fight adds one UnitCombatRecord.BattlesSurvived to every body still alive, free and in
    // that army: a killed, captured or removed body (a spent single-use launcher, an aircraft
    // shot down, a retreating army destroyed) is not a survivor. Summoned bodies never count.
    // A tactical ground battle is one fight (BattleState.Combatants); outside it one air strike
    // pass at a hex and one AA reaction are each one fight (AviationCombatOperation).
    internal sealed class CombatEngagement
    {
        private readonly Dictionary<UnitData, ArmyData> _participants = new Dictionary<UnitData, ArmyData>();
        private bool _closed;

        internal int Count => _participants.Count;

        internal void Add(UnitData unit, ArmyData army)
        {
            if (_closed || unit == null || army == null || unit.IsSummoned || unit.HitPointsCurrent <= 0
                || _participants.ContainsKey(unit)) return;
            _participants.Add(unit, army);
        }

        internal void AddMembers(ArmyData army)
        {
            if (army == null) return;
            foreach (UnitData unit in army.Members) Add(unit, army);
        }

        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            foreach (KeyValuePair<UnitData, ArmyData> pair in _participants)
                if (pair.Key.HitPointsCurrent > 0 && !pair.Key.IsPrisoner && pair.Value.Members.Contains(pair.Key))
                    pair.Key.CombatRecord.BattlesSurvived++;
        }
    }
}
