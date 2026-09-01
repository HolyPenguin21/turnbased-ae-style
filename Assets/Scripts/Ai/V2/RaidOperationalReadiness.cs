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
        // §11 — the AI has enough numeric field power and at least one raid-eligible hero, but no
        // legal already-formed OR transactionally assemblable same-hex force clears the estimator.
        // This is an organization gap, NOT a FieldCombatPower shortage: nothing new must be bought.
        public bool NeedsAssembly;
        public string PowerReason;
        public string AssemblyReason;
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

            // §11 — NeedsPower means an ACTUAL numeric power deficiency, nothing else. A structural
            // assembly failure with sufficient numeric power is NeedsAssembly, and never inflates
            // a phantom +1 FieldCombatPower request.
            bool needsPower = numericDeficit > AiConfigV2.allocatorSliceEpsilon;
            bool needsHero = !executable && !needsPower && inventory.AvailableHeroes <= 0;
            bool needsAssembly = !executable && !needsPower && !needsHero;

            return new RaidOperationalReadiness
            {
                ReadyPlan = ready,
                Inventory = inventory,
                RequiredPower = requiredPower,
                NumericPowerDeficit = numericDeficit,
                RequestedPower = needsPower ? numericDeficit : 0f,
                NeedsPower = needsPower,
                NeedsHero = needsHero,
                NeedsAssembly = needsAssembly,
                PowerReason = "free_field_power_below_requirement",
                AssemblyReason = needsHero
                    ? "no_raid_eligible_hero_anywhere"
                    : "sufficient_power_no_legal_ready_or_assemblable_force",
                ReadyReason = ready.Reason ?? "ready-force solver rejected the target",
            };
        }
    }
}
