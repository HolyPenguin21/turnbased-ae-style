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
            float defenderHexDefenseBonus = 0f, bool allowCommandHandover = false)
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
                        allowCommandHandover, allowCompleteTransfer: allowCommandHandover))
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
            bool allowCommandHandover = false, bool allowCompleteTransfer = false)
        {
            if (primary == null || candidate == null)
                return false;
            if (allowCommandHandover)
            {
                ArmyData host = LiveArmy(primary);
                ArmyData donor = LiveArmy(candidate);
                var opponents = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
                if (host == null || donor == null)
                {
                    List<WorthIt.DefenderProfile> before = NonAviationProfiles(primary);
                    List<WorthIt.DefenderProfile> sparableProfiles = NonAviationProfiles(candidate)
                        .Take(System.Math.Max(0, candidate.MemberCount
                            - (allowCompleteTransfer && candidate.MemberCount == 1 ? 0 : 1)))
                        .ToList();
                    return TryProjectReinforcement(before, sparableProfiles, primary.Capacity,
                        primary.MemberCount, primary.Commander, opponents, out var projected,
                        out _, out _, out _, out _, defenderHexDefenseBonus,
                        requireWinGain: false)
                        && AiPower.EffectiveArmyPowerFromProfiles(projected)
                            > AiPower.EffectiveArmyPowerFromProfiles(before);
                }
                // ATK-F04 — the live armies get the one Attack handoff plan Provisioning and
                // Execution run, projected for the arrival turn (no charge from today's AP).
                return GroundCombatReinforcement.PlanAttackHandoff(host, donor, opponents,
                    defenderHexDefenseBonus, requireChargeNow: false, out _) != null;
            }
            List<WorthIt.DefenderProfile> bodies = NonAviationProfiles(candidate);
            // Mirror the live donor rule: Attack may consume one-body supports; other
            // donors and Raid retain a member (which may be a hero).
            int transferable = System.Math.Min(bodies.Count,
                System.Math.Max(0, candidate.MemberCount - (allowCompleteTransfer && candidate.MemberCount == 1 ? 0 : 1)));
            if (transferable <= 0)
                return false;

            List<WorthIt.DefenderProfile> sparable = bodies
                .OrderByDescending(ProfileCombatValue)
                .Take(transferable)
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
                why = "support army has no body it may legally spare (a container is never emptied)";
                return false;
            }

            int freeSlots = System.Math.Max(0, primaryCapacity - primaryMemberCount);
            if (freeSlots > 0)
            {
                int take = System.Math.Min(freeSlots, sparableSupportBodies.Count);
                for (int i = 0; i < take; i++)
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
                int weakest = Enumerable.Range(0, before.Count)
                    .OrderBy(i => before[i].MaxHitPoints > 0f ? before[i].HitPoints / before[i].MaxHitPoints : 1f)
                    .ThenBy(i => ProfileCombatValue(before[i]))
                    .First();
                float weakestValue = ProfileCombatValue(before[weakest]);
                int fresh = Enumerable.Range(0, sparableSupportBodies.Count)
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
