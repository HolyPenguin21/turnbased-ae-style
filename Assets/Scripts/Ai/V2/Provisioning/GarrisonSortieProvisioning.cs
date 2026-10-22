using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // The lane-neutral provisioning of a garrison sortie and of a pinned garrison extraction,
    // shared by ActiveDefence (immediate intercept, garrison relief) and Attack (second operation).
    // Each lane adds only its own fight test and its own mission shape.
    internal static class GarrisonSortieProvisioning
    {
        // The admission every sortie lane runs first: the donor and shell are not claimed by
        // another leg, and both the funded envelope and the turn pool pay the whole plan.
        internal static ProvisionFailure? Admit(ProvisioningSession session, FundedEntry funded,
            PlayerRoot root, GarrisonSortiePlan p, string lane)
        {
            HashSet<int> excluded = session.ExcludedForGroundCombat(funded.Mission);
            if (excluded.Contains(p.DonorGarrisonId) || p.ShellArmyId.HasValue && excluded.Contains(p.ShellArmyId.Value))
                return ProvisionFailure.MoverContended(Prefix(lane) + "sortie donor or shell claimed");
            if (p.TotalAp > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon
                || !session.FitsTurnAp(root, p.TotalAp))
                return ProvisionFailure.EnvelopeTooSmall(p.TotalAp, Prefix(lane) + "sortie complete AP envelope unavailable");
            return null;
        }

        private static string Prefix(string lane) => string.IsNullOrEmpty(lane) ? "" : lane + " ";

        // 2026-10-21 — the lane-neutral half of a pinned garrison sortie, shared by ActiveDefence's
        // immediate intercept and Attack's second operation:
        // the pinned shell (or a canonical creation), the donor's extraction under its floor, the
        // transfer legality and the complete AP envelope. `evaluate` is the lane's own fight test
        // of the exact bodies from the donor's hex. Pure: nothing is moved here.
        internal static bool TryResolve(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            GarrisonSortiePlan p, ActorCommitments commitments, float apEnvelope,
            Func<HexCoord, List<UnitData>, (bool ok, string why)> evaluate,
            out ArmyData donor, out ArmyData shell, out List<UnitData> units, out string why)
        {
            donor = null; shell = null; units = null; why = "invalid pending sortie";
            if (p == null || evaluate == null) return false;
            if (p.ShellArmyId.HasValue)
            {
                shell = AiV2Util.ResolveArmy(player, p.ShellArmyId.Value);
                if (shell == null) { why = "pinned shell lost"; return false; }
            }
            else if (ctx?.StartingDeckCatalog?.GetCatalog(player.Faction) == null
                || ctx.HexSelection?.Map == null || !ctx.Map.CanEnter(p.StrongholdHex))
            { why = "canonical creation unavailable"; return false; }
            if (commitments?.IsArmyClaimed(p.DonorGarrisonId) == true
                || !TryResolveExtraction(player, p.DonorGarrisonId, p.StrongholdHex,
                    p.UnitRuntimeIds, shell, commitments, !p.ShellArmyId.HasValue, out donor, out units, out why)) return false;
            ArmyData prospective = shell ?? ArmyData.CreateVisualSnapshot();
            if (shell == null) { prospective.Owner = player; prospective.Hex = donor.Hex; }
            if (!ArmyActions.CanTransferMembers(units, donor, prospective, out why)) return false;
            (bool fights, string fightWhy) = evaluate(donor.Hex, units);
            if (!fights) { why = fightWhy; return false; }
            int totalAp = p.MaterializationAp + ArmyData.ComputeActivationApCost(units);
            if (totalAp != p.TotalAp || totalAp > apEnvelope + AiConfigV2.allocatorSliceEpsilon
                || root == null || !root.CanSpendActionPoints(totalAp)
                || totalAp > StrategicSpendability.SpendableAp(player, root, ctx) + AiConfigV2.allocatorSliceEpsilon)
            { why = "sortie AP no longer covers creation and onward activation"; return false; }
            return true;
        }


        // The stronghold a sortie leaves keeps its pinned cover: an own Base/Citadel, its garrison
        // in place and the one body the plan never takes (shared by both sortie lanes).
        internal static bool CoverHolds(PlayerSetupData player, GarrisonSortiePlan p,
            IReadOnlyList<UnitData> units, out string why)
        {
            BuildingData home = OwnWorld.Of(player).BuildingAt(p.StrongholdHex);
            ArmyData donor = AiV2Util.ResolveArmy(player, p.DonorGarrisonId);
            UnitData cover = donor?.Members.FirstOrDefault(u => u != null && u.RuntimeId == p.CoverRuntimeId);
            if (home == null || !(home.IsBase || home.IsStartingCitadel) || donor == null
                || !donor.IsGarrison || !donor.Hex.Equals(p.StrongholdHex)
                || !AiArmyRoles.IsGroundBattleBody(cover) || cover.IsPrisoner || cover.HitPointsCurrent <= 0
                || units.Contains(cover))
            { why = "sortie mandatory stronghold cover lost"; return false; }
            why = null; return true;
        }

        internal static bool TryResolveExtraction(PlayerSetupData player, int? donorId,
            HexCoord? sourceHex, IReadOnlyList<int> ids, ArmyData shell, ActorCommitments commitments,
            bool allowCreate, out ArmyData donor, out List<UnitData> units, out string why)
        {
            donor = null; units = null; why = "no pinned extraction";
            if (!donorId.HasValue || !sourceHex.HasValue || ids == null || ids.Count == 0
                || ids.Distinct().Count() != ids.Count || shell == null && !allowCreate) return false;
            donor = AiV2Util.ResolveArmy(player, donorId.Value);
            if (donor == null || !donor.IsGarrison || donor.IsPrison || donor.Owner != player
                || !donor.Hex.Equals(sourceHex.Value))
            { why = "donor garrison gone or moved"; return false; }
            ArmyData resolved = donor;
            units = ids.Select(id => resolved.Members.FirstOrDefault(u => u != null && u.RuntimeId == id)).ToList();
            if (units.Any(u => u == null || u.HitPointsCurrent <= 0))
            { why = "planned bodies left the garrison or died"; return false; }
            if (shell != null && (shell.Owner != player || !shell.Hex.Equals(donor.Hex)
                || shell.Members.Count != 0 || shell.HasActivatedThisTurn || shell.IsGarrison || shell.IsPrison
                || shell.IsCarrierContainer || AviationRules.IsAirfield(shell) || AviationRules.IsAirArmy(shell)
                || commitments != null && !ReusableArmySelector.IsReusableShell(shell, player, commitments)))
            { why = "shell no longer an empty free container"; return false; }
            if (!AiArmyRoles.CanSpareGarrisonMembers(player, donor, units))
            { why = "garrison floor keeps the bodies"; return false; }
            why = null; return true;
        }
    }
}
