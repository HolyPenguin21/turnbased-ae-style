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
            int rejectedStrategicValue = 0;
            int rejectedDeliveryValue = 0;

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

                float resourceCost = StrategicCardEvaluator.ResourceCostSum(def?.resourceCost);
                float cardAp = def?.apCost ?? 0f;
                float payback = EconomyPaybackTurns(gain, resourceCost, cardAp);
                if (payback > AiConfigV2.economyExtractionMaxPaybackTurns)
                {
                    rejectedPayback++;
                    continue;
                }

                float exposure = StrategicCardEvaluator.ThreatExposure(s, site.Hex);
                int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(s, site.Hex);
                var siteOnlyScore = new TaskScore(
                    economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(gain, resourcePriority),
                    payback: TaskScoreEvaluator.Payback(payback),
                    ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                    cardPrice: TaskScoreEvaluator.CardPrice(cardAp, resourceCost),
                    hexThreatRisk: TaskScoreEvaluator.HexThreatRisk(exposure));

                // Continuity owns the actor for an existing objective. A later, cheaper builder
                // must not supply a different delivery cost for that same durable operation.
                MissionIntent pinnedExtraction = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.Economy
                    && i.Economy?.Kind == EconomyTaskKind.BuildExtraction
                    && i.Economy.TargetHex.Equals(site.Hex)
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
                TaskScoreDiagnostics.Log("Extraction", site.Hex, score,
                    $"resource={site.ResourceType} priority={resourcePriority:0.###} "
                    + $"marginalGain={gain:0.###} paybackTurns={payback:0.###} cardAp={cardAp:0.###} "
                    + $"resourceCost={resourceCost:0.###} distance={travel:0.###} extraAp={extraAp:0.###} "
                    + $"exposure={exposure:0.###} moverOpportunity={opportunity:0.###}");

                if (siteOnlyScore.Value <= AiConfigV2.allocatorSliceEpsilon)
                {
                    rejectedStrategicValue++;
                    continue;
                }
                if (value <= AiConfigV2.allocatorSliceEpsilon)
                {
                    if (builder == null) rejectedNoBuilder++;
                    else rejectedDeliveryValue++;
                    continue;
                }

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
                    EconomyHeroOpportunityCost = opportunity,
                    EconomyAssignmentApCost = assignmentAp,
                    EconomyPaybackTurns = payback,
                    EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                    EconomyProjectedActivationApCost = builder?.ProjectedActivationApCost ?? 0,
                    EconomyProjectedMaxMovement = builder?.ProjectedMaxMovement ?? 0,
                    EconomyBuilderRoutes = site.BuilderRoutes,
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"{site.ResourceType} task={score.Value:0.##} priority={resourcePriority:0.##} "
                        + $"marginalGain={gain:0.##} payback={payback:0.##} "
                        + $"travel={travel:0.##} exposure={exposure:0.##} moverOpp={opportunity:0.##}",
                });
            }

            string baseSummary = AddBaseCandidates(
                s, candidates, player, ctx, activeIntents, commitments,
                out int baseNoBuilder, out int baseStrategicValue,
                out int baseDeliveryValue, out int baseThreshold);

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
                .ThenByDescending(x => x.Value + x.EconomyStrategicUrgency)
                .ThenByDescending(x => x.EconomySiteValue)
                .ThenByDescending(x => x.EconomyExpectedIncomeGain)
                .ThenBy(x => x.EconomyTravelCost)
                .ThenBy(x => x.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(x => x.TargetHex?.R ?? int.MaxValue);

            // Select a Base challenger BEFORE the cap of one. That cap governs executable
            // demands, not the number of sites permitted into the hysteresis comparison.
            // Only a candidate that can reuse the incumbent's EXACT card and actor may
            // replace a live commitment; changing actors requires separate provisioning.
            AxisDemand selectedBase = SelectBaseDemandForCurrentCommitment(
                baseRanked.ToList(), activeIntents);
            List<AxisDemand> selected = extractionRanked
                .Take(Mathf.Max(0, AiConfigV2.economyMaxInfrastructureDemandsPerTurn))
                .Concat(selectedBase != null
                    && AiConfigV2.economyMaxExpansionBaseDemandsPerTurn > 0
                        ? new[] { selectedBase } : System.Array.Empty<AxisDemand>())
                .ToList();

            var selectedHexes = new HashSet<HexCoord>();
            var selectedBuilders = new HashSet<int>();
            var selectedCards = new HashSet<CardData>();
            selected = selected
                .OrderByDescending(d => HasActiveEconomyBuildIntent(activeIntents, d))
                .ThenByDescending(d => d.Value + d.EconomyStrategicUrgency)
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
                    AiDebugLog.Write($"[AI][V2][Economy][Demand] selected=continuity "
                        + $"target=({demand.TargetHex?.Q},{demand.TargetHex?.R}) "
                        + "reason=existing_target_specific_actor");
                    continue;
                }
                AxisDemand emitted = demand.EconomyPreferredBuilderArmyId.HasValue
                    ? demand
                    : EconomyHeroPrerequisite(demand);
                AiDebugLog.Write($"[AI][V2][Economy][Demand] selected={emitted.Capability} "
                    + $"resource={emitted.EconomyResourceType?.ToString() ?? "none"} "
                    + $"target=({emitted.TargetHex?.Q},{emitted.TargetHex?.R}) value={emitted.Value:0.##} "
                    + $"rejected={Mathf.Max(0, candidates.Count - selected.Count)}");
                yield return emitted;
            }

            AiDebugLog.Write($"[AI][V2][Economy][BaseCandidates] {baseSummary}");
            int rejectionTotal = rejectedNoBuilder + baseNoBuilder + rejectedPayback
                + rejectedStrategicValue + baseStrategicValue
                + rejectedDeliveryValue + baseDeliveryValue + baseThreshold;
            AiDebugLog.Write($"[AI][V2][Economy][Rejections] no_builder={rejectedNoBuilder + baseNoBuilder} "
                + $"payback={rejectedPayback} strategic_value={rejectedStrategicValue + baseStrategicValue} "
                + $"delivery_value={rejectedDeliveryValue + baseDeliveryValue} threshold={baseThreshold}");
            if (selected.Count == 0)
                AiDebugLog.Write($"[AI][V2][Economy][Demand] selected=none rejected={rejectionTotal} "
                    + "reason=no_legal_valuable_site_or_base");
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
            EconomyStrategicUrgency = source.EconomyStrategicUrgency,
            EconomyPreferredBuilderArmyId = source.EconomyPreferredBuilderArmyId,
            EconomyProjectedActivationApCost = source.EconomyProjectedActivationApCost,
            EconomyProjectedMaxMovement = source.EconomyProjectedMaxMovement,
            EconomyBuilderRoutes = source.EconomyBuilderRoutes,
            WorldTaskScore = source.WorldTaskScore,
            Value = source.Value,
            Explain = source.Explain + "; prerequisite=mobile_hero",
        };

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
            return EconomyBuilderCandidates(snap, target, routes, activeIntents, commitments)
                .Where(x => x.route.IsOnTarget || ActiveAssignment(activeIntents, x.army.ArmyId) == null
                    || ActiveAssignment(activeIntents, x.army.ArmyId).Kind == MissionKind.Economy
                    || EconomyLoanAllowed(ActiveAssignment(activeIntents, x.army.ArmyId), buildValue,
                        x.route.TravelCost, x.army.CurrentMovement, out _))
                .Select(x => AssessEconomyArmy(snap, target, x.route, x.army,
                    buildApCost, includeReturn))
                .Where(x => x.Suitability != EconomyArmySuitability.Ineligible)
                .OrderByDescending(x => x.Route.HasActiveEconomyCommitment
                    || ActiveAssignment(activeIntents, x.Route.ArmyId)?.Kind == MissionKind.Economy)
                .ThenBy(x => x.Suitability == EconomyArmySuitability.Ready ? 0
                    : x.Suitability == EconomyArmySuitability.LightenAtBase ? 1 : 2)
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
                    choice.Suitability = EconomyArmySuitability.Ready;
                    choice.IneligibleReason = null;
                    choice.MinimumEscortCount = 0;
                    choice.ProjectedActivationApCost = route.ActivationApCost;
                    choice.ProjectedMaxMovement = route.MaxMovement;
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
                if (EconomyRosterSafe(current, threats, minimumEscort))
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
                        if (!EconomyRosterSafe(projected, threats, minimumEscort))
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

        internal static bool EconomyRosterSafe(
            IReadOnlyList<WorthIt.DefenderProfile> roster,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            if ((roster?.Count ?? 0) < minimumEscort)
                return false;
            if (threats == null || threats.Count == 0)
                return true;
            if (threats.Any(t => t.Defenders == null || t.Defenders.Count == 0))
                return false;
            return threats.All(t => WorthIt.CanDamageAll(roster, t.Defenders)
                && WorthIt.WinChance(roster, t.Defenders, 0f)
                    >= AiConfig.economyEscortMinWinChance);
        }

        internal static int MinimumSafeEconomyEscortCount(
            IReadOnlyList<WorthIt.DefenderProfile> roster,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort)
        {
            List<WorthIt.DefenderProfile> pool = roster?.ToList()
                ?? new List<WorthIt.DefenderProfile>();
            for (int count = Mathf.Max(0, minimumEscort); count <= pool.Count; count++)
                if (Combinations(pool, count).Any(x =>
                        EconomyRosterSafe(x, threats, minimumEscort)))
                    return count;
            return int.MaxValue;
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
                    if (EconomyRosterSafe(roster, threats, minimumEscort))
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
            int routeCost, int movementAvailable, out float netValue)
        {
            netValue = buildValue - AiConfigV2.economyLoanContinuationLoss
                - Mathf.Max(0, routeCost) * AiConfigV2.taskScoreReactivationApWeight;
            return EconomyDonorStructurallyEligible(donor)
                && routeCost <= movementAvailable
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
            out int deliveryValueRejected, out int thresholdRejected)
        {
            noBuilder = 0;
            strategicValueRejected = 0;
            deliveryValueRejected = 0;
            thresholdRejected = 0;
            List<CardData> baseCards = (s.Self.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition?.cardType == CardType.Base)
                .OrderBy(c => c.Definition.authoredKey ?? c.Definition.displayName)
                .ToList();
            if (baseCards.Count == 0 || s.Economy?.BaseOpportunities == null)
            {
                MissionIntentRegistry.GetOrCreate(player)
                    .MarkBaseExpansionCandidate(s.TurnNumber, null, null,
                        structurallyEligible: false);
                return "considered=0 kept=0 reason=no_base_card_or_opportunity";
            }

            int considered = 0;
            int kept = 0;
            AxisDemand best = null;
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
                    float paybackTurns = economicGainFact > AiConfigV2.allocatorSliceEpsilon
                        ? EconomyPaybackTurns(economicGainFact,
                            StrategicCardEvaluator.ResourceCostSum(card.EffectivePlayResourceCost),
                            card.EffectivePlayApCost)
                        : float.PositiveInfinity;
                    int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(s, site.Hex);
                    float resourceCost = StrategicCardEvaluator.ResourceCostSum(
                        card.EffectivePlayResourceCost);
                    // Base income is multi-resource. Reuse the card-semantic per-type gain owner
                    // and bind each resource's shortage only to its OWN marginal production.
                    var marginalByResource = new List<(float Gain, float Priority)>();
                    foreach (ResourceType type in ResourceBundle.All)
                    {
                        float typeGain = StrategicCardEvaluator.BaseCardMarginalGain(
                            s, site, card.Definition, type);
                        if (typeGain <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        float priority = s.Economy.PerType
                            .Where(x => x.Type == type)
                            .Select(x => TaskScoreEvaluator.ResourcePriority(x,
                                ResourceStarvationRegistry.Pressure(player, type)))
                            .DefaultIfEmpty(0f).First();
                        marginalByResource.Add((typeGain, priority));
                    }
                    float basePriority = marginalByResource.Count == 0 ? 0f
                        : marginalByResource.Max(x => x.Priority);
                    float economic = TaskScoreEvaluator.EconomicHexBenefit(marginalByResource);
                    float payback = economicGainFact > AiConfigV2.allocatorSliceEpsilon
                        ? TaskScoreEvaluator.Payback(paybackTurns) : 0f;
                    float airfield = TaskScoreEvaluator.Airfield(facts.Airfield);
                    float global = Mathf.Clamp(facts.GlobalEffect, 0f,
                        AiConfigV2.taskScoreGlobalCardEffectMax);
                    float front = TaskScoreEvaluator.FrontProgress(site.ForwardProgressValue);
                    float corridor = TaskScoreEvaluator.CorridorAlignment(site.CorridorAlignmentValue);
                    float proximity = TaskScoreEvaluator.OwnTerritoryProximity(homeDistance);
                    float defense = TaskScoreEvaluator.TerrainDefense(site.DefenseBonusValue);
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
                        hexThreatRisk: risk);
                    // Staging is about positive physical/strategic purpose, NOT present-day net
                    // profitability: delivery/card costs may be overcome by future wait urgency.
                    // Generic proximity alone must never stage a completely empty Base.
                    bool meaningful = committed || HasMeaningfulBaseBenefit(siteOnlyScore);
                    if (!meaningful)
                    {
                        strategicValueRejected++;
                        continue;
                    }

                    MissionIntent pinnedBase = committed ? activeIntents?.FirstOrDefault(i =>
                        i != null && i.Status == IntentStatus.Active
                        && i.Kind == MissionKind.Economy
                        && i.Economy?.Kind == EconomyTaskKind.FoundBase
                        && i.Economy.TargetHex.Equals(site.Hex)
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
                        hexThreatRisk: risk);
                    float value = score.Value;

                    TaskScoreDiagnostics.Log("Base", site.Hex, score,
                        $"economicGain={economicGainFact:0.###} resourcePriority={basePriority:0.###} "
                        + $"paybackTurns={(float.IsInfinity(paybackTurns) ? -1f : paybackTurns):0.###} "
                        + $"airfieldRaw={facts.Airfield:0.###} globalRaw={facts.GlobalEffect:0.###} "
                        + $"frontRaw={site.ForwardProgressValue:0.###} corridorRaw={site.CorridorAlignmentValue:0.###} "
                        + $"defenseRaw={site.DefenseBonusValue:0.###} "
                        + $"distance={travel:0.###} extraAp={extraAp:0.###} exposure={facts.Exposure:0.###} "
                        + $"moverOpportunity={heroCost:0.###}");

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
                        EconomyHeroOpportunityCost = heroCost,
                        EconomyAssignmentApCost = assignmentAp,
                        EconomyPaybackTurns = paybackTurns,
                        EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                        EconomyProjectedActivationApCost = builder?.ProjectedActivationApCost ?? 0,
                        EconomyProjectedMaxMovement = builder?.ProjectedMaxMovement ?? 0,
                        EconomyBuilderRoutes = site.BuilderRoutes,
                        WorldTaskScore = score,
                        Value = score.Value,
                        Explain = $"Base task={score.Value:0.##} economic={economic:0.##} "
                            + $"payback={payback:0.##} airfield={airfield:0.##} global={global:0.##} "
                            + $"front={front:0.##} corridor={corridor:0.##} proximity={proximity:0.##} "
                            + $"defense={defense:0.##} "
                            + $"price={cardPrice:0.##} delivery={score.Delivery:0.##} "
                            + $"moverOpp={heroCost:0.##} risk={risk:0.##}",
                    });
                }

            AxisDemand stagedBase = meaningfulDemands
                .OrderByDescending(d => IsActiveBaseCommitment(
                    activeIntents, d.TargetHex, d.EconomyBuildCard) ? 1 : 0)
                .ThenByDescending(d => d.Value
                    + (intentState.IsStagedBaseExpansion(d.EconomyBuildCard, d.TargetHex)
                        ? AiConfigV2.economyBaseStagingHysteresisThreshold : 0f))
                .ThenByDescending(d => d.EconomySiteValue)
                .ThenBy(d => d.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(d => d.TargetHex?.R ?? int.MaxValue)
                .FirstOrDefault();
            bool urgencyEligible = stagedBase?.TargetHex != null;
            float urgency = intentState.MarkBaseExpansionCandidate(s.TurnNumber,
                stagedBase?.EconomyBuildCard, stagedBase?.TargetHex, urgencyEligible);

            foreach (AxisDemand demand in meaningfulDemands)
            {
                bool staged = stagedBase != null
                    && demand.EconomyBuildCard == stagedBase.EconomyBuildCard
                    && demand.TargetHex.Equals(stagedBase.TargetHex);
                float candidateUrgency = staged ? urgency : 0f;
                demand.EconomyStrategicUrgency = candidateUrgency;
                if (candidateUrgency > 0f)
                    demand.Explain += $" urgency={candidateUrgency:0.##}";

                bool committed = IsActiveBaseCommitment(
                    activeIntents, demand.TargetHex, demand.EconomyBuildCard);
                bool admitted = committed || demand.Value + candidateUrgency
                    >= AiConfigV2.economyBaseDemandMinValue;
                AiDebugLog.WriteVerbose($"[AI][V2][Economy][BaseAdmission] "
                    + $"card={demand.EconomyBuildCard?.Definition?.displayName} "
                    + $"target=({demand.TargetHex?.Q},{demand.TargetHex?.R}) "
                    + $"value={demand.Value:0.##} urgency={candidateUrgency:0.##} "
                    + $"committed={committed} decision={(admitted ? "keep" : "defer")}");
                if (!admitted)
                {
                    if (!demand.EconomyPreferredBuilderArmyId.HasValue)
                        noBuilder++;
                    else if (demand.EconomySiteValue <= AiConfigV2.allocatorSliceEpsilon)
                        strategicValueRejected++;
                    else if (demand.Value <= AiConfigV2.allocatorSliceEpsilon)
                        deliveryValueRejected++;
                    else
                        thresholdRejected++;
                    continue;
                }

                output.Add(demand);
                kept++;
                if (best == null || demand.Value + demand.EconomyStrategicUrgency
                    > best.Value + best.EconomyStrategicUrgency)
                    best = demand;
            }

            return best == null
                ? $"considered={considered} kept={kept} best=none "
                    + $"wait={intentState.BaseExpansionWaitTurns} urgency={urgency:0.##}"
                : $"considered={considered} kept={kept} best={best.EconomyBuildCard.Definition.displayName} "
                    + $"target=({best.TargetHex?.Q},{best.TargetHex?.R}) value={best.Value:0.##} "
                    + $"wait={intentState.BaseExpansionWaitTurns} urgency={urgency:0.##}";
        }

        // One Base selection decision owner. The incumbent's current fully delivered score
        // wins over its captured score when a same-card/same-actor candidate is still present.
        // Never compare the challenger's full Value against Economy.BuildValue (site only).
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
            && rival.Value >= AiConfigV2.economyBaseDemandMinValue
            && rival.Value > rival.EconomySwitchIncumbentValue.Value
                + AiConfigV2.economyBaseSwitchHysteresisThreshold;

        // This criterion gates ONLY continuity staging; canonical net-value admission still
        // applies afterwards. Avoid letting the generic home-proximity bonus create fake projects.
        internal static bool HasMeaningfulBaseBenefit(TaskScore score) =>
            score.EconomicHexBenefit > AiConfigV2.allocatorSliceEpsilon
            || score.Payback > AiConfigV2.allocatorSliceEpsilon
            || score.Airfield > AiConfigV2.allocatorSliceEpsilon
            || score.GlobalCardEffect > AiConfigV2.allocatorSliceEpsilon
            || score.FrontProgress > AiConfigV2.allocatorSliceEpsilon
            || score.CorridorAlignment > AiConfigV2.allocatorSliceEpsilon
            || score.TerrainDefense > AiConfigV2.allocatorSliceEpsilon;

        private static bool HasActiveEconomyIntentAtHexOfKind(IReadOnlyList<MissionIntent> intents,
            HexCoord? target, EconomyTaskKind kind)
        {
            if (!target.HasValue || intents == null)
                return false;
            return intents.Any(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.Economy && i.Economy?.Kind == kind
                && i.Economy.TargetHex.Equals(target.Value));
        }

        private static bool IsActiveBaseCommitment(IReadOnlyList<MissionIntent> intents,
            HexCoord? target, CardData card)
        {
            if (!target.HasValue || intents == null)
                return false;
            return intents.Any(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.Economy && i.Economy?.Kind == EconomyTaskKind.FoundBase
                && i.Economy.TargetHex.Equals(target.Value)
                && (i.Economy.BuildCard == null || i.Economy.BuildCard == card));
        }

        private static bool HasActiveEconomyBuildIntent(
            IReadOnlyList<MissionIntent> intents, AxisDemand demand)
        {
            if (demand?.TargetHex == null || intents == null)
                return false;
            EconomyTaskKind kind = demand.Capability == CapabilityKind.EconomicExpansionBase
                ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
            return intents.Any(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId.HasValue
                && i.Economy?.Kind == kind
                && i.Economy.TargetHex.Equals(demand.TargetHex.Value)
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
