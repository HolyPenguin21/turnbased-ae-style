using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ARCH-02 §17 — the canonical owner of "after a materialization deployed, how much operational
    // capability did it actually add, and which armies now hold a Housekeeping capability lease for
    // it". Extracted verbatim from StrategicManager so Phase A, Phase B and (through them) the
    // bounded StrategicReactionPass share one measurement, not three that can drift apart.
    internal static class CapabilityDeliveryEvaluator
    {
        internal static IReadOnlyList<int> OperationalLeaseArmyIds(HashSet<int> armyIdsBefore,
            WorldSnapshot after, MaterializationPlan plan, AxisDemand demand)
        {
            var ids = new HashSet<int>();
            if (after?.Self?.Armies == null || demand == null)
                return ids.ToList();

            int existingRecipient = plan?.Deploy.Army != null ? plan.Deploy.Army.Id : -1;
            foreach (ArmySnapshot army in after.Self.Armies)
            {
                if (army == null || (!armyIdsBefore.Contains(army.ArmyId) && !IsOperationalForDemand(army, demand)))
                    continue;
                if (army.ArmyId == existingRecipient && IsOperationalForDemand(army, demand))
                    ids.Add(army.ArmyId);
            }
            foreach (ArmySnapshot army in after.Self.Armies)
                if (army != null && !armyIdsBefore.Contains(army.ArmyId) && IsOperationalForDemand(army, demand))
                    ids.Add(army.ArmyId);
            return ids.OrderBy(id => id).ToList();
        }

        // ARCH-02 §16 — the army-level delivery check now lives in MaterializationDeliveryPolicy
        // alongside the plan-level one. Forwarder kept for this class's own lease bookkeeping.
        internal static bool IsOperationalForDemand(ArmySnapshot army, AxisDemand demand)
            => MaterializationDeliveryPolicy.IsArmyOperationalForDemand(army, demand);

        internal static float DeliveredCapabilityAmount(AxisDemand demand,
            CapabilityInventory before, CapabilityInventory after)
        {
            if (demand == null || before == null || after == null)
                return 0f;
            switch (demand.Capability)
            {
                case CapabilityKind.FieldCombatPower:
                    return Mathf.Max(0f, after.RaidAvailableFieldPower - before.RaidAvailableFieldPower);
                case CapabilityKind.GarrisonCombatPower:
                    return Mathf.Max(0f, after.GarrisonCombatPower - before.GarrisonCombatPower);
                case CapabilityKind.Hero:
                    return Mathf.Max(0, after.AvailableHeroes - before.AvailableHeroes);
                case CapabilityKind.ScoutCapability:
                    if ((demand.RequiredTraits & TraitPreference.Stealth) != 0)
                        return Mathf.Max(0, after.StealthScouts - before.StealthScouts);
                    return Mathf.Max(0, after.ReadyScouts - before.ReadyScouts);
                default:
                    return 0f;
            }
        }

        // §3 — the ONE post-delivery finalization path shared by Phase A, Phase B and (through
        // those two) the bounded StrategicReactionPass. It owns delivered-capability measurement
        // and the Housekeeping capability lease for every army created/modified to satisfy a live
        // strategic demand, so a later Phase A / Phase B divergence cannot silently drop the lease
        // again. Callers still own the parts that genuinely differ by phase: Phase A's discrete
        // follow-up AP borrow against the axis ledger, and each phase's own residual bookkeeping.
        internal static bool FinalizeOperationalDelivery(PlayerSetupData player, AiTurnContext ctx,
            WorldSnapshot afterSnap, MaterializationPlan plan, AxisDemand demand,
            CapabilityInventory before, CapabilityInventory after, HashSet<int> armyIdsBefore,
            out float delivered)
        {
            delivered = DeliveredCapabilityAmount(demand, before, after);
            if (delivered <= AiConfigV2.allocatorSliceEpsilon)
                return false;
            IReadOnlyList<int> leased = OperationalLeaseArmyIds(armyIdsBefore, afterSnap, plan, demand);
            StrategicCapabilityLeaseRegistry.Mark(player, ctx.TurnNumber, demand.Capability, leased);
            return true;
        }
    }
}
