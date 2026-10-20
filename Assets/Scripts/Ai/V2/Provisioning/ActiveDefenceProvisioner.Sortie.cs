using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    internal static partial class ActiveDefenceProvisioner
    {
        private static ProvisioningResult ProvisionSortie(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded, ActiveDefenceMissionTarget target)
        {
            GarrisonSortiePlan p = target.Sortie;
            HashSet<int> excluded = session.ExcludedForGroundCombat(funded.Mission);
            if (excluded.Contains(p.DonorGarrisonId) || p.ShellArmyId.HasValue && excluded.Contains(p.ShellArmyId.Value))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended("sortie donor or shell claimed"));
            if (p.TotalAp > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon
                || root == null || p.TotalAp > root.ActionPoints - session.ApClaimed + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(p.TotalAp, "sortie complete AP envelope unavailable"));
            ActorCommitments commitments = ActorCommitments.FromIntents(WorldSnapshot.IntentsOf(session.Snapshot), session.Snapshot, null);
            if (!TryResolveSortie(player, root, ctx, target, commitments, funded.Tentative.Ap,
                out _, out _, out _, out string why))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(why));
            return ProvisioningResult.Ok(new ProvisionedMission { Mission = funded.Mission,
                Key = StableMissionKey.For(funded.Mission), Kind = MissionKind.ActiveDefence,
                MoverArmyId = p.Detachment.ArmyId, FocusHex = p.EnemyHex, ExecutionHex = p.EnemyHex,
                ActiveDefenceTarget = target, ClaimedAp = p.TotalAp, ClaimedPhysical = funded.PhysicalDraw });
        }

        internal static bool TryResolveSortie(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ActiveDefenceMissionTarget target, ActorCommitments commitments, float apEnvelope,
            out ArmyData donor, out ArmyData shell, out List<UnitData> units, out string why)
        {
            donor = null; shell = null; units = null; why = "invalid pending sortie";
            GarrisonSortiePlan p = target.Sortie;
            if (!target.RequiresSortieMaterialization || target.PrimaryArmyId != p.ShellArmyId) return false;
            if (p.ShellArmyId.HasValue)
            {
                shell = AiV2Util.ResolveArmy(player, p.ShellArmyId.Value);
                if (shell == null) { why = "pinned shell lost"; return false; }
            }
            else if (ctx?.StartingDeckCatalog?.GetCatalog(player.Faction) == null
                || ctx.HexSelection?.Map == null || !ctx.Map.CanEnter(p.StrongholdHex))
            { why = "canonical creation unavailable"; return false; }
            if (commitments?.IsArmyClaimed(p.DonorGarrisonId) == true
                || !TryResolveGarrisonExtraction(player, p.DonorGarrisonId, p.StrongholdHex,
                    p.UnitRuntimeIds, shell, commitments, !p.ShellArmyId.HasValue, out donor, out units, out why)) return false;
            ArmyData prospective = shell ?? ArmyData.CreateVisualSnapshot();
            if (shell == null) { prospective.Owner = player; prospective.Hex = donor.Hex; }
            if (!ArmyActions.CanTransferMembers(units, donor, prospective, out why)
                || !TryEvaluateSortie(player, ctx, target, donor.Hex, units, out why)) return false;
            int totalAp = p.MaterializationAp + ArmyData.ComputeActivationApCost(units);
            if (totalAp != p.TotalAp || totalAp > apEnvelope + AiConfigV2.allocatorSliceEpsilon
                || root == null || !root.CanSpendActionPoints(totalAp)
                || totalAp > StrategicSpendability.SpendableAp(player, root, ctx) + AiConfigV2.allocatorSliceEpsilon)
            { why = "sortie AP no longer covers creation and onward activation"; return false; }
            return true;
        }

        private static WorldSnapshot CurrentSortieKnowledge(PlayerSetupData player, AiTurnContext ctx) =>
            new WorldSnapshot { Observer = player, Map = ctx.Map, Known = WorldAnalysis.BuildKnown(player, null) };

        private static List<WorthIt.DefendingArmy> SortieOpposition(PlayerSetupData player,
            AiTurnContext ctx, GarrisonSortiePlan p)
        {
            var into = new List<WorthIt.DefendingArmy>();
            // Includes the target itself: every known defender on this pinned hex is one canonical fight.
            AiV2Util.AddKnownCoOccupants(CurrentSortieKnowledge(player, ctx), p.EnemyHex, -1, into);
            return into;
        }

        internal static bool TryEvaluateSortie(PlayerSetupData player, AiTurnContext ctx,
            ActiveDefenceMissionTarget target, HexCoord from, IReadOnlyList<UnitData> units, out string why)
        {
            why = "sortie witness no longer valid";
            GarrisonSortiePlan p = target.Sortie;
            if (p == null || ctx?.Map == null || target.Phase != ActiveDefencePhase.Intercept
                || target.InterceptPurpose != ActiveDefenceInterceptPurpose.ImmediateOpportunity
                || target.ImmediateTurn != ctx.TurnNumber || p.EnemyArmyId != target.EnemyArmyId
                || !p.EnemyHex.Equals(target.LastKnownHex) || !p.StrongholdHex.Equals(target.ProtectedAssetHex)
                || ctx.HexSelection != null && ctx.HexSelection.IsBattleActive
                || units == null || units.Any(u => !AiArmyRoles.IsGroundBattleBody(u)
                    || u.IsPrisoner || u.HitPointsCurrent <= 0 || u.Owner != player)
                || !p.MatchesRoster(units.Select(u => u.RuntimeId))) return false;
            if (units.Count == 1 && AbilityParams.UnitHasAnyRecce(units[0])) return false;
            BuildingData home = OwnWorld.Of(player).BuildingAt(p.StrongholdHex);
            ArmyData donor = AiV2Util.ResolveArmy(player, p.DonorGarrisonId);
            UnitData cover = donor?.Members.FirstOrDefault(u => u != null && u.RuntimeId == p.CoverRuntimeId);
            if (home == null || !(home.IsBase || home.IsStartingCitadel) || donor == null
                || !donor.IsGarrison || !donor.Hex.Equals(p.StrongholdHex)
                || !AiArmyRoles.IsGroundBattleBody(cover) || cover.IsPrisoner || cover.HitPointsCurrent <= 0
                || units.Contains(cover))
            { why = "sortie mandatory stronghold cover lost"; return false; }
            // If another action raised the floor after extraction, re-check the identical batch
            // against a visual pre-extraction roster. No body is transferred and no id is consumed.
            if (target.IsMaterializedSortie)
            {
                ArmyData before = ArmyData.CreateVisualSnapshot();
                before.Owner = player; before.Hex = donor.Hex; before.IsGarrison = true;
                before.Members.AddRange(donor.Members); before.Members.AddRange(units);
                if (!AiArmyRoles.CanSpareGarrisonMembers(player, before, units))
                { why = "sortie bodies now required by the garrison floor"; return false; }
            }
            var knowledge = CurrentSortieKnowledge(player, ctx);
            AiMapMemory.KnownEnemySighting? sighting = knowledge.Known.EnemySightings
                .Where(s => s.ArmyId == p.EnemyArmyId && s.Hex.Equals(p.EnemyHex) && !s.IsAir
                    && s.Owner != null && s.Owner != player && !s.Owner.IsNeutral && !s.Owner.IsEliminated)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            if (!sighting.HasValue || !VisionSystem.IsVisible(player, p.EnemyHex)
                || ActiveDefenceObjectiveEvaluator.OnKnownForeignStructure(knowledge, p.EnemyHex))
            { why = "sortie target moved, became hidden or moved onto a foreign structure"; return false; }
            var opposition = new List<WorthIt.DefendingArmy>();
            AiV2Util.AddKnownCoOccupants(knowledge, p.EnemyHex, -1, opposition);
            var profiles = units.Select(WorthIt.FromLiveUnit).ToList();
            if (ActiveDefenceObjectiveEvaluator.IsSignificantHostilePower(AiPower.SightingPower(sighting.Value))
                || !ActiveDefenceObjectiveEvaluator.SortieClears(profiles, opposition, out _, out _)
                || !ActiveDefenceObjectiveEvaluator.CanReachNow(ctx.Map, player, from, p.EnemyHex,
                    ArmyData.ComputeCurrentMovement(units), ArmyData.ComputeMaxMovement(units), out _))
            { why = "sortie fresh combat or same-turn movement gate failed"; return false; }
            why = null; return true;
        }
    }
}
