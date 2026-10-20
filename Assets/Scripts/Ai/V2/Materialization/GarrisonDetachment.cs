using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Map;
using Game.Units;

namespace Game.Ai.V2
{
    // Shared snapshot mechanics for relief and immediate sorties. Safety belongs to
    // AiArmyRoles.CanSpareGarrisonMembers and is revalidated for the complete pinned batch.
    internal static class GarrisonDetachment
    {
        internal static IEnumerable<int> SpareBodyIndices(ArmySnapshot donor, bool requireSoloMovement = true) =>
            Enumerable.Range(0, donor?.Members?.Count ?? 0)
                .Where(i => donor.NonHeroSpareable != null && i < donor.NonHeroSpareable.Count
                    && donor.NonHeroSpareable[i] && i < donor.NonHeroRuntimeIds.Count
                    && !(i < donor.NonHeroIsAviation.Count && donor.NonHeroIsAviation[i])
                    && (!requireSoloMovement || i >= donor.NonHeroCurrentMovement.Count || donor.NonHeroCurrentMovement[i] > 0))
                .OrderByDescending(i => AiPower.EffectiveArmyPowerFromProfiles(new[] { donor.Members[i] }))
                .ThenBy(i => donor.NonHeroRuntimeIds[i]);

        internal static ArmySnapshot Project(ArmySnapshot donor, IReadOnlyList<int> chosen,
            int armyId, int capacity, bool projectTransportMovement = false)
        {
            int Of(IReadOnlyList<int> values, int i, int fallback) =>
                values != null && i < values.Count ? values[i] : fallback;
            var members = chosen.Select(i => donor.Members[i]).ToList();
            // Projections consume no real runtime ids. The domain owns Transport and movement.
            List<UnitData> roster = !projectTransportMovement ? null : chosen.Select(i =>
            {
                WorthIt.DefenderProfile profile = donor.Members[i];
                UnitData unit = UnitData.CreateProjection(donor.NonHeroRuntimeIds[i]);
                unit.Owner = donor.Owner; unit.HitPointsCurrent = (int)profile.HitPoints;
                unit.IsSummoned = profile.IsSummoned;
                unit.MoveMax = Of(donor.NonHeroMoveMax, i, 1);
                unit.MoveCurrent = Of(donor.NonHeroCurrentMovement, i, 1);
                unit.RestoreMovementSpent(Of(donor.NonHeroMovementSpent, i,
                    System.Math.Max(0, unit.MoveMax - unit.MoveCurrent)));
                if (profile.TypeTags != null) unit.TypeTags.UnionWith(profile.TypeTags);
                if (profile.Abilities != null) unit.Abilities.UnionWith(profile.Abilities);
                return unit;
            }).ToList();
            return new ArmySnapshot
            {
                ArmyId = armyId, Owner = donor.Owner, Hex = donor.Hex,
                MemberCount = chosen.Count, Members = members,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
                MaxMovement = roster != null ? ArmyData.ComputeMaxMovement(roster)
                    : chosen.Min(i => Of(donor.NonHeroMoveMax, i, 1)),
                CurrentMovement = roster != null ? ArmyData.ComputeCurrentMovement(roster)
                    : chosen.Min(i => Of(donor.NonHeroCurrentMovement, i, 1)),
                ActivationApCost = chosen.Sum(i => Of(donor.NonHeroActivationApCosts, i, 0)), Capacity = capacity,
            };
        }
        internal static int? WeakestCover(ArmySnapshot donor) =>
            Enumerable.Range(0, donor?.Members?.Count ?? 0)
                .Where(i => AiArmyRoles.IsGroundBattleBody(donor.Members[i]) && donor.Members[i].HitPoints > 0
                    && i < donor.NonHeroRuntimeIds.Count
                    && !(i < donor.NonHeroIsAviation.Count && donor.NonHeroIsAviation[i]))
                .OrderBy(i => AiPower.EffectiveArmyPowerFromProfiles(new[] { donor.Members[i] }))
                .ThenBy(i => donor.NonHeroRuntimeIds[i])
                .Select(i => (int?)donor.NonHeroRuntimeIds[i]).FirstOrDefault();
    }
}
