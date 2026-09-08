using System.Collections.Generic;
using System.Globalization;
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
        public readonly bool IsWholeFold;
        public readonly string Reason;

        // -2 = a zero-AP commander reorder inside one container (FromArmyId == ToArmyId, UnitKey
        // is the hero to promote to first). Not a member transfer, not a swap.
        private const int ReorderSentinel = -2;

        public bool IsSwap => SwapUnitKey >= 0;
        public bool IsReorder => SwapUnitKey == ReorderSentinel;

        public PlannedTransfer(int unitKey, int fromArmyId, int toArmyId, string reason)
            : this(unitKey, fromArmyId, toArmyId, -1, false, reason) { }

        private PlannedTransfer(int unitKey, int fromArmyId, int toArmyId, int swapUnitKey,
            bool isWholeFold, string reason)
        {
            UnitKey = unitKey;
            FromArmyId = fromArmyId;
            ToArmyId = toArmyId;
            SwapUnitKey = swapUnitKey;
            IsWholeFold = isWholeFold;
            Reason = reason;
        }

        public static PlannedTransfer Swap(int unitAKey, int armyAId, int unitBKey, int armyBId, string reason) =>
            new PlannedTransfer(unitAKey, armyAId, armyBId, unitBKey, false, reason);

        public static PlannedTransfer Reorder(int heroKey, int armyId, string reason) =>
            new PlannedTransfer(heroKey, armyId, armyId, ReorderSentinel, false, reason);

        public static PlannedTransfer WholeFold(int unitKey, int fromArmyId, int toArmyId,
            string reason) =>
            new PlannedTransfer(unitKey, fromArmyId, toArmyId, -1, true, reason);
    }

    public sealed class ReorganizationPlan
    {
        public int Q;
        public int R;
        // Historical name kept to avoid churn: entries are ordered reorg operations and may be
        // either one-way transfers or direct swaps (PlannedTransfer.IsSwap).
        public readonly List<PlannedTransfer> Transfers = new List<PlannedTransfer>();
        public readonly Dictionary<int, List<int>> ExpectedMembership = new Dictionary<int, List<int>>();
        // Diagnostic-only snapshots of the canonical strongest-first field profile. Empty reusable
        // shells are intentionally absent; these values never participate in execution.
        public readonly List<float> BeforeFormationStrengths = new List<float>();
        public readonly List<float> AfterFormationStrengths = new List<float>();

        public bool IsEmpty => Transfers.Count == 0;
        public string HexKey => Q + "," + R;

        public string DebugSummary()
        {
            string before = FormatProfile(BeforeFormationStrengths);
            string after = FormatProfile(AfterFormationStrengths);
            if (IsEmpty)
                return $"({Q},{R}) no-op profile {before}";
            IEnumerable<string> ops = Transfers.Select(t => t.IsReorder
                    ? $"commander u{t.UnitKey}:#{t.FromArmyId}"
                    : t.IsWholeFold
                        ? $"atomic-fold #{t.FromArmyId}->#{t.ToArmyId}"
                        : t.IsSwap
                            ? $"swap u{t.UnitKey}:#{t.FromArmyId}<->u{t.SwapUnitKey}:#{t.ToArmyId}"
                            : $"move #{t.FromArmyId}->#{t.ToArmyId} ({t.Reason})")
                .GroupBy(label => label)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            return $"({Q},{R}) profile {before}->{after}; {Transfers.Count} member operation(s): {string.Join(", ", ops)}";
        }

        private static string FormatProfile(IEnumerable<float> values) =>
            "[" + string.Join(",", (values ?? Enumerable.Empty<float>())
                .Select(v => v.ToString("0.##", CultureInfo.InvariantCulture))) + "]";
    }
}
