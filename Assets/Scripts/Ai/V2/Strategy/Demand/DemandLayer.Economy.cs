using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public static partial class DemandLayer
    {
        // Economy task owners expose one TaskScore assembly point per external task. Everything
        // passed into these methods is a raw world/card/route fact; conversion to score units is
        // centralized here through TaskScoreEvaluator.
        private static TaskScore BuildExtractionScore(WorldSnapshot snap, float usefulGain,
            float resourcePriority, float paybackTurns, int homeDistance, float cardAp,
            float resourceApEquivalent, float extraActivationAp = 0f, float moverOpportunityCost = 0f) =>
            new TaskScore(
                economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(
                    usefulGain, resourcePriority),
                payback: TaskScoreEvaluator.Payback(paybackTurns),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                cardPrice: TaskScoreEvaluator.Price(ActionPrice.Ap(cardAp) + resourceApEquivalent),
                delivery: TaskScoreEvaluator.Price(extraActivationAp),
                moverOpportunityCost: TaskScoreEvaluator.MoverOpportunityCost(moverOpportunityCost),
                citadelThreatRisk: TaskScoreEvaluator.CitadelThreatRisk(snap),
                baseThreatRisk: TaskScoreEvaluator.BaseThreatRisk(snap));

        // A capability demand carries intrinsic slots only: the chain that materializes the
        // collector prices its card and deploy (StrategicCardEvaluator / MaterializationDelivery),
        // and the later mobile-collection task prices the walk with the real collector
        // (BuildMobileCollectionScore). No execution slot is guessed here.
        private static TaskScore BuildCollectorCapabilityScore(float usefulGain,
            float resourcePriority, int homeDistance) =>
            new TaskScore(
                economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(
                    usefulGain, resourcePriority),
                // No facility to lose if the target is abandoned — proximity stays upside-only.
                ownTerritoryProximity: Mathf.Max(0f,
                    TaskScoreEvaluator.OwnTerritoryProximity(homeDistance)));

        internal static TaskScore BuildMobileCollectionScore(WorldSnapshot snapshot,
            MobileCollectionOpportunity op)
        {
            EconomyResourceStanding standing = snapshot.Economy.PerType
                .First(x => x.Type == op.ResourceType);
            float usefulGain = standing.UsefulMarginalIncomeGain(op.EffectiveRemainingYield);
            // EconomyResourceStanding already froze starvation pressure during WorldAnalysis;
            // do not re-read mutable registry state while pricing the same snapshot.
            float priority = TaskScoreEvaluator.ResourcePriority(standing);
            ArmySnapshot collector = snapshot.Self.Armies
                .FirstOrDefault(a => a != null && a.ArmyId == op.CollectorArmyId);
            if (collector == null)
                return default;
            float activationApNow = !collector.HasActivatedThisTurn && op.TravelAp > 0
                ? collector.ActivationApCost : 0f;
            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(
                snapshot, op.TargetHex);
            return new TaskScore(
                economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(
                    usefulGain, priority),
                // Mobile collection has no capital/resource outlay. Arrival time is priced once
                // by Delivery; Payback stays literal zero instead of becoming a fake best-quality
                // 0-turn payback.
                payback: 0f,
                ownTerritoryProximity: Mathf.Max(0f,
                    TaskScoreEvaluator.OwnTerritoryProximity(homeDistance)),
                cardPrice: TaskScoreEvaluator.Price(activationApNow),
                delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                    collector.ActivationApCost, op.TurnsToFirstIncome)),
                moverOpportunityCost: 0f);
        }

        private static TaskScore BuildFoundBaseScore(WorldSnapshot snap,
            IReadOnlyList<(float Gain, float Priority)> marginalByResource,
            bool hasUsefulGain, float paybackTurns, StrategicCardEvaluator.BaseSiteValue facts,
            EconomyBaseOpportunity site, int homeDistance, float cardAp, float resourceApEquivalent,
            float extraActivationAp = 0f, float moverOpportunityCost = 0f) =>
            new TaskScore(
                economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(marginalByResource),
                payback: hasUsefulGain ? TaskScoreEvaluator.Payback(paybackTurns) : 0f,
                airfield: TaskScoreEvaluator.Airfield(facts.Airfield),
                // BaseSiteValue.GlobalEffect is already authored in TaskScore units.
                globalCardEffect: TaskScoreEvaluator.GlobalCardEffectScoreUnits(
                    facts.GlobalEffect),
                frontProgress: TaskScoreEvaluator.FrontProgress(site.ForwardProgressValue),
                corridorAlignment: TaskScoreEvaluator.CorridorAlignment(site.CorridorAlignmentValue),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                terrainDefense: TaskScoreEvaluator.TerrainDefense(site.DefenseBonusValue),
                cardPrice: TaskScoreEvaluator.Price(ActionPrice.Ap(cardAp) + resourceApEquivalent),
                delivery: TaskScoreEvaluator.Price(extraActivationAp),
                moverOpportunityCost: TaskScoreEvaluator.MoverOpportunityCost(moverOpportunityCost),
                citadelThreatRisk: TaskScoreEvaluator.CitadelThreatRisk(snap),
                baseThreatRisk: TaskScoreEvaluator.BaseThreatRisk(snap),
                economicExpansionValue: TaskScoreEvaluator.EconomicExpansionValue(
                    site.NewResourceClusterHexes
                    / AiConfigV2.economyBaseExpansionClusterFullCount));

        internal static IEnumerable<AxisDemand> EconomyDemands(WorldSnapshot s, DesireBreakdown b,
            PlayerSetupData player, AiTurnContext ctx, PlayerRoot root,
            IReadOnlyList<MissionIntent> activeIntents = null, ActorCommitments commitments = null)
        {
            if (s?.Self == null || s.Economy?.PerType == null)
            {
                AiDebugLog.Write("[AI][V2][Economy][Demand] selected=none reason=no_economy_snapshot");
                yield break;
            }

            var standings = s.Economy.PerType.ToDictionary(x => x.Type, x => x);
            var candidates = new List<AxisDemand>();
            int rejectedNoBuilder = 0;
            int rejectedPayback = 0;
            int rejectedSurplus = 0;
            int rejectedStrategicValue = 0;
            int rejectedDeliveryValue = 0;
            int newHeroFallback = 0;
            int newHeroPaired = 0;
            int rejectedSuppressed = 0;
            // The delivery-failure streak Continuity records per (resource, site) — the SAME
            // suppression Base candidates read in AddBaseCandidates. A project nobody could
            // deliver for consecutive turns is not re-selected (and its H/E/M/T is not held)
            // during its cooldown.
            MissionIntentState intentState = player != null
                ? MissionIntentRegistry.GetOrCreate(player) : null;

            foreach (EconomyExtractionOpportunity site in s.Economy.ExtractionOpportunities
                ?? System.Array.Empty<EconomyExtractionOpportunity>())
            {
                if (!standings.TryGetValue(site.ResourceType, out EconomyResourceStanding rs))
                    continue;
                if (intentState != null && intentState.IsExtractionDeliverySuppressed(
                        s.TurnNumber, site.ResourceType, site.Hex))
                {
                    rejectedSuppressed++;
                    continue;
                }
                CardDefinition def = ExtractionDefinition(ctx, site.ResourceType);
                if (ctx?.GameConfig != null && def == null)
                    continue;

                float starvation = ResourceStarvationRegistry.Pressure(player, site.ResourceType);
                float resourcePriority = TaskScoreEvaluator.ResourcePriority(rs, starvation);
                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;

                // Continuity owns the actor for an existing objective. A later, cheaper builder
                // must not supply a different delivery cost for that same durable operation.
                MissionIntent pinnedExtraction = activeIntents?.FirstOrDefault(i =>
                    MissionContinuityLayer.HoldsEconomyBuildSite(i, site.Hex)
                    && i.Economy.Kind == EconomyTaskKind.BuildExtraction
                    && i.Economy.ResourceType == site.ResourceType
                    && i.PreferredMoverArmyId.HasValue);
                // Same deliberate exception as a committed Base (AddBaseCandidates): Continuity
                // already owns this delivery's lifecycle (stall/idle reap), EconomyMissionPlanner
                // keeps walking its builder from the durable intent, and Phase A can only build
                // it from THIS demand. Dropping it on dipped surplus/payback/value economics
                // strands the builder on the site with its resources held and nothing to build.
                bool committed = pinnedExtraction != null;

                // Keep raw income for execution; value and payback use only
                // economically useful marginal income from the frozen snapshot.
                float usefulGain = rs.UsefulMarginalIncomeGain(gain);
                if (usefulGain <= AiConfigV2.allocatorSliceEpsilon && !committed)
                {
                    rejectedSurplus++;
                    continue;
                }

                float resourceCost = StrategicCardEvaluator.ResourceCostSum(def?.resourceCost);
                float resourcePrice = ActionPrice.Resources(def?.resourceCost, s);
                float cardAp = def?.apCost ?? 0f;
                float payback = EconomyPaybackTurns(usefulGain, resourceCost, cardAp);
                if (payback > AiConfigV2.economyExtractionMaxPaybackTurns && !committed)
                {
                    rejectedPayback++;
                    continue;
                }

                // Threat near the site/route is an escort requirement (builder ranking), not a
                // score term. A threatened home defers every build through its own slots.
                int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(s, site.Hex);
                TaskScore siteOnlyScore = BuildExtractionScore(s, usefulGain, resourcePriority,
                    payback, homeDistance, cardAp, resourcePrice);
                float citadelRisk = siteOnlyScore.CitadelThreatRisk;

                EconomyBuilderChoice builder = SelectEconomyBuilder(
                    s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                    siteOnlyScore.Value, cardAp, includeReturn: true,
                    pinnedBuilderArmyId: pinnedExtraction?.PreferredMoverArmyId);
                float travel = builder?.Route.TravelCost
                    ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                float opportunity = EconomyMissionOpportunityCost(builder, activeIntents, site.Hex);
                float assignmentAp = builder?.TotalAssignmentApCost ?? cardAp;
                float extraAp = Mathf.Max(0f, assignmentAp - cardAp);

                // extraAp is already the real re-activation AP for this multi-turn route
                // (EstimateEconomyAssignmentAp: paid outbound/return activations x real
                // ActivationApCost) — travel was a second, redundant raw-distance charge on
                // top of that same real fact.
                TaskScore score = BuildExtractionScore(s, usefulGain, resourcePriority, payback,
                    homeDistance, cardAp, resourcePrice, extraAp, opportunity);
                float value = score.Value;

                if (siteOnlyScore.Value <= AiConfigV2.allocatorSliceEpsilon && !committed)
                {
                    rejectedStrategicValue++;
                    continue;
                }
                // The ready hero makes this site a loss. Unless Continuity already owns that hero
                // for it, offer the site builder-less instead: Materialization may still deliver it
                // with a NEW hero, but only when that is cheaper than the ready one and the new
                // hero's own delivery stays under the site's value.
                bool readyLossToNewHero = value <= AiConfigV2.allocatorSliceEpsilon
                    && builder != null && !committed;
                if (value <= AiConfigV2.allocatorSliceEpsilon && !readyLossToNewHero && !committed)
                {
                    if (builder == null) rejectedNoBuilder++;
                    else rejectedDeliveryValue++;
                    continue;
                }
                if (readyLossToNewHero)
                    newHeroFallback++;

                candidates.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = CapabilityKind.EconomicInfrastructure,
                    DesiredAmount = 1f,
                    TargetHex = site.Hex,
                    EconomyResourceType = site.ResourceType,
                    EconomyBuildResourceCost = def?.resourceCost,
                    EconomyBuildApCost = def?.apCost ?? 0,
                    MinimumFollowupAp = def?.apCost ?? 0,
                    EconomyExpectedIncomeGain = gain,
                    EconomySiteValue = siteOnlyScore.Value,
                    EconomyTravelCost = travel,
                    EconomyHeroOpportunityCost = readyLossToNewHero ? 0f : opportunity,
                    EconomyAssignmentApCost = readyLossToNewHero ? cardAp : assignmentAp,
                    EconomyPaybackTurns = payback,
                    EconomyPreferredBuilderArmyId = readyLossToNewHero
                        ? null : builder?.Army.ArmyId,
                    EconomyReadyDeliveryCost = readyLossToNewHero
                        ? ReadyDeliveryCost(extraAp, opportunity) : (float?)null,
                    EconomyBuilderRoutes = site.BuilderRoutes,
                    WorldTaskScore = readyLossToNewHero ? siteOnlyScore : score,
                    Value = readyLossToNewHero ? siteOnlyScore.Value : score.Value,
                    Explain = $"{site.ResourceType} task={score.Value:0.##} priority={resourcePriority:0.##} "
                        + $"marginalGain={gain:0.##} usefulGain={usefulGain:0.##} payback={payback:0.##} "
                        + $"travel={travel:0.##} citadelRisk={citadelRisk:0.##} "
                        + $"moverOpp={opportunity:0.##}"
                        + (readyLossToNewHero
                            ? $"; ready#{builder.Army.ArmyId} loses -> new_hero_only" : ""),
                });
            }

            foreach (AxisDemand collectorDemand in CollectorCapabilityDemands(
                s, standings, player, activeIntents))
                yield return collectorDemand;

            foreach (AxisDemand sourceDemand in GlobalResourceSourceDemands(s))
                yield return sourceDemand;

            string baseSummary = AddBaseCandidates(
                s, candidates, player, ctx, activeIntents, commitments,
                out int baseNoBuilder, out int baseStrategicValue,
                out int baseDeliveryValue, out int baseThreshold, out int baseNewHeroFallback);

            IOrderedEnumerable<AxisDemand> extractionRanked = candidates
                .Where(x => x.Capability == CapabilityKind.EconomicInfrastructure
                    && x.EconomyResourceType.HasValue
                    && !HasActiveEconomyIntentAtHexOfKind(
                        activeIntents, x.TargetHex, EconomyTaskKind.FoundBase))
                .OrderByDescending(x => HasActiveEconomyBuildIntent(activeIntents, x) ? 1 : 0)
                .ThenByDescending(x => x.Value)
                .ThenByDescending(x => x.EconomyExpectedIncomeGain)
                .ThenBy(x => x.EconomyTravelCost)
                .ThenBy(x => x.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(x => x.TargetHex?.R ?? int.MaxValue);

            IOrderedEnumerable<AxisDemand> baseRanked = candidates
                .Where(x => x.Capability == CapabilityKind.EconomicExpansionBase
                    && !HasActiveEconomyIntentAtHexOfKind(
                        activeIntents, x.TargetHex, EconomyTaskKind.BuildExtraction))
                .OrderByDescending(x => IsActiveBaseCommitment(
                    activeIntents, x.TargetHex, x.EconomyBuildCard))
                .ThenByDescending(x => x.Value)
                .ThenByDescending(x => x.EconomySiteValue)
                .ThenByDescending(x => x.EconomyExpectedIncomeGain)
                .ThenBy(x => x.EconomyTravelCost)
                .ThenBy(x => x.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(x => x.TargetHex?.R ?? int.MaxValue);

            // economyMaxExpansionBaseDemandsPerTurn is a plain on/off switch for Base origination,
            // not a count — SelectBaseDemandsForCurrentCommitment already returns at most one demand
            // per DISTINCT (card, builder) pair, each a genuinely independent project; the hex/actor/
            // card dedup right below still allows only one of them to actually win a shared resource.
            List<AxisDemand> selectedBases = AiConfigV2.economyMaxExpansionBaseDemandsPerTurn > 0
                ? SelectBaseDemandsForCurrentCommitment(baseRanked.ToList(), activeIntents)
                : new List<AxisDemand>();
            // No local count cap: MissionAdmissionPolicy.Capacity(ExecutionLane.Economy) is already
            // int.MaxValue downstream, and PhaseAApBudget/MaterializationReservation/ResourceAllocator
            // are the real, single owners of how many of these candidates can actually execute this
            // turn. A `.Take(N)` here duplicated that arbitration one layer too early and silently
            // discarded candidates the real allocator would have happily funded (see rejected=1/2 on
            // "selected=EconomicInfrastructure" in AiDebug.log even with no Base in hand yet).
            List<AxisDemand> selected = extractionRanked.ToList()
                .Concat(selectedBases)
                .ToList();

            var selectedHexes = new HashSet<HexCoord>();
            var selectedBuilders = new HashSet<int>();
            var selectedCards = new HashSet<CardData>();
            selected = selected
                .OrderByDescending(d => HasActiveEconomyBuildIntent(activeIntents, d))
                .ThenByDescending(d => d.Value)
                .Where(d =>
                {
                    if ((d.TargetHex.HasValue && selectedHexes.Contains(d.TargetHex.Value))
                        || (d.EconomyPreferredBuilderArmyId.HasValue
                            && selectedBuilders.Contains(d.EconomyPreferredBuilderArmyId.Value))
                        || (d.EconomyBuildCard != null && selectedCards.Contains(d.EconomyBuildCard)))
                        return false;
                    if (d.TargetHex.HasValue) selectedHexes.Add(d.TargetHex.Value);
                    if (d.EconomyPreferredBuilderArmyId.HasValue)
                        selectedBuilders.Add(d.EconomyPreferredBuilderArmyId.Value);
                    if (d.EconomyBuildCard != null) selectedCards.Add(d.EconomyBuildCard);
                    return true;
                }).ToList();

            foreach (AxisDemand demand in selected)
            {
                if (!demand.EconomyPreferredBuilderArmyId.HasValue
                    && HasActiveEconomyBuildIntent(activeIntents, demand))
                {
                    AiDebugLog.WriteDeduped($"continuity|{demand.TargetHex}",
                        $"[AI][V2][Economy][Demand] selected=continuity "
                        + $"target=({demand.TargetHex?.Q},{demand.TargetHex?.R}) "
                        + "reason=existing_target_specific_actor");
                    continue;
                }
                AxisDemand emitted = demand.EconomyPreferredBuilderArmyId.HasValue
                    ? demand
                    : EconomyHeroPrerequisite(demand);
                AiDebugLog.WriteDeduped($"{emitted.Capability}|{emitted.TargetHex}",
                    $"[AI][V2][Economy][Demand] selected={emitted.Capability} "
                    + $"resource={emitted.EconomyResourceType?.ToString() ?? "none"} "
                    + $"target=({emitted.TargetHex?.Q},{emitted.TargetHex?.R}) value={emitted.Value:0.##} "
                    + $"rejected={Mathf.Max(0, candidates.Count - selected.Count)}");
                yield return emitted;

                AxisDemand alternative = PairedNewHeroAlternative(demand, activeIntents);
                if (alternative != null)
                {
                    newHeroPaired++;
                    AiDebugLog.WriteDeduped($"new-hero-alt|{alternative.TargetHex}",
                        $"[AI][V2][Economy][Demand] selected=Hero alternative=new_hero "
                        + $"target=({alternative.TargetHex?.Q},{alternative.TargetHex?.R}) "
                        + $"ready#{demand.EconomyPreferredBuilderArmyId} "
                        + $"readyDelivery={alternative.EconomyReadyDeliveryCost:0.##}");
                    yield return alternative;
                }
            }

            AiDebugLog.WriteDeduped("base-summary", $"[AI][V2][Economy][BaseCandidates] {baseSummary}");
            int rejectionTotal = rejectedNoBuilder + baseNoBuilder + rejectedPayback + rejectedSurplus
                + rejectedSuppressed
                + rejectedStrategicValue + baseStrategicValue
                + rejectedDeliveryValue + baseDeliveryValue + baseThreshold;
            AiDebugLog.WriteDeduped("rejections", $"[AI][V2][Economy][Rejections] no_builder={rejectedNoBuilder + baseNoBuilder} "
                + $"payback={rejectedPayback} surplus={rejectedSurplus} strategic_value={rejectedStrategicValue + baseStrategicValue} "
                + $"delivery_value={rejectedDeliveryValue + baseDeliveryValue} "
                + $"threshold={baseThreshold} suppressed={rejectedSuppressed} "
                + $"new_hero_fallback={newHeroFallback + baseNewHeroFallback} "
                + $"new_hero_paired={newHeroPaired}");
            if (selected.Count == 0)
                AiDebugLog.WriteDeduped("selected-none",
                    $"[AI][V2][Economy][Demand] selected=none rejected={rejectionTotal} "
                    + "reason=no_legal_valuable_site_or_base");
        }

        // Economy creates the budget of opportunities: a card in hand whose effective abilities
        // carry a PlayerGlobal recurring-resource effect (ApBonus / Produce*) is a standing Economy
        // obligation to put it into play, admitted in Phase A before operational missions spend
        // the turn — not a Phase-B leftover. One demand per carrier card, pinned by
        // EconomySourceCard: a Facility is placed by InfrastructureFulfillment, a Unit/Hero by the
        // materialization chain (any placement — the effect works in any own non-Prison army).
        // Base cards stay with the FoundBase pipeline (it already prices the same effect through
        // GlobalCardEffect). The TaskScore carries the intrinsic slot only — the chain / facility
        // play prices the card itself (StrategicCardEvaluator, ResourceGainRoleFit), exactly like
        // a CollectorCapability demand.
        internal static IEnumerable<AxisDemand> GlobalResourceSourceDemands(WorldSnapshot s)
        {
            foreach (CardData card in s?.Self?.Hand ?? System.Array.Empty<CardData>())
            {
                CardDefinition def = card?.Definition;
                if (def == null || def.isAviation)
                    continue;
                CapabilityKind capability;
                if (def.cardType == CardType.Facility)
                    capability = CapabilityKind.GlobalResourceFacility;
                else if (def.cardType == CardType.Unit || def.cardType == CardType.Hero)
                    capability = CapabilityKind.GlobalResourceCarrier;
                else
                    continue;
                IReadOnlyList<string> abilities =
                    MaterializationChainMatching.EffectiveAbilities(def, card.Equipment);
                if (!StrategicEffectRegistry.HasGlobalRecurringEffect(abilities))
                    continue;

                float global = TaskScoreEvaluator.GlobalCardEffectScoreUnits(
                    StrategicCardEvaluator.GlobalEffectValue(s, def));
                var score = new TaskScore(globalCardEffect: global);
                AiDebugLog.WriteDeduped($"global-source|{def.authoredKey}|{capability}",
                    $"[AI][V2][Economy][GlobalSource] decision=CREATE card={def.displayName} "
                    + $"capability={capability} effects=[{string.Join(",", abilities)}] "
                    + $"globalEffect={global:0.##}");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = capability,
                    DesiredAmount = 1f,
                    EconomySourceCard = card,
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"global source {def.displayName} ({def.cardType}) globalEffect={global:0.##}",
                };
            }
        }

        // A mobile collector card, materialized SOLO (see MaterializationChainEnumerator's
        // CollectorCapability soloOnly rule) straight onto a known, currently-uncovered resource
        // hex. Deliberately separate from the ExtractionOpportunity/facility loop above: this is
        // never a facility (no EconomicInfrastructure/EconomicExpansionBase card, no site-slot
        // rule), it is a cheap disposable field unit — same "own army, own decision" shape as a
        // Scout card, just for a resource hex instead of the fog. A site already covered by an
        // existing free army (s.Economy.MobileCollectionOpportunities) never needs a card spent on
        // it — that opportunistic path stays strictly cheaper and takes priority by construction.
        internal static IEnumerable<AxisDemand> CollectorCapabilityDemands(WorldSnapshot s,
            Dictionary<ResourceType, EconomyResourceStanding> standings, PlayerSetupData player,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            // Demand answers ONLY "is an additional collector on this site worth having". It does
            // not require a ready collector card in hand and does not pick the physical card:
            // MaterializationChainEnumerator owns Direct / AttachDeploy / GenerateDeploy /
            // GenerateAttachDeploy for exactly this capability. Pre-selecting one hand card here
            // would hide the generated and equipment-borne sources and price the demand off an
            // arbitrary card. Cost is charged once, downstream, by
            // StrategicCardEvaluator.ResourceCost over the concrete chain.
            List<CardData> collectorCards = (s.Self?.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition != null && !c.Definition.isAviation
                    && (c.Definition.cardType == CardType.Unit || c.Definition.cardType == CardType.Hero))
                .ToList();
            if (s.Economy?.CollectorSites == null)
                yield break;

            // Analysis now publishes raw physical mobile-collector opportunities. Preserve the
            // old semantic contract: an existing actor suppresses CollectorCapability only when
            // at least one such actor has a positive canonical MobileCollection TaskScore.
            var existingMobileCoverage = new HashSet<(HexCoord, ResourceType)>(
                (s.Economy.MobileCollectionOpportunities ?? System.Array.Empty<MobileCollectionOpportunity>())
                    .Where(o => BuildMobileCollectionScore(s, o).Value
                        > AiConfigV2.allocatorSliceEpsilon)
                    .Select(o => (o.TargetHex, o.ResourceType)));

            var candidates = new List<AxisDemand>();
            foreach (EconomyExtractionOpportunity site in s.Economy.CollectorSites)
            {
                if (existingMobileCoverage.Contains((site.Hex, site.ResourceType))
                    || !standings.TryGetValue(site.ResourceType, out EconomyResourceStanding rs)
                    || HasActiveEconomyIntentAtHexOfKind(activeIntents, site.Hex, EconomyTaskKind.MobileCollection))
                    continue;

                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                float usefulGain = gain <= AiConfigV2.allocatorSliceEpsilon
                    ? 0f : rs.UsefulMarginalIncomeGain(gain);
                if (usefulGain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;

                string requiredAbility = UnitAbilities.CollectAbilityFor(site.ResourceType);
                // A ready standalone card, when one exists, is still recorded on the demand so
                // Phase-B tempo cannot burn the very card this demand is waiting to materialize
                // (MaterializationReservation.ClaimsEconomyBuildCard). It is a CLAIM, not a choice
                // and not a price: the enumerator still compares every legal chain, including the
                // equipment-borne and generated ones this lookup cannot see.
                CardData claimedCard = collectorCards
                    .Where(c => MaterializationChainMatching
                        .EffectiveAbilities(c.Definition, c.Equipment).Contains(requiredAbility))
                    .OrderBy(c => c.Definition.apCost)
                    .ThenBy(c => c.Definition.authoredKey ?? c.Definition.displayName)
                    .FirstOrDefault();

                float starvation = ResourceStarvationRegistry.Pressure(player, site.ResourceType);
                float priority = TaskScoreEvaluator.ResourcePriority(rs, starvation);
                int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(s, site.Hex);
                // Chain-independent ETA baseline (diagnostics only — the walk is priced by the
                // mobile-collection task once a real collector exists).
                int etaTurns = Mathf.Max(1, Mathf.CeilToInt(
                    homeDistance / (float)Mathf.Max(1, AiConfigV2.etaFallbackMoveBudget)));

                // Site economics only. CardPrice/Payback are deliberately NOT folded in here any
                // more: StrategicCardEvaluator.ResourceCost is "the ONLY place a chain is charged
                // for cost" and already prices AP, resources, the extra chain step and the
                // generation success discount for whichever chain actually delivers this
                // capability. Pricing a guessed card here as well was a straight double count, and
                // it is what made a generated or equipment-borne collector uncomparable.
                TaskScore score = BuildCollectorCapabilityScore(
                    usefulGain, priority, homeDistance);
                if (score.Value <= AiConfigV2.allocatorSliceEpsilon)
                    continue;

                candidates.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = CapabilityKind.CollectorCapability,
                    DesiredAmount = 1f,
                    TargetHex = site.Hex,
                    EconomyResourceType = site.ResourceType,
                    EconomyBuildCard = claimedCard,
                    EconomyExpectedIncomeGain = gain,
                    EconomySiteValue = score.Value,
                    EconomyTravelCost = homeDistance,
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"Collector {site.ResourceType} task={score.Value:0.##} priority={priority:0.##} "
                        + $"gain={gain:0.##} usefulGain={usefulGain:0.##} "
                        + $"homeDist={homeDistance} eta={etaTurns} "
                        + $"handCard={(claimedCard != null ? claimedCard.Definition.displayName : "none")}",
                });
            }

            // No local count cap — see the matching comment on extractionRanked in EconomyDemands.
            // The physical EconomyBuildCard each candidate names is the real, single-spend
            // constraint, already enforced downstream by MaterializationReservation's
            // ClaimedEconomyBuildCards, not a count taken before the allocator ever sees the rest.
            foreach (AxisDemand demand in candidates.OrderByDescending(d => d.Value))
            {
                AiDebugLog.WriteDeduped($"collector|{demand.EconomyResourceType}|{demand.TargetHex}",
                    $"[AI][V2][Economy][Demand] selected=CollectorCapability "
                    + $"resource={demand.EconomyResourceType} "
                    + $"target=({demand.TargetHex?.Q},{demand.TargetHex?.R}) value={demand.Value:0.##}");
                yield return demand;
            }
        }

        internal static AxisDemand EconomyHeroPrerequisite(AxisDemand source) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.Hero,
            DesiredAmount = 1f,
            TargetHex = source.TargetHex,
            EconomyResourceType = source.EconomyResourceType,
            EconomyBuildCard = source.EconomyBuildCard,
            EconomyBuildResourceCost = source.EconomyBuildResourceCost,
            EconomyBuildApCost = source.EconomyBuildApCost,
            MinimumFollowupAp = source.MinimumFollowupAp,
            EconomyExpectedIncomeGain = source.EconomyExpectedIncomeGain,
            EconomySiteValue = source.EconomySiteValue,
            EconomyTravelCost = source.EconomyTravelCost,
            EconomyHeroOpportunityCost = source.EconomyHeroOpportunityCost,
            EconomyAssignmentApCost = source.EconomyAssignmentApCost,
            EconomyPaybackTurns = source.EconomyPaybackTurns,
            EconomyPreferredBuilderArmyId = source.EconomyPreferredBuilderArmyId,
            EconomyReadyDeliveryCost = source.EconomyReadyDeliveryCost,
            EconomyBuilderRoutes = source.EconomyBuilderRoutes,
            WorldTaskScore = source.WorldTaskScore,
            Value = source.Value,
            Explain = source.Explain + "; prerequisite=mobile_hero",
        };

        // What delivering a build with its READY hero costs in TaskScore units: the same
        // delivery (extra activation AP) and mover-opportunity slots the ready demand is scored
        // with. The ready path also pays the build card, as would a new hero, so it cancels out.
        private static float ReadyDeliveryCost(float extraAp, float moverOpportunityCost) =>
            TaskScoreEvaluator.Price(extraAp)
            + TaskScoreEvaluator.MoverOpportunityCost(moverOpportunityCost);

        // "Ready hero vs new hero" for a build a ready hero can serve at positive value. Emitted
        // next to the ready demand as a Hero prerequisite that carries the ready cost;
        // Materialization (MaterializationDeliveryPolicy) admits a new-hero chain only when its
        // card price plus its own delivery is lower. Demand never picks the card. No alternative
        // when the ready hero is already on target (nothing to save) or Continuity already runs
        // this build.
        private static AxisDemand PairedNewHeroAlternative(AxisDemand ready,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            if (ready == null || !ready.EconomyPreferredBuilderArmyId.HasValue
                || !ready.TargetHex.HasValue
                || HasActiveEconomyBuildIntent(activeIntents, ready)
                || (ready.Capability == CapabilityKind.EconomicExpansionBase
                    && IsActiveBaseCommitment(activeIntents, ready.TargetHex,
                        ready.EconomyBuildCard)))
                return null;
            float readyCost = ReadyDeliveryCost(
                ready.EconomyAssignmentApCost - ready.EconomyBuildApCost,
                ready.EconomyHeroOpportunityCost);
            if (readyCost <= AiConfigV2.allocatorSliceEpsilon)
                return null;
            AxisDemand alternative = EconomyHeroPrerequisite(ready);
            alternative.EconomyPreferredBuilderArmyId = null;
            alternative.EconomyAssignmentApCost = ready.EconomyBuildApCost;
            alternative.EconomyHeroOpportunityCost = 0f;
            alternative.EconomyReadyDeliveryCost = readyCost;
            alternative.Value = ready.EconomySiteValue;
            alternative.Explain +=
                $"; alternative=new_hero ready#{ready.EconomyPreferredBuilderArmyId}";
            return alternative;
        }

        internal sealed class EconomyBuilderChoice
        {
            public EconomyBuilderRouteSnapshot Route;
            public ArmySnapshot Army;
            public float TotalAssignmentApCost;
            public EconomyArmySuitability Suitability;
            public string IneligibleReason;
            public int MinimumEscortCount;
            public int ProjectedActivationApCost;
            public int ProjectedMaxMovement;
            public float PreparationApCost;
            public ArmySnapshot PreparationGarrison;
            public IReadOnlyList<int> RetainedIndices = System.Array.Empty<int>();
            public IReadOnlyList<int> AddedIndices = System.Array.Empty<int>();

            internal EconomyBuilderChoice DetachedDecision()
            {
                var copy = (EconomyBuilderChoice)MemberwiseClone();
                copy.RetainedIndices = System.Array.AsReadOnly(RetainedIndices.ToArray());
                copy.AddedIndices = System.Array.AsReadOnly(AddedIndices.ToArray());
                var route = Route;
                if (route.PathHexes != null)
                    route.PathHexes = System.Array.AsReadOnly(route.PathHexes.ToArray());
                if (route.RouteThreats != null)
                    route.RouteThreats = System.Array.AsReadOnly(route.RouteThreats.ToArray());
                copy.Route = route;
                return copy;
            }
        }

        internal enum EconomyArmySuitability
        {
            Ready,
            LightenAtBase,
            ReinforceAtBase,
            Ineligible,
        }

        // Which build an Economy demand is about: a Base (its own capability, or a builder-Hero
        // prerequisite carrying the Base card) or an extraction facility. The one owner for
        // Demand, Missions, Phase A, Continuity and the build reservation owner keys.
        internal static EconomyTaskKind EconomyBuildKind(AxisDemand demand) =>
            demand?.Capability == CapabilityKind.EconomicExpansionBase
            || demand?.EconomyBuildCard?.Definition?.cardType == CardType.Base
                ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;

        internal static EconomyBuilderChoice SelectEconomyBuilder(WorldSnapshot snap,
            HexCoord target, IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            float buildValue, float buildApCost, bool includeReturn,
            int? pinnedBuilderArmyId = null)
        {
            return RankEconomyBuilders(snap, target, routes, activeIntents, commitments,
                buildValue, buildApCost, includeReturn)
                .FirstOrDefault(x => !pinnedBuilderArmyId.HasValue
                    || x.Army?.ArmyId == pinnedBuilderArmyId.Value);
        }

        internal static IReadOnlyList<EconomyBuilderChoice> RankEconomyBuilders(WorldSnapshot snap,
            HexCoord target, IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            float buildValue, float buildApCost, bool includeReturn)
        {
            // Assess the EXACT candidate first: loan admission and final TaskScore must
            // price the same projected roster, outbound trip and return activations.
            // The old raw-hex penalty introduced a second incompatible delivery scorer.
            return EconomyBuilderCandidates(snap, target, routes, activeIntents, commitments)
                .Select(x => AssessEconomyArmy(snap, target, x.route, x.army,
                    buildApCost, includeReturn))
                .Where(x => x.Suitability != EconomyArmySuitability.Ineligible)
                .Where(x => x.Route.IsOnTarget
                    || ActiveAssignment(activeIntents, x.Army.ArmyId) == null
                    || ActiveAssignment(activeIntents, x.Army.ArmyId).Kind == MissionKind.Economy
                    || EconomyLoanAllowed(ActiveAssignment(activeIntents, x.Army.ArmyId), buildValue,
                        x, buildApCost, out _))
                // Economy's own builders come first only while taking them is free (this
                // site's own build, or a builder already walking home); a builder busy on
                // another site is priced like any other donor below.
                .OrderByDescending(x => (x.Route.HasActiveEconomyCommitment
                        || ActiveAssignment(activeIntents, x.Route.ArmyId)?.Kind == MissionKind.Economy)
                    && EconomyMissionOpportunityCost(x, activeIntents, target)
                        <= AiConfigV2.allocatorSliceEpsilon)
                .ThenBy(x => x.Suitability == EconomyArmySuitability.Ready ? 0
                    : x.Suitability == EconomyArmySuitability.LightenAtBase ? 1 : 2)
                // Among equally suitable builders use canonical delivery plus the ONE
                // donor interruption loss. Otherwise a cheaper AP loan can still lose
                // intrinsic value to a slightly dearer uncommitted actor.
                .ThenBy(x => TaskScoreEvaluator.Price(
                        Mathf.Max(0f, x.TotalAssignmentApCost - buildApCost))
                    + EconomyMissionOpportunityCost(x, activeIntents, target))
                .ThenBy(x => x.TotalAssignmentApCost
                    + (!x.Route.IsOnTarget && x.Army?.HeroIsHomeVocation == true
                        ? AiConfigV2.economyHomeHeroAssignmentApPenalty : 0f))
                .ThenBy(x => x.Route.EffectiveArmyPower)
                .ThenBy(x => x.Route.ArmySize)
                .ThenBy(x => x.Route.TravelCost + (includeReturn ? x.Route.ReturnTravelCost : 0))
                .ThenBy(x => x.Route.ArmyId)
                .ToList();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            Dictionary<(HexCoord, EconomyBuilderRouteSnapshot, ArmySnapshot, float, bool), EconomyBuilderChoice>>
            EconomyAssessmentCache = new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
                Dictionary<(HexCoord, EconomyBuilderRouteSnapshot, ArmySnapshot, float, bool), EconomyBuilderChoice>>();

        internal static EconomyBuilderChoice AssessEconomyArmy(WorldSnapshot snap,
            HexCoord target, EconomyBuilderRouteSnapshot route, ArmySnapshot army,
            float buildApCost, bool includeReturn)
        {
            if (snap == null)
                return Compute();
            var cache = EconomyAssessmentCache.GetOrCreateValue(snap);
            var key = (target, route, army, buildApCost, includeReturn);
            if (!cache.TryGetValue(key, out EconomyBuilderChoice assessed))
                cache[key] = assessed = Compute();
            // The cached decision belongs to this snapshot. A consumer may reprice its local
            // copy, but must never overwrite the result observed by the next reader.
            return assessed.DetachedDecision();

            EconomyBuilderChoice Compute()
            {
                var choice = new EconomyBuilderChoice
                {
                    Route = route,
                    Army = army,
                    TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                        route, buildApCost, includeReturn),
                    Suitability = EconomyArmySuitability.Ineligible,
                    IneligibleReason = "insufficient_safe_escort",
                };
                if (army == null)
                    return choice;

                if (route.RequiresGarrisonExtraction)
                {
                    // Analysis publishes structural extraction feasibility through the existing
                    // Shell/Host/Create resolver. Demand consumes that immutable witness.
                    if (!route.ExtractionContainerAvailable)
                    {
                        choice.IneligibleReason = "no_garrison_extraction_container";
                        return choice;
                    }
                    choice.Suitability = EconomyArmySuitability.Ready;
                    choice.IneligibleReason = null;
                    choice.MinimumEscortCount = 0;
                    // Extraction is a one-time preparation stage, never a recurring activation.
                    choice.PreparationApCost = route.ExtractionApCost;
                    EconomyBuilderRouteSnapshot projectedRoute = route;
                    choice.Route = projectedRoute;
                    choice.TotalAssignmentApCost = choice.PreparationApCost + EstimateEconomyAssignmentAp(
                        projectedRoute, buildApCost, includeReturn);
                    choice.ProjectedActivationApCost = projectedRoute.ActivationApCost;
                    choice.ProjectedMaxMovement = route.MaxMovement;
                    AiDebugLog.WriteDeduped($"{target}|{army.ArmyId}",
                        $"[ECO][Builder] site=({target.Q},{target.R}) "
                        + $"actor=#{army.ArmyId} source=Garrison route=VALID "
                        + $"extraction=snapshot requiredAp={route.ExtractionApCost:0.##} "
                        + "decision=READY");
                    return choice;
                }

                List<AiMapMemory.KnownEnemySighting> threats = (route.RouteThreats
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Where(t => t.Owner?.IsNeutral != true)
                    .ToList();
                bool atBase = snap?.Self?.BaseHexes?.Contains(army.Hex) == true;
                ArmySnapshot localGarrison = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.IsGarrison && a.Hex.Equals(army.Hex) && a != army);
                bool mayPrepare = atBase && localGarrison != null && !localGarrison.HasActivatedThisTurn
                    && !army.EconomyRosterProtected;
                bool safeRear = threats.Count == 0;
                int minimumEscort = safeRear ? 0 : 1;
                choice.MinimumEscortCount = minimumEscort;

                List<int> currentIndices = Enumerable.Range(0, army.Members?.Count ?? 0)
                    .Where(i => i >= (army.NonHeroIsAviation?.Count ?? 0)
                        || !army.NonHeroIsAviation[i]).ToList();
                List<WorthIt.DefenderProfile> current = currentIndices
                    .Select(i => army.Members[i]).ToList();
                if (EconomyRosterSafe(current, army.Commander, threats, minimumEscort))
                {
                    List<int> retained = mayPrepare
                        ? MinimumSafeEconomyEscortIndices(army, threats, minimumEscort,
                            route.MaximumStepCost, localGarrison.Capacity > 0
                                ? Mathf.Max(0, localGarrison.Capacity - localGarrison.OccupiedBattleSlots)
                                : int.MaxValue, currentIndices.Count)
                        : currentIndices;
                    if (retained == null) return choice;
                    choice.RetainedIndices = retained;
                    choice.PreparationGarrison = mayPrepare ? localGarrison : null;
                    int smallest = retained?.Count ?? current.Count;
                    choice.MinimumEscortCount = smallest;
                    int knownBodyAp = army.NonHeroActivationApCosts?.Sum() ?? 0;
                    int heroAp = army.HeroActivationApCost > 0
                        ? army.HeroActivationApCost
                        : Mathf.Max(0, route.ActivationApCost - knownBodyAp);
                    int heroMove = army.HeroMoveMax > 0
                        ? army.HeroMoveMax : route.MaxMovement;
                    choice.ProjectedActivationApCost = heroAp
                        + retained.Sum(i => i < army.NonHeroActivationApCosts.Count
                            ? army.NonHeroActivationApCosts[i] : 0);
                    choice.ProjectedMaxMovement = retained.Count == 0
                        ? heroMove
                        : Mathf.Min(heroMove,
                            retained.Min(i => i < army.NonHeroMoveMax.Count
                                ? army.NonHeroMoveMax[i] : army.MaxMovement));
                    EconomyBuilderRouteSnapshot projectedRoute = route;
                    projectedRoute.ActivationApCost = choice.ProjectedActivationApCost;
                    projectedRoute.MaxMovement = Mathf.Max(1, choice.ProjectedMaxMovement);
                    if (army.NonHeroCurrentMovement.Count == army.Members.Count && army.NonHeroCurrentMovement.Count > 0)
                        projectedRoute.CurrentMovement = retained.Count == 0 ? army.HeroCurrentMovement
                            : Mathf.Min(army.HeroCurrentMovement, retained.Min(i => army.NonHeroCurrentMovement[i]));
                    choice.Route = projectedRoute;
                    choice.TotalAssignmentApCost = choice.PreparationApCost + EstimateEconomyAssignmentAp(
                        projectedRoute, buildApCost, includeReturn);
                    choice.Suitability = mayPrepare && current.Count > smallest
                        ? EconomyArmySuitability.LightenAtBase
                        : EconomyArmySuitability.Ready;
                    return choice;
                }

                if (!mayPrepare || snap?.Self?.Armies == null)
                {
                    if (localGarrison?.HasActivatedThisTurn == true)
                        choice.IneligibleReason = "escort_activated_this_turn";
                    return choice;
                }
                ArmySnapshot garrison = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.IsGarrison && a.Hex.Equals(army.Hex));
                if (garrison == null || garrison == army)
                    return choice;
                if (garrison.HasActivatedThisTurn)
                {
                    choice.IneligibleReason = "escort_activated_this_turn";
                    return choice;
                }
                List<WorthIt.DefenderProfile> reserve =
                    garrison?.Members?.ToList() ?? new List<WorthIt.DefenderProfile>();
                List<int> reserveIndices = Enumerable.Range(0, reserve.Count)
                    .Where(i => i >= (garrison.NonHeroIsAviation?.Count ?? 0)
                        || !garrison.NonHeroIsAviation[i]).ToList();
                for (int add = 1; add <= reserve.Count; add++)
                {
                    List<int> best = null;
                    int bestAp = int.MaxValue;
                    int bestMove = int.MinValue;
                    foreach (List<int> subset in Combinations(reserveIndices, add))
                    {
                        var projected = new List<WorthIt.DefenderProfile>(current);
                        projected.AddRange(subset.Select(i => reserve[i]));
                        if ((army.Capacity > 0 && army.MemberCount + subset.Count > army.Capacity)
                            || !EconomyRosterSafe(projected, army.Commander, threats, minimumEscort))
                            continue;
                        int addedAp = subset.Sum(i => i < garrison.NonHeroActivationApCosts.Count
                            ? garrison.NonHeroActivationApCosts[i] : 0);
                        int addedMove = subset.Min(i => i < garrison.NonHeroMoveMax.Count
                            ? garrison.NonHeroMoveMax[i] : garrison.MaxMovement);
                        if (Mathf.Min(route.MaxMovement, addedMove) < route.MaximumStepCost) continue;
                        if (best == null || addedAp < bestAp
                            || (addedAp == bestAp && addedMove > bestMove))
                        {
                            best = subset;
                            bestAp = addedAp;
                            bestMove = addedMove;
                        }
                    }
                    if (best != null)
                    {
                        choice.RetainedIndices = currentIndices;
                        choice.AddedIndices = best;
                        choice.PreparationGarrison = garrison;
                        choice.PreparationApCost = army.HasActivatedThisTurn
                            ? best.Where(i => i >= garrison.NonHeroRuntimeIds.Count
                                || !army.ActivationCoveredUnitRuntimeIds.Contains(garrison.NonHeroRuntimeIds[i]))
                                .Sum(i => i < garrison.NonHeroActivationApCosts.Count
                                    ? garrison.NonHeroActivationApCosts[i] : 0) : 0f;
                        choice.MinimumEscortCount = current.Count + best.Count;
                        choice.Suitability = EconomyArmySuitability.ReinforceAtBase;
                        choice.ProjectedActivationApCost = route.ActivationApCost + bestAp;
                        choice.ProjectedMaxMovement = Mathf.Min(route.MaxMovement,
                            bestMove > 0 ? bestMove : route.MaxMovement);
                        EconomyBuilderRouteSnapshot projectedRoute = route;
                        projectedRoute.ActivationApCost = choice.ProjectedActivationApCost;
                        projectedRoute.MaxMovement = Mathf.Max(1, choice.ProjectedMaxMovement);
                        if (garrison.NonHeroCurrentMovement.Count == reserve.Count)
                            projectedRoute.CurrentMovement = Mathf.Min(route.CurrentMovement,
                                best.Min(i => garrison.NonHeroCurrentMovement[i]));
                        choice.Route = projectedRoute;
                        choice.TotalAssignmentApCost = choice.PreparationApCost + EstimateEconomyAssignmentAp(
                            projectedRoute, buildApCost, includeReturn);
                        return choice;
                    }
                }
                return choice;
            }
        }

        // `commander` — the escorted formation's commander (the builder hero), who leads the
        // escort in any fight on the way.
        internal static bool EconomyRosterSafe(
            IReadOnlyList<WorthIt.DefenderProfile> roster, WorthIt.SideCommander commander,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            if ((roster?.Count ?? 0) < minimumEscort)
                return false;
            if (threats == null || threats.Count == 0)
                return true;
            if (threats.Any(t => t.Defenders == null || t.Defenders.Count == 0))
                return false;
            return threats.All(t => WorthIt.CanDamageAll(roster, t.Defenders)
                && WorthIt.WinChance(roster, t.Defenders, 0f, commander, t.Commander)
                    >= AiConfig.economyEscortMinWinChance);
        }

        internal static List<int> MinimumSafeEconomyEscortIndices(ArmySnapshot army,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort,
            int minimumMovement = 0, int unloadRoom = int.MaxValue, int existingBodies = 0)
        {
            IReadOnlyList<WorthIt.DefenderProfile> pool = army?.Members
                ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<int> indices = Enumerable.Range(0, pool.Count)
                .Where(i => i >= (army.NonHeroIsAviation?.Count ?? 0)
                    || !army.NonHeroIsAviation[i]).ToList();
            for (int count = Mathf.Max(0, minimumEscort); count <= indices.Count; count++)
            {
                List<int> best = null;
                int bestAp = int.MaxValue;
                int bestMove = int.MinValue;
                foreach (List<int> subset in Combinations(indices, count))
                {
                    if (existingBodies - subset.Count > unloadRoom) continue;
                    int ap = subset.Sum(i => i < army.NonHeroActivationApCosts.Count
                        ? army.NonHeroActivationApCosts[i] : 0);
                    int move = subset.Count == 0 ? army.HeroMoveMax
                        : subset.Min(i => i < army.NonHeroMoveMax.Count
                            ? army.NonHeroMoveMax[i] : army.MaxMovement);
                    move = Mathf.Min(army.HeroMoveMax > 0 ? army.HeroMoveMax : army.MaxMovement, move);
                    if (move < minimumMovement) continue;
                    if (best != null && (ap > bestAp || (ap == bestAp && move <= bestMove)))
                        continue;
                    List<WorthIt.DefenderProfile> roster = subset.Select(i => pool[i]).ToList();
                    if (EconomyRosterSafe(roster, army.Commander, threats, minimumEscort))
                    {
                        best = subset;
                        bestAp = ap;
                        bestMove = move;
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
        }

        private static IEnumerable<List<T>> Combinations<T>(IReadOnlyList<T> source,
            int count, int start = 0, List<T> prefix = null)
        {
            prefix ??= new List<T>();
            if (prefix.Count == count)
            {
                yield return new List<T>(prefix);
                yield break;
            }
            for (int i = start; i <= source.Count - (count - prefix.Count); i++)
            {
                prefix.Add(source[i]);
                foreach (List<T> result in Combinations(source, count, i + 1, prefix))
                    yield return result;
                prefix.RemoveAt(prefix.Count - 1);
            }
        }

        internal static float EconomyCurrentStageAp(float preparationAp, float activationAp,
            bool activated, bool travelNeeded, bool completionThisTurn,
            float buildAp, float followupAp) => Mathf.Max(0f, preparationAp)
                + (travelNeeded && !activated ? Mathf.Max(0f, activationAp) : 0f)
                + (completionThisTurn ? Mathf.Max(buildAp, followupAp) : 0f);

        internal static float EstimateEconomyAssignmentAp(EconomyBuilderRouteSnapshot route,
            float buildApCost, bool includeReturn)
        {
            int move = Mathf.Max(1, route.MaxMovement);
            int outboundTurns = route.TravelCost <= 0 ? 0
                : route.CurrentMovement > 0
                    ? 1 + Mathf.CeilToInt(Mathf.Max(0,
                        route.TravelCost - route.CurrentMovement) / (float)move)
                    : Mathf.CeilToInt(route.TravelCost / (float)move);
            // The "already paid" discount only applies when this turn's activation actually bought
            // MP toward the first leg of the route (route.CurrentMovement > 0, matching the branch
            // above that folded this turn into outboundTurns). When CurrentMovement == 0 the
            // builder's current activation is fully spent with nothing left for this route, so the
            // FIRST outbound turn still needs a brand-new activation next turn — subtracting one
            // here would double-count the same already-consumed activation as covering a future
            // turn it never touched.
            bool currentTurnAlreadyProgressesRoute = route.CurrentMovement > 0;
            int paidOutboundActivations = Mathf.Max(0,
                outboundTurns - (route.HasActivatedThisTurn && currentTurnAlreadyProgressesRoute
                    && outboundTurns > 0 ? 1 : 0));
            if (route.IsOnTarget) paidOutboundActivations = 0;
            // int.MaxValue is the route snapshot's "no safe way back" (SafeStepPathing found no
            // return path at all) — unknown, not a cost: pricing it as ~1e9 turns drove the task
            // to value -1e9 (playtest 2026-10-01 #4, Draven T5).
            bool returnKnown = route.ReturnTravelCost > 0 && route.ReturnTravelCost < int.MaxValue;
            int returnTurns = includeReturn && returnKnown
                ? Mathf.CeilToInt(route.ReturnTravelCost / (float)move) : 0;
            return Mathf.Max(0f, buildApCost)
                + (paidOutboundActivations + returnTurns) * Mathf.Max(0, route.ActivationApCost);
        }

        private static IEnumerable<(EconomyBuilderRouteSnapshot route, ArmySnapshot army)>
            EconomyBuilderCandidates(WorldSnapshot snap, HexCoord target,
                IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
                IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments)
        {
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = routes
                ?? System.Array.Empty<EconomyBuilderRouteSnapshot>();
            foreach (EconomyBuilderRouteSnapshot route in witnessed)
            {
                ArmySnapshot army = snap?.Self?.Armies?.FirstOrDefault(
                    a => a != null && a.ArmyId == route.ArmyId);
                if (CandidateRejection(snap, target, route, army, activeIntents, commitments) == null)
                    yield return (route, army);
            }
        }

        // Why a witnessed route's army is not a structural builder candidate for `target`, or
        // null when it is. The ONE candidate gate of EconomyBuilderCandidates, also printed by
        // Provisioning's diagnostic trace (EconomyBuilderCandidateRejection).
        private static string CandidateRejection(WorldSnapshot snap, HexCoord target,
            EconomyBuilderRouteSnapshot route, ArmySnapshot army,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments)
        {
            if (army == null)
                return "not_in_snapshot";
            // Shape only: a garrison needs an extraction route, or already stands on the site with
            // a hero. Either way the assignment / claim / threat gates below still apply — a
            // garrison's heroes are pinned through its army id (ActorCommitments: an Economy or
            // Development intent holds the garrison while its hero is still inside).
            if (army.IsGarrison)
            {
                if (!route.RequiresGarrisonExtraction && !(route.IsOnTarget && army.HasHero))
                    return "garrison_without_extraction_route";
            }
            else if (!army.IsMobileEconomyBuilder)
                return "not_mobile_economy_builder";

            MissionIntent assignment = ActiveAssignment(activeIntents, army.ArmyId);
            if (assignment != null)
            {
                if (assignment.Kind == MissionKind.Economy)
                {
                    if (!EconomyDonorStructurallyEligible(assignment)
                        && (assignment.Economy == null
                            || !assignment.Economy.TargetHex.Equals(target)))
                        return $"economy_assignment_elsewhere={assignment.IntentKey}";
                }
                else if (!EconomyDonorStructurallyEligible(assignment))
                    return $"protected_assignment={assignment.IntentKey}";
            }
            if (assignment == null && commitments != null && commitments.IsArmyClaimed(army.ArmyId))
                return "claimed";
            return null;
        }

        // Diagnostics: why `armyId` never became a ranked builder for `target` — the candidate
        // gate's answer, or the later ranking stage (suitability / loan) when the gate passed.
        internal static string EconomyBuilderCandidateRejection(WorldSnapshot snap, HexCoord target,
            IReadOnlyList<EconomyBuilderRouteSnapshot> routes, int armyId,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments)
        {
            foreach (EconomyBuilderRouteSnapshot route in routes ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
            {
                if (route.ArmyId != armyId)
                    continue;
                ArmySnapshot army = snap?.Self?.Armies?.FirstOrDefault(
                    a => a != null && a.ArmyId == armyId);
                return CandidateRejection(snap, target, route, army, activeIntents, commitments)
                    ?? "ranking_rejected (suitability or loan gate)";
            }
            return "no_witnessed_route";
        }

        private static MissionIntent ActiveAssignment(
            IReadOnlyList<MissionIntent> activeIntents, int armyId) =>
            activeIntents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                && i.PreferredMoverArmyId == armyId);

        // What taking this builder off its current task costs this build: the displaced task's
        // own value (MissionIntent.DisplacementValue), whatever axis it belongs to. Only
        // continuing the build of this same site is free; a return leg is free by construction.
        private static float EconomyMissionOpportunityCost(EconomyBuilderChoice builder,
            IReadOnlyList<MissionIntent> activeIntents, HexCoord site)
        {
            if (builder?.Army == null)
                return 0f;
            return TaskScoreEvaluator.MoverOpportunityCost(MissionIntent.DisplacementValueOf(
                activeIntents, builder.Army.ArmyId,
                isOwn: i => MissionContinuityLayer.HoldsEconomyBuildSite(i, site)));
        }

        internal static bool EconomyDonorStructurallyEligible(MissionIntent donor)
        {
            if (donor == null)
                return false;
            if (donor.Kind == MissionKind.Economy)
                return donor.Economy?.Kind == EconomyTaskKind.ReturnBuilder;
            if (donor.Funding != CommitmentTier.None && donor.Funding != CommitmentTier.Soft)
                return false;
            if (donor.Kind == MissionKind.Scout)
                return donor.Scout != null && donor.Scout.Kind != ScoutTargetKind.Surveil;
            if (donor.Kind == MissionKind.Raid)
                return donor.Raid != null && !donor.Raid.OperationStarted;
            return false;
        }

        internal static bool EconomyLoanAllowed(MissionIntent donor, float buildValue,
            EconomyBuilderChoice builder, float buildApCost, out float netValue)
        {
            // buildValue is already card-priced site TaskScore.Value. The builder's
            // assessed operation AP includes the card and real outbound/return activations;
            // subtract only extra AP via the SAME conversion as final TaskScore.Delivery.
            float extraAp = Mathf.Max(0f,
                (builder?.TotalAssignmentApCost ?? buildApCost) - buildApCost);
            netValue = buildValue - TaskScoreEvaluator.Price(extraAp)
                - TaskScoreEvaluator.MoverOpportunityCost(donor?.DisplacementValue ?? 0f);
            // Same-turn reachability remains a legality gate rather than a per-hex fee.
            // Donor protections for Surveil, started Raid and Hard commitments are unchanged.
            return builder != null && EconomyDonorStructurallyEligible(donor)
                && builder.Route.TravelCost <= builder.Route.CurrentMovement
                && netValue >= AiConfigV2.taskScoreEconomyLoanHysteresisThreshold;
        }


        internal static float EconomyPaybackTurns(float expectedIncomeGain,
            float resourceCost, float assignmentApCost) => expectedIncomeGain <= 0f
                ? float.PositiveInfinity
                : Mathf.Max(0f, resourceCost) / expectedIncomeGain;

        private static string AddBaseCandidates(WorldSnapshot s, List<AxisDemand> output,
            PlayerSetupData player, AiTurnContext ctx,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            out int noBuilder, out int strategicValueRejected,
            out int deliveryValueRejected, out int thresholdRejected, out int newHeroFallback)
        {
            noBuilder = 0;
            strategicValueRejected = 0;
            deliveryValueRejected = 0;
            thresholdRejected = 0;
            newHeroFallback = 0;
            List<CardData> baseCards = (s.Self.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition?.cardType == CardType.Base)
                .OrderBy(c => c.Definition.authoredKey ?? c.Definition.displayName)
                .ToList();
            if (baseCards.Count == 0 || s.Economy?.BaseOpportunities == null)
            {
                return "considered=0 kept=0 reason=no_base_card_or_opportunity";
            }

            int considered = 0;
            int kept = 0;
            AxisDemand best = null;
            (float Value, HexCoord Hex, string Card, float Economic, float Payback,
                float Global, float Expansion)? bestRejected = null;
            MissionIntentState intentState = MissionIntentRegistry.GetOrCreate(player);
            var meaningfulDemands = new List<AxisDemand>();

            foreach (EconomyBaseOpportunity site in s.Economy.BaseOpportunities)
                foreach (CardData card in baseCards)
                {
                    considered++;
                    if (intentState.IsBaseExpansionDeliverySuppressed(s.TurnNumber, card, site.Hex))
                    {
                        thresholdRejected++;
                        continue;
                    }
                    if (HasActiveEconomyIntentAtHexOfKind(activeIntents, site.Hex,
                        EconomyTaskKind.BuildExtraction))
                        continue;

                    bool committed = IsActiveBaseCommitment(activeIntents, site.Hex, card);
                    StrategicCardEvaluator.BaseSiteValue facts =
                        StrategicCardEvaluator.ScoreBaseSite(s, site, card);

                    // Only real card-semantic facts come from Evaluation; TaskScore is the
                    // sole numeric evaluator of this Base's economic and strategic value.
                    float economicGainFact = Mathf.Max(0f, facts.HexYield);
                    int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(s, site.Hex);
                    float resourceCost = StrategicCardEvaluator.ResourceCostSum(
                        card.EffectivePlayResourceCost);
                    float resourcePrice = ActionPrice.Resources(card.EffectivePlayResourceCost, s);
                    // Base income is multi-resource. Reuse the card-semantic per-type gain owner
                    // and bind each resource's shortage only to its OWN marginal production.
                    var marginalByResource = new List<(float Gain, float Priority)>();
                    float usefulGainTotal = 0f;
                    foreach (ResourceType type in ResourceBundle.All)
                    {
                        float typeGain = StrategicCardEvaluator.BaseCardMarginalGain(
                            s, site, card.Definition, type);
                        if (typeGain <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        EconomyResourceStanding standing = s.Economy.PerType
                            .FirstOrDefault(x => x.Type == type);
                        float usefulTypeGain = standing.UsefulMarginalIncomeGain(typeGain);
                        if (usefulTypeGain <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        float priority = TaskScoreEvaluator.ResourcePriority(standing,
                            ResourceStarvationRegistry.Pressure(player, type));
                        marginalByResource.Add((usefulTypeGain, priority));
                        usefulGainTotal += usefulTypeGain;
                    }
                    float paybackTurns = usefulGainTotal > AiConfigV2.allocatorSliceEpsilon
                        ? EconomyPaybackTurns(usefulGainTotal, resourceCost, card.EffectivePlayApCost)
                        : float.PositiveInfinity;
                    // Economy-native structural fact (Analysis-owned, see
                    // CountNewResourceClusterHexes): this Base would open a resource cluster no
                    // owned base already reaches, regardless of whether that cluster's income is
                    // USEFUL right now (economic/payback price that separately).
                    bool hasUsefulGain = usefulGainTotal > AiConfigV2.allocatorSliceEpsilon;
                    TaskScore siteOnlyScore = BuildFoundBaseScore(s, marginalByResource,
                        hasUsefulGain, paybackTurns, facts, site, homeDistance,
                        card.EffectivePlayApCost, resourcePrice);
                    float economic = siteOnlyScore.EconomicHexBenefit;
                    float payback = siteOnlyScore.Payback;
                    float airfield = siteOnlyScore.Airfield;
                    float global = siteOnlyScore.GlobalCardEffect;
                    float front = siteOnlyScore.FrontProgress;
                    float corridor = siteOnlyScore.CorridorAlignment;
                    float proximity = siteOnlyScore.OwnTerritoryProximity;
                    float defense = siteOnlyScore.TerrainDefense;
                    float expansion = siteOnlyScore.EconomicExpansionValue;
                    float cardPrice = siteOnlyScore.CardPrice;
                    float citadelRisk = siteOnlyScore.CitadelThreatRisk;
                    // InfrastructureActions.TryFoundBase carries the extraction facilities
                    // into the new Base: their production is preserved, not lost.

                    // Economy owns the REASON to found this Base. Positional terms
                    // (airfield/front/corridor/defense/proximity) still rank WHERE an already
                    // economy-justified Base should go, but they must not manufacture an Economy
                    // project by themselves. A committed delivery is preserved by Continuity.
                    bool meaningful = committed || HasMeaningfulBaseBenefit(siteOnlyScore);
                    if (!meaningful)
                    {
                        strategicValueRejected++;
                        // Diagnostics-only: hasEconomyPurpose is an OR of four epsilon-floored terms,
                        // so every rejected site has all four near zero by construction — ranking
                        // rejects by "how close" to that floor is meaningless. What IS worth surfacing
                        // is the single best-placed rejected site's full siteOnlyScore.Value: it shows
                        // how much placement value is being correctly withheld for genuinely having no
                        // economic reason yet, the one number a whole-session "kept=0" cannot answer.
                        if (bestRejected == null || siteOnlyScore.Value > bestRejected.Value.Value)
                            bestRejected = (siteOnlyScore.Value, site.Hex, card.Definition.displayName,
                                economic, payback, global, expansion);
                        continue;
                    }

                    MissionIntent pinnedBase = committed ? activeIntents?.FirstOrDefault(i =>
                        MissionContinuityLayer.HoldsEconomyBuildSite(i, site.Hex)
                        && i.Economy.Kind == EconomyTaskKind.FoundBase
                        && (i.Economy.BuildCard == null || i.Economy.BuildCard == card)
                        && i.PreferredMoverArmyId.HasValue) : null;
                    EconomyBuilderChoice builder = SelectEconomyBuilder(
                        s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                        siteOnlyScore.Value, card.EffectivePlayApCost, includeReturn: false,
                        pinnedBuilderArmyId: pinnedBase?.PreferredMoverArmyId);
                    bool structuralRoute = site.PreparationTravelCost < int.MaxValue
                        || HasStructuralEconomyBuilderRoute(s, site.Hex, site.BuilderRoutes);
                    if (!structuralRoute)
                    {
                        noBuilder++;
                        continue;
                    }

                    float travel = builder?.Route.TravelCost ?? site.PreparationTravelCost;
                    float heroCost = EconomyMissionOpportunityCost(builder, activeIntents, site.Hex);
                    float assignmentAp = builder?.TotalAssignmentApCost ?? card.EffectivePlayApCost;
                    float extraAp = Mathf.Max(0f, assignmentAp - card.EffectivePlayApCost);
                    TaskScore score = BuildFoundBaseScore(s, marginalByResource,
                        hasUsefulGain, paybackTurns, facts, site, homeDistance,
                        card.EffectivePlayApCost, resourcePrice, extraAp, heroCost);
                    float value = score.Value;
                    // Same rule as extraction: a ready hero that turns a fresh Base into a loss
                    // leaves it to a possible NEW hero (Materialization prices that path).
                    bool readyLossToNewHero = builder != null && pinnedBase == null
                        && value <= AiConfigV2.allocatorSliceEpsilon
                        && siteOnlyScore.Value > AiConfigV2.allocatorSliceEpsilon;
                    if (readyLossToNewHero)
                        newHeroFallback++;

                    meaningfulDemands.Add(new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Economy,
                        Capability = CapabilityKind.EconomicExpansionBase,
                        DesiredAmount = 1f,
                        TargetHex = site.Hex,
                        EconomyBuildCard = card,
                        EconomyBuildResourceCost = card.EffectivePlayResourceCost,
                        EconomyBuildApCost = card.EffectivePlayApCost,
                        MinimumFollowupAp = card.EffectivePlayApCost,
                        EconomyExpectedIncomeGain = economicGainFact,
                        EconomySiteValue = siteOnlyScore.Value,
                        EconomyTravelCost = travel,
                        EconomyHeroOpportunityCost = readyLossToNewHero ? 0f : heroCost,
                        EconomyAssignmentApCost = readyLossToNewHero
                            ? card.EffectivePlayApCost : assignmentAp,
                        EconomyPaybackTurns = paybackTurns,
                        EconomyPreferredBuilderArmyId = readyLossToNewHero
                            ? null : builder?.Army.ArmyId,
                        EconomyReadyDeliveryCost = readyLossToNewHero
                            ? ReadyDeliveryCost(extraAp, heroCost) : (float?)null,
                        EconomyBuilderRoutes = site.BuilderRoutes,
                        WorldTaskScore = readyLossToNewHero ? siteOnlyScore : score,
                        Value = readyLossToNewHero ? siteOnlyScore.Value : score.Value,
                        Explain = $"Base task={score.Value:0.##} economic={economic:0.##} "
                            + $"payback={payback:0.##} expansion={expansion:0.##} "
                            + $"airfield={airfield:0.##} global={global:0.##} "
                            + $"front={front:0.##} corridor={corridor:0.##} proximity={proximity:0.##} "
                            + $"defense={defense:0.##} "
                            + $"price={cardPrice:0.##} delivery={score.Delivery:0.##} "
                            + $"moverOpp={heroCost:0.##} citadelRisk={citadelRisk:0.##}"
                            + (readyLossToNewHero
                                ? $"; ready#{builder.Army.ArmyId} loses -> new_hero_only" : ""),
                    });
                }

            foreach (AxisDemand demand in meaningfulDemands)
            {
                bool committed = IsActiveBaseCommitment(
                    activeIntents, demand.TargetHex, demand.EconomyBuildCard);
                // HasMeaningfulBaseBenefit only proves a non-zero economy REASON exists (see its
                // own header comment — hasEconomyPurpose). It was never the whole admission
                // decision: the canonical TaskScore.Value (reason + price + delivery + placement,
                // the same fold every other axis competes on) must also clear zero, or a Base with
                // a real purpose but a net-negative full price (e.g. expansion=3 but price/delivery
                // outweigh it) is admitted anyway — a fresh project must never originate net-negative.
                // A live COMMITTED Base is the one deliberate exception: Continuity already owns its
                // lifecycle/hysteresis (CanReplaceCommittedBase, IntentReapedStall/Idle) and must not
                // be abandoned here merely because marginal economics dipped after the project started.
                bool admitted = committed
                    || (HasMeaningfulBaseBenefit(demand.WorldTaskScore)
                        && demand.Value > AiConfigV2.allocatorSliceEpsilon);
                if (!admitted)
                {
                    if (!demand.EconomyPreferredBuilderArmyId.HasValue)
                        noBuilder++;
                    else if (demand.EconomySiteValue <= AiConfigV2.allocatorSliceEpsilon)
                        strategicValueRejected++;
                    else
                        deliveryValueRejected++;
                    continue;
                }

                output.Add(demand);
                kept++;
                AiDebugLog.WriteDeduped(
                    $"{demand.EconomyBuildCard?.Definition?.displayName}@{demand.TargetHex}",
                    $"[AI][V2][Economy][BaseCandidate] kept @({demand.TargetHex?.Q},{demand.TargetHex?.R}) "
                    + demand.Explain);
                if (best == null || demand.Value > best.Value)
                    best = demand;
            }

            if (best != null)
                return $"considered={considered} kept={kept} best={best.EconomyBuildCard.Definition.displayName} "
                    + $"target=({best.TargetHex?.Q},{best.TargetHex?.R}) value={best.Value:0.##}";
            return bestRejected == null
                ? $"considered={considered} kept={kept} best=none"
                : $"considered={considered} kept={kept} best=none closestMiss="
                    + $"{bestRejected.Value.Card}@({bestRejected.Value.Hex.Q},{bestRejected.Value.Hex.R}) "
                    + $"fullValueIfAdmitted={bestRejected.Value.Value:0.##} "
                    + $"economic={bestRejected.Value.Economic:0.##} payback={bestRejected.Value.Payback:0.##} "
                    + $"global={bestRejected.Value.Global:0.##} expansion={bestRejected.Value.Expansion:0.##} "
                    + "reason=no_economy_purpose_yet";
        }

        // One demand per DISTINCT (card, builder) pair — each is a genuinely independent Base
        // project (separate card, separate actor, no shared resource yet). Collapsing all of them
        // down to one global "best" (the old behaviour) meant the AI could only ever entertain a
        // single Base candidate per turn even holding two Base cards with two free builders; the
        // hex/actor/card dedup in EconomyDemands' caller still resolves the case where two
        // candidates would in fact compete for the same hex, mover or card. Only the ONE group
        // matching the live incumbent's exact (card, actor) goes through the hysteresis owner below —
        // every other group is a brand-new project with no incumbent to protect, so its own top-ranked
        // site is offered directly.
        internal static List<AxisDemand> SelectBaseDemandsForCurrentCommitment(
            IReadOnlyList<AxisDemand> ranked, IReadOnlyList<MissionIntent> activeIntents)
        {
            var result = new List<AxisDemand>();
            if (ranked == null || ranked.Count == 0)
                return result;

            MissionIntent incumbent = activeIntents?.FirstOrDefault(IsRetargetableBaseCommitment);

            foreach (var group in ranked.GroupBy(d => (d.EconomyBuildCard, d.EconomyPreferredBuilderArmyId)))
            {
                bool isIncumbentGroup = incumbent != null
                    && group.Key.EconomyBuildCard == incumbent.Economy.BuildCard
                    && group.Key.EconomyPreferredBuilderArmyId == incumbent.PreferredMoverArmyId;
                AxisDemand chosen = isIncumbentGroup
                    ? SelectBaseDemandForCurrentCommitment(group.ToList(), activeIntents)
                    : group.FirstOrDefault();
                if (chosen != null)
                    result.Add(chosen);
            }
            return result;
        }

        // One Base selection decision owner for a SINGLE (card, builder) group — the incumbent's
        // current fully delivered score wins over its captured score when a same-card/same-actor
        // candidate is still present. Never compare the challenger's full Value against
        // Economy.BuildValue (site only). Called once per matching group by the plural selector
        // above; callers with no live incumbent commitment may call it directly (unchanged single-
        // candidate contract preserved for existing tests).
        internal static AxisDemand SelectBaseDemandForCurrentCommitment(
            IReadOnlyList<AxisDemand> ranked, IReadOnlyList<MissionIntent> activeIntents)
        {
            AxisDemand first = ranked?.FirstOrDefault();
            MissionIntent incumbent = activeIntents?.FirstOrDefault(IsRetargetableBaseCommitment);
            if (first == null || incumbent == null)
                return first;

            AxisDemand refreshed = ranked.FirstOrDefault(d => d != null
                && d.TargetHex.HasValue && d.TargetHex.Value.Equals(incumbent.Economy.TargetHex)
                && d.EconomyBuildCard == incumbent.Economy.BuildCard
                && d.EconomyPreferredBuilderArmyId == incumbent.PreferredMoverArmyId);
            float? incumbentValue = refreshed != null ? refreshed.Value
                : incumbent.Economy.IntrinsicValue;
            if (!incumbentValue.HasValue)
                return refreshed;  // unknown canonical value: only the incumbent may execute

            AxisDemand challenger = null;
            foreach (AxisDemand candidate in ranked)
            {
                if (candidate == null || candidate.EconomyBuildCard != incumbent.Economy.BuildCard
                    || candidate.EconomyPreferredBuilderArmyId != incumbent.PreferredMoverArmyId
                    || !candidate.TargetHex.HasValue
                    || candidate.TargetHex.Value.Equals(incumbent.Economy.TargetHex))
                    continue;
                candidate.EconomySwitchIncumbentValue = incumbentValue.Value;
                if (!CanReplaceCommittedBase(incumbent, candidate))
                    continue;
                if (challenger == null || candidate.Value > challenger.Value)
                    challenger = candidate;
            }
            // An unrelated candidate must not execute while the old Base still owns its
            // card/actor. If its site is no longer offered, Continuity owns retirement.
            return challenger ?? refreshed;
        }

        // One hysteresis/admission predicate reused by Demand, Phase A and Continuity.
        // Explicit scan provenance prevents a stale site-only score from authorizing a switch.
        internal static bool CanReplaceCommittedBase(MissionIntent incumbent, AxisDemand rival) =>
            IsRetargetableBaseCommitment(incumbent)
            && rival?.RequestingAxis == DesireAxis.Economy
            && rival.Capability == CapabilityKind.EconomicExpansionBase
            && rival.TargetHex.HasValue
            && !rival.TargetHex.Value.Equals(incumbent.Economy.TargetHex)
            && rival.EconomyBuildCard == incumbent.Economy.BuildCard
            && rival.EconomyPreferredBuilderArmyId == incumbent.PreferredMoverArmyId
            && rival.EconomySwitchIncumbentValue.HasValue
            && rival.Value > AiConfigV2.allocatorSliceEpsilon
            && rival.Value > rival.EconomySwitchIncumbentValue.Value
                + AiConfigV2.economyBaseSwitchHysteresisThreshold;

        // The Base commitment the switch hysteresis protects and may retarget: an ACTIVE FoundBase
        // that still owns its card and its actor. One predicate for Demand's incumbent lookups and
        // CanReplaceCommittedBase (Phase A / Continuity).
        private static bool IsRetargetableBaseCommitment(MissionIntent i) =>
            i != null && i.Kind == MissionKind.Economy && i.Status == IntentStatus.Active
            && i.Economy?.Kind == EconomyTaskKind.FoundBase
            && i.Economy.BuildCard != null && i.PreferredMoverArmyId.HasValue;

        // Economy admission predicate for a NEW Base project. The axis only needs to prove WHY a
        // Base is worth having at all: useful local income/payback, a PlayerGlobal effect
        // explicitly evaluated for IntendedRole.Economy by StrategicCardEvaluator, or a structural
        // network-expansion fact (EconomicExpansionValue — this site reaches a resource cluster no
        // owned base already reaches, independent of whether that income is useful YET). Whether the
        // WHOLE project (this reason plus price, delivery, threat, AND placement — airfield,
        // front/corridor, proximity, terrain-defense) is worth funding is then decided exactly once,
        // by the same full TaskScore.Value every other axis competes on (see ARCHITECTURE.md "one
        // struct, one fold"). A second, narrower partial-sum here (the former EconomyBaseAdmissionValue)
        // duplicated that fold over a hand-picked subset of slots and rejected sites whose real
        // TaskScore.Value was already positive once placement was counted — the exact "post-fold
        // score adjustment" ARCHITECTURE.md calls out as a bug, not a precedent. Placement still
        // cannot manufacture a reason on its own: hasEconomyPurpose guards that with zero cost terms
        // involved. Committed deliveries bypass this predicate at the call sites so Continuity is
        // not abandoned merely because marginal economics changed after the project started.
        internal static bool HasMeaningfulBaseBenefit(TaskScore score) =>
            score.EconomicHexBenefit > AiConfigV2.allocatorSliceEpsilon
            || score.Payback > AiConfigV2.allocatorSliceEpsilon
            || score.GlobalCardEffect > AiConfigV2.allocatorSliceEpsilon
            || score.EconomicExpansionValue > AiConfigV2.allocatorSliceEpsilon;

        private static bool HasActiveEconomyIntentAtHexOfKind(IReadOnlyList<MissionIntent> intents,
            HexCoord? target, EconomyTaskKind kind)
        {
            if (!target.HasValue || intents == null)
                return false;
            bool build = kind == EconomyTaskKind.BuildExtraction || kind == EconomyTaskKind.FoundBase;
            return intents.Any(i => (build
                    ? MissionContinuityLayer.HoldsEconomyBuildSite(i, target.Value)
                    : i != null && i.Status == IntentStatus.Active && i.Kind == MissionKind.Economy
                        && i.Economy?.TargetHex.Equals(target.Value) == true)
                && i.Economy.Kind == kind);
        }

        private static bool IsActiveBaseCommitment(IReadOnlyList<MissionIntent> intents,
            HexCoord? target, CardData card)
        {
            if (!target.HasValue || intents == null)
                return false;
            return intents.Any(i => MissionContinuityLayer.MatchesEconomyBuild(i, EconomyTaskKind.FoundBase,
                    target.Value, null, card));
        }

        private static bool HasActiveEconomyBuildIntent(
            IReadOnlyList<MissionIntent> intents, AxisDemand demand)
        {
            return MissionContinuityLayer.HasEconomyBuildCommitment(intents, demand);
        }

        private static bool HasStructuralEconomyBuilderRoute(WorldSnapshot snap, HexCoord target,
            IReadOnlyList<EconomyBuilderRouteSnapshot> routes)
        {
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = routes
                ?? System.Array.Empty<EconomyBuilderRouteSnapshot>();
            return witnessed.Any(route => route.TravelCost < int.MaxValue
                && (snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>()).Any(army =>
                    army != null && army.ArmyId == route.ArmyId
                    && (army.IsMobileEconomyBuilder
                        || (route.IsOnTarget && army.IsGarrison && army.HasHero))));
        }

        private static CardDefinition ExtractionDefinition(AiTurnContext ctx, ResourceType type)
        {
            CardDefinition[] cards = ctx?.GameConfig?.extractionFacilityCards;
            int index = (int)type;
            return cards != null && index >= 0 && index < cards.Length ? cards[index] : null;
        }
    }
}
