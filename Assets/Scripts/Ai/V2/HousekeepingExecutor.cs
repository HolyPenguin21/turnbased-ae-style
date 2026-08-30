using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING EXECUTOR  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  Runs one ReorganizationPlan against the LIVE world through the canonical
    //  ArmyActions.TransferMember only — no direct ArmyData.Members / registry / garrison-list
    //  manipulation. Every transfer is re-preflighted against live state immediately before it
    //  runs (capacity, ownership, same hex, membership, garrison floor, protection, no
    //  double-move). Same-hex is re-asserted here as defence in depth: a cross-hex transfer would
    //  turn Step 8C from reorganisation into movement.
    //
    //  FAILURE SEMANTICS (design doc §17): no invented rollback over the gameplay API. A transfer
    //  that already succeeded stays. On an unexpected failure the rest of THIS hex's plan is
    //  abandoned (its capacity/membership assumptions are now stale); other hexes are independent.
    //  StateChanged is set only after a real successful gameplay mutation.
    // ===========================================================================================
    public sealed class HousekeepingExecResult
    {
        public bool StateChanged;
        public int Applied;
        public int Failed;
        public bool AbortedRemainder;
    }

    internal static class HousekeepingExecutor
    {
        public static HousekeepingExecResult Execute(ReorganizationPlan plan, ArmyReorgAnalysis analysis,
            PlayerSetupData player, AiTurnContext ctx, ActorCommitments commitments)
        {
            var res = new HousekeepingExecResult();
            if (plan == null || plan.IsEmpty || analysis == null || player == null || ctx == null)
                return res;

            var movedUnits = new HashSet<UnitData>();

            foreach (PlannedTransfer t in plan.Transfers)
            {
                if (!analysis.ArmyById.TryGetValue(t.FromArmyId, out ArmyData from)
                    || !analysis.ArmyById.TryGetValue(t.ToArmyId, out ArmyData to)
                    || !analysis.UnitByKey.TryGetValue(t.UnitKey, out UnitData unit)
                    || from == null || to == null || unit == null)
                {
                    res.Failed++;
                    res.AbortedRemainder = true;
                    AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — ABORT: stale plan reference "
                        + $"(u{t.UnitKey} #{t.FromArmyId}->#{t.ToArmyId})");
                    break;
                }

                if (!Preflight(player, from, to, unit, commitments, movedUnits, out string why))
                {
                    res.Failed++;
                    res.AbortedRemainder = true;
                    AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — ABORT: preflight rejected "
                        + $"{unit.Name} #{from.Id}->#{to.Id} ({why})");
                    break;
                }

                if (!ArmyActions.TransferMember(unit, from, to, ctx.HexSelection, out string fail))
                {
                    res.Failed++;
                    res.AbortedRemainder = true;
                    AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — ABORT: transfer failed "
                        + $"{unit.Name} #{from.Id}->#{to.Id} ({fail})");
                    break;
                }

                movedUnits.Add(unit);
                ctx.RecordArmyVisit(unit, from, to);
                res.Applied++;
                res.StateChanged = true;
                AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — moved {unit.Name} "
                    + $"#{from.Id}->#{to.Id} ({t.Reason})");
            }

            return res;
        }

        private static bool Preflight(PlayerSetupData player, ArmyData from, ArmyData to, UnitData unit,
            ActorCommitments commitments, HashSet<UnitData> movedUnits, out string why)
        {
            why = null;
            if (from == to) { why = "same container"; return false; }
            if (movedUnits.Contains(unit)) { why = "unit already moved this plan"; return false; }
            if (from.Owner != player || to.Owner != player) { why = "owner changed"; return false; }
            if (!from.Hex.Equals(to.Hex)) { why = "not same hex"; return false; }
            if (!ArmyRegistry.AllForOwner(player).Contains(from) || !ArmyRegistry.AllForOwner(player).Contains(to))
            { why = "container no longer registered"; return false; }
            if (!from.Members.Contains(unit)) { why = "unit not in source"; return false; }
            if (to.IsPrison) { why = "destination is a prison"; return false; }
            if (commitments != null && (commitments.IsArmyClaimed(from.Id) || commitments.IsArmyClaimed(to.Id)))
            { why = "a container became mission-claimed"; return false; }
            if (!to.IsAirfield && !to.HasRoom) { why = "destination full"; return false; }
            if (!from.CanLeaveWithoutOvercrowding(unit)) { why = "source would overcrowd"; return false; }
            if (from.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, from, unit, allowCitadelEmergency: false))
            { why = "garrison safety floor"; return false; }
            return true;
        }
    }
}
