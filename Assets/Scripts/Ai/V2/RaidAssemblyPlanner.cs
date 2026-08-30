using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID ASSEMBLY PLANNER  (Strategy V2 build-order step 9 — the pure raid-force solver)
    // ===========================================================================================
    //  A PURE actor/composition solver (spec §30 / §31 Stage 3): it never mutates game state, it
    //  only answers "from the frozen own-force picture, which existing army (or which same-hex
    //  consolidation) could execute this Raid, and does the projected roster clear the shared
    //  battle estimator". ProvisioningManager owns the live preflight + the canonical apply.
    //
    //  ORDER (spec §31): prefer a READY army that already clears the estimator; only then a
    //  same-hex consolidation onto a hero-led host. Cross-hex recall / garrison stripping /
    //  multi-turn force concentration are DEFERRED (spec §60 / §61) — this returns
    //  Reason != null for those, and Provisioning fails cleanly into the bounded re-pack.
    //
    //  The estimator is WorthIt (spec §32) run against the SAME DefenderProfile roster and the
    //  SAME threshold family (AiConfigV2.raidMinViableWinChance == V1 raidMinimumWinChance) that
    //  AggressionObjectiveEvaluator and CombatOpportunityAnalyzer use.
    // ===========================================================================================
    public sealed class RaidAssemblyPlan
    {
        public bool Feasible;
        public string Reason;               // null iff Feasible

        public int BaseArmyId;              // the army that will execute the raid
        public bool NeedsAssembly;          // false -> BaseArmyId is used as-is
        public readonly List<int> MergeArmyIds = new List<int>();   // co-located armies whose non-hero bodies fold into BaseArmyId

        public float ProjectedWinChance;
        public bool CoversAllDefenders;

        public static RaidAssemblyPlan Infeasible(string reason) =>
            new RaidAssemblyPlan { Feasible = false, Reason = reason };
    }

    public static class RaidAssemblyPlanner
    {
        private const int NoHeroStackCapacity = 2;   // mirror of ArmyData BaseCapacity, as elsewhere in V2

        public static RaidAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds)
        {
            if (snap?.Self?.Armies == null)
                return RaidAssemblyPlan.Infeasible("no own-force snapshot");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();

            List<ArmySnapshot> eligible = snap.Self.Armies
                .Where(a => a != null && !a.IsPrison && !a.IsAir && !a.IsGarrison && !a.IsSoloRecce
                            && a.MemberCount > 0 && a.CurrentMovement > 0
                            && (excludeArmyIds == null || !excludeArmyIds.Contains(a.ArmyId)))
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ToList();
            if (eligible.Count == 0)
                return RaidAssemblyPlan.Infeasible("no eligible ground combat army");

            // ---- Stage 2: a ready army that already clears the estimator --------------------
            foreach (ArmySnapshot a in eligible)
            {
                List<WorthIt.DefenderProfile> roster = (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
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

            // ---- Stage 3: same-hex consolidation onto a hero-led host ----------------------
            // Prefer a hero-led host (a raid is hero-led — parity with V1 NeedsHero); fall back to
            // the strongest host when the target is undefended / coverable without a hero.
            IEnumerable<ArmySnapshot> hosts = eligible
                .OrderByDescending(a => a.HasHero)
                .ThenByDescending(a => a.EffectiveArmyPower);
            foreach (ArmySnapshot host in hosts)
            {
                List<ArmySnapshot> sameHex = eligible
                    .Where(a => a.ArmyId != host.ArmyId && a.Hex.Equals(host.Hex))
                    .OrderByDescending(a => a.EffectiveArmyPower)
                    .ToList();
                if (sameHex.Count == 0)
                    continue;

                int cap = host.HasHero && host.HeroCommandRating > 0 ? host.HeroCommandRating : NoHeroStackCapacity;
                var roster = new List<WorthIt.DefenderProfile>(host.Members ?? System.Array.Empty<WorthIt.DefenderProfile>());
                var merged = new List<int>();
                foreach (ArmySnapshot donor in sameHex)
                {
                    if (roster.Count >= cap)
                        break;
                    // Only non-hero bodies fold in (donor keeps its own hero / structure decisions
                    // to ProvisioningManager's live preflight); the snapshot roster has no per-unit
                    // hero flag, so approximate by taking members while there is room.
                    foreach (WorthIt.DefenderProfile p in donor.Members ?? System.Array.Empty<WorthIt.DefenderProfile>())
                    {
                        if (roster.Count >= cap)
                            break;
                        roster.Add(p);
                    }
                    merged.Add(donor.ArmyId);
                }
                if (merged.Count == 0)
                    continue;
                if (!Clears(roster, defenders, out float win, out bool cover))
                    continue;

                var plan = new RaidAssemblyPlan
                {
                    Feasible = true,
                    BaseArmyId = host.ArmyId,
                    NeedsAssembly = true,
                    ProjectedWinChance = win,
                    CoversAllDefenders = cover,
                };
                plan.MergeArmyIds.AddRange(merged);
                return plan;
            }

            return RaidAssemblyPlan.Infeasible(
                "no ready army and no same-hex consolidation clears the raid estimator "
                + $"(cross-hex recall / multi-turn concentration deferred, spec §60/§61)");
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
