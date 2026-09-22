using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RAID ASSEMBLY PLANNER  (Strategy V2 build-order step 9 — the raid-force solver)
    // ===========================================================================================
    //  Target discovery/value stays snapshot-driven, but our own force is authoritative live state.
    //  The solver first prefers an already-sufficient army. If none exists it may build a minimal
    //  same-hex package by taking AT MOST ONE safe non-hero body from each donor. A donor is never
    //  emptied and every selected body is retained in the plan so Provisioning can transactionally
    //  revalidate/apply exactly the roster that passed WorthIt here.
    //
    //  ARCH-02 §29/§31 — this class is the constrained physical ASSEMBLY SOLVER only. Actor
    //  eligibility is GroundCombatActorEligibility; the WorthIt win/coverage check is GroundCombatFeasibility;
    //  same-hex donor legality is GroundCombatDonorPolicy; the fresh-vs-continuation win gates are
    //  RaidAdmissionPolicy. It computes no strategic objective value.
    // ===========================================================================================
    public sealed class GroundCombatAssemblyTransfer
    {
        public int DonorArmyId;
        public UnitData Unit;
    }

    public sealed class GroundCombatAssemblyPlan
    {
        public bool Feasible;
        public string Reason;
        public int BaseArmyId;
        public bool NeedsAssembly;
        public readonly List<int> MergeArmyIds = new List<int>();
        public readonly List<GroundCombatAssemblyTransfer> Transfers = new List<GroundCombatAssemblyTransfer>();
        public float ProjectedWinChance;
        public bool CoversAllDefenders;

        public static GroundCombatAssemblyPlan Infeasible(string reason) =>
            new GroundCombatAssemblyPlan { Feasible = false, Reason = reason };
    }

    // ARCH-02 §29 — the fresh-start vs continuation win-chance gates. Starting a raid and
    // continuing an already-started operation are deliberately different decisions.
    internal static class GroundCombatAdmissionPolicy
    {
        // Fresh admission still requires the strict raidMinViableWinChance (0.65 today).
        internal static float FreshStartWinChanceGate => AiConfigV2.raidMinViableWinChance;

        // Once a Hard raid has actually left its staging hex, small Monte-Carlo variance / loss of
        // same-hex donor availability must not instantly turn the incumbent actor into a structural
        // AssemblyInfeasible failure. The assigned incumbent may continue while it still covers
        // every known defender and keeps at least this lower safety floor. 0.40 is intentionally
        // conservative: it fixes the observed 0.78-start -> ~0.41-next-turn discontinuity without
        // authorising a clearly hopeless attack. Fresh raids never see this floor.
        internal const float ContinuationWinChanceFloor = 0.40f;
    }

    // Compatibility name for existing Raid call sites; the policy itself is target-agnostic.
    internal static class RaidAdmissionPolicy
    {
        internal static float FreshStartWinChanceGate => GroundCombatAdmissionPolicy.FreshStartWinChanceGate;
        internal const float ContinuationWinChanceFloor = GroundCombatAdmissionPolicy.ContinuationWinChanceFloor;
    }

    // AGG-RAID §1 — the generalized ground-combat assembly REQUEST. The kernel below is shared by
    // Raid (Assault / Reinforcement) and by any future Active Defence lane; nothing Raid-specific
    // (target merit, phase transitions, cooldowns) lives inside it. Every field is an explicit
    // constraint the caller owns.
    public sealed class GroundCombatAssemblyRequest
    {
        // Defenders the assembled force must be able to beat. Empty == "no fight to win" (a pure
        // escort / rendezvous request), which trivially clears the estimator.
        public IReadOnlyList<WorthIt.DefenderProfile> Defenders =
            System.Array.Empty<WorthIt.DefenderProfile>();
        // Fresh-start vs continuation win gate. Callers pass RaidAdmissionPolicy's two constants.
        public float WinChanceGate = AiConfigV2.raidMinViableWinChance;
        // When set, this actor is tried first and (with PinToPreferred) exclusively.
        public int? PreferredPrimaryArmyId;
        public bool PinToPreferred;
        // Armies excluded because they are busy / already claimed this cycle.
        public ISet<int> ExcludedArmyIds;
        // Staging restriction: only armies standing on this hex may host the formation.
        public HexCoord? StagingHex;
        // May the kernel build a package out of same-hex donors, or must it find an
        // already-sufficient army?
        public bool AllowSameHexAssembly = true;
        // The result must be an army OTHER than PreferredPrimaryArmyId (a separate support actor).
        public bool RequiresSeparateSupportActor;
    }

    public static class GroundCombatAssemblyPlanner
    {
        // Legacy/raid-shaped facade. `target` is identity only; the kernel needs the defenders.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds) =>
            Plan(snap, new GroundCombatAssemblyRequest
            {
                Defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>(),
                WinChanceGate = RaidAdmissionPolicy.FreshStartWinChanceGate,
                ExcludedArmyIds = excludeArmyIds,
            });

        // The generalized kernel. Prefer an already-sufficient free army; otherwise (when allowed)
        // build a minimal same-hex package around a legal host.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, GroundCombatAssemblyRequest request)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");
            if (request == null)
                return GroundCombatAssemblyPlan.Infeasible("no ground-combat assembly request");

            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                request.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            // Perf — Record()'s caller re-invokes Plan() once per already-found army (excluding it
            // each time) to enumerate the whole eligible set, and both loops below return on the
            // FIRST army that clears the estimator. Trying the strongest army first means a
            // decisive matchup exits after one Monte-Carlo call instead of working through weaker
            // armies that were never going to beat it there first anyway. OrderBy is stable, so
            // this ordering survives untouched through the PreferredPrimaryArmyId reorder below.
            List<ArmySnapshot> eligible = GroundCombatActorEligibility
                .EligibleReadyArmies(snap, request.ExcludedArmyIds)
                .Where(a => Admissible(a, request))
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ToList();
            if (request.PinToPreferred && request.PreferredPrimaryArmyId.HasValue)
                eligible = eligible.Where(a => a.ArmyId == request.PreferredPrimaryArmyId.Value).ToList();
            else if (request.PreferredPrimaryArmyId.HasValue)
                eligible = eligible
                    .OrderByDescending(a => a.ArmyId == request.PreferredPrimaryArmyId.Value)
                    .ToList();
            if (eligible.Count == 0)
                return GroundCombatAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // Already-formed force always wins over reorganisation.
            foreach (ArmySnapshot a in eligible)
            {
                GroundCombatAssemblyPlan exact = PlanForArmyAtThreshold(
                    snap, defenders, a.ArmyId, request.WinChanceGate);
                if (exact.Feasible)
                    return exact;
            }

            if (!request.AllowSameHexAssembly)
                return GroundCombatAssemblyPlan.Infeasible(
                    "no already-formed free ground force clears the shared combat estimator "
                    + "(same-hex assembly not permitted for this request)");

            // Minimal same-hex reinforcement. The host itself must be a real mobile combat body;
            // donors may be reserve armies or garrisons, but dedicated Recce / aviation / prisons
            // and mission-claimed containers are excluded.
            foreach (ArmySnapshot a in eligible)
            {
                GroundCombatAssemblyPlan assembled = TryAssembleForHost(
                    snap, defenders, a, request.ExcludedArmyIds, request.WinChanceGate);
                if (assembled.Feasible)
                    return assembled;
            }

            return GroundCombatAssemblyPlan.Infeasible(
                "no already-formed or transactionally assemblable same-hex force clears the shared raid estimator");
        }

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

        // FIX-02 — the travel speed of the roster this plan would produce. A recruit slower than
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

        // AGG-RAID P0#1 — reinforcement support-actor candidates. An EXISTING free ground-combat
        // army (already on the map, needing no card play / materialization) qualifies as a
        // Reinforcement support actor when merging its roster into the primary's would improve the
        // primary's projected WorthIt win chance against the current defenders — the same economics
        // ProvisioningManager.ReinforcementImprovesOdds re-checks live at execution time, evaluated
        // here at snapshot granularity so Missions can propose a concrete leg before an actor is
        // physically claimed. Demand reads this to decide whether a NEW army even needs to be
        // materialized; Missions/Provisioning read it to run the normal actor-contention batch solve.
        public static List<int> ReinforcementSupportCandidates(WorldSnapshot snap, int primaryArmyId,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> excludeArmyIds)
        {
            var ids = new List<int>();
            if (snap?.Self?.Armies == null)
                return ids;
            ArmySnapshot primary = snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == primaryArmyId);
            if (primary == null)
                return ids;

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<WorthIt.DefenderProfile> primaryBodies = NonAviationProfiles(primary);

            var excluded = excludeArmyIds != null ? new HashSet<int>(excludeArmyIds) : new HashSet<int>();
            excluded.Add(primaryArmyId);
            foreach (ArmySnapshot candidate in GroundCombatActorEligibility.EligibleReadyArmies(snap, excluded))
            {
                List<WorthIt.DefenderProfile> bodies = NonAviationProfiles(candidate);
                // Mirrors SparableSupportBodies' minimum-container invariant at snapshot level:
                // leave at least one total member (a hero may be that retained member). Previously
                // a single-body army was advertised as support even though execution could transfer
                // nothing, producing a permanent select -> reject loop.
                int transferable = System.Math.Min(bodies.Count,
                    System.Math.Max(0, candidate.MemberCount - 1));
                if (transferable <= 0)
                    continue;

                List<WorthIt.DefenderProfile> sparable = bodies
                    .OrderByDescending(ProfileCombatValue)
                    .Take(transferable)
                    .ToList();
                if (TryProjectReinforcement(primaryBodies, sparable, primary.Capacity,
                        primary.MemberCount, defenders, out _, out _))
                    ids.Add(candidate.ArmyId);
            }
            return ids;
        }

        // GroundCombat is the single owner of reinforcement admission. Both the snapshot candidate
        // pass and live Provisioning call this projection, so they evaluate the roster the atomic
        // handoff can ACTUALLY produce: fill free slots, otherwise swap one stronger body for the
        // weakest non-aviation body. The previous whole-convoy append could approve an impossible
        // improvement when the primary army was already full.
        internal static bool TryProjectReinforcement(
            IReadOnlyList<WorthIt.DefenderProfile> primaryBodies,
            IReadOnlyList<WorthIt.DefenderProfile> sparableSupportBodies,
            int primaryCapacity, int primaryMemberCount,
            IReadOnlyList<WorthIt.DefenderProfile> defenders,
            out List<WorthIt.DefenderProfile> projected, out string why)
        {
            why = null;
            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
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
                projected.AddRange(sparableSupportBodies.Take(freeSlots));
            }
            else
            {
                if (before.Count == 0)
                {
                    why = "primary is full and has no swappable non-hero body";
                    return false;
                }
                WorthIt.DefenderProfile weakest = before
                    .OrderBy(p => p.MaxHitPoints > 0f ? p.HitPoints / p.MaxHitPoints : 1f)
                    .ThenBy(ProfileCombatValue)
                    .First();
                WorthIt.DefenderProfile fresh = sparableSupportBodies
                    .OrderByDescending(ProfileCombatValue)
                    .FirstOrDefault(p => ProfileCombatValue(p) > ProfileCombatValue(weakest));
                if (ProfileCombatValue(fresh) <= ProfileCombatValue(weakest))
                {
                    why = "primary is full and no support body improves on its weakest member";
                    return false;
                }
                projected.Remove(weakest);
                projected.Add(fresh);
            }

            float winBefore = defenders.Count == 0 ? 1f
                : WorthIt.WinChance(before,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            float winAfter = defenders.Count == 0 ? 1f
                : WorthIt.WinChance(projected,
                    (IReadOnlyCollection<WorthIt.DefenderProfile>)defenders, 0f);
            if (winAfter <= winBefore + 0.001f)
            {
                why = $"projected executable win {winAfter:0.##} does not improve on {winBefore:0.##}";
                return false;
            }
            return true;
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

        private static float ProfileCombatValue(WorthIt.DefenderProfile p) =>
            p.Attack + p.Defense + p.HitPoints + 0.25f * p.Initiative;

        private static bool Admissible(ArmySnapshot a, GroundCombatAssemblyRequest r)
        {
            if (a == null) return false;
            if (r.StagingHex.HasValue && !a.Hex.Equals(r.StagingHex.Value)) return false;
            if (r.RequiresSeparateSupportActor && r.PreferredPrimaryArmyId.HasValue
                && a.ArmyId == r.PreferredPrimaryArmyId.Value)
                return false;
            return true;
        }

        // Exact feasibility for one ALREADY ASSIGNED actor. Fresh actor admission is performed by
        // Plan() above at the strict raidMinViableWinChance. Provisioning calls this method only
        // after its batch assignment has picked a concrete actor; GroundCombatAdmissionRegistry additionally
        // uses it for the PreferredMover of a durable Hard Raid. That incumbent gets bounded
        // continuation hysteresis so a valid multi-turn operation is not destroyed by the stricter
        // start gate on every subsequent turn.
        public static GroundCombatAssemblyPlan PlanForArmy(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId) =>
            PlanForArmyAtThreshold(snap, defenders, armyId, RaidAdmissionPolicy.ContinuationWinChanceFloor);

        // AGG-RAID §6 — the exact gate the Aggression demand layer re-runs against the NEXT
        // objective before it may call an active Raid "covered". Threshold is explicit: a fresh
        // target is a fresh start decision even for an incumbent army.
        public static GroundCombatAssemblyPlan PlanForArmyAt(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId, float minWinChance) =>
            PlanForArmyAtThreshold(snap, defenders, armyId, minWinChance);

        internal static GroundCombatAssemblyPlan PlanForArmyAtThreshold(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, int armyId, float minWinChance)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !a.IsStructuralRaidActor)
                return GroundCombatAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            defenders = defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!GroundCombatFeasibility.Clears(roster, defenders, minWinChance, out float win, out bool cover))
                return GroundCombatAssemblyPlan.Infeasible(
                    $"raid actor #{armyId} does not clear the assigned-actor raid estimator "
                    + $"(win {win:0.00} < {minWinChance:0.00} or coverage missing)");

            return new GroundCombatAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = a.ArmyId,
                NeedsAssembly = false,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
            };
        }

        private static GroundCombatAssemblyPlan TryAssembleForHost(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ArmySnapshot hostSnap, ISet<int> excludeArmyIds,
            float minWinChance)
        {
            PlayerSetupData owner = hostSnap?.Owner;
            if (owner == null)
                return GroundCombatAssemblyPlan.Infeasible("assembly host has no owner");
            ArmyData host = ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == hostSnap.ArmyId);
            if (host == null || host.Members.Count == 0 || host.CurrentMovement <= 0)
                return GroundCombatAssemblyPlan.Infeasible("assembly host is no longer live/mobile");

            var projectedUnits = new List<UnitData>(host.Members);
            var projectedProfiles = projectedUnits.Select(WorthIt.FromLiveUnit).ToList();
            var selected = new List<GroundCombatAssemblyTransfer>();

            // §12 — a heroless host may take ONE eligible same-hex hero from a safe donor
            // (typically the garrison). Preference: CombatLeader > Flexible > SupportOperator.
            // A lone-hero container is intentionally left to Housekeeping first: Provisioning's
            // canonical raid transaction never empties donor containers, so the planner must not
            // promise a transfer the executor will reject.
            if (!projectedUnits.Any(u => u != null && u.IsHero))
            {
                (ArmyData heroDonor, UnitData hero) = GroundCombatDonorPolicy.PickAttachableHero(owner, host, excludeArmyIds);
                if (hero != null)
                {
                    var withHero = new List<UnitData>(projectedUnits) { hero };
                    if (ArmyData.ComputeCapacity(withHero, host.IsGarrison) >= withHero.Count)
                    {
                        projectedUnits.Add(hero);
                        projectedProfiles.Add(WorthIt.FromLiveUnit(hero));
                        selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = heroDonor.Id, Unit = hero });
                        if (GroundCombatFeasibility.Clears(projectedProfiles, defenders, minWinChance, out float hWin, out bool hCover))
                            return FinishAssembly(host, selected, hWin, hCover);
                    }
                }
            }

            IEnumerable<ArmySnapshot> donorSnaps = snap.Self.Armies
                .Where(d => d != null && d.ArmyId != host.Id && d.Owner == owner
                    && d.Hex.Equals(host.Hex) && !d.IsPrison && !d.IsAir && !d.IsSoloRecce
                    && d.MemberCount > 1
                    && (excludeArmyIds == null || !excludeArmyIds.Contains(d.ArmyId)))
                .OrderByDescending(d => d.EffectiveArmyPower)
                .ThenBy(d => d.ArmyId);

            foreach (ArmySnapshot donorSnap in donorSnaps)
            {
                ArmyData donor = ArmyRegistry.AllForOwner(owner)
                    .FirstOrDefault(a => a != null && a.Id == donorSnap.ArmyId);
                if (donor == null || donor.Members.Count <= 1 || !donor.Hex.Equals(host.Hex)
                    || donor.IsPrison || donor.IsAirfield || donor.IsAirArmy || AiArmyRoles.IsSoloRecce(donor))
                    continue;

                List<UnitData> picks = donor.Members
                    .Where(u => u != null && !u.IsHero && !u.IsAviation
                        && donor.Members.Count > 1
                        && donor.CanLeaveWithoutOvercrowding(u)
                        && (!host.HasActivatedThisTurn || u.ActivationApCost <= 0))
                    .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                    .ThenBy(u => u.Name)
                    .ToList();
                var selectedFromDonor = new List<UnitData>();
                foreach (UnitData pick in picks)
                {
                    if (donor.Members.Count - selectedFromDonor.Count <= 1)
                        break;
                    selectedFromDonor.Add(pick);
                    if (donor.IsGarrison
                        && !AiArmyRoles.CanSpareGarrisonMembers(owner, donor, selectedFromDonor))
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        continue;
                    }
                    var withPick = new List<UnitData>(projectedUnits) { pick };
                    if (ArmyData.ComputeCapacity(withPick, host.IsGarrison) < withPick.Count)
                    {
                        selectedFromDonor.RemoveAt(selectedFromDonor.Count - 1);
                        break;
                    }

                    projectedUnits.Add(pick);
                    projectedProfiles.Add(WorthIt.FromLiveUnit(pick));
                    selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = pick });
                    if (GroundCombatFeasibility.Clears(projectedProfiles, defenders, minWinChance,
                            out float win, out bool cover))
                        return FinishAssembly(host, selected, win, cover);
                }
            }

            // The hero alone (no bodies available/needed) may already clear.
            if (selected.Count > 0 && GroundCombatFeasibility.Clears(projectedProfiles, defenders, minWinChance, out float wOnly, out bool cOnly))
                return FinishAssembly(host, selected, wOnly, cOnly);

            return GroundCombatAssemblyPlan.Infeasible($"raid actor #{host.Id} cannot reach the win bar from safe same-hex donors");
        }

        private static GroundCombatAssemblyPlan FinishAssembly(ArmyData host,
            IReadOnlyList<GroundCombatAssemblyTransfer> selected, float win, bool cover)
        {
            var plan = new GroundCombatAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = host.Id,
                NeedsAssembly = true,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
            };
            foreach (GroundCombatAssemblyTransfer t in selected)
            {
                plan.Transfers.Add(t);
                if (!plan.MergeArmyIds.Contains(t.DonorArmyId))
                    plan.MergeArmyIds.Add(t.DonorArmyId);
            }
            return plan;
        }
    }
}
