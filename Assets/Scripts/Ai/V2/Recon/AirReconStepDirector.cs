using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ARCH-02 §35 — the air-recon INFORMATION-WEIGHTING policy. Aviation is SUPPORT: it only ever
    // REVEALS hexes (never marks one ground-Visited) and serves only the AirSweep observation
    // pass, so it has ONE mode — an observation sweep that values never-observed and stale hexes
    // alike along the route (AirReconRouteScorer). There is no Explore/Refresh split for air;
    // Refresh is returned only because ReconMode is the shared patrol-state vocabulary.
    internal static class AirReconModePolicy
    {
        internal static ReconMode RequestedMode(PlayerSetupData player, WorldSnapshot snapshot) =>
            ReconMode.Refresh;
        // Durable mode precedence is shared by read-only capability projection and execution.
        internal static ReconMode EffectiveMode(PlayerSetupData player, int armyId, ReconMode requested) =>
            ReconPatrolStateRegistry.TryGet(player, armyId, out ReconPatrolState patrol)
                ? patrol.Mode : requested;

    }

    // ARCH-02 review r4 — the explicit lifecycle owner for a wing's ReconAirSortieState. The
    // planner (AirReconStepDirector.PlanStep) is read-only; ReconAirExecutor calls these to record
    // authoritative facts (Observe / BeginTurn) and to apply a StepDecision's intended transition
    // AFTER a successful gameplay action (Apply). A rejected / failed action never reaches Apply,
    // so the sortie state cannot drift ahead of what actually happened.
    internal static class ReconAirSortieLifecycle
    {
        // Idempotent facts about where the wing physically is right now — not a pending transition.
        internal static void Observe(ReconAirSortieState sortie, ArmyData air)
        {
            if (!air.Hex.Equals(sortie.LaunchHex))
            {
                sortie.ClaimedSector = ReconDirectionModel.Sector(sortie.LaunchHex, air.Hex);
                sortie.HasClaim = true;
            }
        }

        // An owned airfield ends the sortie only when the wing is turning for home: the Return
        // phase, or the used-up outbound leg (PlanStep's own Return trigger, which it does not
        // re-evaluate while standing on an airfield). An intermediate friendly airfield crossed
        // during Outbound is just another hex of the route.
        internal static bool CompletesAtAirfield(ReconAirSortieState sortie, bool atAirfield,
            bool hasDeparted) =>
            sortie != null && atAirfield && hasDeparted
            && sortie.Phase == ReconAirPhase.Return;

        // One Hold re-opening rule for the live director and its read-only scorer projection.
        internal static ReconAirPhase PhaseAfterHold(ReconAirPhase phase, bool newTurn, int safeEnds) =>
            phase == ReconAirPhase.Hold && newTurn
                ? (safeEnds > 0 ? ReconAirPhase.Outbound : ReconAirPhase.Return)
                : phase;

        // Mark this AI turn as processed for the sortie (Hold-reopen-once semantics). Executor-owned.
        internal static bool BeginTurn(ReconAirSortieState sortie, int turn) => sortie.BeginTurn(turn);

        // Apply the StepDecision's intended durable transition. Call ONLY after the executor
        // confirmed the matching gameplay action succeeded.
        internal static void Apply(ReconAirSortieState sortie, in AirReconStepDirector.StepDecision d)
        {
            if (sortie == null) return;
            if (d.NextBestOutboundScore.HasValue)
                sortie.BestOutboundStepScore = d.NextBestOutboundScore.Value;
            if (d.NextPhase.HasValue)
                sortie.Phase = d.NextPhase.Value;
            if (d.NextDecisionReason != null)
                sortie.LastDecisionReason = d.NextDecisionReason;
            if (d.SetChosenLanding)
            {
                sortie.ChosenLandingHex = d.LandingHex;
                sortie.HasChosenLanding = true;
            }
        }
    }

    // ARCH-02 §35 / review r3 P0 — the per-step air-recon PLANNER. Every tactical decision that
    // used to live inside ReconAirExecutor.RunActor is here: phase state machine (Outbound /
    // Turning / Hold / Return), ReconMode resolution, the ReconAirStepPlanner.Pick call, the
    // Outbound->Turning->Return transitions, PickReturnStep + landing hysteresis, the activation
    // energy / affordability gates, and recoverable opportunistic strikes. It reads live world
    // state on every call. ReconAirExecutor only issues the
    // canonical Move / Strike / assignment-bookkeeping calls the returned decision names.
    internal static class AirReconStepDirector
    {
        internal enum StepKind
        {
            Stop,          // sortie is done for this pass (see teardown flags)
            HoldEndTurn,   // a Hold set earlier this turn — end the sortie's turn aloft here
            HoldReopen,    // a Hold set on a previous turn — reopen and check for a strike
            Strike,        // a legal recoverable strike exists at the current hex right now
            ReturnStep,    // one adjacent step toward the chosen landing airfield
            ForwardStep,   // one adjacent Outbound / Turning step toward useful information
        }

        internal readonly struct StepDecision
        {
            public readonly StepKind Kind;
            public readonly HexCoord Step;
            public readonly HexCoord LandingHex;
            public readonly ReconMode Mode;
            public readonly float StepScore;
            public readonly string Reason;

            // ReturnStep only — the step also happens to be an informative Pick target.
            public readonly bool AlsoInformative;
            // ForwardStep only — this was the Turning pivot step (log "pivot step taken").
            public readonly bool PivotToReturnAfterMove;
            // HoldReopen only — phase to resume unless the arrival strike forced Return.
            public readonly ReconAirPhase ResumePhase;
            // Stop only — teardown the executor must perform.
            public readonly bool RetireAssignment;
            public readonly bool RemoveReservation;

            // ---- INTENDED lifecycle transition ------------------------------------------------
            //  ARCH-02 review r4 — PlanStep is read-only; the durable ReconAirSortieState mutation
            //  it would have made is described here and applied by ReconAirSortieLifecycle.Apply
            //  ONLY AFTER the executor has successfully performed the corresponding gameplay call.
            //  A rejected / failed action therefore leaves the sortie state untouched.
            public readonly ReconAirPhase? NextPhase;         // null => Phase unchanged
            public readonly string NextDecisionReason;        // null => LastDecisionReason unchanged
            public readonly float? NextBestOutboundScore;     // null => BestOutboundStepScore unchanged
            // Return / Forward — the landing the director resolved (via PickReturnStep + landing
            // hysteresis, or the Pick's own landing). Written to ChosenLandingHex / HasChosenLanding
            // by Apply, i.e. only after the Move actually succeeded. LandingHex holds the value.
            public readonly bool SetChosenLanding;

            private StepDecision(StepKind kind, HexCoord step, HexCoord landing, ReconMode mode,
                float stepScore, string reason, bool alsoInformative, bool pivotToReturnAfterMove,
                ReconAirPhase resumePhase, bool retireAssignment, bool removeReservation,
                ReconAirPhase? nextPhase, string nextDecisionReason, float? nextBestOutboundScore,
                bool setChosenLanding)
            {
                Kind = kind;
                Step = step;
                LandingHex = landing;
                Mode = mode;
                StepScore = stepScore;
                Reason = reason;
                AlsoInformative = alsoInformative;
                PivotToReturnAfterMove = pivotToReturnAfterMove;
                ResumePhase = resumePhase;
                RetireAssignment = retireAssignment;
                RemoveReservation = removeReservation;
                NextPhase = nextPhase;
                NextDecisionReason = nextDecisionReason;
                NextBestOutboundScore = nextBestOutboundScore;
                SetChosenLanding = setChosenLanding;
            }

            public static StepDecision Stop(string reason, bool retireAssignment = false,
                bool removeReservation = false) =>
                new StepDecision(StepKind.Stop, default, default, default, 0f, reason, false, false,
                    ReconAirPhase.Return, retireAssignment, removeReservation, null, null, null, false);

            public static StepDecision HoldEndTurn(string reason) =>
                new StepDecision(StepKind.HoldEndTurn, default, default, default, 0f, reason, false,
                    false, ReconAirPhase.Return, false, false, null, null, null, false);

            public static StepDecision HoldReopen(ReconAirPhase resumePhase, string reason) =>
                new StepDecision(StepKind.HoldReopen, default, default, default, 0f, reason, false,
                    false, resumePhase, false, false, null, reason, null, false);

            public static StepDecision Strike(string reason) =>
                new StepDecision(StepKind.Strike, default, default, default, 0f, reason, false, false,
                    ReconAirPhase.Return, false, false, null, null, null, false);

            public static StepDecision Return(HexCoord step, HexCoord landing, bool alsoInformative,
                string reason, string nextDecisionReason, float? nextBestOutboundScore) =>
                new StepDecision(StepKind.ReturnStep, step, landing, default, 0f, reason,
                    alsoInformative, false, ReconAirPhase.Return, false, false,
                    ReconAirPhase.Return, nextDecisionReason, nextBestOutboundScore, true);

            public static StepDecision Forward(HexCoord step, HexCoord landing, ReconMode mode,
                float score, bool pivot, string reason, string nextDecisionReason,
                float? nextBestOutboundScore) =>
                new StepDecision(StepKind.ForwardStep, step, landing, mode, score, reason, false,
                    pivot, ReconAirPhase.Return, false, false,
                    pivot ? ReconAirPhase.Return : (ReconAirPhase?)null, nextDecisionReason,
                    nextBestOutboundScore, true);
        }

        // Decide the next thing this airborne wing should do — READ-ONLY. `newTurn` is the result
        // of the executor's own sortie.BeginTurn(turn) lifecycle call. Strike eligibility is read
        // live on every step, including the first step of a new turn and the return leg.
        // PlanStep issues NO gameplay call and makes NO durable ReconAirSortieState change: any
        // Phase / reason / best-score transition it wants is returned on the StepDecision and
        // applied by ReconAirSortieLifecycle.Apply after a confirmed successful action. (The one
        // exception is a transient sortie.Phase = Turning around the pivot re-Pick, restored in a
        // finally before return — the scorer reads Phase, and it never survives the call.)
        internal static StepDecision PlanStep(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData air, ReconAirSortieState sortie, bool newTurn,
            HexCoord? missionFocusHex = null)
        {
            int armyId = air.Id;
            bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
            int safeUnlandedEnds = AviationRange.SafeUnlandedEndsRemaining(air);

            ReconAirPhase workingPhase = sortie.Phase;
            string decisionReason = null;
            float? nextBestOutboundScore = null;
            bool pivotAfterForward = false;

            // ---- Hold resolution -------------------------------------------------------------
            if (workingPhase == ReconAirPhase.Hold)
            {
                if (!newTurn)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Hold — ending turn aloft; "
                        + $"safeEnds={safeUnlandedEnds} reason={sortie.LastDecisionReason}");
                    return StepDecision.HoldEndTurn("hold set earlier this turn");
                }
                ReconAirPhase resume = ReconAirSortieLifecycle.PhaseAfterHold(
                    workingPhase, newTurn, safeUnlandedEnds);
                if (resume == ReconAirPhase.Return)
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Hold->Return "
                        + $"reason=endurance_deadline safeEnds={safeUnlandedEnds}");
                return StepDecision.HoldReopen(resume,
                    resume == ReconAirPhase.Return
                        ? "endurance deadline after hold"
                        : "hold reopened on fresh turn");
            }

            // ---- opportunistic strike at the current hex ------------------------------------
            if (!atAirfield && CanOpportunisticallyStrike(player, ctx, air))
                return StepDecision.Strike("recoverable strike at current hex");

            // ---- normal forward / return flow --------------------------------------------------
            ReconMode mode = AirReconModePolicy.EffectiveMode(player, armyId,
                AirReconModePolicy.RequestedMode(player, snapshot));

            ReconAirStepPlanner.StepChoice? choice =
                ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie,
                    missionFocusHex: missionFocusHex);

            if (!atAirfield && workingPhase == ReconAirPhase.Outbound && choice.HasValue)
            {
                float bestOutbound = Math.Max(sortie.BestOutboundStepScore, choice.Value.Score);
                nextBestOutboundScore = bestOutbound;
                int mpSlackAfterStep = air.CurrentMovement - choice.Value.RouteCost;
                bool marginalDrop = bestOutbound > 0.01f
                    && choice.Value.Score <= AiConfigV2.airReconTurningMarginalGainFloor * bestOutbound;
                bool returnReserve = choice.Value.RequiredTurns <= 1
                    && mpSlackAfterStep <= AiConfigV2.airReconTurningMpReserveSlack;
                // No Recon-specific distance cap: the route planner already proves after every
                // adjacent step that a landing remains reachable inside the live endurance budget.
                // For TurnsWithoutRefuel=0 this naturally preserves a same-turn round trip; positive
                // endurance may use later turns without a parallel move/2 rule.

                if (workingPhase == ReconAirPhase.Outbound && (marginalDrop || returnReserve))
                {
                    string why = returnReserve ? "return_reserve" : "marginal_gain";
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Turning reason={why} "
                        + $"stepScore={choice.Value.Score:0.00} best={bestOutbound:0.00} "
                        + $"mpSlackAfter={mpSlackAfterStep}");
                    workingPhase = ReconAirPhase.Turning;
                }
            }

            bool forwardStepUseful = choice.HasValue
                && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;

            if (!atAirfield && workingPhase == ReconAirPhase.Turning)
            {
                // The scorer reads sortieState.Phase (Turning gets a lateral weighting). Set it for
                // the re-Pick ONLY, restore before returning — nothing durable survives PlanStep.
                ReconAirPhase saved = sortie.Phase;
                sortie.Phase = ReconAirPhase.Turning;
                try
                {
                    choice = ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie,
                        missionFocusHex: missionFocusHex);
                }
                finally
                {
                    sortie.Phase = saved;
                }
                forwardStepUseful = choice.HasValue
                    && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                if (!forwardStepUseful)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning->Return reason=no_safe_pivot");
                    workingPhase = ReconAirPhase.Return;
                }
            }

            if (!atAirfield && workingPhase == ReconAirPhase.Outbound && !forwardStepUseful)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Return reason=no_safe_forward_step");
                workingPhase = ReconAirPhase.Return;
            }

            bool mustReturn = !atAirfield && (workingPhase == ReconAirPhase.Return || !forwardStepUseful);
            if (mustReturn)
            {
                HexCoord? returnStep = PickReturnStep(player, ctx.Map, air, sortie, out HexCoord landing,
                    out string returnReason);
                if (!returnStep.HasValue)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Return] actor=#{armyId} at "
                        + $"({air.Hex.Q},{air.Hex.R}) — no reachable owned-airfield step; hold position");
                    return StepDecision.Stop("no reachable owned-airfield step");
                }
                bool alsoInformative = choice.HasValue && choice.Value.Hex.Equals(returnStep.Value)
                    && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                return StepDecision.Return(returnStep.Value, landing, alsoInformative, returnReason,
                    decisionReason, nextBestOutboundScore);
            }

            if (!forwardStepUseful)
                return StepDecision.Stop("no useful forward step");

            // No strategic Energy opportunity-cost gate here any more. Whether this sortie is worth
            // its AP/Energy was decided once this turn by ProvisioningManager.AirSortieReservation-
            // Admission (-> AviationSortieReservationEvaluator). This layer only enforces the LIVE
            // HARD affordability gate (CanIssueMoveNow) plus the route/endurance checks above.
            if (!AiTurnController.CanIssueMoveNow(root, air, ctx.Map, choice.Value.Hex))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} cannot afford/issue first step "
                    + $"AP{choice.Value.ActivationAp:0.#}/E{choice.Value.ActivationEnergy:0.#}; cancel/return");
                return StepDecision.Stop("air recon activation unaffordable",
                    retireAssignment: atAirfield, removeReservation: atAirfield);
            }

            bool pivotStep = workingPhase == ReconAirPhase.Turning;
            return StepDecision.Forward(choice.Value.Hex, choice.Value.LandingHex, mode,
                choice.Value.Score, pivotStep || pivotAfterForward, choice.Value.Reason,
                decisionReason, nextBestOutboundScore);
        }

        // ==========================================================================================
        //  OPPORTUNISTIC STRIKE  (spec §46) — the DECISION only. ReconAirExecutor executes the
        //  AviationActions call; AirReconStepDirector.ResolveAfterStrike stamps the post-strike phase.
        // ==========================================================================================
        internal static bool CanOpportunisticallyStrike(PlayerSetupData player,
            AiTurnContext ctx, ArmyData air)
        {
            if (air == null || ctx?.HexSelection?.AviationCombatPresenter == null
                || !AviationRules.IsValidAirArmy(air)
                || !AviationActions.CanActivateForStationaryStrike(air)
                || !AviationActions.CanStrikeAtCurrentHex(air))
                return false;

            if (!AiAirSortiePlanner.CanStrikeAndRecover(air, ctx.Map, player))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                    + "decision=SKIP reason=no_recoverable_strike");
                return false;
            }

            // Once an airborne recon wing physically meets an enemy, the strike is free in MP.
            // Do not invent a second strategic worth-it threshold here: if the attack is legal and
            // a landing remains feasible inside the live endurance budget, attack and then replan.
            return true;
        }

        // Post-strike flight decision. A strike costs no movement and therefore never ends the
        // flight by itself. If a recoverable route still exists, preserve the current flight
        // direction (Outbound keeps exploring/supporting, Return keeps returning). Hold is set only
        // by the executor when the wing actually ends its turn aloft.
        internal static void ResolveAfterStrike(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            ReconAirSortieState sortie, bool attacked)
        {
            if (sortie == null)
                return;

            bool safeReturnGone = air == null || !AiAirSortiePlanner.CanRecover(air, ctx.Map, player);
            if (safeReturnGone)
            {
                sortie.Phase = ReconAirPhase.Return;
                sortie.LastDecisionReason = "return_after_strike: no recoverable route remains";
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air?.Id} attacked={attacked}; "
                    + "WARN no recoverable route after strike — turning home");
                return;
            }

            if (sortie.Phase != ReconAirPhase.Return)
                sortie.Phase = ReconAirPhase.Outbound;
            sortie.LastDecisionReason = "continue_after_strike: strike spent no movement";
            AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} attacked={attacked}; "
                + $"flight continues phase={sortie.Phase} mp={air.CurrentMovement} "
                + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(air)}");
        }

        // ==========================================================================================
        //  RETURN-STEP SELECTION + LANDING HYSTERESIS  (spec §34 / §38)
        // ==========================================================================================
        private static HexCoord? PickReturnStep(PlayerSetupData player, HexMap map, ArmyData air,
            ReconAirSortieState sortie, out HexCoord landing, out string reason)
        {
            landing = default;
            reason = null;

            HexCoord? sameTurn = AiAirSortiePlanner.TryReplan(air, map, player);
            if (sameTurn.HasValue)
            {
                landing = ApplyLandingHysteresis(player, map, air, sortie, sameTurn.Value, out string h);
                reason = "same-turn safest return" + h;
                return AiAirSortiePlanner.FirstRouteStep(map, air.Hex, landing);
            }

            MultiTurnSortie? multi = AiAirSortiePlanner.TryReplanMultiTurnReturn(air, map, player);
            if (multi.HasValue)
            {
                landing = ApplyLandingHysteresis(player, map, air, sortie, multi.Value.LandingHex, out string h);
                reason = $"multi-turn safest return t{multi.Value.RequiredTurns}" + h;
                if (landing.Equals(multi.Value.LandingHex))
                {
                    HexCoord? first = AiAirSortiePlanner.FirstRouteStep(multi.Value.PathFromActionToLanding);
                    if (first.HasValue)
                        return first;
                }
                return AiAirSortiePlanner.FirstRouteStep(map, air.Hex, landing);
            }
            return null;
        }

        // READ-ONLY (review r5) — computes which landing the hysteresis rule picks without writing
        // sortie.ChosenLandingHex/HasChosenLanding. The caller's StepDecision carries the result
        // (SetChosenLanding + LandingHex); ReconAirSortieLifecycle.Apply persists it after the
        // matching Move actually succeeds.
        private static HexCoord ApplyLandingHysteresis(PlayerSetupData player, HexMap map, ArmyData air,
            ReconAirSortieState sortie, HexCoord candidate, out string reason)
        {
            if (sortie == null || !sortie.HasChosenLanding)
            {
                reason = $" landing=adopt({candidate.Q},{candidate.R})";
                return candidate;
            }
            if (sortie.ChosenLandingHex.Equals(candidate))
            {
                reason = $" landing=keep({candidate.Q},{candidate.R})";
                return candidate;
            }
            if (!AiAirSortiePlanner.CanReturnThisTurnTo(player, map, air, sortie.ChosenLandingHex))
            {
                reason = $" landing=switch(prev_unreachable ({sortie.ChosenLandingHex.Q},{sortie.ChosenLandingHex.R}) "
                    + $"-> ({candidate.Q},{candidate.R}))";
                return candidate;
            }

            int prevForward = AiAirSortiePlanner.NearestKnownEnemyDistance(player, sortie.ChosenLandingHex);
            int newForward = AiAirSortiePlanner.NearestKnownEnemyDistance(player, candidate);
            int prevCost = AiAirSortiePlanner.ReturnPathCostOrMax(map, air, sortie.ChosenLandingHex);
            int newCost = AiAirSortiePlanner.ReturnPathCostOrMax(map, air, candidate);
            bool muchMoreForward = prevForward != int.MaxValue && newForward != int.MaxValue
                && prevForward - newForward >= AiConfigV2.airReconLandingSwitchForwardMargin;
            bool muchCheaper = prevCost - newCost >= AiConfigV2.airReconLandingSwitchCostMargin;
            if (muchMoreForward || muchCheaper)
            {
                reason = $" landing=switch(forward {prevForward}->{newForward} cost {prevCost}->{newCost})";
                return candidate;
            }
            reason = $" landing=keep_hysteresis(prev ({sortie.ChosenLandingHex.Q},{sortie.ChosenLandingHex.R}) "
                + $"vs cand ({candidate.Q},{candidate.R}) forward {prevForward}/{newForward} cost {prevCost}/{newCost})";
            return sortie.ChosenLandingHex;
        }

    }
}
