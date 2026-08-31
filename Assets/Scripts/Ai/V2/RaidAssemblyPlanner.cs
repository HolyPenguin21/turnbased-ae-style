using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID ASSEMBLY PLANNER  (Strategy V2 build-order step 9 — the pure raid-force solver)
    // ===========================================================================================
    //  Side-effect-free ready-force solver. Same-hex consolidation stays fail-closed until a real
    //  atomic batch-transfer primitive exists. `PlanForArmy` is the exact single-actor form used
    //  by ProvisioningManager's batch Raid matching; both paths share the same eligibility and
    //  WorthIt estimator, so batch assignment cannot drift from final provisioning feasibility.
    //
    //  IMPORTANT: actor preference is mobility-first, not power-first. The cheapest activation
    //  envelope wins, then the smallest already-sufficient combat body. This prevents a raid that
    //  already clears the estimator from automatically claiming the biggest stack and turning
    //  harmless reinforcement into a larger AP activation bill on following turns.
    // ===========================================================================================
    public sealed class RaidAssemblyPlan
    {
        public bool Feasible;
        public string Reason;
        public int BaseArmyId;
        public bool NeedsAssembly;
        public readonly List<int> MergeArmyIds = new List<int>();
        public float ProjectedWinChance;
        public bool CoversAllDefenders;

        public static RaidAssemblyPlan Infeasible(string reason) =>
            new RaidAssemblyPlan { Feasible = false, Reason = reason };
    }

    public static class RaidAssemblyPlanner
    {
        public static RaidAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<ArmySnapshot> eligible = EligibleReadyArmies(snap, excludeArmyIds);
            if (eligible.Count == 0)
                return RaidAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            foreach (ArmySnapshot a in eligible)
            {
                RaidAssemblyPlan exact = PlanForArmy(snap, target, defenders, a.ArmyId);
                if (exact.Feasible)
                    return exact;
            }

            return RaidAssemblyPlan.Infeasible(
                "no already-formed free army clears the raid estimator; same-hex consolidation is "
                + "temporarily quarantined because the existing sequential TransferMember apply is not atomic");
        }

        // Exact feasibility for one actor. This is intentionally public within V2's model layer:
        // ProvisioningManager first solves the funded Raid set as an injective batch, then calls the
        // same primitive again at the atomic door to revalidate the assigned actor.
        public static RaidAssemblyPlan PlanForArmy(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !IsReadyRaidActor(a))
                return RaidAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!Clears(roster, defenders, out float win, out bool cover))
                return RaidAssemblyPlan.Infeasible($"raid actor #{armyId} does not clear the shared raid estimator");

            return new RaidAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = a.ArmyId,
                NeedsAssembly = false,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
            };
        }

        private static List<ArmySnapshot> EligibleReadyArmies(WorldSnapshot snap, ISet<int> excludeArmyIds) =>
            snap.Self.Armies
                .Where(a => a != null && IsReadyRaidActor(a)
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId)))
                // Already-activated actors are effectively free on the AP axis. Otherwise prefer
                // the smallest activation envelope, then the least overkill body that still passes
                // PlanForArmy's shared combat estimator.
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();

        // ONE structural Raid actor predicate shared by Strategy diagnostics, Demand capability
        // inventory and final Provisioning. Garrison is deliberately reserve/potential power only:
        // it can feed housekeeping reorganisation, but it is never a mobile army by itself.
        internal static bool IsStructuralRaidActor(ArmySnapshot a)
        {
            if (a == null || a.IsPrison || a.IsAir || a.IsGarrison || a.IsSoloRecce || a.MemberCount <= 0)
                return false;

            PlayerSetupData owner = a.Owner;
            if (owner == null)
                return false;
            ArmyData live = ArmyRegistry.AllForOwner(owner).FirstOrDefault(x => x != null && x.Id == a.ArmyId);
            return live != null && !live.IsPrison && !live.IsGarrison && !live.IsAirfield && !live.IsAirArmy
                && !AiArmyRoles.IsSoloRecce(live) && !AiArmyRoles.IsSoloHeroAwaitingEscort(live)
                && live.Members.Count > 0;
        }

        internal static bool IsReadyRaidActor(ArmySnapshot a) =>
            IsStructuralRaidActor(a) && a.CurrentMovement > 0;

        private static bool Clears(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out float win, out bool cover)
        {
            cover = ProfilesCoverAll(attackers, defenders);
            win = defenders.Count == 0
                ? 1f
                : WorthIt.WinChance((IReadOnlyCollection<WorthIt.DefenderProfile>)attackers,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            return cover && win >= AiConfigV2.raidMinViableWinChance;
        }

        private static bool ProfilesCoverAll(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders)
        {
            if (defenders == null || defenders.Count == 0) return true;
            if (attackers == null || attackers.Count == 0) return false;
            foreach (WorthIt.DefenderProfile def in defenders)
            {
                bool covered = false;
                foreach (WorthIt.DefenderProfile atk in attackers)
                    if (WorthIt.CanDamage(atk.Attack, def, 0f)) { covered = true; break; }
                if (!covered) return false;
            }
            return true;
        }
    }
}
