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
    // ARCH-02 §35 — the air-recon INFORMATION-WEIGHTING policy. Aviation only ever REVEALS a hex
    // (it never marks one ground-Visited). While a large fraction of the whole board is still
    // never-observed it values that dark territory (Explore); otherwise it re-checks stale known
    // hexes (Refresh). Shared by AirReconPlanner (initial admission) and AirReconStepDirector
    // (per-step) so the executor never owns this decision.
    internal static class AirReconModePolicy
    {
        internal static ReconMode RequestedMode(PlayerSetupData player, WorldSnapshot snapshot)
        {
            int total = snapshot?.MapKnowledge?.TotalHexes ?? 0;
            if (total <= 0)
                return ReconMode.Refresh;
            int observed = AiReconIntelMemory.Snapshot(player)?.Count ?? 0;
            float neverObservedFrac = 1f - Math.Min(1f, observed / (float)total);
            return neverObservedFrac >= AiConfigV2.airReconExploreDarkFloor
                ? ReconMode.Explore : ReconMode.Refresh;
        }
    }

    // ARCH-02 review r4 — the explicit lifecycle owner for a wing's ReconAirSortieState. The
    // planner (AirReconStepDirector.PlanStep) is read-only; ReconAirExecutor calls these to record
    // authoritative facts (Observe / BeginTurn) and to apply a StepDecision's intended transition
    // AFTER a successful gameplay action (Apply). A rejected / failed action never reaches Apply,
    // so the sortie state cannot drift ahead of what actually happened.
    internal static class ReconAirSortieLifecycle
    {
        // Idempotent facts about where the wing physically is right now — not a pending transition.
        internal static void Observe(ReconAirSortieState sortie, ArmyData air, AiTurnContext ctx, bool atAirfield)
        {
            if (sortie.LaunchTurn < 0)
                sortie.LaunchTurn = atAirfield ? ctx.TurnNumber : ctx.TurnNumber - 1;
            if (!air.Hex.Equals(sortie.LaunchHex))
            {
                sortie.ClaimedSector = ReconDirectionModel.Sector(sortie.LaunchHex, air.Hex);
                sortie.HasClaim = true;
            }
        }

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
        }
    }

    // ARCH-02 §35 / review r3 P0 — the per-step air-recon PLANNER. Every tactical decision that
    // used to live inside ReconAirExecutor.RunActor is here: phase state machine (Outbound /
    // Turning / Hold / Return), ReconMode resolution, the ReconAirStepPlanner.Pick call, the
    // Outbound->Turning->Return transitions, PickReturnStep + landing hysteresis, the activation
    // energy / affordability gates, and the opportunistic-strike arbitration (favourable estimate,
    // KNOWN-AA, safe-return proof). It reads LIVE world state on every call — live replanning is
    // allowed, but it happens in the planner, not the executor. ReconAirExecutor only issues the
    // canonical Move / Strike / assignment-bookkeeping calls the returned decision names.
    internal static class AirReconStepDirector
    {
        internal enum StepKind
        {
            Stop,          // sortie is done for this pass (see teardown flags)
            HoldEndTurn,   // a Hold set earlier this turn — end the sortie's turn aloft here
            HoldReopen,    // a Hold set on a previous turn — run an arrival strike check, then resume ResumePhase
            Strike,        // a favourable opportunistic strike exists at the current hex right now
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

            private StepDecision(StepKind kind, HexCoord step, HexCoord landing, ReconMode mode,
                float stepScore, string reason, bool alsoInformative, bool pivotToReturnAfterMove,
                ReconAirPhase resumePhase, bool retireAssignment, bool removeReservation,
                ReconAirPhase? nextPhase, string nextDecisionReason, float? nextBestOutboundScore)
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
            }

            public static StepDecision Stop(string reason, bool retireAssignment = false,
                bool removeReservation = false) =>
                new StepDecision(StepKind.Stop, default, default, default, 0f, reason, false, false,
                    ReconAirPhase.Return, retireAssignment, removeReservation, null, null, null);

            public static StepDecision HoldEndTurn(string reason) =>
                new StepDecision(StepKind.HoldEndTurn, default, default, default, 0f, reason, false,
                    false, ReconAirPhase.Return, false, false, null, null, null);

            public static StepDecision HoldReopen(ReconAirPhase resumePhase, string reason) =>
                new StepDecision(StepKind.HoldReopen, default, default, default, 0f, reason, false,
                    false, resumePhase, false, false, null, reason, null);

            public static StepDecision Strike(string reason) =>
                new StepDecision(StepKind.Strike, default, default, default, 0f, reason, false, false,
                    ReconAirPhase.Return, false, false, null, null, null);

            public static StepDecision Return(HexCoord step, HexCoord landing, bool alsoInformative,
                string reason, string nextDecisionReason, float? nextBestOutboundScore) =>
                new StepDecision(StepKind.ReturnStep, step, landing, default, 0f, reason,
                    alsoInformative, false, ReconAirPhase.Return, false, false,
                    ReconAirPhase.Return, nextDecisionReason, nextBestOutboundScore);

            public static StepDecision Forward(HexCoord step, HexCoord landing, ReconMode mode,
                float score, bool pivot, string reason, string nextDecisionReason,
                float? nextBestOutboundScore) =>
                new StepDecision(StepKind.ForwardStep, step, landing, mode, score, reason, false,
                    pivot, ReconAirPhase.Return, false, false,
                    pivot ? ReconAirPhase.Return : (ReconAirPhase?)null, nextDecisionReason, nextBestOutboundScore);
        }

        // Decide the next thing this airborne wing should do — READ-ONLY. `newTurn` is the result
        // of the executor's own sortie.BeginTurn(turn) lifecycle call; `arrivalStrikeCheck` is true
        // right after the executor completed a move this pass (or a storage launch's first step).
        // PlanStep issues NO gameplay call and makes NO durable ReconAirSortieState change: any
        // Phase / reason / best-score transition it wants is returned on the StepDecision and
        // applied by ReconAirSortieLifecycle.Apply after a confirmed successful action. (The one
        // exception is a transient sortie.Phase = Turning around the pivot re-Pick, restored in a
        // finally before return — the scorer reads Phase, and it never survives the call.)
        internal static StepDecision PlanStep(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData air, ReconAirSortieState sortie, bool newTurn,
            bool arrivalStrikeCheck)
        {
            int armyId = air.Id;
            bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
            int airborneTurns = sortie.AirborneTurnsElapsed(ctx.TurnNumber);

            ReconAirPhase workingPhase = sortie.Phase;
            string decisionReason = null;
            float? nextBestOutboundScore = null;

            bool canRemainAirborne = !atAirfield
                && AiAirSortiePlanner.CanEndTurnHereAndRecover(air, ctx.Map, player);
            bool mustRecoverThisTurn = !atAirfield && airborneTurns >= 1 && !canRemainAirborne;

            // ---- Hold resolution -------------------------------------------------------------
            if (workingPhase == ReconAirPhase.Hold)
            {
                if (!newTurn)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Hold — ending turn aloft; "
                        + $"airborneTurns={airborneTurns} reason={sortie.LastDecisionReason}");
                    return StepDecision.HoldEndTurn("hold set earlier this turn");
                }
                ReconAirPhase resume = mustRecoverThisTurn ? ReconAirPhase.Return : ReconAirPhase.Outbound;
                if (mustRecoverThisTurn)
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Hold->Return reason=must_recover "
                        + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(air)} airborneTurns={airborneTurns}");
                return StepDecision.HoldReopen(resume,
                    mustRecoverThisTurn ? "must_recover: endurance deadline after hold" : "hold reopened on fresh turn");
            }

            if (mustRecoverThisTurn && workingPhase == ReconAirPhase.Outbound)
            {
                workingPhase = ReconAirPhase.Return;
                decisionReason = "must_recover: endurance deadline / no recovery plan remains";
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Return reason=must_recover "
                    + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(air)} airborneTurns={airborneTurns}");
            }

            // ---- opportunistic strike at the current hex ------------------------------------
            if (arrivalStrikeCheck && !atAirfield && EvaluateOpportunisticStrike(player, ctx, air).Favourable)
                return StepDecision.Strike("favourable strike at current hex");

            // ---- normal forward / return flow --------------------------------------------------
            ReconMode mode = AirReconModePolicy.RequestedMode(player, snapshot);
            if (ReconAssignmentRegistry.TryGet(player, armyId, out ReconAssignment existing))
                mode = existing.Mode;

            ReconAirStepPlanner.StepChoice? choice =
                ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie);

            if (!atAirfield && workingPhase == ReconAirPhase.Outbound && choice.HasValue)
            {
                float bestOutbound = Math.Max(sortie.BestOutboundStepScore, choice.Value.Score);
                nextBestOutboundScore = bestOutbound;
                int mpSlackAfterStep = air.CurrentMovement - choice.Value.RouteCost;
                bool marginalDrop = bestOutbound > 0.01f
                    && choice.Value.Score <= AiConfigV2.airReconTurningMarginalGainFloor * bestOutbound;
                bool returnReserve = choice.Value.RequiredTurns <= 1
                    && mpSlackAfterStep <= AiConfigV2.airReconTurningMpReserveSlack;
                if (returnReserve && canRemainAirborne && !mustRecoverThisTurn)
                {
                    decisionReason = "hold_airborne: two-turn endurance window, return deferred";
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound hold_airborne "
                        + $"reason=two_turn_window mpSlackAfter={mpSlackAfterStep} "
                        + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(air)} airborneTurns={airborneTurns}");
                    returnReserve = false;
                }
                if (marginalDrop || returnReserve)
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
                    choice = ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie);
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

            // §40–44 — Energy opportunity cost is charged once, at the launching activation. A wing
            // still on its own airfield about to take its first step this turn must clear the same
            // reserve a storage launch does.
            if (atAirfield && !air.HasActivatedThisTurn)
            {
                ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                    air.ActivationEnergyCost, choice.Value.Score, air.Id);
                AiDebugLog.Write(energy.ToLog($"actor=#{armyId}"));
                if (!energy.Allowed)
                    return StepDecision.Stop("air recon energy opportunity cost",
                        retireAssignment: true, removeReservation: true);
            }

            if (!AiTurnController.CanIssueMoveNow(root, player, air, ctx.Map, choice.Value.Hex))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} cannot afford/issue first step "
                    + $"AP{choice.Value.ActivationAp:0.#}/E{choice.Value.ActivationEnergy:0.#}; cancel/return");
                return StepDecision.Stop("air recon activation unaffordable",
                    retireAssignment: atAirfield, removeReservation: atAirfield);
            }

            bool pivotStep = workingPhase == ReconAirPhase.Turning;
            return StepDecision.Forward(choice.Value.Hex, choice.Value.LandingHex, mode,
                choice.Value.Score, pivotStep, choice.Value.Reason, decisionReason, nextBestOutboundScore);
        }

        // ==========================================================================================
        //  OPPORTUNISTIC STRIKE  (spec §46) — the DECISION only. ReconAirExecutor executes the
        //  AviationActions call; AirReconStepDirector.ResolveAfterStrike stamps the post-strike phase.
        // ==========================================================================================
        internal readonly struct StrikeAssessment
        {
            public readonly bool Favourable;
            public readonly float DamageFraction;
            public readonly float KillProbability;
            public readonly string SkipReason;

            public StrikeAssessment(bool favourable, float damageFraction, float killProbability, string skipReason)
            {
                Favourable = favourable;
                DamageFraction = damageFraction;
                KillProbability = killProbability;
                SkipReason = skipReason;
            }
        }

        internal static StrikeAssessment EvaluateOpportunisticStrike(PlayerSetupData player,
            AiTurnContext ctx, ArmyData air)
        {
            if (air == null || ctx == null || !AviationRules.IsValidAirArmy(air)
                || !AviationActions.CanStrikeAtCurrentHex(air))
                return new StrikeAssessment(false, 0f, 0f, "cannot_strike_here");

            if (AiAirSortiePlanner.KnownAaExposureAt(player, air.Hex) > 0)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                    + "decision=SKIP reason=known_aa_on_hex");
                return new StrikeAssessment(false, 0f, 0f, "known_aa_on_hex");
            }

            if (!AiAirSortiePlanner.TryReplan(air, ctx.Map, player).HasValue
                && !AiAirSortiePlanner.TryReplanMultiTurnReturn(air, ctx.Map, player).HasValue)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                    + "decision=SKIP reason=no_safe_return_before_strike");
                return new StrikeAssessment(false, 0f, 0f, "no_safe_return_before_strike");
            }

            float bestDamageFraction = 0f;
            float bestKillProb = 0f;
            foreach (ArmyData target in AviationCombatPresenter.FindAirStrikeTargetsAt(air.Hex, player))
            {
                if (target?.Owner == null || target.Owner == player)
                    continue;
                var visible = StealthSystem.TargetableMembersFor(target, player).ToList();
                if (visible.Count == 0)
                    continue;
                float totalHp = visible.Sum(m => Math.Max(1f, m.HitPointsCurrent));
                var profiles = visible.Select(WorthIt.FromLiveUnit).ToList();
                AviationCombatEstimator.AirStrikeEstimate est = AviationCombatEstimator.EstimateAirStrike(
                    air.Members, WorthIt.DefenseSum(visible), WorthIt.AttackSum(visible), profiles);
                float damageFraction = totalHp > 0.01f
                    ? (float)Math.Max(0.0, Math.Min(1.0, est.ExpectedDamage / totalHp))
                    : 0f;
                if (damageFraction > bestDamageFraction)
                {
                    bestDamageFraction = damageFraction;
                    bestKillProb = est.KillAnyProbability;
                }
            }

            bool favourable = bestDamageFraction >= AiConfigV2.airReconOpportunisticMinDamageFraction
                && bestKillProb >= AiConfigV2.airReconOpportunisticMinKillProbability;
            if (!favourable)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                    + $"decision=SKIP reason=estimate_unfavourable dmgFrac={bestDamageFraction:0.00} killP={bestKillProb:0.00}");
                return new StrikeAssessment(false, bestDamageFraction, bestKillProb, "estimate_unfavourable");
            }
            return new StrikeAssessment(true, bestDamageFraction, bestKillProb, null);
        }

        // Post-strike phase decision (spec §46 / AI-AIR-02). Called by the executor right after it
        // resolves the strike, on fully-settled live state. A second strike next turn is only an
        // OPTION: if the wing can still prove a safe airborne EndTurn + recovery, Hold; else Return.
        internal static void ResolveAfterStrike(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            ReconAirSortieState sortie, bool attacked)
        {
            if (sortie == null)
                return;
            sortie.MissionMode = ReconAirMissionMode.ReconStrike;

            bool safeReturnGone = air == null || !AviationRules.IsValidAirArmy(air)
                || (!AiAirSortiePlanner.TryReplan(air, ctx.Map, player).HasValue
                    && !AiAirSortiePlanner.TryReplanMultiTurnReturn(air, ctx.Map, player).HasValue);
            bool canRemainAfterStrike = !safeReturnGone
                && AiAirSortiePlanner.CanEndTurnHereAndRecover(air, ctx.Map, player);

            if (canRemainAfterStrike)
            {
                sortie.Phase = ReconAirPhase.Hold;
                sortie.LastDecisionReason = "hold_airborne_after_strike: second-strike window re-evaluated next turn";
            }
            else
            {
                sortie.Phase = ReconAirPhase.Return;
                sortie.LastDecisionReason = "return_after_strike: no safe airborne window remains";
            }

            if (safeReturnGone)
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air?.Id} attacked={attacked}; "
                    + "WARN no safe return after strike — next iteration will hold/seek any airfield");
            else
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} attacked={attacked}; "
                    + $"safe return preserved, sortie now {(canRemainAfterStrike ? "Hold (2-turn strike window)" : "Return")}");
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
                return FirstStep(map, air.Hex, landing);
            }

            MultiTurnSortie? multi = AiAirSortiePlanner.TryReplanMultiTurnReturn(air, map, player);
            if (multi.HasValue)
            {
                landing = ApplyLandingHysteresis(player, map, air, sortie, multi.Value.LandingHex, out string h);
                reason = $"multi-turn safest return t{multi.Value.RequiredTurns}" + h;
                if (landing.Equals(multi.Value.LandingHex))
                {
                    HexPath p = multi.Value.PathFromActionToLanding;
                    if (p != null && p.Hexes.Count > 1)
                        return p.Hexes[1];
                }
                return FirstStep(map, air.Hex, landing);
            }
            return null;
        }

        private static HexCoord ApplyLandingHysteresis(PlayerSetupData player, HexMap map, ArmyData air,
            ReconAirSortieState sortie, HexCoord candidate, out string reason)
        {
            if (sortie == null || !sortie.HasChosenLanding)
            {
                if (sortie != null) { sortie.ChosenLandingHex = candidate; sortie.HasChosenLanding = true; }
                reason = $" landing=adopt({candidate.Q},{candidate.R})";
                return candidate;
            }
            if (sortie.ChosenLandingHex.Equals(candidate))
            {
                reason = $" landing=keep({candidate.Q},{candidate.R})";
                return candidate;
            }
            if (!ReturnLandingStillViable(player, map, air, sortie.ChosenLandingHex))
            {
                reason = $" landing=switch(prev_unreachable ({sortie.ChosenLandingHex.Q},{sortie.ChosenLandingHex.R}) "
                    + $"-> ({candidate.Q},{candidate.R}))";
                sortie.ChosenLandingHex = candidate;
                return candidate;
            }

            int prevForward = AiAirSortiePlanner.NearestKnownEnemyDistance(player, sortie.ChosenLandingHex);
            int newForward = AiAirSortiePlanner.NearestKnownEnemyDistance(player, candidate);
            int prevCost = PathCostOrMax(map, air, sortie.ChosenLandingHex);
            int newCost = PathCostOrMax(map, air, candidate);
            bool muchMoreForward = prevForward != int.MaxValue && newForward != int.MaxValue
                && prevForward - newForward >= AiConfigV2.airReconLandingSwitchForwardMargin;
            bool muchCheaper = prevCost - newCost >= AiConfigV2.airReconLandingSwitchCostMargin;
            if (muchMoreForward || muchCheaper)
            {
                reason = $" landing=switch(forward {prevForward}->{newForward} cost {prevCost}->{newCost})";
                sortie.ChosenLandingHex = candidate;
                return candidate;
            }
            reason = $" landing=keep_hysteresis(prev ({sortie.ChosenLandingHex.Q},{sortie.ChosenLandingHex.R}) "
                + $"vs cand ({candidate.Q},{candidate.R}) forward {prevForward}/{newForward} cost {prevCost}/{newCost})";
            return sortie.ChosenLandingHex;
        }

        private static bool ReturnLandingStillViable(PlayerSetupData player, HexMap map, ArmyData air, HexCoord landing)
        {
            if (!AviationRules.IsOwnedAirfieldAt(landing, player))
                return false;
            if (AiAirSortiePlanner.FreeLandingCapacity(landing, player, air) < air.Members.Count)
                return false;
            HexPath path = HexPathfinder.FindPath(map, air.Hex, landing, flatCost: true);
            if (path == null)
                return false;
            if (AviationRules.PathMoveCost(air, path) > air.CurrentMovement)
                return false;
            int baseline = AiAirSortiePlanner.KnownAaExposureAt(player, air.Hex);
            return AiAirSortiePlanner.KnownAaExposure(player, path) - baseline <= 0;
        }

        private static int PathCostOrMax(HexMap map, ArmyData air, HexCoord landing)
        {
            HexPath path = HexPathfinder.FindPath(map, air.Hex, landing, flatCost: true);
            return path != null ? AviationRules.PathMoveCost(air, path) : int.MaxValue;
        }

        private static HexCoord? FirstStep(HexMap map, HexCoord from, HexCoord to)
        {
            if (from.Equals(to)) return to;
            HexPath path = HexPathfinder.FindPath(map, from, to, flatCost: true);
            return path != null && path.Hexes.Count > 1 ? path.Hexes[1] : (HexCoord?)null;
        }
    }
}
