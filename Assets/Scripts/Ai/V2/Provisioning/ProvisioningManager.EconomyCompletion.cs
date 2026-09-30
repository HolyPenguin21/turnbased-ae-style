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
    // The Economy completion plan and its costing/composition helpers — Provisioning's
    // contract with Execution. A mechanical partial of ProvisioningManager: TaskExecutor
    // replays the pinned decision through PlanEconomyCompletion, ApplyEconomyArmyLightening,
    // EconomyMissionClaimedAp and CostVector, and must keep finding them together.

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
            public readonly string OwnerKey;

            private EconomyCompletionPlan(bool feasible, ProvisionFailure failure,
                MissionIntent donor, ArmyData garrison, List<UnitData> unload,
                List<UnitData> reinforcement, bool travelNeeded, bool completionThisTurn,
                ResourceCost stageCost, float realAp, string ownerKey)
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
                bool completionThisTurn, ResourceCost stageCost, float realAp, string ownerKey) =>
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
            EconomyMissionTarget target, DemandLayer.EconomyBuilderChoice builderChoice,
            ArmyData hero, int identityArmyId, float apEnvelope, float apPoolRemaining,
            float alreadyCommittedApCost = 0f)
        {
            apEnvelope -= alreadyCommittedApCost;
            apPoolRemaining -= alreadyCommittedApCost;
            float eps = AiConfigV2.allocatorSliceEpsilon;
            MissionIntent donor = standingIntents.FirstOrDefault(i => i != null
                && i.Kind != MissionKind.Economy && i.PreferredMoverArmyId == identityArmyId
                && DemandLayer.EconomyDonorStructurallyEligible(i));
            if (!MaterializeEconomyRoster(player, hero, builderChoice,
                    out ArmyData garrison, out List<UnitData> lighteningPlan,
                    out List<UnitData> reinforcementPlan))
                return EconomyCompletionPlan.No(ProvisionFailure.AssemblyInfeasible(
                    $"economy builder #{identityArmyId} preparation witness is stale"));
            List<UnitData> projectedMembers = hero.Members.Where(u => !lighteningPlan.Contains(u))
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
                var liveChoice = new DemandLayer.EconomyBuilderChoice
                {
                    Route = liveRoute,
                    TotalAssignmentApCost = builderChoice.PreparationApCost + DemandLayer.EstimateEconomyAssignmentAp(
                        liveRoute, target.BuildApCost,
                        target.Kind == EconomyTaskKind.BuildExtraction),
                };
                if (!DemandLayer.EconomyLoanAllowed(donor, target.BuildValue,
                        liveChoice, target.BuildApCost, out float loanNet))
                    return EconomyCompletionPlan.No(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }

            bool travelNeeded = !hero.Hex.Equals(target.TargetHex);
            bool completionThisTurn = distance <= projectedMovement;
            ResourceCost stageCost = completionThisTurn ? target.BuildResourceCost : null;
            float realAp = EconomyMissionClaimedAp(hero, target.BuildApCost,
                target.MinimumFollowupAp, lighteningPlan, reinforcementPlan,
                garrison, travelNeeded, completionThisTurn);
            if (realAp > apEnvelope + eps)
                return EconomyCompletionPlan.No(ProvisionFailure.EnvelopeTooSmall(realAp,
                    $"economy hero #{identityArmyId} needs {realAp:0.##} AP for "
                    + (completionThisTurn ? "delivery + completion" : "this travel stage")));
            if (realAp > apPoolRemaining + eps)
                return EconomyCompletionPlan.No(ProvisionFailure.MoverContended("economy AP no longer available"));

            string owner = EconomyMissionPlanner.OwnerKey(key);
            if (!StrategicSpendability.FitsSpendableForEconomyCompletion(player, root, ctx, stageCost, owner))
                return EconomyCompletionPlan.No(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, CostVector(stageCost)),
                    "economy completion resources no longer spendable"));

            return EconomyCompletionPlan.Yes(donor, garrison, lighteningPlan, reinforcementPlan,
                travelNeeded, completionThisTurn, stageCost, realAp, owner);
        }

        // Materialization of Demand's exact choice. A changed identity/order/transfer contract
        // invalidates the witness; it never triggers a second roster optimizer here.
        internal static bool MaterializeEconomyRoster(PlayerSetupData player, ArmyData builder,
            DemandLayer.EconomyBuilderChoice choice, out ArmyData garrison,
            out List<UnitData> unload, out List<UnitData> reinforcement)
        {
            garrison = null;
            unload = new List<UnitData>();
            reinforcement = new List<UnitData>();
            if (builder == null || choice?.Army == null) return false;
            // Hero extraction has its own pinned container plan and is re-admitted after mutation.
            if (choice.Route.RequiresGarrisonExtraction) return true;
            List<UnitData> bodies = builder.Members.Where(u => u.IsGroundCombatant).ToList();
            if (!RosterMatches(choice.Army, bodies)) return false;
            if (choice.PreparationGarrison == null) return true;
            garrison = AiV2Util.ResolveArmy(player, choice.PreparationGarrison.ArmyId);
            if (garrison == null || !garrison.IsGarrison || garrison.Owner != player
                || !garrison.Hex.Equals(builder.Hex) || garrison.HasActivatedThisTurn
                || !choice.Army.Hex.Equals(builder.Hex)) return false;
            List<UnitData> reserve = garrison.Members.Where(u => u.IsGroundCombatant).ToList();
            if (!RosterMatches(choice.PreparationGarrison, reserve)) return false;
            var retained = new HashSet<int>(choice.RetainedIndices);
            unload = bodies.Where((u, index) => AiArmyRoles.IsGroundBattleBody(u)
                && !retained.Contains(index)).ToList();
            if (choice.AddedIndices.Any(i => i < 0 || i >= reserve.Count)) return false;
            reinforcement = choice.AddedIndices.Select(i => reserve[i]).ToList();
            if ((unload.Count > 0 || reinforcement.Count > 0) && choice.Army.EconomyRosterProtected)
                return false;
            return (unload.Count == 0 || ArmyActions.CanTransferMembers(unload, builder, garrison, out _))
                && (reinforcement.Count == 0 || ArmyActions.CanTransferMembers(reinforcement, garrison, builder, out _));
        }

        private static bool RosterMatches(ArmySnapshot snapshot, IReadOnlyList<UnitData> bodies) =>
            snapshot.Members.Count == bodies.Count
                && (snapshot.NonHeroRuntimeIds.Count == 0
                    || (snapshot.NonHeroRuntimeIds.SequenceEqual(bodies.Select(u => u.RuntimeId))
                        && snapshot.NonHeroActivationApCosts.SequenceEqual(bodies.Select(u => u.ActivationApCost))
                        && snapshot.NonHeroMoveMax.SequenceEqual(bodies.Select(u => u.MoveMax))
                        && snapshot.NonHeroCurrentMovement.SequenceEqual(
                            bodies.Select(AviationRules.EffectiveMoveCurrent))));

