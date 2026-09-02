using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ReconOnly Air Recon operational pass. It deliberately runs AFTER provisioned missions: air is
    // an information fallback, not a way to steal an aircraft from higher-priority work. No cached
    // route survives a transition. Every outbound AND return move is exactly one adjacent hex,
    // resolved by the existing authoritative aviation/movement paths, followed by live
    // visibility/IntelAge refresh and a fresh safety/reward decision.
    //
    // AiTaskKind.AirRecon is retained only as the EXISTING landing-slot reservation primitive.
    // V2 never calls AiAviationSupport.ContinueSortie for these actors: ReconAssignment + the live
    // planner own direction/mode, while the task contributes only LandingHex capacity ownership and
    // the ordinary first-move Energy-reservation seam already understood by AiAviationSupport.
    internal static class ReconAirExecutor
    {
        // Per-turn air-recon actor cap and the storage launch-subset rule now live in the shared
        // ReconAirCapacityPolicy so ReconCapacitySnapshot enforces the exact same limits.

        public static IEnumerator RunFallback(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot)
        {
            // Spec §29 / review P1 #3 — AirRecon is not tied to the temporary ReconOnly isolation.
            // It runs whenever there is a snapshot to fly against; per-airfield readiness, the
            // Energy opportunity policy and the minimum-useful-score gate below decide whether any
            // sortie is actually worth launching, in Full V2 exactly as in ReconOnly.
            if (player == null || root == null || ctx?.Map == null || snapshot?.Self == null)
            {
                // Spec §6 — never leave "AirRecon path never reached" and "AirRecon evaluated and
                // chose not to fly" indistinguishable in the log.
                AiDebugLog.Write("[AI][V2][Recon][Air] fallback — not reached ("
                    + $"player={(player != null ? 1 : 0)} root={(root != null ? 1 : 0)} "
                    + $"map={(ctx?.Map != null ? 1 : 0)} snapshot={(snapshot?.Self != null ? 1 : 0)})");
                yield break;
            }

            var used = new HashSet<int>();
            int actorsUsed = 0;
            var airSkips = new List<string>();

            var active = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a)
                    && a.Controller != null && a.CurrentMovement > 0
                    && !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && ReconAssignmentRegistry.TryGet(player, a.Id, out _))
                .OrderBy(a => a.Id)
                .ToList();

            int ownedAirfields = AiAviationSupport.OwnedAirfieldHexes(player).Count();
            // §P2 — three DISTINCT counts, never one ambiguous "aircraft=" that reads 0 while a
            // hangar holds two: aircraft parked in airfield storage, aircraft already airborne,
            // and aircraft sitting ready on their own airfield with no task.
            int storedAircraftCount = AiAviationSupport.OwnedAirfieldHexes(player)
                .Select(h => AviationRules.FindAirfieldAt(h, player))
                .Where(s => s != null)
                .Sum(s => s.Members.Count);
            int airborneAircraft = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a)
                    && !AviationRules.IsOwnedAirfieldAt(a.Hex, player))
                .Sum(a => Math.Max(1, a.Members.Count));
            int readyOnAirfield = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a)
                    && AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && AiTaskRegistry.TaskFor(player, a) == null)
                .Sum(a => Math.Max(1, a.Members.Count));

            void AirFallbackSummary(string exit) => AiDebugLog.Write(
                $"[AI][V2][Recon][Air] fallback — {exit}: airfields={ownedAirfields} "
                + $"stored={storedAircraftCount} airborne={airborneAircraft} ready={readyOnAirfield} "
                + $"inFlightWithAssignment={active.Count} sortiesThisPass={actorsUsed} "
                + $"skips=[{(airSkips.Count > 0 ? string.Join(",", airSkips) : "none")}]");

            foreach (ArmyData air in active)
            {
                if (actorsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn) break;
                yield return RunActor(player, root, ctx, snapshot, air);
                used.Add(air.Id);
                actorsUsed++;
            }

            if (actorsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn)
            {
                airSkips.Add("actorLimitReached");
                AirFallbackSummary("stop after in-flight actors");
                yield break;
            }

            var candidates = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !used.Contains(a.Id)
                    && AviationRules.IsValidAirArmy(a) && a.Controller != null && a.CurrentMovement > 0
                    && AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && AiTaskRegistry.TaskFor(player, a) == null)
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : 1)
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationEnergyCost)
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.Id)
                .ToList();

            if (candidates.Count == 0)
                airSkips.Add("noReadyAircraftOffAirfieldTask");

            foreach (ArmyData air in candidates)
            {
                if (actorsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn) break;
                bool moved = false;
                yield return RunActor(player, root, ctx, snapshot, air, value => moved = value);
                if (moved)
                    actorsUsed++;
                else
                    airSkips.Add("readyAircraftNoUsefulStep");
            }

            if (actorsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn)
            {
                airSkips.Add("actorLimitReached");
                AirFallbackSummary("stop after ready aircraft");
                yield break;
            }

            ReconMode requestedMode = RequestedMode(player, snapshot);
            if (ownedAirfields == 0)
                airSkips.Add("noOwnedAirfield");
            foreach (HexCoord airfieldHex in AiAviationSupport.OwnedAirfieldHexes(player).ToList())
            {
                if (actorsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn) break;
                ArmyData stored = AviationRules.FindAirfieldAt(airfieldHex, player);
                if (stored == null || stored.Members.Count < AiConfig.aviationLaunchMinReadyAircraft)
                {
                    airSkips.Add(stored == null ? "airfieldEmpty" : "belowMinReadyAircraft");
                    continue;
                }

                // §P0 — a recon sortie needs ONE seeing aircraft, not the whole hangar. Launching
                // every stored wing as a single stack sums their activation AP/Energy and
                // permanently sinks the step score negative (spec §29 — air recon is an
                // information fallback, never a mass sortie). Take the cheapest-to-activate
                // minimum subset; the rest stay ready in storage.
                var launchSubset = SelectReconLaunchSubset(stored.Members);
                if (!AiAviationSupport.CanAffordLaunch(root, player, launchSubset))
                {
                    airSkips.Add("launchApEnergyUnavailable");
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({airfieldHex.Q},{airfieldHex.R}) "
                        + $"aircraft={launchSubset.Count}/{stored.Members.Count} — launch AP/Energy unavailable; skip");
                    continue;
                }

                var storedAircraft = launchSubset;
                var launchCandidate = new AirStrikeTask.LaunchCandidate(airfieldHex, null, storedAircraft);
                ReconAirStepPlanner.StepChoice? first = ReconAirStepPlanner.PickFromStorage(
                    player, ctx, launchCandidate, snapshot, requestedMode, ctx.TurnNumber);
                if (!first.HasValue || first.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                {
                    airSkips.Add("noUsefulRefreshStep");
                    continue;
                }

                // §40–44 — a routine refresh sortie must not dip into Energy a playable high-value
                // hand card (or another in-flight AirRecon activation) still needs.
                int storageLaunchEnergy = storedAircraft.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                ReconAirEnergyDecision storageEnergy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                    storageLaunchEnergy, first.Value.Score, excludeArmyId: -1);
                AiDebugLog.Write(storageEnergy.ToLog($"airfield=({airfieldHex.Q},{airfieldHex.R})"));
                if (!storageEnergy.Allowed)
                {
                    airSkips.Add("energyReserveRejectedLaunch");
                    continue;
                }

                bool firstVisitedBefore = VisionSystem.IsVisited(player, first.Value.Hex);
                var beforeIds = new HashSet<int>(ArmyRegistry.AllForOwner(player)
                    .Where(AviationRules.IsValidAirArmy).Select(a => a.Id));
                var launchDecision = new AiDecision
                {
                    Kind = AiActionKind.LaunchAirRecon,
                    ExistingArmy = null,
                    TargetHex = airfieldHex,
                    AircraftToLaunch = storedAircraft,
                    AirActionHex = first.Value.Hex,
                    AirLandingHex = first.Value.LandingHex,
                    Score = first.Value.Score,
                    Category = AiTaskCategory.Reconnaissance,
                    Reason = $"V2 Air Recon — {requestedMode} one-step launch; {first.Value.Reason}",
                };

                yield return AiAviationSupport.LaunchRoutine(player, launchDecision, ctx, AiTaskKind.AirRecon);

                ArmyData launched = ArmyRegistry.AllForOwner(player)
                    .Where(a => a != null && AviationRules.IsValidAirArmy(a) && !beforeIds.Contains(a.Id))
                    .OrderBy(a => a.Id)
                    .FirstOrDefault();
                if (launched == null)
                {
                    airSkips.Add("launchFormedNoAircraft");
                    continue;
                }

                AiTask reservationTask = AiTaskRegistry.TaskFor(player, launched);
                if (launched.Hex.Equals(airfieldHex))
                {
                    RemoveAirReconReservation(player, launched);
                    airSkips.Add("launchFirstStepNoProgress");
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] actor=#{launched.Id} launch formed but "
                        + "first step made no progress; V2 assignment not started");
                    continue;
                }

                if (reservationTask != null && reservationTask.Kind == AiTaskKind.AirRecon)
                {
                    reservationTask.AirOutbound = true;
                    reservationTask.TargetHex = first.Value.Hex;
                    reservationTask.LandingHex = first.Value.LandingHex;
                }

                ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, launched.Id,
                    airfieldHex, first.Value.Hex, requestedMode, ctx.TurnNumber);
                ReconAssignmentRegistry.MarkProgress(player, launched.Id, ctx.TurnNumber);
                // Seed the per-sortie boomerang/phase state from the real launch hex so the trail
                // and Outbound phase begin at the airfield, not one hex out.
                ReconAirSortieState launchSortie = ReconAirSortieRegistry.GetOrCreate(player, launched.Id, airfieldHex);
                launchSortie.RecordStep(launched.Hex);
                launchSortie.BestOutboundStepScore = Math.Max(launchSortie.BestOutboundStepScore, first.Value.Score);
                AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                AiMapMemory.RecordAirReconTarget(player, launched.Hex, ctx.TurnNumber);
                LogVisitedInvariant(player, first.Value.Hex, firstVisitedBefore, "storage-launch-first-step");
                AiDebugLog.Write($"[AI][V2][Recon][Air][Handoff] actor=#{launched.Id} "
                    + $"launch=({airfieldHex.Q},{airfieldHex.R}) first=({launched.Hex.Q},{launched.Hex.R}) "
                    + $"mode={assignment.Mode}; V1 task retained only as landing-slot reservation");

                used.Add(launched.Id);
                actorsUsed++;

                if (launched.Controller != null && launched.CurrentMovement > 0
                    && !AviationRules.IsOwnedAirfieldAt(launched.Hex, player))
                    yield return RunActor(player, root, ctx, snapshot, launched, initialStepAlreadyMoved: true);
            }

            AirFallbackSummary(actorsUsed > 0 ? "done" : "evaluated, no sortie");
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
                if (!air.Hex.Equals(sortie.LaunchHex))
                {
                    sortie.ClaimedSector = ReconDirectionModel.Sector(sortie.LaunchHex, air.Hex);
                    sortie.HasClaim = true;
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

                    AiTask reservation = EnsureAirReconReservation(player, air, landing, outbound: false);
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
                        reservation, () => moved = true);
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
                AiTask reservationTask = EnsureAirReconReservation(player, air,
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

                if (!AiTurnController.CanIssueMoveNow(root, player, air, ctx.Map, choice.Value.Hex, reservationTask))
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
                    $"V2 Air Recon — {mode} {sortie.Phase} one-step live replan", reservationTask, () => stepMoved = true);
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

            if (AiAviationSupport.KnownAaExposureAt(player, air.Hex) > 0)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                    + "decision=SKIP reason=known_aa_on_hex");
                yield break;
            }

            // A strike costs no movement, so return feasibility should be unchanged — but verify a
            // safe landing exists at all before committing to reveal ourselves.
            if (!AiAviationSupport.TryReplan(air, ctx.Map, player).HasValue
                && !AiAviationSupport.TryReplanMultiTurnReturn(air, ctx.Map, player).HasValue)
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
            if (sortie != null)
                sortie.Phase = ReconAirPhase.Return; // reveal happened — turn for home, live replan next iteration

            if (afterStrike != null && AviationRules.IsValidAirArmy(afterStrike)
                && !AiAviationSupport.TryReplan(afterStrike, ctx.Map, player).HasValue
                && !AiAviationSupport.TryReplanMultiTurnReturn(afterStrike, ctx.Map, player).HasValue)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} attacked={result.Attacked}; "
                    + "WARN no safe return after strike — next iteration will hold/seek any airfield");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} attacked={result.Attacked}; "
                    + "safe return preserved, sortie now Return");
            }
        }

        private static IEnumerator MoveOne(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            HexCoord next, string reason, AiTask reservationTask, Action onMoved)
        {
            HexCoord before = air.Hex;
            bool visitedBefore = VisionSystem.IsVisited(player, next);
            var decision = AiDecision.Move(air, next, reason, reservationTask, 0f, AiTaskCategory.Reconnaissance);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            ArmyData live = Resolve(player, air.Id);
            HexCoord after = live != null ? live.Hex : trace.EndHex;
            if (!after.Equals(before))
                onMoved?.Invoke();

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            LogVisitedInvariant(player, next, visitedBefore, "live-step");
            // AI-AIR-01 §5 — stamp every hex this sortie actually observed so a later route's
            // RedundancyPenalty / hard "repeats a recent air observation" reject has real data
            // (V2 never calls AiAviationSupport.ContinueSortie, which was the only V1 stamper).
            if (!after.Equals(before))
                AiMapMemory.RecordAirReconTarget(player, after, ctx.TurnNumber);
            AiDebugLog.Write($"[AI][V2][Recon][Air][Observe] actor=#{air.Id} "
                + $"({before.Q},{before.R})->({after.Q},{after.R}) intel refreshed; groundVisitedWrite=0");
        }

        private static HexCoord? PickReturnStep(PlayerSetupData player, HexMap map, ArmyData air,
            ReconAirSortieState sortie, out HexCoord landing, out string reason)
        {
            landing = default;
            reason = null;

            HexCoord? sameTurn = AiAviationSupport.TryReplan(air, map, player);
            if (sameTurn.HasValue)
            {
                landing = ApplyLandingHysteresis(player, map, air, sortie, sameTurn.Value, out string h);
                reason = "same-turn safest return" + h;
                return FirstStep(map, air.Hex, landing);
            }

            AiAviationSupport.MultiTurnSortie? multi =
                AiAviationSupport.TryReplanMultiTurnReturn(air, map, player);
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

            int prevForward = AiAviationSupport.NearestKnownEnemyDistance(player, sortie.ChosenLandingHex);
            int newForward = AiAviationSupport.NearestKnownEnemyDistance(player, candidate);
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
            if (AiAviationSupport.FreeLandingCapacity(landing, player, air) < air.Members.Count)
                return false;
            HexPath path = HexPathfinder.FindPath(map, air.Hex, landing, flatCost: true);
            if (path == null)
                return false;
            if (AviationRules.PathMoveCost(air, path) > air.CurrentMovement)
                return false;
            int baseline = AiAviationSupport.KnownAaExposureAt(player, air.Hex);
            return AiAviationSupport.KnownAaExposure(player, path) - baseline <= 0;
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

        private static AiTask EnsureAirReconReservation(PlayerSetupData player, ArmyData air,
            HexCoord landing, bool outbound, HexCoord? target = null)
        {
            AiTask task = AiTaskRegistry.TaskFor(player, air);
            if (task == null)
            {
                task = new AiTask { Kind = AiTaskKind.AirRecon, Army = air };
                AiTaskRegistry.Add(player, task);
            }
            if (task.Kind != AiTaskKind.AirRecon)
                return null;

            task.AirOutbound = outbound;
            task.LandingHex = landing;
            task.TargetHex = target ?? landing;
            return task;
        }

        private static void RemoveAirReconReservation(PlayerSetupData player, ArmyData air)
        {
            AiTask task = air != null ? AiTaskRegistry.TaskFor(player, air) : null;
            if (task == null || task.Kind != AiTaskKind.AirRecon)
                return;
            AiResourceReservation.Release(task);
            AiTaskRegistry.Remove(player, task);
        }

        private static void LogVisitedInvariant(PlayerSetupData player, HexCoord hex, bool visitedBefore, string phase)
        {
            bool visitedAfter = VisionSystem.IsVisited(player, hex);
            if (!visitedBefore && visitedAfter)
                AiDebugLog.Write($"[AI][V2][Recon][Air][INVARIANT-FAIL] phase={phase} aircraft unexpectedly "
                    + $"marked ground Visited at ({hex.Q},{hex.R})");
        }

        // Spec §29 / §P1 — aviation still only REVEALS a hex and never marks it ground-Visited
        // (LogVisitedInvariant enforces that on every step). Its INFORMATION WEIGHTING, however,
        // now follows the real map: while a large fraction of the whole board is still
        // never-observed — including hexes no ground scout can reach — value that unknown
        // territory (Explore weighting) instead of only re-checking stale known hexes (Refresh
        // weighting). Air Recon runs after every provisioned ground scout on its own actors, so
        // this can never close a mandatory ground Explore/Visit.
        private static ReconMode RequestedMode(PlayerSetupData player, WorldSnapshot snapshot)
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
