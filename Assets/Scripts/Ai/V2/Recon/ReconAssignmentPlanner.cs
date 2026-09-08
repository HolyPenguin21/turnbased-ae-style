using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON ASSIGNMENT / PROVISIONING  (AI V2 architecture — Level 5)
    // ===========================================================================================
    //  Single responsibility: given a funded Recon mission, which concrete actor executes it — the
    //  ONE canonical owner of actor<->Recon job matching. This class answers exactly three kinds of
    //  question, and nothing about strategic priority, axis funding, or a strategic objective:
    //
    //    A. EvaluateCandidate — "can THIS actor execute THIS job at all" (route/vantage/stealth
    //       feasibility). The canonical actor/job feasibility definition.
    //    B/D. BuildCandidates / AssignFunded — given a batch of FUNDED missions, the best one-to-one
    //       actor assignment across all of them at once (job <= 1 actor, actor <= 1 job).
    //    C. MeasureCapacity — a READ-ONLY aggregate query Demand uses to ask "how much of this is
    //       there ANY usable actor for" without Demand itself knowing how matching works.
    //
    //  This is deliberately NOT a pre-funding reservation subsystem: it owns no cross-call mutable
    //  room/ledger state, mirrors no allocator deferrals, and never runs before AxisBudgetLedger /
    //  ResourceAllocator have funded a mission. Assignment begins for real only after generic
    //  funding (ResourceAllocator.Pack) has picked a mission — see ProvisioningManager.
    // ===========================================================================================

    public enum ReconAssignmentBlockReason
    {
        None,
        ActorMissing,
        NoRoute,
        NoReachableVantage,
    }

    // Round 3 (Problem 2) — the ONE structured "why did this funded mission get no actor"
    // vocabulary. AssignFunded computes this for every mission left unassigned after the batch
    // solve, using the SAME BuildCandidates output the solve itself used (never a second re-derived
    // eligibility/route/vantage pass) — ProvisioningManager.ClassifyNoAssignment is now a pure
    // translation of whichever of these it receives into a ProvisionFailure.
    public enum ScoutAssignmentFailureReason
    {
        NoMoverExists,          // no structural candidate at all (stealth-filtered where relevant)
        NoExecutableStep,       // an eligible mover exists but none has a safe first step / vantage
        NoObservationVantage,   // Surveil only — no on-map vantage within any scout's vision
        MoverContended,         // a capable candidate existed but lost this pass's batch solve
    }

    // Result of AssignFunded — the successful actor<->mission bindings, plus a rejection reason for
    // every mission that got none. Both maps are keyed by the same StableMissionKey space as
    // `open`; a mission present in neither was not part of this call.
    public sealed class ReconAssignmentResult
    {
        public readonly Dictionary<StableMissionKey, ScoutExecutionCandidate> Assigned =
            new Dictionary<StableMissionKey, ScoutExecutionCandidate>();
        public readonly Dictionary<StableMissionKey, ScoutAssignmentFailureReason> Rejected =
            new Dictionary<StableMissionKey, ScoutAssignmentFailureReason>();
    }

    public readonly struct ReconAssignmentCandidateResult
    {
        public readonly bool Feasible;
        public readonly ReconAssignmentBlockReason BlockReason;

        public ReconAssignmentCandidateResult(bool feasible, ReconAssignmentBlockReason reason)
        {
            Feasible = feasible;
            BlockReason = reason;
        }

        public static readonly ReconAssignmentCandidateResult Ok =
            new ReconAssignmentCandidateResult(true, ReconAssignmentBlockReason.None);

        public static ReconAssignmentCandidateResult Blocked(ReconAssignmentBlockReason reason) =>
            new ReconAssignmentCandidateResult(false, reason);
    }

    // Read-only aggregate result of MeasureCapacity — witnessed (proven-executable) actor counts,
    // split by requirement class. NOT the same as a raw actor COUNT (e.g. capacity.Generic*
    // LaneActors.Count / IdleGroundScouts.Count) — those are unverified; this is the result of an
    // actual joint actor<->job matching (one actor <= one job, one job <= one actor).
    public readonly struct ReconCapacityMeasurement
    {
        public readonly int GroundLaneWitnessed;
        public readonly int ObsLaneWitnessed;
        public readonly int GroundIdleWitnessed;
        public readonly int ObsIdleWitnessed;
        // Round 3 (Problem 3) — the stealth-lane equivalent of GroundIdleWitnessed/ObsIdleWitnessed:
        // a JOINT actor<->job witness (one stealth-capable actor <= one stealth job) over the
        // runnable stealth objectives, produced by the SAME SolveReconFlow pass as the generic
        // numbers above. Replaces DemandLayer's old ReconAssignmentPlanner.CountEligibleMovers raw
        // count (a second, unwitnessed capacity API) — see the facade section below.
        public readonly int StealthGroundWitnessed;
        public readonly int StealthObsWitnessed;

        public ReconCapacityMeasurement(int groundLane, int obsLane, int groundIdle, int obsIdle,
            int stealthGroundWitnessed = 0, int stealthObsWitnessed = 0)
        {
            GroundLaneWitnessed = groundLane;
            ObsLaneWitnessed = obsLane;
            GroundIdleWitnessed = groundIdle;
            ObsIdleWitnessed = obsIdle;
            StealthGroundWitnessed = stealthGroundWitnessed;
            StealthObsWitnessed = stealthObsWitnessed;
        }
    }

    internal static class ReconAssignmentPlanner
    {
        // =======================================================================================
        //  A. EvaluateCandidate — the ONLY canonical actor/job feasibility definition. Answers
        //     "can this one actor execute this one job", ignoring everyone else (actor uniqueness /
        //     concrete-job uniqueness / class-quota bookkeeping is the CALLER's job — BuildCandidates/
        //     AssignFunded for real assignment, MeasureCapacity's flow network for the Demand
        //     witness). Must NOT evaluate axis funding — that is Generic Funding's job, not
        //     Assignment's, and combining the two is exactly the FundedActionableNow-style composite
        //     concept this architecture forbids.
        // =======================================================================================
        public static ReconAssignmentCandidateResult EvaluateCandidate(AiTurnContext ctx,
            PlayerSetupData player, WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (mover == null)
                return ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.ActorMissing);
            if (ctx?.Map == null)
                return ReconAssignmentCandidateResult.Ok;

            ArmyData live = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == mover.ArmyId);
            if (live == null)
                return ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.ActorMissing);

            if (target.Kind != ScoutTargetKind.Surveil)
                return SafeStepPathing.FindNextSafeStep(ctx.Map, live, target.FocusHex) != null
                    ? ReconAssignmentCandidateResult.Ok
                    : ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.NoRoute);

            foreach (SurveilVantageCandidate v in SurveilVantageSelector.Rank(snap, mover, target))
                if (SafeStepPathing.FindNextSafeStep(ctx.Map, live, v.ExecutionHex) != null)
                    return ReconAssignmentCandidateResult.Ok;
            return ReconAssignmentCandidateResult.Blocked(ReconAssignmentBlockReason.NoReachableVantage);
        }

        // Thin bool convenience over EvaluateCandidate — kept because most callers only need the
        // yes/no answer (Demand's witness matching, the assignment solver's pairwise feasibility
        // filter). A trivial local alias inside Assignment, not a second facade.
        internal static bool CanExecute(AiTurnContext ctx, PlayerSetupData player, WorldSnapshot snap,
            ArmySnapshot mover, ScoutMissionTarget target) =>
            EvaluateCandidate(ctx, player, snap, mover, target).Feasible;

        // =======================================================================================
        //  ELIGIBILITY FACADE — round 3 (Problem 3): the raw eligible-actor enumeration primitives
        //  (EligibleMovers/CountEligibleMovers/StructuralCandidates/HasStructuralCandidate) are now
        //  PRIVATE — internal building blocks BuildCandidates/MeasureCapacity/the assignment
        //  diagnosis below are built on, never a second "how much capacity" answer another layer
        //  could reach for instead of MeasureCapacity. Every non-Assignment caller gets exactly TWO
        //  narrow, purpose-built doors instead:
        //    · MeasureCapacity — the ONE aggregate "how much capacity" witness (Demand).
        //    · HasEligibleMover — a single boolean "does ANY usable actor exist at all right now"
        //      (CapabilityPoolExhaustionRegistry's pool-empty check — a structurally different
        //      question from a witnessed capacity SIZE, so it keeps its own name instead of
        //      overloading MeasureCapacity's contract).
        // =======================================================================================
        private static List<ArmySnapshot> EligibleMovers(WorldSnapshot snap, ScoutMissionTarget target,
            ISet<int> excludeArmyIds) => ScoutMoverSelector.Eligible(snap, target, excludeArmyIds);

        private static IEnumerable<ArmySnapshot> StructuralCandidates(WorldSnapshot snap,
            ScoutMissionTarget target) => ScoutMoverSelector.StructuralCandidates(snap, target);

        private static bool HasStructuralCandidate(WorldSnapshot snap, ScoutMissionTarget target) =>
            ScoutMoverSelector.HasStructuralCandidate(snap, target);

        // The one sanctioned boolean "is this pool empty right now" door (CapabilityPoolExhaustionRegistry).
        internal static bool HasEligibleMover(WorldSnapshot snap, ScoutMissionTarget target) =>
            EligibleMovers(snap, target, null).Count > 0;

        // =======================================================================================
        //  E. ResolveExecutionHex — Explore/Refresh execute AT the target; Surveil executes from the
        //     best currently-reachable vantage.
        // =======================================================================================
        internal static HexCoord ResolveExecutionHex(WorldSnapshot snap, ArmySnapshot mover, ScoutMissionTarget target)
        {
            if (target.Kind != ScoutTargetKind.Surveil)
                return target.FocusHex;
            var vantages = SurveilVantageSelector.Rank(snap, mover, target).ToList();
            return vantages.Count > 0 ? vantages[0].ExecutionHex : target.FocusHex;
        }

        // =======================================================================================
        //  B/D. BuildCandidates / AssignFunded — real Provisioning-time assignment. Only FUNDED
        //     missions participate (moved here verbatim from ProvisioningManager, spec §14 — the
        //     one-to-one solver + its scoring stay behaviourally identical, only the owner moves).
        // =======================================================================================
        internal static List<ScoutExecutionCandidate> BuildCandidates(WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, ScoutMissionTarget target, ISet<int> excludeArmyIds,
            PlayerRoot root = null, IReadOnlyList<AirObservationSlot> airPool = null)
        {
            var list = new List<ScoutExecutionCandidate>();
            bool stealthRequired = target.Stealth == StealthRequirement.Required;
            bool surveil = target.Kind == ScoutTargetKind.Surveil;

            List<ArmySnapshot> movers = ScoutMoverSelector.Eligible(snap, target, excludeArmyIds);
            foreach (ArmySnapshot mover in movers)
            {
                if (!surveil)
                {
                    if (ctx?.Map != null)
                    {
                        ArmyData liveMover = ResolveArmy(player, mover.ArmyId);
                        if (liveMover == null
                            || SafeStepPathing.FindNextSafeStep(ctx.Map, liveMover, target.FocusHex) == null)
                            continue;
                    }
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, target.FocusHex, stealthRequired);
                    list.Add(new ScoutExecutionCandidate(mover, target.FocusHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, 0f, 0, pc.AlreadyHidden, pc.RequiredAp));
                    continue;
                }

                ArmyData live = ResolveArmy(player, mover.ArmyId);
                if (live == null) continue;
                foreach (SurveilVantageCandidate v in SurveilVantageSelector.Rank(snap, mover, target))
                {
                    if (SafeStepPathing.FindNextSafeStep(ctx?.Map, live, v.ExecutionHex) == null)
                        continue;
                    ScoutPairCost pc = ScoutCostModel.PairCost(snap, mover, v.ExecutionHex, stealthRequired: true);
                    list.Add(new ScoutExecutionCandidate(mover, v.ExecutionHex, pc.EffActivationAp,
                        pc.EtaTurns, pc.Distance, v.DetectionRisk, v.StandOff, pc.AlreadyHidden, pc.RequiredAp));
                    break;
                }
            }

            AppendAirCandidates(list, snap, ctx, player, root, target, excludeArmyIds, airPool);
            return list;
        }

        // =======================================================================================
        //  ROUND 4/5 — AIR CANDIDATES. Fuses WHICH air actor/airfield executes a funded Observation
        //  mission into the SAME Assignment owner as Ground, instead of AirReconPlanner picking
        //  independently and late. Preserves the hard invariant air never satisfies
        //  Explore/GroundTraversal (never reached — caller filters), and never a stealth-Required /
        //  positive-DetectionRisk mission (air cannot go hidden). `airPool` is the SAME ordered,
        //  per-pass-capped candidate pool (ready standalone wings, then one hangar launch subset per
        //  owned airfield, capped to ReconAirCapacityPolicy.MaxAirReconActorsPerTurn minus wings
        //  already continuing a prior sortie) AssignFunded computes ONCE for the whole batch via
        //  ReconAirCapacityPolicy.EvaluateDetailed — the same primitive ReconAirReservationPrepass
        //  uses for sizing, so the actor pool Assignment considers can never diverge from what the
        //  capacity signal promised Demand.
        //
        //  RECON-AIR-04 (round 5) — feasibility here is no longer the generic "can THIS actor fly
        //  SOME useful step somewhere" SlotWouldFly probe (that check stays correct for RECON-AIR-02
        //  capacity SIZING, a structural "can anything useful happen" question, but is not proof of
        //  fitness for a SPECIFIC mission). Instead each candidate is proven against THIS mission's
        //  actual target: Pick/PickFromStorage is called with the mission's FocusHex (Refresh) or
        //  best reachable vantage (Surveil, AirExisting only — see round-4 scope note below) as the
        //  RECON-AIR-05 mission-focus anchor, and the resulting step must make GENUINE progress
        //  toward that target (strictly closer, or the target already falls within the resulting
        //  vision) — not just clear MinimumUsefulScore somewhere unrelated. RequiredEnergy is now
        //  populated from the SAME real Pick result (RECON-AIR-01), instead of being left at 0.
        //
        //  Round-4 scope note (kept): an AirLaunch candidate (no live ArmyData yet) is restricted to
        //  Refresh-kind targets — FocusHex is used directly, no vantage computation needed. Surveil
        //  vantage selection (SurveilVantageSelector.Rank) needs a real ArmySnapshot position/vision,
        //  which only AirExisting (an already-existing ready wing) has; extending vantage ranking to
        //  a not-yet-launched hangar subset was judged out of this round's safe scope.
        // =======================================================================================
        private static void AppendAirCandidates(List<ScoutExecutionCandidate> list, WorldSnapshot snap,
            AiTurnContext ctx, PlayerSetupData player, PlayerRoot root, ScoutMissionTarget target,
            ISet<int> excludeArmyIds, IReadOnlyList<AirObservationSlot> airPool)
        {
            if (airPool == null || airPool.Count == 0 || root == null || ctx?.Map == null || snap?.Self == null)
                return;
            bool observationClass = ReconScoutKinds.IsRefresh(target.Kind) || ReconScoutKinds.IsSurveil(target.Kind);
            bool stealthOrRisky = target.Stealth == StealthRequirement.Required || target.DetectionRisk > 0f;
            if (!observationClass || stealthOrRisky)
                return;

            ReconMode mode = AirReconModePolicy.RequestedMode(player, snap);
            foreach (AirObservationSlot slot in airPool)
            {
                if (slot.ActorId.HasValue)
                {
                    if (excludeArmyIds != null && excludeArmyIds.Contains(slot.ActorId.Value))
                        continue;
                    ArmySnapshot mover = snap.Self.Armies?.FirstOrDefault(a => a != null && a.ArmyId == slot.ActorId.Value);
                    if (mover == null)
                        continue;
                    ArmyData live = ResolveArmy(player, slot.ActorId.Value);
                    if (live == null)
                        continue;

                    HexCoord anchorTarget;
                    if (target.Kind == ScoutTargetKind.Surveil)
                    {
                        SurveilVantageCandidate? vantage = SurveilVantageSelector.Rank(snap, mover, target)
                            .Select(v => (SurveilVantageCandidate?)v).FirstOrDefault();
                        if (!vantage.HasValue)
                            continue; // no reachable vantage — round-3/4 NoObservationVantage territory
                        anchorTarget = vantage.Value.ExecutionHex;
                    }
                    else
                    {
                        anchorTarget = target.FocusHex;
                    }

                    // Score against the SAME scorer inputs the structural capacity probe and the
                    // executor use (ReconAirReservationPrepass.BuildScoringContextForWing): projected
                    // turn-start sortie state, self-sortie exclusion, AND the per-actor effective
                    // mode (a continuing wing's durable ReconPatrolState.Mode wins over the pass's
                    // global RequestedMode). Any of these left at its default gives a continuing wing
                    // a RouteScore that diverges from what capacity promised Demand and what the
                    // executor will actually fly.
                    (ReconAirSortieState projectedSortie, int excludeSortieId, ReconMode actorMode) =
                        ReconAirReservationPrepass.BuildScoringContextForWing(player, ctx, live, mode);
                    var airScoringCtx = new AirReconScoringContext { ExcludeSortieId = excludeSortieId };
                    ReconAirStepPlanner.StepChoice? choice = ReconAirStepPlanner.Pick(
                        player, ctx, live, snap, actorMode, ctx.TurnNumber, sortieState: projectedSortie,
                        scoringCtx: airScoringCtx, missionFocusHex: anchorTarget);
                    if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                        continue;
                    int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                        + AbilityParams.GetBestRecceRadius(live);
                    if (!MakesGenuineProgress(live.Hex, choice.Value.Hex, anchorTarget, vision))
                        continue;

                    list.Add(new ScoutExecutionCandidate(mover, anchorTarget, Mathf.RoundToInt(choice.Value.ActivationAp),
                        1, 0, 0f, 0, false, choice.Value.ActivationAp, ScoutExecutorKind.AirExisting,
                        requiredEnergy: choice.Value.ActivationEnergy, routeScore: choice.Value.Score));
                }
                else
                {
                    if (target.Kind != ScoutTargetKind.Refresh)
                        continue; // round-4 scope — see note above
                    ArmyData airfield = AviationRules.FindAirfieldAt(slot.AirfieldHex, player);
                    if (airfield == null)
                        continue;
                    List<UnitData> subset = ReconAirCapacityPolicy.SelectReconLaunchSubset(airfield.Members);
                    if (subset.Count == 0)
                        continue;

                    var launchCandidate = new AirLaunchCandidate(slot.AirfieldHex, null, subset);
                    ReconAirStepPlanner.StepChoice? choice = ReconAirStepPlanner.PickFromStorage(
                        player, ctx, launchCandidate, snap, mode, ctx.TurnNumber,
                        scoringCtx: null, missionFocusHex: target.FocusHex);
                    if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                        continue;
                    int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                        + subset.Select(AbilityParams.GetBestRecceRadius).DefaultIfEmpty(0).Max();
                    if (!MakesGenuineProgress(slot.AirfieldHex, choice.Value.Hex, target.FocusHex, vision))
                        continue;

                    list.Add(new ScoutExecutionCandidate(null, target.FocusHex, Mathf.RoundToInt(choice.Value.ActivationAp),
                        1, 0, 0f, 0, false, choice.Value.ActivationAp, ScoutExecutorKind.AirLaunch,
                        slot.AirfieldHex, subset, requiredEnergy: choice.Value.ActivationEnergy,
                        routeScore: choice.Value.Score));
                }
            }
        }

        // RECON-AIR-04 — "makes genuine progress toward THIS target": the candidate step lands
        // strictly closer to the mission's bound target than the actor's current position, OR the
        // target already falls within the resulting vision footprint (so the step itself completes
        // the observation). Proving MinimumUsefulScore alone (the old check) only proved SOME useful
        // step exists somewhere — never that this one serves the mission it is about to be bound to.
        private static bool MakesGenuineProgress(HexCoord from, HexCoord candidateHex, HexCoord missionTarget, int vision)
        {
            int before = HexGridMath.Distance(from, missionTarget);
            int after = HexGridMath.Distance(candidateHex, missionTarget);
            return after < before || after <= Mathf.Max(0, vision);
        }

        // AssignFunded — best one-to-one actor/execution-candidate assignment across every OPEN
        // funded Scout mission at once (bounded exhaustive search + lexicographic scoring; the
        // portfolio is always small — a handful of funded Recon missions per pass). Returns the
        // chosen ScoutExecutionCandidate per mission key, PLUS (round 3 / Problem 2) a structured
        // rejection reason for every mission that got none — computed from the SAME cands[i] list
        // the batch solve itself used, so Provisioning's classifier never re-derives eligibility.
        public static ReconAssignmentResult AssignFunded(
            WorldSnapshot snap, AiTurnContext ctx, PlayerSetupData player,
            List<FundedEntry> open, ISet<int> alreadyClaimedArmyIds, PlayerRoot root = null,
            IReadOnlyCollection<ProvisionedMission> alreadyProvisioned = null)
        {
            var result = new ReconAssignmentResult();
            if (open == null || open.Count == 0)
                return result;

            // Round 4/5 — the SAME ordered, per-pass-capped air-actor pool for every mission in this
            // batch. Continuing airborne Recon wings participate in the same funded Assignment pool
            // as ready wings (they are ordered FIRST, and ScoreScoutAssignment gives the incumbent a
            // continuity PREFERENCE) — not an execution entitlement outside this solve. Computed once
            // so the MaxAirReconActorsPerTurn ceiling is a property of the WHOLE batch, not silently
            // re-granted per mission.
            //
            // RECON-AIR-03 (Problem: filter-before-take) — feasibility (EvaluateAirStructuralFeasibility)
            // MUST run BEFORE `.Take(remaining)`, never after: taking first and filtering second lets an
            // early infeasible candidate silently consume one of the `remaining` slots that a later,
            // genuinely valid candidate needed — the later candidate is truncated off the list
            // before its feasibility is ever checked. `BuildFeasibleAirPool` is `.Where(...).Take(...)`.
            List<AirObservationSlot> airPool = null;
            int airEnergyBudget = 0;
            int airActorCap = 0;
            int claimedGroundActors = 0;
            int claimedAirActors = 0;
            if (alreadyClaimedArmyIds != null)
            {
                foreach (int claimedId in alreadyClaimedArmyIds)
                {
                    ArmySnapshot claimed = snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == claimedId);
                    if (claimedId < 0 || claimed?.IsAir == true)
                        claimedAirActors++;
                    else if (claimed?.IsSoloRecce == true)
                        claimedGroundActors++;
                }
            }
            if (root != null && player != null)
            {
                ReconAirObservationDetail detail = ReconAirCapacityPolicy.EvaluateDetailed(player, root);
                // Round 7 (Problem 1) — an already-airborne wing (detail.AirborneWings) is no longer
                // handled outside Assignment (AirReconPlanner's old ContinueActorIds bypass is gone):
                // it must compete for a FRESH ProvisionedMission through this SAME pool, exactly like
                // a ready idle wing or a hangar launch, to keep making strategic Recon progress. The
                // per-pass actor cap is therefore the WHOLE MaxAirReconActorsPerTurn ceiling now
                // (no longer pre-reserving slots for continuing wings outside this solve), and
                // airborne wings are ordered FIRST so an incumbent continuing sortie is not truncated
                // out of the pool by BuildFeasibleAirPool before ScoreScoutAssignment's
                // actorDiscontinuity continuity preference even gets to consider it.
                airActorCap = Mathf.Max(0,
                    ReconAirCapacityPolicy.MaxAirReconActorsPerTurn - claimedAirActors);
                // Round 8 (Problem 3) — Assignment answers WHO executes a funded mission, it is NOT a
                // second resource-admission authority. The air-actor pool is now filtered by
                // STRUCTURAL feasibility only (EvaluateAirStructuralFeasibility: actor / route / mode /
                // target-progress, no AP, no Energy, no AviationSortieReservationEvaluator.ShouldReserve)
                // — SlotWouldFly's activation-economics half is gone from here. EnergyBudgetBase is
                // likewise no longer an admission ceiling in the batch solve (airEnergyBudget is left
                // at int.MaxValue so RecurseScout's guard can never fire): the exact per-actor AP/Energy
                // is resolved after binding (AppendAirCandidates) and reconciled by
                // ProvisioningManager.ProvisionAir -> EnvelopeTooSmall -> ResourceAllocator.Repack, the
                // ONE funding authority.
                airEnergyBudget = int.MaxValue;
                ReconMode mode = AirReconModePolicy.RequestedMode(player, snap);
                IEnumerable<AirObservationSlot> ordered = detail.AirborneWings.Concat(detail.SpareCandidatesInOrder);
                airPool = BuildFeasibleAirPool(ordered, airActorCap,
                    slot => ReconAirReservationPrepass.EvaluateAirStructuralFeasibility(
                        player, ctx, snap, mode, slot, null).Feasible);
            }

            var cands = new List<List<ScoutExecutionCandidate>>(open.Count);
            foreach (FundedEntry fe in open)
            {
                var target = (ScoutMissionTarget)fe.Mission.Target;
                cands.Add(BuildCandidates(snap, ctx, player, target, alreadyClaimedArmyIds, root, airPool));
            }

            int groundActorCap = Mathf.Max(0, ReconConcurrencyPolicy.HardCap - claimedGroundActors);
            List<HexCoord> fixedGroundFoci = (alreadyProvisioned ?? System.Array.Empty<ProvisionedMission>())
                .Where(pm => pm != null && pm.Kind == MissionKind.Scout
                    && pm.ExecutorKind == ScoutExecutorKind.Ground)
                .Select(pm => pm.FocusHex).ToList();
            ReconAssignmentResult solved = AssignFromCandidates(
                open, cands, airEnergyBudget, airActorCap, groundActorCap, fixedGroundFoci);
            foreach (KeyValuePair<StableMissionKey, ScoutExecutionCandidate> kv in solved.Assigned)
                result.Assigned[kv.Key] = kv.Value;

            for (int i = 0; i < open.Count; i++)
            {
                StableMissionKey key = StableMissionKey.For(open[i].Mission);
                if (result.Assigned.ContainsKey(key))
                    continue;
                // cands[i] is EXACTLY the candidate list the solver considered for this mission —
                // empty means no structural/executable candidate existed at all this pass; non-empty
                // means a real candidate existed but lost the batch's one-actor-per-job competition
                // (or a cumulative air constraint) to a higher-priority mission (genuine contention,
                // not absence).
                var target = (ScoutMissionTarget)open[i].Mission.Target;
                result.Rejected[key] = cands[i].Count > 0
                    ? ScoutAssignmentFailureReason.MoverContended
                    : DiagnoseEmpty(snap, ctx, player, target, alreadyClaimedArmyIds);
            }

            if (open.Count > 0)
                AiDebugLog.Write($"[AI][V2][Recon][Assignment] assignFunded — {open.Count} open, assigned ["
                    + string.Join(" ", result.Assigned.Select(kv =>
                        $"{kv.Key}->{kv.Value.ExecutorKind}#{kv.Value.ActorKey}@({kv.Value.ExecutionHex.Q},{kv.Value.ExecutionHex.R})")) + "]"
                    + (result.Rejected.Count > 0 ? $" rejected [{string.Join(" ", result.Rejected.Select(kv => $"{kv.Key}:{kv.Value}"))}]" : ""));
            return result;
        }

        // Round 3 (Problem 2) — moved verbatim from ProvisioningManager.ClassifyNoAssignment: the
        // ONE place that decides WHY a Scout job with zero executable candidates has none. Only
        // called when BuildCandidates already came back empty for this exact (target, exclude) pair
        // — never re-runs BuildCandidates itself, just narrates it with the structural/eligibility
        // primitives this class already owns.
        private static ScoutAssignmentFailureReason DiagnoseEmpty(WorldSnapshot snap, AiTurnContext ctx,
            PlayerSetupData player, ScoutMissionTarget target, ISet<int> excludeArmyIds)
        {
            bool surveil = target.Kind == ScoutTargetKind.Surveil;

            if (!HasStructuralCandidate(snap, target))
                return ScoutAssignmentFailureReason.NoMoverExists;

            if (!surveil)
            {
                // An unassigned ground Explore/Refresh is only MoverContended if a capable UNCLAIMED
                // scout that could actually take a safe first step toward the focus was preferred
                // elsewhere. If unclaimed eligible scouts exist but NONE can reach the focus this
                // turn, that is NoExecutableStep, not contention.
                List<ArmySnapshot> freeEligible = EligibleMovers(snap, target, excludeArmyIds);
                if (freeEligible.Count > 0 && ctx?.Map != null)
                {
                    bool anyReachable = freeEligible.Any(mv =>
                    {
                        ArmyData live = ResolveArmy(player, mv.ArmyId);
                        return live != null
                            && SafeStepPathing.FindNextSafeStep(ctx.Map, live, target.FocusHex) != null;
                    });
                    if (!anyReachable)
                        return ScoutAssignmentFailureReason.NoExecutableStep;
                }
                return ScoutAssignmentFailureReason.MoverContended;
            }

            bool anyStructuralVantage = StructuralCandidates(snap, target)
                .Any(mv => SurveilVantageSelector.Rank(snap, mv, target).Count > 0);
            if (!anyStructuralVantage)
                return ScoutAssignmentFailureReason.NoObservationVantage;

            bool anyEligibleHasVantage = EligibleMovers(snap, target, excludeArmyIds)
                .Any(mv => SurveilVantageSelector.Rank(snap, mv, target).Count > 0);
            return anyEligibleHasVantage
                ? ScoutAssignmentFailureReason.NoExecutableStep
                : ScoutAssignmentFailureReason.MoverContended;
        }

        // RECON-AIR-03 — the batch solve's CUMULATIVE air constraints, enforced HERE (not just as a
        // per-pool sizing cap) so no combination the solver could pick ever exceeds what a shared
        // physical resource can actually support across the WHOLE batch at once:
        //   · one actor/subset -> at most one mission (usedArmyIds — pre-existing, ActorKey already
        //     disambiguates AirLaunch by airfield, so this doubles as "one airfield subset -> at
        //     most one mission" too).
        //   · airActorCap — total DISTINCT air actors (AirExisting + AirLaunch) chosen across the
        //     whole batch never exceeds ReconAirCapacityPolicy.MaxAirReconActorsPerTurn (minus wings
        //     already continuing a prior sortie). Defence in depth on top of the pool already being
        //     sized to this same cap (BuildFeasibleAirPool) — a batch can never pick MORE distinct
        //     air actors than the pool holds, but this makes the invariant explicit and unit-testable
        //     independent of pool construction.
        //   · airEnergyBudget — the cumulative Energy TWO OR MORE AirLaunch candidates would consume
        //     together is checked against ONE shared budget, not against the full stockpile
        //     independently per mission (the bug: two launches each individually affordable, but not
        //     jointly). This is a SOFT, best-effort guard — Provisioning + Generic Funding remain the
        //     real resource authority (ProvisioningManager.ProvisionAir / ProvisioningSession.
        //     EnergyClaimed do the authoritative, sequential real check) — this only stops Assignment
        //     from greedily proposing a combination Provisioning is certain to reject.
        private static void RecurseScout(int i, List<FundedEntry> open, List<List<ScoutExecutionCandidate>> cands,
            int[] chosen, HashSet<int> usedArmyIds, ref long[] bestKey, int[] best,
            float airEnergyBudget, int airActorCap, int groundActorCap,
            IReadOnlyList<HexCoord> fixedGroundFoci,
            float usedAirLaunchEnergy = 0f, int usedAirActors = 0, int usedGroundActors = 0)
        {
            if (i == open.Count)
            {
                long[] key = ScoreScoutAssignment(open, cands, chosen);
                if (bestKey == null || Lex(key, bestKey) < 0)
                {
                    bestKey = key;
                    System.Array.Copy(chosen, best, chosen.Length);
                }
                return;
            }

            chosen[i] = -1;
            RecurseScout(i + 1, open, cands, chosen, usedArmyIds, ref bestKey, best,
                airEnergyBudget, airActorCap, groundActorCap,
                fixedGroundFoci,
                usedAirLaunchEnergy, usedAirActors, usedGroundActors);
            for (int c = 0; c < cands[i].Count; c++)
            {
                ScoutExecutionCandidate cand = cands[i][c];
                int aid = cand.ActorKey;
                if (usedArmyIds.Contains(aid)) continue;

                bool isAir = cand.ExecutorKind != ScoutExecutorKind.Ground;
                if (isAir && usedAirActors + 1 > airActorCap)
                    continue;
                if (!isAir && usedGroundActors + 1 > groundActorCap)
                    continue;
                if (!isAir)
                {
                    bool tooCloseToChosenGround = false;
                    ScoutMissionTarget target = (ScoutMissionTarget)open[i].Mission.Target;
                    if (fixedGroundFoci != null && fixedGroundFoci.Any(focus =>
                        HexGridMath.Distance(target.FocusHex, focus) < AiConfigV2.scoutTargetMinSeparation))
                        tooCloseToChosenGround = true;
                    for (int j = 0; j < i; j++)
                    {
                        if (chosen[j] < 0)
                            continue;
                        ScoutExecutionCandidate other = cands[j][chosen[j]];
                        if (other.ExecutorKind != ScoutExecutorKind.Ground)
                            continue;
                        ScoutMissionTarget otherTarget = (ScoutMissionTarget)open[j].Mission.Target;
                        if (HexGridMath.Distance(target.FocusHex, otherTarget.FocusHex)
                            < AiConfigV2.scoutTargetMinSeparation)
                        {
                            tooCloseToChosenGround = true;
                            break;
                        }
                    }
                    if (tooCloseToChosenGround)
                        continue;
                }
                bool isAirLaunch = cand.ExecutorKind == ScoutExecutorKind.AirLaunch;
                float nextAirLaunchEnergy = usedAirLaunchEnergy + (isAirLaunch ? cand.RequiredEnergy : 0f);
                // Round 8 (Problem 3) — AssignFunded now passes airEnergyBudget = int.MaxValue: Energy
                // affordability is ProvisioningManager + ResourceAllocator's job alone, never a second
                // Recon-scoped admission decision here. Guard kept (not deleted) so AssignFromCandidates'
                // signature and its focused test stay stable; it simply never fires in production.
                if (isAirLaunch && nextAirLaunchEnergy > airEnergyBudget + AiConfigV2.allocatorSliceEpsilon)
                    continue;

                usedArmyIds.Add(aid);
                chosen[i] = c;
                RecurseScout(i + 1, open, cands, chosen, usedArmyIds, ref bestKey, best,
                    airEnergyBudget, airActorCap, groundActorCap, fixedGroundFoci,
                    nextAirLaunchEnergy,
                    usedAirActors + (isAir ? 1 : 0), usedGroundActors + (isAir ? 0 : 1));
                usedArmyIds.Remove(aid);
            }
            chosen[i] = -1;
        }

        // RECON-AIR-03 — real batch-solve entry point over ALREADY-BUILT candidate lists (the exact
        // production `cands` AssignFunded builds via BuildCandidates/AppendAirCandidates), separated
        // out so both AssignFunded and a focused test can drive the SAME solver/scoring/cumulative-
        // constraint code without re-deriving live-world candidate generation.
        internal static ReconAssignmentResult AssignFromCandidates(List<FundedEntry> open,
            List<List<ScoutExecutionCandidate>> cands, float airEnergyBudget, int airActorCap)
        {
            return AssignFromCandidates(open, cands, airEnergyBudget, airActorCap,
                ReconConcurrencyPolicy.HardCap, null);
        }

        private static ReconAssignmentResult AssignFromCandidates(List<FundedEntry> open,
            List<List<ScoutExecutionCandidate>> cands, float airEnergyBudget, int airActorCap,
            int groundActorCap, IReadOnlyList<HexCoord> fixedGroundFoci)
        {
            var result = new ReconAssignmentResult();
            if (open == null || open.Count == 0)
                return result;

            var chosen = new int[open.Count];
            var best = new int[open.Count];
            for (int i = 0; i < best.Length; i++) best[i] = -1;
            long[] bestKey = null;
            RecurseScout(0, open, cands, chosen, new HashSet<int>(), ref bestKey, best,
                airEnergyBudget, airActorCap, groundActorCap, fixedGroundFoci);

            for (int i = 0; i < open.Count; i++)
                if (best[i] >= 0)
                    result.Assigned[StableMissionKey.For(open[i].Mission)] = cands[i][best[i]];
            return result;
        }

        // RECON-AIR-03 — filter-THEN-take, never the reverse (see AssignFunded's call site comment
        // for why the ordering itself was the bug). A small, independently-testable pure function.
        internal static List<AirObservationSlot> BuildFeasibleAirPool(IEnumerable<AirObservationSlot> ordered,
            int take, System.Func<AirObservationSlot, bool> feasible)
        {
            var result = new List<AirObservationSlot>();
            if (ordered == null || take <= 0)
                return result;
            foreach (AirObservationSlot slot in ordered)
            {
                if (result.Count >= take)
                    break;
                if (feasible == null || feasible(slot))
                    result.Add(slot);
            }
            return result;
        }

        private static long[] ScoreScoutAssignment(List<FundedEntry> open,
            List<List<ScoutExecutionCandidate>> cands, int[] chosen)
        {
            int n = open.Count;
            int covered = 0;
            long priorityCoverage = 0;
            int envelopeViolations = 0;
            long envelopeOverflow = 0;
            int actorDiscontinuity = 0;
            int wastedStealth = 0;
            long risk = 0, standOff = 0, requiredAp = 0, eta = 0, dist = 0;

            for (int i = 0; i < n; i++)
            {
                if (chosen[i] < 0) continue;
                ScoutExecutionCandidate cand = cands[i][chosen[i]];
                covered++;
                priorityCoverage += n - i;

                float apOverflow = Mathf.Max(0f, cand.RequiredAp - open[i].Tentative.Ap);
                float energyOverflow = Mathf.Max(0f, cand.RequiredEnergy - open[i].PhysicalDraw.Energy);
                if (apOverflow > AiConfigV2.allocatorSliceEpsilon
                    || energyOverflow > AiConfigV2.allocatorSliceEpsilon)
                {
                    envelopeViolations++;
                    envelopeOverflow += Mathf.RoundToInt((apOverflow + energyOverflow) * 1000f);
                }

                int? preferred = open[i].Mission.PreferredMoverArmyId;
                if (preferred.HasValue && cand.ActorKey != preferred.Value
                    && cands[i].Any(alt => alt.ActorKey == preferred.Value))
                    actorDiscontinuity++;

                var target = (ScoutMissionTarget)open[i].Mission.Target;
                bool needStealth = target.Stealth == StealthRequirement.Required;
                if (!needStealth && cand.IsStealthCapableMover
                    && cands[i].Any(alt => !alt.IsStealthCapableMover))
                    wastedStealth++;

                risk += Mathf.RoundToInt(cand.DetectionRisk * 1_000_000f);
                standOff += cand.StandOff;
                requiredAp += Mathf.RoundToInt(cand.RequiredAp);
                eta += cand.EtaTurns;
                dist += cand.Distance;
            }

            var key = new long[11 + 3 * n];
            key[0] = -covered;
            key[1] = -priorityCoverage;
            // Prefer an actor that fits the envelope already funded for this mission. This keeps a
            // feasible ground candidate from being displaced by an air candidate that Provisioning
            // must immediately reject/repack, without making Assignment a second funding authority.
            key[2] = envelopeViolations;
            key[3] = envelopeOverflow;
            key[4] = actorDiscontinuity;
            key[5] = wastedStealth;
            key[6] = risk;
            key[7] = -standOff;
            key[8] = requiredAp;
            key[9] = eta;
            key[10] = dist;
            for (int i = 0; i < n; i++)
            {
                int b = 11 + 3 * i;
                if (chosen[i] < 0)
                    key[b] = key[b + 1] = key[b + 2] = long.MaxValue;
                else
                {
                    ScoutExecutionCandidate cand = cands[i][chosen[i]];
                    key[b] = cand.ActorKey;
                    key[b + 1] = cand.ExecutionHex.Q;
                    key[b + 2] = cand.ExecutionHex.R;
                }
            }
            return key;
        }

        private static int Lex(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0) return c;
            }
            return 0;
        }

        private static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        // =======================================================================================
        //  C. MeasureCapacity — Demand's ONE read-only aggregate query. Moved verbatim from
        //     DemandLayer.ComputeReconWitness/SolveReconFlow (spec §5/§9 checklist) — Demand must
        //     not know HOW the matching is produced, only the resulting witnessed counts.
        //
        //     A small max-flow network, not a bipartite matching — see the historical note kept
        //     below (unchanged reasoning, only the owner moved):
        //       1. Matching actor<->individual-objective maximises JOB COUNT, not CLASS COVERAGE.
        //       2. Anonymous per-class quota slots fix #1 but throw away JOB uniqueness.
        //     A flow network keeps BOTH constraints (job <= 1 actor, actor <= 1 job) AND the
        //     remaining per-class quota as one problem, solved breadth-then-depth so every
        //     outstanding class gets at least one unit before any class gets a second.
        // =======================================================================================
        public static ReconCapacityMeasurement MeasureCapacity(AiTurnContext ctx, PlayerSetupData player,
            WorldSnapshot snap, ReconCapacitySnapshot capacity, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, IReadOnlyList<ReconObjective> groundVisitRunnable,
            IReadOnlyList<ReconObjective> observationRunnable,
            IReadOnlyList<ReconObjective> stealthGroundRunnable = null,
            IReadOnlyList<ReconObjective> stealthObservationRunnable = null)
        {
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>();
            ArmySnapshot Resolve(int id) => armies.FirstOrDefault(a => a != null && a.ArmyId == id);

            var laneTarget = new Dictionary<int, ScoutMissionTarget>();
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i?.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value) || i.Scout.RequiresStealth)
                        continue;
                    laneTarget[i.PreferredMoverArmyId.Value] = new ScoutMissionTarget
                    {
                        FocusHex = i.Scout.FocusHex,
                        Kind = i.Scout.Kind,
                        Stealth = StealthRequirement.None,
                        DetectionRisk = 0f,
                    };
                }

            var laneActorClaimed = new HashSet<int>();
            int RevalidateLane(IEnumerable<int> ids)
            {
                int n = 0;
                foreach (int id in ids)
                {
                    if (laneActorClaimed.Contains(id))
                        continue;
                    ArmySnapshot a = Resolve(id);
                    if (a == null || !laneTarget.TryGetValue(id, out ScoutMissionTarget t))
                        continue;
                    if (CanExecute(ctx, player, snap, a, t))
                    {
                        laneActorClaimed.Add(id);
                        n++;
                    }
                }
                return n;
            }

            int groundLaneWitnessed = RevalidateLane(capacity.GenericGroundLaneActors);
            int obsLaneWitnessed = RevalidateLane(capacity.GenericObservationLaneActors);

            var idleActors = armies.Where(a => a != null && capacity.IdleGroundScouts.Contains(a.ArmyId)).ToList();
            int remainingGroundSlots = Mathf.Max(0, capacity.DesiredGroundTraversalConcurrency - groundLaneWitnessed);
            int remainingObsSlots = Mathf.Max(0, capacity.DesiredObservationConcurrency - obsLaneWitnessed
                - capacity.AirborneReconLanes - capacity.SpareAirObservationSorties);

            (int groundP1, int obsP1, var usedActors, var usedGroundIdx, var usedObsIdx) = SolveReconFlow(
                ctx, player, snap, idleActors, groundVisitRunnable, observationRunnable,
                Mathf.Min(remainingGroundSlots, 1), Mathf.Min(remainingObsSlots, 1));

            var leftoverActors = idleActors.Where(a => !usedActors.Contains(a.ArmyId)).ToList();
            var leftoverGround = groundVisitRunnable == null ? new List<ReconObjective>()
                : groundVisitRunnable.Where((o, idx) => !usedGroundIdx.Contains(idx)).ToList();
            var leftoverObs = observationRunnable == null ? new List<ReconObjective>()
                : observationRunnable.Where((o, idx) => !usedObsIdx.Contains(idx)).ToList();

            (int groundP2, int obsP2, _, _, _) = SolveReconFlow(
                ctx, player, snap, leftoverActors, leftoverGround, leftoverObs,
                remainingGroundSlots - groundP1, remainingObsSlots - obsP1);

            int groundIdleWitnessed = groundP1 + groundP2;
            int obsIdleWitnessed = obsP1 + obsP2;

            // Round 3 (Problem 3) — the stealth-lane witness, produced by the SAME joint
            // actor<->job matching as the generic numbers above (never a second raw eligibility
            // count): stealth-capable, currently-unclaimed movers against the runnable stealth
            // objectives, split ground/observation exactly like the generic classes.
            int stealthGroundWitnessed = 0, stealthObsWitnessed = 0;
            bool anyStealthJobs = (stealthGroundRunnable != null && stealthGroundRunnable.Count > 0)
                || (stealthObservationRunnable != null && stealthObservationRunnable.Count > 0);
            if (anyStealthJobs)
            {
                var stealthTarget = new ScoutMissionTarget { Stealth = StealthRequirement.Required };
                ISet<int> claimed = commitments?.ClaimedArmyIdSet;
                List<ArmySnapshot> stealthActors = EligibleMovers(snap, stealthTarget, claimed);
                (stealthGroundWitnessed, stealthObsWitnessed, _, _, _) = SolveReconFlow(
                    ctx, player, snap, stealthActors, stealthGroundRunnable, stealthObservationRunnable,
                    stealthGroundRunnable?.Count ?? 0, stealthObservationRunnable?.Count ?? 0);
            }

            return new ReconCapacityMeasurement(groundLaneWitnessed, obsLaneWitnessed, groundIdleWitnessed,
                obsIdleWitnessed, stealthGroundWitnessed, stealthObsWitnessed);
        }

        // =======================================================================================
        //  RECON-AIR-02 (round 5) — the AIR half of Demand's "how much capacity" witness, folded
        //  into the SAME canonical Assignment/capacity owner as MeasureCapacity's ground numbers
        //  (moved here verbatim from the old ReconAirReservationPrepass.Run — a separate
        //  orchestrated capacity-sizing stage with its own per-turn registry). Recomputed fresh on
        //  every call, exactly like MeasureCapacity's own SolveReconFlow passes — no cross-call
        //  state, no registry. Answers the SAME structural question MeasureCapacity answers for
        //  ground: "does a usable actor structurally exist" (SlotWouldFly proves a route/energy-
        //  opportunity exists RIGHT NOW), never "is it funded" — funding is Generic Funding's job.
        //
        //  Two call sites (DemandLayer.ReconDemands calls this, then MeasureCapacity) rather than
        //  one because ReconCapacitySnapshot.Build needs these numbers as an INPUT to size its own
        //  Desired/deficit fields, which MeasureCapacity's ground witness then reads back out of the
        //  built snapshot (capacity.AirborneReconLanes / SpareAirObservationSorties) — a genuine
        //  ordering dependency, not a second capacity authority: both calls live on this one class,
        //  and DemandLayer never talks to a separate air-specific class for capacity any more.
        // =======================================================================================
        public static (int AirborneWitnessed, int SpareLaunchWitnessed) MeasureAirCapacity(
            AiTurnContext ctx, PlayerSetupData player, PlayerRoot root, WorldSnapshot snap,
            IReadOnlyList<ReconObjective> reconObjectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments)
        {
            if (player == null || root == null)
            {
                AiDebugLog.Write("[AI][V2][ReconAirCap] no player/root — nothing structurally available");
                return (0, 0);
            }

            // How many GENERIC Observation lanes (Refresh / Surveil, non-stealth) are worth
            // guaranteeing with air: runnable generic observation objectives, capped by the desired
            // observation concurrency minus the generic observation lanes already claimed by a
            // ground/air actor.
            var obsRunnable = (reconObjectives ?? System.Array.Empty<ReconObjective>())
                .Where(o => o != null && o.BaseValue > 0f
                    && o.Kind != ReconObjectiveKind.Explore
                    && o.Stealth != StealthRequirement.Required && !(o.DetectionRisk > 0f))
                .OrderByDescending(o => o.BaseValue).ThenBy(o => o.IntentKey)
                .ToList();

            var activeObsLaneActors = new HashSet<int>();
            var airActorIds = new HashSet<int>((snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.IsAir).Select(a => a.ArmyId));
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                    if (i?.Scout != null && !i.Scout.RequiresStealth
                        && i.Scout.Kind != ScoutTargetKind.Explore
                        && i.PreferredMoverArmyId.HasValue
                        && commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value)
                        && !airActorIds.Contains(i.PreferredMoverArmyId.Value))
                        activeObsLaneActors.Add(i.PreferredMoverArmyId.Value);

            int desiredObs = ReconConcurrencyPolicy.DesiredForClass(
                snap, obsRunnable, ReconConcurrencyPolicy.ReconCoverageClass.Observation);
            int observationNeed = Mathf.Clamp(
                obsRunnable.Count, 0, Mathf.Max(0, desiredObs - activeObsLaneActors.Count));

            ReconAirObservationDetail detail = ReconAirCapacityPolicy.EvaluateDetailed(player, root);
            ReconMode mode = AirReconModePolicy.RequestedMode(player, snap);

            // Round 7 (Problem 2) — STRUCTURAL greedy, in the executor's own order. CAPABILITY
            // != FUNDING: this witness never consults root.ActionPoints, EnergyBudgetBase,
            // slot.Ap/Energy-vs-budget or AviationSortieReservationEvaluator.ShouldReserve — that
            // activation-economics question lives only in ProvisioningManager.
            // AirSortieReservationAdmission, at Provisioning time.
            //
            // Round 8 (Problem 2) — "structural" is NOT "structural somewhere". A structurally
            // flyable wing counts as Observation capacity only if it makes GENUINE progress toward
            // one of the concrete runnable Observation objectives, tested with the SAME
            // Pick(missionFocusHex:) + MakesGenuineProgress rule AppendAirCandidates binds a real
            // funded mission with (AirActorProgressesAnObjective below). Capacity candidate rules ==
            // Assignment candidate rules; a wing whose only useful step is toward somewhere OTHER
            // than any runnable objective is phantom capacity and no longer witnessed here.
            int slotsUsed = 0;
            int airborneProbed = 0, airborneStuck = 0, launchProbed = 0, launchRejected = 0;
            int airborneWitnessed = 0, spareLaunchWitnessed = 0;

            // Wedges (from our Citadel) reserved by an accepted STORAGE launch this pass. They have
            // no live army yet, so the next structural probe would not see them; feeding them
            // forward stops two reserved launch sorties both claiming one wedge and producing a
            // capacity count that collapses in execution.
            HexCoord citadelHex = snap?.Self != null ? snap.Self.Citadel : default;
            var provisionalWedges = new List<ReconSector>();
            var reservedActorIds = new HashSet<int>();
            // Round 8 (Problem 2) — an Observation objective already witnessed by one air actor is
            // struck off so a second wing cannot witness the SAME job (mirrors the one-actor <= one-
            // job constraint the real batch solve enforces).
            var consumedObjectiveKeys = new HashSet<MissionIntentKey>();

            foreach (AirObservationSlot wing in detail.AirborneWings)
            {
                if (slotsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn)
                    break;
                airborneProbed++;
                slotsUsed++;

                AirStructuralFeasibility sf = ReconAirReservationPrepass.EvaluateAirStructuralFeasibility(
                    player, ctx, snap, mode, wing, provisionalWedges);
                if (sf.Feasible
                    && AirActorProgressesAnObjective(ctx, player, snap, mode, wing, obsRunnable,
                        consumedObjectiveKeys, provisionalWedges))
                {
                    airborneWitnessed++;
                    if (wing.ActorId.HasValue)
                        reservedActorIds.Add(wing.ActorId.Value);
                }
                else
                {
                    airborneStuck++;   // recovery protected / flyable elsewhere, but not observation capacity
                }
            }

            int launchNeed = Mathf.Max(0, observationNeed - airborneWitnessed);
            foreach (AirObservationSlot slot in detail.SpareCandidatesInOrder)
            {
                if (slotsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn
                    || spareLaunchWitnessed >= launchNeed)
                    break;
                launchProbed++;

                AirStructuralFeasibility sf = ReconAirReservationPrepass.EvaluateAirStructuralFeasibility(
                    player, ctx, snap, mode, slot, provisionalWedges);
                if (!sf.Feasible
                    || !AirActorProgressesAnObjective(ctx, player, snap, mode, slot, obsRunnable,
                        consumedObjectiveKeys, provisionalWedges))
                {
                    launchRejected++;
                    continue;
                }
                spareLaunchWitnessed++;
                slotsUsed++;
                if (slot.ActorId.HasValue)
                    reservedActorIds.Add(slot.ActorId.Value);
                if (ctx?.Map != null)
                    provisionalWedges.Add(ReconDirectionModel.Sector(citadelHex, sf.ChosenHex));
            }

            AiDebugLog.Write($"[AI][V2][ReconAirCap] structuralObsLanes={airborneWitnessed + spareLaunchWitnessed} "
                + $"(airborne {airborneWitnessed}/{airborneProbed} stuck {airborneStuck} + "
                + $"launch {spareLaunchWitnessed}/{launchProbed} rejected {launchRejected}) "
                + $"obsNeed={observationNeed} desiredObs={desiredObs} activeObsLanes={activeObsLaneActors.Count} "
                + $"mode={mode} (structural-only capability witness — no AP/Energy/reservation read; "
                + "activation economics happen only at Provisioning time)");

            return (airborneWitnessed, spareLaunchWitnessed);
        }

        // Round 8 (Problem 2) — the target-anchored half of the air Observation-capacity witness.
        // A structurally flyable air actor is Observation capacity only if it makes GENUINE progress
        // toward at least one still-unmatched runnable Observation objective, tested with the EXACT
        // primitives AppendAirCandidates binds a real funded mission with:
        //   · Refresh objective -> anchor = its FocusHex.
        //   · Surveil objective -> anchor = best reachable vantage (needs a live wing position/vision,
        //     so a not-yet-launched hangar subset is Refresh-only, mirroring AppendAirCandidates'
        //     round-4 scope note).
        //   · ReconAirStepPlanner.Pick / PickFromStorage with missionFocusHex = anchor, score must
        //     clear MinimumUsefulScore, and the resulting step must MakesGenuineProgress toward the
        //     anchor (strictly closer, or the anchor already falls inside the resulting vision).
        // Objectives already witnessed by an earlier actor this call are struck off (one actor <=
        // one job), so N wings can never all witness the same job.
        private static bool AirActorProgressesAnObjective(AiTurnContext ctx, PlayerSetupData player,
            WorldSnapshot snap, ReconMode mode, AirObservationSlot slot,
            IReadOnlyList<ReconObjective> obsRunnable, HashSet<MissionIntentKey> consumedObjectiveKeys,
            IReadOnlyList<ReconSector> provisionalWedges)
        {
            if (ctx?.Map == null)
                return true; // bare test harness — mirror EvaluateAirStructuralFeasibility's own fallback
            if (obsRunnable == null || obsRunnable.Count == 0)
                return false;

            ArmyData live = slot.ActorId.HasValue ? ResolveArmy(player, slot.ActorId.Value) : null;
            ArmySnapshot mover = slot.ActorId.HasValue
                ? snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == slot.ActorId.Value)
                : null;
            if (slot.ActorId.HasValue && (live == null || mover == null))
                return false;

            List<UnitData> subset = null;
            if (!slot.ActorId.HasValue)
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(slot.AirfieldHex, player);
                if (airfield == null)
                    return false;
                subset = ReconAirCapacityPolicy.SelectReconLaunchSubset(airfield.Members);
                if (subset.Count == 0)
                    return false;
            }

            int baseVision = ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0;
            int vision = live != null
                ? baseVision + AbilityParams.GetBestRecceRadius(live)
                : baseVision + subset.Select(AbilityParams.GetBestRecceRadius).DefaultIfEmpty(0).Max();
            HexCoord from = live != null ? live.Hex : slot.AirfieldHex;

            foreach (ReconObjective o in obsRunnable)
            {
                if (o == null || consumedObjectiveKeys.Contains(o.IntentKey))
                    continue;

                HexCoord anchor;
                if (o.Kind == ReconObjectiveKind.Surveil)
                {
                    if (mover == null)
                        continue; // launch subset — Refresh-only (round-4 scope)
                    SurveilVantageCandidate? v = SurveilVantageSelector.Rank(snap, mover, o.ToTarget())
                        .Select(x => (SurveilVantageCandidate?)x).FirstOrDefault();
                    if (!v.HasValue)
                        continue;
                    anchor = v.Value.ExecutionHex;
                }
                else
                {
                    anchor = o.FocusHex;
                }

                ReconAirStepPlanner.StepChoice? choice;
                if (live != null)
                {
                    (ReconAirSortieState projected, int excludeSortieId, ReconMode actorMode) =
                        ReconAirReservationPrepass.BuildScoringContextForWing(player, ctx, live, mode);
                    var scoring = new AirReconScoringContext
                    {
                        ExcludeSortieId = excludeSortieId,
                        ProvisionalWedgeClaims = provisionalWedges,
                    };
                    choice = ReconAirStepPlanner.Pick(player, ctx, live, snap, actorMode, ctx.TurnNumber,
                        sortieState: projected, scoringCtx: scoring, missionFocusHex: anchor);
                }
                else
                {
                    var scoring = new AirReconScoringContext { ProvisionalWedgeClaims = provisionalWedges };
                    choice = ReconAirStepPlanner.PickFromStorage(player, ctx,
                        new AirLaunchCandidate(slot.AirfieldHex, null, subset), snap, mode, ctx.TurnNumber,
                        scoringCtx: scoring, missionFocusHex: anchor);
                }
                if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                    continue;
                if (!MakesGenuineProgress(from, choice.Value.Hex, anchor, vision))
                    continue;

                consumedObjectiveKeys.Add(o.IntentKey);
                return true;
            }
            return false;
        }

        // One max-flow solve: source -> each actor (cap 1) -> each individual job it can reach
        // (cap 1) -> that job's class-aggregator (cap 1, job uniqueness) -> sink (cap = groundCap /
        // obsCap). Returns the flow used per class, plus WHICH actors (by ArmyId) and WHICH job
        // indices carried flow, so a caller running a follow-up phase can exclude them.
        private static (int GroundUsed, int ObsUsed, HashSet<int> UsedActorIds, HashSet<int> UsedGroundJobIndex,
            HashSet<int> UsedObsJobIndex) SolveReconFlow(AiTurnContext ctx, PlayerSetupData player, WorldSnapshot snap,
            IReadOnlyList<ArmySnapshot> actors, IReadOnlyList<ReconObjective> groundJobs,
            IReadOnlyList<ReconObjective> obsJobs, int groundCap, int obsCap)
        {
            var usedActorIds = new HashSet<int>();
            var usedGroundIdx = new HashSet<int>();
            var usedObsIdx = new HashSet<int>();
            int groundJobCount = groundJobs?.Count ?? 0;
            int obsJobCount = obsJobs?.Count ?? 0;
            groundCap = Mathf.Max(0, groundCap);
            obsCap = Mathf.Max(0, obsCap);
            if (actors == null || actors.Count == 0 || (groundCap <= 0 && obsCap <= 0)
                || (groundJobCount == 0 && obsJobCount == 0))
                return (0, 0, usedActorIds, usedGroundIdx, usedObsIdx);

            int actorBase = 1;
            int groundJobBase = actorBase + actors.Count;
            int obsJobBase = groundJobBase + groundJobCount;
            int groundAgg = obsJobBase + obsJobCount;
            int obsAgg = groundAgg + 1;
            int sink = obsAgg + 1;
            int nodeCount = sink + 1;
            var graph = new List<FlowEdge>[nodeCount];
            for (int n = 0; n < nodeCount; n++) graph[n] = new List<FlowEdge>();

            for (int i = 0; i < actors.Count; i++)
                AddFlowEdge(graph, 0, actorBase + i, 1);
            for (int j = 0; j < groundJobCount; j++)
                AddFlowEdge(graph, groundJobBase + j, groundAgg, 1);
            for (int j = 0; j < obsJobCount; j++)
                AddFlowEdge(graph, obsJobBase + j, obsAgg, 1);
            if (groundCap > 0)
                AddFlowEdge(graph, groundAgg, sink, groundCap);
            if (obsCap > 0)
                AddFlowEdge(graph, obsAgg, sink, obsCap);
            for (int i = 0; i < actors.Count; i++)
            {
                ArmySnapshot a = actors[i];
                if (groundCap > 0)
                    for (int j = 0; j < groundJobCount; j++)
                        if (CanExecute(ctx, player, snap, a, groundJobs[j].ToTarget()))
                            AddFlowEdge(graph, actorBase + i, groundJobBase + j, 1);
                if (obsCap > 0)
                    for (int j = 0; j < obsJobCount; j++)
                        if (CanExecute(ctx, player, snap, a, obsJobs[j].ToTarget()))
                            AddFlowEdge(graph, actorBase + i, obsJobBase + j, 1);
            }

            MaxFlow(graph, 0, sink);

            for (int i = 0; i < actors.Count; i++)
                if (UsedCapacity(graph, 0, actorBase + i) > 0)
                    usedActorIds.Add(actors[i].ArmyId);
            for (int j = 0; j < groundJobCount; j++)
                if (UsedCapacity(graph, groundJobBase + j, groundAgg) > 0)
                    usedGroundIdx.Add(j);
            for (int j = 0; j < obsJobCount; j++)
                if (UsedCapacity(graph, obsJobBase + j, obsAgg) > 0)
                    usedObsIdx.Add(j);

            int groundUsed = groundCap > 0 ? UsedCapacity(graph, groundAgg, sink) : 0;
            int obsUsed = obsCap > 0 ? UsedCapacity(graph, obsAgg, sink) : 0;
            return (groundUsed, obsUsed, usedActorIds, usedGroundIdx, usedObsIdx);
        }

        private sealed class FlowEdge
        {
            public int To;
            public int Capacity;
            public int Reverse;
        }

        private static void AddFlowEdge(List<FlowEdge>[] graph, int from, int to, int capacity)
        {
            graph[from].Add(new FlowEdge { To = to, Capacity = capacity, Reverse = graph[to].Count });
            graph[to].Add(new FlowEdge { To = from, Capacity = 0, Reverse = graph[from].Count - 1 });
        }

        private static int UsedCapacity(List<FlowEdge>[] graph, int from, int to)
        {
            foreach (FlowEdge e in graph[from])
                if (e.To == to)
                    return graph[to][e.Reverse].Capacity;
            return 0;
        }

        private static int MaxFlow(List<FlowEdge>[] graph, int source, int sink)
        {
            int flow = 0;
            int n = graph.Length;
            while (true)
            {
                var parentNode = new int[n];
                var parentEdge = new int[n];
                for (int i = 0; i < n; i++) { parentNode[i] = -1; parentEdge[i] = -1; }
                parentNode[source] = source;
                var queue = new Queue<int>();
                queue.Enqueue(source);
                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    if (u == sink) break;
                    for (int e = 0; e < graph[u].Count; e++)
                    {
                        FlowEdge edge = graph[u][e];
                        if (edge.Capacity > 0 && parentNode[edge.To] < 0)
                        {
                            parentNode[edge.To] = u;
                            parentEdge[edge.To] = e;
                            queue.Enqueue(edge.To);
                        }
                    }
                }
                if (parentNode[sink] < 0)
                    break;

                int aug = int.MaxValue;
                for (int v = sink; v != source; v = parentNode[v])
                    aug = Mathf.Min(aug, graph[parentNode[v]][parentEdge[v]].Capacity);
                for (int v = sink; v != source; v = parentNode[v])
                {
                    FlowEdge edge = graph[parentNode[v]][parentEdge[v]];
                    edge.Capacity -= aug;
                    graph[v][edge.Reverse].Capacity += aug;
                }
                flow += aug;
            }
            return flow;
        }
    }
}