#if UNITY_INCLUDE_TESTS
        // Compatibility seams for existing live preparation tests: Analysis creates the facts,
        // Demand decides, the same production materializer applies. No live optimizer remains.
        internal static int TryLightenEconomyArmy(PlayerSetupData player, ArmyData builder,
            HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx, int minimumEscort = 0)
        {
            if (player == null || builder == null || ctx?.Map is null || !AiArmyRoles.IsHeroLed(builder))
                return 0;
            BuildingData home = BuildingRegistry.FindAt(builder.Hex);
            if (home?.Owner != player || !(home.IsBase || home.IsStartingCitadel)) return 0;
            var frozen = WorldAnalysis.ToArmySnapshot(builder, player, true, 0);
            ArmyData liveGarrison = ArmyRegistry.FindGarrisonAt(builder.Hex, player);
            var self = new SelfSnapshot { BaseHexes = new[] { builder.Hex },
                Armies = liveGarrison == null ? new[] { frozen }
                    : new[] { frozen, WorldAnalysis.ToArmySnapshot(liveGarrison, player, true, 0) } };
            var facts = new WorldSnapshot { Self = self, Known = snapshot?.Known };
            IReadOnlyList<EconomyBuilderRouteSnapshot> routes = WorldAnalysis.EconomyBuilderRoutes(
                facts, player, ctx, target);
            if (routes.Count == 0) return 0;
            var choice = DemandLayer.AssessEconomyArmy(facts, target, routes.First(x => x.ArmyId == builder.Id),
                frozen, 0f, false);
            if (choice.Suitability == DemandLayer.EconomyArmySuitability.Ineligible
                || !MaterializeEconomyRoster(player, builder, choice, out ArmyData garrison,
                    out List<UnitData> unload, out List<UnitData> reinforcement)) return 0;
            return ApplyEconomyArmyLightening(builder, garrison, unload, reinforcement, ctx);
        }
