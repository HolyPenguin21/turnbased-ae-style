using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class GroundCombatAssemblyPlanner
    {
        // Reinforcement support-actor candidates. An EXISTING free ground-combat
        // army (already on the map, needing no card play / materialization) qualifies as a
        // Reinforcement support actor when merging its roster into the primary's would improve the
        // primary's projected WorthIt win chance against the current opposition — the same economics
        // ProvisioningManager.ReinforcementImprovesOdds re-checks live at execution time, evaluated
        // here at snapshot granularity so Missions can propose a concrete leg before an actor is
        // physically claimed. Demand reads this to decide whether a NEW army even needs to be
        // materialized; Missions/Provisioning read it to run the normal actor-contention batch solve.
        // `allowCommandHandover` — the lane hands command over at the rendezvous (Attack: the
        // provisioner validates with GroundCombatReinforcement.ImprovesOdds(allowCommandHandover:
        // true)), so a support whose hero would take command qualifies exactly as execution admits it.
        public static List<int> ReinforcementSupportCandidates(WorldSnapshot snap, int primaryArmyId,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> excludeArmyIds,
            float defenderHexDefenseBonus = 0f, bool allowCommandHandover = false,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            var ids = new List<int>();
            if (snap?.Self?.Armies == null)
                return ids;
            ArmySnapshot primary = snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == primaryArmyId);
            if (primary == null)
                return ids;

            var excluded = excludeArmyIds != null ? new HashSet<int>(excludeArmyIds) : new HashSet<int>();
            excluded.Add(primaryArmyId);
            foreach (ArmySnapshot candidate in GroundCombatActorEligibility.EligibleReadyArmies(snap, excluded))
                if (SupportImprovesPrimary(primary, candidate, opposition, defenderHexDefenseBonus,
                        allowCommandHandover, rosterAdmissible))
                    ids.Add(candidate.ArmyId);
            return ids;
        }

        // Snapshot-level: would handing `candidate`'s sparable bodies to `primary` raise the
        // primary's win chance — or, for a lane that hands command over, would its hero take
        // command of the primary (GroundCombatReinforcement.CommandHandover, the rule the
        // rendezvous leg's ImprovesOdds admits on)? The one per-candidate test behind
        // ReinforcementSupportCandidates, also used by Continuity to drop a planned Gather
        // support that no longer helps.
        internal static bool SupportImprovesPrimary(ArmySnapshot primary, ArmySnapshot candidate,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus = 0f,
            bool allowCommandHandover = false,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            if (primary == null || candidate == null)
                return false;
            ArmyData host = LiveArmy(primary);
            ArmyData donor = LiveArmy(candidate);
            if (host != null && donor != null)
                return allowCommandHandover
                    ? GroundCombatReinforcement.PlanAttackHandoff(host, donor, opposition,
                        defenderHexDefenseBonus, requireChargeNow: false, out _,
                        rosterAdmissible: rosterAdmissible) != null
                    : GroundCombatReinforcement.ImprovesOdds(host, donor, opposition,
                        defenderHexDefenseBonus, out _, rosterAdmissible: rosterAdmissible);
            // A route predicate needs complete live UnitData, including the commander and its
            // movement effects. A profile-only fallback cannot prove that contract.
            if (rosterAdmissible != null) return false;
            if (allowCommandHandover)
            {
                var opponents = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
                List<WorthIt.DefenderProfile> before = NonAviationProfiles(primary);
                List<WorthIt.DefenderProfile> sparableProfiles = NonAviationProfiles(candidate);
                return TryProjectReinforcement(before, sparableProfiles, primary.Capacity,
                    primary.MemberCount, primary.Commander, opponents, out var projected,
                    out _, out _, out _, out _, defenderHexDefenseBonus,
                    requireWinGain: false)
                    && (AiPower.EffectiveArmyPowerFromProfiles(projected)
                            > AiPower.EffectiveArmyPowerFromProfiles(before)
                        // GroundCombatReinforcement.RaisesPrimary's carrier clause.
                        || GroundCombatDonorPolicy.DisruptionHelps(before, opponents)
                            && !GroundCombatDonorPolicy.DisruptionHelps(projected, opponents));
            }

            List<WorthIt.DefenderProfile> bodies = NonAviationProfiles(candidate);
            // Mirror the live donor rule (GroundCombatReinforcement.SparableSupportBodies): every
            // body may go, the support may be emptied.
            if (bodies.Count == 0)
                return false;

            List<WorthIt.DefenderProfile> sparable = bodies
                .OrderByDescending(ProfileCombatValue)
                .ToList();
            return TryProjectReinforcement(NonAviationProfiles(primary), sparable, primary.Capacity,
                primary.MemberCount, primary.Commander, opposition ?? System.Array.Empty<WorthIt.DefendingArmy>(),
                out _, out _, defenderHexDefenseBonus);
        }

        // GroundCombat is the single owner of reinforcement admission. Both the snapshot candidate
        // pass and live Provisioning call this projection, so they evaluate the roster the atomic
        // handoff can ACTUALLY produce: fill free slots, otherwise swap one stronger body for the
        // weakest non-aviation body. The previous whole-convoy append could approve an impossible
        // improvement when the primary army was already full.
        internal static bool TryProjectReinforcement(
            IReadOnlyList<WorthIt.DefenderProfile> primaryBodies,
            IReadOnlyList<WorthIt.DefenderProfile> sparableSupportBodies,
            int primaryCapacity, int primaryMemberCount, WorthIt.SideCommander primaryCommander,
            IReadOnlyList<WorthIt.DefendingArmy> opposition,
            out List<WorthIt.DefenderProfile> projected, out string why,
            float defenderHexDefenseBonus = 0f) =>
            TryProjectReinforcement(primaryBodies, sparableSupportBodies, primaryCapacity,
                primaryMemberCount, primaryCommander, opposition, out projected, out why, out _,
                defenderHexDefenseBonus);

        internal static bool TryProjectReinforcement(
            IReadOnlyList<WorthIt.DefenderProfile> primaryBodies,
            IReadOnlyList<WorthIt.DefenderProfile> sparableSupportBodies,
            int primaryCapacity, int primaryMemberCount, WorthIt.SideCommander primaryCommander,
            IReadOnlyList<WorthIt.DefendingArmy> opposition,
            out List<WorthIt.DefenderProfile> projected, out string why,
            out float projectedWin, float defenderHexDefenseBonus) =>
            TryProjectReinforcement(primaryBodies, sparableSupportBodies, primaryCapacity,
                primaryMemberCount, primaryCommander, opposition, out projected, out why,
                out projectedWin, out _, out _, defenderHexDefenseBonus);

        internal static bool TryProjectReinforcement(
            IReadOnlyList<WorthIt.DefenderProfile> primaryBodies,
            IReadOnlyList<WorthIt.DefenderProfile> sparableSupportBodies,
            int primaryCapacity, int primaryMemberCount, WorthIt.SideCommander primaryCommander,
            IReadOnlyList<WorthIt.DefendingArmy> opposition,
            out List<WorthIt.DefenderProfile> projected, out string why,
            out float projectedWin, out List<int> incoming, out int displacedIndex,
            float defenderHexDefenseBonus, bool requireWinGain = true)
        {
            why = null;
            projectedWin = 0f;
            incoming = new List<int>();
            displacedIndex = -1;
            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            var before = (primaryBodies ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            projected = new List<WorthIt.DefenderProfile>(before);
            if (sparableSupportBodies == null || sparableSupportBodies.Count == 0)
            {
                why = "support army has no body it may legally spare";
                return false;
            }

            // The live handoff's carrier rule (GroundCombatReinforcement.PlanHandoff siteOpposition):
            // a first Disruption carrier against a known structure defence is offered first and
            // may replace the weakest body regardless of its own combat value.
            int carrier = GroundCombatDonorPolicy.DisruptionHelps(before, opposition)
                ? Enumerable.Range(0, sparableSupportBodies.Count).Where(i =>
                    Game.Combat.DisruptionRules.HasCarrier(new[] { sparableSupportBodies[i] }))
                    .DefaultIfEmpty(-1).First()
                : -1;
            int freeSlots = System.Math.Max(0, primaryCapacity - primaryMemberCount);
            if (freeSlots > 0)
            {
                int take = System.Math.Min(freeSlots, sparableSupportBodies.Count);
                foreach (int i in Enumerable.Range(0, sparableSupportBodies.Count)
                    .OrderBy(i => i == carrier ? 0 : 1).ThenBy(i => i).Take(take))
                {
                    projected.Add(sparableSupportBodies[i]);
                    incoming.Add(i);
                }
            }
            else
            {
                if (before.Count == 0)
                {
                    why = "primary is full and has no swappable non-hero body";
                    return false;
                }
                // Same "who gives way" rule as the live handoff (GroundCombatReinforcement.
                // WeakestBodies): an army support carrier is never swapped out. Its effects are
                // all ability-scoped, so the profile's abilities decide it exactly.
                int weakest = Enumerable.Range(0, before.Count)
                    .Where(i => !StrategicEffectRegistry.HasArmySupportEffect(before[i].Abilities, 0))
                    .OrderBy(i => before[i].MaxHitPoints > 0f ? before[i].HitPoints / before[i].MaxHitPoints : 1f)
                    .ThenBy(i => ProfileCombatValue(before[i]))
                    .DefaultIfEmpty(-1)
                    .First();
                if (weakest < 0)
                {
                    why = "primary is full and every body is an army support carrier";
                    return false;
                }
                float weakestValue = ProfileCombatValue(before[weakest]);
                int fresh = carrier >= 0 ? carrier : Enumerable.Range(0, sparableSupportBodies.Count)
                    .OrderByDescending(i => ProfileCombatValue(sparableSupportBodies[i]))
                    .Where(i => ProfileCombatValue(sparableSupportBodies[i]) > weakestValue)
                    .DefaultIfEmpty(-1)
                    .First();
                if (fresh < 0)
                {
                    why = "primary is full and no support body improves on its weakest member";
                    return false;
                }
                projected.RemoveAt(weakest);
                projected.Add(sparableSupportBodies[fresh]);
                incoming.Add(fresh);
                displacedIndex = weakest;
            }

            // Handoffs move ground bodies only: the primary's commander leads before and after.
            float winBefore = WorthIt.EstimateSequential(before, primaryCommander, opposition,
                defenderHexDefenseBonus).WinChance;
            float winAfter = WorthIt.EstimateSequential(projected, primaryCommander, opposition,
                defenderHexDefenseBonus).WinChance;
            projectedWin = winAfter;
            if (requireWinGain && winAfter <= winBefore + 0.001f)
            {
                why = $"projected executable win {winAfter:0.##} does not improve on {winBefore:0.##}";
                return false;
            }
            return true;
        }
    }
}
