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
            int turn = ctx.TurnNumber;

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

                    if (!PreflightWholeFold(player, turn, from, to, batchUnits, commitments,
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

                    WorldDeltaLifecycle.CommitMutation();
                    foreach (UnitData member in batchUnits)
                    {
                        movedUnits.Add(member);
                    }
                    res.Applied += batchUnits.Count;
                    res.StateChanged = true;
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
                    // T05 — the same contract the planner read: a claimed container reorders only
                    // when its operation allows commander promotion.
                    ArmyMutationContract reorderContract =
                        ArmyReorgAnalyzer.MutationContractFor(player, turn, from, commitments);
                    if (reorderContract != null && !reorderContract.MayReorderCommander)
                    {
                        Fail(res, plan, $"reorder rejected #{from.Id} — mission contract "
                            + $"{reorderContract.Label} keeps its commander");
                        break;
                    }
                    if (from.IsGarrison)
                    {
                        // Garrison order is canonical (CommandRating), restored in one mutation;
                        // an already-canonical roster is a no-op: no commit, no StateChanged.
                        if (from.NormalizeGarrisonOrder())
                        {
                            WorldDeltaLifecycle.CommitMutation();
                            res.Applied++;
                            res.StateChanged = true;
                        }
                        continue;
                    }
                    if (!from.TryReorderCommander(unit, out string reorderFail))
                    {
                        Fail(res, plan, $"reorder failed #{from.Id} ({unit.Name}) ({reorderFail})");
                        break;
                    }
                    WorldDeltaLifecycle.CommitMutation();
                    res.Applied++;
                    res.StateChanged = true;
                    continue;
                }

                if (t.IsSwap)
                {
                    if (!analysis.UnitByKey.TryGetValue(t.SwapUnitKey, out UnitData other) || other == null)
                    {
                        Fail(res, plan, $"stale swap reference (u{t.SwapUnitKey})");
                        break;
                    }
                    if (!PreflightSwap(player, turn, from, unit, to, other, commitments, movedUnits, out string why))
                    {
                        Fail(res, plan, $"preflight rejected swap {unit.Name} #{from.Id}<->{other.Name} #{to.Id} ({why})");
                        break;
                    }
                    if (!ArmyActions.SwapMembers(unit, from, other, to, ctx.HexSelection, out string fail))
                    {
                        Fail(res, plan, $"swap failed {unit.Name} #{from.Id}<->{other.Name} #{to.Id} ({fail})");
                        break;
                    }

                    WorldDeltaLifecycle.CommitMutation();
                    movedUnits.Add(unit);
                    movedUnits.Add(other);
                    res.Applied++;
                    res.StateChanged = true;
                    continue;
                }

                if (!PreflightTransfer(player, turn, from, to, unit, commitments, movedUnits, out string transferWhy))
                {
                    Fail(res, plan, $"preflight rejected {unit.Name} #{from.Id}->#{to.Id} ({transferWhy})");
                    break;
                }

                if (!ArmyActions.TransferMember(unit, from, to, ctx.HexSelection, out string transferFail))
                {
                    Fail(res, plan, $"transfer failed {unit.Name} #{from.Id}->#{to.Id} ({transferFail})");
                    break;
                }

                WorldDeltaLifecycle.CommitMutation();
                movedUnits.Add(unit);
                res.Applied++;
                res.StateChanged = true;
            }

            return res;
        }

        private static void Fail(HousekeepingExecResult res, ReorganizationPlan plan, string detail)
        {
            res.Failed++;
            res.AbortedRemainder = true;
            AiDebugLog.Write($"[AI][V2]   housekeeping {plan.HexKey} — ABORT: {detail}");
        }

        // T05 — `a` gives, `b` receives. The giver must be free (a claimed container never donates);
        // the receiver may be claimed only while its live contract admits inbound members. Swaps
        // pass inboundOnly=false: both sides give, so both must be free.
        private static bool CommonPreflight(PlayerSetupData player, int turn, ArmyData a, ArmyData b,
            ActorCommitments commitments, out string why, bool inboundOnly = true,
            UnitData released = null)
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
            ArmyMutationContract giver = ArmyReorgAnalyzer.MutationContractFor(player, turn, a, commitments);
            ArmyMutationContract receiver = ArmyReorgAnalyzer.MutationContractFor(player, turn, b, commitments);
            // ATK-F03 — the one outbound exception: a preparation host lets a non-commander hero
            // go to a free container (the planner chose it as an excess hero).
            bool heroRelease = giver != null && giver.MayReleaseExcessHeroes && inboundOnly
                && released != null && released.IsHero && released != a.Commander && receiver == null;
            // 2026-10-01 — and a body its frozen target roster does not contain (the planner chose
            // it for a missing position's source; ReorgViability.PreparationRosterWaste).
            bool bodyRelease = giver != null && giver.MayReleaseExcessHeroes && inboundOnly
                && receiver == null && giver.MayReleaseBody(a, released);
            if (giver != null && !heroRelease && !bodyRelease)
            { why = "source is mission-claimed"; return false; }
            // A facility operator of a protected host may only stay home: the one local-garrison rule.
            if (giver != null && heroRelease && LocalOperatorRelease.IsSoleOperator(player, a.Hex, released)
                && !LocalOperatorRelease.MayLeaveForLocalGarrison(player, a, released, b, out why))
                return false;
            if (receiver != null && (!inboundOnly || !receiver.MayReceive))
            { why = $"destination mission contract {receiver.Label} admits no inbound"; return false; }
            return true;
        }

        // T05 — live twin of the planner's CanAccept movement floor: a route-bound operation
        // (contract.KeepsMovement) must not be slowed by the members joining it.
        private static bool KeepsOperationMovement(PlayerSetupData player, int turn, ArmyData to,
            IReadOnlyList<UnitData> incoming, ActorCommitments commitments, out string why)
        {
            why = null;
            ArmyMutationContract contract = ArmyReorgAnalyzer.MutationContractFor(player, turn, to, commitments);
            if (contract == null || !contract.KeepsMovement || to.Members.Count == 0)
                return true;
            var projected = new List<UnitData>(to.Members);
            projected.AddRange(incoming);
            if (ArmyData.ComputeCurrentMovement(projected) < to.CurrentMovement
                || ArmyData.ComputeMaxMovement(projected) < to.MaxMovement)
            {
                why = $"would slow mission army ({contract.Label})";
                return false;
            }
            return true;
        }

        private static bool PreflightWholeFold(PlayerSetupData player, int turn, ArmyData from, ArmyData to,
            IReadOnlyList<UnitData> units, ActorCommitments commitments,
            HashSet<UnitData> movedUnits, out string why)
        {
            if (!CommonPreflight(player, turn, from, to, commitments, out why))
                return false;
            if (from.IsGarrison)
            { why = "whole-fold cannot consume a garrison"; return false; }
            if (units == null || units.Count == 0)
            { why = "empty whole-fold"; return false; }
            if (units.Any(u => u == null || movedUnits.Contains(u)))
            { why = "whole-fold member already moved or missing"; return false; }
            if (units.Any(u => u.IsAviation))
            { why = "aviation unit"; return false; }
            if (units.Any(u => u.ActivationApCost > 0 && to.RequiresActivationCharge(u)))
            { why = "whole-fold would spend AP on activated destination"; return false; }
            if (!ArmyActions.CanTransferMembers(units, from, to, out why))
                return false;
            if (!KeepsOperationMovement(player, turn, to, units, commitments, out why))
                return false;
            return true;
        }

        private static bool PreflightTransfer(PlayerSetupData player, int turn, ArmyData from, ArmyData to,
            UnitData unit, ActorCommitments commitments, HashSet<UnitData> movedUnits, out string why)
        {
            if (!CommonPreflight(player, turn, from, to, commitments, out why, released: unit))
                return false;
            if (movedUnits.Contains(unit)) { why = "unit already moved this plan"; return false; }
            if (!from.Members.Contains(unit)) { why = "unit not in source"; return false; }
            if (unit.IsAviation) { why = "aviation unit"; return false; }
            // Canonical TransferMember charges the incoming unit's ActivationApCost when the
            // destination has already activated. Housekeeping owns a 0-AP reserve today, so such a
            // candidate is structurally illegal here rather than silently spending another axis's AP.
            if (unit.ActivationApCost > 0 && to.RequiresActivationCharge(unit))
            { why = "would spend AP on activated destination"; return false; }
            // Mirror ArmyActions.TransferMember's projected-roster capacity rule exactly. A hero
            // may legally join a currently-full no-hero army because its CommandRating raises the
            // resulting capacity; using to.HasRoom here recreated the planner/runtime mismatch.
            if (!ArmyData.RosterFits(ArmyData.ProjectAdd(to.Members, unit, to.IsGarrison), to.IsGarrison))
            { why = "destination would exceed projected capacity"; return false; }
            if (!from.CanLeaveWithoutOvercrowding(unit)) { why = "source would overcrowd"; return false; }
            if (!KeepsOperationMovement(player, turn, to, new[] { unit }, commitments, out why))
                return false;
            if (from.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(player, from, unit, allowCitadelEmergency: false))
            { why = "garrison safety floor"; return false; }
            return true;
        }

        private static bool PreflightSwap(PlayerSetupData player, int turn, ArmyData armyA, UnitData unitA,
            ArmyData armyB, UnitData unitB, ActorCommitments commitments, HashSet<UnitData> movedUnits, out string why)
        {
            if (!CommonPreflight(player, turn, armyA, armyB, commitments, out why, inboundOnly: false))
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
                // A hero (no power) leaves and a body enters: the garrison's defence never drops.
            }
            if (movedUnits.Contains(unitA) || movedUnits.Contains(unitB)) { why = "swap member already moved this plan"; return false; }
            if (!armyA.Members.Contains(unitA) || !armyB.Members.Contains(unitB)) { why = "swap membership changed"; return false; }
            if (unitA.IsAviation || unitB.IsAviation) { why = "aviation unit"; return false; }
            // unitA enters B, unitB enters A. Zero-AP invariant mirrors ArmyActions.CanSwapMembers.
            if ((unitA.ActivationApCost > 0 && armyB.RequiresActivationCharge(unitA))
                || (unitB.ActivationApCost > 0 && armyA.RequiresActivationCharge(unitB)))
            { why = "swap would spend AP on activated destination"; return false; }
            if (!ArmyActions.CanSwapMembers(unitA, armyA, unitB, armyB, out string fail))
            { why = fail; return false; }
            return true;
        }
    }
}
