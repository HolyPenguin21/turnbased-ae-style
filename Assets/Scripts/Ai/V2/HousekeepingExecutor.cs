using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING EXECUTOR  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  Applies one pure ReorganizationPlan to LIVE state only through canonical gameplay APIs:
    //  ArmyActions.TransferMember / ArmyActions.SwapMembers. Every operation is re-preflighted
    //  against current ownership, same-hex scope, mission claims, capacity, aviation boundaries,
    //  garrison safety and the 0-AP Housekeeping invariant. No direct roster/registry mutation.
    //
    //  Partial failure is intentionally non-transactional: successful earlier operations stay;
    //  an unexpected failure aborts the stale remainder of THIS hex only.
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
                    Fail(res, plan, $"stale plan reference (u{t.UnitKey} #{t.FromArmyId}->#{t.ToArmyId})");
                    break;
                }

                if (t.IsSwap)
                {
                    if (!analysis.UnitByKey.TryGetValue(t.SwapUnitKey, out UnitData other) || other == null)
                    {
                        Fail(res, plan, $"stale swap reference (u{t.SwapUnitKey})");
                        break;
                    }
                    if (!PreflightSwap(player, from, unit, to, other, commitments, movedUnits, out string why))
                    {
                        Fail(res, plan, $"preflight rejected swap {unit.Name} #{from.Id}<->{other.Name} #{to.Id} ({why})");
                        break;
                    }
                    if (!ArmyActions.SwapMembers(unit, from, other, to, ctx.HexSelection, out string fail))
                    {
                        Fail(res, plan, $"swap failed {unit.Name} #{from.Id}<->{other.Name} #{to.Id} ({fail})");
                        break;
                    }

                    movedUnits.Add(unit);
                    movedUnits.Add(other);
                    ctx.RecordArmyVisit(unit, from, to);
                    ctx.RecordArmyVisit(other, to, from);
                    res.Applied++;
                    res.StateChanged = true;
                    AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — swapped {unit.Name} #{from.Id} "
                        + $"<-> {other.Name} #{to.Id} ({t.Reason})");
                    continue;
                }

                if (!PreflightTransfer(player, from, to, unit, commitments, movedUnits, out string transferWhy))
                {
                    Fail(res, plan, $"preflight rejected {unit.Name} #{from.Id}->#{to.Id} ({transferWhy})");
                    break;
                }

                if (!ArmyActions.TransferMember(unit, from, to, ctx.HexSelection, out string transferFail))
                {
                    Fail(res, plan, $"transfer failed {unit.Name} #{from.Id}->#{to.Id} ({transferFail})");
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

        private static void Fail(HousekeepingExecResult res, ReorganizationPlan plan, string detail)
        {
            res.Failed++;
            res.AbortedRemainder = true;
            AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — ABORT: {detail}");
        }

        private static bool CommonPreflight(PlayerSetupData player, ArmyData a, ArmyData b,
            ActorCommitments commitments, out string why)
        {
            why = null;
            if (a == b) { why = "same container"; return false; }
            if (a.Owner != player || b.Owner != player) { why = "owner changed"; return false; }
            if (!a.Hex.Equals(b.Hex)) { why = "not same hex"; return false; }
            if (!ArmyRegistry.AllForOwner(player).Contains(a) || !ArmyRegistry.AllForOwner(player).Contains(b))
            { why = "container no longer registered"; return false; }
            if (a.IsPrison || b.IsPrison) { why = "prison container"; return false; }
            if (AviationRules.IsAirfield(a) || AviationRules.IsAirArmy(a)
                || AviationRules.IsAirfield(b) || AviationRules.IsAirArmy(b))
            { why = "aviation container"; return false; }
            if (commitments != null && (commitments.IsArmyClaimed(a.Id) || commitments.IsArmyClaimed(b.Id)))
            { why = "a container became mission-claimed"; return false; }
            return true;
        }

        private static bool PreflightTransfer(PlayerSetupData player, ArmyData from, ArmyData to, UnitData unit,
            ActorCommitments commitments, HashSet<UnitData> movedUnits, out string why)
        {
            if (!CommonPreflight(player, from, to, commitments, out why))
                return false;
            if (movedUnits.Contains(unit)) { why = "unit already moved this plan"; return false; }
            if (!from.Members.Contains(unit)) { why = "unit not in source"; return false; }
            if (unit.IsAviation) { why = "aviation unit"; return false; }
            // Canonical TransferMember charges the incoming unit's ActivationApCost when the
            // destination has already activated. Housekeeping owns a 0-AP reserve today, so such a
            // candidate is structurally illegal here rather than silently spending another axis's AP.
            if (to.HasActivatedThisTurn && unit.ActivationApCost > 0)
            { why = "would spend AP on activated destination"; return false; }
            if (!to.HasRoom) { why = "destination full"; return false; }
            if (!from.CanLeaveWithoutOvercrowding(unit)) { why = "source would overcrowd"; return false; }
            if (from.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, from, unit, allowCitadelEmergency: false))
            { why = "garrison safety floor"; return false; }
            return true;
        }

        private static bool PreflightSwap(PlayerSetupData player, ArmyData armyA, UnitData unitA,
            ArmyData armyB, UnitData unitB, ActorCommitments commitments, HashSet<UnitData> movedUnits, out string why)
        {
            if (!CommonPreflight(player, armyA, armyB, commitments, out why))
                return false;
            // Planner only proposes field-field composition swaps. Keeping that boundary here avoids
            // inventing a second simultaneous garrison-floor replacement rule in Housekeeping.
            if (armyA.IsGarrison || armyB.IsGarrison) { why = "garrison swap not owned by housekeeping"; return false; }
            if (movedUnits.Contains(unitA) || movedUnits.Contains(unitB)) { why = "swap member already moved this plan"; return false; }
            if (!armyA.Members.Contains(unitA) || !armyB.Members.Contains(unitB)) { why = "swap membership changed"; return false; }
            if (unitA.IsAviation || unitB.IsAviation) { why = "aviation unit"; return false; }
            // unitA enters B, unitB enters A. Zero-AP invariant mirrors ArmyActions.CanSwapMembers.
            if ((armyB.HasActivatedThisTurn && unitA.ActivationApCost > 0)
                || (armyA.HasActivatedThisTurn && unitB.ActivationApCost > 0))
            { why = "swap would spend AP on activated destination"; return false; }
            if (!ArmyActions.CanSwapMembers(unitA, armyA, unitB, armyB, out string fail))
            { why = fail; return false; }
            return true;
        }
    }
}
