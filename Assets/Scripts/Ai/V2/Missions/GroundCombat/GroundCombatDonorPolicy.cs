using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ARCH-02 §29 — raid donor policy, split out of GroundCombatAssemblyPlanner. Owns "which same-hex unit
    // may a donor legally spare for the assembling host" (hero attach preference + non-hero body
    // combat value). The transaction boundary (a donor is never emptied) mirrors Provisioning's
    // canonical raid transaction. Bodies verbatim.
    internal static class GroundCombatDonorPolicy
    {
        // Strike force — the armies an Attack gather may BUY as supports: the primary of an
        // active Raid, priced at what abandoning that operation loses (MissionIntent
        // .DisplacementValue, TaskScore units — a Raid already walking home loses nothing).
        // ActiveDefence owns its responder until that threat is completed/invalidated and is
        // never an offensive gather donor. The gather carries the loss in its own TaskScore
        // (MoverOpportunityCost) and the allocator arbitrates; Continuity retires the Raid once
        // the gather holds its army. A running operation of unknown worth is not for sale.
        internal static Dictionary<int, float> BorrowableDonorValues(IEnumerable<MissionIntent> intents)
        {
            var values = new Dictionary<int, float>();
            foreach (MissionIntent i in intents ?? Enumerable.Empty<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || !i.PreferredMoverArmyId.HasValue
                    || i.Kind != MissionKind.Raid
                    || (!i.IsLifecycleLeg && i.LastIntrinsicValue <= 0f))
                    continue;
                values[i.PreferredMoverArmyId.Value] = i.DisplacementValue;
            }
            return values;
        }

        // §12 — the best same-hex hero that may legally join `host`, or (null, null): the one
        // commander evaluation (HeroRoleEvaluator.CompareCandidates) for THIS fight, then a stable
        // donor-id tiebreak. A donor must retain at least one member because Provisioning
        // enforces that same transaction boundary.
        internal static (ArmyData donor, UnitData hero) PickAttachableHero(PlayerSetupData owner,
            ArmyData host, ISet<int> excludeArmyIds,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus)
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
            // The hero this host should be led by against THIS fight: the one commander
            // evaluation (HeroRoleEvaluator) over the host's own bodies, then donor id.
            List<WorthIt.DefenderProfile> bodies = host.Members
                .Where(u => u != null && u.IsGroundCombatant)
                .Select(WorthIt.FromLiveUnit).ToList();
            var ranked = candidates
                .Select(x => (x.donor, x.hero, candidate: HeroRoleEvaluator.Candidate(x.hero,
                    HeroRoleEvaluator.ProjectCommand(x.hero.CommandRating, 0,
                        WorthIt.SideCommander.Of(x.hero), bodies, opposition, defenderHexDefenseBonus),
                    0)))
                .ToList();
            ranked.Sort((x, y) =>
            {
                int c = HeroRoleEvaluator.CompareCandidates(x.candidate, y.candidate);
                if (c != 0) return c;
                c = string.CompareOrdinal(x.hero.Name ?? string.Empty, y.hero.Name ?? string.Empty);
                return c != 0 ? c : x.donor.Id.CompareTo(y.donor.Id);
            });
            return (ranked[0].donor, ranked[0].hero);
        }

        internal static float UnitCombatValue(UnitData u) =>
            u == null ? 0f : WorthIt.CombatValue(u.Attack, u.Defense, u.HitPointsCurrent, u.Initiative);
    }
}
