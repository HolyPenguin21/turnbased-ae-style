using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;

namespace Game.Ai.V2
{
    // Existing Missions stage, not a production-specific mover/manager. Preparation supplies
    // the profitable, exact hero+facility witness; Provisioning alone binds a legal actor.
    internal static class DevelopmentMissionPlanner
    {
        internal static List<MissionProposal> Propose(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, IReadOnlyList<AxisDemand> demands)
        {
            var missions = new List<MissionProposal>();
            var occupiedHeroes = new HashSet<Game.Units.UnitData>();
            var occupiedSites = new HashSet<MissionIntentKey>();
            foreach (MissionIntent intent in activeIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (intent?.Kind != MissionKind.Development || intent.Development?.Hero == null
                    || intent.Status != IntentStatus.Active)
                    continue;
                DevelopmentIntent d = intent.Development;
                occupiedHeroes.Add(d.Hero);
                occupiedSites.Add(intent.IntentKey);
                DevelopmentMissionTarget target = new DevelopmentMissionTarget
                {
                    FacilityHex = d.FacilityHex, Mode = d.Mode, Hero = d.Hero,
                    HeroKey = d.HeroKey, SourceArmyId = intent.PreferredMoverArmyId,
                    IntrinsicValue = d.IntrinsicValue,
                };
                missions.Add(Create(target, snapshot, true, intent.Funding,
                    d.IntrinsicValue, intent.PreferredMoverArmyId));
            }

            foreach (AxisDemand demand in demands ?? System.Array.Empty<AxisDemand>())
            {
                DevelopmentOpportunity op = demand?.DevOpportunity;
                if (demand?.RequestingAxis != DesireAxis.Development
                    || demand.Capability != CapabilityKind.DevelopmentOperator
                    || op?.PreparationExistingHero == null || demand.Value <= 0f)
                    continue;
                var target = new DevelopmentMissionTarget
                {
                    FacilityHex = op.FacilityHex, Mode = op.Mode,
                    Hero = op.PreparationExistingHero,
                    HeroKey = GenerationSource.StableHeroKey(op.PreparationExistingHero),
                    SourceArmyId = op.PreparationSourceArmyId,
                    IntrinsicValue = demand.Value,
                };
                MissionProposal mission = Create(target, snapshot, false, CommitmentTier.None,
                    demand.Value, target.SourceArmyId);
                if (occupiedSites.Contains(MissionIntentKey.For(mission))
                    || occupiedHeroes.Contains(target.Hero))
                    continue;
                missions.Add(mission);
                occupiedSites.Add(MissionIntentKey.For(mission));
                occupiedHeroes.Add(target.Hero);
            }
            return missions;
        }

        private static MissionProposal Create(DevelopmentMissionTarget target, WorldSnapshot snapshot,
            bool committed, CommitmentTier funding, float value, int? preferred)
        {
            ArmySnapshot actor = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                && preferred.HasValue && a.ArmyId == preferred.Value);
            int distance = actor == null ? 0 : HexGridMath.Distance(actor.Hex, target.FacilityHex);
            float activation = actor != null && !actor.IsGarrison && distance > 0
                && !actor.HasActivatedThisTurn ? actor.ActivationApCost : 0f;
            // The garrison's entire roster is NOT the mover. Its legal, pinned extraction
            // envelope is calculated in Provisioning, using the canonical extraction tiers.
            // A zero-cost reusable shell may exist, so keep ApMinimum at zero; but a
            // fresh container can require CreateArmy AP. Reserve that conservative cost
            // as the DESIRED (not only Maximum) envelope before lower-priority work
            // spends it. Provisioning resolves the real Shell/Host/Create price and
            // releases the difference through the existing allocator claim path.
            float upperBound = actor?.IsGarrison == true
                ? ArmyActions.CreateArmyApCost : activation;
            float desired = upperBound;
            var m = new MissionProposal
            {
                Kind = MissionKind.Development, Target = target,
                BaseValue = value, LocalAdmissionScore = value,
                PreferredMoverArmyId = preferred, FromDurableIntent = committed,
                DurableFundingTier = funding,
                Requirements = new MissionRequirements
                {
                    RequiresArmy = true, RequiresHero = true, MoverKnown = preferred.HasValue,
                    ApMinimum = 0f, ApDesired = desired, ApMaximum = upperBound,
                    EstimatedDistance = distance,
                    EtaTurns = actor == null ? 0 : UnityEngine.Mathf.CeilToInt(
                        distance / (float)UnityEngine.Mathf.Max(1, actor.MaxMovement)),
                },
                Explain = $"development {(committed ? "committed" : "fresh")} "
                    + $"{target.Mode} hero={target.HeroKey} -> "
                    + $"({target.FacilityHex.Q},{target.FacilityHex.R})",
            };
            m.Axes.Value[DesireAxis.Development] = 1f;
            return m;
        }
    }
}
