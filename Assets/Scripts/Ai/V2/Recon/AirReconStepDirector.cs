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
            if (!air.Hex.Equals(sortie.LaunchHex))
            {
                sortie.ClaimedSector = ReconDirectionModel.Sector(sortie.LaunchHex, air.Hex);
                sortie.HasClaim = true;
            }
        }

        // An owned airfield ends the sortie only once the planner has turned the flight home.
        // Crossing an intermediate friendly airfield during Outbound remains a normal route step.
        internal static bool CompletesAtAirfield(ReconAirSortieState sortie, bool atAirfield,
            bool hasDeparted) =>
            sortie != null && atAirfield && hasDeparted && sortie.Phase == ReconAirPhase.Return;

        // Mark this AI turn as processed. Used to allow one stationary strike check on each new turn.
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
    // energy / affordability gates, and the opportunistic-strike arbitration (favourable estimate,
    // KNOWN-AA, safe-return proof). It reads LIVE world state on every call — live replanning is
    // allowed, but it happens in the planner, not the executor. ReconAirExecutor only issues the
    // canonical Move / Strike / assignment-bookkeeping calls the returned decision names.
    internal static class AirReconStepDirector
    {
        internal enum StepKind
        {
            Stop,
            Strike,
            ReturnStep,
            ForwardStep,
        }

        internal readonly struct StepDecision
        {
            public readonly StepKind Kind;
            public readonly HexCoord Step;
            public readonly HexCoord LandingHex;
            public readonly ReconMode Mode;
            public readonly float StepScore;
            public readonly string Reason;
            public readonly bool AlsoInformative;
            public readonly bool PivotToReturnAfterMove;
            public readonly bool RetireAssignment;
            public readonly bool RemoveReservation;
            public readonly ReconAirPhase? NextPhase;
            public readonly string NextDecisionReason;
            public readonly float? NextBestOutboundScore;
            public readonly bool SetChosenLanding;

            private StepDecision(StepKind kind, HexCoord step, HexCoord landing, ReconMode mode,
                float stepScore, string reason, bool alsoInformative, bool pivotToReturnAfterMove,
                bool retireAssignment, bool removeReservation, ReconAirPhase? nextPhase,
                string nextDecisionReason, float? nextBestOutboundScore, bool setChosenLanding)
            {
                Kind = kind;
                Step = step;
                LandingHex = landing;
                Mode = mode;
                StepScore = stepScore;
                Reason = reason;
                AlsoInformative = alsoInformative;
                PivotToReturnAfterMove = pivotToReturnAfterMove;
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
                    retireAssignment, removeReservation, null, null, null, false);

            public static StepDecision Strike(string reason) =>
                new StepDecision(StepKind.Strike, default, default, default, 0f, reason, false, false,
                    false, false, null, null, null, false);

            public static StepDecision Return(HexCoord step, HexCoord landing, bool alsoInformative,
                string reason, string nextDecisionReason, float? nextBestOutboundScore) =>
                new StepDecision(StepKind.ReturnStep, step, landing, default, 0f, reason,
                    alsoInformative, false, false, false, ReconAirPhase.Return,
                    nextDecisionReason, nextBestOutboundScore, true);

            public static StepDecision Forward(HexCoord step, HexCoord landing, ReconMode mode,
                float score, bool pivot, string reason, string nextDecisionReason,
                float? nextBestOutboundScore) =>
                new StepDecision(StepKind.ForwardStep, step, landing, mode, score, reason, false,
                    pivot, false, false, pivot ? ReconAirPhase.Return : (ReconAirPhase?)null,
                    nextDecisionReason, nextBestOutboundScore, true);
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
            bool arrivalStrikeCheck, HexCoord? missionFocusHex = null)
        {
            int armyId = air.Id;
            bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);

            ReconAirPhase workingPhase = sortie.Phase;
            string decisionReason = null;
            float? nextBestOutboundScore = null;
            bool pivotAfterForward = false;

            // One source of truth for fuel: when no safe unlanded EndTurn remains, this turn must
            // finish on an owned airfield. No frozen launch cap or elapsed-turn counter is involved.
            int safeEnds = AviationRange.SafeUnlandedEndsRemaining(air);
            bool mustLandThisTurn = !atAirfield && safeEnds <= 0;
            if (mustLandThisTurn && workingPhase == ReconAirPhase.Outbound)
            {
                workingPhase = ReconAirPhase.Return;
                decisionReason = "must_recover: no safe unlanded end remains";
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Return "
                    + $"reason=must_recover safeEnds={safeEnds}");
            }

            // An attack does not consume MP or end the flight. Check once on arrival and once at the
            // start of every later airborne turn; after the strike the same planner continues moving.
            if ((arrivalStrikeCheck || newTurn) && !atAirfield
                && EvaluateOpportunisticStrike(player, ctx, air).Favourable)
                return StepDecision.Strike("enemy at current hex and recovery remains possible");

            ReconMode mode = AirReconModePolicy.RequestedMode(player, snapshot);
            if (ReconPatrolStateRegistry.TryGet(player, armyId, out ReconPatrolState existing))
                mode = existing.Mode;

            ReconAirStepPlanner.StepChoice? choice =
                ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie,
                    missionFocusHex: missionFocusHex);

            if (!atAirfield && workingPhase == ReconAirPhase.Outbound && choice.HasValue)
            {
                float bestOutbound = Math.Max(sortie.BestOutboundStepScore, choice.Value.Score);
                nextBestOutboundScore = bestOutbound;
                bool marginalDrop = bestOutbound > 0.01f
                    && choice.Value.Score <= AiConfigV2.airReconTurningMarginalGainFloor * bestOutbound;

                if (marginalDrop)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Turning "
                        + $"reason=marginal_gain stepScore={choice.Value.Score:0.00} best={bestOutbound:0.00}");
                    workingPhase = ReconAirPhase.Turning;
                }
            }

            bool forwardStepUseful = choice.HasValue
                && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;

            if (!atAirfield && workingPhase == ReconAirPhase.Turning)
            {
                ReconAirPhase saved = sortie.Phase;
                sortie.Phase = ReconAirPhase.Turning;
                try
                {
                    choice = ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber,
                        sortie, missionFocusHex: missionFocusHex);
                }
                finally
                {
                    sortie.Phase = saved;
                }
                forwardStepUseful = choice.HasValue
                    && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                if (!forwardStepUseful)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} "
                        + "phase=Turning->Return reason=no_recoverable_pivot");
                    workingPhase = ReconAirPhase.Return;
                }
            }

            if (!atAirfield && workingPhase == ReconAirPhase.Outbound && !forwardStepUseful)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} "
                    + "phase=Outbound->Return reason=no_recoverable_forward_step");
                workingPhase = ReconAirPhase.Return;
            }

            bool mustReturn = !atAirfield
                && (workingPhase == ReconAirPhase.Return || !forwardStepUseful);
            if (mustReturn)
            {
                HexCoord? returnStep = PickReturnStep(player, ctx.Map, air, sortie, out HexCoord landing,
                    out string returnReason);
                if (!returnStep.HasValue)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Return] actor=#{armyId} at "
                        + $"({air.Hex.Q},{air.Hex.R}) — no reachable owned-airfield step");
                    return StepDecision.Stop("no reachable owned-airfield step");
                }
                bool alsoInformative = choice.HasValue && choice.Value.Hex.Equals(returnStep.Value)
                    && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                return StepDecision.Return(returnStep.Value, landing, alsoInformative, returnReason,
                    decisionReason, nextBestOutboundScore);
            }

            if (!forwardStepUseful)
                return StepDecision.Stop("no useful recoverable forward step");

            if (!AiTurnController.CanIssueMoveNow(root, air, ctx.Map, choice.Value.Hex))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} cannot afford/issue step "
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

            // The only strategic safety gate is fuel/recovery. Anti-air remains a physical gameplay
            // reaction at execution time; the AI does not pre-avoid it.
            if (!AiAirSortiePlanner.TryReplan(air, ctx.Map, player).HasValue
                && !AiAirSortiePlanner.TryReplanMultiTurnReturn(air, ctx.Map, player).HasValue)
                return new StrikeAssessment(false, 0f, 0f, "no_recovery_route");

            float bestDamageFraction = 0f;
            float bestKillProb = 0f;
            bool hasTarget = false;
            foreach (ArmyData target in AviationCombatPresenter.FindAirStrikeTargetsAt(air.Hex, player))
            {
                if (target?.Owner == null || target.Owner == player)
                    continue;
                var visible = StealthSystem.TargetableMembersFor(target, player).ToList();
                if (visible.Count == 0)
                    continue;
                hasTarget = true;
                float totalHp = visible.Sum(m => Math.Max(1f, m.HitPointsCurrent));
                var profiles = visible.Select(WorthIt.FromLiveUnit).ToList();
                AviationCombatEstimator.AirStrikeEstimate est = AviationCombatEstimator.EstimateAirStrike(
                    air.Members, WorthIt.DefenseSum(visible), WorthIt.AttackSum(visible), profiles);
                bestDamageFraction = Math.Max(bestDamageFraction,
                    totalHp > 0.01f ? (float)Math.Max(0.0, Math.Min(1.0, est.ExpectedDamage / totalHp)) : 0f);
                bestKillProb = Math.Max(bestKillProb, est.KillAnyProbability);
            }
            return hasTarget
                ? new StrikeAssessment(true, bestDamageFraction, bestKillProb, null)
                : new StrikeAssessment(false, 0f, 0f, "no_visible_target");
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
            sortie.LastDecisionReason = attacked
                ? "strike_completed: continue flight with remaining MP"
                : "strike_attempted: continue flight";
            AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air?.Id} attacked={attacked}; "
                + "attack consumed no MP; sortie remains under normal endurance/route planning");
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
            if (!ReturnLandingStillViable(player, map, air, sortie.ChosenLandingHex))
            {
                reason = $" landing=switch(prev_unreachable ({sortie.ChosenLandingHex.Q},{sortie.ChosenLandingHex.R}) "
                    + $"-> ({candidate.Q},{candidate.R}))";
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
