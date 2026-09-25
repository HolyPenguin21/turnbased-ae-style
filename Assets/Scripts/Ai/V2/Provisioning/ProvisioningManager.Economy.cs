using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Economy lane provisioning: the build/found path and its two recovery paths. A
    // mechanical partial of ProvisioningManager; the kind dispatcher in Provision
    // (ProvisioningManager.cs) is the only caller of ProvisionEconomy, which in turn owns
    // the recovery and mobile-collection branches.

    internal static partial class ProvisioningManager
    {
        private static bool IsMobileEconomyHero(ArmyData army, PlayerSetupData player) =>
            army != null && army.Owner == player && AiArmyRoles.IsHeroLed(army);

        internal static bool IsEligibleEconomyRecoveryActor(
            MissionProposal mission, ArmySnapshot actor)
        {
            if (mission == null || actor == null || mission.Kind != MissionKind.Economy
                || !(mission.Target is EconomyMissionTarget target)
                || target.Kind != EconomyTaskKind.ReturnBuilder
                || !mission.PreferredMoverArmyId.HasValue)
                return false;
            return actor.ArmyId == mission.PreferredMoverArmyId.Value
                && (!target.BuilderArmyId.HasValue
                    || actor.ArmyId == target.BuilderArmyId.Value)
                && actor.HasHero && !actor.IsPrison && !actor.IsAir && !actor.IsAirfield;
        }

        private static ProvisioningResult ProvisionEconomy(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target)
        {
            MissionProposal m = funded.Mission;
            StableMissionKey key = StableMissionKey.For(m);
            if (target.Kind == EconomyTaskKind.MobileCollection
                || target.Kind == EconomyTaskKind.ReturnCollector)
                return ProvisionMobileCollection(player, root, ctx, session, funded, target, key);
            if (target.Kind == EconomyTaskKind.ReturnBuilder)
                return ProvisionEconomyRecovery(player, root, ctx, session, funded, target, key);
            if (MissionOutcomeLedger.EconomyObjectiveSatisfied(player, target))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied("economy target already built"));
            if (target.Kind == EconomyTaskKind.FoundBase
                && (target.BuildCard == null || hand?.Hand == null || !hand.Hand.Contains(target.BuildCard)))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated("founding card no longer in hand"));

            List<MissionIntent> standingIntents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            MissionIntentKey currentIntentKey = MissionIntentKey.For(m);
            ActorCommitments actorCommitments = ActorCommitments.FromIntents(
                standingIntents, session.Snapshot, null);
            IReadOnlyList<DemandLayer.EconomyBuilderChoice> rankedBuilders =
                DemandLayer.RankEconomyBuilders(session.Snapshot, target.TargetHex,
                    target.BuilderRoutes, standingIntents, actorCommitments,
                    target.BuildValue, target.BuildApCost,
                    includeReturn: target.Kind == EconomyTaskKind.BuildExtraction);
            IEnumerable<DemandLayer.EconomyBuilderChoice> eligibleBuilders = rankedBuilders;
            if (m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
                eligibleBuilders = eligibleBuilders.Where(
                    x => x.Route.ArmyId == m.PreferredMoverArmyId.Value);

            // DIAGNOSTIC (FoundBase only) — traces which single eligibility clause below rejects a
            // durable intent's committed mover, since the FirstOrDefault predicate normally
            // swallows all of them into one MoverContended result.
            if (target.Kind == EconomyTaskKind.FoundBase
                && m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
            {
                int preferredId = m.PreferredMoverArmyId.Value;
                var eligibleList = eligibleBuilders.ToList();
                eligibleBuilders = eligibleList;
                bool inRankedAtAll = rankedBuilders.Any(x => x.Route.ArmyId == preferredId);
                AiDebugLog.Write($"[AI][V2][Economy][TRACE] {player?.Nickname} durable mover #{preferredId} "
                    + $"target=({target.TargetHex.Q},{target.TargetHex.R}) turn={session.Snapshot?.TurnNumber} — "
                    + $"inRankedBuilders={inRankedAtAll} rankedBuildersTotal={rankedBuilders.Count} "
                    + $"eligibleAfterMoverFilter={eligibleList.Count}");
                if (!inRankedAtAll)
                {
                    // Never reached EconomyBuilderCandidates' yield at all — replicate its gates
                    // here (read-only, does not touch the real generator) to see which one ate it.
                    ArmySnapshot snapArmy = session.Snapshot?.Self?.Armies
                        ?.FirstOrDefault(a => a != null && a.ArmyId == preferredId);
                    if (snapArmy == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{preferredId} upstream — "
                            + "not found in WorldSnapshot.Self.Armies (destroyed/merged/not owned this turn?)");
                    }
                    else
                    {
                        MissionIntent upstreamAssignment = standingIntents.FirstOrDefault(
                            i => i != null && i.Status == IntentStatus.Active
                            && i.PreferredMoverArmyId == preferredId);
                        bool economyTargetMismatch = upstreamAssignment != null
                            && upstreamAssignment.Kind == MissionKind.Economy
                            && (upstreamAssignment.Economy == null
                                || !upstreamAssignment.Economy.TargetHex.Equals(target.TargetHex));
                        bool nonEconomyDonorBlock = upstreamAssignment != null
                            && upstreamAssignment.Kind != MissionKind.Economy
                            && !DemandLayer.EconomyDonorStructurallyEligible(upstreamAssignment);
                        bool claimedUpstream = actorCommitments != null
                            && actorCommitments.IsArmyClaimed(preferredId);
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{preferredId} upstream "
                            + $"(EconomyBuilderCandidates-equivalent) — hex=({snapArmy.Hex.Q},{snapArmy.Hex.R}) "
                            + $"isMobileEconomyBuilder={snapArmy.IsMobileEconomyBuilder} "
                            + $"assignment={(upstreamAssignment == null ? "none" : $"{upstreamAssignment.Kind}/{upstreamAssignment.IntentKey} status={upstreamAssignment.Status}")} "
                            + $"economyTargetMismatch={economyTargetMismatch} nonEconomyDonorBlock={nonEconomyDonorBlock} "
                            + $"claimedUpstream={claimedUpstream} "
                            + $"underImmediateThreat={DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, snapArmy.Hex)}");
                    }
                }
                foreach (DemandLayer.EconomyBuilderChoice x in eligibleList)
                {
                    ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                    if (a == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} — ResolveArmy returned null");
                        continue;
                    }
                    bool cMobile = IsMobileEconomyHero(a, player);
                    bool cThreat = !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex);
                    bool cClaimed = !session.ClaimedArmyIds.Contains(a.Id);
                    MissionIntent conflicting = standingIntents.FirstOrDefault(i => i.PreferredMoverArmyId == a.Id
                        && !i.IntentKey.Equals(currentIntentKey)
                        && !DemandLayer.EconomyDonorStructurallyEligible(i));
                    bool cDonorConflict = conflicting == null;
                    bool atTarget = a.Hex.Equals(target.TargetHex);
                    HexCoord? nextStep = atTarget
                        ? (HexCoord?)null
                        : SafeStepPathing.FindNextSafeStep(ctx.Map, a, target.TargetHex);
                    bool cPath = atTarget || (a.CurrentMovement > 0 && nextStep.HasValue);
                    AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{a.Id} hex=({a.Hex.Q},{a.Hex.R}) "
                        + $"currentMovement={a.CurrentMovement} maxMovement={a.MaxMovement} "
                        + $"isMobileEconomyHero={cMobile} notUnderImmediateThreat={cThreat} "
                        + $"notClaimedThisPass={cClaimed} noConflictingIntent={cDonorConflict}"
                        + (conflicting != null ? $" (conflictsWith={conflicting.IntentKey} kind={conflicting.Kind} status={conflicting.Status})" : "")
                        + $" atTargetHex={atTarget} hasSafeNextStep={(atTarget ? (object)"n/a" : nextStep.HasValue)} "
                        + $"=> ELIGIBLE={cMobile && cThreat && cClaimed && cDonorConflict && cPath}");
                }
            }

            bool IsCandidateEligible(DemandLayer.EconomyBuilderChoice x)
            {
                if (x.Route.RequiresGarrisonExtraction)
                {
                    // ArmyId here names the Garrison, not yet a separate mover — re-derive the
                    // exact same candidate AiArmyRoles.BestSparableEconomyHero would give
                    // Analysis right now (canonical, same predicate as CanSpareGarrisonMember),
                    // never trusting a hero identity carried across from an earlier phase.
                    ArmyData g = ResolveArmy(player, x.Route.ArmyId);
                    UnitData sparable = g == null ? null
                        : AiArmyRoles.BestSparableEconomyHero(player, g);
                    return g != null && sparable != null
                        && !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, g.Hex)
                        && !session.ClaimedArmyIds.Contains(g.Id)
                        && (g.Hex.Equals(target.TargetHex)
                            || SafeStepPathing.FindSafePathCost(
                                ctx.Map, player, g.Hex, target.TargetHex, sparable.MoveMax)
                                != int.MaxValue);
                }
                ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                return a != null && IsMobileEconomyHero(a, player)
                && !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex)
                && !session.ClaimedArmyIds.Contains(a.Id)
                && !standingIntents.Any(i => i.PreferredMoverArmyId == a.Id
                    && !i.IntentKey.Equals(currentIntentKey)
                    && !DemandLayer.EconomyDonorStructurallyEligible(i))
                && (a.Hex.Equals(target.TargetHex)
                    || (a.CurrentMovement > 0
                        && SafeStepPathing.FindNextSafeStep(
                            ctx.Map, a, target.TargetHex).HasValue));
            }

            // A garrison-extraction candidate can pass IsCandidateEligible (a sparable hero exists)
            // yet still have no viable container, so walk the ranked list in order and keep trying
            // until one candidate actually produces a builder — never give up on the first
            // candidate while other eligible ones remain. No mutation happens here. The loop finds
            // either a hero that ALREADY exists as a real field mover (direct-army candidates) or,
            // for a garrison candidate, a viable GarrisonExtractionCandidate PLAN plus a
            // conservative pre-mutation cost estimate — never both. The plan's real materialization
            // (ArmyActions.CreateArmy/TransferMember) happens later, in
            // TaskExecutor.MaterializeEconomyGarrisonBuilder, inside that step's own
            // beforeStep/afterStep window — see the synthetic-MoverArmyId return further down.
            DemandLayer.EconomyBuilderChoice builderChoice = null;
            ArmyData hero = null;
            ArmyData deferredGarrison = null;
            GarrisonExtractionCandidate deferredPlan = default;
            EconomyCompletionPlan deferredPreparation = default;
            float ecoApEnvelopeRemaining = funded.Tentative.Ap;
            float rawApRemaining = root.ActionPoints - session.ApClaimed;
            float eps = AiConfigV2.allocatorSliceEpsilon;
            // The cheapest garrison-extraction cost seen across every candidate that was
            // structurally legal but rejected only for exceeding the AP envelope/pool. If the loop
            // ends with no builder AND this is set, the failure is a funding shortfall, not "no
            // legal way to get a builder" — the caller below reports EnvelopeTooSmall(requiredAp)
            // instead of a generic NoMoverExists.
            float? economyBuilderShortfallAp = null;
            void TrackEconomyShortfall(float requiredAp) => economyBuilderShortfallAp =
                economyBuilderShortfallAp.HasValue
                    ? Mathf.Min(economyBuilderShortfallAp.Value, requiredAp) : requiredAp;
            foreach (DemandLayer.EconomyBuilderChoice candidate in eligibleBuilders
                .OrderBy(x => m.PreferredMoverArmyId == x.Route.ArmyId ? 0 : 1)
                .Where(IsCandidateEligible))
            {
                if (candidate.Route.RequiresGarrisonExtraction)
                {
                    ArmyData candidateGarrison = ResolveArmy(player, candidate.Route.ArmyId);
                    if (candidateGarrison == null)
                        continue;
                    GarrisonExtractionCandidate plan = ResolveGarrisonExtractionCandidate(
                        player, candidateGarrison, actorCommitments, session,
                        root, ecoApEnvelopeRemaining);
                    if (plan.Tier == GarrisonExtractionTier.None)
                    {
                        // plan.ApCost is 0f only for a true non-existence (no sparable hero); a
                        // positive value here is the cheapest tier's real cost, rejected purely for
                        // exceeding ecoApEnvelopeRemaining (see ResolveGarrisonExtractionCandidate).
                        if (plan.ApCost > 0f)
                            TrackEconomyShortfall(plan.ApCost);
                        continue;
                    }
                    // Cheap pre-check before paying for a full composition search: ONLY the one
                    // cost that is unconditionally real regardless of hex/turn specifics (creating
                    // the container, or the hero's own late-join charge into an already-activated
                    // Shell/Host). Hero.ActivationApCost and the build/followup cost are
                    // deliberately NOT added — both can be zero in the real plan (no travel, or the
                    // build cannot complete this stage), so adding them would stop this being a
                    // true lower bound and could reject an affordable candidate before
                    // PlanEconomyCompletion looks at it.
                    float roughEstimate = plan.ApCost;
                    if (roughEstimate > ecoApEnvelopeRemaining + eps
                        || roughEstimate > rawApRemaining + eps)
                    {
                        // Passed ResolveGarrisonExtractionCandidate's own envelope check but not the
                        // rawApRemaining one (session.ApClaimed by other missions this same pass,
                        // which the resolver cannot see) — still a real funding shortfall, not a
                        // structural impossibility.
                        TrackEconomyShortfall(roughEstimate);
                        continue;
                    }

                    // Compute and PIN the FULL preparation plan (composition, donor, authoritative
                    // AP, resource stage cost) here, against a read-only preview of the
                    // not-yet-real container (BuildGarrisonExtractionPreview) — the SAME
                    // PlanEconomyCompletion the direct-army path uses below, so Execution never
                    // re-plans, only re-validates this exact decision and applies it. `plan.ApCost`
                    // is passed as `alreadyCommittedApCost` so the feasibility checks inside see
                    // the budget reduced by the extraction cost this candidate will ALSO pay — see
                    // that parameter.
                    ArmyData preview = BuildGarrisonExtractionPreview(player, candidateGarrison, plan);
                    int identityArmyId = plan.Container?.Id ?? -1;
                    EconomyCompletionPlan prep = PlanEconomyCompletion(player, root, ctx,
                        session.Snapshot, standingIntents, key, target, candidate, preview,
                        identityArmyId, ecoApEnvelopeRemaining, rawApRemaining, plan.ApCost);
                    if (!prep.Feasible)
                        continue;

                    deferredGarrison = candidateGarrison;
                    deferredPlan = plan;
                    deferredPreparation = prep;
                    builderChoice = candidate;
                    break;
                }
                ArmyData a = ResolveArmy(player, candidate.Route.ArmyId);
                if (a == null)
                    continue;
                builderChoice = candidate;
                hero = a;
                break;
            }

            if (deferredGarrison != null)
            {
                // Preflight the canonical Continuity site lease before spending a builder
                // extraction AP or reserving completion resources for an incompatible project.
                int candidateBuilder = deferredPlan.Container?.Id ?? -1;
                if (!MissionContinuityLayer.CanGrantEconomyBuildSite(player, target.TargetHex,
                        currentIntentKey, candidateBuilder, target.BuildCard))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site already leased by another active project"));

                // Reserve the physical build-stage resources NOW, when this mission commits to
                // being deferred, not only after it materializes in Execution: otherwise a SECOND
                // Economy mission provisioned later in the same batch pass sees the pool as still
                // free and claims the same resources StrategicSpendability.FitsSpendableResources
                // already approved for this one. A later stale/failure in Execution releases them
                // through ReleaseEconomyReservation via ReservationOwner.
                if (deferredPreparation.CompletionThisTurn)
                    InfrastructureFulfillment.ReserveEconomyCost(player, ctx.TurnNumber,
                        deferredPreparation.OwnerKey, target.BuildResourceCost, target.BuildApCost);

                // Nothing mutated — transferredMemberCount 0 is honest (ProvisioningResult.Ok's own
                // "changed = transferredMemberCount > 0" rule). MoverArmyId is a synthetic negative
                // id, same pattern ScoutExecutorKind.AirLaunch already uses for an actor that does
                // not exist yet; TaskExecutor.RunEconomyStep detects EconomyExtractionGarrisonArmyId
                // >= 0 and materializes for real before doing anything else.
                return ProvisioningResult.Ok(new ProvisionedMission
                {
                    Mission = m, Key = key, Kind = MissionKind.Economy,
                    MoverArmyId = SyntheticGarrisonExtractionActorId(deferredGarrison.Id),
                    EconomyExtractionGarrisonArmyId = deferredGarrison.Id,
                    EconomyExtractionPlan = deferredPlan,
                    EconomyExtractionPreparation = deferredPreparation,
                    EconomyPreparationPending = true,
                    FocusHex = target.TargetHex, ExecutionHex = deferredGarrison.Hex,
                    EconomyTarget = target,
                    // ClaimedAp is the TOTAL funded amount: deferredPlan.ApCost (CreateArmy, or the
                    // hero's own late-join charge into an already-activated Shell/Host) plus the
                    // preparation's RealAp, so the envelope Execution re-validates against includes
                    // the extraction AP — see PlanEconomyCompletion's `alreadyCommittedApCost` for
                    // why that AP is real and separate.
                    ClaimedAp = deferredPlan.ApCost + deferredPreparation.RealAp,
                    ClaimedPhysical = CostVector(deferredPreparation.StageCost),
                    ReservationOwner = deferredPreparation.OwnerKey,
                });
            }

            if (hero == null)
            {
                if (m.FromDurableIntent && m.PreferredMoverArmyId.HasValue)
                {
                    int preferredId = m.PreferredMoverArmyId.Value;
                    ArmyData preferredArmy = ResolveArmy(player, preferredId);

                    if (preferredArmy == null)
                        return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                            $"committed economy builder #{preferredId} no longer exists"));

                    // Canonical, army-specific safe-route witness (same contract EconomyBuilderRoutes
                    // uses at Analysis time — MaxMovement, not this turn's remaining CurrentMovement,
                    // so a merely-spent-for-now mover is never misclassified as unreachable). When
                    // this is int.MaxValue the committed mover has no safe path at all right now, as
                    // distinct from "a path exists but this mover didn't clear the other eligibility
                    // checks this turn" — the latter stays MoverContended below, unchanged.
                    int routeCost = SafeStepPathing.FindSafePathCost(
                        ctx.Map, preferredArmy, target.TargetHex);
                    if (routeCost == int.MaxValue)
                        return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                            $"committed economy builder #{preferredId} has no safe route to the site right now"));

                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        $"committed economy builder #{preferredId} cannot advance this turn"));
                }
                // DIAGNOSTIC (FoundBase only) — the durable-mover trace above never fires here:
                // this is reached only once a fresh Economy mission's FirstOrDefault predicate
                // rejected every ranked candidate (or rankedBuilders was empty). Replicate that
                // exact predicate per candidate, read-only, so the single clause eating each one is
                // visible.
                if (target.Kind == EconomyTaskKind.FoundBase)
                {
                    AiDebugLog.Write($"[AI][V2][Economy][TRACE] {player?.Nickname} fresh economy mission "
                        + $"kind={target.Kind} target=({target.TargetHex.Q},{target.TargetHex.R}) "
                        + $"turn={session.Snapshot?.TurnNumber} — rankedBuildersTotal={rankedBuilders.Count}");
                    foreach (DemandLayer.EconomyBuilderChoice x in rankedBuilders)
                    {
                        if (x.Route.RequiresGarrisonExtraction)
                        {
                            ArmyData g = ResolveArmy(player, x.Route.ArmyId);
                            UnitData sparable = g == null ? null
                                : AiArmyRoles.BestSparableEconomyHero(player, g);
                            bool cThreatG = g != null
                                && !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, g.Hex);
                            bool cClaimedG = g != null && !session.ClaimedArmyIds.Contains(g.Id);
                            bool cPathG = g != null && sparable != null && (g.Hex.Equals(target.TargetHex)
                                || SafeStepPathing.FindSafePathCost(
                                    ctx.Map, player, g.Hex, target.TargetHex, sparable.MoveMax) != int.MaxValue);
                            // Calls the SAME pure resolver the real extraction path uses
                            // (ResolveGarrisonExtractionCandidate + ApplyGarrisonExtraction) — the
                            // trace must never keep its own copy of the container search, which
                            // would drift out of sync.
                            GarrisonExtractionCandidate containerPlan = g == null
                                ? GarrisonExtractionCandidate.No("garrison not resolved")
                                : ResolveGarrisonExtractionCandidate(player, g, actorCommitments,
                                    session, root, funded.Tentative.Ap);
                            bool cContainerG = containerPlan.Tier != GarrisonExtractionTier.None;
                            bool shallowEligibleG = g != null && sparable != null && cThreatG
                                && cClaimedG && cPathG && cContainerG;
                            // The shallow gates above (mirrored from IsCandidateEligible +
                            // ResolveGarrisonExtractionCandidate) are NOT the whole real gate: the
                            // real loop also runs the roughEstimate AP pre-check and the full
                            // PlanEconomyCompletion (donor loan, real path for the PREVIEW army,
                            // composition/lightening, authoritative AP, StrategicSpendability)
                            // before accepting a candidate. Replicate the SAME two downstream
                            // checks, read-only, so the real rejection reason is visible instead of
                            // falling through to the generic NoMoverExists below (see
                            // docs/ai-economy-mover-materialization-decision-tree.md, "Known
                            // trap").
                            string prepDetailG = "n/a";
                            bool prepFeasibleG = false;
                            if (shallowEligibleG)
                            {
                                float roughEstimateG = containerPlan.ApCost;
                                if (roughEstimateG > ecoApEnvelopeRemaining + eps
                                    || roughEstimateG > rawApRemaining + eps)
                                {
                                    prepDetailG = $"RoughEstimateTooBig ap={roughEstimateG:0.##} "
                                        + $"ecoEnvelope={ecoApEnvelopeRemaining:0.##} rawPool={rawApRemaining:0.##}";
                                }
                                else
                                {
                                    ArmyData previewG = BuildGarrisonExtractionPreview(player, g, containerPlan);
                                    int identityArmyIdG = containerPlan.Container?.Id ?? -1;
                                    EconomyCompletionPlan prepG = PlanEconomyCompletion(player, root, ctx,
                                        session.Snapshot, standingIntents, key, target, x, previewG,
                                        identityArmyIdG, ecoApEnvelopeRemaining, rawApRemaining, containerPlan.ApCost);
                                    prepFeasibleG = prepG.Feasible;
                                    prepDetailG = prepG.Feasible
                                        ? $"Feasible realAp={prepG.RealAp:0.##} completionThisTurn={prepG.CompletionThisTurn}"
                                        : $"{prepG.Failure.Kind} — {prepG.Failure.Detail}";
                                }
                            }
                            AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} (garrison-extraction) "
                                + $"resolved={g != null} sparableHero={sparable != null} "
                                + $"notUnderImmediateThreat={cThreatG} notClaimedThisPass={cClaimedG} hasPath={cPathG} "
                                + $"container={(cContainerG ? containerPlan.Tier.ToString() : containerPlan.Reason)} "
                                + (cContainerG ? $"containerApCost={containerPlan.ApCost:0.##} " : "")
                                + $"shallowEligible={shallowEligibleG} plan=[{prepDetailG}] "
                                + $"=> ELIGIBLE={shallowEligibleG && prepFeasibleG}");
                            continue;
                        }
                        ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                        if (a == null)
                        {
                            AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} — ResolveArmy returned null");
                            continue;
                        }
                        bool cMobile = IsMobileEconomyHero(a, player);
                        bool cThreat = !DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex);
                        bool cClaimed = !session.ClaimedArmyIds.Contains(a.Id);
                        MissionIntent conflicting = standingIntents.FirstOrDefault(i => i.PreferredMoverArmyId == a.Id
                            && !i.IntentKey.Equals(currentIntentKey)
                            && !DemandLayer.EconomyDonorStructurallyEligible(i));
                        bool cDonorConflict = conflicting == null;
                        bool atTarget = a.Hex.Equals(target.TargetHex);
                        HexCoord? nextStep = atTarget
                            ? (HexCoord?)null
                            : SafeStepPathing.FindNextSafeStep(ctx.Map, a, target.TargetHex);
                        bool cPath = atTarget || (a.CurrentMovement > 0 && nextStep.HasValue);
                        bool shallowEligible = cMobile && cThreat && cClaimed && cDonorConflict && cPath;
                        // Same reasoning as the garrison-extraction branch above: the shallow
                        // checks here mirror only IsCandidateEligible, not the full
                        // PlanEconomyCompletion the real loop runs against this army once selected.
                        // Replicate that final gate too so a direct-army candidate that looks
                        // ELIGIBLE here but fails on donor loan / AP / spendable resources shows
                        // its real reason.
                        string prepDetail = "n/a";
                        bool prepFeasible = false;
                        if (shallowEligible)
                        {
                            EconomyCompletionPlan prep = PlanEconomyCompletion(player, root, ctx,
                                session.Snapshot, standingIntents, key, target, x, a, a.Id,
                                ecoApEnvelopeRemaining, rawApRemaining);
                            prepFeasible = prep.Feasible;
                            prepDetail = prep.Feasible
                                ? $"Feasible realAp={prep.RealAp:0.##} completionThisTurn={prep.CompletionThisTurn}"
                                : $"{prep.Failure.Kind} — {prep.Failure.Detail}";
                        }
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{a.Id} hex=({a.Hex.Q},{a.Hex.R}) "
                            + $"currentMovement={a.CurrentMovement} maxMovement={a.MaxMovement} "
                            + $"isMobileEconomyHero={cMobile} notUnderImmediateThreat={cThreat} "
                            + $"notClaimedThisPass={cClaimed} noConflictingIntent={cDonorConflict}"
                            + (conflicting != null ? $" (conflictsWith={conflicting.IntentKey} kind={conflicting.Kind} status={conflicting.Status})" : "")
                            + $" atTargetHex={atTarget} hasSafeNextStep={(atTarget ? (object)"n/a" : nextStep.HasValue)} "
                            + $"shallowEligible={shallowEligible} plan=[{prepDetail}] "
                            + $"=> ELIGIBLE={shallowEligible && prepFeasible}");
                    }
                }
                // A structurally legal container existed (Shell/Host/Create) for at least one
                // candidate this pass and was rejected only for exceeding the AP envelope/pool:
                // that is a repriceable funding shortfall, not a genuine absence of any way to get
                // a builder. RepriceThisTurn lets the allocator fund it instead of the candidate
                // retrying next turn under RetryNextTurn with no larger envelope.
                if (economyBuilderShortfallAp.HasValue)
                    return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(
                        economyBuilderShortfallAp.Value,
                        $"garrison-extraction builder needs {economyBuilderShortfallAp.Value:0.##} AP, "
                        + "envelope/pool too small"));
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "no free hero can advance toward economy site"));
            }

            // The direct-army path (hero already a real, live field army) applies no composition
            // change inside Provisioning either: PlanEconomyCompletion (pure) decides, and the SAME
            // Execution apply path the garrison-extraction candidate uses
            // (TaskExecutor.ApplyEconomyPreparation) commits it — no live-army mutation happens
            // inside Provisioning for ANY Economy actor. `identityArmyId = hero.Id` since the hero
            // is already real.
            EconomyCompletionPlan directPrep = PlanEconomyCompletion(player, root, ctx,
                session.Snapshot, standingIntents, key, target, builderChoice, hero, hero.Id,
                funded.Tentative.Ap, root.ActionPoints - session.ApClaimed);
            if (!directPrep.Feasible)
                return ProvisioningResult.Fail(directPrep.Failure);

            if (!MissionContinuityLayer.CanGrantEconomyBuildSite(player, target.TargetHex,
                    currentIntentKey, hero.Id, target.BuildCard))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "economy build site already leased by another active project"));

            // Reserve the physical stage cost NOW (same cross-mission-visibility reasoning as the
            // deferred garrison-extraction branch above) — whether or not composition/donor work is
            // still pending, this mission has committed to this build.
            if (directPrep.CompletionThisTurn)
                InfrastructureFulfillment.ReserveEconomyCost(player, ctx.TurnNumber,
                    directPrep.OwnerKey, target.BuildResourceCost, target.BuildApCost);
            else
            {
                // This hero cannot finish the build this turn, so it is a multi-turn delivery
                // starting or continuing. Give Continuity a durable identity for it (mirrors the
                // Hero-materialization path in CapabilityDeliveryEvaluator) so StrategicPhaseA's
                // protectedActiveEconomyBuild protects the full H/E/M/T vector every later turn
                // regardless of remaining travel — rather than
                // InfrastructureFulfillment.ShouldReserveDeferredEconomyResources' one-turn horizon
                // protecting unconditionally on every turn of the walk and freezing resources too
                // early.
                MissionIntent delivery = MissionContinuityLayer.BeginEconomyDelivery(player, new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = target.Kind == EconomyTaskKind.FoundBase
                        ? CapabilityKind.EconomicExpansionBase : CapabilityKind.EconomicInfrastructure,
                    TargetHex = target.TargetHex,
                    EconomyResourceType = target.ResourceType,
                    EconomyBuildCard = target.BuildCard,
                    EconomyBuildResourceCost = target.BuildResourceCost,
                    EconomyBuildApCost = target.BuildApCost,
                    MinimumFollowupAp = target.MinimumFollowupAp,
                    EconomySiteValue = target.BuildValue,
                    // The admitted mission's canonical TaskScore — the intent's IntrinsicValue.
                    // Without it the handoff recorded 0 (not "unknown"), and a later turn with no
                    // refreshed demand priced the durable build at zero (Economy audit B9).
                    Value = m.BaseValue,
                }, hero.Id, ctx.TurnNumber);
                if (delivery == null)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site lease changed during provisioning"));
            }

            // "Pending" means Execution still has real work to do before movement: an actual
            // composition change, OR a donor loan that must be suspended (bookkeeping only, but
            // still not something Provisioning may do — see PlanEconomyCompletion's own comment on
            // why it stays read-only). When neither applies the hero's roster is already exactly
            // right and there is nothing to defer — this mission proceeds straight to movement
            // exactly as it always has, no extra admission pass spent on a step that would mutate
            // nothing.
            bool preparationPending = directPrep.Donor != null
                || directPrep.Unload.Count > 0 || directPrep.Reinforcement.Count > 0;

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = m, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = hero.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                EconomyPreparationPending = preparationPending,
                EconomyExtractionPreparation = directPrep,
                ClaimedAp = directPrep.RealAp,
                ClaimedPhysical = CostVector(directPrep.StageCost),
                ReservationOwner = directPrep.OwnerKey,
            });
        }

        private static ProvisioningResult ProvisionEconomyRecovery(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target, StableMissionKey key)
        {
            MissionProposal mission = funded.Mission;
            int? preferredId = mission.PreferredMoverArmyId ?? target.BuilderArmyId;
            if (!preferredId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "return-builder mission has no preferred actor"));

            ArmySnapshot actorSnapshot = session.Snapshot?.Self?.Armies?
                .FirstOrDefault(a => a != null && a.ArmyId == preferredId.Value);
            if (!IsEligibleEconomyRecoveryActor(mission, actorSnapshot))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"preferred return builder #{preferredId.Value} is no longer eligible"));

            ArmyData actor = ResolveArmy(player, preferredId.Value);
            if (actor == null || actor.Owner != player
                || !actor.Members.Any(u => u != null && u.IsHero))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"preferred return builder #{preferredId.Value} no longer exists"));
            BuildingData shelter = BuildingRegistry.FindAt(target.TargetHex);
            bool isOwnCitadel = player.CitadelHexQ == target.TargetHex.Q
                && player.CitadelHexR == target.TargetHex.R;
            if (shelter == null || shelter.Owner != player
                || (!shelter.IsBase && !isOwnCitadel))
                return ProvisioningResult.Fail(ProvisionFailure.TargetInvalidated(
                    $"recovery shelter ({target.TargetHex.Q},{target.TargetHex.R}) is no longer protected"));
            if (actor.Hex.Equals(target.TargetHex))
                return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                    $"return builder #{actor.Id} already protected"));
            if (actor.CurrentMovement <= 0)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"return builder #{actor.Id} has no movement"));
            if (!SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.TargetHex).HasValue
                || SafeStepPathing.FindSafePathCost(ctx.Map, actor, target.TargetHex) == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"no safe recovery route for builder #{actor.Id}"));

            float activation = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
            if (activation > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activation,
                    $"return builder #{actor.Id} needs {activation:0.##} AP"));
            if (activation > root.ActionPoints - session.ApClaimed
                + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "recovery activation AP no longer available"));

            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = actor.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                ClaimedAp = activation, ClaimedPhysical = ResourceVector.Zero,
                ReservationOwner = null,
            });
        }

        private static ProvisioningResult ProvisionMobileCollection(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded,
            EconomyMissionTarget target, StableMissionKey key)
        {
            MissionProposal mission = funded.Mission;
            int? actorId = mission.PreferredMoverArmyId ?? target.CollectorArmyId;
            if (!actorId.HasValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoMoverExists(
                    "mobile collection mission has no pinned collector"));
            ArmySnapshot frozen = session.Snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == actorId.Value);
            bool returning = target.Kind == EconomyTaskKind.ReturnCollector;
            if (frozen == null || frozen.IsAir || frozen.IsAirfield || frozen.IsGarrison
                || frozen.IsPrison || (!returning && (!target.ResourceType.HasValue
                    || frozen.CollectionCapacity.Get(target.ResourceType.Value) <= 0f)))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"collector #{actorId.Value} is no longer eligible"));
            ArmyData actor = ResolveArmy(player, actorId.Value);
            if (actor == null || actor.Owner != player || actor.IsAirArmy || actor.IsAirfield
                || actor.IsGarrison || actor.IsPrison)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"collector #{actorId.Value} no longer exists"));
            if (session.ClaimedArmyIds.Contains(actor.Id))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    $"collector #{actor.Id} already claimed this pass"));
            if (actor.Hex.Equals(target.TargetHex))
            {
                if (returning)
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied(
                        $"collector #{actor.Id} already protected"));
                return ProvisioningResult.Ok(new ProvisionedMission
                {
                    Mission = mission, Key = key, Kind = MissionKind.Economy,
                    MoverArmyId = actor.Id, FocusHex = target.TargetHex,
                    ExecutionHex = target.TargetHex, EconomyTarget = target,
                    ClaimedAp = 0f, ClaimedPhysical = ResourceVector.Zero,
                });
            }
            if (actor.CurrentMovement <= 0
                || !SafeStepPathing.FindNextSafeStep(ctx.Map, actor, target.TargetHex).HasValue
                || SafeStepPathing.FindSafePathCost(ctx.Map, actor, target.TargetHex) == int.MaxValue)
                return ProvisioningResult.Fail(ProvisionFailure.NoExecutableStep(
                    $"collector #{actor.Id} has no safe executable step"));
            float activation = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
            if (activation > funded.Tentative.Ap + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(activation,
                    $"collector #{actor.Id} needs {activation:0.##} AP"));
            if (root == null || activation > root.ActionPoints - session.ApClaimed
                + AiConfigV2.allocatorSliceEpsilon)
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "collector activation AP no longer available"));
            return ProvisioningResult.Ok(new ProvisionedMission
            {
                Mission = mission, Key = key, Kind = MissionKind.Economy,
                MoverArmyId = actor.Id, FocusHex = target.TargetHex,
                ExecutionHex = target.TargetHex, EconomyTarget = target,
                ClaimedAp = activation, ClaimedPhysical = ResourceVector.Zero,
            });
        }
    }
}
