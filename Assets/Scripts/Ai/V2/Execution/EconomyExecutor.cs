using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // The Economy lane's step: the pinned preparation of a builder (garrison extraction, composition
    // change, donor loan), the walk to the build or collection site and the build or collection on
    // arrival; the funding release of a step that does not complete. Called by TaskExecutor.
    internal static class EconomyExecutor
    {
        // The one mutation site of pending Economy work Provisioning pinned: a garrison extraction
        // (the hero is not real yet) and/or a composition change or donor-loan suspend of a direct
        // army (pm.EconomyPreparationPending). Returns false when nothing is pending. On success the
        // step ends here (one mutation per step) and pm.MoverArmyId becomes the real builder.
        internal static bool TryApplyPendingPreparation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore,
            WorldSnapshot snapshot)
        {
            if (pm.Kind != MissionKind.Economy
                || (pm.EconomyExtractionGarrisonArmyId < 0 && !pm.EconomyPreparationPending))
                return false;

            bool materialized = ApplyPreparation(player, root, ctx, pm, result, apBefore, snapshot);
            result.StartHex = pm.ExecutionHex;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            if (!materialized)
            {
                result.FinalHex = pm.ExecutionHex;
                if (!result.ActualActorArmyId.HasValue)
                    result.StopForReplan(pm.EconomyExtractionGarrisonArmyId >= 0
                        ? ExecutionStopReason.TargetInvalidated : ExecutionStopReason.MoverLost);
                else
                    result.NeedsReplan = true;
                return true;
            }

            result.FinalHex = AiV2Util.ResolveArmy(player, pm.MoverArmyId)?.Hex ?? pm.ExecutionHex;
            result.StopReason = ExecutionStopReason.StepCompleted;
            result.NeedsReplan = false;
            return true;
        }

        // The SOLE apply site for pm.EconomyExtractionPreparation, for BOTH shapes of pending
        // Economy work: a garrison-extraction candidate (hero not yet real —
        // EconomyExtractionGarrisonArmyId >= 0) AND a direct-army candidate whose hero is already
        // real but whose composition change/donor-loan suspend Provisioning left pinned. No Economy
        // actor is ever mutated inside Provisioning. Execution never re-plans here — Provisioning
        // already computed and pinned the FULL decision (ProvisioningManager.PlanEconomyCompletion,
        // against a read-only preview for the extraction case, the real live hero for the direct
        // case); this only APPLIES the pinned hero extraction (if any) and cheaply re-validates the
        // facts that can shift between Provisioning and this step (operator duty, AP and spendability —
        // never choosing a replacement composition/donor/route).
        private static bool ApplyPreparation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore,
            WorldSnapshot snapshot)
        {
            ProvisioningManager.EconomyCompletionPlan prep = pm.EconomyExtractionPreparation;
            if (!prep.Feasible)
                return false;   // defensive only — Provisioning only ever defers a feasible plan

            var site = DevelopmentOpportunityEvaluator.LivePreparationSite(
                player, AiHandRegistry.Peek(player), ctx, snapshot?.Intents);
            ActorCommitments commitments = ActorCommitments.FromIntents(
                WorldSnapshot.IntentsOf(snapshot), snapshot, null);

            ArmyData materialized;
            bool extractionNeeded = pm.EconomyExtractionGarrisonArmyId >= 0;
            if (extractionNeeded)
            {
                ArmyData garrison = AiV2Util.ResolveArmy(player, pm.EconomyExtractionGarrisonArmyId);
                if (garrison == null)
                    return false;
                // Materialize the EXACT plan Provisioning already chose
                // and funded (pm.EconomyExtractionPlan), never a fresh re-resolve: a re-resolve with
                // commitments:null/session:null runs under weaker constraints than the original
                // choice and can legally pick a different — or already-claimed — hero/container/tier.
                ProvisioningManager.GarrisonExtractionCandidate plan = pm.EconomyExtractionPlan;
                if (plan.Tier == ProvisioningManager.GarrisonExtractionTier.None)
                    return false;
                // Revalidate the pinned hero, not a replacement chosen under weaker constraints.
                // A prepared site can become selected after binding, before this mutation step.
                if (plan.Hero == null || !garrison.Members.Contains(plan.Hero)
                    || !AiArmyRoles.CanSpareGarrisonMember(player, garrison, plan.Hero)
                    || AiArmyRoles.FacilityNeedsHero(player, garrison, plan.Hero,
                        site, new[] { plan.Hero }))
                {
                    result.ActualActorArmyId = garrison.Id;
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    return false;
                }
                materialized = ProvisioningManager.ApplyGarrisonExtraction(
                    player, garrison, plan, ctx, out string extractionReason);
                if (materialized == null)
                {
                    AiDebugLog.Write($"[AI][V2][Economy] extraction rejected for {pm.Key}: {extractionReason}");
                    return false;
                }

                // Extraction is one complete task step; preparation and movement are re-admitted.
                pm.MoverArmyId = materialized.Id;
                pm.EconomyExtractionGarrisonArmyId = -1;
                pm.EconomyPreparationPending = prep.Donor != null
                    || prep.Unload.Count > 0 || prep.Reinforcement.Count > 0;
                pm.ClaimedAp = prep.RealAp;
                result.ActualActorArmyId = materialized.Id;
                result.ActorMaterialized = true;
                result.EconomyMaterializationSourceArmyId = garrison.Id;
                AiDebugLog.Write($"[AI][V2][Economy] materialized builder #{materialized.Id} "
                    + $"for {pm.Key}; preparation deferred to next admission");
                return true;
            }
            else
            {
                // Direct-army case — the hero is already a real, live field army; nothing to extract.
                materialized = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
                if (materialized == null)
                    return false;
                // Before ANY composition mutation: the pinned plan is stale when the walk would now
                // take a served operator it cannot leave home (nothing is mutated or spent).
                if (!materialized.Hex.Equals(pm.EconomyTarget.TargetHex)
                    && DevelopmentOpportunityEvaluator.OperatorDutyBlocksDeparture(player, materialized,
                        commitments, site))
                {
                    AiDebugLog.Write($"[AI][V2][Economy] materialization prep stale for {pm.Key}: "
                        + "builder carries a served operator that cannot stay home - nothing mutated");
                    result.ActualActorArmyId = materialized.Id;
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    return false;
                }
            }

            float eps = AiConfigV2.allocatorSliceEpsilon;
            // The envelope is what Provisioning actually funded THIS mission (pm.ClaimedAp, the
            // AUTHORITATIVE PlanEconomyCompletion figure), minus whatever the hero extraction
            // itself just spent — never the player's entire current AP pool.
            float spentSoFar = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            float remainingEnvelope = Mathf.Max(0f, pm.ClaimedAp - spentSoFar);
            if (prep.RealAp > remainingEnvelope + eps
                || !root.CanSpendActionPoints(Mathf.CeilToInt(prep.RealAp)))
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep stale for {pm.Key}: AP no "
                    + "longer available - hero stays a real field mover, found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }
            int rateBeforeFunding = root.TradeRate;
            bool funded = TradeFunding.Fund(player, root, ctx, prep.StageCost, new SpendAuthority(prep.OwnerKey, true));
            if (root.TradeRate != rateBeforeFunding)
            { result.ResourceConverted = true; result.StateVersionAfter = WorldDeltaLifecycle.Current; }
            if (!funded)
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep stale for {pm.Key}: resources "
                    + "no longer spendable - hero stays a real field mover, found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }

            // The funded AP/escort witness describes the departing roster. Commit its zero-AP
            // operator release before composition and before repricing that roster's activation.
            if (!materialized.Hex.Equals(pm.EconomyTarget.TargetHex)
                && AiArmyRoles.DepartureStripsOperator(player, materialized, site))
            {
                int released = LocalOperatorRelease.ReleaseBeforeDeparture(
                    player, ctx, materialized, commitments, site);
                if (released > 0)
                {
                    result.StateVersionAfter = WorldDeltaLifecycle.Current;
                    result.EconomyPrepared = true;
                }
                if (AiArmyRoles.DepartureStripsOperator(player, materialized, site)
                    || !AiArmyRoles.IsHeroLed(materialized))
                {
                    result.ActualActorArmyId = materialized.Id;
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    return false;
                }
            }

            int preparedMembers = ProvisioningManager.ApplyEconomyArmyLightening(
                materialized, prep.Garrison, prep.Unload, prep.Reinforcement, ctx);
            int plannedTransfers = prep.Unload.Count + prep.Reinforcement.Count;
            if (preparedMembers != plannedTransfers)
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep failed for {pm.Key}: "
                    + "composition transaction did not commit"
                    + " - hero stays a real field mover, found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }
            // Immediate join AP is already spent; validate the live unspent remainder.
            float realAp = ProvisioningManager.EconomyMissionClaimedAp(materialized,
                pm.EconomyTarget.BuildApCost, pm.EconomyTarget.MinimumFollowupAp, null,
                added: null, unloadTarget: null,
                travelNeeded: prep.TravelNeeded,
                completionThisTurn: prep.CompletionThisTurn);
            float spentAfterComposition = Mathf.Max(0f,
                apBefore - (root != null ? root.ActionPoints : apBefore));
            float envelopeAfterComposition = Mathf.Max(0f, pm.ClaimedAp - spentAfterComposition);
            if (realAp > envelopeAfterComposition + eps)
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep stale for {pm.Key}: could "
                    + "not lighten within the funded AP envelope - hero stays a real field mover, "
                    + "found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.EconomyPrepared = true;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }
            // No EconomyReservationLifecycle.ReserveEconomyCost call here: Provisioning already
            // reserved it (see the deferred branch of ProvisionEconomy) the moment this mission
            // committed to being deferred, so the resource pool is honestly reduced for any OTHER
            // Economy mission provisioned later this turn. Reserving again here would
            // double-charge the ledger for one build.
            if (prep.Donor != null)
            {
                prep.Donor.Status = IntentStatus.Suspended;
                prep.Donor.Suspended = SuspendReason.EconomyLoan;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] borrow actor=#{materialized.Id} "
                    + $"from={prep.Donor.IntentKey} to={pm.Key}");
            }

            pm.MoverArmyId = materialized.Id;
            pm.EconomyExtractionGarrisonArmyId = -1;
            pm.EconomyPreparationPending = false;
            pm.ReservationOwner = prep.OwnerKey;
            pm.EconomyLoanSource = prep.Donor?.IntentKey;
            pm.ClaimedAp = realAp;
            pm.ClaimedPhysical = ProvisioningManager.CostVector(prep.StageCost);
            result.ActualActorArmyId = materialized.Id;
            result.EconomyPrepared = true;
            AiDebugLog.Write($"[AI][V2][Economy] prepared builder #{materialized.Id} for {pm.Key} "
                + (extractionNeeded ? "(garrison extraction)" : "(composition/loan only)"));
            return true;
        }

        internal static IEnumerator RunStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore,
            WorldSnapshot snapshot = null)
        {
            ArmyData army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            if (army == null || army.Owner != player)
            {
                result.StopForReplan(ExecutionStopReason.MoverLost);
                yield break;
            }
            // WorldAnalysis.Observation.PublishStepObservationDelta's EconomyDeliveryReady branch
            // passes this straight through as the Actor-invalidation id.
            result.ActualActorArmyId = army.Id;
            EconomyMissionTarget target = pm.EconomyTarget;
            if (target.Kind == EconomyTaskKind.MobileCollection
                && !WorldAnalysis.CollectorRouteSafeLive(player, ctx, army, target.TargetHex, target.ResourceType))
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                result.FinalHex = army.Hex;
                yield break;
            }
            if (army.Hex.Equals(target.TargetHex))
            {
                result.ReachedGoal = target.Kind.IsReturn();
                result.StopReason = result.ReachedGoal
                    ? ExecutionStopReason.ReachedGoal
                    : ExecutionStopReason.StepCompleted;
                result.NeedsReplan = false;
                result.FinalHex = army.Hex;
                // A deferred mission that materializes directly
                // onto its target hex (garrison.Hex == target.TargetHex — e.g. an in-place base)
                // reaches this branch having already spent real AP (CreateArmy / an activation
                // charge / lightening) with zero StepsMoved; ApCheck's live-pool-delta invariant
                // (spec §2.1) would otherwise flag every one of those turns as a reporting mismatch.
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                if (result.ReachedGoal)
                    AiDebugLog.Write($"[AI][V2][Economy][Recovery] builder #{army.Id} protected at "
                        + $"({army.Hex.Q},{army.Hex.R})");
                else
                {
                    if (target.Kind == EconomyTaskKind.MobileCollection)
                    {
                        result.EconomyHolding = true;
                        AiDebugLog.Write($"[AI][V2][Economy][Mobile] collector #{army.Id} holding "
                            + $"@({army.Hex.Q},{army.Hex.R}) for global income tick");
                    }
                    else
                    {
                        result.EconomyDeliveryReady = true;
                        AiDebugLog.Write($"[AI][V2][Economy] delivery ready {pm.Key}; request Phase-A build follow-up");
                    }
                }
                yield break;
            }
            // The departure itself, read live (the same owners as admission and binding): a build
            // walk leaves the operator of a served facility / selected preparation site at home when
            // that is legal and free (LocalOperatorRelease), otherwise it does not start — the
            // existing stale-plan refusal, no replacement and no new mission; the caller releases the
            // funding exactly once and Continuity retires the builder through ResolveActive.
            if (target.Kind == EconomyTaskKind.BuildExtraction || target.Kind == EconomyTaskKind.FoundBase)
            {
                AiHandData hand = AiHandRegistry.Peek(player);
                System.Func<ResearchProductionMode, HexCoord?> site =
                    DevelopmentOpportunityEvaluator.LivePreparationSite(player, hand, ctx, snapshot?.Intents);
                if (AiArmyRoles.DepartureStripsOperator(player, army, site))
                {
                    ActorCommitments commitments = snapshot == null ? null : ActorCommitments.FromIntents(
                        WorldSnapshot.IntentsOf(snapshot), snapshot, null);
                    if (!LocalOperatorRelease.CanKeepOperatorsHome(player, army, commitments, site, out string why))
                    {
                        AiDebugLog.WriteDeduped($"economy-operator-duty:{army.Id}",
                            $"[AI][V2][Economy] builder #{army.Id} stays at ({army.Hex.Q},{army.Hex.R}): "
                            + $"departure would take a served operator ({why})");
                        result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                        result.FinalHex = army.Hex;
                        yield break;
                    }
                    if (LocalOperatorRelease.ReleaseBeforeDeparture(player, ctx, army, commitments, site) > 0)
                        result.StateVersionAfter = WorldDeltaLifecycle.Current;
                    if (AiArmyRoles.DepartureStripsOperator(player, army, site))
                    {
                        result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                        result.FinalHex = army.Hex;
                        yield break;
                    }
                }
            }
            yield return RunTransportStep(player, root, ctx, pm, result, apBefore,
                target.TargetHex, $"economy - {target.Kind}");
            HexCoord after = result.FinalHex;
            bool recoveryArrived = result.StopReason != ExecutionStopReason.MoverLost
                && target.Kind.IsReturn()
                && after.Equals(target.TargetHex);
            result.ReachedGoal = recoveryArrived;
            if (recoveryArrived)
                result.StopReason = ExecutionStopReason.ReachedGoal;
            if (result.StopReason == ExecutionStopReason.MoveRejected)
                result.NeedsReplan = true;
        }

        // The one safe-path step of a non-combat actor (an Economy builder or collector, a
        // Development operator): one bound adjacent move and its AP/step accounting. It never
        // selects the actor or the objective.
        internal static IEnumerator RunTransportStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore,
            HexCoord target, string label)
        {
            ArmyData army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            if (army == null)
            {
                result.StopForReplan(ExecutionStopReason.MoverLost);
                yield break;
            }
            result.ActualActorArmyId = army.Id;
            result.FinalHex = army.Hex;
            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, target);
            if (!next.HasValue)
            {
                result.StopForReplan(ExecutionStopReason.NoSafeStep);
                yield break;
            }
            HexCoord before = army.Hex;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player,
                AiDecision.Move(army, next.Value,
                    $"V2 {label} at ({target.Q},{target.R})", 0f, AiGroundMoveAuthority.Transit), ctx, trace);
            army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            HexCoord after = army != null ? army.Hex : trace.EndHex;
            result.FinalHex = after;
            if (!after.Equals(before)) result.StepsMoved = 1;
            WorldDeltaLifecycle.RecordExecutionMutation(result, result.StepsMoved > 0
                || trace.BattleOccurred || trace.EnteredStealthThisStep);
            result.StopReason = army == null ? ExecutionStopReason.MoverLost
                : pm.Kind == MissionKind.Development && trace.BattleOccurred
                ? ExecutionStopReason.BattleStarted
                : pm.Kind == MissionKind.Development && trace.HexEventOccurred
                    ? ExecutionStopReason.HexEventStarted
                    : result.StepsMoved > 0
                            ? ExecutionStopReason.StepCompleted : ExecutionStopReason.MoveRejected;
            result.NeedsReplan = army == null || result.StepsMoved == 0;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            if (AiDebugLog.Verbose)
                AiDebugLog.Write($"[AI][V2] {label} move {pm.Key} "
                    + $"({before.Q},{before.R})->({after.Q},{after.R})");
        }

        // The funding an Economy step held is released when the step does not complete.
        internal static void ReleaseReservation(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm)
        {
            if (pm?.Kind == MissionKind.Economy && ctx != null)
                MissionLeaseBook.ReleaseByOwner(player, ctx.TurnNumber,
                    pm.ReservationOwner);
        }
    }
}
