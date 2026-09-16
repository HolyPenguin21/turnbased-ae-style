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
            foreach (MissionIntent intent in activeIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (intent?.Kind != MissionKind.Economy
                    || intent.Status != IntentStatus.Active
                    || intent.Economy == null
                    || !intent.PreferredMoverArmyId.HasValue)
                    continue;
                EconomyIntent e = intent.Economy;
                AxisDemand refreshed = demands?.FirstOrDefault(d => d != null
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
                        : $"{e.Kind}:{e.TargetHex.Q},{e.TargetHex.R}",
                    // PreferredMoverArmyId is the continuity-owned actor identity. The payload is
                    // kept synchronized with it so provisioning never sees two competing builders.
                    BuilderArmyId = intent.PreferredMoverArmyId,
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
                float intrinsic = e.Kind == EconomyTaskKind.ReturnBuilder ? 0f
                    : refreshed?.Value ?? e.IntrinsicValue ?? 0f;
                var mission = new MissionProposal
                {
                    Kind = MissionKind.Economy, Target = target,
                    BaseValue = intrinsic, LocalAdmissionScore = intrinsic,
                    Requirements = Requirements(target, intent, snapshot,
                        refreshed?.EconomyTravelCost ?? -1f),
                    PreferredMoverArmyId = intent.PreferredMoverArmyId,
                    FromDurableIntent = true, DurableFundingTier = intent.Funding,
                    Explain = $"economy committed {target.Kind} #{intent.PreferredMoverArmyId.Value} "
                        + $"@({target.TargetHex.Q},{target.TargetHex.R}) intrinsic={intrinsic:0.##}",
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
                    // Wait urgency belongs only to lane-local admission, not intrinsic TaskScore.
                    LocalAdmissionScore = d.Value + d.EconomyStrategicUrgency,
                    Requirements = Requirements(target, incumbent, snapshot,
                        d.EconomyTravelCost),
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
            MissionIntent incumbent, WorldSnapshot snapshot, float witnessedTravelCost)
        {
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
            var r = new MissionRequirements
            {
                RequiresArmy = true, RequiresHero = true, MoverKnown = preferredId.HasValue,
            };
            // An off-site garrison is a real builder candidate only when Analysis witnessed a
            // sparable hero and its safe route. The GARRISON's MP/activation belong to the whole
            // stationary roster, not to the hero who will be extracted in Execution.
            EconomyBuilderRouteSnapshot? extractionRoute = null;
            if (preferredId.HasValue && t.BuilderRoutes != null)
                foreach (EconomyBuilderRouteSnapshot route in t.BuilderRoutes)
                    if (route.ArmyId == preferredId.Value && route.RequiresGarrisonExtraction)
                    {
                        extractionRoute = route;
                        break;
                    }
            List<ArmySnapshot> heroes = snapshot?.Self?.Armies?
                .Where(a => a != null && (a.IsMobileEconomyBuilder
                    || (a.IsGarrison && a.HasHero && (a.Hex.Equals(t.TargetHex)
                        || (extractionRoute.HasValue && a.ArmyId == preferredId.Value))))).ToList();
            ArmySnapshot nearest = null;
            // With a durable owner but no matching snapshot actor, NEVER price a different hero.
            // The allocator may still retry the commitment; provisioning owns actual validity.
            bool completionThisTurn = !preferredId.HasValue;
            float activation = 0f;
            if (heroes != null && heroes.Count > 0)
            {
                nearest = preferredId.HasValue
                    ? heroes.FirstOrDefault(a => a.ArmyId == preferredId.Value)
                    : null;
                if (!preferredId.HasValue)
                    nearest = heroes.OrderBy(a => HexGridMath.Distance(a.Hex, t.TargetHex))
                        .ThenBy(a => a.ArmyId).First();
            }
            if (nearest != null)
            {
                int distance = witnessedTravelCost >= 0f
                    ? UnityEngine.Mathf.CeilToInt(witnessedTravelCost)
                    : extractionRoute.HasValue ? extractionRoute.Value.TravelCost
                        : HexGridMath.Distance(nearest.Hex, t.TargetHex);
                int movement = extractionRoute.HasValue
                    ? extractionRoute.Value.CurrentMovement : nearest.CurrentMovement;
                bool activated = extractionRoute.HasValue
                    ? extractionRoute.Value.HasActivatedThisTurn : nearest.HasActivatedThisTurn;
                bool travelNeeded = distance > 0;
                completionThisTurn = distance <= movement;
                int projectedActivation = t.ProjectedActivationApCost > 0
                    ? t.ProjectedActivationApCost
                    : extractionRoute.HasValue ? extractionRoute.Value.ActivationApCost
                        : nearest.ActivationApCost;
                int projectedMove = t.ProjectedMaxMovement > 0
                    ? t.ProjectedMaxMovement : nearest.MaxMovement;
                activation = travelNeeded && !activated ? projectedActivation : 0f;
                r.EstimatedDistance = distance;
                r.EtaTurns = completionThisTurn ? 0
                    : UnityEngine.Mathf.CeilToInt(
                        UnityEngine.Mathf.Max(0, distance - movement)
                            / (float)UnityEngine.Mathf.Max(1, projectedMove));
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

        public static string OwnerKey(StableMissionKey key) => $"Economy:{key}";
    }
}
