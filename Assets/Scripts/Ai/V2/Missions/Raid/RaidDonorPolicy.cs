using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ARCH-02 §29 — raid donor policy, split out of RaidAssemblyPlanner. Owns "which same-hex unit
    // may a donor legally spare for the assembling host" (hero attach preference + non-hero body
    // combat value). The transaction boundary (a donor is never emptied) mirrors Provisioning's
    // canonical raid transaction. Bodies verbatim.
    internal static class RaidDonorPolicy
    {
        // §12 — the best same-hex hero that may legally join `host`, or (null, null).
        // CombatLeader > Flexible > SupportOperator, then a stable donor-id tiebreak. A donor must
        // retain at least one member because Provisioning enforces that same transaction boundary.
        internal static (ArmyData donor, UnitData hero) PickAttachableHero(PlayerSetupData owner,
            ArmyData host, ISet<int> excludeArmyIds)
        {
            var candidates = new List<(ArmyData donor, UnitData hero)>();
            foreach (ArmyData donor in ArmyRegistry.AllForOwner(owner))
            {
                if (donor == null || donor.Id == host.Id || donor.Members.Count <= 1
                    || !donor.Hex.Equals(host.Hex)
                    || donor.IsPrison || donor.IsAirfield || donor.IsAirArmy || AiArmyRoles.IsSoloRecce(donor)
                    || (excludeArmyIds != null && excludeArmyIds.Contains(donor.Id)))
                    continue;
                foreach (UnitData h in donor.Members)
                {
                    if (h == null || !h.IsHero || h.IsAviation)
                        continue;
                    if (!donor.CanLeaveWithoutOvercrowding(h))
                        continue;
                    if (donor.IsGarrison && !AiArmyRoles.CanSpareGarrisonMember(owner, donor, h))
                        continue;
                    if (host.HasActivatedThisTurn && h.ActivationApCost > 0)
                        continue;
                    candidates.Add((donor, h));
                }
            }
            if (candidates.Count == 0)
                return (null, null);
            candidates.Sort((x, y) =>
            {
                int c = HeroRoleEvaluator.CompareForFieldCommand(x.hero, y.hero);
                return c != 0 ? c : x.donor.Id.CompareTo(y.donor.Id);
            });
            return candidates[0];
        }

        internal static float UnitCombatValue(UnitData u) =>
            u == null ? 0f : u.Attack + u.Defense + u.HitPointsCurrent + 0.25f * u.Initiative;
    }
}
