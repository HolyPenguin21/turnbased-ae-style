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
    //  complete cost -> check the live Phase A AP budget -> check live
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
        // A paid stand-alone Base-level step (Development CapacityUnlock): a real, successful action that
        // is NOT a facility, a played card or a new building. It never satisfies a "facility is ready" demand.
        public bool CapacityUnlocked;
        public float ApSpent;
        public ResourceCost ResourcesSpent;   // forwarded from the BuildingPlayResult
        public bool StateChanged;
        // DEV path plays a CardType.Facility card out of hand; the ECO extraction path is a
        // hero-built site with NO hand card — Outcome.Played must reflect that, not "Built".
        public bool CardPlayed;
        public int AdditionalCardsConsumed;
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
            succeeded: Built || CapacityUnlocked, stateChanged: StateChanged, apSpent: ApSpent,
            resourcesSpent: ResourcesSpent,
            played: CardPlayed, generated: Generated, attached: false, moved: false, created: Built,
            needsReplan: GenerationAttempted || CapacityUnlocked, stateVersionAfter: StateVersionAfter,
            failReason: Built || CapacityUnlocked || Generated ? null : Detail);
    }

    internal static class InfrastructureFulfillment
    {
        public static bool Handles(CapabilityKind k) =>
            k == CapabilityKind.EconomicInfrastructure
            || k == CapabilityKind.EconomicExpansionBase
            || k == CapabilityKind.DevelopmentInfrastructure
            || k == CapabilityKind.DevelopmentOperator
            || k == CapabilityKind.GlobalResourceFacility;

        // This existing staffing owner validates persisted claims before Phase A/B and after
        // infrastructure mutations. Never duplicate generation, movement or card execution.
        // Current AP/resources are deliberately NOT eligibility criteria for a future turn.
        internal static void RestoreGeneratedOperatorClaims(PlayerSetupData player,
            AiHandData hand, int turn, MaterializationReservation reservation)
        {
            if (player == null || reservation == null) return;
            IReadOnlyList<CardData> cards = MissionIntentRegistry.GetOrCreate(player)
                .Development.ReconcileGeneratedDevelopmentOperators(turn, (card, site, mode) =>
                {
                    if (card?.Definition?.cardType != CardType.Hero
                        || hand?.Hand?.Contains(card) != true
                        || !MaterializationChainMatching.EffectiveAbilities(
                            card.Definition, card.Equipment, card.Mutator)
                            .Contains(ResearchProductionSystem.RoleAbility(mode)))
                        return false;
                    if (!DevelopmentOpportunityEvaluator.IsPreparationSite(player, site)
                        || ResearchProductionSystem.FindActor(player, site, mode) != null
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
            // CapacityUnlock: buy exactly this Base level (re-confirmed by the gameplay primitive).
            public BuildingData CapacityBuilding;
            public BaseUpgradeTier CapacityTier;
            public int CapacityExpectedLevel;
        }

        // What Phase A's arbiter reads before choosing between this build lane and the materialization
        // portfolio: the intrinsic card score of the exact action TryFulfill would run NOW and
        // whether every admission gate (shared AP pool, spendable resources, live affordability)
        // passes. Pure: nothing is reserved, spent or mutated here.
        internal readonly struct InfraProposal
        {
            public readonly bool Admissible;
            public readonly float Score;
            public readonly string Reason;
            public InfraProposal(bool admissible, float score, string reason)
            { Admissible = admissible; Score = score; Reason = reason; }
        }

        internal static InfraProposal Propose(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            PhaseAApBudget apBudget, MaterializationReservation reservation = null,
            IReadOnlyList<MissionIntent> activeIntents = null, ActorCommitments commitments = null)
        {
            if (demand == null || ctx == null || root == null || player == null)
                return new InfraProposal(false, 0f, "missing args");
            InfraCandidate cand = BuildCandidate(snap, player, root, hand, ctx, demand, apBudget,
                reservation, activeIntents, commitments);
            if (cand == null)
                return new InfraProposal(false, 0f,
                    $"{demand.Capability}: no legal authoritative build available now");
            string refusal = AdmissionRefusal(player, root, ctx, demand, cand, apBudget);
            return refusal != null ? new InfraProposal(false, cand.DecisionScore, refusal)
                : new InfraProposal(true, cand.DecisionScore, cand.Explain);
        }

        private static InfraCandidate BuildCandidate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            PhaseAApBudget apBudget, MaterializationReservation reservation,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments) =>
            demand.Capability == CapabilityKind.EconomicInfrastructure
                ? BuildEconomyCandidate(snap, player, root, hand, ctx, demand)
                : demand.Capability == CapabilityKind.EconomicExpansionBase
                    ? BuildEconomyBaseCandidate(snap, player, root, hand, ctx, demand, apBudget,
                        reservation, activeIntents, commitments)
                : demand.Capability == CapabilityKind.DevelopmentInfrastructure
                    ? BuildDevelopmentCandidate(snap, player, root, hand, ctx, demand, activeIntents)
                    : demand.Capability == CapabilityKind.DevelopmentOperator
                        ? BuildDevelopmentOperatorCandidate(snap, player, root, hand, ctx,
                            demand, reservation)
                        : demand.Capability == CapabilityKind.GlobalResourceFacility
                            ? BuildGlobalResourceFacilityCandidate(snap, player, root, hand, ctx, demand)
                            : null;

        // The budget admission BEFORE any gameplay mutation (spec §1): null = admitted.
        private static string AdmissionRefusal(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AxisDemand demand, InfraCandidate cand, PhaseAApBudget apBudget)
        {
            SpendAuthority authority = SpendAuthorityFor(demand);
            // Radar already affected demand value/priority; this admission reads the ONE
            // unreserved AP pool.
            if (apBudget != null)
            {
                float axisRoom = apBudget.UnreservedBalance();
                if (cand.ApCost > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return $"shared AP pool {axisRoom:0.##} < {DesireAxes.Abbrev(demand.RequestingAxis)} demand cost {cand.ApCost:0.##}";
            }
            // Respect the same strategic + legacy persistent-resource reservations as every
            // materialization path. Raw gameplay affordability is still rechecked below.
            // SpendAuthorityFor: an Economy build here (or a global source put into play) completes
            // NOW and outranks other builds' deferred holds; DEV infrastructure has no Economy
            // authority and keeps respecting them like any other card spend.
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, cand.ResCost, authority))
                return $"{demand.Capability}: reserved resources cannot cover {cand.Explain}";
            // Live gameplay affordability (the executor re-checks; this keeps the demand open
            // cleanly rather than letting a doomed transaction run).
            float spendableAp = StrategicSpendability.SpendableAp(player, root, ctx, authority);
            if (cand.ApCost > spendableAp + AiConfigV2.allocatorSliceEpsilon
                || !root.CanSpendActionPoints(UnityEngine.Mathf.CeilToInt(cand.ApCost))
                || (cand.ResCost != null && !cand.ResCost.CanAfford(root)))
                return $"{demand.Capability}: live AP/resources cannot cover {cand.Explain}";
            return null;
        }

        public static InfraFulfillResult TryFulfill(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            PhaseAApBudget apBudget, MaterializationReservation reservation = null,
            IReadOnlyList<MissionIntent> activeIntents = null, ActorCommitments commitments = null)
        {
            if (demand == null || ctx == null || root == null || player == null)
                return InfraFulfillResult.No("missing args");

            InfraCandidate cand = BuildCandidate(snap, player, root, hand, ctx, demand, apBudget,
                reservation, activeIntents, commitments);
            if (cand == null)
                return InfraFulfillResult.No($"{demand.Capability}: no legal authoritative build available now");
            SpendAuthority authority = SpendAuthorityFor(demand);
            string economyOwner = authority.Owner;
            string refusal = AdmissionRefusal(player, root, ctx, demand, cand, apBudget);
            if (refusal != null)
                return InfraFulfillResult.No(refusal);

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
                int version = generated.StateChanged ? WorldDeltaLifecycle.CommitMutation() : WorldDeltaLifecycle.Current;
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

            // A stand-alone Base level: one payment + one mutation by the gameplay primitive, then ONE
            // world receipt. No card is played, no facility exists yet - the placement is a later action.
            if (cand.CapacityTier != null)
                return ExecuteCapacityUnlock(player, root, ctx, cand);

            // --- authoritative transaction ---
            BuildingPlayResult r = cand.Execute();
            if (r.Built && economyOwner != null)
                MissionLeaseBook.ReleaseByOwner(player, ctx.TurnNumber,
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
                AdditionalCardsConsumed = r.AdditionalCardsConsumed,
                BuilderArmyId = cand.BuilderArmyId,
                StateVersionAfter = r.StateVersionAfter, Detail = cand.Explain };
        }

        internal static string EconomyReservationOwner(AxisDemand demand) => EconomyReservationIdentity(demand);
        internal static string EconomyBuildOwner(EconomyTaskKind kind, ResourceType? resourceType,
            HexCoord target) => EconomyBuildIdentity(kind, resourceType, target);
        internal static string EconomyHeroPrerequisiteOwner(AxisDemand demand) => EconomyHeroPrerequisiteIdentity(demand);

        internal static ReservationOwner EconomyReservationIdentity(AxisDemand demand)
        {
            if (demand?.TargetHex == null || (demand.Capability != CapabilityKind.EconomicInfrastructure
                && demand.Capability != CapabilityKind.EconomicExpansionBase))
                return null;
            return EconomyBuildIdentity(DemandLayer.EconomyBuildKind(demand),
                demand.EconomyResourceType, demand.TargetHex.Value);
        }

        // The ONE demand-side rule for which reservations the action closing `demand` may draw on
        // (StrategicSpendability.SpendAuthority); read by every stage through
        // AxisDemand.SpendAuthority — Phase-A admission, pricing, portfolio, execution and here.
        //   · an on-hex Economy build (EconomicInfrastructure / ExpansionBase): its own owner key,
        //     and it completes NOW;
        //   · an Economy global-resource source (Facility or Unit/Hero carrier) put into play:
        //     completes NOW, no own hold;
        //   · a builder-Hero prerequisite: only its own pending build's hold — the hero is that
        //     build's first step, not a completion;
        //   · anything else: no special authority.
        internal static SpendAuthority SpendAuthorityFor(AxisDemand demand)
        {
            string buildOwner = EconomyReservationOwner(demand);
            if (buildOwner != null)
                return new SpendAuthority(buildOwner, economyCompletesNow: true);
            if (demand != null && demand.RequestingAxis == DesireAxis.Economy
                && (demand.Capability == CapabilityKind.GlobalResourceFacility
                    || demand.Capability == CapabilityKind.GlobalResourceCarrier))
                return new SpendAuthority(null, economyCompletesNow: true);
            // 2026-10-02 playtest (Rurik/Kryll): a Collector's unit costs 1 Human, but a deferred
            // Base build held ALL the Human (stock 1-3, hold 2-3) for turns, so ~600 collector
            // attempts failed "resources need H=1 have=0" - and the collector is the only thing
            // that raises the very income the build waits for. A Collector of a SCARCE resource (any of
            // the four, never a surplus one - EconomyResourceScarce) is one unit and pays off by
            // income, so like a build that completes now it may draw on OTHER builds' deferred holds (they shield
            // H/E/M/T from non-Economy spending; this IS Economy spending). Every other hold -
            // reaction envelope, Attack continuation, its own build - still counts.
            if (demand != null && demand.RequestingAxis == DesireAxis.Economy
                && demand.Capability == CapabilityKind.CollectorCapability
                && demand.EconomyResourceScarce)
                return new SpendAuthority(null, economyCompletesNow: true);
            string heroBuildOwner = EconomyHeroPrerequisiteOwner(demand);
            if (heroBuildOwner != null)
                return new SpendAuthority(heroBuildOwner, economyCompletesNow: false);
            // 2026-10-01 (user decision): a card that builds the Attack's own strike force may use
            // the AP held for the Attack's next step (the derived operation-continuation claim);
            // every other card plays around it.
            return demand != null && demand.UsesAttackContinuationAp
                ? new SpendAuthority(TurnResourceBook.OperationContinuationOwner, economyCompletesNow: false)
                : default;
        }

        // The reservation owner key of one build: the same key its mission and intent carry.
        internal static ReservationOwner EconomyBuildIdentity(EconomyTaskKind kind, ResourceType? resourceType,
            HexCoord target)
        {
            return EconomyMissionPlanner.ReservationIdentity(StableMissionKey.ForEconomy(kind,
                MissionIntentKey.EconomyObjectiveId(kind, null, null, resourceType), target));
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
            // The accepted build's prerequisite (Hero or its pinned escort) keeps the same
            // persistent-resource hold before a ready builder/route exists. A speculative
            // new-Hero alternative alongside a ready builder is not a second obligation.
            if (EconomyHeroPrerequisiteOwner(demand) != null && !demand.IsEconomyNewHeroAlternative)
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
                    && ((a.IsMobileEconomyBuilder
                            && !(a.OperatorDutyBlocksDeparture && !route.IsOnTarget))
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
            ReservationOwner owner = EconomyReservationIdentity(demand);
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
            if (player == null || !MissionContinuityLayer.IsLiveEconomyBuild(intent)
                || economy.BuildResourceCost == null)
                return;

            ReservationOwner owner = EconomyMissionPlanner.ReservationIdentity(intent.LastAttemptKey);
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

        // A bare Hero or pinned escort prerequisite has no ready builder/route to witness.
        // Its accepted build's H/E/M/T must stay protected for however many turns Economy
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
            ReservationOwner owner = EconomyHeroPrerequisiteIdentity(heroPrerequisiteDemand);
            if (owner == null)
                return;
            ReserveDeferredEconomyResourcesCore(player, turn, owner, heroPrerequisiteDemand);
        }

        // The build an Economy Hero/escort prerequisite serves, as its reservation owner key; null
        // for any other demand. See AxisDemand.EconomyHeroBuildOwner.
        internal static ReservationOwner EconomyHeroPrerequisiteIdentity(AxisDemand demand)
        {
            if (demand == null || demand.RequestingAxis != DesireAxis.Economy
                || (demand.Capability != CapabilityKind.Hero
                    && !(demand.Capability == CapabilityKind.FieldCombatPower && demand.EconomyEscortArmyId.HasValue))
                || !demand.TargetHex.HasValue
                || demand.EconomyBuildResourceCost == null)
                return null;
            return EconomyBuildIdentity(DemandLayer.EconomyBuildKind(demand),
                demand.EconomyResourceType, demand.TargetHex.Value);
        }

        private static void ReserveDeferredEconomyResourcesCore(
            PlayerSetupData player, int turn, ReservationOwner owner, AxisDemand demand, float buildAp = 0f)
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
            MissionLeaseBook.ReplaceReasonOwner(player, turn,
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
                    // Through TurnResourceBook: its own hold and other builds' deferred holds are
                    // drawable, another owner's completion or the reaction envelope is not.
                    && StrategicSpendability.FitsSpendableForEconomyCompletion(
                        player, root, ctx, build.BuildResourceCost, owner);
                ReconcileEconomyCompletionOwner(player, turn, owner, intent, true,
                    completionThisTurn);
            }
        }

        // Called ONCE per turn (each Phase B entry path of AiStrategyV2Pipeline.RunTurn), after the operational loop has settled and every surviving
        // Completion owner has been reconciled — i.e. at the point where each remaining
        // EconomyDeferredBuild owner is proven NOT to complete this turn. Such a build is paid from
        // the stock of a later turn, and that stock always includes at least one more income tick
        // (IncomeProjection.IncomeFor — the same number the round-start grant pays). Holding the
        // full vector from today's stock froze exactly that part for Phase B while the build
        // could not use it anyway. The tick is shared by every deferred owner (deterministic owner
        // order), never counted twice. Next turn's Phase A re-writes the full vector
        // (OwnerReasonMatches mismatch), before Provisioning can promote it to Completion, so the
        // build turn still sees its whole cost protected. Phase A never calls this: there the
        // deferred stage is not yet proof that completion is impossible this turn.
        internal static void ReleaseDeferredEconomyIncomeCover(PlayerSetupData player,
            AiTurnContext ctx)
        {
            if (player == null || ctx?.Map == null)
                return;
            int turn = ctx.TurnNumber;
            IReadOnlyList<string> owners = StrategicResourceReservationLedger.OwnersWithReason(
                player, turn, StrategicReservationReason.EconomyDeferredBuild);
            if (owners.Count == 0)
                return;
            List<StrategicResourceReservation> rows = StrategicResourceReservationLedger
                .Rows(player, turn)
                .Where(r => r.Reason == StrategicReservationReason.EconomyDeferredBuild)
                .OrderBy(r => r.Owner, System.StringComparer.Ordinal)
                .ToList();
            foreach (ResourceType type in ResourceBundle.All)
            {
                float cover = IncomeProjection.IncomeFor(player, type, ctx.Map);
                StrategicReservedResource resource = StrategicResourceReservationLedger.Map(type);
                foreach (StrategicResourceReservation row in rows.Where(r => r.Resource == resource))
                {
                    if (cover <= 0f)
                        break;
                    float released = UnityEngine.Mathf.Min(cover, row.Amount);
                    cover -= released;
                    MissionLeaseBook.Upsert(player, turn,
                        new StrategicResourceReservation
                        {
                            Identity = row.Identity, Reason = row.Reason, Resource = row.Resource,
                            Amount = row.Amount - released, ExpirationStage = row.ExpirationStage,
                        });
                }
            }
            AiDebugLog.Write($"[AI][V2][Economy][DeferredIncomeCover] turn={turn} owners={owners.Count} "
                + $"active [{StrategicResourceReservationLedger.DebugLine(player, turn)}]");
        }

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
                // This owner's own deferred hold is being promoted to completion — downgrade only
                // ITS rows; every other Economy build's deferred H/E/M/T hold stays.
                MissionLeaseBook.ReplaceReasonOwner(player, turn,
                    StrategicReservationReason.EconomyDeferredBuild, owner, replaceOwnerRows: true);
                if (StrategicResourceReservationLedger.OwnerReasonMatches(player, turn, owner,
                        reason, cost, buildAp))
                    return;
                MissionLeaseBook.ReplaceReasonOwner(player, turn,
                    StrategicReservationReason.EconomyBuildCompletion, owner,
                    replaceOwnerRows: true);
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

        private static InfraCandidate BuildEconomyBaseCandidate(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            AxisDemand demand, PhaseAApBudget apBudget, MaterializationReservation reservation,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments)
        {
            if (!demand.TargetHex.HasValue || demand.EconomyBuildCard == null
                || !HexSelectionController.HasOwnHeroArmyAt(demand.TargetHex.Value, player)
                || !BuildingPlayExecutor.CanFoundBaseAt(player, hand, ctx,
                    demand.EconomyBuildCard, demand.TargetHex.Value, out _))
                return null;
            CardData card = demand.EconomyBuildCard;
            HexCoord hex = demand.TargetHex.Value;
            int? builderId = null;
            CardData defender = null;
            // Completion must be assessed from the current snapshot, not the outbound witness.
            // A guaranteed held-card continuation replaces only the body requirement; known
            // route/site threats still go through the same Economy assessment.
            foreach (EconomyBuilderRouteSnapshot route in WorldAnalysis.EconomyBuilderRoutes(snap, player, ctx, hex)
                .Where(r => r.IsOnTarget && !r.RequiresGarrisonExtraction)
                .OrderBy(r => r.ArmyId == demand.EconomyPreferredBuilderArmyId ? 0 : 1)
                .ThenBy(r => r.ArmyId))
            {
                if (!BuildingPlayExecutor.PlanBaseGarrison(player, root, hand, ctx, card, hex,
                    route.ArmyId, out CardData continuation, out _, admitDefender: candidate =>
                    {
                        float ap = card.EffectivePlayApCost + CardCostRules.PlayAp(candidate);
                        SpendAuthority authority = SpendAuthorityFor(demand);
                        return reservation?.ClaimsDevelopmentOperatorCard(candidate) != true
                            && reservation?.ClaimsEconomyBuildCard(candidate) != true
                            && (apBudget == null || ap <= apBudget.UnreservedBalance() + AiConfigV2.allocatorSliceEpsilon)
                            && ap <= StrategicSpendability.SpendableAp(player, root, ctx, authority) + AiConfigV2.allocatorSliceEpsilon
                            && StrategicSpendability.FitsSpendableResources(player, root, ctx,
                                BuildingPlayExecutor.BaseAndGarrisonCost(card, candidate), authority);
                    })) continue;
                // Composition readiness alone does not grant ownership of an Attack/Defence
                // mover standing here. Reuse the same candidate/claim gate as outbound planning.
                var choice = DemandLayer.SelectEconomyBuilder(snap, hex, new[] { route },
                    activeIntents, commitments, demand.EconomySiteValue > 0f
                        ? demand.EconomySiteValue : demand.Value,
                    card.EffectivePlayApCost, includeReturn: false, pinnedBuilderArmyId: route.ArmyId,
                    requiresFoundingGarrison: continuation == null);
                if (choice == null || choice.Suitability != DemandLayer.EconomyArmySuitability.Ready)
                    continue;
                builderId = route.ArmyId;
                defender = continuation;
                break;
            }
            if (!builderId.HasValue) return null;
            return new InfraCandidate
            {
                ApCost = card.EffectivePlayApCost + (defender == null ? 0 : CardCostRules.PlayAp(defender)),
                BuilderArmyId = builderId,
                ResCost = BuildingPlayExecutor.BaseAndGarrisonCost(card, defender),
                TargetHex = hex,
                Explain = $"Base {card.Definition.displayName} @({hex.Q},{hex.R})",
                Execute = () => BuildingPlayExecutor.PlayBaseCard(player, root, hand, ctx, card, hex, builderId, defender),
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

        private static InfraFulfillResult ExecuteCapacityUnlock(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, InfraCandidate cand)
        {
            int apBefore = root.ActionPoints;
            // No spend authority: the payment may not reach into anyone else's hold.
            ReservationInvariants.SpendProbe probe = ReservationInvariants.BeginSpend(
                player, root, ctx, "phaseA CapacityUnlock");
            BaseUpgradeOutcome outcome = InfrastructureActions.TryUpgradeBase(
                cand.CapacityBuilding, ctx.GameConfig?.baseUpgradeTiers, cand.CapacityExpectedLevel);
            ReservationInvariants.EndSpend(player, root, ctx, probe);
            if (!outcome.Ok)
            {
                // Nothing was paid or changed: the plan is stale. The next pass re-judges the world.
                AiDebugLog.Write($"[AI][V2]   infra — CapacityUnlock refused: {outcome.FailReason} ({cand.Explain})");
                return InfraFulfillResult.No(outcome.FailReason);
            }
            // ONE top-level mutation, one receipt; the placement later is its own mutation.
            int version = WorldDeltaLifecycle.CommitMutation();
            WorldDeltaLifecycle.Publish(player, ctx.TurnNumber,
                StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Capability,
                hexes: new[] { cand.TargetHex });
            return new InfraFulfillResult
            {
                Built = false, CapacityUnlocked = true, CardPlayed = false,
                ApSpent = apBefore - root.ActionPoints, ResourcesSpent = outcome.ResourcesSpent,
                StateChanged = true, StateVersionAfter = version,
                Detail = $"{cand.Explain} -> level {outcome.LevelAfter}, facility slots {outcome.UnlockedSlotsAfter}",
            };
        }

        // DEV CapacityUnlock - buy the next Base level so the confirmed hand Facility can be placed
        // later. The ADMITTED opportunity is a plan; everything it assumed is re-derived from the live
        // world here and a mismatch yields no candidate (a stale plan is never re-targeted at another
        // base, tier or card). The structural witnesses (operator path, catalog, mode not built elsewhere)
        // are re-read from the evaluator preparation facts, the one owner of that rule.
        private static InfraCandidate BuildCapacityUnlockCandidate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            DevelopmentOpportunity plan = demand.DevOpportunity;
            if (plan == null || !demand.TargetHex.HasValue || !demand.DevelopmentOperatorMode.HasValue
                || !plan.FacilityHex.Equals(demand.TargetHex.Value) || plan.Mode != demand.DevelopmentOperatorMode.Value
                || !DevelopmentOpportunityEvaluator.ConfirmCapacityUnlock(plan, snap, player, root, hand, ctx,
                    activeIntents, out BuildingData b, out BaseUpgradeTier tier))
                return null;
            HexCoord hex = plan.FacilityHex;
            ResearchProductionMode mode = plan.Mode;
            CardDefinition witness = plan.PreparationFacilityCard.Definition;
            float score = DevelopmentPreparationScorer.CapacityUnlock(tier, snap,
                t => StrategicSpendability.SpendableAmount(player, root, ctx, t), player);
            if (score <= AiConfigV2.allocatorSliceEpsilon)
                return null;
            return new InfraCandidate
            {
                ApCost = tier.apCost,
                ResCost = tier.cost,
                DecisionScore = score,
                TargetHex = hex,
                CapacityBuilding = b, CapacityTier = tier, CapacityExpectedLevel = b.Level,
                Explain = $"CapacityUnlock {b.Name}@({hex.Q},{hex.R}) level {b.Level}->{b.Level + 1} "
                    + $"for {witness.displayName} ({mode})",
            };
        }

        // DEV — a CardType.Facility with Research/Production, into an owned Base slot.
        private static InfraCandidate BuildDevelopmentCandidate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            if (hand?.Hand == null)
                return null;
            // The tier and the placement are separate actions: this request is one or the other.
            if (demand.DevOpportunity?.PreparationKind == DevelopmentPreparationKind.CapacityUnlock)
                return BuildCapacityUnlockCandidate(snap, player, root, hand, ctx, demand, activeIntents);
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
            var rejected = new List<string>();
            foreach ((CardData card, int ordinal) in cards)
            {
                foreach (HexCoord baseHex in bases)
                {
                    if (demand.TargetHex.HasValue && !demand.TargetHex.Value.Equals(baseHex))
                        continue;
                    string at = $"{card.Definition.displayName}@({baseHex.Q},{baseHex.R})";
                    // A full Base is NOT upgraded behind the placement: that is the stand-alone
                    // CapacityUnlock step. A placement pays only the Facility's own bill.
                    if (!BuildingPlayExecutor.CanPlaceFacilityAt(player, hand, ctx, card, baseHex,
                            out string placeReason))
                    {
                        rejected.Add($"{at}:{placeReason}");
                        continue;
                    }
                    ResourceCost stageCost = card.EffectivePlayResourceCost;
                    int stageAp = card.EffectivePlayApCost;
                    if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, stageCost))
                    {
                        rejected.Add($"{at}:spendable_resources");
                        continue;
                    }
                    float score = DevelopmentPreparationScorer.Facility(card, stageAp, stageCost, snap, inv,
                        hand, t => StrategicSpendability.SpendableAmount(player, root, ctx, t), player);
                    if (score <= AiConfigV2.allocatorSliceEpsilon)
                    {
                        rejected.Add($"{at}:not_worth_playing({score:0.00})");
                        continue;
                    }
                    CardData selectedCard = card;
                    HexCoord selectedHex = baseHex;
                    legal.Add(new InfraCandidate
                    {
                        ApCost = stageAp,
                        ResCost = stageCost,
                        DecisionScore = score,
                        HandOrdinal = ordinal,
                        TargetHex = selectedHex,
                        Explain = $"Facility {selectedCard.Definition.displayName} into Base @({selectedHex.Q},{selectedHex.R})",
                        Execute = () => BuildingPlayExecutor.PlayFacilityCard(
                            player, root, hand, ctx, selectedCard, selectedHex),
                    });
                }
            }
            if (legal.Count == 0 && rejected.Count > 0)
                AiDebugLog.Write($"[AI][V2]   infra — DevelopmentInfrastructure rejected: "
                    + string.Join(" | ", rejected));
            return BestDevelopmentCandidate(legal);
        }

        // ECO GLOBAL SOURCE — the pinned Facility carrying a PlayerGlobal recurring effect, into an
        // owned Base slot. Worth is the SAME non-combat Facility score Phase B uses
        // (ScoreNonCombat -> ResourceGainRoleFit); a non-positive play stays in hand. The Citadel is
        // preferred (the base least likely to be lost with the facility), then coordinates.
        private static InfraCandidate BuildGlobalResourceFacilityCandidate(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            AxisDemand demand)
        {
            CardData card = demand.EconomySourceCard;
            if (card?.Definition == null || hand?.Hand == null || !hand.Hand.Contains(card))
                return null;
            HexCoord citadel = snap?.Self != null ? snap.Self.Citadel : default;
            string lastReject = "no owned Base";
            HexCoord? at = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player && b.IsBase)
                .Select(b => b.Hex)
                .Where(h =>
                {
                    if (BuildingPlayExecutor.CanPlaceFacilityAt(player, hand, ctx, card, h, out string why))
                        return true;
                    lastReject = why ?? lastReject;
                    return false;
                })
                .OrderBy(h => h.Equals(citadel) ? 0 : 1)
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .Select(h => (HexCoord?)h)
                .FirstOrDefault();
            // Every Base slot is locked: the SAME capacity-unlock rule the Phase-B maintenance
            // candidate uses picks the Base/tier, and this one Economy action buys it and places
            // the Facility (the upgrade is this source's own prerequisite, like Development's).
            BuildingData upgradeBase = null;
            BaseUpgradeTier upgradeTier = null;
            if (!at.HasValue && StrategicMaintenancePolicy.TryFindCapacityUnlock(
                    card, player, hand, ctx, out upgradeBase, out upgradeTier))
                at = upgradeBase.Hex;
            if (!at.HasValue)
            {
                AiDebugLog.WriteDedupedWithId(demand.TraceId,
                    $"[AI][V2][Economy][GlobalSource] card={card.Definition.displayName} decision=WAIT "
                    + $"reason=no_legal_base_slot ({lastReject})");
                return null;
            }
            StrategicCardUseCandidate use = StrategicCardEvaluator.ScoreNonCombat(
                NonCombatRole.Facility, card, snap, CapabilityInventory.Build(snap, player, null),
                hand, bestEquipmentUpgrade: 0f);
            // The upgrade is priced once, on the canonical AP/resource price table.
            float upgradePrice = StrategicMaintenancePolicy.CapacityTierPrice(upgradeTier, snap);
            float net = use.NetScore - upgradePrice;
            if (net <= AiConfigV2.allocatorSliceEpsilon)
            {
                AiDebugLog.WriteDedupedWithId(demand.TraceId,
                    $"[AI][V2][Economy][GlobalSource] card={card.Definition.displayName} decision=HOLD "
                    + $"reason=non_positive_play net={use.NetScore:0.00} upgradePrice={upgradePrice:0.00}");
                return null;
            }
            HexCoord hex = at.Value;
            BuildingData buildingToUpgrade = upgradeBase;
            BaseUpgradeTier tierToBuy = upgradeTier;
            return new InfraCandidate
            {
                ApCost = card.EffectivePlayApCost + (tierToBuy?.apCost ?? 0),
                ResCost = StrategicCardEvaluator.AddResourceCosts(
                    card.EffectivePlayResourceCost, tierToBuy?.cost),
                DecisionScore = net,
                TargetHex = hex,
                Explain = $"global source Facility {card.Definition.displayName} into Base "
                    + $"@({hex.Q},{hex.R})"
                    + (tierToBuy != null ? $" after capacity upgrade to level {buildingToUpgrade.Level + 1}" : "")
                    + $" net={net:0.00}",
                Execute = () => PlaceFacilityAfterOptionalUpgrade(
                    player, root, hand, ctx, card, hex, buildingToUpgrade, tierToBuy),
            };
        }

        // One action: buy `tier` on `building` (when a slot must be unlocked first), then place the
        // Facility. Shared by every infrastructure path whose facility carries its own capacity
        // prerequisite (global resource source, Development facility stage).
        private static BuildingPlayResult PlaceFacilityAfterOptionalUpgrade(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, CardData card, HexCoord hex,
            BuildingData building, BaseUpgradeTier tier)
        {
            if (tier == null)
                return BuildingPlayExecutor.PlayFacilityCard(player, root, hand, ctx, card, hex);
            int apBefore = root.ActionPoints;
            if (!StrategicMaintenancePolicy.ExecuteCapacityUpgrade(player, root, ctx, building, tier))
                return BuildingPlayResult.Fail("capacity upgrade refused");
            int upgradeVersion = WorldDeltaLifecycle.CommitMutation();
            BuildingPlayResult placed = BuildingPlayExecutor.PlayFacilityCard(
                player, root, hand, ctx, card, hex);
            // The upgrade is a real mutation even if the placement then fails.
            placed.StateChanged = true;
            placed.ApSpent = apBefore - root.ActionPoints;
            placed.ResourcesSpent = StrategicCardEvaluator.AddResourceCosts(
                placed.ResourcesSpent, tier.cost);
            if (placed.StateVersionAfter < upgradeVersion)
                placed.StateVersionAfter = upgradeVersion;
            return placed;
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
            if (hand?.Hand == null || !demand.TargetHex.HasValue
                || !demand.DevelopmentOperatorMode.HasValue
                || !DevelopmentOpportunityEvaluator.IsPreparationSite(player, demand.TargetHex.Value))
                return null;

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            var legal = new List<InfraCandidate>();
            // Operator-first prepares the same own base; a facility is not a deployment precondition.
            var sites = new[] { new DevelopmentFacility
            {
                Hex = demand.TargetHex.Value, Mode = demand.DevelopmentOperatorMode.Value,
                HasHero = ResearchProductionSystem.FindActor(player, demand.TargetHex.Value,
                    demand.DevelopmentOperatorMode.Value) != null,
                Contested = Game.Combat.BattleInitiator.FindEnemyAt(demand.TargetHex.Value, player) != null,
            } };
            foreach (DevelopmentFacility fac in sites)
            {
                if (fac.HasHero || fac.Contested) continue;

                string role = ResearchProductionSystem.RoleAbility(fac.Mode);
                ArmyData garrison = ArmyRegistry.AllAt(fac.Hex)
                    .FirstOrDefault(a => a != null && a.Owner == player && a.IsGarrison && !a.IsPrison);
                if (garrison == null || !PlacementRules.CanDepositIntoGarrison(garrison))
                    continue;

                foreach ((CardData card, int ordinal) in hand.Hand.Select((card, ordinal) => (card, ordinal)))
                {
                    IReadOnlyList<string> abilities = card?.Definition != null
                        ? MaterializationChainMatching.EffectiveAbilities(card.Definition, card.Equipment, card.Mutator)
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

                    float score = DevelopmentPreparationScorer.HandOperator(card, ordinal, fac.Hex, fac.Mode,
                        garrison, snap, inv,
                        t => StrategicSpendability.SpendableAmount(player, root, ctx, t), player);
                    if (score <= AiConfigV2.allocatorSliceEpsilon)
                        continue;

                    HexCoord at = fac.Hex;
                    CardData selectedCard = card;
                    ResearchProductionMode mode = fac.Mode;
                    legal.Add(new InfraCandidate
                    {
                        ApCost = selectedCard.EffectivePlayApCost,
                        ResCost = selectedCard.EffectivePlayResourceCost,
                        DecisionScore = score,
                        HandOrdinal = ordinal,
                        TargetHex = at,
                        Explain = $"operator {selectedCard.Definition.displayName} ({mode}) into preparation base garrison @({at.Q},{at.R})",
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
                || !destination.IsBase
                || destinationGarrison == null || !PlacementRules.CanDepositIntoGarrison(destinationGarrison)
                || !ArmyActions.HasRequiredGroundDeploymentBuilding(player, demand.TargetHex.Value, proposed.CardDef))
                return null;
            GenerationStep live = GenerationSource.Enumerate(player, root, ctx, hand,
                reservation.ClaimedGeneratorUses, reservation.TriedGeneratorCards)
                .FirstOrDefault(g => g.CardKey == proposed.CardKey && g.Hero == proposed.Hero
                    && g.CardDef == proposed.CardDef);
            if (live == null || live.SuccessChance <= 0f)
                return null;
            // The same card score as the hand operator, for the card this Challenge would mint:
            // benefit contingent on success, the Challenge paid in full.
            float generatedScore = DevelopmentPreparationScorer.GeneratedOperator(live,
                demand.TargetHex.Value, demand.DevelopmentOperatorMode.Value, destinationGarrison,
                snap, inv, t => StrategicSpendability.SpendableAmount(player, root, ctx, t), player);
            if (generatedScore <= AiConfigV2.allocatorSliceEpsilon)
                return null;
            return new InfraCandidate
            {
                Generation = live,
                ApCost = ResearchProductionSystem.AttemptApCost(live.CardDef),
                ResCost = live.GenerationResourceCost,
                DecisionScore = generatedScore, TargetHex = demand.TargetHex.Value,
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


