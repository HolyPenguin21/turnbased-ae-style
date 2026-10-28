using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Ai.V2
{
    // One immutable witness for extraction AND its same-turn intercept. Never a generic garrison actor.
    public sealed class GarrisonSortiePlan
    {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal GarrisonSortiePlan(bool __matchSave, int __DonorGarrisonId, int __CoverRuntimeId, int __EnemyArmyId, int __RouteCost, int? __ShellArmyId, global::Game.HexGrid.HexCoord __StrongholdHex, global::Game.HexGrid.HexCoord __EnemyHex, global::System.Collections.Generic.IReadOnlyList<int> __UnitRuntimeIds, global::Game.Ai.V2.ArmySnapshot __Detachment, float __WinChance, bool __CoversAllDefenders)
        {
            DonorGarrisonId = __DonorGarrisonId;
            CoverRuntimeId = __CoverRuntimeId;
            EnemyArmyId = __EnemyArmyId;
            RouteCost = __RouteCost;
            ShellArmyId = __ShellArmyId;
            StrongholdHex = __StrongholdHex;
            EnemyHex = __EnemyHex;
            UnitRuntimeIds = __UnitRuntimeIds;
            Detachment = __Detachment;
            WinChance = __WinChance;
            CoversAllDefenders = __CoversAllDefenders;
        }

        public readonly int DonorGarrisonId, CoverRuntimeId, EnemyArmyId, RouteCost;
        public readonly int? ShellArmyId;
        public readonly HexCoord StrongholdHex, EnemyHex;
        public readonly IReadOnlyList<int> UnitRuntimeIds;
        public readonly ArmySnapshot Detachment;
        public readonly float WinChance;
        public readonly bool CoversAllDefenders;
        public int MaterializationAp => ShellArmyId.HasValue ? 0 : ArmyActions.CreateArmyApCost;
        public int TotalAp => MaterializationAp + Detachment.ActivationApCost;
        internal GarrisonSortiePlan(ArmySnapshot donor, int cover, ArmySnapshot shell,
            ActiveDefenceMissionTarget target, IReadOnlyList<int> ids, ArmySnapshot detachment,
            int routeCost, float win, bool covers)
        {
            DonorGarrisonId = donor.ArmyId; CoverRuntimeId = cover; ShellArmyId = shell?.ArmyId;
            StrongholdHex = donor.Hex; EnemyArmyId = target.EnemyArmyId; EnemyHex = target.LastKnownHex;
            UnitRuntimeIds = Array.AsReadOnly(ids.ToArray()); Detachment = detachment;
            RouteCost = routeCost; WinChance = win; CoversAllDefenders = covers;
        }
        // 2026-10-21 — the same pinned detachment struck at a hostile Base (Attack's second
        // operation, AttackSecondaryPolicy.PlanGarrisonSortie): no enemy army, EnemyHex is the site.
        internal GarrisonSortiePlan(ArmySnapshot donor, int cover, ArmySnapshot shell, HexCoord siteHex,
            IReadOnlyList<int> ids, ArmySnapshot detachment, int routeCost, float win, bool covers)
        {
            DonorGarrisonId = donor.ArmyId; CoverRuntimeId = cover; ShellArmyId = shell?.ArmyId;
            StrongholdHex = donor.Hex; EnemyArmyId = -1; EnemyHex = siteHex;
            UnitRuntimeIds = Array.AsReadOnly(ids.ToArray()); Detachment = detachment;
            RouteCost = routeCost; WinChance = win; CoversAllDefenders = covers;
        }
        internal bool MatchesRoster(IEnumerable<int> ids) => ids != null
            && new HashSet<int>(ids).SetEquals(UnitRuntimeIds) && ids.Count() == UnitRuntimeIds.Count;
    }
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        internal static GarrisonSortiePlan PlanGarrisonSortie(WorldSnapshot snap,
            ActiveDefenceObjective objective, IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> excluded)
        {
            var target = objective.Target;
            var contact = snap.Threat?.Contacts?.FirstOrDefault(c => c?.Army?.ArmyId == target.EnemyArmyId
                && c.Knowledge == ContactKnowledge.Exact && c.Position.HasValue
                && c.Position.Value.Equals(target.LastKnownHex));
            if (target.InterceptPurpose != ActiveDefenceInterceptPurpose.ImmediateOpportunity
                || contact == null || IsSignificantHostilePower(contact.Army.EffectiveArmyPower)) return null;
            ArmySnapshot donor = snap.Self.Armies.FirstOrDefault(a => a != null && a.IsGarrison
                && a.Owner == snap.Observer && a.Hex.Equals(target.ProtectedAssetHex));
            if (donor == null || excluded?.Contains(donor.ArmyId) == true) return null;
            int? cover = GarrisonDetachment.WeakestCover(donor);
            if (!cover.HasValue) return null;
            ArmySnapshot shell = snap.Self.Armies.Where(a => a.Owner == snap.Observer
                && ReusableArmySelector.IsReusableShell(a, donor.Hex, excluded)).OrderBy(a => a.ArmyId).FirstOrDefault();
            int capacity = shell?.Capacity ?? ArmyData.ComputeCapacity(Array.Empty<UnitData>(), false);
            int actorId = shell?.ArmyId ?? -1;
            var chosen = new List<int>();
            foreach (int i in GarrisonDetachment.SpareBodyIndices(donor, requireSoloMovement: false)
                .Where(i => AiArmyRoles.IsGroundBattleBody(donor.Members[i]) && donor.Members[i].HitPoints > 0
                    && donor.NonHeroRuntimeIds[i] != cover.Value))
            {
                if (chosen.Count >= capacity) break;
                chosen.Add(i);
                ArmySnapshot projected = GarrisonDetachment.Project(donor, chosen, actorId, capacity, true);
                if (projected.MemberCount == 1 && (projected.Members[0].Abilities?.Any(a =>
                    AbilityParams.TryGetRecce(a, out _, out _)) ?? false)) continue;
                if (projected.EffectiveArmyPower <= contact.Army.EffectiveArmyPower
                    || !SortieClears(projected.Members, opposition, out float win, out bool covers)
                    || !CanReachNow(snap, projected, target.LastKnownHex, out int cost)) continue;
                if (DetachmentPinned(snap, donor, chosen, projected, excluded)) continue;
                return new GarrisonSortiePlan(donor, cover.Value, shell, target,
                    chosen.Select(c => donor.NonHeroRuntimeIds[c]).ToArray(), projected, cost, win, covers);
            }
            return null;
        }
        // Would the projected detachment be the last defender a threatened stronghold keeps, once
        // `chosen` left the donor garrison? (Shared by both sortie lanes.)
        internal static bool DetachmentPinned(WorldSnapshot snap, ArmySnapshot donor, IReadOnlyList<int> chosen,
            ArmySnapshot projected, ISet<int> excluded)
        {
            var remainingProfiles = donor.Members.Where((_, n) => !chosen.Contains(n)).ToList();
            var remaining = new ArmySnapshot { ArmyId = donor.ArmyId, Owner = donor.Owner, Hex = donor.Hex,
                IsGarrison = true, MemberCount = donor.MemberCount - chosen.Count, Members = remainingProfiles,
                Commander = donor.Commander, EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(remainingProfiles) };
            return IsPinnedStrongholdDefender(snap, projected, excluded,
                new Dictionary<int, ArmySnapshot> { [donor.ArmyId] = remaining });
        }

        internal static bool SortieClears(IReadOnlyList<WorthIt.DefenderProfile> members,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, out float win, out bool covers)
        {
            win = 0; covers = false;
            if (opposition == null || opposition.Count == 0) return false;
            float enemyPower = opposition.Sum(a => AiPower.EffectiveArmyPowerFromProfiles(
                a.Units.ToList(), Math.Max(0, a.DefenseBonus(0, members))));
            return AiPower.EffectiveArmyPowerFromProfiles(members) > enemyPower
                && GroundCombatFeasibility.Clears(members, default, opposition,
                    GroundCombatAdmissionPolicy.FreshStartWinChanceGate, 0, out win, out covers);
        }
        // A materialized sortie is re-admitted under the SAME strict gate and exact roster, even
        // though its intent is Hard. It never borrows more bodies or sheds a member on re-entry.
        internal static ActiveDefenceResponse AssessSortieIntercept(WorldSnapshot snap,
            ActiveDefenceObjective objective, GarrisonSortiePlan witness, int actorId, ISet<int> excluded)
        {
            var response = new ActiveDefenceResponse { Kind = ActiveDefenceResponseKind.Defer,
                Reason = "sortie_witness_invalid" };
            var actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == actorId);
            var contact = snap?.Threat?.Contacts?.FirstOrDefault(c => c?.Army?.ArmyId == witness?.EnemyArmyId);
            if (objective == null || witness == null || actor == null || !actor.IsStructuralRaidActor
                || !witness.MatchesRoster(actor.NonHeroRuntimeIds) || actor.MemberCount != witness.UnitRuntimeIds.Count
                || contact?.Knowledge != ContactKnowledge.Exact || !contact.Position.HasValue
                || !contact.Position.Value.Equals(witness.EnemyHex)
                || !objective.Target.ProtectedAssetHex.Equals(witness.StrongholdHex)
                || IsSignificantHostilePower(contact.Army.EffectiveArmyPower)
                || actor.EffectiveArmyPower <= contact.Army.EffectiveArmyPower
                || !CanReachNow(snap, actor, witness.EnemyHex, out _)
                || IsPinnedStrongholdDefender(snap, actor, excluded)) return response;
            var unavailable = new HashSet<int>(excluded ?? new HashSet<int>());
            unavailable.Remove(actorId);
            var opposition = KnownOpposition.OfEnemyArmy(snap, witness.EnemyArmyId);
            if (!SortieClears(actor.Members, opposition, out _, out _)) return response;
            var plan = GroundCombatAssemblyPlanner.Plan(snap, new GroundCombatAssemblyRequest {
                Opposition = opposition, PreferredPrimaryArmyId = actorId, PinToPreferred = true,
                ExcludedArmyIds = unavailable, WinChanceGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                AllowSameHexAssembly = false, PreserveDepartureRoster = true });
            if (!plan.Feasible) return response;
            response.Kind = ActiveDefenceResponseKind.Intercept; response.Reason = "pinned_sortie_intercept";
            response.Plan = plan; response.Opposition = opposition; response.ExcludedArmyIds = unavailable;
            return response;
        }
    }
}
