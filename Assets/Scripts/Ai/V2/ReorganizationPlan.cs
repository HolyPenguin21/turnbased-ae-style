using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // One ordered same-hex reorganisation operation. Normally it is a one-way transfer. For a
    // full/full composition improvement it can encode one canonical 1-for-1 SwapMembers action;
    // SwapUnitKey is the member currently in ToArmyId that travels the opposite direction.
    public readonly struct PlannedTransfer
    {
        public readonly int UnitKey;
        public readonly int FromArmyId;
        public readonly int ToArmyId;
        public readonly int SwapUnitKey;
        public readonly string Reason;

        public bool IsSwap => SwapUnitKey >= 0;

        public PlannedTransfer(int unitKey, int fromArmyId, int toArmyId, string reason)
            : this(unitKey, fromArmyId, toArmyId, -1, reason) { }

        private PlannedTransfer(int unitKey, int fromArmyId, int toArmyId, int swapUnitKey, string reason)
        {
            UnitKey = unitKey;
            FromArmyId = fromArmyId;
            ToArmyId = toArmyId;
            SwapUnitKey = swapUnitKey;
            Reason = reason;
        }

        public static PlannedTransfer Swap(int unitAKey, int armyAId, int unitBKey, int armyBId, string reason) =>
            new PlannedTransfer(unitAKey, armyAId, armyBId, unitBKey, reason);
    }

    public sealed class ReorganizationPlan
    {
        public int Q;
        public int R;
        // Historical name kept to avoid churn: entries are ordered reorg operations and may be
        // either one-way transfers or direct swaps (PlannedTransfer.IsSwap).
        public readonly List<PlannedTransfer> Transfers = new List<PlannedTransfer>();
        public readonly Dictionary<int, List<int>> ExpectedMembership = new Dictionary<int, List<int>>();

        public bool IsEmpty => Transfers.Count == 0;
        public string HexKey => Q + "," + R;

        public string DebugSummary()
        {
            if (IsEmpty)
                return $"({Q},{R}) no-op";
            IEnumerable<string> ops = Transfers.Select(t => t.IsSwap
                ? $"swap u{t.UnitKey}:#{t.FromArmyId}<->u{t.SwapUnitKey}:#{t.ToArmyId}"
                : $"u{t.UnitKey}:#{t.FromArmyId}->#{t.ToArmyId}");
            return $"({Q},{R}) {Transfers.Count} reorg operation(s): {string.Join(", ", ops)}";
        }
    }
}
