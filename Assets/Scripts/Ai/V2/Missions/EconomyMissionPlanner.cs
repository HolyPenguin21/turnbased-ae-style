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
                    BuildCard = e.BuildCard,
                    BuildResourceCost = e.BuildResourceCost,
                    BuildApCost = e.BuildApCost,
                    BuildValue = e.BuildValue,
                    MinimumFollowupAp = e.MinimumFollowupAp,
                };
                var mission = new MissionProposal
                {
                    Kind = MissionKind.Economy, Target = target,
                    BaseValue = e.BuildValue, LocalAdmissionScore = e.BuildValue,
                    Requirements = Requirements(target, intent, snapshot, -1f),
                    PreferredMoverArmyId = intent.PreferredMoverArmyId,
                    FromDurableIntent = true, DurableFundingTier = intent.Funding,
                    Explain = $"economy committed {target.Kind} #{intent.PreferredMoverArmyId.Value} "
                        + $"@({target.TargetHex.Q},{target.TargetHex.R})",
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
                if (snapshot?.Self?.Armies != null && snapshot.Self.Armies.Any(a => a != null
                    && a.HasHero && !a.IsPrison && !a.IsAir && !a.IsAirfield
                    && a.Hex.Equals(d.TargetHex.Value)))
                    continue; // direct Phase-A fulfillment owns an already-delivered build
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
                    BuildValue = d.EconomySiteValue > 0f ? d.EconomySiteValue : d.Value,
                    MinimumFollowupAp = d.MinimumFollowupAp,
                    BuilderArmyId = d.EconomyPreferredBuilderArmyId,
                    BuilderRoutes = d.EconomyBuilderRoutes,
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
                    // Cross-lane ordering represents the strategic return of the chosen site.
                    // Builder travel/opportunity cost already controls Demand admission and the
                    // concrete AP/resource envelope below; folding it into BaseValue again lets a
                    // routine one-step Recon refresh permanently outrank an admitted economy plan.
                    BaseValue = target.BuildValue,
                    // Wait urgency is lane-local: it can overtake repeated Extraction contention
                    // without inflating cross-axis value above critical Defence/Reaction.
                    LocalAdmissionScore = d.Value + d.EconomyStrategicUrgency,
                    Requirements = Requirements(target, incumbent, snapshot,
                        d.EconomyTravelCost),
                    PreferredMoverArmyId = incumbent?.PreferredMoverArmyId
                        ?? d.EconomyPreferredBuilderArmyId,
                    FromDurableIntent = incumbent != null,
                    DurableFundingTier = incumbent?.Funding ?? CommitmentTier.None,
                    Explain = $"economy {kind} @({target.TargetHex.Q},{target.TargetHex.R}) site={target.BuildValue:0.0}",
                };
                m.Axes.Value[DesireAxis.Economy] = 1f;
                if (!string.IsNullOrEmpty(d.TraceId)) m.CauseDemandTraceIds.Add(d.TraceId);
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
            List<ArmySnapshot> heroes = snapshot?.Self?.Armies?
                .Where(a => a != null && (a.IsMobileEconomyBuilder
                    || (a.IsGarrison && a.HasHero && a.Hex.Equals(t.TargetHex)))).ToList();
            ArmySnapshot nearest = null;
            bool completionThisTurn = true; // Conservative fallback when Analysis has no actor witness.
            float activation = 0f;
            if (heroes != null && heroes.Count > 0)
            {
                nearest = preferredId.HasValue
                    ? heroes.FirstOrDefault(a => a.ArmyId == preferredId.Value)
                    : null;
                nearest ??= heroes.OrderBy(a => HexGridMath.Distance(a.Hex, t.TargetHex))
                    .ThenBy(a => a.ArmyId).First();
                int distance = witnessedTravelCost >= 0f
                    ? UnityEngine.Mathf.CeilToInt(witnessedTravelCost)
                    : HexGridMath.Distance(nearest.Hex, t.TargetHex);
                bool travelNeeded = distance > 0;
                completionThisTurn = distance <= nearest.CurrentMovement;
                activation = travelNeeded && !nearest.HasActivatedThisTurn
                    ? nearest.ActivationApCost : 0f;
                r.EstimatedDistance = distance;
                r.EtaTurns = completionThisTurn ? 0
                    : UnityEngine.Mathf.CeilToInt(
                        UnityEngine.Mathf.Max(0, distance - nearest.CurrentMovement)
                        / (float)UnityEngine.Mathf.Max(1, nearest.MaxMovement));
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
