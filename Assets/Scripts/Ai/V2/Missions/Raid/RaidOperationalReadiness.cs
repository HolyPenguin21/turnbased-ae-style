using System.Collections.Generic;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // One authoritative OPERATIONAL shortage assessment for a frozen Raid objective. The objective
    // evaluator owns target merit + frozen strategic projections; this type answers the later,
    // different question after continuity claims are known: "can a free actor execute now, and if
    // not, which deployable capability is missing?" Demand reads this directly and Provisioning
    // continues to use the same GroundCombatAssemblyPlanner as its final live proof.
    public sealed class RaidOperationalReadiness
    {
        public GroundCombatAssemblyPlan ReadyPlan;
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
        // T06 — the whole known pool (CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool)
        // can never cover these defenders, so no Hero/FieldCombatPower request can help this
        // target. Recomputed from every fresh snapshot: a new card, output, equipment or a change
        // in the known defence re-opens it; there is no blacklist. Claims, MP and resources never
        // make a target unreachable — they stay NeedsPower / NeedsAssembly (timing).
        public bool ProvenUnreachableWithinKnownPool;
        public string UnreachableReason;

        public bool ReadyExecutable => ReadyPlan != null && ReadyPlan.Feasible;
        // Not ready now, but nothing proves the known pool cannot get there.
        public bool AttainableWithKnownPool => !ReadyExecutable && !ProvenUnreachableWithinKnownPool;

        public static RaidOperationalReadiness Evaluate(WorldSnapshot snap, AggressionObjective objective,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ActorCommitments commitments,
            CapabilityInventory inventory)
        {
            inventory = inventory ?? new CapabilityInventory();
            float hexBonus = AiV2Util.KnownRaidDefenceBonus(snap, objective.Target);
            GroundCombatAssemblyPlan ready = GroundCombatAssemblyPlanner.Plan(
                snap, objective.ToTarget(), opposition, commitments?.ClaimedArmyIdSet, hexBonus);

            // Sized against the same hex defence the readiness plan was built with.
            float requiredPower = GroundCombatFeasibility.RequiredPower(
                WorthIt.UnitsOf(opposition ?? System.Array.Empty<WorthIt.DefendingArmy>()), hexBonus);
            float numericDeficit = Mathf.Max(0f, requiredPower - inventory.RaidAvailableFieldPower);
            bool executable = ready.Feasible;

            // §11 — NeedsPower means an ACTUAL numeric power deficiency, nothing else. A structural
            // assembly failure with sufficient numeric power is NeedsAssembly, and never inflates
            // a phantom +1 FieldCombatPower request.
            string unreachableReason = null;
            bool unreachable = !executable
                && CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(snap,
                    opposition, hexBonus, out unreachableReason);
            bool needsPower = !unreachable && numericDeficit > AiConfigV2.allocatorSliceEpsilon;
            bool needsHero = !executable && !unreachable && !needsPower && inventory.AvailableHeroes <= 0;
            bool needsAssembly = !executable && !unreachable && !needsPower && !needsHero;

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
                ProvenUnreachableWithinKnownPool = unreachable,
                UnreachableReason = unreachableReason,
                PowerReason = "free_field_power_below_requirement",
                AssemblyReason = needsHero
                    ? "no_raid_eligible_hero_anywhere"
                    : "sufficient_power_no_legal_ready_or_assemblable_force",
                ReadyReason = ready.Reason ?? "ready-force solver rejected the target",
            };
        }
    }
}
