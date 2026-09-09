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
    //  complete cost -> check the requesting axis's AxisBudgetLedger entitlement -> check live
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
        public int? BuilderArmyId;
        public int StateVersionAfter = -1;
        public string Detail;

        public static InfraFulfillResult No(string why) => new InfraFulfillResult { Detail = why };

        public V2ActionOutcome Outcome => new V2ActionOutcome(
            succeeded: Built, stateChanged: StateChanged, apSpent: ApSpent, resourcesSpent: ResourcesSpent,
            played: CardPlayed, generated: false, attached: false, moved: false, created: Built,
            needsReplan: false, stateVersionAfter: StateVersionAfter, failReason: Built ? null : Detail);
    }

    internal static class InfrastructureFulfillment
    {
        public static bool Handles(CapabilityKind k) =>
            k == CapabilityKind.EconomicInfrastructure
            || k == CapabilityKind.EconomicExpansionBase
            || k == CapabilityKind.DevelopmentInfrastructure
            || k == CapabilityKind.DevelopmentOperator;

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
            public System.Func<BuildingPlayResult> Execute;
        }

        public static InfraFulfillResult TryFulfill(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand, AxisBudgetLedger ledger)
        {
            if (demand == null || ctx == null || root == null || player == null)
                return InfraFulfillResult.No("missing args");

            InfraCandidate cand =
                demand.Capability == CapabilityKind.EconomicInfrastructure
                    ? BuildEconomyCandidate(snap, player, root, hand, ctx, demand)
                    : demand.Capability == CapabilityKind.EconomicExpansionBase
                        ? BuildEconomyBaseCandidate(snap, player, root, hand, ctx, demand)
                    : demand.Capability == CapabilityKind.DevelopmentInfrastructure
                        ? BuildDevelopmentCandidate(snap, player, root, hand, ctx)
                        : demand.Capability == CapabilityKind.DevelopmentOperator
                            ? BuildDevelopmentOperatorCandidate(snap, player, root, hand, ctx, demand)
                            : null;
            if (cand == null)
                return InfraFulfillResult.No($"{demand.Capability}: no legal authoritative build available now");
            string economyOwner = EconomyReservationOwner(demand);

            // --- budget admission BEFORE any gameplay mutation (spec §1). A building is a large
            //     discrete commitment: require the requesting axis's OWN unreserved entitlement to
            //     cover it (Balance, not the cross-axis discrete-borrow headroom) so an infra build
            //     can never push an axis entitlement negative. ---
            if (ledger != null)
            {
                float axisRoom = ledger.Balance(demand.RequestingAxis)
                                 - ledger.ReservedFollowup(demand.RequestingAxis);
                if (cand.ApCost > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return InfraFulfillResult.No(
                        $"{DesireAxes.Abbrev(demand.RequestingAxis)} axis entitlement {axisRoom:0.##} < cost {cand.ApCost:0.##}");
            }
            // Respect the same strategic + legacy persistent-resource reservations as every
            // materialization path. Raw gameplay affordability is still rechecked below.
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, cand.ResCost,
                    economyOwner))
                return InfraFulfillResult.No($"{demand.Capability}: reserved resources cannot cover {cand.Explain}");

            // --- live gameplay affordability (the executor re-checks; this keeps the demand open
            //     cleanly rather than letting a doomed transaction run) ---
            float spendableAp = economyOwner == null
                ? StrategicResourceReservationLedger.SpendableAp(player, ctx.TurnNumber, root.ActionPoints)
                : StrategicResourceReservationLedger.SpendableExcludingOwner(player, ctx.TurnNumber,
                    StrategicReservedResource.ActionPoints, root.ActionPoints, economyOwner);
            if (cand.ApCost > spendableAp + AiConfigV2.allocatorSliceEpsilon
                || !root.CanSpendActionPoints(UnityEngine.Mathf.CeilToInt(cand.ApCost))
                || (cand.ResCost != null && !cand.ResCost.CanAfford(root)))
                return InfraFulfillResult.No($"{demand.Capability}: live AP/resources cannot cover {cand.Explain}");

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

        // A selected infrastructure demand already has a valuable legal site and a snapshot-witnessed
        // builder route. Protect its persistent build vector before Phase B; a Hero prerequisite
        // never reaches this method, so saving cannot block creation of the missing builder.
        internal static bool ShouldReserveDeferredEconomyResources(
            WorldSnapshot snap, AxisDemand demand)
        {
            if (demand?.EconomyBuilderRoutes == null || snap?.Self?.Armies == null)
                return false;
            foreach (EconomyBuilderRouteSnapshot route in demand.EconomyBuilderRoutes)
            {
                ArmySnapshot actor = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.ArmyId == route.ArmyId && a.HasHero && !a.IsPrison && !a.IsAir);
                if (actor != null && (route.IsOnTarget
                    || route.TravelCost <= UnityEngine.Mathf.Max(0, actor.MaxMovement)))
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
            ReserveEconomyCost(player, turn, owner, demand.EconomyBuildResourceCost, 0f);
        }

        // One canonical writer for direct, deferred and provisioned Economy build reservations.
        // Provisioning adds AP only when completion is reachable this turn; Phase A protects only
        // persistent H/E/M/T while a confirmed route is still being delivered.
        internal static void ReserveEconomyCost(PlayerSetupData player, int turn, string owner,
            ResourceCost cost, float buildAp)
        {
            if (player == null || string.IsNullOrEmpty(owner))
                return;
            if (buildAp > 0f)
                StrategicResourceReservationLedger.Upsert(player, turn,
                    new StrategicResourceReservation
                    {
                        Owner = owner, Reason = StrategicReservationReason.EconomyBuildFollowup,
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
                        Owner = owner, Reason = StrategicReservationReason.EconomyBuildFollowup,
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
            int? builderId = snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.HasHero
                && !a.IsPrison && !a.IsAir && a.Hex.Equals(hex))?.ArmyId;
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
            int? builderId = snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.HasHero
                && !a.IsPrison && !a.IsAir && a.Hex.Equals(built))?.ArmyId;
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
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (hand?.Hand == null)
                return null;
            List<(CardData Card, int Ordinal)> cards = hand.Hand
                .Select((card, ordinal) => (Card: card, Ordinal: ordinal))
                .Where(x => x.Card?.Definition != null && x.Card.Definition.cardType == CardType.Facility
                    && x.Card.Definition.grantedAbilities != null
                    && (x.Card.Definition.grantedAbilities.Contains(UnitAbilities.Research)
                        || x.Card.Definition.grantedAbilities.Contains(UnitAbilities.Production)))
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
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand)
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
                    if (abilities == null || !abilities.Contains(role))
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
            return BestDevelopmentCandidate(legal);
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
            foreach (KeyValuePair<HexCoord, ResourceType> kv in snap.Known.ResourceHexes)
                if (kv.Key.Equals(hex.Value))
                    return kv.Value;
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
