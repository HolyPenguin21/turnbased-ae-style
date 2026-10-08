using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI STRATEGY V2 — TURN PIPELINE
    // ===========================================================================================
    //  The single production path of the AI turn. The normative ownership map (folders, dependency
    //  direction, canonical seams, mid-turn loop contract) is Assets/Scripts/Ai/V2/ARCHITECTURE.md;
    //  this file only ORDERS the stages and never scores, prices or decides eligibility itself.
    //
    //  INVARIANTS THE ORDERING PROTECTS
    //  --------------------------------------------------------------------------------------------
    //  · One shared WorldSnapshot per cycle (WorldAnalysis). Downstream stages read only it.
    //  · Radar: raw per-axis desires (response curves) normalised ONCE to sum == 1. It scales
    //    objective VALUE (EffectiveValue), never slices AP. Axes: Recon, Economy, Aggression,
    //    Development; ActiveDefence and Attack live inside Aggression, there is no Management axis.
    //  · Card play is a service: Phase A (StrategicPhaseA) fulfils AxisDemand capability gaps from
    //    the live AP pool (PhaseAApBudget); Phase B (UseSurplus) is the bounded end-of-turn tempo
    //    arbiter over genuinely remaining AP/resources; Housekeeping is the zero-AP reorg pass.
    //  · Mission<->axis is many-to-many: a MissionProposal carries an AxisContribution vector.
    //  · Re-allocate on provisioning failure is a HARD-BOUNDED loop (iteration cap, per-mission
    //    rejected set, cooldown).
    //  · One estimator, two stages: mission requirements and provisioning feasibility call the
    //    same estimator (WorthIt / AiPower), so the allocator never approves what provisioning
    //    cannot deliver.
    //  · Commitment is first-class: durable MissionIntents and funded commitments survive radar
    //    noise; retarget hysteresis applies (Continuity).
    //  · Provisioning is ATOMIC: one mission at a time in priority order, all-or-nothing claim,
    //    no partial-commit state between its entry and exit.
    // ===========================================================================================

    // --- Stage 3a/3b types (DesireAxis / DesireAxes / DesireVector / Radar / AxisContribution)
    //     live in DesireModels.cs; Stage 4's target types (MissionKind / EconomyTaskKind /
    //     EconomyMissionTarget / DevelopmentMissionTarget / ScoutTargetKind / StealthRequirement /
    //     ScoutMissionTarget) in MissionTargetModels.cs; Stage 4's output + Stage 7's Commitment
    //     (MissionProposal / MissionRequirements / Commitment) in MissionProposal.cs. All three
    //     are still this same stage map, split out for navigability only (round-2 file split).

    // --- Stage 5 types (BudgetSlice / FundingStage / FundedEntry / DeferReason / DeferredEntry /
    //     TentativeAllocation / StableMissionKey / ResourceVector / ProvisionFailureKind /
    //     AiAllocatorState / AllocationSession) live in ResourceAllocator.cs — the whole stage
    //     grew out of a stub into its own file (build-order step 5).

    // --- Stage 6 output lives in Provisioning/: ProvisionedMission.cs, ProvisioningResult.cs
    //     (ProvisionFailure + ProvisioningResult) and ProvisioningSession.cs, with the lane
    //     provisioners beside them. ProvisionFailureKind / ProvisionDisposition live in
    //     ResourceAllocator.cs beside the AllocationSession that consumes them. ExecutionResult /
    //     ExecutionStopReason live in TaskExecutor.cs.

    // ===========================================================================================
    //  THE PIPELINE — walking skeleton. Every stage below is a stub that returns empty/neutral and
    //  mutates NOTHING. Toggling AiConfig.aiStrategyV2Enabled on right now yields an AI that logs
    //  one full pipeline pass and then passes its turn. That is the intended build-order step 1
    //  end state: full loop runs, zero tasks, no throw.
    // ===========================================================================================
    public static partial class Pipeline
    {
        internal static bool StrategicAdmissionNeeded(
            IReadOnlyDictionary<DesireAxis, string> lastHandled,
            DesireAxis axis, string fingerprint) => lastHandled == null
            || !lastHandled.TryGetValue(axis, out string previous)
            || previous != fingerprint;

        // T03 — one axis's demand family by stable consumer identity (consumer intent, capability,
        // pinned host, target hex) plus its amount: the re-admission log's old→new line.
        internal static string DemandIdentityDigest(IEnumerable<AxisDemand> demands, DesireAxis axis) =>
            string.Join(";", (demands ?? Enumerable.Empty<AxisDemand>())
                .Where(d => d != null && d.RequestingAxis == axis)
                .Select(d => $"{d.ConsumerIntentKey?.ToString() ?? "-"}:{d.Capability}"
                    + $":{d.AttackFistArmyId?.ToString() ?? "-"}"
                    + (d.AttackCoverageGap ? ":cov" : "")
                    + $":{(d.TargetHex.HasValue ? $"{d.TargetHex.Value.Q},{d.TargetHex.Value.R}" : "-")}"
                    + $"={d.DesiredAmount.ToString("0.#", CultureInfo.InvariantCulture)}")
                .OrderBy(x => x, System.StringComparer.Ordinal));

        internal static bool RefreshDevelopmentOpportunities(ISet<DesireAxis> dirtyAxes) =>
            dirtyAxes != null && dirtyAxes.Contains(DesireAxis.Development);

        public static IEnumerator RunTurn(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            AiDebugLog.Write($"[AI][V2] === {player?.Nickname} — Strategy V2 pipeline owns this turn "
                + $"(turn {ctx?.TurnNumber}) ===");

            if (player == null || root == null || ctx == null || ctx.Map == null)
            {
                AiDebugLog.Write("[AI][V2] missing player/root/ctx/map — nothing to do.");
                yield break;
            }

            // Correlation scope for this whole main pass (T{turn}-P{colorIndex}-M) + the physical
            // resource totals it opens with, for the end-of-Main [STATE] control line (spec §2.7).
            V2TraceScope trace = AiV2Trace.BeginMain(player, ctx.TurnNumber);
            V2ResourceStamp stateStart = AiV2Trace.Stamp(root);

            // Coroutine disposal/exception also closes all session-owned turn state.
            using var turnSession = AiTurnSession.Begin(player, root, hand, ctx);

            // Initiative AP telemetry — captured now (turn start) and written back at turn end.
            // Belongs EXCLUSIVELY to Game.Ai.V2.Initiative analysis; nothing else in this pipeline
            // reads it (see InitiativeAnalyticsHistory).
            int initiativeStartAp = root.ActionPoints;
            int initiativeBaseAp = root.LastApFromInitiative;
            int initiativeActionableAtStart =
                Game.Ai.V2.Initiative.PreTurnCapacityAnalysis.CountActionableFieldArmies(player, unactivatedOnly: false);

            // 1b. A thin hand is refilled BEFORE the scan, so the drawn cards are in this turn's
            //     demand fulfilment instead of waiting for leftover AP in Phase B.
            int replenishDrawn = HandReplenishPolicy.Run(player, root, hand, ctx);

            // 2. One shared scan.
            WorldSnapshot snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
            // The first Analyze (desires, Aggression facts) is ~50 ms of Monte Carlo: fill the exact
            // estimate cache across frames first, so Analyze below only reads it.
            yield return CombatOpportunityAnalyzer.WarmEstimates(snapshot);
            // P_start: the first scanned force ceiling is this player's baseline for the game.
            ForceBaselineRegistry.RecordStart(player, snapshot.Self.TotalMilitaryPotential);
            AiFrameLog.GameState(snapshot, hand);
            AiFrameLog.WorldAnalysis(snapshot);
            ApBudgetTelemetry.Begin(player, ctx.TurnNumber, initiativeStartAp, snapshot);
            // The first Attack preparation step's AP hold reads this turn's mobilization gate.
            bool mobilizationOpen = AttackForceReadiness.MobilizationOpen(snapshot.Self);
            OperationContinuationWindow.SetMobilizationOpen(player, ctx.TurnNumber, mobilizationOpen);
            if (mobilizationOpen)
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {player.Nickname}: gate open, Phase A plays around "
                    + "the AP of the next preparation step (strike-force cards excepted)");

            // 3. Strategy: independent raw desires -> normalize once -> radar. StrategyLayer writes
            //    its own detailed "[AI][V2]   desires — ..." trace; the line below is the summary.
            AiRadarState radarState = AiRadarStateRegistry.GetOrCreate(player);
            RadarAssessment assessment = StrategyLayer.Evaluate(snapshot, radarState);
            DesireVector desires = assessment.Desires;
            Radar radar = assessment.Radar;
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: radar — {radar.DebugLine()} "
                + $"| threat {desires.MilitaryThreat.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"runway {desires.EconomicRunway.ToString("0.00", CultureInfo.InvariantCulture)}");
            AiFrameLog.Strategy(assessment);

            // 3c. The ONE Recon-opportunity enumeration for the turn — shared by DemandLayer and
            //     ReconMissionPlanner. FROZEN here (before StrategicManager touches own forces): Strategic
            //     Manager changes which SCOUT can execute, never which objectives exist.
            List<ReconObjective> reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);

            // 3d. The ONE Aggression-opportunity enumeration for the turn — shared by DemandLayer
            //     and AggressionMissionLayer (build-order step 9).
            List<RaidObjective> aggressionObjectives = RaidObjectiveEvaluator.Enumerate(
                snapshot, assessment.Breakdown.OpportunityReport);
            // 3e. Development opportunities are NOT enumerated here: DemandLayer.Development calls
            //     DevelopmentOpportunityEvaluator.Enumerate against the settled state of each pass.

            foreach (RaidObjective ao in aggressionObjectives)
                AiDebugLog.Write($"[AI][V2]   aggObjective — {ao.ObjectiveId} @{ao.LastKnownHex.Q},{ao.LastKnownHex.R} "
                    + $"base {ao.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"readyWin {ao.ReadyWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"asmWin {ao.AssemblableWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"def {ao.DefenderCount} gate {(ao.GatePassed ? 1 : 0)}"
                    + $"{(ao.NeedsCombatPower ? " needsPower" : "")}{(ao.NeedsHero ? " needsHero" : "")}");
            AiFrameLog.Objectives(reconObjectives, aggressionObjectives);

            // 7a. Mission Continuity — resolve durable in-flight intents FIRST. This cleanly
            //     retires stale Raid intents
            //     before ActorCommitments or the allocator can protect them.
            List<MissionIntent> activeIntents = MissionContinuityLayer.ResolveActive(
                player, snapshot, reconObjectives, aggressionObjectives, ctx);
            // Normalized "which of my armies are already committed to an operation" view — so
            // DemandLayer / CapabilityInventory / ReusableArmySelector can tell an EXISTING scout
            // from an AVAILABLE one without knowing how continuity stores mover ownership.
            ActorCommitments actorCommitments = turnSession.RefreshActors(activeIntents, snapshot, reconObjectives);
            AiFrameLog.MissionContinuity(activeIntents, actorCommitments);
            AiFrameLog.Forces(snapshot, actorCommitments);

            // DemandLayer measures air capacity itself via ReconAssignmentPlanner.MeasureAirCapacity
            //     (the same canonical capacity owner ground uses), recomputed fresh every call — no
            //     cross-call registry.

            // S1. Demand Layer — capability SHORTAGES (no card selection). All real axes are live.
            var demandAxes = new HashSet<DesireAxis>(DesireAxes.All);
            List<AxisDemand> demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                reconObjectives, aggressionObjectives, activeIntents, actorCommitments, player, ctx, root,
                demandAxes);

            // S2. Phase A's AP budget reads the player's live AP; its only own state is the
            //     follow-up AP promised to capabilities it delivers. Radar scales objective value
            //     only. Every other owner's hold is TurnResourceBook's, applied at each chain guard.
            PhaseAApBudget apBudget = PhaseAApBudget.Create(root);
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: Phase A budget — {apBudget.DebugLine()}");

            // S3. Strategic Manager Phase A — demand-driven card play, before mission planning.
            //     The demand set can materialize only capability requested by a real axis.
            //     Aviation obligations come first (AviationObligations): while a wing must still
            //     return or rebase, Phase A is deferred and the loop below settles the wings; every
            //     axis is then admitted in one re-admission pass.
            int handAtStart = (hand?.Hand?.Count ?? 0) - replenishDrawn;
            var deferredAdmission = new DeferredStrategicAdmission();
            StrategicPhaseResult phaseA;
            if (AviationObligations.Pending(player, ctx))
            {
                phaseA = new StrategicPhaseResult();
                deferredAdmission.Defer(demandAxes);
                AiDebugLog.Write("[AI][V2] Phase A deferred — aviation obligations settle first");
            }
            else
            {
                phaseA = StrategicManager.FulfillDemands(snapshot, player, root, hand,
                    ctx, apBudget, demands, actorCommitments, activeIntents, reconObjectives,
                    radar: radar, deferFreshZeroRadar: true);
                ReservationInvariants.CheckBoundary(player, root, ctx, "phaseA");
            }
            phaseA.CardsDrawn += replenishDrawn;

            // S4. Analysis owns refresh granularity. The existing AiMapMemory revision decides
            //     whether honest knowledge/map facts changed; action kind is not used as a proxy.
            //     Radar remains fixed while operational Aggression facts refresh below.
            if (phaseA.StateChanged)
            {
                snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                    snapshot, player, root, hand, ctx);
                // Direct Economy construction can atomically turn the builder's existing intent
                // into ReturnBuilder (or resume a safe scout); re-reading the same continuity owner
                // here keeps stale pre-build actor claims from executing.
                yield return CombatOpportunityAnalyzer.WarmEstimates(snapshot);
                OperationalFrame phaseAFrame = RefreshOperationalFrame(turnSession, snapshot, assessment.Breakdown);
                reconObjectives = phaseAFrame.Recon;
                aggressionObjectives = phaseAFrame.Aggression;
                activeIntents = phaseAFrame.Intents;
                actorCommitments = phaseAFrame.Commitments;
                // Phase A changed the settled facts behind the initial demand frame. Refresh that
                // frame once here; the first operational admission consumes it without another
                // full Generate call.
                // This call regenerates every axis, so DemandLayer.Development
                // builds its own opportunities against this pass's complete need context.
                demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                    reconObjectives, aggressionObjectives, activeIntents, actorCommitments,
                    player, ctx, root, demandAxes);
            }
            // Phase A is fully reflected in the settled snapshot/continuity view before pause.
            yield return ctx.WaitAtObserverActionBoundary();

            // Combat support preparation for existing ground operations. AirSweep formation
            // belongs to execution of its own admitted and funded Scout task.
            if (!AviationObligations.Pending(player, ctx))
            {
                AviationRebasePlan formation = AviationRebasePlanner.BuildFormationPlan(
                    snapshot, player, root, ctx, reconObjectives, activeIntents, actorCommitments);
                if (formation != null)
                {
                    bool formedWing = false;
                    yield return AviationRebasePlanner.Execute(player, root, ctx, formation,
                        changed => formedWing |= changed);
                    if (formedWing)
                    {
                        // The launch/flight actions already published their revision receipts.
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                        activeIntents = MissionContinuityLayer.ResolveActive(
                            player, snapshot, reconObjectives, aggressionObjectives);
                        actorCommitments = turnSession.RefreshActors(
                            activeIntents, snapshot, reconObjectives);
                    }
                    yield return ctx.WaitAtObserverActionBoundary();
                }
            }

            List<MissionProposal> missions;
            TentativeAllocation allocation = new TentativeAllocation();
            var fundedKeysThisTurn = new HashSet<StableMissionKey>();
            var provisioned = new List<ProvisionedMission>();
            var provisioningFailures = new Dictionary<ProvisionFailureKind, int>();
            var allExecuted = new List<ExecutionResult>();
            var phaseB = new StrategicPhaseResult();
            ActorCommitments postCommitments = null;
            bool phaseBHandled = false;

            // The typed mid-turn architecture is the canonical production path. The
            // initial Phase A settles before operational admission; a later factual Development
            // invalidation may re-enter that same manager through the shared ledger. Each Recon
            // admission still settles exactly one task command, through the same bounded
            // settle -> observe -> typed re-admission path.
            {
                missions = new List<MissionProposal>();
                int settledSteps = 0;
                int noProgressCycles = 0;
                var lastStrategicAdmissionFingerprint = new Dictionary<DesireAxis, string>();
                bool ownershipFreshAfterPhaseA = phaseA.StateChanged;
                bool zeroRadarResidualWindow = false;
                // LifecycleReturnPolicy: return legs wait until the first Phase B round.
                bool lifecycleReturnsReleased = false;
                bool lifecycleReturnsDeferred = false;

                string StrategicAdmissionFingerprint(DesireAxis axis)
                {
                    using var __profile = new Game.Core.ProfileScope("AI/Pipeline.AdmissionFingerprint");
                    string resources = root == null ? "-" : string.Join(",",
                        ResourceBundle.All.Select(t => root.GetResource(t).ToString("0.###",
                            CultureInfo.InvariantCulture)));
                    if (axis == DesireAxis.Development)
                        return DevelopmentAdmissionFingerprint(snapshot, activeIntents,
                            root?.ActionPoints ?? 0, resources, hand?.MutationVersion ?? -1, hand, player,
                            root, ctx);
                    // T03 — Aggression carries its own inputs (AiStrategyV2Pipeline.AggressionAdmission.cs).
                    if (axis == DesireAxis.Aggression)
                        return AggressionAdmissionFingerprint(snapshot, player);
                    // Economy only from here on (Development returned above). The key carries what
                    // Economy's decision reads and nothing that ticks on every executed step: no
                    // global state version, and position/movement/activation only for armies the
                    // economy analysis advertises as builders/collectors (or an Economy intent
                    // holds) — a scout stepping its waypoint cannot change a build decision. Every
                    // army still contributes identity, size and hero presence, so an army gaining a
                    // hero (a new builder candidate) or being formed/destroyed re-admits Economy.
                    HashSet<int> economyArmyIds = EconomyRelevantArmyIds(snapshot, activeIntents);
                    string armies = string.Join(";", (snapshot?.Self?.Armies
                            ?? System.Array.Empty<ArmySnapshot>())
                        .Where(a => a != null).OrderBy(a => a.ArmyId)
                        .Select(a => $"{a.ArmyId}:{a.MemberCount}:{(a.HasHero ? 1 : 0)}"
                            // A served facility's / selected site's operator duty decides builder admissibility.
                            + $":duty{(a.OperatorDutyBlocksDeparture ? 1 : 0)}"
                            + (economyArmyIds.Contains(a.ArmyId)
                                ? $":{a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:{a.ActivationApCost}"
                                : string.Empty)));
                    // Actor occupancy by ANY mission (a builder claimed by a raid is unavailable),
                    // by kind/status/claimed actor only — never intent identity, so a scout
                    // retargeting its waypoint keeps the same key.
                    string claims = string.Join(";", (activeIntents ?? new List<MissionIntent>())
                        .Where(i => i != null)
                        .Select(i => $"{i.Kind}:{i.Status}:{i.PreferredMoverArmyId}"
                            + $":{i.Raid?.AirSupportArmyId}"
                            // Held ground supports (convoys, gathers) — the same list
                            // ActorCommitments claims (GroundCombatLegs).
                            + $":sup{string.Join(",", GroundCombatLegs.HeldGroundSupportArmyIds(i))}")
                        .Distinct().OrderBy(x => x, System.StringComparer.Ordinal));
                    // The fingerprint's site facts are produced by the SAME
                    // WorldAnalysis.EconomyOpportunityRows the typed invalidation is derived from,
                    // so a "this known site became usable" event is never published and then
                    // suppressed here on an unchanged key: the trigger and the admission gate
                    // describe the same world.
                    string economyFacts = "|sites=" + string.Join(";",
                            WorldAnalysis.EconomyOpportunityRows(snapshot)
                            .OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
                            .Select(kv => $"{kv.Key}={kv.Value}"))
                        + "|bases=" + string.Join(";", (snapshot?.Economy?.BaseOpportunities
                            ?? System.Array.Empty<EconomyBaseOpportunity>())
                            .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                            .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.HexYield.Sum:0.###}"))
                        + "|threats=" + string.Join(";", (snapshot?.Known?.EnemySightings
                            ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                            .Concat(snapshot?.Known?.NeutralSightings
                                ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                            .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                            .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.SeenTurn}:{x.Defenders?.Count ?? 0}"))
                        + "|owners=" + string.Join(";", (activeIntents
                            ?? new List<MissionIntent>())
                            .Where(i => i?.Kind == MissionKind.Economy)
                            .OrderBy(i => i.IntentKey)
                            .Select(i => $"{i.IntentKey}:{i.Status}:{i.PreferredMoverArmyId}"));
                    // Raw AP stays: Economy's AP reads (chain sums, ledger-aware SpendableAp) have
                    // no small exact threshold set like Development's apfit, and AP only moves on
                    // an activation or a play, not on every step. Other lanes' holds shrink what
                    // Economy may spend, so they are input; Economy's own rows are its output and
                    // stay out (they would re-admit the axis on its own writes).
                    return $"axis={axis}|ap={root?.ActionPoints ?? 0}"
                        + $"|res={resources}|hand={hand?.MutationVersion ?? -1}"
                        + "|held=" + StrategicResourceReservationLedger.ReasonDigest(player,
                            ctx.TurnNumber, StrategicReservationReason.StrategicReactionPass)
                        + $"|armies={armies}|claims={claims}"
                        + economyFacts;
                }

                foreach (DesireAxis axis in demandAxes.Where(a =>
                             a == DesireAxis.Economy || a == DesireAxis.Development
                             || a == DesireAxis.Aggression))
                    lastStrategicAdmissionFingerprint[axis] =
                        StrategicAdmissionFingerprint(axis);

                // One factual flag may invalidate more than one family (for example, discovering
                // a deficient ResourceSite changes both Recon knowledge and Development
                // opportunity). Snapshot the aggregate once, derive every affected family, and
                // only then consume the shared reasons so family order cannot erase a sibling's
                // trigger.
                void TakeTypedTriggers(out StrategicInvalidationReason operationalReasons,
                    out StrategicInvalidationReason strategicReasons,
                    out HashSet<DesireAxis> dirtyStrategicAxes)
                {
                    StrategicInvalidation pending =
                        turnSession.PendingInvalidations;
                    // The OPERATIONAL mask is built from EVERY enabled mission axis, not only
                    // Recon: destroying a neutral publishes a Contact invalidation Aggression must
                    // consume, so the bounded loop gets a same-turn chance to refresh the objective
                    // list, complete the old target, select the next one, or start a Return
                    // mission. ActiveDefence is folded into Aggression, so this mask needs no extra
                    // case for it.
                    StrategicInvalidationReason operationalMask =
                        AiStrategyV2Scope.OperationalInvalidationMask;
                    operationalReasons = pending.Reasons & operationalMask;
                    strategicReasons = StrategicInvalidationReason.None;
                    dirtyStrategicAxes = new HashSet<DesireAxis>();
                    // T03 — Aggression's shortages (Raid/Attack reinforcement, the Attack preparation
                    // host, ActiveDefence) re-enter on its existing mask like Economy/Development:
                    // otherwise fresh objectives and admission run beside a stale capability
                    // request for the rest of the turn. Its fingerprint drops re-entries whose
                    // inputs did not change (a scout's step, a walk that moved no demand input).
                    foreach (DesireAxis axis in new[]
                             {
                                 DesireAxis.Economy, DesireAxis.Development, DesireAxis.Aggression,
                             })
                    {
                        StrategicInvalidationReason axisReasons = pending.Reasons
                            & DesireAxes.InvalidationMaskFor(axis);
                        // Actor movement alone must not re-run every Economy infrastructure
                        // candidate. It becomes actionable only when continuity's committed
                        // builder actually reached its build hex; factual resource/site changes
                        // still re-admit Economy normally.
                        if (axis == DesireAxis.Economy
                            && axisReasons == StrategicInvalidationReason.Actor
                            && !MissionContinuityLayer.EconomyBuilderReadyForCompletion(activeIntents, snapshot))
                            continue;
                        if (axisReasons == StrategicInvalidationReason.None)
                            continue;
                        dirtyStrategicAxes.Add(axis);
                        strategicReasons |= axisReasons;
                    }
                    turnSession.ConsumeInvalidations(operationalReasons | strategicReasons);
                }

                // Typed strategic re-admission uses the existing Phase-A owner, shared AP ledger and
                // carried reservation. This is deliberately local orchestration, not a second
                // manager or a new vertical layer.
                // Aviation obligations first: while a wing must still return or rebase, the axes
                // wait in `deferredAdmission`. The first call after the last obligation settles
                // admits them together with its own. `flush` admits waiting axes with no new
                // trigger once nothing is pending (loop top); `force` admits them even while an
                // obligation is still pending (the loop is over and will not settle it).
                bool reentryStateChanged = false;
                IEnumerator ReenterStrategicAxes(StrategicInvalidationReason reasons,
                    HashSet<DesireAxis> dirtyAxes, bool flush = false, bool force = false)
                {
                    reentryStateChanged = false;
                    bool triggered = reasons != StrategicInvalidationReason.None
                        && dirtyAxes != null && dirtyAxes.Count > 0;
                    if (!triggered && !((flush || force) && deferredAdmission.HasAxes))
                        yield break;
                    if (AviationObligations.Pending(player, ctx))
                    {
                        if (!force)
                        {
                            if (triggered)
                            {
                                deferredAdmission.Defer(dirtyAxes);
                                AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission deferred — aviation "
                                    + $"obligations pending; axes={string.Join(",", deferredAdmission.Axes)}");
                            }
                            yield break;
                        }
                        AiDebugLog.Write("[AI][V2][Loop] aviation obligations still pending after the "
                            + "loop — admitting the deferred axes anyway");
                    }
                    if (deferredAdmission.HasAxes)
                        dirtyAxes = deferredAdmission.TakeWith(triggered ? dirtyAxes : null);

                    dirtyAxes.RemoveWhere(axis =>
                    {
                        string fingerprint = StrategicAdmissionFingerprint(axis);
                        bool unchanged = !StrategicAdmissionNeeded(
                            lastStrategicAdmissionFingerprint, axis, fingerprint);
                        if (unchanged)
                            // Diagnostics only: admission still compares the full fingerprint.
                            // All axes can have large keys; print a digest and suppress repeats.
                            AiDebugLog.WriteDeduped($"admission-unchanged|{axis}",
                                $"[AI][V2][Loop] strategic re-admission skipped "
                                + $"axis={axis} reason=settled_state_unchanged fingerprint="
                                + $"#{(uint)fingerprint.GetHashCode():x8}/{fingerprint.Length}");
                        return unchanged;
                    });
                    if (dirtyAxes.Count == 0)
                        yield break;

                    yield return CombatOpportunityAnalyzer.WarmEstimates(snapshot);
                    OperationalFrame reenterFrame = RefreshOperationalFrame(turnSession, snapshot, assessment.Breakdown);
                    reconObjectives = reenterFrame.Recon;
                    aggressionObjectives = reenterFrame.Aggression;
                    activeIntents = reenterFrame.Intents;
                    actorCommitments = reenterFrame.Commitments;
                    // T03 — the baseline is the input Generate actually evaluates: taken after
                    // continuity resolved (a completed target, a handed-off donor), before any
                    // follow-up delivery. Post-delivery state is judged by the delta it publishes,
                    // never pre-declared as already considered.
                    Dictionary<DesireAxis, string> admittedFingerprints = dirtyAxes
                        .ToDictionary(axis => axis, StrategicAdmissionFingerprint);
                    Dictionary<DesireAxis, string> demandsBefore = dirtyAxes.ToDictionary(axis => axis,
                        axis => DemandIdentityDigest(demands, axis));
                    List<AxisDemand> regenerated = DemandLayer.Generate(snapshot, assessment.Breakdown,
                        reconObjectives, aggressionObjectives, activeIntents,
                        actorCommitments, player, ctx, root, dirtyAxes);
                    List<AxisDemand> dirtyDemands = regenerated;
                    demands = demands.Where(d => d != null
                            && !dirtyAxes.Contains(d.RequestingAxis))
                        .Concat(regenerated).ToList();
                    // Economy deferred-hold reconciliation now lives entirely inside
                    // StrategicPhaseA (economyAxisAuthoritative) — a single canonical writer
                    // instead of this call duplicating the same existence check right before it.
                    // dirtyAxes.Contains(Economy) is the exact "was Economy actually re-evaluated
                    // this round" signal Phase A needs to tell "Economy resolved" apart from
                    // "Economy wasn't part of this dirty-axis subset".
                    WorldAnalysis.StepObservationStamp beforeCapabilities =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    StrategicPhaseResult followup = StrategicManager.FulfillDemands(
                        snapshot, player, root, hand, ctx, apBudget, dirtyDemands,
                        actorCommitments, activeIntents, reconObjectives,
                        phaseB.Reservation ?? phaseA.Reservation,
                        economyAxisAuthoritative: dirtyAxes.Contains(DesireAxis.Economy), radar: radar,
                        deferFreshZeroRadar: true);
                    phaseA.Accumulate(followup);
                    ReservationInvariants.CheckBoundary(player, root, ctx,
                        $"phaseA reentry axes={string.Join(",", dirtyAxes)}");
                    if (followup.StateChanged)
                    {
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        yield return CombatOpportunityAnalyzer.WarmEstimates(snapshot);
                        OperationalFrame followupFrame = RefreshOperationalFrame(turnSession, snapshot, assessment.Breakdown);
                        reconObjectives = followupFrame.Recon;
                        aggressionObjectives = followupFrame.Aggression;
                        activeIntents = followupFrame.Intents;
                        actorCommitments = followupFrame.Commitments;
                        ownershipFreshAfterPhaseA = true;
                    }
                    WorldAnalysis.StepObservationStamp afterCapabilities =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                        beforeCapabilities, afterCapabilities, null);
                    foreach (DesireAxis axis in dirtyAxes)
                        lastStrategicAdmissionFingerprint[axis] =
                            admittedFingerprints[axis];
                    AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission "
                        + $"axes={string.Join(",", dirtyAxes)} triggers={reasons} "
                        + $"changed={(followup.StateChanged ? 1 : 0)}");
                    foreach (DesireAxis axis in dirtyAxes)
                    {
                        string after = DemandIdentityDigest(regenerated, axis);
                        AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission demands axis={axis} "
                            + (after == demandsBefore[axis]
                                ? $"unchanged count={regenerated.Count(d => d?.RequestingAxis == axis)}"
                                : $"old=[{demandsBefore[axis]}] new=[{after}]"));
                    }
                    reentryStateChanged = followup.StateChanged;
                }

                IEnumerator RunTypedAdmissions()
                {
                    zeroRadarResidualWindow = false;
                    AiDebugLog.Write("[AI][V2][Loop] begin — typed operational admission");

                    // Scout jobs rejected with ProvisionDisposition.RetryNextTurn ("out of the
                    // running THIS turn" — ResourceAllocator.cs:172, covers MoverContended AND
                    // NoExecutableStep alike) carry that verdict, but nothing enforced it across
                    // settled steps: BuildMissionSet re-proposed the same losing job every
                    // micro-step, re-running the full batch solve only to reach the identical
                    // rejection again (same busy/unreachable movers, nothing changed). Originally
                    // this set only recorded MoverContended, so a NoExecutableStep rejection (a
                    // scout physically can't reach its target this turn) kept re-entering the
                    // batch solve every settled step for no reason — same churn, different kind.
                    // Recorded live as each RetryNextTurn failure is seen below (NOT by reading
                    // ProvisioningSession.AssignmentRejections after the step settles — a later
                    // intra-step realloc pass drops an already-rejected mission out of Funded
                    // entirely, and ProvisioningSession.SetAssignment clears+refills that dict on
                    // every pass, so by settle time it only ever held the last pass's leftovers,
                    // almost always empty). Consumed only at the NEXT settled step's BuildMissionSet
                    // filter below, so this step's own remaining realloc passes still see the full
                    // candidate set — the existing intra-step "chance within the batch" is untouched.
                    var retryNextTurnThisPass = new HashSet<StableMissionKey>();

                    // Perf: an AI turn can run dozens of settled steps back-to-back with no other
                    // yield in between (each step's own yields resolve synchronously — see the
                    // profiler), so the whole turn could land in one single-frame hitch (observed
                    // ~885ms / 15 FPS). Give a real frame back to the
                    // engine whenever the wall-clock budget since the last frame is exceeded, so the
                    // same total work is spread across several frames instead of freezing one.
                    // Total AI-turn wall-clock time goes UP by roughly one frame per yield — a
                    // deliberate tradeoff: smoother frame pacing over shorter total wait.
                    const float yieldBudgetSeconds = 0.008f;
                    float lastYieldTime = UnityEngine.Time.realtimeSinceStartup;

                    while (settledSteps < AiConfigV2.maxMidTurnStepsPerTurn
                        && noProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles)
                    {
                    if (UnityEngine.Time.realtimeSinceStartup - lastYieldTime >= yieldBudgetSeconds)
                    {
                        yield return null;
                        lastYieldTime = UnityEngine.Time.realtimeSinceStartup;
                    }
                    // The last aviation obligation may have settled (or stalled) without a typed
                    // trigger: admit the axes that waited for it before this admission.
                    yield return ReenterStrategicAxes(StrategicInvalidationReason.None, null, flush: true);
                    // Every admission reads a settled world. Strategic observations are refreshed
                    // here. The radar frame stays stable for this turn; typed Development facts
                    // re-enter the existing manager immediately after the settled task boundary.
                    if (!ownershipFreshAfterPhaseA)
                    {
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(snapshot, player, root, hand, ctx);
                        yield return CombatOpportunityAnalyzer.WarmEstimates(snapshot);
                        OperationalFrame settledFrame = RefreshOperationalFrame(turnSession, snapshot, assessment.Breakdown);
                        reconObjectives = settledFrame.Recon;
                        aggressionObjectives = settledFrame.Aggression;
                        activeIntents = settledFrame.Intents;
                        actorCommitments = settledFrame.Commitments;
                    }
                    ownershipFreshAfterPhaseA = false;
                    // Demand families persist across settled admissions. Only
                    // ReenterStrategicAxes replaces dirty families after a factual invalidation.

                    Dictionary<MissionIntentKey, string> missionDeferrals;
                    missions = BuildMissionSet(snapshot, assessment.Breakdown, activeIntents,
                        reconObjectives, aggressionObjectives, radar, demands, trace, ctx,
                        out missionDeferrals, aggressionPressureAlreadyRefreshed: true);
                    if (!lifecycleReturnsReleased && !LifecycleReturnPolicy.HomeThreatened(snapshot))
                    {
                        var waiting = missions.Where(m => LifecycleReturnPolicy.IsDeferrableReturn(
                                m, activeIntents)
                            && LifecycleReturnPolicy.MayWait(player, MissionIntentKey.For(m), ctx.TurnNumber))
                            .ToList();
                        if (waiting.Count > 0)
                        {
                            lifecycleReturnsDeferred = true;
                            foreach (MissionProposal m in waiting)
                            {
                                MissionIntentKey waitKey = MissionIntentKey.For(m);
                                missionDeferrals[waitKey] = LifecycleReturnPolicy.DeferralReason;
                                LifecycleReturnPolicy.RecordWait(player, waitKey, ctx.TurnNumber);
                                // A deliberate wait is not a stall (ReconcileAfterTurn).
                                MissionContinuityLayer.MarkProtectedThisTurn(player, waitKey, ctx.TurnNumber);
                            }
                            missions = missions.Except(waiting).ToList();
                            AiDebugLog.WriteDeduped($"returns-wait#{player.ColorIndex}#{ctx.TurnNumber}",
                                $"[AI][V2][Loop] lifecycle returns wait for the tempo pass (no home threat): "
                                + string.Join(", ", waiting.Select(m => StableMissionKey.For(m).ToString())));
                        }
                    }
                    if (missions.Count > 0)
                    {
                        var retained = new List<MissionProposal>(missions.Count);
                        foreach (MissionProposal mission in missions)
                        {
                            // The set is per admission; the registry carries the same verdict to a
                            // later admission of this turn while it still provably holds.
                            if (mission != null
                                && (retryNextTurnThisPass.Contains(StableMissionKey.For(mission))
                                    || CapabilityPoolExhaustionRegistry.ShouldSkipRetried(
                                        player, mission, snapshot)))
                            {
                                // A durable leg at tier None (a Raid's fresh-decision return
                                // fallback) still belongs to an intent that is funded: dropping
                                // it here without a reason made BindFunding warn.
                                if (mission.FromDurableIntent)
                                    missionDeferrals[MissionIntentKey.For(mission)] =
                                        "retry_next_turn_after_provision_failure";
                                continue;
                            }
                            retained.Add(mission);
                        }
                        missions = retained;
                    }
                    List<Commitment> cycleCommitments =
                        MissionContinuityLayer.BindFunding(activeIntents, missions, snapshot,
                            missionDeferrals);
                    var cycleLedger = new MissionOutcomeLedger();
                    cycleLedger.RegisterProposals(missions);
                    cycleLedger.RegisterCommitments(cycleCommitments);

                    AllocationSession cycleSession = ResourceAllocator.BeginTurn(snapshot, radar,
                        missions, cycleCommitments, player);
                    using var cycleProvisioning = new ProvisioningSession(snapshot, turnSession);
                    allocation = cycleSession.Pack();
                    foreach (FundedEntry fe in allocation.Funded)
                        if (fe?.Mission != null)
                            fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));

                    // A multi-turn rebase is already airborne and committed to landing. Resume
                    // one such obligation before discretionary mission progress; no card was played
                    // yet while it was pending (AviationObligations), so its activation resources
                    // are still there. Route safety and destination validity are live-rechecked
                    // inside ExecuteContinuation rather than trusting last turn's projection.
                    List<ArmyData> rebaseContinuations =
                        AviationRebasePlanner.FindMandatoryContinuations(player, ctx.TurnNumber);
                    List<ArmyData> recoveries =
                        ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx);
                    bool rebaseFirst = rebaseContinuations.Count > 0
                        && (recoveries.Count == 0
                            || rebaseContinuations[0].Id <= recoveries[0].Id);
                    if (rebaseFirst)
                    {
                        ArmyData rebaseWing = rebaseContinuations[0];
                        WorldAnalysis.StepObservationStamp beforeRebase =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        bool rebaseMoved = false;
                        yield return AviationRebasePlanner.ExecuteContinuation(
                            player, root, ctx, rebaseWing, v => rebaseMoved = v);
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        WorldAnalysis.StepObservationStamp afterRebase =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                            beforeRebase, afterRebase, null);
                        settledSteps++;
                        ReservationInvariants.CheckBoundary(player, root, ctx,
                            $"step {settledSteps} aviation-rebase #{rebaseWing.Id}");
                        TakeTypedTriggers(out StrategicInvalidationReason rebaseOperationalReasons,
                            out StrategicInvalidationReason rebaseStrategicReasons,
                            out HashSet<DesireAxis> rebaseDirtyAxes);
                        yield return ReenterStrategicAxes(
                            rebaseStrategicReasons, rebaseDirtyAxes);
                        bool rebaseStrategicChanged = reentryStateChanged;
                        bool rebaseProgress = rebaseMoved || rebaseStrategicChanged;
                        noProgressCycles = rebaseProgress ? 0 : noProgressCycles + 1;
                        AiDebugLog.Write($"[AI][V2][Loop] step={settledSteps} aviation-rebase "
                            + $"actor=#{rebaseWing.Id} progress={(rebaseProgress ? 1 : 0)} "
                            + $"operationalTriggers={rebaseOperationalReasons} "
                            + $"strategicTriggers={rebaseStrategicReasons}");
                        if (!rebaseProgress)
                        {
                            // Recon audit B1 — the obligation is skipped for the rest of this turn;
                            // it must not stop every mission's admission with it.
                            AviationObligationStallRegistry.MarkStalled(player, ctx.TurnNumber, rebaseWing.Id);
                            AiDebugLog.Write($"[AI][V2][Loop] aviation rebase #{rebaseWing.Id} could not take "
                                + "a safe step — deferred to next turn, missions continue");
                        }
                        continue;
                    }

                    // Lifecycle safety is admitted before strategic progress, but its must-return
                    // predicate remains owned by ReconAirExecutor. Exactly one airborne action is
                    // settled, observed and then re-admitted like every other step.
                    if (recoveries.Count > 0)
                    {
                        ArmyData recovery = recoveries[0];
                        HexCoord? recoveryFocus =
                            ReconPatrolStateRegistry.TryGet(player, recovery.Id, out ReconPatrolState recoveryState)
                                ? recoveryState.StrategicAnchor : (HexCoord?)null;
                        WorldAnalysis.StepObservationStamp beforeRecovery =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        var recoveryResult = new AirReconExecutionResult();
                        var recoveryControl = new ReconAirExecutor.ActorStepControl();
                        int recoveryApBefore = root.ActionPoints;
                        yield return ReconAirExecutor.RunActorStep(player, root, ctx, snapshot,
                            recovery, recoveryResult, recoveryApBefore, recoveryFocus,
                            perMissionResult: null, control: recoveryControl);
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        WorldAnalysis.StepObservationStamp afterRecovery =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                            beforeRecovery, afterRecovery, null);
                        settledSteps++;
                        ReservationInvariants.CheckBoundary(player, root, ctx,
                            $"step {settledSteps} recovery #{recovery.Id}");
                        bool recoveryProgress = recoveryResult.Mutated;
                        TakeTypedTriggers(out StrategicInvalidationReason recoveryOperationalReasons,
                            out StrategicInvalidationReason recoveryStrategicReasons,
                            out HashSet<DesireAxis> recoveryDirtyAxes);
                        yield return ReenterStrategicAxes(
                            recoveryStrategicReasons, recoveryDirtyAxes);
                        bool recoveryStrategicChanged = reentryStateChanged;
                        // Reentry may publish another compound fact (for example, materializing a
                        // Raid reinforcement changes Actor + Capability). Route that fact through
                        // the same typed fan-out before consuming it so Economy/Development cannot
                        // lose their share to an operational-axis follow-up.
                        TakeTypedTriggers(
                            out StrategicInvalidationReason recoveryFollowupOperational,
                            out StrategicInvalidationReason recoveryFollowupStrategic,
                            out HashSet<DesireAxis> recoveryFollowupAxes);
                        recoveryOperationalReasons |= recoveryFollowupOperational;
                        recoveryStrategicReasons |= recoveryFollowupStrategic;
                        yield return ReenterStrategicAxes(
                            recoveryFollowupStrategic, recoveryFollowupAxes);
                        recoveryStrategicChanged |= reentryStateChanged;
                        recoveryProgress |= recoveryStrategicChanged;
                        noProgressCycles = recoveryProgress ? 0 : noProgressCycles + 1;
                        AiDebugLog.Write($"[AI][V2][Loop] step={settledSteps} recovery actor=#{recovery.Id} "
                            + $"progress={(recoveryProgress ? 1 : 0)} "
                            + $"operationalTriggers={recoveryOperationalReasons} "
                            + $"strategicTriggers={recoveryStrategicReasons}");
                        if (!recoveryProgress)
                        {
                            // Recon audit B1 — a recovery that changed nothing is skipped for the rest
                            // of this turn; it must not stop every mission's admission with it.
                            AviationObligationStallRegistry.MarkStalled(player, ctx.TurnNumber, recovery.Id);
                            AiDebugLog.Write($"[AI][V2][Loop] recovery #{recovery.Id} made no progress — "
                                + "deferred to next turn, missions continue");
                        }
                        continue;
                    }

                    if (allocation.Funded.Count == 0)
                    {
                        zeroRadarResidualWindow = true;
                        AiDebugLog.Write("[AI][V2][Loop] stop — no funded typed mission");
                        break;
                    }

                    ProvisionedMission selected = null;
                    bool selectedIsCommitment = false;
                    StableMissionKey selectedKey = default;
                    var attemptedKeys = new HashSet<StableMissionKey>();
                    // Two independent bounded budgets, not one shared counter: a Scout batch that
                    // keeps failing (assignmentReallocPass) must not be able to consume every
                    // realloc this cycle had, starving the single-mission repack
                    // (repriceReallocPass) that a mandatory Economy EnvelopeTooSmall/
                    // RepriceThisTurn depends on to ever see a corrected envelope this turn.
                    int assignmentReallocPass = 0;
                    int repriceReallocPass = 0;
                    bool provisioningSettled = false;
                    while (!provisioningSettled)
                    {
                        ProvisioningManager.PreparePass(player, root, ctx,
                            cycleProvisioning, allocation, actorCommitments);
                        IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)> scoutFailures =
                            ProvisioningManager.ScoutAssignmentFailures(cycleProvisioning, allocation);
                        if (scoutFailures.Count > 0)
                        {
                            foreach ((FundedEntry failedFunding, ProvisionFailure failure) in scoutFailures)
                            {
                                StableMissionKey failedKey = StableMissionKey.For(failedFunding.Mission);
                                attemptedKeys.Add(failedKey);
                                provisioningFailures.TryGetValue(failure.Kind, out int scoutFailureCount);
                                provisioningFailures[failure.Kind] = scoutFailureCount + 1;
                                CapabilityPoolExhaustionRegistry.DeferNoExecutableStep(
                                    player, failedFunding.Mission, failure);
                                cycleSession.RegisterProvisionFailure(failedFunding, failure);
                                cycleLedger.RecordProvisionFailure(failedFunding.Mission, failure);
                                // Record any RetryNextTurn failure here (during the pass, before the
                                // next realloc's repack can drop this mission out of Funded entirely
                                // and erase it from cycleProvisioning.AssignmentRejections) — reading
                                // the rejection dict only after the whole step settles was catching
                                // just the last realloc pass's leftovers, near-always empty by then.
                                if (failure.Disposition == ProvisionDisposition.RetryNextTurn)
                                {
                                    retryNextTurnThisPass.Add(failedKey);
                                    CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(
                                        player, failedFunding.Mission);
                                }
                                AiDebugLog.Write($"[AI][V2][Loop] assignment-batch "
                                    + $"[{AiV2Trace.FormatCorrelation(failedFunding.Mission)}] {failedKey} — FAIL "
                                    + $"{failure.Kind} [{failure.Disposition}] {failure.Detail}");
                            }

                            List<FundedEntry> openScouts = allocation.Funded.Where(fe =>
                                fe?.Mission?.Kind == MissionKind.Scout
                                && !cycleProvisioning.AlreadyProvisioned(
                                    StableMissionKey.For(fe.Mission))).ToList();
                            Dictionary<StableMissionKey, ProvisionFailure> scoutFailureByKey =
                                scoutFailures.ToDictionary(
                                    f => StableMissionKey.For(f.Funded.Mission), f => f.Failure);
                            CapabilityPoolExhaustionRegistry.SettleScoutBatch(snapshot, player,
                                openScouts.Select(fe => fe.Mission), scoutFailureByKey,
                                cycleProvisioning.Successful.Values.Select(m => m?.Mission));

                            // One batch means one re-pack. The allocator now sees every impossible
                            // Scout at once, so released AP can admit Economy/Development immediately.
                            if (cycleSession.HasNewFailures && !cycleSession.Converged
                                && assignmentReallocPass < AiConfigV2.maxReallocIterations)
                            {
                                assignmentReallocPass++;
                                allocation = cycleSession.Pack();
                                foreach (FundedEntry fe in allocation.Funded)
                                    if (fe?.Mission != null)
                                        fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));
                                continue;
                            }
                        }
                        FundedEntry selectedFunding = allocation.Funded.FirstOrDefault(fe =>
                            fe?.Mission != null
                            && CapabilityPoolExhaustionRegistry.CanAttempt(
                                player, fe.Mission, snapshot));
                        if (selectedFunding == null)
                            break;

                        selectedKey = StableMissionKey.For(selectedFunding.Mission);
                        attemptedKeys.Add(selectedKey);
                        ProvisioningResult provisionResult = ProvisioningManager.Provision(
                            player, root, hand, ctx, cycleProvisioning, selectedFunding);
                        if (provisionResult.Success)
                        {
                            selected = provisionResult.Provisioned;
                            selectedIsCommitment = selectedFunding.IsCommitment;
                            cycleProvisioning.RegisterSuccess(selectedKey, selected);
                            cycleSession.RegisterProvisionSuccess(selectedFunding,
                                selected.ClaimedAp, selected.ClaimedPhysical);
                            cycleLedger.RecordProvisionSuccess(selectedFunding.Mission, selected);
                            provisioned.Add(selected);
                            AiV2Trace.CheckProvisionEnvelope(selectedFunding.Mission.AttemptId,
                                selected.ClaimedAp, selectedFunding.Tentative.Ap);
                            provisioningSettled = true;
                            break;
                        }

                        provisioningFailures.TryGetValue(provisionResult.Failure.Kind,
                            out int failureCount);
                        provisioningFailures[provisionResult.Failure.Kind] = failureCount + 1;
                        CapabilityPoolExhaustionRegistry.RecordProvisionFailure(snapshot, player,
                            selectedFunding.Mission, provisionResult.Failure);
                        cycleSession.RegisterProvisionFailure(selectedFunding, provisionResult.Failure);
                        cycleLedger.RecordProvisionFailure(selectedFunding.Mission,
                            provisionResult.Failure);
                        if (provisionResult.Failure.Disposition == ProvisionDisposition.RetryNextTurn)
                        {
                            retryNextTurnThisPass.Add(selectedKey);
                            CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(
                                player, selectedFunding.Mission);
                        }
                        AiDebugLog.Write($"[AI][V2][Loop] provision [{AiV2Trace.FormatCorrelation(selectedFunding.Mission)}] "
                            + $"{selectedKey} — FAIL {provisionResult.Failure.Kind} "
                            + $"[{provisionResult.Failure.Disposition}] {provisionResult.Failure.Detail}");

                        // A non-repricing failure rejects this key; repack can consider other missions.
                        // Count only a retry of the SAME key with a repriced envelope.
                        if (!cycleSession.HasNewFailures || cycleSession.Converged
                            || (provisionResult.Failure.Disposition == ProvisionDisposition.RepriceThisTurn
                                && ++repriceReallocPass >= AiConfigV2.maxReallocIterations))
                        {
                            provisioningSettled = true;
                            break;
                        }
                        allocation = cycleSession.Pack();
                        foreach (FundedEntry fe in allocation.Funded)
                            if (fe?.Mission != null)
                                fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));
                    }

                    if (selected == null)
                    {
                        cycleLedger.RecordDeferrals(allocation.Deferred);
                        foreach (MissionStepResult outcome in cycleLedger.FinalizeSteps()
                                     .Where(o => o != null && attemptedKeys.Contains(o.AttemptKey)))
                            turnSession.Settle(outcome, snapshot, reconObjectives);
                        noProgressCycles++;
                        // A rejected positive or durable mission must not be mistaken for
                        // an exhausted portfolio; zero-only rejections leave a residual window.
                        zeroRadarResidualWindow = allocation.Funded.All(fe => fe != null
                            && !fe.IsCommitment && fe.Mission != null
                            && fe.Mission.EffectiveValue <= 0f);
                        AiDebugLog.Write($"[AI][V2][Loop] admission stopped — no provisioned task; "
                            + $"noProgress={noProgressCycles}");
                        // No task command ran and no observation can differ. Repeating the same
                        // admission under a fresh session only reproduces the same rejection; stop
                        // this family without consuming the real bounded task-step budget.
                        break;
                    }

                    WorldAnalysis.StepObservationStamp beforeStep =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    var stepResults = new List<ExecutionResult>();
                    if (selected.Kind == MissionKind.Scout
                        && selected.ExecutorKind != ScoutExecutorKind.Ground)
                    {
                        AirReconPlan plan = AirReconPlanner.Plan(player, root, ctx,
                            snapshot, new[] { selected });
                        var airStepResult = new AirReconExecutionResult();
                        yield return ReconAirExecutor.ExecutePlanStep(plan, player, root, ctx,
                            snapshot, airStepResult, stepResults);
                    }
                    else
                    {
                        yield return TaskExecutor.ExecuteStep(player, root, ctx,
                            selected, stepResults, snapshot, enforceFreshPlan: true);
                    }

                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    ExecutionResult settled = stepResults.FirstOrDefault();
                    WorldAnalysis.StepObservationStamp afterStep =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                        beforeStep, afterStep, settled);

                    foreach (ExecutionResult er in stepResults)
                    {
                        cycleLedger.RecordExecution(er);
                        allExecuted.Add(er);
                        ApBudgetTelemetry.RecordStep(player, ctx.TurnNumber, selected.Mission,
                            selectedIsCommitment, er.ApSpent);
                    }
                    cycleLedger.RecordDeferrals(allocation.Deferred);
                    cycleLedger.RefreshObjectiveStatesLive(player);
                    foreach (MissionStepResult outcome in cycleLedger.FinalizeSteps()
                                 .Where(o => o != null && attemptedKeys.Contains(o.AttemptKey)))
                        turnSession.Settle(outcome, snapshot, reconObjectives);
                    // A single atomic move may consume the last MP after Provisioning had
                    // legitimately reserved this owner's completion AP. Settle its stage now.
                    InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                        player, root, hand, ctx);

                    settledSteps++;
                    ReservationInvariants.CheckBoundary(player, root, ctx,
                        $"step {settledSteps} task={selectedKey}");
                    // Snapshot, mission ledger and reservation reconciliation now all describe
                    // the completed command; inspection never sees a half-settled action.
                    yield return ctx.WaitAtObserverActionBoundary();
                    bool progressed = stepResults.Any(er =>
                        er != null && er.Outcome.StateChanged);
                    TakeTypedTriggers(out StrategicInvalidationReason operationalReasons,
                        out StrategicInvalidationReason strategicReasons,
                        out HashSet<DesireAxis> dirtyStrategicAxes);
                    yield return ReenterStrategicAxes(
                        strategicReasons, dirtyStrategicAxes);
                    bool strategicChanged = reentryStateChanged;
                    // A follow-up Phase A action may publish a reason shared by operational and
                    // strategic families. Take one typed snapshot and acknowledge every affected
                    // recipient before the registry clears that reason.
                    TakeTypedTriggers(
                        out StrategicInvalidationReason followupOperationalReasons,
                        out StrategicInvalidationReason followupStrategicReasons,
                        out HashSet<DesireAxis> followupDirtyAxes);
                    operationalReasons |= followupOperationalReasons;
                    strategicReasons |= followupStrategicReasons;
                    yield return ReenterStrategicAxes(
                        followupStrategicReasons, followupDirtyAxes);
                    strategicChanged |= reentryStateChanged;
                    progressed |= strategicChanged;
                    noProgressCycles = progressed ? 0 : noProgressCycles + 1;
                    AiDebugLog.Write($"[AI][V2][Loop] step={settledSteps} task={selectedKey} "
                        + $"progress={(progressed ? 1 : 0)} stop={settled?.StopReason} "
                        + $"operationalTriggers={operationalReasons} strategicTriggers={strategicReasons} "
                        + $"noProgress={noProgressCycles}");
                    if (operationalReasons == StrategicInvalidationReason.None && !strategicChanged)
                    {
                        // Ignore the task that JUST executed: only unfinished positive
                        // allocations should prevent residual admission.
                        zeroRadarResidualWindow = allocation.Funded.All(fe => fe?.Mission != null
                            && (StableMissionKey.For(fe.Mission).Equals(selectedKey)
                                || (!fe.IsCommitment && fe.Mission.EffectiveValue <= 0f)));
                        AiDebugLog.Write("[AI][V2][Loop] stop — settled task produced no typed invalidation");
                        break;
                    }
                    }

                if (settledSteps >= AiConfigV2.maxMidTurnStepsPerTurn)
                    AiDebugLog.Write($"[AI][V2][Loop] bounded stop — max steps "
                        + $"{AiConfigV2.maxMidTurnStepsPerTurn}");
                if (noProgressCycles >= AiConfigV2.maxMidTurnNoProgressCycles)
                    AiDebugLog.Write($"[AI][V2][Loop] bounded stop — no progress cycles "
                        + $"{noProgressCycles}");
                // Axes still waiting for aviation must not be lost when the loop ends first.
                yield return ReenterStrategicAxes(StrategicInvalidationReason.None, null, force: true);

                }

                // A stand-alone Base level bought by the first Phase A opened a slot AFTER the admission
                // baselines above were taken: its Facility demand was generated on the refreshed world but
                // never judged, and an equal fingerprint would keep rejecting it as "settled". Forget the
                // Development baseline and admit it now, before missions or Phase B spend what is left.
                if (phaseA.CapacityUnlocks > 0 && demandAxes.Contains(DesireAxis.Development))
                {
                    lastStrategicAdmissionFingerprint.Remove(DesireAxis.Development);
                    yield return ReenterStrategicAxes(
                        StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Capability,
                        new HashSet<DesireAxis> { DesireAxis.Development });
                }
                yield return RunTypedAdmissions();
                // Also reconcile on bounded/no-progress exits where no additional typed
                // admission occurs: Phase B must see AP that no actor can spend on a build.
                InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                    player, root, hand, ctx);
                // Every build still deferred now cannot complete this turn: release the part of
                // its hold the next income tick covers, so Phase B may spend it.
                InfrastructureFulfillment.ReleaseDeferredEconomyIncomeCover(player, ctx);
                // Continuing Hard operations had their funding chance in the loop above; their
                // Phase-A protection ends here so Phase B sees every AP nobody will spend.
                OperationContinuationWindow.Settle(player, ctx.TurnNumber);

                // Management/Development is another bounded task family, not the owner of the
                // operational loop. Phase B settles until it either exhausts its candidates or
                // publishes a capability-changing residual. Typed Analysis deltas then re-admit
                // only the affected Development and/or Recon family, after which the same shared
                // per-turn tempo budget may resume.
                for (int managementRound = 0;
                     managementRound <= AiConfigV2.maxEndOfTurnTempoReruns;
                     managementRound++)
                {
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                    postCommitments = turnSession.RefreshActors(
                        turnSession.PersistentState.All, snapshot, reconObjectives);

                    WorldAnalysis.StepObservationStamp beforeManagement =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    // A prior Phase B action may have spent AP or removed a build card.
                    // Revalidate each owner's stronger completion claim before the next pass.
                    InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                        player, root, hand, ctx);
                    var phaseBRound = new StrategicPhaseResult();
                    yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                        postCommitments, phaseB.Reservation ?? phaseA.Reservation,
                        phaseBRound, reconObjectives);
                    ReservationInvariants.CheckBoundary(player, root, ctx,
                        $"phaseB round {managementRound + 1}");
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    WorldAnalysis.StepObservationStamp afterManagement =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                        beforeManagement, afterManagement, null);
                    phaseB.Accumulate(phaseBRound);
                    yield return ctx.WaitAtObserverActionBoundary();
                    // Phase B has spent first; return legs now take what is left.
                    bool releaseReturnsNow = !lifecycleReturnsReleased && lifecycleReturnsDeferred;
                    lifecycleReturnsReleased = true;

                    TakeTypedTriggers(out StrategicInvalidationReason operationalReasons,
                        out StrategicInvalidationReason strategicReasons,
                        out HashSet<DesireAxis> dirtyStrategicAxes);
                    bool operationalDirty = operationalReasons != StrategicInvalidationReason.None;
                    bool strategicDirty = strategicReasons != StrategicInvalidationReason.None;
                    yield return ReenterStrategicAxes(
                        strategicReasons, dirtyStrategicAxes);
                    bool strategicChanged = reentryStateChanged;
                    // Phase B reentry can itself publish a compound invalidation. Preserve its
                    // full typed fan-out before acknowledging it.
                    TakeTypedTriggers(
                        out StrategicInvalidationReason managementFollowupOperational,
                        out StrategicInvalidationReason managementFollowupStrategic,
                        out HashSet<DesireAxis> managementFollowupAxes);
                    operationalReasons |= managementFollowupOperational;
                    strategicReasons |= managementFollowupStrategic;
                    operationalDirty |= managementFollowupOperational
                        != StrategicInvalidationReason.None;
                    strategicDirty |= managementFollowupStrategic
                        != StrategicInvalidationReason.None;
                    yield return ReenterStrategicAxes(
                        managementFollowupStrategic, managementFollowupAxes);
                    strategicChanged |= reentryStateChanged;
                    if (operationalDirty || strategicChanged)
                        noProgressCycles = 0;

                    AiDebugLog.Write($"[AI][V2][Loop] management round={managementRound + 1} "
                        + $"strategicTriggers={strategicReasons} "
                        + $"operationalTriggers={operationalReasons} "
                        + $"operationalReadmit={(operationalDirty ? 1 : 0)}");

                    if (operationalDirty)
                    {
                        noProgressCycles = 0;
                        yield return RunTypedAdmissions();
                    }

                    // Phase B can change the hand or world without publishing a typed
                    // operational trigger. Reuse the canonical bounded admission loop
                    // on the settled state before admitting any zero-Radar residual.
                    if (phaseBRound.StateChanged && !operationalDirty)
                    {
                        noProgressCycles = 0;
                        yield return RunTypedAdmissions();
                    }
                    if (releaseReturnsNow && !operationalDirty && !phaseBRound.StateChanged)
                    {
                        AiDebugLog.Write("[AI][V2][Loop] lifecycle returns released after the tempo pass");
                        noProgressCycles = 0;
                        yield return RunTypedAdmissions();
                    }
                    if (!phaseBRound.StateChanged && !strategicChanged)
                        break;
                    if (!operationalDirty && !strategicDirty)
                        break;
                }
                phaseBHandled = true;

                // A zero Radar is not a prohibition. Only AFTER the existing operational
                // and tempo passes exhaust their actionable budgets may new cold-axis
                // preparation use what is physically left. No new budget/scorer/executor:
                // call the same Phase A owner with freshly regenerated cold demands.
                var coldAxes = new HashSet<DesireAxis>(demandAxes.Where(a =>
                    RadarValueScale.For(radar, a) <= 0f));
                if (zeroRadarResidualWindow && coldAxes.Count > 0
                    && settledSteps < AiConfigV2.maxMidTurnStepsPerTurn
                    && noProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles)
                {
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    yield return CombatOpportunityAnalyzer.WarmEstimates(snapshot);
                    OperationalFrame zeroRadarFrame = RefreshOperationalFrame(turnSession, snapshot, assessment.Breakdown);
                    reconObjectives = zeroRadarFrame.Recon;
                    aggressionObjectives = zeroRadarFrame.Aggression;
                    activeIntents = zeroRadarFrame.Intents;
                    actorCommitments = zeroRadarFrame.Commitments;
                    List<AxisDemand> coldDemands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                            reconObjectives, aggressionObjectives, activeIntents,
                            actorCommitments, player, ctx, root, demandAxes)
                        .Where(d => d != null && coldAxes.Contains(d.RequestingAxis)).ToList();
                    if (coldDemands.Count > 0)
                    {
                        // Phase A owns one carried Reservation object. Its per-call residual
                        // rewrite must not erase still-unfulfilled positive-axis telemetry.
                        List<AxisDemand> warmResidual = (phaseB.Reservation ?? phaseA.Reservation)
                            .UnresolvedDemands.Where(d => d != null
                                && RadarValueScale.For(radar, d.RequestingAxis) > 0f).ToList();
                        WorldAnalysis.StepObservationStamp beforeCold =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        StrategicPhaseResult coldPass = StrategicManager.FulfillDemands(
                            snapshot, player, root, hand, ctx, apBudget, coldDemands,
                            actorCommitments, activeIntents, reconObjectives,
                            phaseB.Reservation ?? phaseA.Reservation,
                            economyAxisAuthoritative: coldAxes.Contains(DesireAxis.Economy),
                            radar: radar);
                        phaseA.Accumulate(coldPass);
                        phaseA.Reservation.UnresolvedDemands.AddRange(warmResidual);
                        AiDebugLog.Write($"[AI][V2][Loop] cold Radar residual — demands={coldDemands.Count} "
                            + $"spent={coldPass.CardsPlayed} changed={(coldPass.StateChanged ? 1 : 0)}");
                        if (coldPass.StateChanged)
                        {
                            snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                                snapshot, player, root, hand, ctx);
                            WorldAnalysis.StepObservationStamp afterCold =
                                WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                            WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                                beforeCold, afterCold, null);
                            yield return CombatOpportunityAnalyzer.WarmEstimates(snapshot);
                            OperationalFrame coldFrame = RefreshOperationalFrame(turnSession, snapshot, assessment.Breakdown);
                            reconObjectives = coldFrame.Recon;
                            aggressionObjectives = coldFrame.Aggression;
                            activeIntents = coldFrame.Intents;
                            actorCommitments = coldFrame.Commitments;
                            demands = DemandLayer.Generate(
                                snapshot, assessment.Breakdown, reconObjectives,
                                aggressionObjectives, activeIntents, actorCommitments,
                                player, ctx, root, demandAxes);
                            ownershipFreshAfterPhaseA = true;
                            yield return ctx.WaitAtObserverActionBoundary();
                            yield return RunTypedAdmissions();
                        }
                        else
                        {
                            yield return ctx.WaitAtObserverActionBoundary();
                        }
                    }
                }

                // Air-support safety net: a wing still over its target that cannot safely end
                // another turn there (its strike leg was not run, or found nothing) flies home now
                // instead of taking fuel damage. Free: the sortie launch was already paid.
                foreach (ArmyData unsafeWing in GroundCombatAirSupport.RecallUnsafeStrikes(player, ctx.Map))
                {
                    WorldAnalysis.StepObservationStamp beforeRecall =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    bool recallChanged = false;
                    yield return AviationRebasePlanner.ExecuteContinuation(
                        player, root, ctx, unsafeWing, v => recallChanged = v);
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    WorldAnalysis.StepObservationStamp afterRecall =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                        beforeRecall, afterRecall, null);
                    ReservationInvariants.CheckBoundary(player, root, ctx,
                        $"air-support recall #{unsafeWing.Id}");
                    yield return ctx.WaitAtObserverActionBoundary();
                }

                // Cold Phase A and the following typed admissions may have created or
                // re-bound actors AFTER management captured postCommitments. Housekeeping
                // must see the latest canonical ownership, never the pre-cold snapshot.
                postCommitments = turnSession.RefreshActors(
                    turnSession.PersistentState.All, snapshot, reconObjectives);

                // Final reconciliation remains the only owner of end-of-turn aging/reaping. Intents
                // already reconciled locally carry LastReconciledTurn==turn and are not aged twice.
                turnSession.SettleAfterTurn(System.Array.Empty<MissionStepResult>());
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
            }

            // S5. Strategic Manager Phase B — Surplus Preparation. Spec §5/§13 — this runs in EVERY
            //     cycle: it is hand/card lifecycle management, not an operational
            //     mission family. Card type is never on its own a reason a legal card is left unplayed.
            // Execution can reveal contacts and alter map knowledge (especially aviation). Phase B
            // must consume a coherent strategic snapshot, not operational resources paired with
            // the pre-execution Known/MapKnowledge layers.
            if (!phaseBHandled)
            {
                InfrastructureFulfillment.ReleaseDeferredEconomyIncomeCover(player, ctx);
                OperationContinuationWindow.Settle(player, ctx.TurnNumber);
                snapshot = WorldAnalysis.RefreshStrategicKnowledge(snapshot, player, root, hand, ctx);
                reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                postCommitments = turnSession.RefreshActors(
                    turnSession.PersistentState.All, snapshot, reconObjectives);
                // Phase B is the single bounded end-of-turn tempo arbiter (coroutine).
                yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                    postCommitments, phaseA.Reservation, phaseB, reconObjectives);
                if (phaseB.StateChanged)
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                yield return ctx.WaitAtObserverActionBoundary();
            }

            // Spec §9 — one per-turn StrategicManager summary so it is always answerable why each
            // hand card was or was not played this turn. Per-card blocking reasons are on the
            // strat.A/strat.B diag lines above (fails=[card: needs H/E/M/T=…] / defer … / hold …).
            // "remaining" carries no strategy-scope rejection: Phase B always runs, and card type
            // alone never suppresses a legal card.
            int mPlayed = phaseA.CardsPlayed + phaseB.CardsPlayed;
            int mGen = phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            int mEquip = phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;
            int mInfra = phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            int mDrawn = phaseA.CardsDrawn + phaseB.CardsDrawn;
            int handEnd = hand?.Hand?.Count ?? 0;
            if (AiDebugLog.Verbose) AiDebugLog.Write($"[AI][V2][StrategicManager][Summary] handStart={handAtStart} "
                + $"played={mPlayed} (phaseA {phaseA.CardsPlayed}, phaseB {phaseB.CardsPlayed}) "
                + $"generated={mGen} equipAttached={mEquip} infraBuilt={mInfra} drawn={mDrawn} "
                + $"handEnd={handEnd} remaining={System.Math.Max(0, handEnd)} "
                + $"matAttempts={phaseA.MaterializationAttempts + phaseB.MaterializationAttempts} "
                + $"capDeliveries={phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries} "
                + "blockedReasons=see strat.A/strat.B diag lines");

            // End-of-Main physical resource control totals (spec §2.7). Housekeeping is zero-AP by
            // invariant and the bounded reaction pass logs its own [STATE]; captured here so the
            // Main line means the main phase.
            AiV2Trace.LogState(trace.Id, stateStart, AiV2Trace.Stamp(root));

            // 8. Off-budget housekeeping — NOT an axis, guaranteed minimum, cannot be out-competed.
            //    This safety/cleanup layer does not buy cards or create new
            //    capability and remains the authoritative same-hex reorganisation path.
            var housekeeping = new HousekeepingResult();
            yield return HousekeepingManager.RunHousekeeping(
                snapshot, player, root, ctx, postCommitments, housekeeping, phaseB.Reservation);
            ReservationInvariants.CheckBoundary(player, root, ctx, "housekeeping");
            if (housekeeping.StateChanged)
                snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                    snapshot, player, root, hand, ctx);
            yield return ctx.WaitAtObserverActionBoundary();

            // --- Main-phase activity bucket. DERIVED once, here, from this pipeline's own facts —
            //     never incremented inside a nested layer (spec §11). The Reaction bucket is owned
            //     by StrategicReactionPass the same way. Total = Main + Reaction, no double count.
            V2PhaseActivity main = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            main.DemandsRaised = demands.Count;
            main.MissionsConsidered = missions.Count;
            // §8 — the activity bucket's peers (Provisioned, ExecutionAttempts, …) are all
            // full-turn cumulative, so MissionsFunded is the distinct-missions-funded-this-turn
            // count, not just the last pack's.
            main.MissionsFunded = fundedKeysThisTurn.Count;
            main.Provisioned = provisioned.Count;
            foreach (KeyValuePair<ProvisionFailureKind, int> failure in provisioningFailures)
                for (int i = 0; i < failure.Value; i++)
                    main.RecordProvisionFailure(failure.Key);
            main.ExecutionAttempts = allExecuted.Count(MissionRevalidator.WasAttempt);
            main.ExecutionsSucceeded = allExecuted.Count(MissionRevalidator.WasGenuineExecution);
            main.ExecutionsStaleOrSkipped = allExecuted.Count(MissionRevalidator.WasStaleOrSkipped);
            main.CardsPlayed = phaseA.CardsPlayed + phaseB.CardsPlayed;
            main.CardsDrawn = phaseA.CardsDrawn + phaseB.CardsDrawn;
            main.InfrastructureAttempts = phaseA.InfrastructureAttempts + phaseB.InfrastructureAttempts;
            main.InfrastructureBuilt = phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            main.MaterializationAttempts = phaseA.MaterializationAttempts + phaseB.MaterializationAttempts;
            main.MaterializationsSucceeded = phaseA.MaterializationsSucceeded + phaseB.MaterializationsSucceeded;
            main.GeneratedCardAttempts = phaseA.GeneratedCardAttempts + phaseB.GeneratedCardAttempts;
            main.GeneratedCardsSucceeded = phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            main.EquipmentAssignmentAttempts = phaseA.EquipmentAssignmentAttempts + phaseB.EquipmentAssignmentAttempts;
            main.EquipmentAssignmentsSucceeded = phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;
            main.CapabilityDeliveries = phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries;

            // No strategic resource reservation may survive turn end. Anything still
            // standing is an owner that failed to release; log it and force-clear.
            turnSession.AuditTurnEnd(snapshot, reconObjectives);
            turnSession.CompleteReservations();
            ReservationInvariants.LogTurnSummary(player, ctx.TurnNumber);

            RecordInitiativeAnalytics(player, root, hand, initiativeStartAp, initiativeBaseAp, initiativeActionableAtStart);

            // Emit the canonical turn summary only after every turn-scoped cleanup/invariant check
            // has completed, so [TURN-END] really is the final strategic lifecycle marker.
            if (AiDebugLog.Verbose) AiDebugLog.Write($"[AI][V2] === {player.Nickname} — V2 turn ends "
                + $"(demands {demands.Count}, stratA {phaseA.CardsPlayed}, missions {missions.Count}, "
                + $"lastPackFunded {allocation.Funded.Count}, turnFundedUnique {fundedKeysThisTurn.Count}, "
                + $"provisioned {provisioned.Count}, executed {allExecuted.Count}, stratB {phaseB.CardsPlayed}) ===");
            V2TurnActivityTelemetry.LogSummary(player, ctx.TurnNumber);
            ApTurnMeasure apMeasure = ApTurnPressure.Measure(player, root, hand, ctx,
                initiativeStartAp, allocation.Deferred);
            ApTurnPressure.Record(player, ctx.TurnNumber, apMeasure);
            ApBudgetTelemetry.End(player, ctx,
                StrategicTempoBudget.For(player, ctx.TurnNumber).DrawActionsUsed, apMeasure);
            turnSession.Dispose();
            yield return null;
        }

        // One recipe for re-deriving the operational facts of a decision frame after a settled
        // mutation: Recon and Aggression objectives, durable intents, then the actor-claim view.
        private readonly struct OperationalFrame
        {
            internal readonly List<ReconObjective> Recon;
            internal readonly List<RaidObjective> Aggression;
            internal readonly List<MissionIntent> Intents;
            internal readonly ActorCommitments Commitments;
            internal OperationalFrame(List<ReconObjective> recon, List<RaidObjective> aggression,
                List<MissionIntent> intents, ActorCommitments commitments)
            { Recon = recon; Aggression = aggression; Intents = intents; Commitments = commitments; }
        }

        private static OperationalFrame RefreshOperationalFrame(AiTurnSession session,
            WorldSnapshot snapshot, DesireBreakdown breakdown)
        {
            List<ReconObjective> recon = ReconObjectiveEvaluator.Enumerate(snapshot);
            // Rebuild the operational Aggression facts from this settled snapshot before
            // re-enumerating objectives, so a neutral destroyed by the previous step is gone.
            StrategyLayer.RefreshAggressionOperationalFacts(snapshot, breakdown);
            List<RaidObjective> aggression = RaidObjectiveEvaluator.Enumerate(
                snapshot, breakdown.OpportunityReport);
            List<MissionIntent> intents = MissionContinuityLayer.ResolveActive(
                session.Player, snapshot, recon, aggression);
            return new OperationalFrame(recon, aggression, intents,
                session.RefreshActors(intents, snapshot, recon));
        }

        private static List<MissionProposal> BuildMissionSet(WorldSnapshot snapshot,
            DesireBreakdown breakdown, IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<ReconObjective> reconObjectives,
            IReadOnlyList<RaidObjective> aggressionObjectives, Radar radar,
            IReadOnlyList<AxisDemand> demands, V2TraceScope trace,
            AiTurnContext ctx, out Dictionary<MissionIntentKey, string> deferredThisPass,
            bool aggressionPressureAlreadyRefreshed = false)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Pipeline.BuildMissionSet");
            // Orchestration owns mid-turn sequencing: refresh only the Recon lane pressures from
            // the current snapshot right before Missions consumes them, so a frontier completion
            // earlier this same settled pass is reflected without Missions itself triggering
            // Strategy/Desire recomputation.
            StrategyLayer.RefreshReconLanePressures(snapshot, breakdown);
            // The same discipline for the Aggression lane: refresh only the
            // operational opportunity facts from the current snapshot, never the radar.
            if (!aggressionPressureAlreadyRefreshed)
                StrategyLayer.RefreshAggressionOperationalFacts(snapshot, breakdown);
            deferredThisPass = new Dictionary<MissionIntentKey, string>();
            List<MissionProposal> missions = ReconMissionPlanner.Propose(snapshot, breakdown,
                activeIntents, reconObjectives, deferredThisPass, ctx);
            missions.AddRange(AggressionMissionLayer.Propose(snapshot, breakdown,
                activeIntents, aggressionObjectives, ctx, deferredThisPass));
            missions.AddRange(EconomyMissionPlanner.Propose(snapshot, breakdown,
                activeIntents, demands, deferredThisPass));
            missions.AddRange(DevelopmentMissionPlanner.Propose(snapshot, activeIntents, demands));

            foreach (MissionProposal m in missions)
                if (m != null && string.IsNullOrEmpty(m.AttemptId))
                    m.AttemptId = trace?.NextMissionAttemptId() ?? "?";
            foreach (MissionProposal m in missions)
                if (m != null)
                    m.EffectiveValue = m.BaseValue * RadarValueScale.For(radar, m);
            AttackPreparationPriority.Apply(missions);
            AiFrameLog.TaskScores(snapshot?.Observer, snapshot?.TurnNumber ?? 0, missions);

            AiV2Trace.CorrelateDemandsToMissions(demands, missions);
            return missions;
        }

        // End-of-turn initiative AP telemetry write-back (see the turn-start capture above). A
        // turn that ended at 0 AP only counts as "needed more AP" if real AP work still remained
        // — an unactivated field army, or an affordable AP-costing card still in hand.
        private static void RecordInitiativeAnalytics(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            int startAp, int baseAp, int actionableAtStart)
        {
            int endAp = root.ActionPoints;
            int apSpent = UnityEngine.Mathf.Max(0, startAp - endAp);
            int unactivatedActionable =
                Game.Ai.V2.Initiative.PreTurnCapacityAnalysis.CountActionableFieldArmies(player, unactivatedOnly: true);

            bool affordableCardWaiting = false;
            if (hand != null && endAp > 0)
                foreach (Game.Cards.CardData c in hand.Hand)
                {
                    int ap = c != null ? CardCostRules.PlayAp(c) : 0;
                    if (ap > 0 && ap <= endAp) { affordableCardWaiting = true; break; }
                }

            bool hadPotentialWork = unactivatedActionable > 0 || affordableCardWaiting;

            Game.Ai.V2.Initiative.InitiativeAnalyticsHistory.Record(player,
                new Game.Ai.V2.Initiative.InitiativeTurnRecord(
                    baseAp, startAp, apSpent, endAp,
                    actionableAtStart, unactivatedActionable, hadPotentialWork));
            AiMatchStats.RecordAiTurn(player, startAp, apSpent, endAp);
        }

        // Armies whose POSITION/movement/activation can change an Economy decision (the Economy
        // admission fingerprint above): every Economy intent's mover/builder/collector plus every
        // army the Economy analysis advertises as a possible builder/collector.
        internal static HashSet<int> EconomyRelevantArmyIds(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var ids = new HashSet<int>();
            foreach (MissionIntent i in activeIntents ?? new List<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || i.Kind != MissionKind.Economy
                    || i.Economy == null)
                    continue;
                if (i.PreferredMoverArmyId.HasValue) ids.Add(i.PreferredMoverArmyId.Value);
                if (i.Economy.BuilderArmyId != null) ids.Add(i.Economy.BuilderArmyId.Value);
                if (i.Economy.CollectorArmyId != null) ids.Add(i.Economy.CollectorArmyId.Value);
            }
            EconomyStanding eco = snapshot?.Economy;
            if (eco != null)
            {
                void AddRoutes(IReadOnlyList<EconomyBuilderRouteSnapshot> routes)
                {
                    foreach (EconomyBuilderRouteSnapshot r in routes
                                 ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
                        ids.Add(r.ArmyId);
                }
                foreach (EconomyExtractionOpportunity x in eco.ExtractionOpportunities
                             ?? System.Array.Empty<EconomyExtractionOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (EconomyExtractionOpportunity x in eco.CollectorSites
                             ?? System.Array.Empty<EconomyExtractionOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (EconomyBaseOpportunity x in eco.BaseOpportunities
                             ?? System.Array.Empty<EconomyBaseOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (MobileCollectionOpportunity x in eco.MobileCollectionOpportunities
                             ?? System.Array.Empty<MobileCollectionOpportunity>())
                    ids.Add(x.CollectorArmyId);
            }
            return ids;
        }
    }

    // ---- Stage stubs. Each grows real logic in its build-order step, then splits into its own
    //      file. Signatures are deliberate seams; fill the bodies, don't reshape the flow.

    // WorldAnalysis (build-order step 2) now lives in its own file, WorldAnalysis.cs.

    // StrategyLayer (build-order step 3) now lives in its own file, DesireEvaluators.cs, together
    // with ReconEvaluator / AggressionEvaluator, the AiRadarState cross-turn registry, and the
    // RadarAssessment / DesireBreakdown contract it returns.

    // MissionLayer (build-order step 4, + step 7.1 candidate beam) now lives in its own file,
    // ReconMissionPlanner.cs, with ScoutCostModel (the shared AP/Energy/ETA estimator — risk 3).
    // It reads the DesireBreakdown and emits a CANDIDATE BEAM of up to
    // AiConfigV2.scoutCandidateBeamWidth Scout proposals (execution capacity K and mission
    // conflicts are the allocator's job — MissionAdmissionPolicy); Raid is added in step 9.

    // MissionContinuityLayer (build-order step 7) lives in MissionIntent.cs, with MissionIntent /
    // MissionIntentKey / ScoutIntent / MissionIntentRegistry (durable intent state), CommitmentTier
    // / IntentStatus (funding policy + suspension), and MissionOutcomeLedger / MissionStepResult
    // (the ordered per-turn record ReconcileAfterTurn transitions on). ScoutObjectiveEvaluator (the
    // shared completion / validity home) lives in ScoutObjectiveEvaluator.cs.

    // ResourceAllocator (build-order step 5) lives in ResourceAllocator.cs. ProvisioningManager /
    // ProvisioningSession / ProvisionedMission / ProvisionFailure / ProvisioningResult (build-order
    // step 6a) live in ProvisioningManager.cs, with the shared ScoutMoverSelector. TaskExecutor /
    // ExecutionResult / ExecutionStopReason (step 6a) live in TaskExecutor.cs. AiScoutStealthPolicy
    // (the shared V1+V2 stealth-warrant primitive) lives in Assets/Scripts/Ai/AiScoutStealthPolicy.cs.

    // HousekeepingManager (renamed from Manager) — build-order step 8C. A SEPARATE, post-mission
    // system from StrategicManager: it owns deterministic same-hex army/garrison REORGANIZATION,
    // not card play. NOT a radar axis — off-budget. It now lives in its own file,
    // HousekeepingManager.cs, with LocalForceGroup / ArmyReorgProfile (ArmyReorgProfile.cs),
    // ArmyReorgAnalyzer.cs, ArmyReorganizationPlanner.cs, ReorganizationPlan.cs and
    // HousekeepingExecutor.cs. This orchestration file only calls it (stage 8 above).
}
