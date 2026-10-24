using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ECONOMY RESERVATION LIFECYCLE
    // ===========================================================================================
    //  The one writer of Economy build reservations (EconomyDeferredBuild / EconomyBuildCompletion
    //  rows) and of their Completion -> Deferred / Released transitions. Split out of
    //  InfrastructureFulfillment (level 1 of the pipeline simplification), which keeps the demand
    //  side: build identity, spend authority and the authoritative build transaction. Rows live in
    //  StrategicResourceReservationLedger and change only through MissionLeaseBook; this class
    //  decides WHEN a hold is written, downgraded or released, never how much a spender may use
    //  (TurnResourceBook / StrategicSpendability).
    // ===========================================================================================
    internal static class EconomyReservationLifecycle
    {
        // A selected infrastructure demand already has a valuable legal site and a
        // snapshot-witnessed builder route. This is the FIRST-SIGHT gate only, called from
        // StrategicPhaseA exclusively when no durable Economy intent exists yet for ANY target this
        // turn (an active build short-circuits it otherwise, PhaseAEconomyHolds.HoldPreIntentBuild), so it never fires
        // on a continuing multi-turn delivery. Once Provisioning binds a builder that cannot finish
        // this turn, it hands the delivery to Continuity
        // (EconomyContinuity.BeginEconomyDelivery, called from
        // ProvisioningManager.ProvisionEconomy's direct-army path), and from the NEXT turn on
        // PhaseAEconomyHolds.ProtectActiveBuilds protects the full H/E/M/T vector regardless
        // of remaining travel. Only WHILE that durable identity does not exist yet — the turn a
        // distant candidate is first scored — does this one-turn commitment horizon apply, so a
        // merely-discovered distant site does not freeze resources other axes could still spend for
        // however many turns it takes to become reachable.
        internal static bool ShouldReserveDeferredEconomyResources(
            WorldSnapshot snap, AxisDemand demand)
        {
            // The accepted build's prerequisite (Hero or its pinned escort) keeps the same
            // persistent-resource hold before a ready builder/route exists, while it is still
            // deliverable (StrategicPhaseA.PendingPrerequisiteHoldIsLive). A speculative
            // new-Hero alternative alongside a ready builder is not a second obligation.
            if (InfrastructureFulfillment.EconomyHeroPrerequisiteOwner(demand) != null && !demand.IsEconomyNewHeroAlternative)
                return true;
            if (demand?.EconomyBuilderRoutes == null || snap?.Self?.Armies == null)
                return false;
            foreach (EconomyBuilderRouteSnapshot route in demand.EconomyBuilderRoutes)
            {
                // A garrison hero that must first be extracted is a real builder too, but only the
                // one Demand actually chose (AssessEconomyArmy already proved its container); its
                // reach is the extracted hero's own movement carried on the route, not the
                // garrison's.
                bool chosenGarrisonBuilder = route.RequiresGarrisonExtraction
                    && demand.EconomyPreferredBuilderArmyId == route.ArmyId;
                ArmySnapshot actor = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.ArmyId == route.ArmyId && a.HasHero && !a.IsPrison && !a.IsAir
                    && (((a.IsMobileEconomyBuilder
                            || (!route.IsOnTarget && a.EconomyDeparture?.IsMobileEconomyBuilder == true))
                            && !(a.OperatorDutyBlocksDeparture && !route.IsOnTarget))
                        || (a.IsGarrison && (chosenGarrisonBuilder
                            || a.Hex.Equals(demand.TargetHex ?? a.Hex)))));
                if (actor == null)
                    continue;
                int reach = chosenGarrisonBuilder ? route.MaxMovement
                    : (!route.IsOnTarget ? actor.EconomyDeparture ?? actor : actor).MaxMovement;
                if (route.IsOnTarget || route.TravelCost <= UnityEngine.Mathf.Max(0, reach))
                    return true;
            }
            return false;
        }

        internal static void ReserveDeferredEconomyResources(
            WorldSnapshot snap, PlayerSetupData player, int turn, AxisDemand demand)
        {
            ReservationOwner owner = InfrastructureFulfillment.EconomyReservationIdentity(demand);
            if (owner == null || !ShouldReserveDeferredEconomyResources(snap, demand))
                return;
            ReserveDeferredEconomyResourcesCore(player, turn, owner, demand);
        }

        // Continuity may intentionally suppress a repeated Economy demand once a concrete
        // builder owns the operation. Preserve that active intent's exact build vector directly;
        // absence from the current demand list is not cancellation.
        internal static void ReserveDeferredEconomyResourcesForActiveIntent(
            PlayerSetupData player, int turn, MissionIntent intent)
        {
            EconomyIntent economy = intent?.Economy;
            if (player == null || !EconomyContinuity.IsLiveEconomyBuild(intent)
                || economy.BuildResourceCost == null)
                return;

            ReservationOwner owner = EconomyMissionPlanner.ReservationIdentity(intent.LastAttemptKey);
            // AP is turn-local execution capacity and has no legal deferred state: a build's AP is
            // reserved only at EconomyBuildCompletion, after Provisioning has proved this concrete
            // actor can finish the build THIS turn (ProvisioningManager -> ReserveEconomyCost).
            ReserveDeferredEconomyResourcesCore(player, turn, owner, new AxisDemand
            {
                EconomyBuildResourceCost = economy.BuildResourceCost,
            });
        }

        // A bare Hero or pinned escort prerequisite has no ready builder/route to witness.
        // Its accepted build's H/E/M/T stays protected while the prerequisite is still
        // deliverable and only waits on AP/resources, so Phase B cannot spend the exact
        // resources the build still needs. Owner decision 2026-10-18: Phase A
        // (StrategicPhaseA.PendingPrerequisiteHoldIsLive) does not select this hold in a pass
        // where the prerequisite is structurally undeliverable from the current hand/world. The Hero-prerequisite payload (EconomyBuildResourceCost)
        // already carries the target build's real cost (see DemandLayer.EconomyHeroPrerequisite);
        // this reserves it the instant that demand is the accepted Economy target for this pass,
        // whether or not a builder route exists yet. Same owner key and same EconomyDeferredBuild
        // reason as the witnessed-route path above, so EconomyBuildCompletion transitions it
        // exactly the same way once a builder actually starts moving/building.
        internal static void ReserveDeferredEconomyResourcesForPendingHero(
            PlayerSetupData player, int turn, AxisDemand heroPrerequisiteDemand)
        {
            ReservationOwner owner = InfrastructureFulfillment.EconomyHeroPrerequisiteIdentity(heroPrerequisiteDemand);
            if (owner == null)
                return;
            ReserveDeferredEconomyResourcesCore(player, turn, owner, heroPrerequisiteDemand);
        }

        private static void ReserveDeferredEconomyResourcesCore(
            PlayerSetupData player, int turn, ReservationOwner owner, AxisDemand demand)
        {
            // Every test here is OWNER-specific, and the completion test must run BEFORE the
            // deferred replacement:
            //
            // · If this owner already holds a provisioned EconomyBuildCompletion envelope, that is
            // the stronger stage of the very same build (same H/E/M/T plus its completion AP). Keep
            // it; downgrading it here would drop the AP a provisioned builder is about to spend.
            // Asking the GLOBAL HasReason instead would let ANOTHER owner's completion suppress
            // this owner's deferred hold and leave its resources free for Phase B.
            //
            // · Asking after ReplaceReasonOwner(..., replaceOwnerRows: true) cannot work: that call
            // is exactly what deletes this owner's completion rows.
            if (StrategicResourceReservationLedger.HasOwnerReason(player, turn, owner,
                    StrategicReservationReason.EconomyBuildCompletion))
                return;
            if (StrategicResourceReservationLedger.OwnerReasonMatches(player, turn, owner,
                    StrategicReservationReason.EconomyDeferredBuild,
                    demand.EconomyBuildResourceCost, 0f))
                return;
            MissionLeaseBook.ReplaceReasonOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, owner,
                replaceOwnerRows: true);
            ReserveEconomyCost(player, turn, owner, demand.EconomyBuildResourceCost, 0f,
                StrategicReservationReason.EconomyDeferredBuild);
        }

        // The only Completion -> Deferred / Released transition. Repeating this on the
        // same owner after the first downgrade is a no-op; unrelated projects are untouched.
        internal static void ReconcileEconomyCompletionOwner(PlayerSetupData player, int turn,
            string owner, MissionIntent intent, bool durableValid, bool completionThisTurn)
        {
            if (!StrategicResourceReservationLedger.HasOwnerReason(player, turn, owner,
                    StrategicReservationReason.EconomyBuildCompletion))
                return;
            if (!durableValid || intent?.Economy == null)
            {
                MissionLeaseBook.ReleaseByOwner(player, turn, owner);
                return;
            }
            if (completionThisTurn)
                return;

            // Explicit owner-scoped downgrade: a repeated Phase A deferred request MUST NOT
            // implicitly demote a still-executable Completion, but this settled lifecycle
            // decision has proved it cannot finish this turn. Keep only durable H/E/M/T.
            MissionLeaseBook.ReplaceReasonOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, owner, replaceOwnerRows: true);
            ReserveEconomyCost(player, turn, EconomyMissionPlanner.ReservationIdentity(intent.LastAttemptKey), intent.Economy.BuildResourceCost, 0f,
                StrategicReservationReason.EconomyDeferredBuild);
        }

        // Called after settled operational steps AND immediately before each Phase B admission.
        // Compute current-turn feasibility from the REAL actor/path/AP/card, not the snapshot
        // used by the earlier Provisioning prediction. No global world rebuild or AP threshold.
        internal static void ReconcileEconomyCompletionReservations(PlayerSetupData player,
            IMissionIntentView intents, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (player == null || root == null || ctx == null)
                return;
            int turn = ctx.TurnNumber;
            IReadOnlyList<string> owners =
                StrategicResourceReservationLedger.CompletionOwners(player, turn);
            if (owners.Count == 0)
                return;
            List<MissionIntent> economy = (intents?.All ?? (IEnumerable<MissionIntent>)System.Array.Empty<MissionIntent>())
                .Where(i => i != null && i.Kind == MissionKind.Economy && i.Economy != null
                    && (i.Economy.Kind == EconomyTaskKind.FoundBase
                        || i.Economy.Kind == EconomyTaskKind.BuildExtraction))
                .ToList();
            foreach (string owner in owners)
            {
                MissionIntent intent = economy.FirstOrDefault(i =>
                    EconomyMissionPlanner.OwnerKey(i.LastAttemptKey) == owner);
                if (intent == null)
                {
                    ReconcileEconomyCompletionOwner(player, turn, owner, null, false, false);
                    continue;
                }
                EconomyIntent build = intent.Economy;
                ArmyData actor = OwnWorld.Of(player).Armies.FirstOrDefault(a =>
                    a != null && a.Id == intent.PreferredMoverArmyId && a.Owner == player
                    && AiArmyRoles.IsHeroLed(a));
                bool cardStillAvailable = build.Kind != EconomyTaskKind.FoundBase
                    || (build.BuildCard != null && hand?.Hand?.Contains(build.BuildCard) == true);
                bool durableValid = actor != null && cardStillAvailable;
                if (!durableValid)
                {
                    ReconcileEconomyCompletionOwner(player, turn, owner, intent, false, false);
                    continue;
                }
                int route = actor.Hex.Equals(build.TargetHex) ? 0
                    : ctx.Map == null ? int.MaxValue
                    : SafeStepPathing.FindSafePathCost(ctx.Map, actor, build.TargetHex);
                // Actual turn AP determines whether this concrete owner CAN still execute.
                // Another owner's legitimate hold is not evidence that our already-provisioned
                // completion became physically impossible; otherwise iteration order would
                // downgrade one of two individually executable independent projects.
                float liveAp = root.ActionPoints;
                float activationAp = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
                bool completionThisTurn = intent.Status == IntentStatus.Active
                    && route != int.MaxValue && route <= actor.CurrentMovement
                    && liveAp + AiConfigV2.allocatorSliceEpsilon
                        >= build.BuildApCost + (route > 0 ? activationAp : 0f)
                    // Through TurnResourceBook: its own hold and other builds' deferred holds are
                    // drawable, another owner's completion or the reaction envelope is not.
                    && StrategicSpendability.FitsSpendableForEconomyCompletion(
                        player, root, ctx, build.BuildResourceCost, owner);
                ReconcileEconomyCompletionOwner(player, turn, owner, intent, true,
                    completionThisTurn);
            }
        }

        // Called ONCE per turn (each Phase B entry path of AiStrategyV2Pipeline.RunTurn), after the
        // operational loop has settled and every surviving Completion owner has been reconciled -
        // i.e. at the point where each remaining EconomyDeferredBuild owner is proven NOT to
        // complete this turn. Such a build is paid from the stock of a later turn, and that stock
        // always includes at least one more income tick (IncomeProjection.IncomeFor - the same
        // number the round-start grant pays). Holding the full vector from today's stock froze
        // exactly that part for Phase B while the build could not use it anyway.
        //
        // The tick is fixed here once for the rest of the turn; the ledger keeps every deferred
        // owner's FULL vector and TurnResourceBook.LedgerClaims subtracts the cover when it lists
        // the rows as claims (shared by every deferred owner in deterministic owner order, never
        // counted twice). A later Phase A re-statement of the full vector (a re-admission after a
        // Phase B round) or a Completion downgraded after this point therefore keeps the cover.
        // Phase A never calls this: there the deferred stage is not yet proof that completion is
        // impossible this turn.
        internal static void ReleaseDeferredEconomyIncomeCover(PlayerSetupData player,
            AiTurnContext ctx)
        {
            if (player == null || ctx?.Map == null)
                return;
            int turn = ctx.TurnNumber;
            var cover = new float[System.Enum.GetValues(typeof(StrategicReservedResource)).Length];
            foreach (ResourceType type in ResourceBundle.All)
                cover[(int)StrategicResourceReservationLedger.Map(type)] =
                    System.Math.Max(0, IncomeProjection.IncomeFor(player, type, ctx.Map));
            OpenDeferredIncomeCover(player, turn, cover);
            AiDebugLog.Write($"[AI][V2][Economy][DeferredIncomeCover] turn={turn} cover=["
                + string.Join(" ", ResourceBundle.All.Select(t =>
                    $"{t}={cover[(int)StrategicResourceReservationLedger.Map(t)]:0.##}"))
                + $"] active [{StrategicResourceReservationLedger.DebugLine(player, turn)}]");
        }

        // `cover` is indexed by (int)StrategicReservedResource.
        internal static void OpenDeferredIncomeCover(PlayerSetupData player, int turn, float[] cover)
        {
            if (player != null && cover != null)
                IncomeCoverByPlayer[player] = (turn, cover);
        }

        // The income tick this turn's deferred holds may count as already paid (0 before the cover
        // opens and for AP, which has no deferred state).
        internal static float DeferredIncomeCover(PlayerSetupData player, int turn,
            StrategicReservedResource resource) =>
            player != null && IncomeCoverByPlayer.TryGetValue(player, out var entry) && entry.Turn == turn
                ? entry.Cover[(int)resource] : 0f;

        private static readonly Dictionary<PlayerSetupData, (int Turn, float[] Cover)> IncomeCoverByPlayer =
            new Dictionary<PlayerSetupData, (int Turn, float[] Cover)>();

        // Match-start reset (CitadelSetupController), alongside the other V2 registries.
        internal static void ClearAll() => IncomeCoverByPlayer.Clear();

        // owner == null is the whole-reason reset (no Economy obligation survived this pass); an
        // explicit owner drops only that build's deferred rows and leaves every other build's hold.
        internal static void ClearDeferredEconomyResources(PlayerSetupData player, int turn,
            string owner = null) =>
            MissionLeaseBook.ReplaceReasonOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, owner);

        // Keep only `keepOwner`'s deferred Economy hold (StrategicPhaseA's single pre-intent hold).
        internal static void RetainDeferredEconomyOwner(PlayerSetupData player, int turn,
            string keepOwner) =>
            MissionLeaseBook.ReleaseReasonExceptOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, keepOwner);

        // One canonical writer for direct, deferred and provisioned Economy build reservations.
        // Provisioning adds AP only when completion is reachable this turn; Phase A protects only
        // persistent H/E/M/T while a confirmed route is still being delivered.
        internal static void ReserveEconomyCost(PlayerSetupData player, int turn, ReservationOwner owner,
            ResourceCost cost, float buildAp,
            StrategicReservationReason reason = StrategicReservationReason.EconomyBuildCompletion)
        {
            if (player == null || string.IsNullOrEmpty(owner))
                return;
            if (reason == StrategicReservationReason.EconomyBuildCompletion)
            {
                // This owner's own deferred hold is being promoted to completion — drop only ITS
                // deferred rows; every other Economy build's deferred H/E/M/T hold stays. An equal
                // completion vector already held is kept as is (idempotent repeat).
                MissionLeaseBook.ReplaceReasonOwner(player, turn,
                    StrategicReservationReason.EconomyDeferredBuild, owner);
                if (StrategicResourceReservationLedger.OwnerReasonMatches(player, turn, owner,
                        reason, cost, buildAp))
                    return;
                MissionLeaseBook.ReplaceReasonOwner(player, turn,
                    StrategicReservationReason.EconomyBuildCompletion, owner);
            }
            if (buildAp > 0f)
                MissionLeaseBook.Upsert(player, turn,
                    new StrategicResourceReservation
                    {
                        Identity = owner, Reason = reason,
                        Resource = StrategicReservedResource.ActionPoints, Amount = buildAp,
                        ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                    });
            if (cost == null)
                return;
            foreach (ResourceType type in ResourceBundle.All)
            {
                int amount = cost.Get(type);
                if (amount <= 0)
                    continue;
                MissionLeaseBook.Upsert(player, turn,
                    new StrategicResourceReservation
                    {
                        Identity = owner, Reason = reason,
                        Resource = StrategicResourceReservationLedger.Map(type), Amount = amount,
                        ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                    });
            }
        }
    }
}