#endif

        internal static int ApplyEconomyArmyLightening(ArmyData builder,
            ArmyData garrison, IReadOnlyList<UnitData> unload,
            IReadOnlyList<UnitData> reinforcement, AiTurnContext ctx)
        {
            if (builder == null || garrison == null)
                return 0;
            bool unloadApplied = false;
            if (unload.Count > 0)
            {
                if (!ArmyActions.TransferMembersAtomic(
                        unload, builder, garrison, ctx.HexSelection, out string whyUnload))
                    return 0;
                unloadApplied = true;
            }
            if (reinforcement.Count > 0 && !ArmyActions.TransferMembersAtomic(
                    reinforcement, garrison, builder, ctx.HexSelection, out string whyAdd))
            {
                // Unload+reinforce is ONE canonical composition change: a reinforcement failure
                // must not leave an already-applied unload in place while the caller reports
                // failure. The planner does not currently produce both non-empty at once, but this
                // must not depend on that as an unstated invariant. Roll the unload back so this
                // call is all-or-nothing.
                if (unloadApplied && !ArmyActions.TransferMembersAtomic(
                        unload, garrison, builder, ctx.HexSelection, out string whyRollback))
                    AiDebugLog.Write($"[AI][V2][Economy][WARN] lighten rollback failed for builder "
                        + $"#{builder.Id} after a reinforce failure: {whyRollback} — roster left "
                        + "partially unloaded");
                return 0;
            }
            int changed = unload.Count + reinforcement.Count;
            if (changed > 0)
                AiDebugLog.Write($"[AI][V2][Economy] builder #{builder.Id} prepared "
                    + $"unload={unload.Count} add={reinforcement.Count} "
                    + $"garrison=#{garrison.Id} power={AiPower.EffectiveArmyPower(builder.Members):0.##}");
            return changed;
        }

#if UNITY_INCLUDE_TESTS
        internal static IReadOnlyList<UnitData> SelectEconomyEscort(ArmyData builder,
            IReadOnlyList<UnitData> bodies, IReadOnlyList<AiMapMemory.KnownEnemySighting> threats,
            int minimumEscort = 0)
        {
            if (builder == null) return null;
            var pool = (bodies ?? Array.Empty<UnitData>()).Where(u => u != null).Distinct().ToList();
            ArmySnapshot facts = WorldAnalysis.ToArmySnapshot(builder, builder.Owner, true, 0);
            facts.Members = pool.Select(WorthIt.FromLiveUnit).ToList();
            facts.NonHeroActivationApCosts = pool.Select(u => u.ActivationApCost).ToList();
            facts.NonHeroMoveMax = pool.Select(u => u.MoveMax).ToList();
            facts.NonHeroIsAviation = pool.Select(u => u.IsAviation).ToList();
            List<int> selected = DemandLayer.MinimumSafeEconomyEscortIndices(facts, threats, minimumEscort);
            return selected?.Select(i => pool[i]).ToList();
        }
#endif

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
            return DemandLayer.EconomyCurrentStageAp(immediateTransfers, activation,
                builder?.HasActivatedThisTurn == true, travelNeeded, completionThisTurn,
                buildApCost, minimumFollowupAp);
        }

        internal static ResourceVector CostVector(ResourceCost cost) => cost == null
            ? ResourceVector.Zero
            : new ResourceVector(0f, cost.Get(ResourceType.Human), cost.Get(ResourceType.Energy),
                cost.Get(ResourceType.Materials), cost.Get(ResourceType.Tech));
    }
}
