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

            foreach (EconomyExtractionOpportunity site in s.Economy.ExtractionOpportunities
                ?? System.Array.Empty<EconomyExtractionOpportunity>())
            {
                if (!standings.TryGetValue(site.ResourceType, out EconomyResourceStanding rs))
                    continue;
                CardDefinition def = ExtractionDefinition(ctx, site.ResourceType);
                if (ctx?.GameConfig != null && def == null)
                    continue;

                float starvation = ResourceStarvationRegistry.Pressure(player, site.ResourceType);
                float resourcePriority = TaskScoreEvaluator.ResourcePriority(rs, starvation);
                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                // Keep raw income for execution; value and payback use only
                // economically useful marginal income from the frozen snapshot.
                float usefulGain = rs.UsefulMarginalIncomeGain(gain);
                if (usefulGain <= AiConfigV2.allocatorSliceEpsilon)
                {
                    // Existing delivery is still proposed directly by EconomyMissionPlanner
                    // from its durable intent, without refreshing it with surplus economics.
                    rejectedSurplus++;
                    continue;
                }

                float resourceCost = StrategicCardEvaluator.ResourceCostSum(def?.resourceCost);
                float cardAp = def?.apCost ?? 0f;
                float payback = EconomyPaybackTurns(usefulGain, resourceCost, cardAp);
                if (payback > AiConfigV2.economyExtractionMaxPaybackTurns)
                {
                    rejectedPayback++;
                    continue;
                }

                float exposure = StrategicCardEvaluator.ThreatExposure(s, site.Hex);
                int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(s, site.Hex);
                var siteOnlyScore = new TaskScore(
                    economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(usefulGain, resourcePriority),
                    payback: TaskScoreEvaluator.Payback(payback),
                    ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                    cardPrice: TaskScoreEvaluator.CardPrice(cardAp, resourceCost),
                    hexThreatRisk: TaskScoreEvaluator.HexThreatRisk(exposure));

                // Continuity owns the actor for an existing objective. A later, cheaper builder
                // must not supply a different delivery cost for that same durable operation.
                MissionIntent pinnedExtraction = activeIntents?.FirstOrDefault(i =>
                    MissionContinuityLayer.HoldsEconomyBuildSite(i, site.Hex)
                    && i.Economy.Kind == EconomyTaskKind.BuildExtraction
                    && i.Economy.ResourceType == site.ResourceType
                    && i.PreferredMoverArmyId.HasValue);
                EconomyBuilderChoice builder = SelectEconomyBuilder(
                    s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                    siteOnlyScore.Value, cardAp, includeReturn: true,
                    pinnedBuilderArmyId: pinnedExtraction?.PreferredMoverArmyId);
                float travel = builder?.Route.TravelCost
                    ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                float opportunity = EconomyMissionOpportunityCost(builder, activeIntents);
                float assignmentAp = builder?.TotalAssignmentApCost ?? cardAp;
                float extraAp = Mathf.Max(0f, assignmentAp - cardAp);

                var score = new TaskScore(
                    economicHexBenefit: siteOnlyScore.EconomicHexBenefit,
                    payback: siteOnlyScore.Payback,
                    ownTerritoryProximity: siteOnlyScore.OwnTerritoryProximity,
                    cardPrice: siteOnlyScore.CardPrice,
                    // extraAp is already the real re-activation AP for this multi-turn route
                    // (EstimateEconomyAssignmentAp: paid outbound/return activations x real
                    // ActivationApCost) — travel was a second, redundant raw-distance charge on
                    // top of that same real fact.
                    delivery: extraAp * AiConfigV2.taskScoreReactivationApWeight,
                    moverOpportunityCost: Mathf.Max(0f, opportunity),
                    hexThreatRisk: siteOnlyScore.HexThreatRisk);
                float value = score.Value;

                if (siteOnlyScore.Value <= AiConfigV2.allocatorSliceEpsilon)
                {
                    rejectedStrategicValue++;
                    continue;
                }
                // The ready hero makes this site a loss. Unless Continuity already owns that hero
                // for it, offer the site builder-less instead: Materialization may still deliver it
                // with a NEW hero, but only when that is cheaper than the ready one and the new
                // hero's own delivery stays under the site's value.
                bool readyLossToNewHero = value <= AiConfigV2.allocatorSliceEpsilon
                    && builder != null && pinnedExtraction == null;
                if (value <= AiConfigV2.allocatorSliceEpsilon && !readyLossToNewHero)
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
                    EconomyThreatExposure = exposure,
                    EconomyHeroOpportunityCost = readyLossToNewHero ? 0f : opportunity,
                    EconomyAssignmentApCost = readyLossToNewHero ? cardAp : assignmentAp,
                    EconomyPaybackTurns = payback,
                    EconomyPreferredBuilderArmyId = readyLossToNewHero
                        ? null : builder?.Army.ArmyId,
                    EconomyProjectedActivationApCost = readyLossToNewHero ? 0
                        : builder?.ProjectedActivationApCost ?? 0,
                    EconomyProjectedMaxMovement = readyLossToNewHero ? 0
                        : builder?.ProjectedMaxMovement ?? 0,
                    EconomyReadyDeliveryCost = readyLossToNewHero
                        ? ReadyDeliveryCost(extraAp, opportunity) : (float?)null,
                    EconomyBuilderRoutes = site.BuilderRoutes,
                    WorldTaskScore = readyLossToNewHero ? siteOnlyScore : score,
                    Value = readyLossToNewHero ? siteOnlyScore.Value : score.Value,
                    Explain = $"{site.ResourceType} task={score.Value:0.##} priority={resourcePriority:0.##} "
                        + $"marginalGain={gain:0.##} usefulGain={usefulGain:0.##} payback={payback:0.##} "
                        + $"travel={travel:0.##} exposure={exposure:0.##} "
                        + $"moverOpp={opportunity:0.##}"
                        + (readyLossToNewHero
                            ? $"; ready#{builder.Army.ArmyId} loses -> new_hero_only" : ""),
                });
            }

            foreach (AxisDemand collectorDemand in CollectorCapabilityDemands(
                s, standings, player, activeIntents))
                yield return collectorDemand;

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
            // int.MaxValue downstream, and ApBudgetLedger/MaterializationReservation/ResourceAllocator
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
                + rejectedStrategicValue + baseStrategicValue
                + rejectedDeliveryValue + baseDeliveryValue + baseThreshold;
            AiDebugLog.WriteDeduped("rejections", $"[AI][V2][Economy][Rejections] no_builder={rejectedNoBuilder + baseNoBuilder} "
                + $"payback={rejectedPayback} surplus={rejectedSurplus} strategic_value={rejectedStrategicValue + baseStrategicValue} "
                + $"delivery_value={rejectedDeliveryValue + baseDeliveryValue} "
                + $"threshold={baseThreshold} "
                + $"new_hero_fallback={newHeroFallback + baseNewHeroFallback} "
                + $"new_hero_paired={newHeroPaired}");
            if (selected.Count == 0)
                AiDebugLog.WriteDeduped("selected-none",
                    $"[AI][V2][Economy][Demand] selected=none rejected={rejectionTotal} "
                    + "reason=no_legal_valuable_site_or_base");
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

            var existingMobileCoverage = new HashSet<(HexCoord, ResourceType)>(
                (s.Economy.MobileCollectionOpportunities ?? System.Array.Empty<MobileCollectionOpportunity>())
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
                float exposure = StrategicCardEvaluator.ThreatExposure(s, site.Hex);
                int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(s, site.Hex);
                // Chain-independent ETA baseline — the same canonical fallback move budget every
                // other axis uses when the concrete mover is not chosen yet. Using the pre-picked
                // card's moveMax here was part of the same premature physical choice.
                int etaTurns = Mathf.Max(1, Mathf.CeilToInt(
                    homeDistance / (float)Mathf.Max(1, AiConfigV2.etaFallbackMoveBudget)));

                // Site economics only. CardPrice/Payback are deliberately NOT folded in here any
                // more: StrategicCardEvaluator.ResourceCost is "the ONLY place a chain is charged
                // for cost" and already prices AP, resources, the extra chain step and the
                // generation success discount for whichever chain actually delivers this
                // capability. Pricing a guessed card here as well was a straight double count, and
                // it is what made a generated or equipment-borne collector uncomparable.
                var score = new TaskScore(
                    economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(usefulGain, priority),
                    // No facility to lose if the target is abandoned — proximity stays upside-only,
                    // never a penalty for placing a collector far from home.
                    ownTerritoryProximity: Mathf.Max(0f,
                        TaskScoreEvaluator.OwnTerritoryProximity(homeDistance)),
                    delivery: TaskScoreEvaluator.DeliveryFromEta(0f,
                        etaTurns, AiConfigV2.taskScoreReactivationApWeight),
                    hexThreatRisk: TaskScoreEvaluator.HexThreatRisk(exposure));
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
                    EconomyThreatExposure = exposure,
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"Collector {site.ResourceType} task={score.Value:0.##} priority={priority:0.##} "
                        + $"gain={gain:0.##} usefulGain={usefulGain:0.##} "
                        + $"homeDist={homeDistance} eta={etaTurns} exposure={exposure:0.##} "
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
            EconomyThreatExposure = source.EconomyThreatExposure,
            EconomyHeroOpportunityCost = source.EconomyHeroOpportunityCost,
            EconomyAssignmentApCost = source.EconomyAssignmentApCost,
            EconomyPaybackTurns = source.EconomyPaybackTurns,
            EconomyPreferredBuilderArmyId = source.EconomyPreferredBuilderArmyId,
            EconomyProjectedActivationApCost = source.EconomyProjectedActivationApCost,
            EconomyProjectedMaxMovement = source.EconomyProjectedMaxMovement,
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
            Mathf.Max(0f, extraAp) * AiConfigV2.taskScoreReactivationApWeight
            + Mathf.Max(0f, moverOpportunityCost);

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
            alternative.EconomyProjectedActivationApCost = 0;
            alternative.EconomyProjectedMaxMovement = 0;
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
        }

        internal enum EconomyArmySuitability
        {
            Ready,
            LightenAtBase,
            ReinforceAtBase,
            Ineligible,
        }

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
                .OrderByDescending(x => x.Route.HasActiveEconomyCommitment
                    || ActiveAssignment(activeIntents, x.Route.ArmyId)?.Kind == MissionKind.Economy)
                .ThenBy(x => x.Suitability == EconomyArmySuitability.Ready ? 0
                    : x.Suitability == EconomyArmySuitability.LightenAtBase ? 1 : 2)
                // Among equally suitable builders use canonical delivery plus the ONE
                // donor interruption loss. Otherwise a cheaper AP loan can still lose
                // intrinsic value to a slightly dearer uncommitted actor.
                .ThenBy(x => Mathf.Max(0f, x.TotalAssignmentApCost - buildApCost)
                    * AiConfigV2.taskScoreReactivationApWeight
                    + EconomyMissionOpportunityCost(x, activeIntents))
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
            return assessed;

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
                    // A structural route (the garrison has a sparable hero and a safe path to
                    // `target`) is not the same fact as a deliverable builder: the hero must also
                    // be extractable into a real field container. Otherwise Demand would trust
                    // EconomyPreferredBuilderArmyId as "already provided" and skip
                    // EconomyHeroPrerequisite (see EconomyDemands) for a phantom builder no shell,
                    // host or fresh army can ever take delivery of. Reuse the SAME pure Shell ->
                    // Host -> Create resolver Provisioning and Execution share
                    // (ProvisioningManager.ResolveGarrisonExtractionCandidate) instead of a second
                    // copy of that decision. The unbounded envelope is intentional: Demand does not
                    // know this turn's AP budget (Provisioning's job) — this call only answers
                    // "does ANY legal container exist, and what does the cheapest one cost", the
                    // StructuralCandidate + DeliveryFeasible half of the contract; ExecutableNow is
                    // decided later, with live commitments/session, inside Provisioning.
                    ArmyData liveGarrison = ArmyRegistry.AllAt(army.Hex)
                        .FirstOrDefault(a => a != null && a.Id == army.ArmyId);
                    ProvisioningManager.GarrisonExtractionCandidate extraction = liveGarrison == null
                        ? ProvisioningManager.GarrisonExtractionCandidate.No("garrison no longer exists")
                        : ProvisioningManager.ResolveGarrisonExtractionCandidate(
                            liveGarrison.Owner, liveGarrison, commitments: null, session: null,
                            root: null, ecoApEnvelopeRemaining: float.MaxValue);
                    if (extraction.Tier == ProvisioningManager.GarrisonExtractionTier.None)
                    {
                        choice.IneligibleReason = extraction.Reason ?? "no_garrison_extraction_container";
                        AiDebugLog.WriteDeduped($"{target}|{army.ArmyId}",
                            $"[ECO][Builder] site=({target.Q},{target.R}) "
                            + $"actor=#{army.ArmyId} source=Garrison route=VALID extraction=None "
                            + "decision=REJECT reason=" + choice.IneligibleReason);
                        return choice;
                    }
                    choice.Suitability = EconomyArmySuitability.Ready;
                    choice.IneligibleReason = null;
                    choice.MinimumEscortCount = 0;
                    // The real minimum delivery AP includes the container's own cost (Shell/Host
                    // transfer activation, or CreateArmyApCost) on top of the hero's own
                    // reactivation. Fold it into a projected Route the same way every other branch
                    // of this method does, so TotalAssignmentApCost (builder ranking,
                    // EconomyLoanAllowed, EconomyMissionOpportunityCost) and
                    // choice.Route.ActivationApCost (what EconomyMissionPlanner.Requirements reads
                    // via SelectEconomyBuilder) both see the identical real cost Provisioning will
                    // check funds against — not just this struct's separate Projected* fields.
                    EconomyBuilderRouteSnapshot projectedRoute = route;
                    projectedRoute.ActivationApCost = route.ActivationApCost
                        + Mathf.RoundToInt(extraction.ApCost);
                    choice.Route = projectedRoute;
                    choice.TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                        projectedRoute, buildApCost, includeReturn);
                    choice.ProjectedActivationApCost = projectedRoute.ActivationApCost;
                    choice.ProjectedMaxMovement = route.MaxMovement;
                    AiDebugLog.WriteDeduped($"{target}|{army.ArmyId}",
                        $"[ECO][Builder] site=({target.Q},{target.R}) "
                        + $"actor=#{army.ArmyId} source=Garrison route=VALID "
                        + $"extraction={extraction.Tier} requiredAp={extraction.ApCost:0.##} "
                        + "decision=READY");
                    return choice;
                }

                List<AiMapMemory.KnownEnemySighting> threats = (route.RouteThreats
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Where(t => t.Owner?.IsNeutral != true)
                    .ToList();
                bool atBase = snap?.Self?.BaseHexes?.Contains(army.Hex) == true;
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
                    List<int> retained = atBase
                        ? MinimumSafeEconomyEscortIndices(army, threats, minimumEscort)
                        : currentIndices;
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
                    choice.Route = projectedRoute;
                    choice.TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                        projectedRoute, buildApCost, includeReturn);
                    choice.Suitability = atBase && current.Count > smallest
                        ? EconomyArmySuitability.LightenAtBase
                        : EconomyArmySuitability.Ready;
                    return choice;
                }

                if (!atBase || snap?.Self?.Armies == null)
                    return choice;
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
                        if (!EconomyRosterSafe(projected, army.Commander, threats, minimumEscort))
                            continue;
                        int addedAp = subset.Sum(i => i < garrison.NonHeroActivationApCosts.Count
                            ? garrison.NonHeroActivationApCosts[i] : 0);
                        int addedMove = subset.Min(i => i < garrison.NonHeroMoveMax.Count
                            ? garrison.NonHeroMoveMax[i] : garrison.MaxMovement);
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
                        choice.MinimumEscortCount = current.Count + best.Count;
                        choice.Suitability = EconomyArmySuitability.ReinforceAtBase;
                        choice.ProjectedActivationApCost = route.ActivationApCost + bestAp;
                        choice.ProjectedMaxMovement = Mathf.Min(route.MaxMovement,
                            bestMove > 0 ? bestMove : route.MaxMovement);
                        EconomyBuilderRouteSnapshot projectedRoute = route;
                        projectedRoute.ActivationApCost = choice.ProjectedActivationApCost;
                        projectedRoute.MaxMovement = Mathf.Max(1, choice.ProjectedMaxMovement);
                        choice.Route = projectedRoute;
                        choice.TotalAssignmentApCost = EstimateEconomyAssignmentAp(
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

        private static List<int> MinimumSafeEconomyEscortIndices(ArmySnapshot army,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
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
                    int ap = subset.Sum(i => i < army.NonHeroActivationApCosts.Count
                        ? army.NonHeroActivationApCosts[i] : 0);
                    int move = subset.Count == 0 ? army.HeroMoveMax
                        : subset.Min(i => i < army.NonHeroMoveMax.Count
                            ? army.NonHeroMoveMax[i] : army.MaxMovement);
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
            int returnTurns = includeReturn && route.ReturnTravelCost > 0
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
                ?? SnapshotFallbackRoutes(snap, target);
            foreach (EconomyBuilderRouteSnapshot route in witnessed)
            {
                ArmySnapshot army = snap?.Self?.Armies?.FirstOrDefault(
                    a => a != null && a.ArmyId == route.ArmyId);
                if (army == null)
                    continue;
                if (route.IsOnTarget && army.IsGarrison && army.HasHero)
                {
                    yield return (route, army);
                    continue;
                }
                if (army.IsGarrison)
                {
                    if (!route.RequiresGarrisonExtraction)
                        continue;
                }
                else if (!army.IsMobileEconomyBuilder)
                    continue;

                MissionIntent assignment = ActiveAssignment(activeIntents, army.ArmyId);
                bool claimed = commitments != null && commitments.IsArmyClaimed(army.ArmyId);
                if (assignment != null)
                {
                    if (assignment.Kind == MissionKind.Economy)
                    {
                        if (!EconomyDonorStructurallyEligible(assignment)
                            && (assignment.Economy == null
                                || !assignment.Economy.TargetHex.Equals(target)))
                            continue;
                    }
                    else if (!EconomyDonorStructurallyEligible(assignment))
                        continue;
                }
                if (claimed && assignment == null)
                    continue;
                if (EconomyBuilderUnderImmediateThreat(snap, army.Hex))
                    continue;
                yield return (route, army);
            }
        }

        private static IReadOnlyList<EconomyBuilderRouteSnapshot> SnapshotFallbackRoutes(
            WorldSnapshot snap, HexCoord target)
        {
            var result = new List<EconomyBuilderRouteSnapshot>();
            foreach (ArmySnapshot army in snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
            {
                if (army == null)
                    continue;
                if (army.IsGarrison)
                {
                    if (army.HasHero && army.Hex.Equals(target))
                        result.Add(new EconomyBuilderRouteSnapshot
                        {
                            ArmyId = army.ArmyId, TravelCost = 0, ReturnTravelCost = 0,
                            CurrentMovement = army.CurrentMovement, MaxMovement = army.MaxMovement,
                            ActivationApCost = army.ActivationApCost, ArmySize = army.MemberCount,
                            HasActivatedThisTurn = army.HasActivatedThisTurn,
                            EffectiveArmyPower = army.EffectiveArmyPower, IsOnTarget = true,
                        });
                    continue;
                }
                if (!army.IsMobileEconomyBuilder)
                    continue;
                result.Add(new EconomyBuilderRouteSnapshot
                {
                    ArmyId = army.ArmyId,
                    TravelCost = HexGridMath.Distance(army.Hex, target),
                    ReturnTravelCost = snap?.Self?.BaseHexes?.Count > 0
                        ? snap.Self.BaseHexes.Min(h => HexGridMath.Distance(target, h)) : 0,
                    CurrentMovement = army.CurrentMovement,
                    MaxMovement = army.MaxMovement,
                    ActivationApCost = army.ActivationApCost,
                    HasActivatedThisTurn = army.HasActivatedThisTurn,
                    ArmySize = army.MemberCount,
                    EffectiveArmyPower = army.EffectiveArmyPower,
                    IsOnTarget = army.Hex.Equals(target),
                });
            }
            return result;
        }

        private static MissionIntent ActiveAssignment(
            IReadOnlyList<MissionIntent> activeIntents, int armyId) =>
            activeIntents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                && i.PreferredMoverArmyId == armyId);

        private static float EconomyMissionOpportunityCost(EconomyBuilderChoice builder,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            if (builder?.Army == null)
                return 0f;
            MissionIntent assignment = ActiveAssignment(activeIntents, builder.Army.ArmyId);
            return assignment == null || assignment.Kind == MissionKind.Economy
                ? 0f
                : AiConfigV2.economyLoanContinuationLoss;
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
            netValue = buildValue - extraAp * AiConfigV2.taskScoreReactivationApWeight
                - AiConfigV2.economyLoanContinuationLoss;
            // Same-turn reachability remains a legality gate rather than a per-hex fee.
            // Donor protections for Surveil, started Raid and Hard commitments are unchanged.
            return builder != null && EconomyDonorStructurallyEligible(donor)
                && builder.Route.TravelCost <= builder.Route.CurrentMovement
                && netValue >= AiConfigV2.economyLoanHysteresisThreshold;
        }

        internal static bool EconomyBuilderUnderImmediateThreat(
            WorldSnapshot snap, HexCoord hex) =>
            snap?.Threat?.Threats != null && snap.Threat.Threats.Any(t => t?.Asset != null
                && t.Asset.Hex.Equals(hex) && t.Severity >= AiConfigV2.threatSeverityTrigger
                && (!t.EnemyEta.HasValue || t.EnemyEta.Value <= 1));

        internal static float EconomyRecoveryThreatExposure(WorldSnapshot snap, HexCoord hex) =>
            StrategicCardEvaluator.ThreatExposure(snap, hex);

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
                    float basePriority = marginalByResource.Count == 0 ? 0f
                        : marginalByResource.Max(x => x.Priority);
                    float economic = TaskScoreEvaluator.EconomicHexBenefit(marginalByResource);
                    float payback = usefulGainTotal > AiConfigV2.allocatorSliceEpsilon
                        ? TaskScoreEvaluator.Payback(paybackTurns) : 0f;
                    float airfield = TaskScoreEvaluator.Airfield(facts.Airfield);
                    float global = Mathf.Clamp(facts.GlobalEffect, 0f,
                        AiConfigV2.taskScoreGlobalCardEffectMax);
                    float front = TaskScoreEvaluator.FrontProgress(site.ForwardProgressValue);
                    float corridor = TaskScoreEvaluator.CorridorAlignment(site.CorridorAlignmentValue);
                    float proximity = TaskScoreEvaluator.OwnTerritoryProximity(homeDistance);
                    float defense = TaskScoreEvaluator.TerrainDefense(site.DefenseBonusValue);
                    // Economy-native structural fact (Analysis-owned, see
                    // CountNewResourceClusterHexes): this Base would open a resource cluster no
                    // owned base already reaches, regardless of whether that cluster's income is
                    // USEFUL right now (economic/payback price that separately). Without this,
                    // HasMeaningfulBaseBenefit only ever sees direct income/payback/global-effect —
                    // a Base with no immediate useful gain can never originate even when it is the
                    // only way to reach a whole new part of the map.
                    float expansion = TaskScoreEvaluator.EconomicExpansionValue(
                        site.NewResourceClusterHexes / AiConfigV2.economyBaseExpansionClusterFullCount);
                    float cardPrice = TaskScoreEvaluator.CardPrice(
                        card.EffectivePlayApCost, resourceCost);
                    float risk = TaskScoreEvaluator.HexThreatRisk(facts.Exposure);
                    // InfrastructureActions.TryFoundBase carries the extraction facilities
                    // into the new Base: their production is preserved, not lost.

                    var siteOnlyScore = new TaskScore(
                        economicHexBenefit: economic,
                        payback: payback,
                        airfield: airfield,
                        globalCardEffect: global,
                        frontProgress: front,
                        corridorAlignment: corridor,
                        ownTerritoryProximity: proximity,
                        terrainDefense: defense,
                        cardPrice: cardPrice,
                        hexThreatRisk: risk,
                        economicExpansionValue: expansion);
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
                    float heroCost = EconomyMissionOpportunityCost(builder, activeIntents);
                    float assignmentAp = builder?.TotalAssignmentApCost ?? card.EffectivePlayApCost;
                    float extraAp = Mathf.Max(0f, assignmentAp - card.EffectivePlayApCost);
                    var score = new TaskScore(
                        economicHexBenefit: economic,
                        payback: payback,
                        airfield: airfield,
                        globalCardEffect: global,
                        frontProgress: front,
                        corridorAlignment: corridor,
                        ownTerritoryProximity: proximity,
                        terrainDefense: defense,
                        cardPrice: cardPrice,
                        delivery: extraAp * AiConfigV2.taskScoreReactivationApWeight,
                        moverOpportunityCost: Mathf.Max(0f, heroCost),
                        hexThreatRisk: risk,
                        economicExpansionValue: expansion);
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
                        EconomyThreatExposure = facts.Exposure,
                        EconomyHeroOpportunityCost = readyLossToNewHero ? 0f : heroCost,
                        EconomyAssignmentApCost = readyLossToNewHero
                            ? card.EffectivePlayApCost : assignmentAp,
                        EconomyPaybackTurns = paybackTurns,
                        EconomyPreferredBuilderArmyId = readyLossToNewHero
                            ? null : builder?.Army.ArmyId,
                        EconomyProjectedActivationApCost = readyLossToNewHero ? 0
                            : builder?.ProjectedActivationApCost ?? 0,
                        EconomyProjectedMaxMovement = readyLossToNewHero ? 0
                            : builder?.ProjectedMaxMovement ?? 0,
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
                            + $"moverOpp={heroCost:0.##} risk={risk:0.##}"
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

            MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                && i.Kind == MissionKind.Economy && i.Status == IntentStatus.Active
                && i.Economy?.Kind == EconomyTaskKind.FoundBase
                && i.Economy.BuildCard != null && i.PreferredMoverArmyId.HasValue);

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
            MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                && i.Kind == MissionKind.Economy && i.Status == IntentStatus.Active
                && i.Economy?.Kind == EconomyTaskKind.FoundBase
                && i.Economy.BuildCard != null && i.PreferredMoverArmyId.HasValue);
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
            incumbent != null && incumbent.Status == IntentStatus.Active
            && incumbent.Kind == MissionKind.Economy
            && incumbent.Economy?.Kind == EconomyTaskKind.FoundBase
            && incumbent.PreferredMoverArmyId.HasValue
            && incumbent.Economy.BuildCard != null
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
            return intents.Any(i => MissionContinuityLayer.HoldsEconomyBuildSite(i, target.Value)
                && i.Economy.Kind == EconomyTaskKind.FoundBase
                && (i.Economy.BuildCard == null || i.Economy.BuildCard == card));
        }

        private static bool HasActiveEconomyBuildIntent(
            IReadOnlyList<MissionIntent> intents, AxisDemand demand)
        {
            if (demand?.TargetHex == null || intents == null)
                return false;
            EconomyTaskKind kind = demand.Capability == CapabilityKind.EconomicExpansionBase
                ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
            return intents.Any(i => MissionContinuityLayer.HoldsEconomyBuildSite(i, demand.TargetHex.Value)
                && i.PreferredMoverArmyId.HasValue && i.Economy.Kind == kind
                && (kind == EconomyTaskKind.FoundBase
                    || i.Economy.ResourceType == demand.EconomyResourceType));
        }

        private static bool HasStructuralEconomyBuilderRoute(WorldSnapshot snap, HexCoord target,
            IReadOnlyList<EconomyBuilderRouteSnapshot> routes)
        {
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = routes
                ?? SnapshotFallbackRoutes(snap, target);
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
