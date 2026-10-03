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
