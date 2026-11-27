using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    // The Economy completion plan and its costing: Provisioning pins it, EconomyExecutor re-validates
    // it through PlanEconomyCompletion / EconomyMissionClaimedAp and applies its composition.

    internal static partial class ProvisioningManager
    {
        // The pure Economy completion admission: validate donor/route, materialize Demand's
        // pinned roster and check AP/resource feasibility. Kept separate so
        // Provisioning can compute and PIN this exact decision against a read-only preview (see
        // BuildGarrisonExtractionPreview) for a deferred garrison-extraction candidate — Execution
        // then only re-validates the volatile parts (AP, resources) and APPLIES the pinned
        // Unload/Reinforcement, never re-deriving them. The direct-army path (ProvisionEconomy)
        // calls this with the REAL live hero; every read here
        // (Hex/Members/MaxMovement/CurrentMovement/HasActivatedThisTurn) is satisfied the same way
        // by a real ArmyData or by the preview. `identityArmyId` is `hero.Id` for the direct-army
        // path; for a preview it is the REAL container id when one already exists (Shell/Host) or
        // -1 for Create (nothing can be bound to an army that does not exist yet).
        internal readonly struct EconomyCompletionPlan
        {
            public readonly bool Feasible;
            public readonly ProvisionFailure Failure;
            public readonly MissionIntent Donor;
            public readonly ArmyData Garrison;
            public readonly List<UnitData> Unload;
            public readonly List<UnitData> Reinforcement;
            public readonly bool TravelNeeded;
            public readonly bool CompletionThisTurn;
            public readonly ResourceCost StageCost;
            public readonly float RealAp;
            public readonly ReservationOwner OwnerKey;

            private EconomyCompletionPlan(bool feasible, ProvisionFailure failure,
                MissionIntent donor, ArmyData garrison, List<UnitData> unload,
                List<UnitData> reinforcement, bool travelNeeded, bool completionThisTurn,
                ResourceCost stageCost, float realAp, ReservationOwner ownerKey)
            {
                Feasible = feasible; Failure = failure; Donor = donor; Garrison = garrison;
                Unload = unload; Reinforcement = reinforcement; TravelNeeded = travelNeeded;
                CompletionThisTurn = completionThisTurn; StageCost = stageCost; RealAp = realAp;
                OwnerKey = ownerKey;
            }

            public static EconomyCompletionPlan No(ProvisionFailure failure) =>
                new EconomyCompletionPlan(false, failure, null, null, null, null,
                    false, false, null, 0f, null);
            public static EconomyCompletionPlan Yes(MissionIntent donor, ArmyData garrison,
                List<UnitData> unload, List<UnitData> reinforcement, bool travelNeeded,
                bool completionThisTurn, ResourceCost stageCost, float realAp, ReservationOwner ownerKey) =>
                new EconomyCompletionPlan(true, default, donor, garrison, unload, reinforcement,
                    travelNeeded, completionThisTurn, stageCost, realAp, ownerKey);
        }

        // `alreadyCommittedApCost` is the extraction AP a deferred garrison-extraction candidate's
        // OWN GarrisonExtractionCandidate.ApCost already accounts for (CreateArmy, or a hero
        // joining an already-activated Shell/Host) — a cost EconomyMissionClaimedAp cannot see,
        // since it only reads the projected roster's ActivationApCost sum. It is subtracted from
        // the envelope/pool BEFORE any feasibility check, so a candidate whose extraction cost
        // alone would blow the budget is rejected. The direct-army path (hero already real, no
        // extraction) passes the default 0.
        internal static EconomyCompletionPlan PlanEconomyCompletion(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> standingIntents, StableMissionKey key,
            EconomyMissionTarget target, EconomyBuilderSelection.EconomyBuilderChoice builderChoice,
            ArmyData hero, int identityArmyId, float apEnvelope, float apPoolRemaining,
            float alreadyCommittedApCost = 0f, ActorCommitments commitments = null,
            Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            // A caller that decides several candidates in one pass hands its own reads in (the same
            // intents, the same live duty site); alone, this plan reads them itself.
            commitments ??= ActorCommitments.FromIntents(standingIntents, snapshot, null);
            preparationSite ??= DevelopmentOpportunityEvaluator.LivePreparationSite(
                player, AiHandRegistry.Peek(player), ctx, snapshot?.Intents);
            apEnvelope -= alreadyCommittedApCost;
            apPoolRemaining -= alreadyCommittedApCost;
            float eps = AiConfigV2.allocatorSliceEpsilon;
            MissionIntent donor = standingIntents.FirstOrDefault(i => i != null
                && i.Kind != MissionKind.Economy && i.PreferredMoverArmyId == identityArmyId
                && EconomyBuilderSelection.EconomyDonorStructurallyEligible(i));
            if (!MaterializeEconomyRoster(player, hero, builderChoice,
                    out ArmyData garrison, out List<UnitData> lighteningPlan,
                    out List<UnitData> reinforcementPlan, snapshot?.Intents, ctx,
                    commitments, preparationSite))
                return EconomyCompletionPlan.No(ProvisionFailure.AssemblyInfeasible(
                    $"economy builder #{identityArmyId} preparation witness is stale"));
            // Operators the departure leaves in the local garrison (zero AP, same gate as Execution)
            // are not part of the walking roster.
            IReadOnlyList<UnitData> staying = hero.Hex.Equals(target.TargetHex) || hero.IsGarrison
                ? System.Array.Empty<UnitData>()
                : LocalOperatorRelease.OperatorsNeededHome(player, hero, preparationSite);
            List<UnitData> projectedMembers = hero.Members.Where(u => !lighteningPlan.Contains(u) && !staying.Contains(u))
                .Concat(reinforcementPlan).ToList();
            int projectedMovement = ArmyData.ComputeCurrentMovement(projectedMembers);
            int projectedMaxMovement = ArmyData.ComputeMaxMovement(projectedMembers);
            int distance = SafeStepPathing.FindSafePathCost(ctx.Map, player, hero.Hex,
                target.TargetHex, projectedMaxMovement);
            if (distance == int.MaxValue)
                return EconomyCompletionPlan.No(ProvisionFailure.NoExecutableStep("no safe economy route"));
            if (donor != null)
            {
                // Provisioning validates LIVE path/MP against the SAME Demand-owned loan
                // predicate. Reprice the already selected builder's projected roster from
                // current physical facts; never resurrect the old raw-hex distance scorer.
                EconomyBuilderRouteSnapshot liveRoute = builderChoice.Route;
                liveRoute.TravelCost = distance;
                liveRoute.CurrentMovement = projectedMovement;
                liveRoute.MaxMovement = projectedMaxMovement;
                liveRoute.HasActivatedThisTurn = hero.HasActivatedThisTurn;
                var liveChoice = new EconomyBuilderSelection.EconomyBuilderChoice
                {
                    Route = liveRoute,
                    TotalAssignmentApCost = builderChoice.PreparationApCost + EconomyBuilderSelection.EstimateEconomyAssignmentAp(
                        liveRoute, target.BuildApCost,
                        target.Kind == EconomyTaskKind.BuildExtraction),
                };
                if (!EconomyBuilderSelection.EconomyLoanAllowed(donor, target.BuildValue,
                        liveChoice, target.BuildApCost, out float loanNet))
                    return EconomyCompletionPlan.No(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }

            bool travelNeeded = !hero.Hex.Equals(target.TargetHex);
            bool completionThisTurn = distance <= projectedMovement;
            ResourceCost stageCost = completionThisTurn ? target.BuildResourceCost : null;
            float realAp = EconomyMissionClaimedAp(hero, target.BuildApCost,
                target.MinimumFollowupAp, staying.Count == 0 ? lighteningPlan
                    : lighteningPlan.Concat(staying).ToList(), reinforcementPlan,
                garrison, travelNeeded, completionThisTurn);
            if (realAp > apEnvelope + eps)
                return EconomyCompletionPlan.No(ProvisionFailure.EnvelopeTooSmall(realAp,
                    $"economy hero #{identityArmyId} needs {realAp:0.##} AP for "
                    + (completionThisTurn ? "delivery + completion" : "this travel stage")));
            if (realAp > apPoolRemaining + eps)
                return EconomyCompletionPlan.No(ProvisionFailure.MoverContended("economy AP no longer available"));

            ReservationOwner owner = EconomyMissionPlanner.ReservationIdentity(key);
            if (!TradeFunding.CanFund(player, root, ctx, stageCost, new SpendAuthority(owner, true)))
                return EconomyCompletionPlan.No(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, ResourceVector.Of(stageCost)),
                    "economy completion resources no longer spendable"));

            return EconomyCompletionPlan.Yes(donor, garrison, lighteningPlan, reinforcementPlan,
                travelNeeded, completionThisTurn, stageCost, realAp, owner);
        }

        // Materialization of Demand's exact choice. A changed identity/order/transfer contract
        // invalidates the witness; it never triggers a second roster optimizer here.
        internal static bool MaterializeEconomyRoster(PlayerSetupData player, ArmyData builder,
            EconomyBuilderSelection.EconomyBuilderChoice choice, out ArmyData garrison,
            out List<UnitData> unload, out List<UnitData> reinforcement, IMissionIntentView intents,
            AiTurnContext ctx = null, ActorCommitments commitments = null,
            Func<ResearchProductionMode, HexCoord?> preparationSite = null)
        {
            garrison = null;
            unload = new List<UnitData>();
            reinforcement = new List<UnitData>();
            if (builder == null || choice?.Army == null) return false;
            // Hero extraction has its own pinned container plan and is re-admitted after mutation.
            if (choice.Route.RequiresGarrisonExtraction && choice.PreparationGarrison == null) return true;
            List<UnitData> bodies = builder.Members.Where(u => u.IsGroundCombatant).ToList();
            if (!choice.Route.RequiresGarrisonExtraction && !RosterMatches(choice.Army, bodies)) return false;
            if (choice.PreparationGarrison == null) return true;
            garrison = AiV2Util.ResolveArmy(player, choice.PreparationGarrison.ArmyId);
            if (garrison == null || !garrison.IsGarrison || garrison.Owner != player
                || !garrison.Hex.Equals(builder.Hex) || garrison.HasActivatedThisTurn
                || !choice.Army.Hex.Equals(builder.Hex)) return false;
            List<UnitData> reserve = garrison.Members.Where(u => u.IsGroundCombatant).ToList();
            if (!RosterMatches(choice.PreparationGarrison, reserve)) return false;
            var retained = new HashSet<int>(choice.RetainedIndices);
            unload = choice.Route.RequiresGarrisonExtraction ? new List<UnitData>()
                : bodies.Where((u, index) => AiArmyRoles.IsGroundBattleBody(u)
                    && !retained.Contains(index)).ToList();
            if (choice.AddedIndices.Any(i => i < 0 || i >= reserve.Count)) return false;
            reinforcement = choice.AddedIndices.Select(i => reserve[i]).ToList();
            if (reinforcement.Count > 0
                && !AiArmyRoles.CanSpareGarrisonMembers(player, garrison, reinforcement)) return false;
            if ((unload.Count > 0 || reinforcement.Count > 0) && choice.Army.EconomyRosterProtected)
                return false;
            ArmyData departing = builder;
            ArmyData receiving = garrison;
            preparationSite = choice.Route.IsOnTarget ? null
                : preparationSite ?? DevelopmentOpportunityEvaluator.LivePreparationSite(
                    player, AiHandRegistry.Peek(player), ctx, intents);
            if (!choice.Route.IsOnTarget
                && LocalOperatorRelease.OperatorsNeededHome(player, builder, preparationSite).Count > 0)
            {
                if (!LocalOperatorRelease.CanKeepOperatorsHome(player, builder, commitments,
                        preparationSite, out _, out departing)) return false;
                receiving = ArmyData.CreateVisualSnapshot();
                receiving.Owner = garrison.Owner; receiving.Hex = garrison.Hex; receiving.IsGarrison = true;
                receiving.Members.AddRange(garrison.Members);
                receiving.Members.AddRange(builder.Members.Where(u => !departing.Members.Contains(u)));
            }
            return (unload.Count == 0 || ArmyActions.CanTransferMembers(unload, departing, receiving, out _))
                && (reinforcement.Count == 0 || ArmyActions.CanTransferMembers(reinforcement, receiving, departing, out _));
        }

        private static bool RosterMatches(ArmySnapshot snapshot, IReadOnlyList<UnitData> bodies) =>
            snapshot.Members.Count == bodies.Count
                && (snapshot.NonHeroRuntimeIds.Count == 0
                    || (snapshot.NonHeroRuntimeIds.SequenceEqual(bodies.Select(u => u.RuntimeId))
                        && snapshot.NonHeroActivationApCosts.SequenceEqual(bodies.Select(u => u.ActivationApCost))
                        && snapshot.NonHeroMoveMax.SequenceEqual(bodies.Select(WorldAnalysis.SoloMoveMax))
                        && snapshot.NonHeroCurrentMovement.SequenceEqual(
                            bodies.Select(WorldAnalysis.SoloMoveCurrent))));



        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded) =>
            EconomyMissionClaimedAp(builder, buildApCost, minimumFollowupAp, unloaded,
                added: null, unloadTarget: null, travelNeeded: true, completionThisTurn: true);

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded,
            bool travelNeeded, bool completionThisTurn)
            => EconomyMissionClaimedAp(builder, buildApCost, minimumFollowupAp,
                unloaded, added: null, unloadTarget: null,
                travelNeeded: travelNeeded, completionThisTurn: completionThisTurn);

        internal static float EconomyMissionClaimedAp(ArmyData builder, float buildApCost,
            float minimumFollowupAp, IReadOnlyCollection<UnitData> unloaded,
            IReadOnlyCollection<UnitData> added, ArmyData unloadTarget,
            bool travelNeeded, bool completionThisTurn)
        {
            float immediateTransfers = ArmyActions.TransferMembersApCost(added, builder)
                + ArmyActions.TransferMembersApCost(unloaded, unloadTarget);
            float activation = builder == null ? 0f : builder.Members
                    .Where(u => u != null && (unloaded == null || !unloaded.Contains(u)))
                    .Concat(added ?? System.Array.Empty<UnitData>())
                    .Distinct().Sum(u => u.ActivationApCost);
            return EconomyBuilderSelection.EconomyCurrentStageAp(immediateTransfers, activation,
                builder?.HasActivatedThisTurn == true, travelNeeded, completionThisTurn,
                buildApCost, minimumFollowupAp);
        }

    }
}

