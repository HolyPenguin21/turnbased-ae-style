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
        // 2026-09-14 review round 8 (P0) — the pure DECISION half of what FinishEconomyBuilder used
        // to compute inline: donor loan, route, lightening/reinforcement composition, AP/resource
        // feasibility. Split out so Provisioning can compute and PIN this exact decision against a
        // read-only preview (see BuildGarrisonExtractionPreview) for a deferred garrison-extraction
        // candidate — Execution then only re-validates the volatile parts (AP, resources) and
        // APPLIES the pinned Unload/Reinforcement, never re-deriving them. The direct-army path
        // (FinishEconomyBuilder, below) calls this with the REAL live hero — identical behaviour to
        // before this split, since every read here (Hex/Members/MaxMovement/CurrentMovement/
        // HasActivatedThisTurn) is satisfied the same way by a real ArmyData or by the preview.
        // `identityArmyId` is `hero.Id` for the direct-army path; for a preview it is the REAL
        // container id when one already exists (Shell/Host) or -1 for Create (nothing could already
        // be bound to an army that does not exist yet).
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

        // 2026-09-14 review round 10 (P0) — `alreadyCommittedApCost` is the extraction AP a deferred
        // garrison-extraction candidate's OWN GarrisonExtractionCandidate.ApCost already accounts
        // for (CreateArmy, or a hero joining an already-activated Shell/Host) — a cost
        // EconomyMissionClaimedAp (inside this function) has no way to know about, since it only
        // ever reads the projected roster's own ActivationApCost sum, never a separate container-
        // creation/late-join charge. Subtracted from the envelope/pool BEFORE any feasibility check
        // here, so a candidate whose extraction cost alone would blow the budget is correctly
        // rejected (rather than accepted on a budget that silently excluded a cost the caller must
        // still pay). The direct-army path (hero already real, no extraction) passes the default 0.
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
            int distance = SafeStepPathing.FindSafePathCost(ctx.Map, hero, target.TargetHex);
            if (distance == int.MaxValue)
                return EconomyCompletionPlan.No(ProvisionFailure.NoExecutableStep("no safe economy route"));
            if (donor != null)
            {
                // Provisioning validates LIVE path/MP against the SAME Demand-owned loan
                // predicate. Reprice the already selected builder's projected roster from
                // current physical facts; never resurrect the old raw-hex distance scorer.
                EconomyBuilderRouteSnapshot liveRoute = builderChoice.Route;
                liveRoute.TravelCost = distance;
                liveRoute.CurrentMovement = hero.CurrentMovement;
                liveRoute.HasActivatedThisTurn = hero.HasActivatedThisTurn;
                var liveChoice = new DemandLayer.EconomyBuilderChoice
                {
                    Route = liveRoute,
                    TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(
                        liveRoute, target.BuildApCost,
                        target.Kind == EconomyTaskKind.BuildExtraction),
                };
                if (!DemandLayer.EconomyLoanAllowed(donor, target.BuildValue,
                        liveChoice, target.BuildApCost, out float loanNet))
                    return EconomyCompletionPlan.No(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }

            bool travelNeeded = !hero.Hex.Equals(target.TargetHex);
            bool completionThisTurn = distance <= hero.CurrentMovement;
            ResourceCost stageCost = completionThisTurn ? target.BuildResourceCost : null;
            List<UnitData> lighteningPlan = PlanEconomyArmyLightening(
                player, hero, identityArmyId, target.TargetHex, snapshot, ctx,
                builderChoice.MinimumEscortCount, out ArmyData garrison,
                out List<UnitData> reinforcementPlan);
            if (builderChoice.Suitability == DemandLayer.EconomyArmySuitability.ReinforceAtBase
                && reinforcementPlan.Count == 0)
                return EconomyCompletionPlan.No(ProvisionFailure.AssemblyInfeasible(
                    $"economy builder #{identityArmyId} no longer has its planned minimum escort"));
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
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, stageCost, owner))
                return EconomyCompletionPlan.No(ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(realAp, CostVector(stageCost)),
                    "economy completion resources no longer spendable"));

            return EconomyCompletionPlan.Yes(donor, garrison, lighteningPlan, reinforcementPlan,
                travelNeeded, completionThisTurn, stageCost, realAp, owner);
        }

        // Economy-specific, same-hex preparation belongs here because the target and its route are
        // already selected. The canonical atomic batch transfer guarantees an all-or-nothing
        // roster change; a failed preflight simply leaves the original army usable.
        internal static int TryLightenEconomyArmy(PlayerSetupData player, ArmyData builder,
            HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx)
            => TryLightenEconomyArmy(player, builder, target, snapshot, ctx,
                minimumEscort: 0);

        internal static int TryLightenEconomyArmy(PlayerSetupData player, ArmyData builder,
            HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx, int minimumEscort)
        {
            List<UnitData> plan = PlanEconomyArmyLightening(
                player, builder, target, snapshot, ctx, minimumEscort,
                out ArmyData garrison, out List<UnitData> reinforcement);
            return ApplyEconomyArmyLightening(
                builder, garrison, plan, reinforcement, ctx);
        }

        private static List<UnitData> PlanEconomyArmyLightening(PlayerSetupData player,
            ArmyData builder, HexCoord target, WorldSnapshot snapshot, AiTurnContext ctx,
            int minimumEscort, out ArmyData garrison,
            out List<UnitData> reinforcement)
            => PlanEconomyArmyLightening(player, builder, builder?.Id ?? -1, target, snapshot, ctx,
                minimumEscort, out garrison, out reinforcement);

        // 2026-09-14 review round 8 (P0) — `identityArmyId` decouples the loan-protection intent
        // check from `builder.Id` so a Provisioning-time READ-ONLY PREVIEW of a not-yet-real
        // garrison-extraction container (ArmyData.CreateVisualSnapshot(), Id always -1) can still be
        // checked against the REAL container id it stands in for when one already exists (Shell/Host
        // tier) — using the preview's own -1 id would silently skip this protection for those tiers.
        // Both existing callers (the overload above, TryLightenEconomyArmy) keep passing `builder.Id`
        // — unchanged behaviour for every live-army caller.
        private static List<UnitData> PlanEconomyArmyLightening(PlayerSetupData player,
            ArmyData builder, int identityArmyId, HexCoord target, WorldSnapshot snapshot,
            AiTurnContext ctx, int minimumEscort, out ArmyData garrison,
            out List<UnitData> reinforcement)
        {
            garrison = null;
            reinforcement = new List<UnitData>();
            var unload = new List<UnitData>();
            if (player == null || builder == null || ctx == null || builder.IsGarrison
                || builder.IsPrison || builder.IsAirfield || builder.IsAirArmy
                || !builder.Members.Any(u => u != null && u.IsHero))
                return unload;
            // A loan may temporarily redirect an Explore/early-Raid actor, but it must not also
            // rewrite that durable mission's roster behind Continuity's back. Hard Defence,
            // Surveil and started Raid are already excluded at assignment; this preserves the
            // remaining loanable obligations as well.
            if (MissionIntentRegistry.GetOrCreate(player).All.Any(i => i != null
                && i.Status == IntentStatus.Active && i.Kind != MissionKind.Economy
                && i.PreferredMoverArmyId == identityArmyId))
                return unload;
            BuildingData home = BuildingRegistry.FindAt(builder.Hex);
            bool isCitadel = player.CitadelHexQ == builder.Hex.Q
                && player.CitadelHexR == builder.Hex.R;
            if ((home == null || home.Owner != player || (!home.IsBase && !isCitadel)))
                return unload;
            garrison = ArmyRegistry.FindGarrisonAt(builder.Hex, player);
            if (garrison == null || garrison == builder
                || garrison.HasActivatedThisTurn)
                return unload;

            List<UnitData> bodies = builder.Members
                .Where(u => AiArmyRoles.IsGroundBattleBody(u))
                .OrderByDescending(u => AiPower.ToPowerUnit(u).BasePower)
                .ThenBy(u => u.Name).ToList();
            HexPath escortRoute = SafeStepPathing.FindSafePath(
                ctx.Map, player, builder.Hex, target, builder.MaxMovement);
            // No route means no reliable exposure witness. Keep the live roster intact instead
            // of substituting endpoints and potentially unloading the escort for an unreachable
            // operation. The caller may retry after the map/known blockers change.
            if (escortRoute == null)
                return unload;
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats =
                WorldAnalysis.KnownThreatsAffectingEconomyRoute(
                    snapshot, escortRoute.Hexes);
            IReadOnlyList<UnitData> retained = SelectEconomyEscort(
                builder, bodies, threats, minimumEscort);
            if (retained != null)
            {
                var keep = new HashSet<UnitData>(retained);
                unload.AddRange(bodies.Where(u => !keep.Contains(u))
                    .OrderByDescending(u => u.ActivationApCost)
                    .ThenBy(u => AiPower.ToPowerUnit(u).BasePower)
                    .ThenBy(u => u.Name));
                while (unload.Count > 0
                       && !ArmyActions.CanTransferMembers(
                           unload, builder, garrison, out _))
                    unload.RemoveAt(unload.Count - 1);
                return unload;
            }

            // The field roster is deficient. At a Base/Citadel add only the smallest garrison
            // subset that makes the whole hero-led formation safe; do not add surplus beyond it.
            List<UnitData> reserve = garrison.Members
                .Where(u => AiArmyRoles.IsGroundBattleBody(u))
                .OrderBy(u => u.ActivationApCost)
                .ThenByDescending(u => AiPower.ToPowerUnit(u).BasePower)
                .ThenBy(u => u.Name).ToList();
            for (int count = 1; count <= reserve.Count; count++)
                foreach (List<UnitData> subset in Combinations(reserve, count))
                {
                    var projected = bodies.Concat(subset)
                        .Select(WorthIt.FromLiveUnit).ToList();
                    if (!DemandLayer.EconomyRosterSafe(
                            projected, threats, minimumEscort))
                        continue;
                    if (!ArmyActions.CanTransferMembers(
                            subset, garrison, builder, out _))
                        continue;
                    reinforcement.AddRange(subset);
                    return unload;
                }
            return unload;
        }

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
                // 2026-09-14 review round 6 (P0) — unload+reinforce is ONE canonical composition
                // change, not two independent ones: a reinforcement failure must not leave an
                // already-applied unload silently uncommitted-for (caller reported failure while the
                // unload stayed real). The current planner never actually produces both non-empty at
                // once, but this must not depend on that as an unstated invariant. Roll the unload
                // back so this call is honestly all-or-nothing.
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

        // Composition selection only; combat truth remains WorthIt (coverage + full-roster Monte
        // Carlo) and tie quality remains AiPower. The smallest safe body count wins. If the memory
        // lacks per-unit profiles, null deliberately refuses lightening rather than inventing a
        // third Economy strength surrogate.
        internal static IReadOnlyList<UnitData> SelectEconomyEscort(ArmyData builder,
            IReadOnlyList<UnitData> bodies,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats)
            => SelectEconomyEscort(builder, bodies, threats, minimumEscort: 0);

        internal static IReadOnlyList<UnitData> SelectEconomyEscort(ArmyData builder,
            IReadOnlyList<UnitData> bodies,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            if (builder == null)
                return null;
            if (threats == null || threats.Count == 0)
                return (bodies ?? System.Array.Empty<UnitData>())
                    .Where(u => u != null)
                    .OrderBy(u => u.ActivationApCost)
                    .ThenByDescending(u => u.MoveMax)
                    .ThenBy(u => u.Name)
                    .Take(Mathf.Max(0, minimumEscort)).ToList();
            if (threats.Any(t => t.Defenders == null || t.Defenders.Count == 0))
                return null;

            List<UnitData> pool = (bodies ?? System.Array.Empty<UnitData>())
                .Where(u => u != null).Distinct().ToList();
            for (int count = Mathf.Max(0, minimumEscort); count <= pool.Count; count++)
            {
                List<UnitData> best = null;
                int bestAp = int.MaxValue;
                int bestMove = int.MinValue;
                float bestPower = float.MinValue;
                foreach (List<UnitData> subset in Combinations(pool, count))
                {
                    // Heroes travel with the builder but do not participate in ground combat;
                    // WorthIt's ArmyData overloads apply the same non-hero boundary.
                    var roster = subset.Select(WorthIt.FromLiveUnit).ToList();
                    bool safe = DemandLayer.EconomyRosterSafe(
                        roster, threats, minimumEscort);
                    if (!safe)
                        continue;
                    int ap = subset.Sum(u => u.ActivationApCost);
                    int move = subset.Count == 0 ? builder.MaxMovement
                        : subset.Min(u => u.MoveMax);
                    float power = AiPower.EffectiveArmyPower(subset);
                    if (best == null || ap < bestAp
                        || (ap == bestAp && move > bestMove)
                        || (ap == bestAp && move == bestMove
                            && power > bestPower + AiConfigV2.allocatorSliceEpsilon))
                    {
                        best = subset;
                        bestAp = ap;
                        bestMove = move;
                        bestPower = power;
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
        }

        private static IEnumerable<List<UnitData>> Combinations(
            IReadOnlyList<UnitData> source, int count, int start = 0,
            List<UnitData> prefix = null)
        {
            prefix ??= new List<UnitData>();
            if (prefix.Count == count)
            {
                yield return new List<UnitData>(prefix);
                yield break;
            }
            for (int i = start; i <= source.Count - (count - prefix.Count); i++)
            {
                prefix.Add(source[i]);
                foreach (List<UnitData> result in Combinations(source, count, i + 1, prefix))
                    yield return result;
                prefix.RemoveAt(prefix.Count - 1);
            }
        }

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
            float activation = 0f;
            if (travelNeeded && builder != null && !builder.HasActivatedThisTurn)
                activation = builder.Members
                    .Where(u => u != null && (unloaded == null || !unloaded.Contains(u)))
                    .Concat(added ?? System.Array.Empty<UnitData>())
                    .Distinct().Sum(u => u.ActivationApCost);
            float completion = completionThisTurn
                ? Mathf.Max(buildApCost, minimumFollowupAp) : 0f;
            return immediateTransfers + activation + completion;
        }

        internal static ResourceVector CostVector(ResourceCost cost) => cost == null
            ? ResourceVector.Zero
            : new ResourceVector(0f, cost.Get(ResourceType.Human), cost.Get(ResourceType.Energy),
                cost.Get(ResourceType.Materials), cost.Get(ResourceType.Tech));
    }
}
