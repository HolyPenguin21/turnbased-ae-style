using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID ASSEMBLY PLANNER  (Strategy V2 build-order step 9 — the pure raid-force solver)
    // ===========================================================================================
    //  A side-effect-free actor/composition solver: it never mutates gameplay state. For the
    //  manual-test corrective pass it deliberately returns ONLY an already-ready army. The original
    //  same-hex consolidation path was proved unsafe: ProvisioningManager applied several canonical
    //  TransferMember calls sequentially, so transfer #1 could mutate the world and spend AP while
    //  transfer #2 failed. That violates V2's hard provisioning contract: SUCCESS applies the
    //  complete plan; FAIL changes nothing.
    //
    //  Same-hex assembly is therefore FAIL-CLOSED until it is backed by one canonical atomic batch
    //  transfer primitive with whole-batch capacity/AP validation. This is intentionally a safety
    //  quarantine, not a strategic redesign: StrategicManager may still prepare combat capability,
    //  and the moment a real field army already clears the shared estimator it is immediately
    //  eligible for Raid. Cross-hex recall / multi-turn concentration remain deferred as before.
    //
    //  Snapshot.IsAir historically marks a MOBILE air army only. An airfield is a different
    //  ArmyData container, so a snapshot-only `!IsAir` filter can accidentally admit parked
    //  aircraft as a ground Raid actor. IsLiveGroundFieldArmy closes that representation seam by
    //  resolving only the matching OWN live ArmyData and checking structural container flags. It
    //  does not inspect opponents/fog state and it does not mutate anything.
    //
    //  The estimator is WorthIt run against the SAME DefenderProfile roster and threshold family
    //  AggressionObjectiveEvaluator / CombatOpportunityAnalyzer use (ONE ESTIMATOR, MANY STAGES).
    // ===========================================================================================
    public sealed class RaidAssemblyPlan
    {
        public bool Feasible;
        public string Reason;               // null iff Feasible

        public int BaseArmyId;              // the army that will execute the raid
        public bool NeedsAssembly;          // corrective pass: always false on a feasible plan
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

            List<ArmySnapshot> eligible = snap.Self.Armies
                .Where(a => a != null && !a.IsPrison && !a.IsAir && !a.IsGarrison && !a.IsSoloRecce
                            && a.MemberCount > 0 && a.CurrentMovement > 0
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId))
                            && IsLiveGroundFieldArmy(a))
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ThenBy(a => a.ArmyId)
                .ToList();
            if (eligible.Count == 0)
                return RaidAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // A ready army is safe: provisioning only binds it; no composition mutation occurs.
            foreach (ArmySnapshot a in eligible)
            {
                List<WorthIt.DefenderProfile> roster =
                    (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
                if (!Clears(roster, defenders, out float win, out bool cover))
                    continue;

                return new RaidAssemblyPlan
                {
                    Feasible = true,
                    BaseArmyId = a.ArmyId,
                    NeedsAssembly = false,
                    ProjectedWinChance = win,
                    CoversAllDefenders = cover,
                };
            }

            return RaidAssemblyPlan.Infeasible(
                "no already-formed free army clears the raid estimator; same-hex consolidation is "
                + "temporarily quarantined because the existing sequential TransferMember apply is not atomic");
        }

        private static bool IsLiveGroundFieldArmy(ArmySnapshot a)
        {
            PlayerSetupData owner = a?.Owner;
            if (owner == null)
                return false;
            ArmyData live = ArmyRegistry.AllForOwner(owner).FirstOrDefault(x => x != null && x.Id == a.ArmyId);
            return live != null && !live.IsPrison && !live.IsGarrison && !live.IsAirfield && !live.IsAirArmy
                && !AiArmyRoles.IsSoloRecce(live) && live.Members.Count > 0;
        }

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

        // Same rule CombatOpportunityAnalyzer.ProfilesCoverAll uses (every defender needs one
        // attacker that can dent it).
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
