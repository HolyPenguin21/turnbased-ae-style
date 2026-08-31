using System.Collections.Generic;
using UnityEngine;

namespace Game.Ai.V2
{
    // One authoritative OPERATIONAL shortage assessment for a frozen Raid objective. The objective
    // evaluator owns target merit + frozen strategic projections; this type answers the later,
    // different question after continuity claims are known: "can a free actor execute now, and if
    // not, which deployable capability is missing?" Demand reads this directly and Provisioning
    // continues to use the same RaidAssemblyPlanner as its final live proof.
    public sealed class RaidOperationalReadiness
    {
        public RaidAssemblyPlan ReadyPlan;
        public CapabilityInventory Inventory;
        public float RequiredPower;
        public float NumericPowerDeficit;
        public float RequestedPower;
        public bool NeedsPower;
        public bool NeedsHero;
        public string PowerReason;
        public string ReadyReason;

        public bool ReadyExecutable => ReadyPlan != null && ReadyPlan.Feasible;

        public static RaidOperationalReadiness Evaluate(WorldSnapshot snap, AggressionObjective objective,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ActorCommitments commitments,
            CapabilityInventory inventory)
        {
            inventory = inventory ?? new CapabilityInventory();
            RaidAssemblyPlan ready = RaidAssemblyPlanner.Plan(
                snap, objective.ToTarget(), defenders, commitments?.ClaimedArmyIdSet);

            float requiredPower = Mathf.Max(1f, objective.TargetPower * AiConfigV2.raidCombatPowerMargin);
            float numericDeficit = Mathf.Max(0f, requiredPower - inventory.RaidAvailableFieldPower);
            bool executable = ready.Feasible;
            bool needsPower = !executable;
            bool needsHero = !executable && inventory.AvailableHeroes <= 0;

            return new RaidOperationalReadiness
            {
                ReadyPlan = ready,
                Inventory = inventory,
                RequiredPower = requiredPower,
                NumericPowerDeficit = numericDeficit,
                RequestedPower = needsPower ? Mathf.Max(1f, numericDeficit) : 0f,
                NeedsPower = needsPower,
                NeedsHero = needsHero,
                PowerReason = numericDeficit > AiConfigV2.allocatorSliceEpsilon
                    ? "free_field_power_below_requirement"
                    : "no_ready_free_army_clears_estimator",
                ReadyReason = ready.Reason ?? "ready-force solver rejected the target",
            };
        }
    }
}
