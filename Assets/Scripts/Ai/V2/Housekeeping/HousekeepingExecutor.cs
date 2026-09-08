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
    //  Separate successful operations stay applied if a later operation fails. A whole-fold is
    //  one explicit atomic batch: all of its members pass final-roster preflight and move together,
    //  or none move. Any unexpected failure still aborts the stale remainder of THIS hex.
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

            for (int operationIndex = 0; operationIndex < plan.Transfers.Count; operationIndex++)
            {
                PlannedTransfer t = plan.Transfers[operationIndex];
                if (!analysis.ArmyById.TryGetValue(t.FromArmyId, out ArmyData from)
                    || !analysis.ArmyById.TryGetValue(t.ToArmyId, out ArmyData to)
                    || !analysis.UnitByKey.TryGetValue(t.UnitKey, out UnitData unit)
                    || from == null || to == null || unit == null)
                {
                    Fail(res, plan, $"stale plan reference (u{t.UnitKey} #{t.FromArmyId}->#{t.ToArmyId})");
                    break;
                }

                if (t.IsWholeFold)
                {
                    var batchTransfers = new List<PlannedTransfer> { t };
                    while (operationIndex + batchTransfers.Count < plan.Transfers.Count)
                    {
                        PlannedTransfer next = plan.Transfers[operationIndex + batchTransfers.Count];
                        if (!next.IsWholeFold || next.FromArmyId != t.FromArmyId
                            || next.ToArmyId != t.ToArmyId)
                            break;
                        batchTransfers.Add(next);
                    }

                    var batchUnits = new List<UnitData>();
                    bool staleBatch = false;
                    foreach (PlannedTransfer bt in batchTransfers)
                    {
                        if (!analysis.UnitByKey.TryGetValue(bt.UnitKey, out UnitData member)
                            || member == null)
                        {
                            Fail(res, plan, $"stale whole-fold member u{bt.UnitKey}");
                            staleBatch = true;
                            break;
                        }
                        batchUnits.Add(member);
                    }
                    if (staleBatch)
                        break;

                    if (!PreflightWholeFold(player, from, to, batchUnits, commitments,
                            movedUnits, out string foldWhy))
                    {
                        Fail(res, plan, $"preflight rejected whole-fold #{from.Id}->#{to.Id} ({foldWhy})");
                        break;
                    }
                    if (!ArmyActions.TransferMembersAtomic(
                            batchUnits, from, to, ctx.HexSelection, out string foldFail))
                    {
                        Fail(res, plan, $"whole-fold failed #{from.Id}->#{to.Id} ({foldFail})");
                        break;
                    }

                    foreach (UnitData member in batchUnits)
                    {
                        movedUnits.Add(member);
                        ctx.RecordArmyVisit(member, from, to);
                    }
                    res.Applied += batchUnits.Count;
                    res.StateChanged = true;
                    AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — whole-folded "
                        + $"{batchUnits.Count} member(s) #{from.Id}->#{to.Id} atomically ({t.Reason})");
                    operationIndex += batchTransfers.Count - 1;
                    continue;
                }

                if (t.IsReorder)
                {
                    // §7 — zero-AP commander promotion. No transfer, no AP, membership unchanged.
                    if (from.Owner != player || !ArmyRegistry.AllForOwner(player).Contains(from))
                    {
                        Fail(res, plan, $"reorder rejected #{from.Id} ({unit.Name}) — container no longer owned/registered");
                        break;
                    }
                    if (commitments != null && commitments.IsArmyClaimed(from.Id))
                    {
                        Fail(res, plan, $"reorder rejected #{from.Id} — became mission-claimed");
                        break;
                    }
                    int oldCap = from.Capacity;
                    if (!from.TryReorderCommander(unit, out string reorderFail))
                    {
                        Fail(res, plan, $"reorder failed #{from.Id} ({unit.Name}) ({reorderFail})");
                        break;
                    }
                    res.Applied++;
                    res.StateChanged = true;
                    AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — commander reorder army #{from.Id} "
                        + $"-> {unit.Name} capacity {oldCap}->{from.Capacity} ({t.Reason})");
                    continue;
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
                    // §16 — a hero-for-body/hero swap that leads a formation reads with the hero's role.
                    string swapRole = unit.IsHero
                        ? $" role={Game.Ai.V2.HeroRoleEvaluator.Classify(unit)}" : "";
                    AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — swapped {unit.Name} #{from.Id} "
                        + $"<-> {other.Name} #{to.Id}{swapRole} ({t.Reason})");
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
                string moveRole = unit.IsHero
                    ? $" role={Game.Ai.V2.HeroRoleEvaluator.Classify(unit)}" : "";
                AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — moved {unit.Name} "
                    + $"#{from.Id}->#{to.Id}{moveRole} ({t.Reason})");
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

        private static bool PreflightWholeFold(PlayerSetupData player, ArmyData from, ArmyData to,
            IReadOnlyList<UnitData> units, ActorCommitments commitments,
            HashSet<UnitData> movedUnits, out string why)
        {
            if (!CommonPreflight(player, from, to, commitments, out why))
                return false;
            if (from.IsGarrison)
            { why = "whole-fold cannot consume a garrison"; return false; }
            if (units == null || units.Count == 0)
            { why = "empty whole-fold"; return false; }
            if (units.Any(u => u == null || movedUnits.Contains(u)))
            { why = "whole-fold member already moved or missing"; return false; }
            if (units.Any(u => u.IsAviation))
            { why = "aviation unit"; return false; }
            if (to.HasActivatedThisTurn && units.Any(u => u.ActivationApCost > 0))
            { why = "whole-fold would spend AP on activated destination"; return false; }
            if (!ArmyActions.CanTransferMembers(units, from, to, out why))
                return false;
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
            // Mirror ArmyActions.TransferMember's projected-roster capacity rule exactly. A hero
            // may legally join a currently-full no-hero army because its CommandRating raises the
            // resulting capacity; using to.HasRoom here recreated the planner/runtime mismatch.
            var projected = new List<UnitData>(to.Members) { unit };
            if (ArmyData.ComputeCapacity(projected, to.IsGarrison) < projected.Count)
            { why = "destination would exceed projected capacity"; return false; }
            if (!from.CanLeaveWithoutOvercrowding(unit)) { why = "source would overcrowd"; return false; }
            if (from.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, from, unit, allowCitadelEmergency: false))
            { why = "garrison safety floor"; return false; }
            // §P1 — a garrison that currently holds a real defensive power reserve must not be
            // dropped below it by a zero-AP structural move.
            if (from.IsGarrison)
            {
                float beforePower = AiPower.EffectiveArmyPower(from.Members);
                if (beforePower >= AiConfigV2.housekeepingGarrisonReservePower
                    && AiPower.EffectiveArmyPower(from.Members.Where(m => m != unit))
                        < AiConfigV2.housekeepingGarrisonReservePower)
                { why = "garrison power reserve"; return false; }
            }
            return true;
        }

        private static bool PreflightSwap(PlayerSetupData player, ArmyData armyA, UnitData unitA,
            ArmyData armyB, UnitData unitB, ActorCommitments commitments, HashSet<UnitData> movedUnits, out string why)
        {
            if (!CommonPreflight(player, armyA, armyB, commitments, out why))
                return false;
            // A garrison may participate on one side when a hero leaves it. The field-side member
            // may be either a body (forming a previously heroless army) OR another hero (returning
            // a SupportOperator to base while a CombatLeader takes command). Never allow a
            // non-hero to be the member leaving the garrison through this Housekeeping-owned path.
            bool garrisonA = armyA.IsGarrison;
            bool garrisonB = armyB.IsGarrison;
            if (garrisonA && garrisonB) { why = "garrison<->garrison swap not owned by housekeeping"; return false; }
            if (garrisonA || garrisonB)
            {
                ArmyData garr = garrisonA ? armyA : armyB;
                UnitData leaving = garrisonA ? unitA : unitB;   // garrison -> field
                UnitData entering = garrisonA ? unitB : unitA;  // field  -> garrison
                if (!leaving.IsHero)
                { why = "garrison swap must send a hero out"; return false; }
                if (!AiArmyRoles.CanSpareGarrisonMember(player, garr, leaving, allowCitadelEmergency: false))
                { why = "garrison hero release breaks security"; return false; }
                // §P1 — a garrison that currently holds its defensive power reserve must not be
                // dropped below it by the swap either (strong hero out, weak body in).
                float garrBefore = AiPower.EffectiveArmyPower(garr.Members);
                if (garrBefore >= AiConfigV2.housekeepingGarrisonReservePower)
                {
                    var garrAfter = garr.Members.Where(m => m != leaving).ToList();
                    garrAfter.Add(entering);
                    if (AiPower.EffectiveArmyPower(garrAfter) < AiConfigV2.housekeepingGarrisonReservePower)
                    { why = "garrison power reserve"; return false; }
                }
            }
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
