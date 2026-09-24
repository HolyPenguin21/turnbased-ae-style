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
    //  INFRASTRUCTURE FULFILLMENT  (Strategy V2 — ECO / DEV demand consumer for buildings)
    // ===========================================================================================
    //  StrategicManager Phase A calls this for EconomicInfrastructure / DevelopmentInfrastructure
    //  demands, BEFORE the Unit/Hero MaterializationCandidateBuilder loop.
    //
    //  ADMISSION ORDER (spec §1): build a candidate WITHOUT touching game state -> compute its
    //  complete cost -> check the one shared ApBudgetLedger AP pool -> check live
    //  gameplay affordability -> ONLY THEN run the authoritative BuildingPlayExecutor transaction
    //  -> the caller debits the actual confirmed AP. A budget or affordability shortfall means the
    //  demand stays OPEN (nothing played, nothing spent) — Debit() is never used as after-the-fact
    //  permission.
    //
    //  CAPABILITY IDENTITY (spec §3, §4). "Built" means the demanded capability really exists:
    //    · ECO(resourceType) -> an extraction facility on a KNOWN, unbuilt, SAME-type resource
    //      site with a hero present. A generic Base somewhere else is NOT a valid fulfillment.
    //      If no such action is possible now the demand is left deferred.
    //    · DEV -> a CardType.Facility carrying Research/Production, placed into an owned Base slot
    //      (that is what WorldAnalysis.HasDevFacility actually checks — a filled slot, not a
    //      building-level ability, so a plain Base card would NOT satisfy it).
    //
    //  GAME-RULE PRECONDITION: founding a Base and building an extraction facility both need one
    //  of the player's own HERO-LED armies on the target hex. EconomyMissionPlanner delivers a
    //  mobile hero when needed; this owner performs only the final, already-local transaction.
    // ===========================================================================================
    internal sealed class InfraFulfillResult : IV2ActionResult
    {
        public bool Built;
        public float ApSpent;
        public ResourceCost ResourcesSpent;   // forwarded from the BuildingPlayResult
        public bool StateChanged;
        // DEV path plays a CardType.Facility card out of hand; the ECO extraction path is a
        // hero-built site with NO hand card — Outcome.Played must reflect that, not "Built".
        public bool CardPlayed;
        // An operator may be manufactured by a DIFFERENT already staffed facility.
        // This is a single Challenge step, not a completed operator deployment.
        public bool GenerationAttempted;
        public bool Generated;
        public GenerationStep Generation;
        public CardData GeneratedOperatorCard;
        public int? BuilderArmyId;
        public int StateVersionAfter = -1;
        public string Detail;

        public static InfraFulfillResult No(string why) => new InfraFulfillResult { Detail = why };

        public V2ActionOutcome Outcome => new V2ActionOutcome(
            succeeded: Built, stateChanged: StateChanged, apSpent: ApSpent, resourcesSpent: ResourcesSpent,
            played: CardPlayed, generated: Generated, attached: false, moved: false, created: Built,
            needsReplan: GenerationAttempted, stateVersionAfter: StateVersionAfter,
            failReason: Built || Generated ? null : Detail);
    }

    internal static class InfrastructureFulfillment
    {
        public static bool Handles(CapabilityKind k) =>
            k == CapabilityKind.EconomicInfrastructure
            || k == CapabilityKind.EconomicExpansionBase
            || k == CapabilityKind.DevelopmentInfrastructure
            || k == CapabilityKind.DevelopmentOperator;

        // This existing staffing owner validates persisted claims before Phase A/B and after
        // infrastructure mutations. Never duplicate generation, movement or card execution.
        // Current AP/resources are deliberately NOT eligibility criteria for a future turn.
        internal static void RestoreGeneratedOperatorClaims(PlayerSetupData player,
            AiHandData hand, int turn, MaterializationReservation reservation)
        {
            if (player == null || reservation == null) return;
            IReadOnlyList<CardData> cards = MissionIntentRegistry.GetOrCreate(player)
                .ReconcileGeneratedDevelopmentOperators(turn, (card, site, mode) =>
                {
                    if (card?.Definition?.cardType != CardType.Hero
                        || hand?.Hand?.Contains(card) != true
                        || !MaterializationChainMatching.EffectiveAbilities(
                            card.Definition, card.Equipment)
                            .Contains(ResearchProductionSystem.RoleAbility(mode)))
                        return false;
                    BuildingData building = BuildingRegistry.FindAt(site);
                    if (building == null || building.Owner != player
                        || !building.HasFacilityWithAbility(
                            ResearchProductionSystem.FacilityAbility(mode))
                        || ResearchProductionSystem.FindActor(player, site, mode) != null
                        || Game.Combat.BattleInitiator.FindEnemyAt(site, player) != null
                        || !ArmyActions.HasRequiredGroundDeploymentBuilding(player, site, card.Definition))
                        return false;
                    return ArmyRegistry.AllAt(site).Any(g => g != null && g.Owner == player
                        && g.IsGarrison && !g.IsPrison
                        && PlacementRules.CanDepositIntoGarrison(g)
                        && CardPlayExecutor.CanFitAfterDeploy(g, card.Definition));
                });
            reservation.ReconcileDevelopmentOperatorCards(cards);
        }

        // One planned build: the authoritative action to run plus the cost to admit it against.
        private sealed class InfraCandidate
        {
            public float ApCost;
            public ResourceCost ResCost;
            public float DecisionScore;
            public int HandOrdinal;
            public HexCoord TargetHex;
            public int? BuilderArmyId;
            public string Explain;
            public GenerationStep Generation;
            public System.Func<BuildingPlayResult> Execute;
        }

        public static InfraFulfillResult TryFulfill(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            ApBudgetLedger ledger, MaterializationReservation reservation = null)
        {
            if (demand == null || ctx == null || root == null || player == null)
                return InfraFulfillResult.No("missing args");

            InfraCandidate cand =
                demand.Capability == CapabilityKind.EconomicInfrastructure
                    ? BuildEconomyCandidate(snap, player, root, hand, ctx, demand)
                    : demand.Capability == CapabilityKind.EconomicExpansionBase
                        ? BuildEconomyBaseCandidate(snap, player, root, hand, ctx, demand)
                    : demand.Capability == CapabilityKind.DevelopmentInfrastructure
                        ? BuildDevelopmentCandidate(snap, player, root, hand, ctx, demand)
                        : demand.Capability == CapabilityKind.DevelopmentOperator
                            ? BuildDevelopmentOperatorCandidate(snap, player, root, hand, ctx,
                                demand, reservation)
                            : null;
            if (cand == null)
                return InfraFulfillResult.No($"{demand.Capability}: no legal authoritative build available now");
            string economyOwner = EconomyReservationOwner(demand);

            // --- budget admission BEFORE any gameplay mutation (spec §1). Radar already affected
            //     demand value/priority; this admission reads the ONE unreserved AP pool. ---
            if (ledger != null)
            {
                float axisRoom = ledger.UnreservedBalance();
                if (cand.ApCost > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return InfraFulfillResult.No(
                        $"shared AP pool {axisRoom:0.##} < {DesireAxes.Abbrev(demand.RequestingAxis)} demand cost {cand.ApCost:0.##}");
            }
            // Respect the same strategic + legacy persistent-resource reservations as every
            // materialization path. Raw gameplay affordability is still rechecked below.
            // An Economy build here completes NOW, so it outranks other builds' deferred holds
            // (StrategicSpendability.FitsSpendableForEconomyCompletion); DEV infrastructure has no
            // economy owner and keeps respecting them like any other card spend.
            bool resourcesFit = economyOwner != null
                ? StrategicSpendability.FitsSpendableForEconomyCompletion(player, root, ctx,
                    cand.ResCost, economyOwner)
                : StrategicSpendability.FitsSpendableResources(player, root, ctx, cand.ResCost,
                    economyOwner);
            if (!resourcesFit)
                return InfraFulfillResult.No($"{demand.Capability}: reserved resources cannot cover {cand.Explain}");

            // --- live gameplay affordability (the executor re-checks; this keeps the demand open
            //     cleanly rather than letting a doomed transaction run) ---
            float spendableAp = StrategicSpendability.SpendableAp(
                player, root, ctx, economyOwner);
            if (cand.ApCost > spendableAp + AiConfigV2.allocatorSliceEpsilon
                || !root.CanSpendActionPoints(UnityEngine.Mathf.CeilToInt(cand.ApCost))
                || (cand.ResCost != null && !cand.ResCost.CanAfford(root)))
                return InfraFulfillResult.No($"{demand.Capability}: live AP/resources cannot cover {cand.Explain}");

            // A promised operator from the catalog is not yet a card in hand. Mint it via
            // the existing gameplay Challenge as ONE atomic stage. Phase A owns retry/AP
            // accounting and reruns the ordinary hand-operator path on the refreshed snapshot.
            if (cand.Generation != null)
            {
                GenerationStep g = cand.Generation;
                // Reconfirm source identity and affordability immediately before mutation.
                if (reservation == null || !reservation.CanGenerateMore
                    || !GenerationSource.Enumerate(player, root, ctx, hand,
                        reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards)
                        .Any(x => x.CardKey == g.CardKey && x.Hero == g.Hero
                            && x.CardDef == g.CardDef))
                    return InfraFulfillResult.No("operator generator unavailable or already attempted");
                int beforeAp = root.ActionPoints;
                int beforeH = root.GetResource(ResourceType.Human);
                int beforeE = root.GetResource(ResourceType.Energy);
                int beforeM = root.GetResource(ResourceType.Materials);
                int beforeT = root.GetResource(ResourceType.Tech);
                MaterializationExecutor.GenerationOutcome generated =
                    MaterializationExecutor.TryGenerate(g, player, root, hand, ctx);
                var paid = new ResourceCost
                {
                    human = beforeH - root.GetResource(ResourceType.Human),
                    energy = beforeE - root.GetResource(ResourceType.Energy),
                    materials = beforeM - root.GetResource(ResourceType.Materials),
                    tech = beforeT - root.GetResource(ResourceType.Tech),
                };
                int version = generated.StateChanged ? V2StateVersion.Bump() : V2StateVersion.Current;
                return new InfraFulfillResult
                {
                    Built = false, GenerationAttempted = generated.Attempted,
                    Generated = generated.Success, Generation = g,
                    GeneratedOperatorCard = generated.Success ? generated.Minted : null,
                    ApSpent = beforeAp - root.ActionPoints, ResourcesSpent = paid,
                    StateChanged = generated.StateChanged, StateVersionAfter = version,
                    Detail = generated.Success
                        ? $"operator {g.CardDef.displayName} generated into hand; deploy next pass"
                        : generated.FailReason,
                };
            }

            // --- authoritative transaction ---
            BuildingPlayResult r = cand.Execute();
            if (r.Built && economyOwner != null)
                StrategicResourceReservationLedger.ReleaseByOwner(player, ctx.TurnNumber,
                    economyOwner);
            if (!r.Built)
            {
                AiDebugLog.Write($"[AI][V2]   infra — {demand.Capability} action rejected: {r.FailReason} ({cand.Explain})");
                return new InfraFulfillResult { Built = false, StateChanged = r.StateChanged, ApSpent = r.ApSpent,
                    ResourcesSpent = r.ResourcesSpent, CardPlayed = r.CardConsumed,
                    StateVersionAfter = r.StateVersionAfter, Detail = r.FailReason };
            }
            return new InfraFulfillResult { Built = true, ApSpent = r.ApSpent, StateChanged = r.StateChanged,
                ResourcesSpent = r.ResourcesSpent, CardPlayed = r.CardConsumed,
                BuilderArmyId = cand.BuilderArmyId,
                StateVersionAfter = r.StateVersionAfter, Detail = cand.Explain };
        }

        internal static string EconomyReservationOwner(AxisDemand demand)
        {
            if (demand?.TargetHex == null || (demand.Capability != CapabilityKind.EconomicInfrastructure
                && demand.Capability != CapabilityKind.EconomicExpansionBase))
                return null;
            EconomyTaskKind kind = demand.Capability == CapabilityKind.EconomicExpansionBase
                ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
            int targetId = demand.EconomyResourceType.HasValue
                ? (int)demand.EconomyResourceType.Value + 1 : 0;
            HexCoord h = demand.TargetHex.Value;
            return EconomyMissionPlanner.OwnerKey(new StableMissionKey(MissionKind.Economy,
                (int)kind, targetId, h.Q, h.R));
        }

        // A selected infrastructure demand already has a valuable legal site and a
        // snapshot-witnessed builder route. This is the FIRST-SIGHT gate only, called from
        // StrategicPhaseA exclusively when no durable Economy intent exists yet for ANY target this
        // turn (protectedActiveEconomyBuild == null short-circuits it otherwise), so it never fires
        // on a continuing multi-turn delivery. Once Provisioning binds a builder that cannot finish
        // this turn, it hands the delivery to Continuity
        // (MissionContinuityLayer.BeginEconomyDelivery, called from
        // ProvisioningManager.ProvisionEconomy's direct-army path), and from the NEXT turn on
        // StrategicPhaseA's protectedActiveEconomyBuild protects the full H/E/M/T vector regardless
        // of remaining travel. Only WHILE that durable identity does not exist yet — the turn a
        // distant candidate is first scored — does this one-turn commitment horizon apply, so a
        // merely-discovered distant site does not freeze resources other axes could still spend for
        // however many turns it takes to become reachable.
        internal static bool ShouldReserveDeferredEconomyResources(
            WorldSnapshot snap, AxisDemand demand)
        {
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
                    && (a.IsMobileEconomyBuilder
                        || (a.IsGarrison && (chosenGarrisonBuilder
                            || a.Hex.Equals(demand.TargetHex ?? a.Hex)))));
                if (actor == null)
                    continue;
                int reach = chosenGarrisonBuilder ? route.MaxMovement : actor.MaxMovement;
                if (route.IsOnTarget || route.TravelCost <= UnityEngine.Mathf.Max(0, reach))
                    return true;
            }
            return false;
        }

        internal static void ReserveDeferredEconomyResources(
            WorldSnapshot snap, PlayerSetupData player, int turn, AxisDemand demand)
        {
            string owner = EconomyReservationOwner(demand);
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
            if (player == null || intent == null || intent.Status != IntentStatus.Active
                || intent.Kind != MissionKind.Economy || economy == null
                || (economy.Kind != EconomyTaskKind.BuildExtraction
                    && economy.Kind != EconomyTaskKind.FoundBase)
                || economy.BuildResourceCost == null)
                return;

            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);
            // AP is turn-local execution capacity and has no legal deferred state:
            // StrategicResourceReservationLedger.Upsert clamps any EconomyDeferredBuild
            // ActionPoints write to zero, and OwnerReasonMatches expects zero for the same reason.
            // A build's AP is reserved only at EconomyBuildCompletion, after Provisioning has
            // proved this concrete actor can finish the build THIS turn (ProvisioningManager →
            // ReserveEconomyCost). The value below is the build's declared follow-up envelope
            // carried for idempotence comparison only, not a hold, and is computed FoundBase-only.
            // Changing that policy needs its own reproduced defect.
            float followupAp = economy.Kind == EconomyTaskKind.FoundBase
                ? UnityEngine.Mathf.Max(economy.BuildApCost, economy.MinimumFollowupAp)
                : 0f;
            ReserveDeferredEconomyResourcesCore(player, turn, owner, new AxisDemand
            {
                EconomyBuildResourceCost = economy.BuildResourceCost,
            }, followupAp);
        }

        // A bare EconomyHeroPrerequisite demand (Capability.Hero, no builder identified yet) can
        // never satisfy ShouldReserveDeferredEconomyResources above — there is no army/route to
        // witness. That left the accepted build's H/E/M/T free for however many turns Economy
        // spent waiting for a deliverable Hero, during which Phase B could spend the exact
        // resources the build still needs. The Hero-prerequisite payload (EconomyBuildResourceCost)
        // already carries the target build's real cost (see DemandLayer.EconomyHeroPrerequisite);
        // this reserves it the instant that demand is the accepted Economy target for this pass,
        // whether or not a builder route exists yet. Same owner key and same EconomyDeferredBuild
        // reason as the witnessed-route path above, so EconomyBuildCompletion transitions it
        // exactly the same way once a builder actually starts moving/building.
        internal static void ReserveDeferredEconomyResourcesForPendingHero(
            PlayerSetupData player, int turn, AxisDemand heroPrerequisiteDemand)
        {
            string owner = EconomyHeroPrerequisiteOwner(heroPrerequisiteDemand);
            if (owner == null)
                return;
            ReserveDeferredEconomyResourcesCore(player, turn, owner, heroPrerequisiteDemand);
        }

        // The build an Economy Hero-prerequisite demand serves, as its reservation owner key; null
        // for any other demand. See AxisDemand.EconomyHeroBuildOwner.
        internal static string EconomyHeroPrerequisiteOwner(AxisDemand demand)
        {
            if (demand == null || demand.RequestingAxis != DesireAxis.Economy
                || demand.Capability != CapabilityKind.Hero || !demand.TargetHex.HasValue
                || demand.EconomyBuildResourceCost == null)
                return null;
            return EconomyReservationOwner(new AxisDemand
            {
                Capability = demand.EconomyBuildCard?.Definition?.cardType == CardType.Base
                    ? CapabilityKind.EconomicExpansionBase
                    : CapabilityKind.EconomicInfrastructure,
                TargetHex = demand.TargetHex,
                EconomyResourceType = demand.EconomyResourceType,
            });
        }

        private static void ReserveDeferredEconomyResourcesCore(
            PlayerSetupData player, int turn, string owner, AxisDemand demand, float buildAp = 0f)
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
                    demand.EconomyBuildResourceCost, buildAp))
                return;
            StrategicResourceReservationLedger.ReplaceReasonOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, owner,
                replaceOwnerRows: true);
            ReserveEconomyCost(player, turn, owner, demand.EconomyBuildResourceCost, buildAp,
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
                StrategicResourceReservationLedger.ReleaseByOwner(player, turn, owner);
                return;
            }
            if (completionThisTurn)
                return;

            // Explicit owner-scoped downgrade: a repeated Phase A deferred request MUST NOT
            // implicitly demote a still-executable Completion, but this settled lifecycle
            // decision has proved it cannot finish this turn. Keep only durable H/E/M/T.
            StrategicResourceReservationLedger.ReplaceReasonOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, owner, replaceOwnerRows: true);
            ReserveEconomyCost(player, turn, owner, intent.Economy.BuildResourceCost, 0f,
                StrategicReservationReason.EconomyDeferredBuild);
        }

        // Called after settled operational steps AND immediately before each Phase B admission.
        // Compute current-turn feasibility from the REAL actor/path/AP/card, not the snapshot
        // used by the earlier Provisioning prediction. No global world rebuild or AP threshold.
        internal static void ReconcileEconomyCompletionReservations(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (player == null || root == null || ctx == null)
                return;
            int turn = ctx.TurnNumber;
            IReadOnlyList<string> owners =
                StrategicResourceReservationLedger.CompletionOwners(player, turn);
            if (owners.Count == 0)
                return;
            List<MissionIntent> intents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Kind == MissionKind.Economy && i.Economy != null
                    && (i.Economy.Kind == EconomyTaskKind.FoundBase
                        || i.Economy.Kind == EconomyTaskKind.BuildExtraction))
                .ToList();
            foreach (string owner in owners)
            {
                MissionIntent intent = intents.FirstOrDefault(i =>
                    EconomyMissionPlanner.OwnerKey(i.LastAttemptKey) == owner);
                if (intent == null)
                {
                    ReconcileEconomyCompletionOwner(player, turn, owner, null, false, false);
                    continue;
                }
                EconomyIntent build = intent.Economy;
                ArmyData actor = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
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
                    && (build.BuildResourceCost == null || build.BuildResourceCost.CanAfford(root));
                ReconcileEconomyCompletionOwner(player, turn, owner, intent, true,
                    completionThisTurn);
            }
        }

        // owner == null is the whole-reason reset (no Economy obligation survived this pass); an
        // explicit owner drops only that build's deferred rows and leaves every other build's hold.
        internal static void ClearDeferredEconomyResources(PlayerSetupData player, int turn,
            string owner = null) =>
            StrategicResourceReservationLedger.ReplaceReasonOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, owner);

        // One canonical writer for direct, deferred and provisioned Economy build reservations.
        // Provisioning adds AP only when completion is reachable this turn; Phase A protects only
        // persistent H/E/M/T while a confirmed route is still being delivered.
        internal static void ReserveEconomyCost(PlayerSetupData player, int turn, string owner,
            ResourceCost cost, float buildAp,
            StrategicReservationReason reason = StrategicReservationReason.EconomyBuildCompletion)
        {
            if (player == null || string.IsNullOrEmpty(owner))
                return;
            if (reason == StrategicReservationReason.EconomyBuildCompletion)
            {
                // This owner's own deferred hold is being promoted to completion — downgrade only
                // ITS rows; every other Economy build's deferred H/E/M/T hold stays.
                StrategicResourceReservationLedger.ReplaceReasonOwner(player, turn,
                    StrategicReservationReason.EconomyDeferredBuild, owner, replaceOwnerRows: true);
                if (StrategicResourceReservationLedger.OwnerReasonMatches(player, turn, owner,
                        reason, cost, buildAp))
                    return;
                StrategicResourceReservationLedger.ReplaceReasonOwner(player, turn,
                    StrategicReservationReason.EconomyBuildCompletion, owner,
                    replaceOwnerRows: true);
            }
            if (buildAp > 0f)
                StrategicResourceReservationLedger.Upsert(player, turn,
                    new StrategicResourceReservation
                    {
                        Owner = owner, Reason = reason,
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
                StrategicResourceReservationLedger.Upsert(player, turn,
                    new StrategicResourceReservation
                    {
                        Owner = owner, Reason = reason,
                        Resource = StrategicResourceReservationLedger.Map(type), Amount = amount,
                        ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                    });
            }
        }

        private static InfraCandidate BuildEconomyBaseCandidate(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            AxisDemand demand)
        {
            if (!demand.TargetHex.HasValue || demand.EconomyBuildCard == null
                || !HexSelectionController.HasOwnHeroArmyAt(demand.TargetHex.Value, player)
                || !BuildingPlayExecutor.CanFoundBaseAt(player, hand, ctx,
                    demand.EconomyBuildCard, demand.TargetHex.Value, out _))
                return null;
            CardData card = demand.EconomyBuildCard;
            HexCoord hex = demand.TargetHex.Value;
            int? builderId = EconomyBuilderAtTarget(snap, demand, hex);
            return new InfraCandidate
            {
                ApCost = card.EffectivePlayApCost,
                BuilderArmyId = builderId,
                ResCost = card.EffectivePlayResourceCost,
                TargetHex = hex,
                Explain = $"Base {card.Definition.displayName} @({hex.Q},{hex.R})",
                Execute = () => BuildingPlayExecutor.PlayBaseCard(player, root, hand, ctx, card, hex),
            };
        }

        private static int? EconomyBuilderAtTarget(
            WorldSnapshot snap, AxisDemand demand, HexCoord target)
        {
            if (snap?.Self?.Armies == null)
                return null;
            var candidates = snap.Self.Armies.Where(a => a != null && a.HasHero
                    && !a.IsPrison && !a.IsAir && a.Hex.Equals(target))
                .OrderBy(a => demand?.EconomyPreferredBuilderArmyId == a.ArmyId ? 0 : 1)
                .ThenBy(a => demand?.EconomyBuilderRoutes != null
                    && demand.EconomyBuilderRoutes.Any(r => r.ArmyId == a.ArmyId && r.IsOnTarget)
                        ? 0 : 1)
                .ThenBy(a => a.ArmyId)
                .ToList();
            return candidates.Count > 0 ? candidates[0].ArmyId : (int?)null;
        }

        // ECO — extraction facility for demand.EconomyResourceType on a same-type known unbuilt
        // site with a hero present. No generic-Base fallback (spec §4).
        private static InfraCandidate BuildEconomyCandidate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand)
        {
            ResourceType? type = demand.EconomyResourceType ?? ResourceTypeAt(snap, demand.TargetHex);
            if (type == null)
                return null;
            CardDefinition facilityDef = ExtractionDef(ctx, type.Value);
            if (facilityDef == null)
                return null;

            if (!demand.TargetHex.HasValue
                || !CandidateEconomyHexes(snap, type.Value, demand.TargetHex)
                    .Any(h => h.Equals(demand.TargetHex.Value))
                || !HexSelectionController.HasOwnHeroArmyAt(demand.TargetHex.Value, player))
                return null;
            HexCoord built = demand.TargetHex.Value;
            int? builderId = EconomyBuilderAtTarget(snap, demand, built);
            return new InfraCandidate
            {
                ApCost = facilityDef.apCost,
                BuilderArmyId = builderId,
                ResCost = facilityDef.resourceCost,
                Explain = $"extraction {facilityDef.displayName} @({built.Q},{built.R}) for {type.Value}",
                Execute = () => BuildingPlayExecutor.BuildExtractionFacility(player, root, ctx, facilityDef, built),
            };
        }

        // Canonical fog-honest extraction opportunities prepared by Analysis. No facility,
        // ownership, slot-capacity or marginal-yield rule is reconstructed here.
        private static IEnumerable<HexCoord> CandidateEconomyHexes(WorldSnapshot snap,
            ResourceType type, HexCoord? preferred)
        {
            var sites = (snap?.Economy?.ExtractionOpportunities
                    ?? System.Array.Empty<EconomyExtractionOpportunity>())
                .Where(site => site.ResourceType == type
                    && site.MarginalIncomeGain > AiConfigV2.allocatorSliceEpsilon)
                .Select(site => site.Hex)
                .OrderBy(h => preferred.HasValue && h.Equals(preferred.Value) ? 0 : 1)
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .ToList();
            foreach (HexCoord h in sites)
                yield return h;
        }

        // DEV — a CardType.Facility with Research/Production, into an owned Base slot.
        private static InfraCandidate BuildDevelopmentCandidate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand)
        {
            if (hand?.Hand == null)
                return null;
            List<(CardData Card, int Ordinal)> cards = hand.Hand
                .Select((card, ordinal) => (Card: card, Ordinal: ordinal))
                .Where(x => x.Card?.Definition != null && x.Card.Definition.cardType == CardType.Facility
                    && x.Card.Definition.grantedAbilities != null
                    && x.Card == demand.DevOpportunity?.PreparationFacilityCard
                    && demand.DevelopmentOperatorMode.HasValue
                    && x.Card.Definition.grantedAbilities.Contains(
                        ResearchProductionSystem.FacilityAbility(demand.DevelopmentOperatorMode.Value)))
                .ToList();
            List<HexCoord> bases = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player && b.IsBase)
                .Select(b => b.Hex)
                .OrderBy(h => h.Q).ThenBy(h => h.R)
                .ToList();
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            var legal = new List<InfraCandidate>();
            foreach ((CardData card, int ordinal) in cards)
            {
                StrategicCardUseCandidate use = StrategicCardEvaluator.ScoreNonCombat(
                    NonCombatRole.Facility, card, snap, inv, hand, bestEquipmentUpgrade: 0f);
                foreach (HexCoord baseHex in bases)
                {
                    if (demand.TargetHex.HasValue && !demand.TargetHex.Value.Equals(baseHex))
                        continue;
                    if (!BuildingPlayExecutor.CanPlaceFacilityAt(player, hand, ctx, card, baseHex, out _))
                        continue;
                    if (!StrategicSpendability.FitsSpendableResources(
                            player, root, ctx, card.EffectivePlayResourceCost))
                        continue;
                    CardData selectedCard = card;
                    HexCoord selectedHex = baseHex;
                    legal.Add(new InfraCandidate
                    {
                        ApCost = selectedCard.EffectivePlayApCost,
                        ResCost = selectedCard.EffectivePlayResourceCost,
                        DecisionScore = use.NetScore,
                        HandOrdinal = ordinal,
                        TargetHex = selectedHex,
                        Explain = $"Facility {selectedCard.Definition.displayName} into Base @({selectedHex.Q},{selectedHex.R})",
                        Execute = () => BuildingPlayExecutor.PlayFacilityCard(
                            player, root, hand, ctx, selectedCard, selectedHex),
                    });
                }
            }
            return BestDevelopmentCandidate(legal);
        }

        // DEV OPERATOR — a hand card carrying the mode's role ability (Researcher / Assembler),
        // deposited into the garrison on an existing but unstaffed facility hex through the
        // authoritative CardPlayExecutor. Parallel to BuildDevelopmentCandidate (a hand card onto a
        // known hex), NOT the materialization chain. Returns null (demand stays open, retried next
        // turn) when no matching legal card is in hand or the garrison cannot take it. Card category
        // is deliberately not an AI criterion; CardPlayExecutor remains the authoritative owner of
        // which deployable categories the current game rules support.
        private static InfraCandidate BuildDevelopmentOperatorCandidate(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            AxisDemand demand, MaterializationReservation reservation)
        {
            if (hand?.Hand == null || snap?.Development?.Facilities == null)
                return null;

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            var legal = new List<InfraCandidate>();
            foreach (DevelopmentFacility fac in snap.Development.Facilities)
            {
                if (fac.HasHero || fac.Contested)
                    continue;
                if (demand.DevelopmentOperatorMode.HasValue
                    && demand.DevelopmentOperatorMode.Value != fac.Mode)
                    continue;
                if (demand.TargetHex.HasValue && !demand.TargetHex.Value.Equals(fac.Hex))
                    continue;

                string role = ResearchProductionSystem.RoleAbility(fac.Mode);
                ArmyData garrison = ArmyRegistry.AllAt(fac.Hex)
                    .FirstOrDefault(a => a != null && a.Owner == player && a.IsGarrison && !a.IsPrison);
                if (garrison == null || !PlacementRules.CanDepositIntoGarrison(garrison))
                    continue;

                foreach ((CardData card, int ordinal) in hand.Hand.Select((card, ordinal) => (card, ordinal)))
                {
                    IReadOnlyList<string> abilities = card?.Definition != null
                        ? MaterializationChainMatching.EffectiveAbilities(card.Definition, card.Equipment)
                        : null;
                    if (card != demand.DevOpportunity?.PreparationOperatorCard
                        || abilities == null || !abilities.Contains(role))
                        continue;

                    var placement = new PlacementOption(fac.Hex, DeploymentKind.Garrison, garrison);
                    CardPlayPlan preflight = placement.Bind(card);
                    if (!CardPlayExecutor.Preflight(player, root, hand, ctx, preflight, out string why))
                    {
                        AiDebugLog.Write($"[AI][V2]   infra — DevelopmentOperator preflight fail "
                            + $"@({fac.Hex.Q},{fac.Hex.R}) {card.Definition.displayName}: {why}");
                        continue;
                    }
                    if (!StrategicSpendability.FitsSpendableResources(
                            player, root, ctx, card.EffectivePlayResourceCost))
                        continue;

                    MaterializationPlan valuationPlan = MaterializationPlanFactory.MakeExistingPlan(
                        MaterializationChainKind.Direct, demand, card, ordinal, null, -1, placement,
                        abilities);
                    StrategicCardUseCandidate use = StrategicCardEvaluator.ScoreForDemand(
                        valuationPlan, demand, valuationPlan.ExpectedTraits, inv,
                        card.Definition.moveMax, hasCompetingHeroDemand: false, snap);

                    HexCoord at = fac.Hex;
                    CardData selectedCard = card;
                    ResearchProductionMode mode = fac.Mode;
                    legal.Add(new InfraCandidate
                    {
                        ApCost = selectedCard.EffectivePlayApCost,
                        ResCost = selectedCard.EffectivePlayResourceCost,
                        DecisionScore = use.NetScore,
                        HandOrdinal = ordinal,
                        TargetHex = at,
                        Explain = $"operator {selectedCard.Definition.displayName} ({mode}) into facility garrison @({at.Q},{at.R})",
                        Execute = () =>
                        {
                            ArmyData g = ArmyRegistry.AllAt(at)
                                .FirstOrDefault(a => a != null && a.Owner == player && a.IsGarrison && !a.IsPrison);
                            CardPlayResult r = CardPlayExecutor.Play(player, root, hand, ctx,
                                CardPlayPlan.Into(selectedCard, at, DeploymentKind.Garrison, g));
                            return new BuildingPlayResult
                            {
                                Built = r.Deployed,
                                ApSpent = r.ApSpent,
                                ResourcesSpent = r.ResourcesSpent,
                                StateChanged = r.StateChanged,
                                CardConsumed = r.Deployed,
                                StateVersionAfter = r.StateVersionAfter,
                                FailReason = r.FailReason,
                            };
                        },
                    });
                }
            }
            InfraCandidate handOperator = BestDevelopmentCandidate(legal);
            if (handOperator != null)
                return handOperator;

            // Only a REAL, already-staffed generator can manufacture the missing operator.
            // Re-enumerate through GenerationSource to respect exact hero/card retry identity,
            // the one turn-wide generation cap and protected resource reservations. Never
            // create an imaginary producer, recursively mint, or steal a committed hero.
            GenerationStep proposed = demand.DevOpportunity?.PreparationOperatorGeneration;
            if (proposed == null || reservation == null || !reservation.CanGenerateMore
                || proposed.CardDef?.cardType != CardType.Hero || proposed.CardDef.isAviation
                || !demand.TargetHex.HasValue || !demand.DevelopmentOperatorMode.HasValue
                || !MaterializationChainMatching.EffectiveAbilities(proposed.CardDef, null)
                    .Contains(ResearchProductionSystem.RoleAbility(demand.DevelopmentOperatorMode.Value)))
                return null;
            BuildingData destination = BuildingRegistry.FindAt(demand.TargetHex.Value);
            ArmyData destinationGarrison = ArmyRegistry.AllAt(demand.TargetHex.Value)
                .FirstOrDefault(a => a != null && a.Owner == player && a.IsGarrison && !a.IsPrison);
            if (destination == null || destination.Owner != player
                || !destination.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(
                    demand.DevelopmentOperatorMode.Value))
                || destinationGarrison == null || !PlacementRules.CanDepositIntoGarrison(destinationGarrison)
                || !ArmyActions.HasRequiredGroundDeploymentBuilding(player, demand.TargetHex.Value, proposed.CardDef))
                return null;
            GenerationStep live = GenerationSource.Enumerate(player, root, ctx, hand,
                reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards)
                .FirstOrDefault(g => g.CardKey == proposed.CardKey && g.Hero == proposed.Hero
                    && g.CardDef == proposed.CardDef);
            if (live == null || live.SuccessChance <= 0f)
                return null;
            return new InfraCandidate
            {
                Generation = live,
                ApCost = ResearchProductionSystem.AttemptApCost(live.CardDef),
                ResCost = live.GenerationResourceCost,
                DecisionScore = demand.Value, TargetHex = demand.TargetHex.Value,
                Explain = $"generate operator {live.CardDef.displayName} at "
                    + $"({live.FacilityHex.Q},{live.FacilityHex.R}) for "
                    + $"({demand.TargetHex.Value.Q},{demand.TargetHex.Value.R})",
            };
        }

        private static InfraCandidate BestDevelopmentCandidate(IEnumerable<InfraCandidate> candidates) =>
            candidates
                .OrderByDescending(c => c.DecisionScore)
                .ThenBy(c => c.TargetHex.Q)
                .ThenBy(c => c.TargetHex.R)
                .ThenBy(c => c.HandOrdinal)
                .FirstOrDefault();

        private static ResourceType? ResourceTypeAt(WorldSnapshot snap, HexCoord? hex)
        {
            if (snap?.Known?.ResourceHexes == null || hex == null)
                return null;
            foreach (AiMapMemory.KnownResourceHex kv in snap.Known.ResourceHexes)
                if (kv.Hex.Equals(hex.Value))
                    return kv.DominantType;
            return null;
        }

        private static CardDefinition ExtractionDef(AiTurnContext ctx, ResourceType type)
        {
            CardDefinition[] arr = ctx?.GameConfig?.extractionFacilityCards;
            int i = (int)type;
            return arr != null && i >= 0 && i < arr.Length ? arr[i] : null;
        }
    }
}
