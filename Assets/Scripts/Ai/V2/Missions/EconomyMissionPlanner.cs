using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // Converts scored Economy demands into durable operational missions. It does not move actors,
    // spend resources, or mutate reservations; those remain provisioning/execution concerns.
    internal static class EconomyMissionPlanner
    {
        public static List<MissionProposal> Propose(WorldSnapshot snapshot,
            DesireBreakdown breakdown, IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<AxisDemand> demands)
        {
            var result = new List<MissionProposal>();
            ActorCommitments currentCommitments = ActorCommitments.FromIntents(
                activeIntents, snapshot, null);
            foreach (MissionIntent intent in activeIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (intent?.Kind != MissionKind.Economy
                    || intent.Status != IntentStatus.Active
                    || intent.Economy == null
                    || !intent.PreferredMoverArmyId.HasValue)
                    continue;
                EconomyIntent e = intent.Economy;
                bool mobile = e.Kind == EconomyTaskKind.MobileCollection
                    || e.Kind == EconomyTaskKind.ReturnCollector;
                // This is a per-pass execution admission, not cancellation of the durable intent.
                // Phase B can re-enter the operational loop after changing the hand/resources,
                // but neither change refills this committed builder's movement. Do not repeatedly
                // fund the same impossible travel step; completion on the target hex is still
                // allowed with zero MP, and a refreshed actor/movement state admits it again.
                ArmySnapshot pinnedBuilder = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == intent.PreferredMoverArmyId.Value);
                if (pinnedBuilder != null && pinnedBuilder.CurrentMovement <= 0
                    && !pinnedBuilder.Hex.Equals(e.TargetHex))
                {
                    AiDebugLog.WriteVerbose($"[AI][V2][Economy] defer travel this turn "
                        + $"intent={intent.IntentKey} actor=#{pinnedBuilder.ArmyId} "
                        + "reason=pinned_builder_movement_exhausted");
                    continue;
                }
                AxisDemand refreshed = mobile ? null : demands?.FirstOrDefault(d => d != null
                    && d.RequestingAxis == DesireAxis.Economy && d.TargetHex.HasValue
                    && d.TargetHex.Value.Equals(e.TargetHex)
                    && d.EconomyResourceType == e.ResourceType
                    && d.EconomyPreferredBuilderArmyId == intent.PreferredMoverArmyId
                    && (e.BuildCard == null || d.EconomyBuildCard == e.BuildCard)
                    && ((e.Kind == EconomyTaskKind.BuildExtraction
                            && d.Capability == CapabilityKind.EconomicInfrastructure)
                        || (e.Kind == EconomyTaskKind.FoundBase
                            && d.Capability == CapabilityKind.EconomicExpansionBase)));
                var target = new EconomyMissionTarget
                {
                    Kind = e.Kind,
                    TargetHex = e.TargetHex,
                    ResourceType = e.ResourceType,
                    ObjectiveId = e.Kind == EconomyTaskKind.ReturnBuilder
                        ? $"ReturnBuilder:{intent.PreferredMoverArmyId.Value}"
                        : e.Kind == EconomyTaskKind.ReturnCollector
                            ? $"ReturnCollector:{intent.PreferredMoverArmyId.Value}"
                        : $"{e.Kind}:{e.TargetHex.Q},{e.TargetHex.R}",
                    // PreferredMoverArmyId is the continuity-owned actor identity. The payload is
                    // kept synchronized with it so provisioning never sees two competing builders.
                    BuilderArmyId = intent.PreferredMoverArmyId,
                    CollectorArmyId = mobile ? intent.PreferredMoverArmyId : e.CollectorArmyId,
                    CollectorSourceArmyId = e.CollectorSourceArmyId,
                    ExpectedMarginalYield = e.ExpectedMarginalYield,
                    SafeReturnHex = e.SafeReturnHex,
                    BuildCard = refreshed?.EconomyBuildCard ?? e.BuildCard,
                    BuildResourceCost = refreshed?.EconomyBuildResourceCost ?? e.BuildResourceCost,
                    BuildApCost = refreshed?.EconomyBuildApCost ?? e.BuildApCost,
                    BuildValue = refreshed != null ? refreshed.EconomySiteValue : e.BuildValue,
                    MinimumFollowupAp = refreshed?.MinimumFollowupAp ?? e.MinimumFollowupAp,
                    BuilderRoutes = refreshed?.EconomyBuilderRoutes,
                    ProjectedActivationApCost = refreshed?.EconomyProjectedActivationApCost
                        ?? e.ProjectedActivationApCost,
                    ProjectedMaxMovement = refreshed?.EconomyProjectedMaxMovement
                        ?? e.ProjectedMaxMovement,
                };
                // An available refreshed demand contains the full delivered TaskScore. BuildValue
                // is a legacy operational/site fact and must not replace it in global admission.
                // A ReturnBuilder is lifecycle work, not a new world task: its priority belongs to
                // its durable commitment rather than to the site it finished building.
                float intrinsic = e.Kind == EconomyTaskKind.ReturnBuilder
                    || e.Kind == EconomyTaskKind.ReturnCollector ? 0f
                    : refreshed?.Value ?? e.IntrinsicValue ?? 0f;
                var mission = new MissionProposal
                {
                    Kind = MissionKind.Economy, Target = target,
                    BaseValue = intrinsic, LocalAdmissionScore = intrinsic,
                    Requirements = Requirements(target, intent, snapshot,
                        activeIntents, currentCommitments),
                    PreferredMoverArmyId = intent.PreferredMoverArmyId,
                    FromDurableIntent = true, DurableFundingTier = intent.Funding,
                    Explain = $"economy committed {target.Kind} #{intent.PreferredMoverArmyId.Value} "
                        + $"@({target.TargetHex.Q},{target.TargetHex.R}) intrinsic={intrinsic:0.##}",
                };
                mission.Axes.Value[DesireAxis.Economy] = 1f;
                result.Add(mission);
            }

            // Mobile collection is an economy mission in its own right. It is emitted directly
            // from the immutable analysis snapshot and therefore does not need an infrastructure
            // card demand from Phase A.
            foreach (MobileCollectionOpportunity op in snapshot?.Economy?.MobileCollectionOpportunities
                         ?? System.Array.Empty<MobileCollectionOpportunity>())
            {
                if (activeIntents?.Any(i => i?.Status == IntentStatus.Active
                    && i.Economy?.Kind == EconomyTaskKind.MobileCollection
                    && i.Economy.ResourceType == op.ResourceType
                    && i.Economy.TargetHex.Equals(op.TargetHex)) == true)
                    continue;
                var target = new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.MobileCollection,
                    TargetHex = op.TargetHex,
                    ResourceType = op.ResourceType,
                    ObjectiveId = $"MobileCollection:{op.ResourceType}:{op.TargetHex.Q},{op.TargetHex.R}",
                    CollectorArmyId = op.CollectorArmyId,
                    CollectorSourceArmyId = op.CollectorArmyId,
                    ExpectedMarginalYield = op.EffectiveRemainingYield,
                    SafeReturnHex = op.SafeReturnHex,
                    BuildValue = op.EffectiveRemainingYield,
                };
                var mission = new MissionProposal
                {
                    Kind = MissionKind.Economy,
                    Target = target,
                    BaseValue = op.TaskScoreValue,
                    LocalAdmissionScore = op.TaskScoreValue,
                    PreferredMoverArmyId = op.CollectorArmyId,
                    Requirements = Requirements(target, null, snapshot, activeIntents,
                        currentCommitments),
                    Explain = $"economy mobile-collect {op.ResourceType} actor=#{op.CollectorArmyId} "
                        + $"@({op.TargetHex.Q},{op.TargetHex.R}) value={op.TaskScoreValue:0.##}",
                };
                mission.Axes.Value[DesireAxis.Economy] = 1f;
                result.Add(mission);
            }

            if (demands == null)
                return result;

            foreach (AxisDemand d in demands.Where(x => x != null
                         && x.RequestingAxis == DesireAxis.Economy
                         && x.TargetHex.HasValue
                         && (x.Capability == CapabilityKind.EconomicInfrastructure
                             || x.Capability == CapabilityKind.EconomicExpansionBase))
                     .OrderByDescending(x => x.Value).ThenBy(x => x.TargetHex.Value.Q)
                     .ThenBy(x => x.TargetHex.Value.R))
            {
                EconomyTaskKind kind = d.Capability == CapabilityKind.EconomicExpansionBase
                    ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
                var target = new EconomyMissionTarget
                {
                    Kind = kind,
                    TargetHex = d.TargetHex.Value,
                    ResourceType = d.EconomyResourceType,
                    ObjectiveId = $"{kind}:{d.TargetHex.Value.Q},{d.TargetHex.Value.R}",
                    BuildCard = d.EconomyBuildCard,
                    BuildResourceCost = d.EconomyBuildResourceCost,
                    BuildApCost = d.EconomyBuildApCost,
                    // Operational site merit is retained separately for existing builder decisions.
                    BuildValue = d.EconomySiteValue,
                    MinimumFollowupAp = d.MinimumFollowupAp,
                    BuilderArmyId = d.EconomyPreferredBuilderArmyId,
                    BuilderRoutes = d.EconomyBuilderRoutes,
                    ProjectedActivationApCost = d.EconomyProjectedActivationApCost,
                    ProjectedMaxMovement = d.EconomyProjectedMaxMovement,
                };
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Kind == MissionKind.Economy && i.Economy != null
                    && i.Economy.Kind == kind && i.Economy.TargetHex.Equals(target.TargetHex));
                // Active Economy work was materialized above from continuity itself. A fresh
                // demand may describe the same site with a newly ranked builder, but it cannot
                // replace or duplicate the committed operation.
                if (incumbent != null)
                    continue;
                var m = new MissionProposal
                {
                    Kind = MissionKind.Economy,
                    Target = target,
                    // The globally compared value must be the entire canonical world-task Fold,
                    // not the site-only value before CardPrice/Delivery/MoverOpportunityCost.
                    BaseValue = d.Value,
                    // Newly admitted Economy missions use their canonical net TaskScore.
                    LocalAdmissionScore = d.Value,
                    Requirements = Requirements(target, incumbent, snapshot,
                        activeIntents, currentCommitments),
                    PreferredMoverArmyId = incumbent?.PreferredMoverArmyId
                        ?? d.EconomyPreferredBuilderArmyId,
                    FromDurableIntent = incumbent != null,
                    DurableFundingTier = incumbent?.Funding ?? CommitmentTier.None,
                    Explain = $"economy {kind} @({target.TargetHex.Q},{target.TargetHex.R}) intrinsic={d.Value:0.##} site={target.BuildValue:0.##}",
                };
                m.Axes.Value[DesireAxis.Economy] = 1f;
                // CauseDemandTraceIds is computed once, downstream, by
                // AiV2Trace.CorrelateDemandsToMissions — do not write it here (single-owner rule,
                // spec §1.6 / file-split Task 1).
                result.Add(m);
            }
            return result;
        }

        private static MissionRequirements Requirements(EconomyMissionTarget t,
            MissionIntent incumbent, WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments)
        {
            if (t.Kind == EconomyTaskKind.MobileCollection
                || t.Kind == EconomyTaskKind.ReturnCollector)
            {
                int? actorId = incumbent?.PreferredMoverArmyId ?? t.CollectorArmyId;
                ArmySnapshot collector = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                    && actorId.HasValue && a.ArmyId == actorId.Value);
                float collectorActivation = collector != null && !collector.HasActivatedThisTurn
                    && !collector.Hex.Equals(t.TargetHex) ? collector.ActivationApCost : 0f;
                int distance = collector == null ? 0
                    : HexGridMath.Distance(collector.Hex, t.TargetHex);
                return new MissionRequirements
                {
                    RequiresArmy = true, RequiresHero = false, MoverKnown = collector != null,
                    ApMinimum = collectorActivation, ApDesired = collectorActivation, ApMaximum = collectorActivation,
                    EstimatedDistance = distance,
                    EtaTurns = collector == null ? 0 : UnityEngine.Mathf.CeilToInt(distance
                        / (float)UnityEngine.Mathf.Max(1, collector.MaxMovement)),
                };
            }
            if (t.Kind == EconomyTaskKind.ReturnBuilder)
            {
                ArmySnapshot recoveryActor = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                    && t.BuilderArmyId.HasValue && a.ArmyId == t.BuilderArmyId.Value);
                float recoveryActivation = recoveryActor != null
                    && !recoveryActor.HasActivatedThisTurn ? recoveryActor.ActivationApCost : 0f;
                int recoveryDistance = recoveryActor == null ? 0
                    : HexGridMath.Distance(recoveryActor.Hex, t.TargetHex);
                return new MissionRequirements
                {
                    RequiresArmy = true, RequiresHero = true, MoverKnown = true,
                    ApMinimum = recoveryActivation, ApDesired = recoveryActivation,
                    ApMaximum = recoveryActivation, EstimatedDistance = recoveryDistance,
                    EtaTurns = recoveryActor == null ? 0
                        : UnityEngine.Mathf.CeilToInt(recoveryDistance
                            / (float)UnityEngine.Mathf.Max(1, recoveryActor.MaxMovement)),
                };
            }

            int? preferredId = incumbent?.PreferredMoverArmyId ?? t.BuilderArmyId;
            IReadOnlyList<EconomyBuilderRouteSnapshot> currentRoutes =
                CurrentBuilderRoutes(snapshot, t);
            DemandLayer.EconomyBuilderChoice currentBuilder =
                DemandLayer.SelectEconomyBuilder(
                snapshot, t.TargetHex, currentRoutes, activeIntents, commitments,
                t.BuildValue, t.BuildApCost, includeReturn: false,
                pinnedBuilderArmyId: preferredId);
            EconomyBuilderRouteSnapshot? routeWitness = currentBuilder?.Route;
            ArmySnapshot actor = currentBuilder?.Army;

            var r = new MissionRequirements
            {
                RequiresArmy = true,
                RequiresHero = true,
                // DemandLayer remains the single owner of safe escort/lighten/reinforce
                // projection. Without its current-snapshot witness the allocator must not inherit
                // the previous position or roster's cost.
                MoverKnown = actor != null && routeWitness.HasValue,
            };
            bool completionThisTurn = false;
            float activation = 0f;
            if (actor != null && routeWitness.HasValue)
            {
                EconomyBuilderRouteSnapshot route = routeWitness.Value;
                int distance = route.TravelCost;
                int movement = route.CurrentMovement;
                bool travelNeeded = distance > 0;
                completionThisTurn = distance <= movement;
                activation = travelNeeded && !route.HasActivatedThisTurn
                    ? route.ActivationApCost : 0f;
                r.EstimatedDistance = distance;
                r.EtaTurns = completionThisTurn ? 0
                    : UnityEngine.Mathf.CeilToInt(
                        UnityEngine.Mathf.Max(0, distance - movement)
                            / (float)UnityEngine.Mathf.Max(1, route.MaxMovement));
            }

            float ap = UnityEngine.Mathf.Max(0f, activation
                + (completionThisTurn
                    ? UnityEngine.Mathf.Max(t.BuildApCost, t.MinimumFollowupAp)
                    : 0f));
            r.ApMinimum = r.ApDesired = r.ApMaximum = ap;

            // A multi-turn delivery is funded for the step it can execute now. Full build resources
            // and follow-up AP enter the envelope only when the witnessed actor can reach the target
            // this turn; InfrastructureFulfillment protects the persistent H/E/M/T vector between
            // turns so Phase B cannot spend it meanwhile.
            ResourceCost cost = completionThisTurn ? t.BuildResourceCost : null;
            if (cost != null)
            {
                r.HumanMinimum = r.HumanDesired = r.HumanMaximum = cost.Get(ResourceType.Human);
                r.EnergyMinimum = r.EnergyDesired = r.EnergyMaximum = cost.Get(ResourceType.Energy);
                r.MaterialsMinimum = r.MaterialsDesired = r.MaterialsMaximum = cost.Get(ResourceType.Materials);
                r.TechMinimum = r.TechDesired = r.TechMaximum = cost.Get(ResourceType.Tech);
            }
            return r;
        }

        private static IReadOnlyList<EconomyBuilderRouteSnapshot> CurrentBuilderRoutes(
            WorldSnapshot snapshot, EconomyMissionTarget target)
        {
            if (target.Kind == EconomyTaskKind.BuildExtraction)
            {
                foreach (EconomyExtractionOpportunity opportunity
                         in snapshot?.Economy?.ExtractionOpportunities
                            ?? System.Array.Empty<EconomyExtractionOpportunity>())
                    if (opportunity.Hex.Equals(target.TargetHex)
                        && (!target.ResourceType.HasValue
                            || opportunity.ResourceType == target.ResourceType.Value))
                        return opportunity.BuilderRoutes
                            ?? System.Array.Empty<EconomyBuilderRouteSnapshot>();
                return System.Array.Empty<EconomyBuilderRouteSnapshot>();
            }

            if (target.Kind == EconomyTaskKind.FoundBase)
            {
                foreach (EconomyBaseOpportunity opportunity
                         in snapshot?.Economy?.BaseOpportunities
                            ?? System.Array.Empty<EconomyBaseOpportunity>())
                    if (opportunity.Hex.Equals(target.TargetHex))
                        return opportunity.BuilderRoutes
                            ?? System.Array.Empty<EconomyBuilderRouteSnapshot>();
                return System.Array.Empty<EconomyBuilderRouteSnapshot>();
            }

            return System.Array.Empty<EconomyBuilderRouteSnapshot>();
        }

        public static string OwnerKey(StableMissionKey key) => $"Economy:{key}";
    }
}
