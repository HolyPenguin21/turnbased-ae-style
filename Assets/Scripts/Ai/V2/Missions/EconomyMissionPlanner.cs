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
        // Why an Active durable Economy intent has no step this pass (null = it has one). The one
        // rule for this planner's own skip and for Continuity's funding diagnostics.
        //  · actor_no_movement_this_cycle — travel needs MP the actor no longer has; completion on
        //    the target hex is still allowed with zero MP, and refreshed movement admits it again.
        //  · collector_holding_site — a collector already on its site holds it for the income
        //    tick; Continuity records the hold (ResolveActive). Proposing it would make a zero-AP
        //    commitment the first funded task of every typed admission and stop the loop.
        internal static string DeferredThisPass(MissionIntent intent, WorldSnapshot snapshot)
        {
            EconomyIntent e = intent?.Economy;
            if (e == null || !intent.PreferredMoverArmyId.HasValue)
                return null;
            ArmySnapshot actor = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == intent.PreferredMoverArmyId.Value);
            if (actor == null)
                return null;
            if (actor.CurrentMovement <= 0 && !actor.Hex.Equals(e.TargetHex))
                return "actor_no_movement_this_cycle";
            if (e.Kind == EconomyTaskKind.MobileCollection && actor.Hex.Equals(e.TargetHex))
                return "collector_holding_site";
            return null;
        }

        public static List<MissionProposal> Propose(WorldSnapshot snapshot,
            DesireBreakdown breakdown, IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<AxisDemand> demands,
            IDictionary<MissionIntentKey, string> deferredThisPass = null)
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
                // A per-pass execution admission, not cancellation of the durable intent: Phase B
                // can re-enter the loop after changing hand/resources, but that never refills the
                // committed builder's movement, so the same impossible step is not re-funded.
                string deferred = DeferredThisPass(intent, snapshot);
                if (deferred != null)
                {
                    if (deferredThisPass != null)
                        deferredThisPass[intent.IntentKey] = deferred;
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
                    // Provisioning ranks builders over the SAME witnessed routes Requirements prices
                    // below — never the raw-distance fallback a missing refreshed demand used to
                    // leave it with (audit B13).
                    BuilderRoutes = refreshed?.EconomyBuilderRoutes
                        ?? CurrentBuilderRoutes(snapshot, new EconomyMissionTarget
                        {
                            Kind = e.Kind, TargetHex = e.TargetHex, ResourceType = e.ResourceType,
                        }),
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
                    // A return leg is valueless; a refreshed demand brings its breakdown; a value
                    // restored from the durable intent alone has none.
                    Score = e.Kind == EconomyTaskKind.ReturnBuilder
                        || e.Kind == EconomyTaskKind.ReturnCollector ? default(TaskScore)
                        : refreshed?.WorldTaskScore,
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

            // Mobile collection is an economy mission in its own right. Analysis publishes every
            // viable actor/site fact row; this task owner applies the one external TaskScore and
            // chooses the same best collector per (site, resource) before mission admission.
            IEnumerable<IGrouping<(HexCoord TargetHex, ResourceType ResourceType),
                MobileCollectionOpportunity>> mobileGroups =
                (snapshot?.Economy?.MobileCollectionOpportunities
                    ?? System.Array.Empty<MobileCollectionOpportunity>())
                .GroupBy(op => (op.TargetHex, op.ResourceType));
            foreach (IGrouping<(HexCoord TargetHex, ResourceType ResourceType),
                         MobileCollectionOpportunity> group in mobileGroups)
            {
                if (activeIntents?.Any(i => i?.Status == IntentStatus.Active
                    && i.Economy?.Kind == EconomyTaskKind.MobileCollection
                    && i.Economy.ResourceType == group.Key.ResourceType
                    && i.Economy.TargetHex.Equals(group.Key.TargetHex)) == true)
                    continue;

                MobileCollectionOpportunity? best = null;
                TaskScore bestScore = default;
                foreach (MobileCollectionOpportunity candidate in group.OrderBy(x => x.CollectorArmyId))
                {
                    TaskScore candidateScore = DemandLayer.BuildMobileCollectionScore(snapshot, candidate);
                    if (candidateScore.Value <= AiConfigV2.allocatorSliceEpsilon)
                        continue;
                    if (!best.HasValue
                        || candidateScore.Value > bestScore.Value
                        || (UnityEngine.Mathf.Approximately(candidateScore.Value, bestScore.Value)
                            && candidate.CollectorArmyId < best.Value.CollectorArmyId))
                    {
                        best = candidate;
                        bestScore = candidateScore;
                    }
                }
                if (!best.HasValue)
                    continue;
                MobileCollectionOpportunity op = best.Value;
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
                    BaseValue = bestScore.Value,
                    Score = bestScore,
                    LocalAdmissionScore = bestScore.Value,
                    PreferredMoverArmyId = op.CollectorArmyId,
                    Requirements = Requirements(target, null, snapshot, activeIntents,
                        currentCommitments),
                    Explain = $"economy mobile-collect {op.ResourceType} actor=#{op.CollectorArmyId} "
                        + $"@({op.TargetHex.Q},{op.TargetHex.R}) value={bestScore.Value:0.##}",
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
                EconomyTaskKind kind = DemandLayer.EconomyBuildKind(d);
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
                };
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i =>
                    MissionContinuityLayer.HoldsEconomyBuildSite(i, target.TargetHex)
                    && i.Economy.Kind == kind);
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
                    Score = d.WorldTaskScore,
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
                pinnedBuilderArmyId: preferredId,
                requiresFoundingGarrison: t.Kind == EconomyTaskKind.FoundBase);
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
            bool travelNeeded = false;
            bool activated = false;
            float activation = 0f;
            if (actor != null && routeWitness.HasValue)
            {
                EconomyBuilderRouteSnapshot route = routeWitness.Value;
                int distance = route.TravelCost;
                int movement = route.CurrentMovement;
                travelNeeded = distance > 0;
                completionThisTurn = distance <= movement;
                activation = route.ActivationApCost;
                activated = route.HasActivatedThisTurn;
                r.EstimatedDistance = distance;
                r.EtaTurns = completionThisTurn ? 0
                    : UnityEngine.Mathf.CeilToInt(
                        UnityEngine.Mathf.Max(0, distance - movement)
                            / (float)UnityEngine.Mathf.Max(1, route.MaxMovement));
            }

            float ap = DemandLayer.EconomyCurrentStageAp(currentBuilder?.PreparationApCost ?? 0f,
                activation, activated, travelNeeded, completionThisTurn,
                t.BuildApCost, t.MinimumFollowupAp);
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

        internal static ReservationOwner ReservationIdentity(StableMissionKey key) =>
            ReservationOwner.ForOperation(MissionIntentKey.ForEconomy((EconomyTaskKind)key.SubKind,
                key.TargetId, new HexCoord(key.Q, key.R)));

        public static string OwnerKey(StableMissionKey key) => $"Economy:{key}";
    }
}
