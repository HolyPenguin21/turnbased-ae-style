using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Development lane provisioning. A mechanical partial of ProvisioningManager; the kind
    // dispatcher in Provision (ProvisioningManager.cs) is still its only caller.

    internal static partial class ProvisioningManager
    {
        // Reuses the EXACT Economy garrison-extraction solver/claim fields and the canonical
        // SafeStepPathing route. No new mover, reservation ledger or actor assignment manager.
        private static ProvisioningResult ProvisionDevelopment(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            DevelopmentMissionTarget target)
        {
            MissionProposal mission = funded.Mission;
            if (target.Hero == null || target.Hero.Owner != player || !target.Hero.IsHero
                || target.Hero.IsPrisoner || !target.Hero.HasAbility(
                    ResearchProductionSystem.RoleAbility(target.Mode))
                || !string.Equals(target.HeroKey, GenerationSource.StableHeroKey(target.Hero),
                    StringComparison.Ordinal))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "bound Development hero lost or no longer qualified"));

            BuildingData facility = BuildingRegistry.FindAt(target.FacilityHex);
            if (facility == null || facility.Owner != player
                || !facility.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(target.Mode)))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "bound Development facility missing"));
            if (BattleInitiator.FindEnemyAt(target.FacilityHex, player) != null)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    "Development facility temporarily contested"));

            ArmyData army = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null
                && !a.IsPrison && a.Members.Contains(target.Hero));
            if (army == null || army.Owner != player)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "bound Development hero no longer belongs to a live own army"));
            if (ResearchProductionSystem.ActorStillQualifies(player, target.Hero,
                    target.FacilityHex, target.Mode))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    "exact Development hero arrived and can operate the facility"));
            if (army.Hex.Equals(target.FacilityHex))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "hero reached facility but cannot qualify for production"));
            if (mission.FromDurableIntent && mission.PreferredMoverArmyId.HasValue
                && army.Id != mission.PreferredMoverArmyId.Value)
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    "durable Development actor changed outside its bound extraction"));

            List<MissionIntent> intents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            MissionIntentKey ownKey = MissionIntentKey.For(mission);
            if (intents.Any(i => !i.IntentKey.Equals(ownKey)
                    && (i.PreferredMoverArmyId == army.Id
                        || i.Development?.Hero == target.Hero)))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "hero is committed to another active mission"));
            ActorCommitments claims = ActorCommitments.FromIntents(intents, session.Snapshot, null);
            if ((claims.IsArmyClaimed(army.Id)
                    && !intents.Any(i => i.IntentKey.Equals(ownKey)
                        && i.PreferredMoverArmyId == army.Id))
                || session.ClaimedArmyIds.Contains(army.Id)
                || DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, army.Hex))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "Development actor contested, threatened or already claimed"));

            bool extract = army.IsGarrison;
            if (!extract && (!AiArmyRoles.IsHeroLed(army)
                    || army.Members.Count(u => ReferenceEquals(u, target.Hero)) != 1))
                return ProvisioningResult.Fail(ProvisionFailure.AssemblyInfeasible(
                    "Development needs a unique qualified hero in a mobile ground army"));
            // Live source protection mirrors the preparation witness and never steals an
            // operator from a different working factory.
            BuildingData source = BuildingRegistry.FindAt(army.Hex);
            if (source != null && source.Owner == player
                && new[] { ResearchProductionMode.Research, ResearchProductionMode.Production }
                    .Any(mode => source.HasFacilityWithAbility(
                        ResearchProductionSystem.FacilityAbility(mode))
                        && ResearchProductionSystem.ActorStillQualifies(player,
                            target.Hero, army.Hex, mode)))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "hero is operating another working facility"));

            int travelCost = extract
                ? SafeStepPathing.FindSafePathCost(ctx.Map, player, army.Hex,
                    target.FacilityHex, target.Hero.MoveMax)
                : SafeStepPathing.FindSafePathCost(ctx.Map, army, target.FacilityHex);
            if (travelCost == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.DestinationUnreachable(
                    "no legal safe route to the Development facility"));

            GarrisonExtractionCandidate extraction = default;
            float requiredAp;
            if (extract)
            {
                extraction = ResolveGarrisonExtractionCandidate(player, army, claims,
                    session, root, funded.Tentative.Ap, target.Hero, target.Mode);
                if (extraction.Tier == GarrisonExtractionTier.None)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        extraction.Reason ?? "no legal extraction of the bound Development hero"));
                requiredAp = extraction.ApCost;
            }
            else
            {
                if (army.CurrentMovement <= 0)
                    return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                        "Development mover has no movement this turn"));
                requiredAp = army.HasActivatedThisTurn ? 0f : army.ActivationApCost;
            }
            if (requiredAp > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(requiredAp,
                    "Development transport AP envelope below extraction/activation"));
            if (requiredAp > root.ActionPoints - session.ApClaimed + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "Development transport cannot spend shared AP pool"));

            var provisioned = new ProvisionedMission
            {
                Mission = mission, Kind = MissionKind.Development,
                Key = StableMissionKey.For(mission), DevelopmentTarget = target,
                MoverArmyId = extract ? SyntheticGarrisonExtractionActorId(army.Id) : army.Id,
                FocusHex = target.FacilityHex, ExecutionHex = army.Hex,
                EconomyExtractionGarrisonArmyId = extract ? army.Id : -1,
                EconomyExtractionPlan = extraction, ClaimedAp = requiredAp,
            };
            AiDebugLog.Write($"[AI][V2][Development] bound hero={target.HeroKey} "
                + $"actor=#{army.Id} site=({target.FacilityHex.Q},{target.FacilityHex.R}) "
                + $"extract={extract} route={travelCost} ap={requiredAp:0.##}");
            return ProvisioningResult.Ok(provisioned);
        }
    }
}
