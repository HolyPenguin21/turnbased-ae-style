using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

using Game.Combat;

namespace Game.Ai.V2
{
    // ReconOnly Air Recon operational pass. It deliberately runs AFTER provisioned missions: air is
    // an information fallback, not a way to steal an aircraft from higher-priority work. No cached
    // route survives a transition. Every outbound AND return move is exactly one adjacent hex,
    // resolved by the existing authoritative aviation/movement paths, followed by live
    // visibility/IntelAge refresh and a fresh safety/reward decision.
    //
    // AiTaskKind.AirRecon is retained only as the EXISTING landing-slot reservation primitive.
    // V2 never calls AiAirSortiePlanner.ContinueSortie for these actors: ReconAssignment + the live
    // planner own direction/mode, while the task contributes only LandingHex capacity ownership and
    // the ordinary first-move Energy-reservation seam already understood by AiAirSortiePlanner.
    internal static class ReconAirExecutor
    {
        // Per-turn air-recon actor cap and the storage launch-subset rule now live in the shared
        // ReconAirCapacityPolicy so ReconCapacitySnapshot enforces the exact same limits.

        // ARCH-02 §35 — EXECUTION ONLY. It receives an AirReconPlan (built by AirReconPlanner
        // before this stage: actor discovery/selection, ReconMode, launch-subset, first-step gate
        // and energy policy are all already decided) and flies it. It does not discover actors,
        // choose a mode, pick a launch subset or decide whether a sortie is worthwhile.
        public static IEnumerator Execute(AirReconPlan plan, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot)
        {
            if (plan == null || player == null || root == null || ctx?.Map == null || snapshot?.Self == null)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] exec — not reached ({plan?.Summary ?? "no plan"})");
                yield break;
            }
            AiDebugLog.Write($"[AI][V2][Recon][Air] exec — {plan.Summary}");

            foreach (int id in plan.ContinueActorIds)
            {
                ArmyData air = Resolve(player, id);
                if (air != null && AviationRules.IsValidAirArmy(air) && air.Controller != null
                    && air.CurrentMovement > 0 && !AviationRules.IsOwnedAirfieldAt(air.Hex, player))
                    yield return RunActor(player, root, ctx, snapshot, air);
            }

            foreach (int id in plan.ReadyActorIds)
            {
                ArmyData air = Resolve(player, id);
                if (air != null && AviationRules.IsValidAirArmy(air) && air.Controller != null
                    && air.CurrentMovement > 0)
                    yield return RunActor(player, root, ctx, snapshot, air);
            }

