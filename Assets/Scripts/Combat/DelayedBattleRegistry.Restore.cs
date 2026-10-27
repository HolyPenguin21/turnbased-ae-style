using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;

namespace Game.Combat
{
    [Serializable] public sealed class DelayedBattleState
    {
        public HexCoord Hex;
        public List<int> ParticipantArmyIds = new List<int>();
    }
    public static partial class DelayedBattleRegistry
    {
        public static List<DelayedBattleState> CaptureState()
        {
            var result = new List<DelayedBattleState>();
            foreach (var battle in Pending)
            {
                var state = new DelayedBattleState { Hex = battle.Hex };
                if (battle.Participants != null) foreach (var army in battle.Participants)
                    state.ParticipantArmyIds.Add(army.Id);
                result.Add(state);
            }
            return result;
        }
        public static void RestoreState(IReadOnlyList<DelayedBattleState> state, Func<int, ArmyData> resolveArmy)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var pending = new List<PendingBattle>(); var hexes = new HashSet<HexCoord>();
            foreach (var entry in state)
            {
                if (entry?.ParticipantArmyIds == null || !hexes.Add(entry.Hex)) throw new ArgumentException("Invalid or duplicate delayed battle.");
                var participants = new List<ArmyData>(); var ids = new HashSet<int>();
                foreach (int id in entry.ParticipantArmyIds)
                {
                    if (!ids.Add(id)) throw new ArgumentException("Duplicate battle participant.");
                    participants.Add(StateReference.Army(id, resolveArmy));
                }
                pending.Add(new PendingBattle { Hex = entry.Hex, Participants = participants });
            }
            Pending.Clear(); Pending.AddRange(pending);
        }
    }
}
