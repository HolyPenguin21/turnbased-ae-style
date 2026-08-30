using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  REORGANIZATION PLAN  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  The complete, ordered, SAME-HEX transfer plan for one LocalForceGroup. Produced by
    //  ArmyReorganizationPlanner as a pure value — it mutates NO game state. HousekeepingExecutor
    //  preflights it against the live world and runs each transfer through the canonical
    //  ArmyActions.TransferMember. Every transfer's source and destination are on this one hex by
    //  construction (the executor re-checks it anyway — defence in depth).
    // ===========================================================================================
    public readonly struct PlannedTransfer
    {
        public readonly int UnitKey;
        public readonly int FromArmyId;
        public readonly int ToArmyId;
        public readonly string Reason;

        public PlannedTransfer(int unitKey, int fromArmyId, int toArmyId, string reason)
        {
            UnitKey = unitKey;
            FromArmyId = fromArmyId;
            ToArmyId = toArmyId;
            Reason = reason;
        }
    }

    public sealed class ReorganizationPlan
    {
        public int Q;
        public int R;
        public readonly List<PlannedTransfer> Transfers = new List<PlannedTransfer>();
        // ArmyId -> the unit keys the planner expects that container to hold once the plan runs.
        public readonly Dictionary<int, List<int>> ExpectedMembership = new Dictionary<int, List<int>>();

        public bool IsEmpty => Transfers.Count == 0;
        public string HexKey => Q + "," + R;

        public string DebugSummary()
        {
            if (IsEmpty)
                return $"({Q},{R}) no-op";
            IEnumerable<string> ops = Transfers.Select(t => $"u{t.UnitKey}:#{t.FromArmyId}->#{t.ToArmyId}");
            return $"({Q},{R}) {Transfers.Count} transfer(s): {string.Join(", ", ops)}";
        }
    }
}
