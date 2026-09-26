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
    //  GroundCombatAdmissionPolicy. It computes no strategic objective value.
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
        // The win-chance gate this plan was admitted at (set where the plan is built against a
        // gate): any later re-check of the same plan asks the same question, never a stricter one.
        public float WinChanceGate;

        public static GroundCombatAssemblyPlan Infeasible(string reason) =>
            new GroundCombatAssemblyPlan { Feasible = false, Reason = reason };
    }

    // Audit F7 — a cross-hex gather: the host holds, the supports walk to it and hand over.
    public sealed class GroundCombatGatherPlan
    {
        public bool Feasible;
        public string Reason;
        public int HostArmyId;
        public HexCoord HostHex;
        // Planned supports, critical path first (longest walk to the host).
        public readonly List<int> SupportArmyIds = new List<int>();
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        // Turns until the slowest support stands on the host's hex.
        public int GatherTurns;
        // Turns the assembled roster needs from the host's hex to the target.
        public int AssaultEta;
        // Σ (support activation × walking turns + its handoff charge) + assembled activation ×
        // assault turns.
        public int TotalAp;
        public int TotalEta => GatherTurns + AssaultEta;

        public static GroundCombatGatherPlan Infeasible(string reason) =>
            new GroundCombatGatherPlan { Feasible = false, Reason = reason };
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

        // Attack (strike force step 4): ONE floor for a fresh and a continuing operation alike.
        // Below it the AI sits and defends; above it the win chance is a term of the Attack score
        // (AttackObjectiveEvaluator.WithResponse), not a gate. It is below the continuation floor,
        // so no Attack re-check is ever stricter than its admission.
        internal static float AttackWinChanceFloor => AiConfigV2.attackMinViableWinChance;

        // The gate an assigned assault actor is (re)planned at: Attack's floor; otherwise the
        // continuation floor for the pinned Hard incumbent and the fresh gate for anything new.
        internal static float AssaultGate(MissionProposal proposal, int actorId) =>
            proposal != null && proposal.Kind == MissionKind.Attack ? AttackWinChanceFloor
            : PinnedOrFreshGate(proposal != null && proposal.FromDurableIntent
                && proposal.DurableFundingTier == CommitmentTier.Hard
                && proposal.PreferredMoverArmyId == actorId);

        // Raid / ActiveDefence: the continuation floor for the durable operation's own pinned
        // actor, the fresh gate for anything new. Every stage selects through this one predicate.
        internal static float PinnedOrFreshGate(bool continuesPinnedOperation) =>
            continuesPinnedOperation ? ContinuationWinChanceFloor : FreshStartWinChanceGate;

        // Raid: a started operation stays in Assault down to the continuation floor; every other
        // phase (and an unstarted one) returns to Assault only at the fresh gate. Continuity's
        // phase machine and the Demand layer's shortage test both read this, so a shortage is
        // reported exactly when Continuity will not let the primary assault on its own.
        internal static float RaidPrimaryGate(RaidIntent raid) =>
            PinnedOrFreshGate(raid != null && raid.OperationStarted
                && raid.Phase == RaidMissionPhase.Assault);
    }

    // The generalized ground-combat assembly REQUEST. The kernel below is shared by Raid,
    // Attack and ActiveDefence; nothing Raid-specific
    // (target merit, phase transitions, cooldowns) lives inside it. Every field is an explicit
    // constraint the caller owns.
    public sealed class GroundCombatAssemblyRequest
    {
        // The opposition the assembled force must beat: every defending army with its own
        // commander (AiV2Util.KnownOpposition / AttackObjectiveEvaluator.KnownSiteOpposition).
        // Empty == "no fight to win" (a pure escort / rendezvous request), which trivially clears.
        public IReadOnlyList<WorthIt.DefendingArmy> Opposition =
            System.Array.Empty<WorthIt.DefendingArmy>();
        // Fresh-start vs continuation win gate. Callers pass GroundCombatAdmissionPolicy's two constants.
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
        // ATK §29/§30 — the defence bonus the DEFENDERS enjoy where the fight will happen
        // (structural + terrain), as the honest knowledge read AiMapMemory.KnownHexDefenseBonus
        // owns. Mission-agnostic: a field battle passes 0f, an assault on a known Base/Citadel
        // passes what memory actually observed. Never read live from BuildingRegistry here.
        public float DefenderHexDefenseBonus;
    }

    public static class GroundCombatAssemblyPlanner
    {
        // Legacy/raid-shaped facade. `target` is identity only; the kernel needs the opposition.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, RaidMissionTarget target,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ISet<int> excludeArmyIds,
            float defenderHexDefenseBonus = 0f) =>
            Plan(snap, new GroundCombatAssemblyRequest
            {
                Opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>(),
                WinChanceGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                ExcludedArmyIds = excludeArmyIds,
                DefenderHexDefenseBonus = defenderHexDefenseBonus,
            });

        // The generalized kernel. Prefer an already-sufficient free army; otherwise (when allowed)
        // build a minimal same-hex package around a legal host.
        public static GroundCombatAssemblyPlan Plan(WorldSnapshot snap, GroundCombatAssemblyRequest request)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");
            if (request == null)
                return GroundCombatAssemblyPlan.Infeasible("no ground-combat assembly request");

            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                request.Opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            List<ArmySnapshot> eligible = OrderedEligible(snap, request);
            if (eligible.Count == 0)
                return GroundCombatAssemblyPlan.Infeasible("no free, mobile ground combat army exists this cycle");

            // Already-formed force always wins over reorganisation.
            foreach (ArmySnapshot a in eligible)
            {
                GroundCombatAssemblyPlan exact = PlanForArmyAtThreshold(
                    snap, opposition, a.ArmyId, request.WinChanceGate,
                    request.DefenderHexDefenseBonus);
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
                    snap, opposition, a, request.ExcludedArmyIds, request.WinChanceGate,
                    request.DefenderHexDefenseBonus);
                if (assembled.Feasible)
                    return assembled;
            }

            return GroundCombatAssemblyPlan.Infeasible(
                "no already-formed or transactionally assemblable same-hex force clears the shared raid estimator");
        }

        // The ONE enumeration of "which ready ground armies could independently take this fight"
        // under the request's gate — the set Plan() would return one by one if it were re-run while
        // excluding each hit. Single pass with identical precedence: every already-formed actor
        // first (Plan's exact loop), then same-hex assembly hosts in the same power order, where
        // every earlier hit is excluded as a donor exactly as the repeated Plan() excluded it.
        // Each actor is Monte-Carlo-tested at most once per stage instead of once per earlier hit.
        internal static List<int> EligibleActorIds(WorldSnapshot snap, GroundCombatAssemblyRequest request)
        {
            var ids = new List<int>();
            if (snap?.Self?.Armies == null || request == null)
                return ids;
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                request.Opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            List<ArmySnapshot> eligible = OrderedEligible(snap, request);
            foreach (ArmySnapshot a in eligible)
                if (PlanForArmyAtThreshold(snap, opposition, a.ArmyId, request.WinChanceGate,
                        request.DefenderHexDefenseBonus).Feasible)
                    ids.Add(a.ArmyId);
            if (!request.AllowSameHexAssembly)
                return ids;

            var excluded = request.ExcludedArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(request.ExcludedArmyIds);
            excluded.UnionWith(ids);
            foreach (ArmySnapshot a in eligible)
            {
                if (excluded.Contains(a.ArmyId))
                    continue;
                if (TryAssembleForHost(snap, opposition, a, excluded, request.WinChanceGate,
                        request.DefenderHexDefenseBonus).Feasible)
                {
                    ids.Add(a.ArmyId);
                    excluded.Add(a.ArmyId);
                }
            }
            return ids;
        }

        // Free ready ground armies admissible for the request, strongest first. Both loops of
        // Plan() return on the FIRST army that clears the estimator, so a decisive matchup exits
        // after one Monte-Carlo call. OrderBy is stable, so this ordering survives untouched
        // through the PreferredPrimaryArmyId reorder below.
        private static List<ArmySnapshot> OrderedEligible(WorldSnapshot snap,
            GroundCombatAssemblyRequest request)
        {
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
            return eligible;
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
                        allowCommandHandover))
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
            bool allowCommandHandover = false)
        {
            if (primary == null || candidate == null)
                return false;
            if (allowCommandHandover && GroundCombatReinforcement.CommandHandover(LiveArmy(primary),
                    LiveArmy(candidate), opposition ?? System.Array.Empty<WorthIt.DefendingArmy>(),
                    defenderHexDefenseBonus, null) != null)
                return true;
            List<WorthIt.DefenderProfile> bodies = NonAviationProfiles(candidate);
            // Mirrors SparableSupportBodies' minimum-container invariant at snapshot level:
            // leave at least one total member (a hero may be that retained member). Previously
            // a single-body army was advertised as support even though execution could transfer
            // nothing, producing a permanent select -> reject loop.
            int transferable = System.Math.Min(bodies.Count,
                System.Math.Max(0, candidate.MemberCount - 1));
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

        // Audit F7 — CROSS-HEX GATHER. Plan() knows an already-sufficient army or a SAME-HEX
        // package only. When the strength exists but is spread over free field armies on different
        // hexes, this answers which army HOSTS the formation and which others walk to it and hand
        // their bodies over, so the assembled force clears `winChanceGate` at the lowest total AP:
        //
        //   cost(host) = Σ support.ActivationAp × max(1, turns(support -> host))
        //              + Σ handoff charge (GroundCombatReinforcement.ProjectedHandoffApCost)
        //              + ActivationAp(assembled roster) × turns(host -> target)
        //
        // Every walking turn re-activates the walker, so the cheapest host is naturally one already
        // on the way to the target and close to its supports. Supports are added greedily by win
        // gain per AP through the SAME fill/swap projection the handoff executes
        // (TryProjectReinforcement over GroundCombatReinforcement.SparableSupportBodies). There is
        // no count or distance cap: the win gate is the bar to be feasible, and past it supports
        // keep joining while each still adds attackGatherMinWinGain (strike force step 5), so a
        // spread-out late-game army attacks at its assembled peak. Candidates are the snapshot's free ready field armies
        // (GroundCombatActorEligibility) minus `excludeArmyIds`; rosters are read live, exactly as
        // TryAssembleForHost does. `pinnedHostArmyId` re-plans a started gather around its host.
        // `donorApPrices` — armies of other operations the gather may buy as SUPPORTS (never as
        // host), each with its abandonment price in AP (GroundCombatDonorPolicy).
        internal static GroundCombatGatherPlan PlanGather(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            HexCoord targetHex, ISet<int> excludeArmyIds, float winChanceGate,
            int? pinnedHostArmyId = null, IReadOnlyDictionary<int, float> donorApPrices = null)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatGatherPlan.Infeasible("no own-force snapshot");
            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();

            List<ArmySnapshot> free = GroundCombatActorEligibility.EligibleReadyArmies(snap, excludeArmyIds);
            List<ArmySnapshot> bought = donorApPrices == null || donorApPrices.Count == 0
                ? new List<ArmySnapshot>()
                : GroundCombatActorEligibility.EligibleReadyArmies(snap, null)
                    .Where(a => donorApPrices.ContainsKey(a.ArmyId)
                        && !free.Any(f => f.ArmyId == a.ArmyId))
                    .ToList();
            List<ArmySnapshot> hosts = pinnedHostArmyId.HasValue
                ? snap.Self.Armies.Where(a => a != null && a.ArmyId == pinnedHostArmyId.Value
                    && a.IsStructuralRaidActor).ToList()
                : free;

            GroundCombatGatherPlan best = null;
            string why = "no free field army can host a gather";
            foreach (ArmySnapshot hostSnap in hosts)
            {
                GroundCombatGatherPlan p = PlanGatherForHost(snap, opposition, defenderHexDefenseBonus,
                    targetHex, hostSnap, free.Concat(bought).Where(s => s.ArmyId != hostSnap.ArmyId).ToList(),
                    winChanceGate, donorApPrices);
                if (!p.Feasible)
                {
                    why = p.Reason;
                    continue;
                }
                if (best == null || p.TotalAp < best.TotalAp
                    || (p.TotalAp == best.TotalAp && (p.TotalEta < best.TotalEta
                        || (p.TotalEta == best.TotalEta
                            && p.ProjectedWinChance > best.ProjectedWinChance + 0.001f))))
                    best = p;
            }
            return best ?? GroundCombatGatherPlan.Infeasible(why);
        }

        private static GroundCombatGatherPlan PlanGatherForHost(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            HexCoord targetHex, ArmySnapshot hostSnap, List<ArmySnapshot> supportSnaps,
            float winChanceGate, IReadOnlyDictionary<int, float> donorApPrices)
        {
            ArmyData host = LiveArmy(hostSnap);
            if (host == null || host.Members.Count == 0)
                return GroundCombatGatherPlan.Infeasible($"gather host #{hostSnap?.ArmyId} is no longer live");

            // The host's side of the handoff, exactly as GroundCombatReinforcement.ImprovesOdds
            // reads the primary.
            // `bodyUnits` stays index-parallel to `bodies`, so every projected fill / swap names
            // the live units that move and the handoff is priced on them.
            var roster = new List<UnitData>(host.Members);
            List<UnitData> bodyUnits = host.Members
                .Where(u => AiArmyRoles.IsGroundBattleBody(u))
                .ToList();
            List<WorthIt.DefenderProfile> bodies = bodyUnits.Select(WorthIt.FromLiveUnit).ToList();

            var pool = new List<GatherSupport>();
            foreach (ArmySnapshot s in supportSnaps)
            {
                ArmyData live = LiveArmy(s);
                List<UnitData> sparable = GroundCombatReinforcement.SparableSupportBodies(live);
                if (live == null || sparable.Count == 0)
                    continue;
                int turns = AiV2Util.CeilDiv(HexGridMath.Distance(s.Hex, host.Hex),
                    System.Math.Max(1, s.MaxMovement));
                pool.Add(new GatherSupport
                {
                    ArmyId = s.ArmyId,
                    Live = live,
                    Units = sparable,
                    Bodies = sparable.Select(WorthIt.FromLiveUnit).ToList(),
                    Turns = turns,
                    // A bought donor also costs the operation it abandons.
                    Ap = s.ActivationApCost * System.Math.Max(1, turns)
                        + (donorApPrices != null && donorApPrices.TryGetValue(s.ArmyId, out float price)
                            ? UnityEngine.Mathf.CeilToInt(price) : 0),
                });
            }
            if (pool.Count == 0)
                return GroundCombatGatherPlan.Infeasible(
                    $"gather host #{host.Id}: no other free field army has a body to spare");

            // The PEAK formation's commander (its bodies plus the pool — HeroRoleEvaluator): the
            // host's own best hero (the assault transaction promotes it before the march), or a
            // support's hero handed over because it leads this fight better
            // (GroundCombatReinforcement.CommandHandover — the handoff applies the same rule; the
            // cheapest such support is kept in the plan for its hero). Its Command sets capacity.
            List<WorthIt.DefenderProfile> pooledBodies = pool.SelectMany(x => x.Bodies).ToList();
            UnitData lead = HeroRoleEvaluator.BestCommanderFor(host.Members, host.IsGarrison,
                opposition, defenderHexDefenseBonus, pooledBodies) ?? host.Commander;
            GatherSupport heroDonor = null;
            foreach (GatherSupport s in pool.OrderBy(x => x.Ap).ThenBy(x => x.ArmyId))
            {
                CommandHandoverPlan handover = GroundCombatReinforcement.CommandHandover(host, s.Live,
                    opposition, defenderHexDefenseBonus, pooledBodies);
                if (handover == null)
                    continue;
                lead = handover.Hero;
                heroDonor = s;
                // The whole exchange the handoff will make (PlanHandoff takes this same plan): the
                // hero and every body it brings join, every host body it exchanges leaves — the
                // formation and the handoff's AP price read the same lists.
                foreach (UnitData gone in handover.Displaced)
                {
                    int at = bodyUnits.IndexOf(gone);
                    roster.Remove(gone);
                    s.Displaced.Add(gone);
                    if (at >= 0)
                    {
                        bodies.RemoveAt(at);
                        bodyUnits.RemoveAt(at);
                    }
                }
                foreach (UnitData joined in handover.Incoming)
                {
                    roster.Add(joined);
                    s.Incoming.Add(joined);
                    if (AiArmyRoles.IsGroundBattleBody(joined))
                    {
                        bodyUnits.Add(joined);
                        bodies.Add(WorthIt.FromLiveUnit(joined));
                    }
                }
                break;
            }
            List<UnitData> ledRoster = roster.Where(u => u != lead).ToList();
            if (lead != null)
                ledRoster.Insert(0, lead);
            int capacity = ArmyData.ComputeCapacity(ledRoster, host.IsGarrison);
            int memberCount = roster.Count;
            WorthIt.SideCommander commander = WorthIt.SideCommander.Of(lead);

            // One Monte-Carlo bound before the greedy loop: the host's slots filled with the
            // strongest bodies the whole pool holds. If even that misses the gate, skip this host.
            List<WorthIt.DefenderProfile> bound = bodies
                .Concat(pool.Where(x => x != heroDonor).SelectMany(x => x.Bodies))
                .OrderByDescending(ProfileCombatValue)
                .Take(bodies.Count + System.Math.Max(0, capacity - memberCount))
                .ToList();
            if (!GroundCombatFeasibility.Clears(bound, commander, opposition, winChanceGate,
                    defenderHexDefenseBonus, out _, out _))
                return GroundCombatGatherPlan.Infeasible(
                    $"gather host #{host.Id}: even the strongest pooled roster misses the "
                    + $"{winChanceGate:0.00} gate");

            bool clears = GroundCombatFeasibility.Clears(bodies, commander, opposition, winChanceGate,
                defenderHexDefenseBonus, out float win, out bool cover);
            var chosen = new List<GatherSupport>();
            // Strike force step 5 — gather to the PEAK, not to the bare gate: below the gate any
            // improving support is taken (best win gain per AP first); past it a support is worth
            // its walk only while it moves the fight by more than Monte-Carlo noise
            // (attackGatherMinWinGain).
            while (true)
            {
                GatherSupport pick = null;
                List<WorthIt.DefenderProfile> pickRoster = null;
                List<int> pickIncoming = null;
                int pickDisplaced = -1;
                float pickWin = 0f, pickRate = 0f;
                foreach (GatherSupport s in pool)
                {
                    if (chosen.Contains(s) || s == heroDonor
                        || !TryProjectReinforcement(bodies, s.Bodies, capacity, memberCount,
                            commander, opposition, out List<WorthIt.DefenderProfile> projected, out _,
                            out float projectedWin, out List<int> incomingIdx, out int displacedIdx,
                            defenderHexDefenseBonus))
                        continue;
                    if (clears && projectedWin - win < AiConfigV2.attackGatherMinWinGain)
                        continue;
                    float rate = (projectedWin - win) / System.Math.Max(1, s.Ap);
                    if (pick == null || rate > pickRate)
                    {
                        pick = s;
                        pickRoster = projected;
                        pickIncoming = incomingIdx;
                        pickDisplaced = displacedIdx;
                        pickWin = projectedWin;
                        pickRate = rate;
                    }
                }
                if (pick == null)
                {
                    if (clears)
                        break;
                    return GroundCombatGatherPlan.Infeasible(
                        $"gather host #{host.Id}: no remaining support improves the formation "
                        + $"(win {win:0.00} < {winChanceGate:0.00})");
                }

                // The live units the projection moved: a fill appends them, a swap exchanges one
                // for the host's weakest body, which leaves with the support.
                if (pickDisplaced >= 0)
                {
                    UnitData gone = bodyUnits[pickDisplaced];
                    bodyUnits.RemoveAt(pickDisplaced);
                    roster.Remove(gone);
                    pick.Displaced.Add(gone);
                }
                foreach (int i in pickIncoming)
                {
                    bodyUnits.Add(pick.Units[i]);
                    roster.Add(pick.Units[i]);
                    pick.Incoming.Add(pick.Units[i]);
                }
                memberCount += pickIncoming.Count - (pickDisplaced >= 0 ? 1 : 0);
                bodies = pickRoster;
                win = pickWin;
                chosen.Add(pick);
                clears = GroundCombatFeasibility.Clears(bodies, commander, opposition, winChanceGate,
                    defenderHexDefenseBonus, out win, out cover);
            }

            if (heroDonor != null && !chosen.Contains(heroDonor))
                chosen.Add(heroDonor);

            int assembledMove = ArmyData.ComputeMaxMovement(roster);
            int assaultEta = AiV2Util.CeilDiv(HexGridMath.Distance(host.Hex, targetHex),
                System.Math.Max(AiConfigV2.etaFallbackMoveBudget, assembledMove));
            var plan = new GroundCombatGatherPlan
            {
                Feasible = true,
                HostArmyId = host.Id,
                HostHex = host.Hex,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
                GatherTurns = chosen.Count == 0 ? 0 : chosen.Max(s => s.Turns),
                AssaultEta = assaultEta,
                // Walks + each support's handoff (the same charge its rendezvous leg provisions)
                // + the assembled roster's march to the target.
                TotalAp = chosen.Sum(s => s.Ap + GroundCombatReinforcement.ProjectedHandoffApCost(
                        s.Incoming, s.Displaced, host, s.Live, supportWalks: s.Turns > 0))
                    + ArmyData.ComputeActivationApCost(roster) * System.Math.Max(1, assaultEta),
            };
            foreach (GatherSupport s in chosen.OrderByDescending(s => s.Turns).ThenBy(s => s.ArmyId))
                plan.SupportArmyIds.Add(s.ArmyId);
            return plan;
        }

        private sealed class GatherSupport
        {
            public int ArmyId;
            public ArmyData Live;
            // What its handoff moves: support -> host, and host -> support.
            public readonly List<UnitData> Incoming = new List<UnitData>();
            public readonly List<UnitData> Displaced = new List<UnitData>();
            public List<UnitData> Units;
            public List<WorthIt.DefenderProfile> Bodies;
            public int Turns;
            public int Ap;
        }

        private static ArmyData LiveArmy(ArmySnapshot s)
        {
            PlayerSetupData owner = s?.Owner;
            return owner == null ? null : ArmyRegistry.AllForOwner(owner)
                .FirstOrDefault(a => a != null && a.Id == s.ArmyId);
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

        // Same projection, also reporting the projected win chance it already computed (PlanGather
        // ranks supports by it without a second Monte-Carlo run).
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

        // Same projection, also naming WHO moves: `incoming` — indices into
        // `sparableSupportBodies` that join the primary; `displacedIndex` — the index into
        // `primaryBodies` the swap sends back to the support (-1 for a fill). PlanGather prices
        // the handoff (GroundCombatReinforcement.ProjectedHandoffApCost) on exactly these units.
        internal static bool TryProjectReinforcement(
            IReadOnlyList<WorthIt.DefenderProfile> primaryBodies,
            IReadOnlyList<WorthIt.DefenderProfile> sparableSupportBodies,
            int primaryCapacity, int primaryMemberCount, WorthIt.SideCommander primaryCommander,
            IReadOnlyList<WorthIt.DefendingArmy> opposition,
            out List<WorthIt.DefenderProfile> projected, out string why,
            out float projectedWin, out List<int> incoming, out int displacedIndex,
            float defenderHexDefenseBonus)
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

        private static float ProfileCombatValue(WorthIt.DefenderProfile p) => WorthIt.CombatValue(p);

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
            IReadOnlyList<WorthIt.DefendingArmy> opposition, int armyId,
            float defenderHexDefenseBonus = 0f) =>
            PlanForArmyAtThreshold(snap, opposition, armyId,
                GroundCombatAdmissionPolicy.ContinuationWinChanceFloor, defenderHexDefenseBonus);

        // The exact gate the Aggression demand layer re-runs against the NEXT
        // objective before it may call an active Raid "covered". Threshold is explicit: a fresh
        // target is a fresh start decision even for an incumbent army.
        public static GroundCombatAssemblyPlan PlanForArmyAt(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, int armyId, float minWinChance,
            float defenderHexDefenseBonus = 0f) =>
            PlanForArmyAtThreshold(snap, opposition, armyId, minWinChance, defenderHexDefenseBonus);

        internal static GroundCombatAssemblyPlan PlanForArmyAtThreshold(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, int armyId, float minWinChance,
            float defenderHexDefenseBonus = 0f)
        {
            if (snap?.Self?.Armies == null)
                return GroundCombatAssemblyPlan.Infeasible("no own-force snapshot");

            ArmySnapshot a = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            if (a == null || !a.IsStructuralRaidActor)
                return GroundCombatAssemblyPlan.Infeasible($"raid actor #{armyId} is not a free mobile ground combat army");

            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            List<WorthIt.DefenderProfile> roster =
                (a.Members ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToList();
            if (!GroundCombatFeasibility.Clears(roster, a.Commander, opposition, minWinChance,
                    defenderHexDefenseBonus, out float win, out bool cover))
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
                WinChanceGate = minWinChance,
            };
        }

        private static GroundCombatAssemblyPlan TryAssembleForHost(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, ArmySnapshot hostSnap, ISet<int> excludeArmyIds,
            float minWinChance, float defenderHexDefenseBonus)
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
            // (typically the garrison), the best commander for THIS fight (HeroRoleEvaluator).
            // A lone-hero container is intentionally left to Housekeeping first: Provisioning's
            // canonical raid transaction never empties donor containers, so the planner must not
            // promise a transfer the executor will reject.
            if (!projectedUnits.Any(u => u != null && u.IsHero))
            {
                (ArmyData heroDonor, UnitData hero) = GroundCombatDonorPolicy.PickAttachableHero(owner, host,
                    excludeArmyIds, opposition, defenderHexDefenseBonus);
                if (hero != null)
                {
                    var withHero = new List<UnitData>(projectedUnits) { hero };
                    if (ArmyData.ComputeCapacity(withHero, host.IsGarrison) >= withHero.Count)
                    {
                        projectedUnits.Add(hero);
                        projectedProfiles.Add(WorthIt.FromLiveUnit(hero));
                        selected.Add(new GroundCombatAssemblyTransfer { DonorArmyId = heroDonor.Id, Unit = hero });
                        if (GroundCombatFeasibility.Clears(projectedProfiles, WorthIt.SideCommander.Of(projectedUnits), opposition, minWinChance,
                                defenderHexDefenseBonus, out float hWin, out bool hCover))
                            return FinishAssembly(host, selected, hWin, hCover, minWinChance);
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
                    .Where(u => AiArmyRoles.IsGroundBattleBody(u)
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
                    if (GroundCombatFeasibility.Clears(projectedProfiles, WorthIt.SideCommander.Of(projectedUnits), opposition, minWinChance,
                            defenderHexDefenseBonus, out float win, out bool cover))
                        return FinishAssembly(host, selected, win, cover, minWinChance);
                }
            }

            // The hero alone (no bodies available/needed) may already clear.
            if (selected.Count > 0 && GroundCombatFeasibility.Clears(projectedProfiles, WorthIt.SideCommander.Of(projectedUnits), opposition,
                    minWinChance, defenderHexDefenseBonus, out float wOnly, out bool cOnly))
                return FinishAssembly(host, selected, wOnly, cOnly, minWinChance);

            return GroundCombatAssemblyPlan.Infeasible($"raid actor #{host.Id} cannot reach the win bar from safe same-hex donors");
        }

        private static GroundCombatAssemblyPlan FinishAssembly(ArmyData host,
            IReadOnlyList<GroundCombatAssemblyTransfer> selected, float win, bool cover, float gate)
        {
            var plan = new GroundCombatAssemblyPlan
            {
                Feasible = true,
                BaseArmyId = host.Id,
                NeedsAssembly = true,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
                WinChanceGate = gate,
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
