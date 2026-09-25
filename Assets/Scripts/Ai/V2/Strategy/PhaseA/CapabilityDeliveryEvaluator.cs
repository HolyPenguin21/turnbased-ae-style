using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
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
            if (after?.Economy == null || demand?.TargetHex == null)
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
            delivered = 0f;
            if (MaterializationDeliveryPolicy.IsEconomyHeroDemand(demand))
            {
                // Both phases must establish the same durable owner before reducing a residual.
                // Revalidate after the real deployment: a successful card play can still fail to
                // deliver a builder if the route or escort changed during that operation.
                var intents = MissionIntentRegistry.GetOrCreate(player).All
                    .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
                var commitments = ActorCommitments.FromIntents(intents, afterSnap, null);
                // Audit F5: both "not delivered" exits below used to be silent, so a hero card that
                // passed the plan-level check (projected army) and then failed the post-deployment
                // check (real army) left no trace of WHY. Diagnostics only — no decision changes.
                if (leased.Count == 0)
                    AiDebugLog.Write($"[AI][V2][Economy][Delivery] demand={demand} decision=NOT_DELIVERED "
                        + "reason=no_deployed_army_is_a_mobile_economy_builder");
                foreach (int builderId in leased)
                {
                    DemandLayer.EconomyBuilderChoice choice = EconomyDeliveryChoice(
                        afterSnap, demand, builderId, intents, commitments,
                        out IReadOnlyList<EconomyBuilderRouteSnapshot> routes);
                    if (choice == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Economy][Delivery] demand={demand} builder=#{builderId} "
                            + "decision=NOT_DELIVERED reason=" + (routes.Count == 0
                                ? "no_witnessed_builder_route_in_refreshed_snapshot"
                                : "builder_ranking_rejected_actor"));
                        continue;
                    }
                    demand.EconomyPreferredBuilderArmyId = builderId;
                    demand.EconomyBuilderRoutes = routes;
                    demand.EconomyAssignmentApCost = choice.TotalAssignmentApCost;
                    // A physically deployed Hero is not a fulfilled Economy build demand
                    // unless Continuity successfully owns its destination lease.
                    MissionIntent delivery = MissionContinuityLayer.BeginEconomyDelivery(
                        player, demand, builderId, ctx.TurnNumber);
                    if (delivery == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Economy][Delivery] demand={demand} builder=#{builderId} "
                            + "decision=NOT_DELIVERED reason=continuity_refused_ownership_grant");
                        continue;
                    }
                    // The delivery is now a durable intent: protect it through the ONE deferred
                    // writer for active builds, same owner key and rules as every later Phase A.
                    InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(
                        player, ctx.TurnNumber, delivery);
                    delivered = 1f;
                    break;
                }
            }
            else if (demand?.Capability == CapabilityKind.CollectorCapability)
            {
                // CapabilityInventory intentionally only models military/Recon supply; it has no
                // collector counter. Measure this resource-specific delivery by the SAME freshly
                // deployed army identities and policy predicate as the capability lease. A second
                // global collector counter would duplicate Economy's collection model.
                delivered = leased.Count > 0 ? 1f : 0f;
            }
            else
                delivered = DeliveredCapabilityAmount(demand, before, after);
            if (delivered <= AiConfigV2.allocatorSliceEpsilon)
                return false;

            // An IndependentFieldArmy is delivered only when the SAME
            // GroundCombat admission used by Demand/Missions/Provisioning accepts the concrete
            // post-deployment roster. A one-body shell is useful construction progress, but it
            // cannot spare a body without emptying its container and must not close the demand or
            // become Continuity's support actor yet.
            if (IsGroundCombatReinforcementDemand(demand))
            {
                if (TryHandoffGroundCombatSupport(player, afterSnap, demand, leased, ctx.TurnNumber))
                    return true;

                // Keep the partial recipient intact through this turn's Housekeeping. The residual
                // remains open (delivered=0), so a later pass/turn may add another body to the same
                // ordinary reserve army and re-run the canonical GroundCombat admission.
                StrategicCapabilityLeaseRegistry.Mark(
                    player, ctx.TurnNumber, demand.Capability, leased);
                delivered = 0f;
                AiDebugLog.Write($"[AI][V2][{demand.ConsumerMissionKind}] materialization partial "
                    + $"support for {demand.ConsumerIntentKey}: no transfer-ready leased army; "
                    + "demand remains open");
                return false;
            }

            // Economy build-delivery already has one Continuity owner; a generic lease would add
            // a second one. Collector delivery is different: its mobile collection mission will
            // be discovered by Analysis and admitted by EconomyMissionPlanner from the refreshed
            // snapshot. Until then the turn-local lease prevents Housekeeping repackaging it.
            if (!MaterializationDeliveryPolicy.IsEconomyHeroDemand(demand))
                StrategicCapabilityLeaseRegistry.Mark(
                    player, ctx.TurnNumber, demand.Capability, leased);
            return true;
        }

        // ATK §41/§75 — a reinforcement demand of EITHER offensive ground-combat lane. Attack
        // raises the identical FieldCombatPower/IndependentFieldArmy demand against an identical
        // proof (its bound primary no longer clears a known defender package), so the delivery that
        // answers it must bind the same way. Gating this on MissionKind.Raid meant Production built
        // the army Attack asked for and then nobody ever handed it over.
        private static bool IsGroundCombatReinforcementDemand(AxisDemand demand)
            => demand != null
                && demand.RequestingAxis == DesireAxis.Aggression
                && demand.Capability == CapabilityKind.FieldCombatPower
                && demand.DeliveryShape == CapabilityDeliveryShape.IndependentFieldArmy
                && demand.ConsumerIntentKey.HasValue
                && (demand.ConsumerMissionKind == MissionKind.Raid
                    || demand.ConsumerMissionKind == MissionKind.Attack);

        // Bind an IndependentFieldArmy delivery to the exact offensive
        // ground-combat intent that asked for it. Returns true when the support actor was handed to
        // Continuity.
        // This is also the single point that stamps the intent's
        // ReinforcementRequestedTurn: the demand is "accepted/funded" exactly when a materialization
        // for its ConsumerIntentKey actually delivered a concrete support army, never merely when
        // AggressionDemandEvaluator.Build (a pure read) proposed it.
        // Raid and Attack differ here in exactly two facts, which the small switch below
        // resolves once: which primary is being reinforced, and which defender package (plus its
        // site defence, §30) the admission is measured against. Everything after that — the one
        // GroundCombatAssemblyPlanner admission, the leased-army intersection, the deterministic
        // pick and the phase/turn stamping — is shared, not copied per lane.
        internal static bool TryHandoffGroundCombatSupport(PlayerSetupData player,
            WorldSnapshot afterSnap, AxisDemand demand, IReadOnlyList<int> leased, int turnNumber)
        {
            if (player == null || demand == null
                || demand.RequestingAxis != DesireAxis.Aggression
                || demand.DeliveryShape != CapabilityDeliveryShape.IndependentFieldArmy
                || !demand.ConsumerIntentKey.HasValue)
                return false;

            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (!state.TryGet(demand.ConsumerIntentKey.Value, out MissionIntent intent)
                || intent == null)
                return false;

            RaidIntent ri = intent.Raid;
            AttackIntent ai = intent.Attack;
            int primaryId;
            IReadOnlyList<WorthIt.DefendingArmy> opposition;
            float hexBonus = 0f;
            if (ri != null && ri.PrimaryArmyId.HasValue)
            {
                primaryId = ri.PrimaryArmyId.Value;
                opposition = AiV2Util.KnownOpposition(afterSnap, ri.Target);
            }
            else if (ai != null && ai.PrimaryArmyId.HasValue && ai.Target.HasValue)
            {
                primaryId = ai.PrimaryArmyId.Value;
                opposition = AttackObjectiveEvaluator.KnownSiteOpposition(afterSnap, ai.Target.Hex);
                hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(
                    afterSnap, null, ai.Target.Hex);
            }
            else
            {
                return false;
            }

            // GroundCombatAssemblyPlanner is the single owner of reinforcement admission. Intersect
            // its transfer-ready candidates with the armies this materialization actually touched;
            // never weaken that contract back to the generic IsStructuralRaidActor shape.
            var admissible = new HashSet<int>(
                GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                    afterSnap, primaryId, opposition, null, hexBonus));
            int? support = leased
                .Where(id => id != primaryId && admissible.Contains(id))
                .OrderBy(id => id)
                .Select(id => (int?)id)
                .FirstOrDefault();
            if (!support.HasValue)
                return false;

            if (ri != null)
            {
                ri.SupportArmyId = support;
                ri.Phase = RaidMissionPhase.Reinforcement;
                ri.ReinforcementRequestedTurn = turnNumber;
            }
            else
            {
                ai.SupportArmyId = support;
                ai.Phase = AttackMissionPhase.Reinforcement;
                ai.ReinforcementRequestedTurn = turnNumber;
            }
            AiDebugLog.Write($"[AI][V2][{intent.Kind}] materialization handoff {intent.IntentKey} "
                + $"support=#{support.Value} primary=#{primaryId} phase=Reinforcement "
                + "(Continuity owns the actor; no generic housekeeping lease)");
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
