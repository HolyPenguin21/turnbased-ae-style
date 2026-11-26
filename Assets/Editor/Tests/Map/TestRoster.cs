using Game.Map;
using Game.Units;

namespace Game.EditorTests
{
    // Test state preparation only: puts a unit into an army that is already registered, the
    // way a fixture used to write Members directly. Gameplay changes a live roster through
    // ArmyActions; ArmyData rejects direct edits of a registered roster.
    internal static class TestRoster
    {
        internal static void Add(ArmyData army, UnitData unit) => army.AppendRosterMember(unit);
        internal static void AddSorted(ArmyData army, UnitData unit) => army.InsertRosterMemberSorted(unit);
    }
}
