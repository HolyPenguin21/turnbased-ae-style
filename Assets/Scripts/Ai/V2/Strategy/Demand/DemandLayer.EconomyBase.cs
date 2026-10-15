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
    // Base-site Economy demand: candidate sites, the current-commitment selection and its
    // retarget rule. The same DemandLayer owner as DemandLayer.Economy.cs.
    public static partial class DemandLayer
    {
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
            IMissionIntentView intentState = s.Intents;
            var meaningfulDemands = new List<AxisDemand>();

            foreach (EconomyBaseOpportunity site in s.Economy.BaseOpportunities)
                foreach (CardData card in baseCards)
                {
                    considered++;
                    if (intentState != null
                        && intentState.Economy.IsBaseExpansionDeliverySuppressed(s.TurnNumber, card, site.Hex))
                    {
                        thresholdRejected++;
                        continue;
                    }
                    if (HasActiveEconomyIntentAtHexOfKind(activeIntents, site.Hex,
                        EconomyTaskKind.BuildExtraction))
                        continue;

                    bool committed = IsActiveBaseCommitment(activeIntents, site.Hex, card);
                    BaseSiteValuation.BaseSiteValue facts =
                        BaseSiteValuation.ScoreBaseSite(s, site, card);

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
                        float typeGain = BaseSiteValuation.BaseCardMarginalGain(
                            s, site, card.Definition, type);
                        if (typeGain <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        EconomyResourceStanding standing = s.Economy.PerType
                            .FirstOrDefault(x => x.Type == type);
                        float usefulTypeGain = standing.UsefulMarginalIncomeGain(typeGain);
                        if (usefulTypeGain <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        float priority = TaskScoreEvaluator.ResourcePriority(standing);
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
                        EconomyContinuity.HoldsEconomyBuildSite(i, site.Hex)
                        && i.Economy.Kind == EconomyTaskKind.FoundBase
                        && (i.Economy.BuildCard == null || i.Economy.BuildCard == card)
                        && i.PreferredMoverArmyId.HasValue) : null;
                    EconomyBuilderSelection.EconomyBuilderChoice builder = EconomyBuilderSelection.SelectEconomyBuilder(
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
                    float heroCost = EconomyBuilderSelection.EconomyMissionOpportunityCost(builder, activeIntents, site.Hex);
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
                        EconomyEscortArmyId = builder == null
                            ? EconomyEscortRecipient(s, site.Hex, site.BuilderRoutes,
                                activeIntents, commitments, card.EffectivePlayApCost)?.ArmyId : null,
                        EconomyReadyDeliveryCost = readyLossToNewHero
                            ? ReadyDeliveryCost(extraAp, heroCost) : (float?)null,
                        EconomyReadyBuilderIsSupport = EconomyBuilderSelection.EconomyBuilderIsSupport(builder),
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

        // Rank sites per delivery identity, applying hysteresis only to the incumbent's exact
        // card/actor. Then choose one deliverable alternative per physical card. Separate cards
        // remain independent projects; the caller still arbitrates shared hexes and actors.
        internal static List<AxisDemand> SelectBaseDemandsForCurrentCommitment(
            IReadOnlyList<AxisDemand> ranked, IReadOnlyList<MissionIntent> activeIntents)
        {
            var result = new List<AxisDemand>();
            if (ranked == null || ranked.Count == 0)
                return result;

            MissionIntent incumbent = activeIntents?.FirstOrDefault(IsRetargetableBaseCommitment);

            foreach (var group in ranked.GroupBy(d => (d.EconomyBuildCard, d.EconomyPreferredBuilderArmyId,
                d.EconomyEscortArmyId)))
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
            // Only alternatives for the SAME physical card use preparation readiness as a tie
            // breaker before optimistic site value. Other projects/axes still compete on Value.
            return result.GroupBy(d => d.EconomyBuildCard).Select(group => group
                .OrderByDescending(d => IsActiveBaseCommitment(activeIntents, d.TargetHex, d.EconomyBuildCard))
                .ThenByDescending(d => incumbent != null && d.EconomyBuildCard == incumbent.Economy.BuildCard
                    && d.EconomyPreferredBuilderArmyId == incumbent.PreferredMoverArmyId)
                .ThenByDescending(d => d.EconomyPreferredBuilderArmyId.HasValue ? 2
                    : d.EconomyEscortArmyId.HasValue ? 1 : 0)
                .ThenByDescending(d => d.Value).First()).ToList();
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
    }
}