            foreach (AirLaunchPlan lp in plan.Launches)
                yield return LaunchOne(lp, player, root, ctx, snapshot);
        }

        // Fly one planned launch. The stale-plan guard (CanAffordLaunch re-check) mirrors §35:
        // if an earlier sortie this pass consumed the AP/Energy, this launch is skipped and
        // reported — the executor does NOT re-plan a different subset or airfield.
        private static IEnumerator LaunchOne(AirLaunchPlan lp, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot)
        {
            if (lp?.Subset == null || lp.Subset.Count == 0)
                yield break;
            if (!AiAirSortiePlanner.CanAffordLaunch(root, player, lp.Subset))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— planned launch no longer affordable (earlier sortie spent it); skip, no replan");
                yield break;
            }

            ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                lp.LaunchEnergy, lp.Score, excludeArmyId: -1);
            AiDebugLog.Write(energy.ToLog($"airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R})"));
            if (!energy.Allowed)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— energy reserve now rejects the planned launch; skip, no replan");
                yield break;
            }

            bool firstVisitedBefore = VisionSystem.IsVisited(player, lp.FirstStepHex);
            var beforeIds = new HashSet<int>(ArmyRegistry.AllForOwner(player)
                .Where(AviationRules.IsValidAirArmy).Select(a => a.Id));
            var launchDecision = new AiDecision
            {
                Kind = AiActionKind.LaunchAirRecon,
                ExistingArmy = null,
                TargetHex = lp.AirfieldHex,
                AircraftToLaunch = lp.Subset,
                AirActionHex = lp.FirstStepHex,
                AirLandingHex = lp.LandingHex,
                Score = lp.Score,
                Reason = $"V2 Air Recon — {lp.Mode} one-step launch; {lp.Reason}",
            };

            yield return AiAirSortiePlanner.LaunchRoutine(player, launchDecision, ctx, AirSortieKind.Recon);

            ArmyData launched = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a) && !beforeIds.Contains(a.Id))
                .OrderBy(a => a.Id)
                .FirstOrDefault();
            if (launched == null)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) "
                    + "— launch formed no aircraft");
                yield break;
            }

            AirSortie reservationTask = AirSortieRegistry.ForArmy(player, launched);
            if (launched.Hex.Equals(lp.AirfieldHex))
            {
                RemoveAirReconReservation(player, launched);
                AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] actor=#{launched.Id} launch formed but "
                    + "first step made no progress; V2 assignment not started");
                yield break;
            }

            if (reservationTask != null && reservationTask.Kind == AirSortieKind.Recon)
            {
                reservationTask.Outbound = true;
                reservationTask.TargetHex = lp.FirstStepHex;
                reservationTask.LandingHex = lp.LandingHex;
            }

            ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, launched.Id,
                lp.AirfieldHex, lp.FirstStepHex, lp.Mode, ctx.TurnNumber);
            ReconAssignmentRegistry.MarkProgress(player, launched.Id, ctx.TurnNumber);
            ReconAirSortieState launchSortie = ReconAirSortieRegistry.GetOrCreate(player, launched.Id, lp.AirfieldHex);
            launchSortie.LaunchTurn = ctx.TurnNumber;
            launchSortie.RecordStep(launched.Hex);
            launchSortie.BestOutboundStepScore = Math.Max(launchSortie.BestOutboundStepScore, lp.Score);
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            StampObservedFootprint(player, ctx, launched, launched.Hex);
            LogVisitedInvariant(player, lp.FirstStepHex, firstVisitedBefore, "storage-launch-first-step");
            AiDebugLog.Write($"[AI][V2][Recon][Air][Handoff] actor=#{launched.Id} "
                + $"launch=({lp.AirfieldHex.Q},{lp.AirfieldHex.R}) first=({launched.Hex.Q},{launched.Hex.R}) "
                + $"mode={assignment.Mode}; V1 task retained only as landing-slot reservation");

            if (launched.Controller != null && launched.CurrentMovement > 0
                && !AviationRules.IsOwnedAirfieldAt(launched.Hex, player))
                yield return RunActor(player, root, ctx, snapshot, launched, initialStepAlreadyMoved: true);
        }

        private static IEnumerator RunActor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData initial, Action<bool> movedCallback = null,
            bool initialStepAlreadyMoved = false)
        {
            bool movedAny = initialStepAlreadyMoved;
            int armyId = initial.Id;
            int guard = Math.Max(2, initial.CurrentMovement + 2);

            while (guard-- > 0)
            {
                ArmyData air = Resolve(player, armyId);
                if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
                {
                    ReconAssignmentRegistry.Retire(player, armyId, "air mover lost / invalid");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    if (air != null) RemoveAirReconReservation(player, air);
                    break;
                }

                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
                    break;

                bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
                if (atAirfield && movedAny)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Landing at "
                        + $"({air.Hex.Q},{air.Hex.R}); sortie complete");
                    ReconAssignmentRegistry.Retire(player, armyId, "air recon landed");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    RemoveAirReconReservation(player, air);
                    break;
                }
                if (air.CurrentMovement <= 0)
                    break;

                ReconAirSortieState sortie = ReconAirSortieRegistry.GetOrCreate(player, armyId, air.Hex);
                if (sortie.LaunchTurn < 0)
                    // First time this sortie is seen without an authoritative launch turn. On its
                    // own airfield this IS the launch turn; already airborne means it left on an
                    // earlier turn (a storage launch that spent all its MP before RunActor, or a V1
                    // handoff) — treat it as at least its second airborne turn so recovery is
                    // considered rather than deferred a turn.
                    sortie.LaunchTurn = atAirfield ? ctx.TurnNumber : ctx.TurnNumber - 1;
                int airborneTurns = sortie.AirborneTurnsElapsed(ctx.TurnNumber);
                if (!air.Hex.Equals(sortie.LaunchHex))
                {
                    sortie.ClaimedSector = ReconDirectionModel.Sector(sortie.LaunchHex, air.Hex);
                    sortie.HasClaim = true;
                }

                // AI-AIR-02 — persistent-plan bookkeeping + the real endurance deadline, re-derived
                // live every decision from the shared aviation rules (never a cached copy).
                bool newTurn = sortie.BeginTurn(ctx.TurnNumber);
                bool canRemainAirborne = !atAirfield
                    && AiAirSortiePlanner.CanEndTurnHereAndRecover(air, ctx.Map, player);
                // MustRecoverThisTurn — the real multi-turn endurance deadline: the wing has ALREADY
                // spent at least one turn-end aloft and can no longer prove another safe airborne
                // EndTurn plus its mandatory return. Only then is Return a hard priority forced at
                // turn start. On the launch turn (airborneTurns == 0) this never fires — the
                // ordinary same-turn boomerang logic below still governs, so a plane
                // (SafeUnlandedEndsRemaining == 0, always a same-turn round trip) is unchanged.
                bool mustRecoverThisTurn = !atAirfield && airborneTurns >= 1 && !canRemainAirborne;
                sortie.MustRecoverThisTurn = mustRecoverThisTurn;

                // A Hold set on a PREVIOUS turn (aloft on purpose, deferring return) re-opens now
                // with fresh movement; a Hold set earlier THIS turn ends the sortie's turn here.
                if (sortie.Phase == ReconAirPhase.Hold)
                {
                    if (!newTurn)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Hold — ending turn aloft; "
                            + $"airborneTurns={airborneTurns} reason={sortie.LastDecisionReason}");
                        break;
                    }
                    // AI-AIR-02 spec — a Hold is a deliberate airborne pause (typically right after
                    // a strike). On the fresh turn, RE-EVALUATE the same-hex strike opportunity
                    // BEFORE moving off: per-turn attack availability has refreshed
                    // (HasAirAttackedThisTurn cleared), the target may still be sitting here. The
                    // second strike is only an option — TryOpportunisticAirStrike runs its own full
                    // favourable + safe-return gate and will itself set Phase (Hold again / Return)
                    // if it actually fires.
                    yield return TryOpportunisticAirStrike(player, ctx, air, sortie);
                    air = Resolve(player, armyId);
                    if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
                    {
                        ReconAssignmentRegistry.Retire(player, armyId, "air mover lost / invalid");
                        ReconAirSortieRegistry.Retire(player, armyId);
                        break;
                    }
                    // Whatever the re-eval left it as, a Hold now resolves for THIS turn: recover if
                    // the endurance deadline has arrived, otherwise resume Outbound on fresh MP.
                    if (sortie.Phase == ReconAirPhase.Hold)
                    {
                        sortie.Phase = mustRecoverThisTurn ? ReconAirPhase.Return : ReconAirPhase.Outbound;
                        if (mustRecoverThisTurn)
                        {
                            sortie.LastDecisionReason = "must_recover: endurance deadline after hold";
                            AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Hold->Return reason=must_recover "
                                + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(air)} airborneTurns={airborneTurns}");
                        }
                    }
                }

                if (mustRecoverThisTurn && sortie.Phase == ReconAirPhase.Outbound)
                {
                    sortie.Phase = ReconAirPhase.Return;
                    sortie.LastDecisionReason = "must_recover: endurance deadline / no recovery plan remains";
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Return reason=must_recover "
                        + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(air)} airborneTurns={airborneTurns}");
                }

                ReconMode mode = RequestedMode(player, snapshot);
                if (ReconAssignmentRegistry.TryGet(player, armyId, out ReconAssignment existing))
                    mode = existing.Mode;

                ReconAirStepPlanner.StepChoice? choice =
                    ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie);

                // Phase transitions (spec §34). While still Outbound, a single pivot step is taken
                // once marginal information gain has dropped or the MP left after the step would
                // barely cover the proven return; after that the sortie is Return-bound.
                if (!atAirfield && sortie.Phase == ReconAirPhase.Outbound && choice.HasValue)
                {
                    sortie.BestOutboundStepScore = Math.Max(sortie.BestOutboundStepScore, choice.Value.Score);
                    int mpSlackAfterStep = air.CurrentMovement - choice.Value.RouteCost;
                    bool marginalDrop = sortie.BestOutboundStepScore > 0.01f
                        && choice.Value.Score <= AiConfigV2.airReconTurningMarginalGainFloor * sortie.BestOutboundStepScore;
                    // The MP-reserve pivot is a same-turn boomerang concept only. A deliberate
                    // multi-turn sortie keeps its own fuel-safety proof in AiAviationSupport and
                    // must not be turned back after one step.
                    bool returnReserve = choice.Value.RequiredTurns <= 1
                        && mpSlackAfterStep <= AiConfigV2.airReconTurningMpReserveSlack;
                    // AI-AIR-02 core invariant — the wing must NOT turn home this turn just because
                    // it has the MP to. Suppress the return-reserve pivot while it can prove it will
                    // legally end this turn aloft AND still make its mandatory return afterwards: its
                    // two-turn endurance is a real tactical window. marginal_gain still pivots — that
                    // trigger is about running out of useful things to see, not about fuel.
                    if (returnReserve && canRemainAirborne && !mustRecoverThisTurn)
                    {
                        sortie.LastDecisionReason = "hold_airborne: two-turn endurance window, return deferred";
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound hold_airborne "
                            + $"reason=two_turn_window mpSlackAfter={mpSlackAfterStep} "
                            + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(air)} airborneTurns={airborneTurns}");
                        returnReserve = false;
                    }
                    if (marginalDrop || returnReserve)
                    {
                        string why = returnReserve ? "return_reserve" : "marginal_gain";
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Turning reason={why} "
                            + $"stepScore={choice.Value.Score:0.00} best={sortie.BestOutboundStepScore:0.00} "
                            + $"mpSlackAfter={mpSlackAfterStep}");
                        sortie.Phase = ReconAirPhase.Turning;
                    }
                }

                bool forwardStepUseful = choice.HasValue
                    && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;

                // The Turning pivot is one real lateral step (boomerang bend), not an instant
                // U-turn. It re-Picks with the pivot's stronger lateral weighting; if no safe
                // informative pivot exists the sortie just goes Return this iteration.
                if (!atAirfield && sortie.Phase == ReconAirPhase.Turning)
                {
                    choice = ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie);
                    forwardStepUseful = choice.HasValue
                        && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                    if (!forwardStepUseful)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning->Return reason=no_safe_pivot");
                        sortie.Phase = ReconAirPhase.Return;
                    }
                }

                if (!atAirfield && sortie.Phase == ReconAirPhase.Outbound && !forwardStepUseful)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Outbound->Return reason=no_safe_forward_step");
                    sortie.Phase = ReconAirPhase.Return;
                }

                bool mustReturn = !atAirfield
                    && (sortie.Phase == ReconAirPhase.Return || !forwardStepUseful);
                if (mustReturn)
                {
                    HexCoord? returnStep = PickReturnStep(player, ctx.Map, air, sortie, out HexCoord landing,
                        out string returnReason);
                    if (!returnStep.HasValue)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air][Return] actor=#{armyId} at "
                            + $"({air.Hex.Q},{air.Hex.R}) — no reachable owned-airfield step; hold position");
                        break;
                    }

                    AirSortie reservation = EnsureAirReconReservation(player, air, landing, outbound: false);
                    if (reservation == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} return blocked — another task owns aircraft");
                        break;
                    }
                    // §34 — a Return step may still be informative when it coincides with the safe
                    // way home, but never a detour that risks the landing: the step itself is always
                    // returnStep.
                    bool alsoInformative = choice.HasValue && choice.Value.Hex.Equals(returnStep.Value)
                        && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Return "
                        + $"({air.Hex.Q},{air.Hex.R})->({returnStep.Value.Q},{returnStep.Value.R}) "
                        + $"landing=({landing.Q},{landing.R}) informative={(alsoInformative ? 1 : 0)} {returnReason}");
                    bool moved = false;
                    yield return MoveOne(player, ctx, air, returnStep.Value, "V2 Air Recon — safe return",
                        () => moved = true);
                    movedAny |= moved;
                    if (!moved) break;
                    ArmyData afterReturn = Resolve(player, armyId);
                    if (afterReturn != null) sortie.RecordStep(afterReturn.Hex);
                    yield return TryOpportunisticAirStrike(player, ctx, afterReturn, sortie);
                    ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (!forwardStepUseful)
                    break;

                // §40–44 — Energy opportunity cost is charged once, at the launching activation.
                // A wing already airborne this turn has paid it; a wing sitting on its own airfield
                // about to take its first step this turn must clear the same reserve a storage
                // launch does.
                if (atAirfield && !air.HasActivatedThisTurn)
                {
                    ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                        air.ActivationEnergyCost, choice.Value.Score, air.Id);
                    AiDebugLog.Write(energy.ToLog($"actor=#{armyId}"));
                    if (!energy.Allowed)
                    {
                        ReconAssignmentRegistry.Retire(player, armyId, "air recon energy opportunity cost");
                        RemoveAirReconReservation(player, air);
                        break;
                    }
                }

                ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, armyId, air.Hex,
                    choice.Value.Hex, mode, ctx.TurnNumber);
                mode = assignment.Mode;
                AirSortie reservationTask = EnsureAirReconReservation(player, air,
                    choice.Value.LandingHex, outbound: true, target: choice.Value.Hex);
                if (reservationTask == null)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} step blocked — another task owns aircraft");
                    break;
                }
                // Track the planner's latest outbound landing so the first Return-phase step has a
                // baseline to apply landing hysteresis against (spec §38).
                sortie.ChosenLandingHex = choice.Value.LandingHex;
                sortie.HasChosenLanding = true;

                if (!AiTurnController.CanIssueMoveNow(root, player, air, ctx.Map, choice.Value.Hex))
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} cannot afford/issue first step "
                        + $"AP{choice.Value.ActivationAp:0.#}/E{choice.Value.ActivationEnergy:0.#}; cancel/return");
                    if (atAirfield)
                    {
                        ReconAssignmentRegistry.Retire(player, armyId, "air recon activation unaffordable");
                        RemoveAirReconReservation(player, air);
                    }
                    break;
                }

                bool stepMoved = false;
                yield return MoveOne(player, ctx, air, choice.Value.Hex,
                    $"V2 Air Recon — {mode} {sortie.Phase} one-step live replan", () => stepMoved = true);
                movedAny |= stepMoved;
                if (!stepMoved) break;
                ArmyData afterStep = Resolve(player, armyId);
                if (afterStep != null) sortie.RecordStep(afterStep.Hex);
                if (sortie.Phase == ReconAirPhase.Turning)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning->Return pivot step taken");
                    sortie.Phase = ReconAirPhase.Return;
                }
                yield return TryOpportunisticAirStrike(player, ctx, afterStep, sortie);
                ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
            }

            // A ready wing that was evaluated this pass but never actually left its airfield must
            // not leave a half-initialised sortie state behind — AirborneTurnsElapsed would later
            // age it into a phantom airborne lifetime and force a premature must_recover once it
            // finally does launch (AI-AIR-02 review P1 — lifecycle drift).
            ArmyData settled = Resolve(player, armyId);
            if (!movedAny && settled != null && AviationRules.IsOwnedAirfieldAt(settled.Hex, player))
                ReconAirSortieRegistry.Retire(player, armyId);

            movedCallback?.Invoke(movedAny);
        }

        // §46 — opportunistic air attack. Called after a completed air step, on fully-settled live
        // state. AirRecon never chose this sortie in order to attack; it only strikes a target that
        // is honestly visible on its own hex, favourable under the shared estimator, not under known
        // AA, and only while a safe landing still provably exists both before and after. No
        // reinforcement, no pursuit — the sortie turns for home afterward.
        private static IEnumerator TryOpportunisticAirStrike(PlayerSetupData player, AiTurnContext ctx,
            ArmyData air, ReconAirSortieState sortie)
        {
            if (air == null || ctx == null || !AviationRules.IsValidAirArmy(air))
                yield break;
            if (!AviationActions.CanStrikeAtCurrentHex(air))
                yield break;

            if (AiAirSortiePlanner.KnownAaExposureAt(player, air.Hex) > 0)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                    + "decision=SKIP reason=known_aa_on_hex");
                yield break;
            }

            // A strike costs no movement, so return feasibility should be unchanged — but verify a
            // safe landing exists at all before committing to reveal ourselves.
            if (!AiAirSortiePlanner.TryReplan(air, ctx.Map, player).HasValue
                && !AiAirSortiePlanner.TryReplanMultiTurnReturn(air, ctx.Map, player).HasValue)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                    + "decision=SKIP reason=no_safe_return_before_strike");
                yield break;
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
                yield break;
            }

            AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
            if (presenter == null)
                yield break;

            AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                + $"decision=STRIKE dmgFrac={bestDamageFraction:0.00} killP={bestKillProb:0.00}");
            var result = new AviationCombatPresenter.AirStrikeResult();
            yield return AviationActions.ResolveStationaryStrike(presenter, air, result);

            ArmyData afterStrike = Resolve(player, air.Id);
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            bool safeReturnGone = afterStrike == null || !AviationRules.IsValidAirArmy(afterStrike)
                || (!AiAirSortiePlanner.TryReplan(afterStrike, ctx.Map, player).HasValue
                    && !AiAirSortiePlanner.TryReplanMultiTurnReturn(afterStrike, ctx.Map, player).HasValue);

            // AI-AIR-02 — a second strike next turn is only an OPTION, never forced. If the wing can
            // still prove it will legally end this turn aloft AND recover afterwards, hold airborne
            // and let a fresh state evaluation next turn decide (strike again, or turn for home).
            // Otherwise the reveal means turn for home now.
            bool canRemainAfterStrike = !safeReturnGone
                && AiAirSortiePlanner.CanEndTurnHereAndRecover(afterStrike, ctx.Map, player);
            if (sortie != null)
            {
                sortie.MissionMode = ReconAirMissionMode.ReconStrike;
                if (canRemainAfterStrike)
                {
                    sortie.Phase = ReconAirPhase.Hold;
                    sortie.LastDecisionReason = "hold_airborne_after_strike: second-strike window re-evaluated next turn";
                }
                else
                {
                    sortie.Phase = ReconAirPhase.Return; // reveal happened, no safe window — live replan next iteration
                    sortie.LastDecisionReason = "return_after_strike: no safe airborne window remains";
                }
            }

            if (safeReturnGone)
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} attacked={result.Attacked}; "
                    + "WARN no safe return after strike — next iteration will hold/seek any airfield");
            else
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} attacked={result.Attacked}; "
                    + $"safe return preserved, sortie now {(canRemainAfterStrike ? "Hold (2-turn strike window)" : "Return")}");
        }

        private static IEnumerator MoveOne(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            HexCoord next, string reason, Action onMoved)
        {
            HexCoord before = air.Hex;
            bool visitedBefore = VisionSystem.IsVisited(player, next);
            var decision = AiDecision.Move(air, next, reason, 0f);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            ArmyData live = Resolve(player, air.Id);
            HexCoord after = live != null ? live.Hex : trace.EndHex;
            if (!after.Equals(before))
                onMoved?.Invoke();

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            LogVisitedInvariant(player, next, visitedBefore, "live-step");
            // AI-AIR-01 §5 — stamp the whole observed FOOTPRINT, not just the hex the wing landed
            // on, so a later route's RedundancyPenalty / hard "repeats a recent air observation"
            // reject reflects what was actually seen (a parallel route one hex over covers nearly
            // the same ground). V2 never calls AiAirSortiePlanner.ContinueSortie, the only V1 stamper.
            if (!after.Equals(before))
                StampObservedFootprint(player, ctx, air, after);
            AiDebugLog.Write($"[AI][V2][Recon][Air][Observe] actor=#{air.Id} "
                + $"({before.Q},{before.R})->({after.Q},{after.R}) intel refreshed; groundVisitedWrite=0");
        }

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

            MultiTurnSortie? multi =
                AiAirSortiePlanner.TryReplanMultiTurnReturn(air, map, player);
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

        // Spec §38 — once a sortie has locked a landing base, keep it across steps unless it is no
        // longer a viable return target, or the fresh candidate is clearly better (materially more
        // forward, or materially cheaper on the remaining route). Prevents airfield A<->B ping-pong
        // on the way home while still letting a genuinely superior base win.
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

        private static AirSortie EnsureAirReconReservation(PlayerSetupData player, ArmyData air,
            HexCoord landing, bool outbound, HexCoord? target = null)
        {
            AirSortie task = AirSortieRegistry.ForArmy(player, air);
            if (task == null)
            {
                task = new AirSortie { Kind = AirSortieKind.Recon, Army = air };
                AirSortieRegistry.Add(player, task);
            }
            if (task.Kind != AirSortieKind.Recon)
                return null;

            task.Outbound = outbound;
            task.LandingHex = landing;
            task.TargetHex = target ?? landing;
            return task;
        }

        private static void RemoveAirReconReservation(PlayerSetupData player, ArmyData air)
        {
            AirSortie task = air != null ? AirSortieRegistry.ForArmy(player, air) : null;
            if (task == null || task.Kind != AirSortieKind.Recon)
                return;
            AirSortieRegistry.Remove(player, task);
        }

        private static void LogVisitedInvariant(PlayerSetupData player, HexCoord hex, bool visitedBefore, string phase)
        {
            bool visitedAfter = VisionSystem.IsVisited(player, hex);
            if (!visitedBefore && visitedAfter)
                AiDebugLog.Write($"[AI][V2][Recon][Air][INVARIANT-FAIL] phase={phase} aircraft unexpectedly "
                    + $"marked ground Visited at ({hex.Q},{hex.R})");
        }

        // AI-AIR-01 §5 — record the whole on-map vision footprint of a completed air step as
        // recently-air-observed, TAGGED WITH THIS SORTIE, so AirReconRouteScorer's recent-coverage
        // overlap sees the ground a sortie actually swept (not just its centre hex) without a wing
        // blocking its own advance on the footprint it just laid down.
        private static void StampObservedFootprint(PlayerSetupData player, AiTurnContext ctx,
            ArmyData air, HexCoord center)
        {
            int sortieId = ReconAirSortieRegistry.TryGet(player, air.Id, out ReconAirSortieState st)
                ? st.SortieId : -1;
            int vision = (ctx?.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + AbilityParams.GetBestRecceRadius(air);
            foreach (HexCoord h in HexGridMath.HexesInRange(center, Math.Max(0, vision)))
                if (ctx?.Map != null && ctx.Map.TryGetTerrainAt(h, out _))
                    AirReconCoverageRegistry.Record(player, h, ctx.TurnNumber, sortieId);
        }

        // Spec §29 / §P1 — aviation still only REVEALS a hex and never marks it ground-Visited
        // (LogVisitedInvariant enforces that on every step). Its INFORMATION WEIGHTING, however,
        // now follows the real map: while a large fraction of the whole board is still
        // never-observed — including hexes no ground scout can reach — value that unknown
        // territory (Explore weighting) instead of only re-checking stale known hexes (Refresh
        // weighting). Air Recon runs after every provisioned ground scout on its own actors, so
        // this can never close a mandatory ground Explore/Visit.
        internal static ReconMode RequestedMode(PlayerSetupData player, WorldSnapshot snapshot)
        {
            // Measure the SAME thing ReconAirStepPlanner.ScoreInformation scores against: hexes
            // with no recorded intel age at all (never observed by anything — feet, vision or a
            // previous flyby), NOT ground-Visited. AiReconIntelMemory.Snapshot holds every hex
            // ever observed; TotalHexes is the on-map denominator.
            int total = snapshot?.MapKnowledge?.TotalHexes ?? 0;
            if (total <= 0)
                return ReconMode.Refresh;
            int observed = AiReconIntelMemory.Snapshot(player)?.Count ?? 0;
            float neverObservedFrac = 1f - Math.Min(1f, observed / (float)total);
            return neverObservedFrac >= AiConfigV2.airReconExploreDarkFloor
                ? ReconMode.Explore : ReconMode.Refresh;
        }

        // §P0 — the minimum useful aircraft subset for one recon sortie. Shared with
        // ReconCapacitySnapshot via ReconAirCapacityPolicy so both read one rule.
        private static List<UnitData> SelectReconLaunchSubset(IReadOnlyList<UnitData> stored) =>
            ReconAirCapacityPolicy.SelectReconLaunchSubset(stored);

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == armyId);
    }
}
