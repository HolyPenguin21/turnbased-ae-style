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
            => demand == null
                ? new List<int>()
                : OperationalLeaseArmyIds(armyIdsBefore, after, plan,
                    army => MaterializationDeliveryPolicy.IsArmyOperationalForDemand(army, demand));

        private static IReadOnlyList<int> OperationalLeaseArmyIds(HashSet<int> armyIdsBefore,
            WorldSnapshot after, MaterializationPlan plan, CapabilityKind capability,
            TraitPreference requiredTraits)
            => OperationalLeaseArmyIds(armyIdsBefore, after, plan,
                army => MaterializationDeliveryPolicy.IsArmyOperationalForCapability(
                    army, capability, requiredTraits));

        private static IReadOnlyList<int> OperationalLeaseArmyIds(HashSet<int> armyIdsBefore,
            WorldSnapshot after, MaterializationPlan plan, System.Func<ArmySnapshot, bool> operational)
        {
            var ids = new HashSet<int>();
            if (after?.Self?.Armies == null || armyIdsBefore == null || operational == null)
                return ids.ToList();

            int existingRecipient = plan?.Deploy.Army != null ? plan.Deploy.Army.Id : -1;
            foreach (ArmySnapshot army in after.Self.Armies)
            {
                if (army == null || !operational(army))
                    continue;
                if (army.ArmyId == existingRecipient || !armyIdsBefore.Contains(army.ArmyId))
                    ids.Add(army.ArmyId);
            }
            return ids.OrderBy(id => id).ToList();
        }

        // ARCH-02 §16 — the army-level delivery check now lives in MaterializationDeliveryPolicy
        // alongside the plan-level one. Forwarder kept for this class's own lease bookkeeping.
        internal static bool IsOperationalForDemand(ArmySnapshot army, AxisDemand demand)
            => MaterializationDeliveryPolicy.IsArmyOperationalForDemand(army, demand);

        // After an Economy Hero materializes, bind the new actor back through DemandLayer's one
        // canonical whole-army ranking. The refreshed Economy snapshot owns route feasibility;
        // this evaluator only identifies the just-delivered actor and returns that ranked result.
        internal static DemandLayer.EconomyBuilderChoice EconomyDeliveryChoice(
            WorldSnapshot after, AxisDemand demand, int builderArmyId,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            out IReadOnlyList<EconomyBuilderRouteSnapshot> builderRoutes)
        {
            builderRoutes = System.Array.Empty<EconomyBuilderRouteSnapshot>();
            if (after?.Economy == null || demand?.TargetHex == null || builderArmyId == 0)
                return null;
            bool foundBase = demand.EconomyBuildCard?.Definition?.cardType == CardType.Base;
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = foundBase
                ? (after.Economy.BaseOpportunities
                    ?? System.Array.Empty<EconomyBaseOpportunity>()).FirstOrDefault(
                    x => x.Hex.Equals(demand.TargetHex.Value)).BuilderRoutes
                : (after.Economy.ExtractionOpportunities
                    ?? System.Array.Empty<EconomyExtractionOpportunity>()).FirstOrDefault(x =>
                    x.Hex.Equals(demand.TargetHex.Value)
                    && demand.EconomyResourceType.HasValue
                    && x.ResourceType == demand.EconomyResourceType.Value).BuilderRoutes;
            builderRoutes = (witnessed ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
                .Where(x => x.ArmyId == builderArmyId).ToList();
            if (builderRoutes.Count == 0)
                return null;
            return DemandLayer.SelectEconomyBuilder(after, demand.TargetHex.Value,
                builderRoutes, activeIntents, commitments,
                demand.EconomySiteValue > 0f ? demand.EconomySiteValue : demand.Value,
                demand.EconomyBuildApCost, includeReturn: !foundBase);
        }

        internal static float DeliveredCapabilityAmount(AxisDemand demand,
            CapabilityInventory before, CapabilityInventory after)
            => demand == null
                ? 0f
                : DeliveredCapabilityAmount(demand.Capability, demand.RequiredTraits, before, after);

        private static float DeliveredCapabilityAmount(CapabilityKind capability,
            TraitPreference requiredTraits, CapabilityInventory before, CapabilityInventory after)
        {
            if (before == null || after == null)
                return 0f;
            switch (capability)
            {
                case CapabilityKind.FieldCombatPower:
                    return Mathf.Max(0f, after.RaidAvailableFieldPower - before.RaidAvailableFieldPower);
                case CapabilityKind.GarrisonCombatPower:
                    return Mathf.Max(0f, after.GarrisonCombatPower - before.GarrisonCombatPower);
                case CapabilityKind.Hero:
                    return Mathf.Max(0, after.AvailableHeroes - before.AvailableHeroes);
                case CapabilityKind.ScoutCapability:
                    if ((requiredTraits & TraitPreference.Stealth) != 0)
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
            IReadOnlyList<int> leased = OperationalLeaseArmyIds(armyIdsBefore, afterSnap, plan, demand);
            // CapabilityInventory.AvailableHeroes intentionally counts combat-ready heroes only.
            // An Economy Hero is delivered by the demand-aware mobile-builder predicate instead.
            delivered = MaterializationDeliveryPolicy.IsEconomyHeroDemand(demand)
                ? leased.Count
                : DeliveredCapabilityAmount(demand, before, after);
            if (delivered <= AiConfigV2.allocatorSliceEpsilon)
                return false;
            // Economy Hero delivery is handed synchronously to MissionContinuityLayer by Phase A;
            // persisting the generic through-Housekeeping lease as well would leave two owners.
            // Other capabilities still need the turn-local barrier until their normal handoff.
            if (!MaterializationDeliveryPolicy.IsEconomyHeroDemand(demand))
                StrategicCapabilityLeaseRegistry.Mark(
                    player, ctx.TurnNumber, demand.Capability, leased);
            return true;
        }

        // Phase B may create a Scout as useful surplus, with no residual AxisDemand. It is still
        // an operational result of the just-executed plan and needs the same turn-local lease so
        // the immediately following housekeeping pass cannot fold it before it gets a turn.
        internal static bool LeaseSurplusScoutDelivery(PlayerSetupData player, AiTurnContext ctx,
            WorldSnapshot afterSnap, MaterializationPlan plan, CapabilityInventory before,
            CapabilityInventory after, HashSet<int> armyIdsBefore, out float delivered)
        {
            delivered = 0f;
            if (plan == null || plan.FinalCapability != CapabilityKind.ScoutCapability)
                return false;

            delivered = DeliveredCapabilityAmount(
                CapabilityKind.ScoutCapability, plan.ExpectedTraits, before, after);
            if (delivered <= AiConfigV2.allocatorSliceEpsilon)
                return false;

            IReadOnlyList<int> leased = OperationalLeaseArmyIds(armyIdsBefore, afterSnap, plan,
                CapabilityKind.ScoutCapability, plan.ExpectedTraits);
            if (leased.Count == 0)
                return false;
            StrategicCapabilityLeaseRegistry.Mark(
                player, ctx.TurnNumber, CapabilityKind.ScoutCapability, leased);
            return true;
        }
    }
}
