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
    // EconomyDemands and every Economy-only private helper (AddBaseCandidates, EconomyHeroPrerequisite, HasActiveEconomyBuildIntent, IsActiveBaseCommitment, HasActiveEconomyIntentAtHexOfKind, EconomyBuilderChoice, SelectEconomyBuilder, and the rest of the Economy vertical slice).
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
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
                float resourcePriority = EconomyResourcePriority(rs);
                CardDefinition def = ExtractionDefinition(ctx, site.ResourceType);
                if (ctx?.GameConfig != null && def == null)
                    continue;
                float starvation = Mathf.Max(rs.StarvationPressure,
                    ResourceStarvationRegistry.Pressure(player, site.ResourceType));
                resourcePriority = Mathf.Max(resourcePriority, starvation);
                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                float resourceCost = StrategicCardEvaluator.ResourceCostSum(def?.resourceCost);
                float preliminaryPayback = EconomyPaybackTurns(
                    gain, resourceCost, def?.apCost ?? 0f);
                float preliminaryValue = ScoreEconomySite(
                    resourcePriority, gain,
                    site.BaseNetworkSynergy, site.NearbyResourceClusterValue,
                    0f, 0f, 0f, resourceCost, def?.apCost ?? 0f,
                    preliminaryPayback);
                EconomyBuilderChoice builder = SelectEconomyBuilder(
                    s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                    preliminaryValue, def?.apCost ?? 0f, includeReturn: true);
                float travel = builder?.Route.TravelCost
                    ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                float exposure = StrategicCardEvaluator.ThreatExposure(s, site.Hex);
                float opportunity = EconomyMissionOpportunityCost(builder, activeIntents);
                float assignmentAp = builder?.TotalAssignmentApCost ?? (def?.apCost ?? 0f);
                float payback = EconomyPaybackTurns(gain, resourceCost, assignmentAp);
                if (payback > AiConfigV2.economyExtractionMaxPaybackTurns)
                {
                    rejectedPayback++;
                    continue;
                }
                float strategicValue = ScoreEconomySite(
                    resourcePriority, gain,
                    site.BaseNetworkSynergy, site.NearbyResourceClusterValue,
                    0f, exposure, 0f, resourceCost, def?.apCost ?? 0f,
                    preliminaryPayback);
                float deliveryApCost = Mathf.Max(0f,
                    assignmentAp - (def?.apCost ?? 0f));
                float value = strategicValue
                    - AiConfigV2.economyBuildApPenalty * deliveryApCost
                    - AiConfigV2.economySiteTravelPenalty * Mathf.Max(0f, travel)
                    - Mathf.Max(0f, opportunity);
                if (strategicValue <= AiConfigV2.allocatorSliceEpsilon)
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
                    EconomySiteValue = strategicValue,
                    EconomyTravelCost = travel,
                    EconomyThreatExposure = exposure,
                    EconomyHeroOpportunityCost = opportunity,
                    EconomyAssignmentApCost = assignmentAp,
                    EconomyPaybackTurns = payback,
                    EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                    EconomyProjectedActivationApCost = builder?.ProjectedActivationApCost ?? 0,
                    EconomyProjectedMaxMovement = builder?.ProjectedMaxMovement ?? 0,
                    EconomyBuilderRoutes = site.BuilderRoutes,
                    Value = value,
                    Explain = $"{site.ResourceType} deficit={rs.DeficitScore:0.##} "
                        + $"resourcePriority={resourcePriority:0.##} marginalGain={gain:0.#} effectiveYield={site.EffectiveYield} "
                        + $"alreadyCollected={site.CurrentBuildingCollection} "
                        + $"network={site.BaseNetworkSynergy:0.##} "
                        + $"cluster={site.NearbyResourceClusterValue:0.##} "
                        + $"site={strategicValue:0.##} delivery={value:0.##} "
                        + $"travel={travel:0.#} exposure={exposure:0.##} "
                        + $"heroCost={opportunity:0.##}",
                });
            }

            string baseSummary = AddBaseCandidates(
                s, candidates, player, ctx, activeIntents, commitments,
                out int baseNoBuilder, out int baseStrategicValue,
                out int baseDeliveryValue, out int baseThreshold);
            // Resource need is a strategic decision; builder convenience chooses a site only
            // after a resource has survived feasibility/payback filtering. This prevents a scout
            // standing on a low-priority resource from silently replacing the hand bottleneck.
            IOrderedEnumerable<AxisDemand> extractionRanked = candidates
                .Where(x => x.Capability == CapabilityKind.EconomicInfrastructure
                    && x.EconomyResourceType.HasValue
                    // A FoundBase intent already owns this hex — extraction must not propose a
                    // competing build on the same target.
                    && !HasActiveEconomyIntentAtHexOfKind(
                        activeIntents, x.TargetHex, EconomyTaskKind.FoundBase))
                // A builder already committed and en route (or standing) on this target must not
                // lose its slot to .Take(N) just because some other resource's priority ticked up
                // this pass — mirrors baseRanked's IsActiveBaseCommitment precedence below.
                .OrderByDescending(x => HasActiveEconomyBuildIntent(activeIntents, x) ? 1 : 0)
                .ThenByDescending(x => standings.TryGetValue(
                        x.EconomyResourceType.Value, out EconomyResourceStanding rs)
                    ? Mathf.Max(EconomyResourcePriority(rs),
                        ResourceStarvationRegistry.Pressure(
                            player, x.EconomyResourceType.Value))
                    : 0f)
                .ThenByDescending(x => x.EconomySiteValue)
                .ThenByDescending(x => x.EconomyExpectedIncomeGain)
                .ThenBy(x => x.EconomyTravelCost)
                .ThenBy(x => x.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(x => x.TargetHex?.R ?? int.MaxValue);
            IOrderedEnumerable<AxisDemand> baseRanked = candidates
                .Where(x => x.Capability == CapabilityKind.EconomicExpansionBase
                    // An active BuildExtraction intent already owns this hex — a fresh Base
                    // candidate must not propose converting/competing for the same target while
                    // that extraction is still in flight.
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
            List<AxisDemand> selected = extractionRanked
                .Take(Mathf.Max(0, AiConfigV2.economyMaxInfrastructureDemandsPerTurn))
                .Concat(baseRanked.Take(
                    Mathf.Max(0, AiConfigV2.economyMaxExpansionBaseDemandsPerTurn)))
                .ToList();
            // One existing builder and one physical Base card can justify only one operation
            // in this admission. Commitment wins; otherwise compare full delivered merit.
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
                // A Phase-A Hero handoff already has one concrete actor and target owned by
                // Continuity. If that actor is temporarily composition-ineligible, its durable
                // mission must retry/defer; requesting another Hero for the same operation would
                // grow the roster every settled pass and create a second owner for one need.
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
            // Preserve the exact operation through Hero materialization. H/E/M/T stay free until
            // a builder route exists because deferred reservations never admit Hero capability.
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
            float buildValue, float buildApCost, bool includeReturn)
        {
            return RankEconomyBuilders(snap, target, routes, activeIntents, commitments,
                buildValue, buildApCost, includeReturn).FirstOrDefault();
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
                // A home-vocation hero (HeroRoleEvaluator — low MoveMax / Researcher / Assembler /
                // ApBonus) is worth more standing garrison duty than travelling to build, but that
                // preference must stay a bounded ranking cost, not an absolute veto a large
                // travel-cost gap can never overturn — a home hero one step away must still beat a
                // field hero eight steps away. Folded into the AP-cost tiebreaker itself (a sort-key
                // adjustment only: the real TotalAssignmentApCost on the winning choice, which flows
                // into AxisDemand.EconomyAssignmentApCost / delivery telemetry, is left untouched).
                // Gated on !IsOnTarget: a home hero building right on its own garrison hex isn't
                // travelling anywhere, so there is nothing here to protect it from.
                .ThenBy(x => x.TotalAssignmentApCost
                    + (!x.Route.IsOnTarget && x.Army?.HeroIsHomeVocation == true
                        ? AiConfigV2.economyHomeHeroAssignmentApPenalty : 0f))
                .ThenBy(x => x.Route.EffectiveArmyPower)
                .ThenBy(x => x.Route.ArmySize)
                .ThenBy(x => x.Route.TravelCost + (includeReturn ? x.Route.ReturnTravelCost : 0))
                .ThenBy(x => x.Route.ArmyId)
                .ToList();
        }

        // Analysis replaces snapshots on every operational/knowledge refresh. Reuse only exact
        // read-only assessments within that snapshot; weak keys cannot retain old turns/players.
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

                // A garrison-hero-extraction row prices only the one sparable hero (already
                // computed in WorldAnalysis.Economy.EconomyBuilderRoutes via AiArmyRoles.
                // BestSparableEconomyHero) — `army` here is still the GARRISON's own full-roster
                // snapshot, which does not describe that hero's future 1-member field roster, so
                // none of the below escort-augmentation math (built for an already-separate field
                // army) applies. Escort, if any is warranted, is decided the normal way afterward by
                // ProvisioningManager.PlanEconomyArmyLightening once the hero is a real separate
                // ArmyData — same as for every other freshly formed economy mover today.
                if (route.RequiresGarrisonExtraction)
                {
                    choice.Suitability = EconomyArmySuitability.Ready;
                    choice.IneligibleReason = null;
                    choice.MinimumEscortCount = 0;
                    choice.ProjectedActivationApCost = route.ActivationApCost;
                    choice.ProjectedMaxMovement = route.MaxMovement;
                    return choice;
                }

                // A known NEUTRAL sighting on the route only ever means "occupies that one hex"
                // (WorldAnalysis.Economy.KnownThreatsAffectingEconomyRoute already keeps it off the
                // route unless the mover would have to stand on it — SafeStepPathing separately
                // refuses to path through it at all). It is a stationary, non-chasing blocker: an
                // economy mover routing past/near it is never forced to fight it, unlike a real
                // enemy player army, which can reposition to intercept. Only enemy sightings should
                // demand a roster that can win the fight — a neutral must never gate builder
                // eligibility on combat strength this early stage has no aggression capability to
                // provide yet.
                List<AiMapMemory.KnownEnemySighting> threats = (route.RouteThreats
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Where(t => t.Owner?.IsNeutral != true)
                    .ToList();
                bool atBase = snap?.Self?.BaseHexes?.Contains(army.Hex) == true;
                // Analysis already attached honestly-witnessed threats that can affect the exact
                // SafeStepPathing route. A clean route is evidence, not a proximity guess, and remains
                // the fog-honest answer. No separate base-adjacency requirement on top of it.
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
                    // Field composition is immutable for Economy: a suitable field army travels as
                    // one actor and must be priced whole. Only a Base/Citadel candidate may project
                    // the minimum retained subset that Provisioning can actually unload atomically.
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

                // Field rosters are immutable for Economy. A deficient field army is rejected here,
                // before its AP reaches Allocation. Only a Base/Citadel garrison may supply the exact
                // minimum missing escort.
                if (!atBase || snap?.Self?.Armies == null)
                    return choice;
                ArmySnapshot garrison = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.IsGarrison && a.Hex.Equals(army.Hex));
                // Mirror ProvisioningManager.PlanEconomyArmyLightening's hard gate here: a garrison
                // already activated this turn cannot actually hand over an escort, so do not score
                // ReinforceAtBase as viable and let Provisioning discover that as AssemblyInfeasible
                // (which also burns a 2-turn structural cooldown on the whole delivery for nothing).
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
            int paidOutboundActivations = Mathf.Max(0,
                outboundTurns - (route.HasActivatedThisTurn && outboundTurns > 0 ? 1 : 0));
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
                    // Only a garrison-hero-extraction row (WorldAnalysis.Economy.
                    // EconomyBuilderRoutes) may reach this point off-target — every other Garrison
                    // row was already handled above or never generated in the first place.
                    if (!route.RequiresGarrisonExtraction)
                        continue;
                }
                else if (!army.IsMobileEconomyBuilder)
                {
                    continue;
                }

                MissionIntent assignment = ActiveAssignment(activeIntents, army.ArmyId);
                bool claimed = commitments != null && commitments.IsArmyClaimed(army.ArmyId);
                if (assignment != null)
                {
                    if (assignment.Kind == MissionKind.Economy)
                    {
                        if (assignment.Economy == null
                            || !assignment.Economy.TargetHex.Equals(target))
                            continue;
                    }
                    else if (!EconomyDonorStructurallyEligible(assignment))
                    {
                        continue;
                    }
                }
                if (claimed && assignment == null)
                    continue;
                if (EconomyBuilderUnderImmediateThreat(snap, army.Hex))
                    continue;
                yield return (route, army);
            }
        }

        // Tests and snapshot-only simulations may construct opportunities without the production
        // Analysis route list. Preserve their structural semantics without any live-registry read.
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

        // Economy borrows an actor, not its entire combat value. Idle/current-Economy builders
        // lose no active mission; a permitted Recon/Raid loan pays the existing continuation loss.
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
            if (donor == null || (donor.Funding != CommitmentTier.None
                && donor.Funding != CommitmentTier.Soft))
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
                - Mathf.Max(0, routeCost) * AiConfigV2.economySiteTravelPenalty;
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

        private static float EconomyResourcePriority(EconomyResourceStanding standing)
        {
            float handShortfall = standing.HandResourceNeed <= AiConfigV2.allocatorSliceEpsilon
                ? 0f
                : Mathf.Clamp01((standing.HandResourceNeed - standing.SpendableStockpile)
                    / standing.HandResourceNeed);
            float operationalShortfall =
                standing.ReservedOperationalNeed <= AiConfigV2.allocatorSliceEpsilon
                    ? 0f
                    : Mathf.Clamp01((standing.ReservedOperationalNeed
                            - standing.SpendableStockpile)
                        / standing.ReservedOperationalNeed);
            return Mathf.Max(standing.DeficitScore, standing.StarvationPressure,
                handShortfall, operationalShortfall);
        }

        internal static float ScoreEconomySite(float deficit, float expectedIncomeGain,
            float baseNetworkSynergy, float nearbyResourceClusterValue, float travelCost,
            float threatExposure, float heroOpportunityCost, float resourceCost,
            float assignmentApCost, float paybackTurns) =>
            AiConfigV2.economySiteDeficitValue * Mathf.Clamp01(deficit)
            + AiConfigV2.economySiteIncomeGainValue * Mathf.Max(0f, expectedIncomeGain)
            + AiConfigV2.economySiteBaseSynergyValue * Mathf.Clamp01(baseNetworkSynergy)
            + AiConfigV2.economySiteClusterValue * Mathf.Max(0f, nearbyResourceClusterValue)
            + AiConfigV2.economyExtractionPaybackValue
                * Mathf.Clamp01(1f - paybackTurns / AiConfigV2.economyExtractionMaxPaybackTurns)
            - AiConfigV2.economyBuildResourcePenalty * Mathf.Max(0f, resourceCost)
            - AiConfigV2.economyBuildApPenalty * Mathf.Max(0f, assignmentApCost)
            - AiConfigV2.economySiteTravelPenalty * Mathf.Max(0f, travelCost)
            - AiConfigV2.economySiteThreatPenalty * Mathf.Clamp01(threatExposure)
            - AiConfigV2.economySiteHeroOpportunityPenalty * Mathf.Max(0f, heroOpportunityCost);

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
                    if (HasActiveEconomyIntentAtHexOfKind(activeIntents, site.Hex, EconomyTaskKind.BuildExtraction))
                        continue;
                    bool committed = IsActiveBaseCommitment(activeIntents, site.Hex, card);
                    StrategicCardEvaluator.BaseSiteValue score =
                        StrategicCardEvaluator.ScoreBaseSite(s, site, card);
                    float hexYield = score.HexYield;
                    float global = score.GlobalEffect;
                    float airfield = score.Airfield;
                    float reasonValue = score.ReasonValue;
                    bool meaningful = reasonValue > AiConfigV2.allocatorSliceEpsilon || committed;
                    if (!meaningful)
                    {
                        strategicValueRejected++;
                        continue;
                    }

                    EconomyBuilderChoice builder = SelectEconomyBuilder(
                        s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                        reasonValue, card.EffectivePlayApCost, includeReturn: false);
                    bool structuralRoute = site.PreparationTravelCost < int.MaxValue
                        || HasStructuralEconomyBuilderRoute(s, site.Hex, site.BuilderRoutes);
                    if (!structuralRoute)
                    {
                        noBuilder++;
                        continue;
                    }

                    float travel = builder?.Route.TravelCost
                        ?? site.PreparationTravelCost;
                    float exposure = score.Exposure;
                    float heroCost = EconomyMissionOpportunityCost(builder, activeIntents);
                    float assignmentAp = builder?.TotalAssignmentApCost
                        ?? card.EffectivePlayApCost;
                    float intrinsicBuildCost = score.IntrinsicBuildCost;
                    float deliveryApCost = Mathf.Max(0f,
                            assignmentAp - card.EffectivePlayApCost)
                        * AiConfigV2.economyBuildApPenalty;
                    float extractionLossPenalty = score.ExtractionLossPenalty;
                    float strategicValue = score.StrategicValue;
                    float value = strategicValue - deliveryApCost
                        - AiConfigV2.economySiteTravelPenalty * travel
                        - Mathf.Max(0f, heroCost);
                    AiDebugLog.WriteVerbose($"[AI][V2][Economy][BaseCandidate] "
                        + $"card={card.Definition.displayName} target=({site.Hex.Q},{site.Hex.R}) "
                        + $"reason={reasonValue:0.##} buildCost={intrinsicBuildCost:0.##} "
                        + $"deliveryApCost={deliveryApCost:0.##} extractionLoss={extractionLossPenalty:0.##} "
                        + $"site={strategicValue:0.##} "
                        + $"delivery={value:0.##} committed={committed} decision=stage");

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
                        EconomyExpectedIncomeGain = site.HexYield.Sum,
                        EconomySiteValue = strategicValue,
                        EconomyTravelCost = travel,
                        EconomyThreatExposure = exposure,
                        EconomyHeroOpportunityCost = heroCost,
                        EconomyAssignmentApCost = assignmentAp,
                        EconomyPreferredBuilderArmyId = builder?.Army.ArmyId,
                        EconomyProjectedActivationApCost = builder?.ProjectedActivationApCost ?? 0,
                        EconomyProjectedMaxMovement = builder?.ProjectedMaxMovement ?? 0,
                        EconomyBuilderRoutes = site.BuilderRoutes,
                        Value = value,
                        Explain = $"Base reason={reasonValue:0.##} "
                            + $"yield={hexYield:0.##} "
                            + $"pressure={site.InfrastructurePressure:0.##} airfield={airfield:0.##} "
                            + $"forward={site.ForwardProgressValue:0.##} "
                            + $"corridor={site.CorridorAlignmentValue:0.##} global={global:0.##} "
                            + $"buildCost={intrinsicBuildCost:0.##} deliveryApCost={deliveryApCost:0.##} "
                            + $"extractionLoss={extractionLossPenalty:0.##}",
                    });
                }

            // Stage the best meaningful, legal and safely-routable Base before value admission.
            // This is what lets the existing continuity urgency accumulate from a negative score.
            // An active commitment (a mission already delivering an actor there) still wins
            // outright — that is real in-flight work, not a candidate preference. Below that,
            // "already staged" is only a hysteresis bonus on top of Value, not a categorical
            // priority tier: a stale staged hex (e.g. yield=0) must still lose to a newly known
            // site once that site's Value clears the staged one by more than the threshold, so
            // urgency can no longer keep compounding on a target that real information has
            // superseded. A small margin stays inside the threshold and does not flip staging.
            AxisDemand stagedBase = meaningfulDemands
                .OrderByDescending(d => IsActiveBaseCommitment(
                    activeIntents, d.TargetHex, d.EconomyBuildCard) ? 1 : 0)
                .ThenByDescending(d => d.Value
                    + (intentState.IsStagedBaseExpansion(d.EconomyBuildCard, d.TargetHex)
                        ? AiConfigV2.economyBaseSwitchHysteresisThreshold : 0f))
                .ThenByDescending(d => d.EconomySiteValue)
                .ThenBy(d => d.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(d => d.TargetHex?.R ?? int.MaxValue)
                .FirstOrDefault();
            bool urgencyEligible = stagedBase?.TargetHex != null;
            float urgency = intentState.MarkBaseExpansionCandidate(s.TurnNumber, stagedBase?.EconomyBuildCard,
                    stagedBase?.TargetHex, urgencyEligible);

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
                    + $"target=({best.TargetHex?.Q},{best.TargetHex?.R}) value={best.EconomySiteValue:0.##} "
                    + $"wait={intentState.BaseExpansionWaitTurns} urgency={urgency:0.##}";
        }

        // Cross-family guard: extraction and base candidates are ranked/selected independently
        // (see EconomyDemands), so nothing else stops a fresh candidate of one family from
        // targeting a hex already owned by an active intent of the OTHER family. This is the
        // only place that checks across EconomyTaskKind.
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

        // ---------------------------------------------------------------------------------------
        //  DEV — three staged shapes (the radar no longer gates on facility+hero; the demand layer
        //  bootstraps each missing prerequisite the way Recon bootstraps a scout):
        //    · no Research/Production facility yet -> ONE DevelopmentInfrastructure gap demand.
        //    · facility built but UNSTAFFED -> ONE DevelopmentOperator demand @the facility hex
        //      (play a Research/Production hero card onto it). No offerings exist without an
        //      operator, so CardUpgrade is not emitted this turn.
        //    · facility staffed -> ONE CardUpgrade demand PER scored DevelopmentOpportunity, each
        //      carrying its opportunity handle. Phase A runs the carried opportunity verbatim.
        // ---------------------------------------------------------------------------------------
    }
}

