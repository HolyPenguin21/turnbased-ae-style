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
        // AI-01 — the roster this plan would ACTUALLY produce: the live host's own members plus
        // every body the plan promises to transfer in. This is the one place that answers
        // "what will the assembled force look like", so cost/movement/AP pricing in Missions and
        // the pre-mutation re-check in Provisioning cannot drift apart into two projections.
        public static List<UnitData> ProjectedRoster(ArmyData host, GroundCombatAssemblyPlan plan)
        {
            var roster = new List<UnitData>();
            if (host != null)
                roster.AddRange(host.Members);
            if (plan != null && plan.NeedsAssembly)
                foreach (GroundCombatAssemblyTransfer t in plan.Transfers)
                    if (t?.Unit != null && !roster.Contains(t.Unit))
                        roster.Add(t.Unit);
            return roster;
        }

        // The FULL activation AP of the roster this plan would produce, priced by the canonical
        // game rule (ArmyData.ComputeActivationApCost). Deliberately NOT zeroed for an
        // already-activated host: whether this turn's charge is waived stays RaidCostModel's
        // current-turn-vs-recurring decision, which already owns that split. Null when the plan is
        // infeasible or its host no longer resolves live — callers then fall back to their
        // existing snapshot-based figure rather than inventing a second cost model.
        public static int? ProjectedActivationApCost(WorldSnapshot snap, GroundCombatAssemblyPlan plan)
        {
            List<UnitData> roster = ProjectedRosterOrNull(snap, plan);
            return roster == null ? (int?)null : ArmyData.ComputeActivationApCost(roster);
        }

        // The travel speed of the roster this plan would produce. A recruit slower than
        // the host lowers the WHOLE formation's shared movement (ArmyData.ComputeMaxMovement), so
        // an ETA derived from the untouched host is optimistic exactly when the plan needs
        // assembly. Same owner, same projection, same null-means-fall-back-to-your-existing-figure
        // contract as ProjectedActivationApCost above — never a second cost/ETA model.
        public static int? ProjectedMaxMovement(WorldSnapshot snap, GroundCombatAssemblyPlan plan)
        {
            List<UnitData> roster = ProjectedRosterOrNull(snap, plan);
            return roster == null ? (int?)null : ArmyData.ComputeMaxMovement(roster);
        }

        // The live roster a feasible plan would produce, or null when the plan is infeasible or
        // its host no longer resolves live. One resolution path for every projection above.
        private static List<UnitData> ProjectedRosterOrNull(WorldSnapshot snap,
            GroundCombatAssemblyPlan plan)
        {
            if (snap?.Self?.Armies == null || plan == null || !plan.Feasible)
                return null;
            ArmySnapshot hostSnap = snap.Self.Armies.FirstOrDefault(
                a => a != null && a.ArmyId == plan.BaseArmyId);
            PlayerSetupData owner = hostSnap?.Owner;
            if (owner == null)
                return null;
            ArmyData host = ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == plan.BaseArmyId);
            if (host == null || host.Members.Count == 0)
                return null;
            return ProjectedRoster(host, plan);
        }

        private static ArmyData LiveArmy(ArmySnapshot s)
        {
            PlayerSetupData owner = s?.Owner;
            return owner == null ? null : ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == s.ArmyId);
        }

        private static List<WorthIt.DefenderProfile> NonAviationProfiles(ArmySnapshot army)
        {
            var result = new List<WorthIt.DefenderProfile>();
            IReadOnlyList<WorthIt.DefenderProfile> members =
                army?.Members ?? System.Array.Empty<WorthIt.DefenderProfile>();
            IReadOnlyList<bool> aviation =
                army?.NonHeroIsAviation ?? System.Array.Empty<bool>();
            for (int i = 0; i < members.Count; i++)
                if (i >= aviation.Count || !aviation[i])
                    result.Add(members[i]);
            return result;
        }

        private static float ProfileCombatValue(WorthIt.DefenderProfile p) => WorthIt.CombatValue(p);
    }
}
