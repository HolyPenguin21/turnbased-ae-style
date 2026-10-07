using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.Aviation;
using UnityEngine;

namespace Game.Ai.V2
{
    internal static class GroundCombatReinforcementTransaction
    {
        internal static bool ApplyLocalRefit(MaterializationPlan plan, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AiHandData hand, ArmyData support, SpendAuthority authority)
        {
            var primary = AiV2Util.ResolveArmy(player, plan.AttackRefitPrimaryId.Value);
            var attack = AttackBaseRefitPolicy.Resolve(player, primary?.Id ?? -1, plan.Deploy.Hex, ctx.TurnNumber);
            if (primary == null || support == null || attack == null
                || attack.RefitCaptureTurn != plan.AttackRefitCaptureTurn
                || (support != primary && AttackBaseRefitPolicy.RosterKey(primary) != plan.AttackRefitRoster)) return false;
            // A direct free-slot deployment already used the canonical card-play transaction.
            if (support == primary)
            {
                if (attack.RefitBattleStopTurn == ctx.TurnNumber && primary.Members.Any(u => u.MoveCurrent != 0))
                {
                    foreach (var u in primary.Members) u.MoveCurrent = 0;
                    WorldDeltaLifecycle.CommitMutation();
                }
                return true;
            }
            var snap = WorldAnalysis.Scan(player, root, hand, ctx);
            var h = GroundCombatReinforcement.PlanAttackHandoff(primary, support,
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, attack.Target.Hex),
                AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx.Map, attack.Target.Hex),
                true, out _, capacityIsProgress: true);
            if (h == null || !AttackBaseRefitPolicy.KeepsForce(primary, h)
                || !AttackBaseRefitPolicy.KeepsMovement(primary, h)
                || !AttackBaseRefitPolicy.KeepsCoverage(primary, h, snap, attack)) return false;
            int followup = AttackBaseRefitPolicy.FollowupAp(player, primary, attack,
                AttackBaseRefitPolicy.FinalRoster(primary, h), ctx, root);
            int cost = GroundCombatReinforcement.HandoffApCost(h, primary, support);
            int movement = primary.CurrentMovement;
            if (StrategicSpendability.SpendableAp(player, root, ctx, authority) < cost + followup
                || !AttackBaseRefitPolicy.OnwardFunded(plan, player, root, ctx, cost,
                    AttackBaseRefitPolicy.FinalRoster(primary, h))
                || !ArmyActions.TransferMembersAtomic(h.Incoming, support, primary, ctx.HexSelection,
                    out _, h.Promote, h.Displaced)) return false;
            // An exchange cannot restore movement spent before the handoff.
            foreach (var u in h.Incoming) u.MoveCurrent = System.Math.Min(u.MoveCurrent, movement);
            WorldDeltaLifecycle.CommitMutation();
            AiDebugLog.Write($"[AI][V2][Attack][Refit] primary=#{primary.Id} base={primary.Hex} {h.Detail} ap={cost}");
            return true;
        }

        // The concrete roster mutation: fill the primary's free slots first, then — if it is full —
        // swap its most critically wounded bodies for fresh ones. Executed through the SAME
        // authoritative ArmyActions primitives a human uses; the support container is never emptied.
        // `commandOpposition` (optional, the lane's fight) — when given, the support's hero may be
        // handed over to lead the primary (GroundCombatReinforcement.CommandHandover), in the same
        // atomic transfer as the bodies its Command makes room for.
        internal static bool ApplyReinforcementHandoff(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ArmyData support, ArmyData primary,
            out int transferred, out bool wasSwap, out string displacedUnitName, out string detail,
            IReadOnlyList<WorthIt.DefendingArmy> commandOpposition = null, float commandHexBonus = 0f,
            bool allowCompleteTransfer = false, bool capacityIsProgress = false)
        {
            transferred = 0;
            wasSwap = false;
            displacedUnitName = null;
            // The one handoff decision (GroundCombatReinforcement.PlanHandoff) — the same plan the
            // leg's AP was provisioned on — applied as one atomic transfer / exchange.
            // The Attack lane (it passes its fight) runs the one Attack plan (PlanAttackHandoff).
            string why;
            HandoffPlan plan = commandOpposition != null
                ? GroundCombatReinforcement.PlanAttackHandoff(primary, support, commandOpposition,
                    commandHexBonus, requireChargeNow: true, out why, capacityIsProgress)
                : GroundCombatReinforcement.PlanHandoff(primary, support,
                    null, commandHexBonus, out why, allowCompleteTransfer);
            if (plan == null)
            {
                detail = why;
                return false;
            }
            if (!ArmyActions.TransferMembersAtomic(plan.Incoming, support, primary, ctx.HexSelection,
                    out string failWhy, plan.Promote, plan.Displaced))
            {
                detail = $"{why}atomic handoff rejected: {failWhy}";
                return false;
            }
            transferred = plan.Incoming.Count;
            wasSwap = plan.Displaced.Count > 0;
            displacedUnitName = wasSwap ? string.Join(",", plan.Displaced.Select(u => u.Name)) : null;
            detail = why + plan.Detail;
            return true;
        }
    }
}
