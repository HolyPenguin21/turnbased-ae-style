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
            foreach (MissionIntent recovery in activeIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (recovery?.Kind != MissionKind.Economy
                    || recovery.Status != IntentStatus.Active
                    || recovery.Economy?.Kind != EconomyTaskKind.ReturnBuilder
                    || !recovery.PreferredMoverArmyId.HasValue)
                    continue;
                EconomyIntent e = recovery.Economy;
                var target = new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.ReturnBuilder,
                    TargetHex = e.TargetHex,
                    ObjectiveId = $"ReturnBuilder:{recovery.PreferredMoverArmyId.Value}",
                    BuilderArmyId = recovery.PreferredMoverArmyId,
                    BuildValue = e.BuildValue,
                };
                var mission = new MissionProposal
                {
                    Kind = MissionKind.Economy, Target = target,
                    BaseValue = e.BuildValue, LocalAdmissionScore = e.BuildValue,
                    Requirements = Requirements(target, recovery, snapshot, -1f),
                    PreferredMoverArmyId = recovery.PreferredMoverArmyId,
                    FromDurableIntent = true, DurableFundingTier = recovery.Funding,
                    Explain = $"economy ReturnBuilder #{recovery.PreferredMoverArmyId.Value} "
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
                var m = new MissionProposal
                {
                    Kind = MissionKind.Economy,
                    Target = target,
                    BaseValue = d.Value,
                    LocalAdmissionScore = d.Value,
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

            float activation = 0f;
            int? preferredId = incumbent?.PreferredMoverArmyId ?? t.BuilderArmyId;
            if (preferredId is int id)
            {
                ArmySnapshot a = snapshot?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id);
                activation = a != null && !a.HasActivatedThisTurn ? a.ActivationApCost : 0f;
            }
            float ap = UnityEngine.Mathf.Max(0f, activation
                + UnityEngine.Mathf.Max(t.BuildApCost, t.MinimumFollowupAp));
            var r = new MissionRequirements
            {
                RequiresArmy = true, RequiresHero = true, MoverKnown = preferredId.HasValue,
                ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
            };
            List<ArmySnapshot> heroes = snapshot?.Self?.Armies?
                .Where(a => a != null && (a.IsMobileEconomyBuilder
                    || (a.IsGarrison && a.HasHero && a.Hex.Equals(t.TargetHex)))).ToList();
            if (heroes != null && heroes.Count > 0)
            {
                ArmySnapshot nearest = preferredId.HasValue
                    ? heroes.FirstOrDefault(a => a.ArmyId == preferredId.Value)
                    : null;
                nearest ??= heroes.OrderBy(a => HexGridMath.Distance(a.Hex, t.TargetHex))
                    .ThenBy(a => a.ArmyId).First();
                int distance = witnessedTravelCost >= 0f
                    ? UnityEngine.Mathf.CeilToInt(witnessedTravelCost)
                    : HexGridMath.Distance(nearest.Hex, t.TargetHex);
                r.EstimatedDistance = distance;
                r.EtaTurns = distance <= nearest.CurrentMovement ? 0
                    : UnityEngine.Mathf.CeilToInt(distance / (float)UnityEngine.Mathf.Max(1, nearest.MaxMovement));
            }
            ResourceCost c = t.BuildResourceCost;
            if (c != null)
            {
                r.HumanMinimum = r.HumanDesired = r.HumanMaximum = c.Get(ResourceType.Human);
                r.EnergyMinimum = r.EnergyDesired = r.EnergyMaximum = c.Get(ResourceType.Energy);
                r.MaterialsMinimum = r.MaterialsDesired = r.MaterialsMaximum = c.Get(ResourceType.Materials);
                r.TechMinimum = r.TechDesired = r.TechMaximum = c.Get(ResourceType.Tech);
            }
            return r;
        }

        public static string OwnerKey(StableMissionKey key) => $"Economy:{key}";
    }
}
