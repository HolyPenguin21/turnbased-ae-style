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

            float ecoApEnvelopeRemaining = funded.Tentative.Ap;
            float rawApRemaining = root.ActionPoints - session.ApClaimed;
            float eps = AiConfigV2.allocatorSliceEpsilon;

            // Why one ranked candidate cannot be this mission's builder right now, or null when it
            // can. The ONE eligibility gate of the selection loop below; the FoundBase diagnostic
            // traces print this same answer instead of re-deriving the clauses.
            string CandidateRejection(DemandLayer.EconomyBuilderChoice x)
            {
                // Another mission holding this actor — a field army, or a garrison whose hero an
                // Economy / Development intent has pinned through the garrison's id.
                MissionIntent conflicting = standingIntents.FirstOrDefault(i =>
                    i.PreferredMoverArmyId == x.Route.ArmyId
                    && !i.IntentKey.Equals(currentIntentKey)
                    && !DemandLayer.EconomyDonorStructurallyEligible(i));
                if (conflicting != null)
                    return $"conflicts_with={conflicting.IntentKey}({conflicting.Kind},{conflicting.Status})";
                if (x.Route.RequiresGarrisonExtraction)
                {
                    // ArmyId here names the Garrison, not yet a separate mover — re-derive the
                    // exact same candidate AiArmyRoles.BestSparableEconomyHero would give
                    // Analysis right now (canonical, same predicate as CanSpareGarrisonMember),
                    // never trusting a hero identity carried across from an earlier phase.
                    ArmyData g = ResolveArmy(player, x.Route.ArmyId);
                    if (g == null) return "garrison_not_resolved";
                    UnitData sparable = AiArmyRoles.BestSparableEconomyHero(player, g);
                    if (sparable == null) return "no_sparable_hero";
                    if (DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, g.Hex))
                        return "under_immediate_threat";
                    if (session.ClaimedArmyIds.Contains(g.Id)) return "claimed_this_pass";
                    if (!g.Hex.Equals(target.TargetHex)
                        && SafeStepPathing.FindSafePathCost(ctx.Map, player, g.Hex,
                            target.TargetHex, sparable.MoveMax) == int.MaxValue)
                        return "no_safe_path";
                    return null;
                }
                ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                if (a == null) return "army_not_resolved";
                if (!IsMobileEconomyHero(a, player)) return "not_mobile_economy_hero";
                if (DemandLayer.EconomyBuilderUnderImmediateThreat(session.Snapshot, a.Hex))
                    return "under_immediate_threat";
                if (session.ClaimedArmyIds.Contains(a.Id)) return "claimed_this_pass";
                if (!a.Hex.Equals(target.TargetHex)
                    && (a.CurrentMovement <= 0
                        || !SafeStepPathing.FindNextSafeStep(ctx.Map, a, target.TargetHex).HasValue))
                    return "no_executable_step";
                return null;
            }

            // One evaluation of an eligible garrison-extraction candidate: the container the hero
            // would move into, the FULL preparation plan pinned against a read-only preview of it,
            // or why it fails — with the AP it would have needed when funding is the only obstacle.
            // Shared by the selection loop and the FoundBase diagnostic trace.
            (ArmyData Garrison, GarrisonExtractionCandidate Plan, EconomyCompletionPlan Prep,
                float? ShortfallAp, string Detail) EvaluateGarrisonCandidate(
                    DemandLayer.EconomyBuilderChoice candidate)
            {
                ArmyData g = ResolveArmy(player, candidate.Route.ArmyId);
                if (g == null)
                    return (null, default, default, null, "garrison_not_resolved");
                GarrisonExtractionCandidate plan = ResolveGarrisonExtractionCandidate(
                    player, g, actorCommitments, session, root, ecoApEnvelopeRemaining);
                if (plan.Tier == GarrisonExtractionTier.None)
                    // plan.ApCost is 0f only for a true non-existence (no sparable hero); a
                    // positive value is the cheapest tier's real cost, rejected purely for
                    // exceeding ecoApEnvelopeRemaining (see ResolveGarrisonExtractionCandidate).
                    return (g, plan, default, plan.ApCost > 0f ? plan.ApCost : (float?)null,
                        plan.Reason);
                // Cheap pre-check before paying for a full composition search: ONLY the one cost
                // that is unconditionally real regardless of hex/turn specifics (creating the
                // container, or the hero's own late-join charge into an already-activated
                // Shell/Host). Hero.ActivationApCost and the build/followup cost are deliberately
                // NOT added — both can be zero in the real plan (no travel, or the build cannot
                // complete this stage), so adding them would stop this being a true lower bound.
                // Failing the raw pool (session.ApClaimed by other missions this same pass, which
                // the resolver cannot see) is still a funding shortfall, not an impossibility.
                float roughEstimate = plan.ApCost;
                if (roughEstimate > ecoApEnvelopeRemaining + eps
                    || roughEstimate > rawApRemaining + eps)
                    return (g, plan, default, roughEstimate,
                        $"RoughEstimateTooBig ap={roughEstimate:0.##} "
                        + $"ecoEnvelope={ecoApEnvelopeRemaining:0.##} rawPool={rawApRemaining:0.##}");
                // Compute and PIN the FULL preparation plan (composition, donor, authoritative AP,
                // resource stage cost) against a read-only preview of the not-yet-real container
                // (BuildGarrisonExtractionPreview) — the SAME PlanEconomyCompletion the direct-army
                // path uses, so Execution never re-plans, only re-validates this exact decision.
                // `plan.ApCost` is passed as `alreadyCommittedApCost` so the feasibility checks see
                // the budget reduced by the extraction cost this candidate will ALSO pay.
                ArmyData preview = BuildGarrisonExtractionPreview(player, g, plan);
                EconomyCompletionPlan prep = PlanEconomyCompletion(player, root, ctx,
                    session.Snapshot, standingIntents, key, target, candidate, preview,
                    plan.Container?.Id ?? -1, ecoApEnvelopeRemaining, rawApRemaining, plan.ApCost);
                return (g, plan, prep, null, prep.Feasible
                    ? $"Feasible realAp={prep.RealAp:0.##} completionThisTurn={prep.CompletionThisTurn}"
                    : $"{prep.Failure.Kind} — {prep.Failure.Detail}");
            }

            // DIAGNOSTIC (FoundBase only) — which gate rejects a durable intent's committed mover,
            // since the selection loop normally folds all of them into one MoverContended result.
            // Prints the real gates' answers (DemandLayer's candidate gate, CandidateRejection).
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
                    AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{preferredId} upstream — "
                        + DemandLayer.EconomyBuilderCandidateRejection(session.Snapshot,
                            target.TargetHex, target.BuilderRoutes, preferredId,
                            standingIntents, actorCommitments));
                foreach (DemandLayer.EconomyBuilderChoice x in eligibleList)
                    AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId} "
                        + $"rejection={CandidateRejection(x) ?? "none"}");
            }

            // A garrison-extraction candidate can pass the eligibility gate (a sparable hero exists)
            // yet still have no viable container, so walk the ranked list in order and keep trying
            // until one candidate actually produces a builder — never give up on the first
            // candidate while other eligible ones remain. No mutation happens here. The loop finds
            // either a hero that ALREADY exists as a real field mover (direct-army candidates) or,
            // for a garrison candidate, a viable GarrisonExtractionCandidate PLAN plus its pinned
            // preparation — never both. The plan's real materialization
            // (ArmyActions.CreateArmy/TransferMember) happens later, in
            // TaskExecutor.ApplyEconomyPreparation, inside that step's own
            // beforeStep/afterStep window — see the synthetic-MoverArmyId return further down.
            DemandLayer.EconomyBuilderChoice builderChoice = null;
            ArmyData hero = null;
            ArmyData deferredGarrison = null;
            GarrisonExtractionCandidate deferredPlan = default;
            EconomyCompletionPlan deferredPreparation = default;
            // The cheapest garrison-extraction cost seen across every candidate that was
            // structurally legal but rejected only for exceeding the AP envelope/pool. If the loop
            // ends with no builder AND this is set, the failure is a funding shortfall, not "no
            // legal way to get a builder" — the caller below reports EnvelopeTooSmall(requiredAp)
            // instead of a generic NoMoverExists.
            float? economyBuilderShortfallAp = null;
            foreach (DemandLayer.EconomyBuilderChoice candidate in eligibleBuilders
                .OrderBy(x => m.PreferredMoverArmyId == x.Route.ArmyId ? 0 : 1)
                .Where(x => CandidateRejection(x) == null))
            {
                if (candidate.Route.RequiresGarrisonExtraction)
                {
                    var evaluated = EvaluateGarrisonCandidate(candidate);
                    if (evaluated.ShortfallAp.HasValue)
                        economyBuilderShortfallAp = economyBuilderShortfallAp.HasValue
                            ? Mathf.Min(economyBuilderShortfallAp.Value, evaluated.ShortfallAp.Value)
                            : evaluated.ShortfallAp.Value;
                    if (evaluated.Garrison == null || !evaluated.Prep.Feasible)
                        continue;

                    deferredGarrison = evaluated.Garrison;
                    deferredPlan = evaluated.Plan;
                    deferredPreparation = evaluated.Prep;
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
                // DIAGNOSTIC (FoundBase only) — a fresh mission's selection loop rejected every
                // ranked candidate (or none was ranked). Print each candidate's real answer: the
                // eligibility gate, then the same garrison evaluation / completion plan the loop ran
                // (docs/ai-economy-mover-materialization-decision-tree.md, "Known trap").
                if (target.Kind == EconomyTaskKind.FoundBase)
                {
                    AiDebugLog.Write($"[AI][V2][Economy][TRACE] {player?.Nickname} fresh economy mission "
                        + $"kind={target.Kind} target=({target.TargetHex.Q},{target.TargetHex.R}) "
                        + $"turn={session.Snapshot?.TurnNumber} — rankedBuildersTotal={rankedBuilders.Count}");
                    foreach (DemandLayer.EconomyBuilderChoice x in rankedBuilders)
                    {
                        string rejection = CandidateRejection(x);
                        string plan = "n/a";
                        if (rejection == null)
                        {
                            if (x.Route.RequiresGarrisonExtraction)
                                plan = EvaluateGarrisonCandidate(x).Detail;
                            else
                            {
                                ArmyData a = ResolveArmy(player, x.Route.ArmyId);
                                EconomyCompletionPlan prep = PlanEconomyCompletion(player, root, ctx,
                                    session.Snapshot, standingIntents, key, target, x, a, a.Id,
                                    ecoApEnvelopeRemaining, rawApRemaining);
                                plan = prep.Feasible
                                    ? $"Feasible realAp={prep.RealAp:0.##} completionThisTurn={prep.CompletionThisTurn}"
                                    : $"{prep.Failure.Kind} — {prep.Failure.Detail}";
                            }
                        }
                        AiDebugLog.Write($"[AI][V2][Economy][TRACE]   #{x.Route.ArmyId}"
                            + (x.Route.RequiresGarrisonExtraction ? " (garrison-extraction)" : "")
                            + $" rejection={rejection ?? "none"} plan=[{plan}]");
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
