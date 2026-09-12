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
                float resourceCost = ResourceCostSum(def?.resourceCost);
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
                float exposure = ThreatExposure(s, site.Hex);
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
            // Fresh-vs-fresh cross-family conflict: the per-list filters above only exclude a
            // candidate that collides with an ACTIVE intent of the other family. On the very
            // first admission for a hex — before either family has an active intent yet — both
            // an extraction and a base candidate can independently pass and land in `selected`
            // together. Keep exactly one per hex: an active intent for either family wins
            // outright (should already be excluded above, kept as a defensive tie-break);
            // otherwise the higher-Value candidate wins.
            if (selected.Count > 1)
            {
                var hexConflicts = selected.Where(d => d.TargetHex.HasValue)
                    .GroupBy(d => d.TargetHex.Value)
                    .Where(g => g.Count() > 1);
                var losers = new HashSet<AxisDemand>();
                foreach (var group in hexConflicts)
                {
                    AxisDemand winner = group
                        .OrderByDescending(d => HasActiveEconomyIntentAtHexOfKind(activeIntents,
                            d.TargetHex, d.Capability == CapabilityKind.EconomicExpansionBase
                                ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction) ? 1 : 0)
                        .ThenByDescending(d => d.Value)
                        .First();
                    foreach (AxisDemand d in group)
                        if (!ReferenceEquals(d, winner))
                            losers.Add(d);
                }
                if (losers.Count > 0)
                    selected = selected.Where(d => !losers.Contains(d)).ToList();
            }
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
                // ApBonus) is worth more standing garrison duty than travelling to build; only send
                // one when no better-suited army qualifies. Preference, not a filter — placed before
                // the AP-cost/travel tiebreakers below so a small cost/distance edge cannot silently
                // override it, but still falls back to the home hero when it is the only candidate.
                // Gated on !IsOnTarget: a home hero building right on its own garrison hex isn't
                // travelling anywhere, so there is nothing here to protect it from.
                .ThenBy(x => !x.Route.IsOnTarget && x.Army?.HeroIsHomeVocation == true ? 1 : 0)
                .ThenBy(x => x.TotalAssignmentApCost)
                .ThenBy(x => x.Route.EffectiveArmyPower)
                .ThenBy(x => x.Route.ArmySize)
                .ThenBy(x => x.Route.TravelCost + (includeReturn ? x.Route.ReturnTravelCost : 0))
                .ThenBy(x => x.Route.ArmyId)
                .ToList();
        }

        private static EconomyBuilderChoice AssessEconomyArmy(WorldSnapshot snap,
            HexCoord target, EconomyBuilderRouteSnapshot route, ArmySnapshot army,
            float buildApCost, bool includeReturn)
        {
            var choice = new EconomyBuilderChoice
            {
                Route = route,
                Army = army,
                TotalAssignmentApCost = EstimateEconomyAssignmentAp(
                    route, buildApCost, includeReturn),
                Suitability = EconomyArmySuitability.Ineligible,
            };
            if (army == null)
                return choice;

            List<AiMapMemory.KnownEnemySighting> threats = EconomyRouteThreats(
                snap, army.Hex, target);
            bool atBase = snap?.Self?.BaseHexes?.Contains(army.Hex) == true;
            // EconomyRouteThreats already scans the whole corridor (direct + detour buffer) against
            // honestly-witnessed sightings — a clean route reported here is not a proximity guess,
            // it is the fog-honest answer. No separate base-adjacency requirement on top of it.
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
                List<int> retained = MinimumSafeEconomyEscortIndices(
                    army, threats, minimumEscort);
                // Field composition is immutable for Economy: a suitable field army travels as
                // one actor and must be priced whole. Only a Base/Citadel candidate may project
                // the minimum retained subset that Provisioning can actually unload atomically.
                if (!atBase)
                    retained = currentIndices;
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
            if (garrison == null || garrison == army || garrison.HasActivatedThisTurn)
                return choice;
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

        internal static List<AiMapMemory.KnownEnemySighting> EconomyRouteThreats(
            WorldSnapshot snapshot, HexCoord from, HexCoord target)
        {
            int direct = HexGridMath.Distance(from, target);
            return (snapshot?.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snapshot?.Known?.NeutralSightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(enemy => HexGridMath.Distance(from, enemy.Hex)
                    + HexGridMath.Distance(enemy.Hex, target) <= direct + 2)
                .ToList();
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
                    >= AiConfig.defenceActiveWinChance);
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
                    List<WorthIt.DefenderProfile> roster = subset.Select(i => pool[i]).ToList();
                    if (!EconomyRosterSafe(roster, threats, minimumEscort))
                        continue;
                    int ap = subset.Sum(i => i < army.NonHeroActivationApCosts.Count
                        ? army.NonHeroActivationApCosts[i] : 0);
                    int move = subset.Count == 0 ? army.HeroMoveMax
                        : subset.Min(i => i < army.NonHeroMoveMax.Count
                            ? army.NonHeroMoveMax[i] : army.MaxMovement);
                    if (best == null || ap < bestAp || (ap == bestAp && move > bestMove))
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
                if (!army.IsMobileEconomyBuilder)
                    continue;

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
                && t.Asset.Hex.Equals(hex) && t.Severity >= AiConfigV2.defenceSeverityTrigger
                && (!t.EnemyEta.HasValue || t.EnemyEta.Value <= 1));

        internal static float EconomyRecoveryThreatExposure(WorldSnapshot snap, HexCoord hex) =>
            ThreatExposure(snap, hex);

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
            var meaningfulDemands = new List<AxisDemand>();

            foreach (EconomyBaseOpportunity site in s.Economy.BaseOpportunities)
                foreach (CardData card in baseCards)
                {
                    considered++;
                    bool committed = IsActiveBaseCommitment(activeIntents, site.Hex, card);
                    float hexYield = BaseHexYieldValue(s, site.HexYield);
                    float global = BaseGlobalEffectValue(s, card.Definition);
                    float airfield = BaseAirfieldValue(s, card.Definition, site.Hex);
                    float reasonValue = AiConfigV2.economyBaseCapacityValue * site.CapacityValue
                        + AiConfigV2.economyBaseHexYieldValue * hexYield
                        + AiConfigV2.economyBaseClusterValue * site.NearbyResourceClusterValue
                        + AiConfigV2.economyBaseNetworkExpansionValue * site.NetworkExpansionValue
                        + AiConfigV2.economyBaseInfrastructurePressureValue * site.InfrastructurePressure
                        + AiConfigV2.economyBaseAirfieldValue * airfield
                        + AiConfigV2.economyBaseLogisticsValue * site.LogisticsValue
                        + AiConfigV2.economyBaseForwardProgressValue * site.ForwardProgressValue
                        + AiConfigV2.economyBaseCorridorAlignmentValue * site.CorridorAlignmentValue
                        + AiConfigV2.economyBaseGlobalEffectValue * global;
                    bool meaningful = reasonValue > AiConfigV2.allocatorSliceEpsilon || committed;
                    if (!meaningful)
                    {
                        strategicValueRejected++;
                        continue;
                    }

                    EconomyBuilderChoice builder = SelectEconomyBuilder(
                        s, site.Hex, site.BuilderRoutes, activeIntents, commitments,
                        reasonValue, card.EffectivePlayApCost, includeReturn: false);
                    bool structuralRoute = HasStructuralEconomyBuilderRoute(
                        s, site.Hex, site.BuilderRoutes);
                    if (!structuralRoute)
                    {
                        noBuilder++;
                        continue;
                    }

                    float travel = builder?.Route.TravelCost
                        ?? AiConfigV2.economyBaseFoundScanRadius + 4f;
                    float exposure = ThreatExposure(s, site.Hex);
                    float heroCost = EconomyMissionOpportunityCost(builder, activeIntents);
                    float assignmentAp = builder?.TotalAssignmentApCost
                        ?? card.EffectivePlayApCost;
                    float intrinsicBuildCost = card.EffectivePlayApCost
                            * AiConfigV2.economyBuildApPenalty
                        + ResourceCostSum(card.EffectivePlayResourceCost)
                            * AiConfigV2.economyBuildResourcePenalty;
                    float deliveryApCost = Mathf.Max(0f,
                            assignmentAp - card.EffectivePlayApCost)
                        * AiConfigV2.economyBuildApPenalty;
                    float extractionLossPenalty = site.ConvertsOwnedExtractionSite
                        ? AiConfigV2.economyBaseExtractionLossPenalty * site.LostExtractionIncome
                        : 0f;
                    float strategicValue = reasonValue - intrinsicBuildCost
                        - AiConfigV2.economySiteThreatPenalty * exposure
                        - extractionLossPenalty;
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
                        Explain = $"Base reason={reasonValue:0.##} capacity={site.CapacityValue:0.##} "
                            + $"yield={hexYield:0.##} cluster={site.NearbyResourceClusterValue:0.##} "
                            + $"network={site.NetworkExpansionValue:0.##} "
                            + $"pressure={site.InfrastructurePressure:0.##} airfield={airfield:0.##} "
                            + $"logistics={site.LogisticsValue:0.##} forward={site.ForwardProgressValue:0.##} "
                            + $"corridor={site.CorridorAlignmentValue:0.##} global={global:0.##} "
                            + $"buildCost={intrinsicBuildCost:0.##} deliveryApCost={deliveryApCost:0.##} "
                            + $"extractionLoss={extractionLossPenalty:0.##}",
                    });
                }

            // Stage the best meaningful, legal and safely-routable Base before value admission.
            // This is what lets the existing continuity urgency accumulate from a negative score.
            AxisDemand stagedBase = meaningfulDemands
                .Where(d => d.EconomyPreferredBuilderArmyId.HasValue)
                .OrderByDescending(d => IsActiveBaseCommitment(
                    activeIntents, d.TargetHex, d.EconomyBuildCard) ? 1 : 0)
                .ThenByDescending(d => d.Value)
                .ThenByDescending(d => d.EconomySiteValue)
                .ThenBy(d => d.TargetHex?.Q ?? int.MaxValue)
                .ThenBy(d => d.TargetHex?.R ?? int.MaxValue)
                .FirstOrDefault();
            bool urgencyEligible = stagedBase?.TargetHex != null
                && HasStructuralEconomyBuilderRoute(
                    s, stagedBase.TargetHex.Value, stagedBase.EconomyBuilderRoutes);
            float urgency = MissionIntentRegistry.GetOrCreate(player)
                .MarkBaseExpansionCandidate(s.TurnNumber, stagedBase?.EconomyBuildCard,
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
                    + $"wait={MissionIntentRegistry.GetOrCreate(player).BaseExpansionWaitTurns} urgency={urgency:0.##}"
                : $"considered={considered} kept={kept} best={best.EconomyBuildCard.Definition.displayName} "
                    + $"target=({best.TargetHex?.Q},{best.TargetHex?.R}) value={best.EconomySiteValue:0.##} "
                    + $"wait={MissionIntentRegistry.GetOrCreate(player).BaseExpansionWaitTurns} urgency={urgency:0.##}";
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

        private static float BaseHexYieldValue(WorldSnapshot s, ResourceBundle yield)
        {
            if (s?.Economy?.PerType == null)
                return 0f;
            var standings = s.Economy.PerType.ToDictionary(x => x.Type, x => x);
            float value = 0f;
            foreach (ResourceType type in ResourceBundle.All)
                if (standings.TryGetValue(type, out EconomyResourceStanding standing))
                    value += yield.Get(type) * Mathf.Max(0.25f, standing.DeficitScore);
            return value;
        }

        private static float BaseGlobalEffectValue(WorldSnapshot s, CardDefinition definition)
        {
            if (definition?.grantedAbilities == null)
                return 0f;
            EffectContribution contribution = StrategicEffectRegistry.Contributions(
                IntendedRole.Economy, definition.grantedAbilities, 0,
                new EffectEvaluationContext(s));
            return contribution.GlobalRoleFit + contribution.GlobalImmediateTempo
                + contribution.GlobalThreatResponse + contribution.GlobalCapabilityGap
                + contribution.GlobalForceGrowth + contribution.GlobalSynergy;
        }

        private static float BaseAirfieldValue(WorldSnapshot s, CardDefinition definition,
            HexCoord target)
        {
            if (definition == null || definition.airfieldCapacity <= 0 || s?.Self == null)
                return 0f;
            bool aviationRelevant = (s.Self.Hand ?? System.Array.Empty<CardData>())
                    .Any(c => c?.Definition?.isAviation == true)
                || (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>()).Any(a => a != null && a.IsAir);
            if (!aviationRelevant)
                return 0f;
            List<ArmySnapshot> airfields = (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.IsAirfield).ToList();
            if (airfields.Count == 0)
                return 1f;
            int distance = airfields.Min(a => HexGridMath.Distance(a.Hex, target));
            return Mathf.Clamp01(distance / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
        }

        private static CardDefinition ExtractionDefinition(AiTurnContext ctx, ResourceType type)
        {
            CardDefinition[] cards = ctx?.GameConfig?.extractionFacilityCards;
            int index = (int)type;
            return cards != null && index >= 0 && index < cards.Length ? cards[index] : null;
        }

        private static float ThreatExposure(WorldSnapshot s, HexCoord target)
        {
            if (s?.Known?.EnemySightings == null)
                return 0f;
            float exposure = 0f;
            foreach (AiMapMemory.KnownEnemySighting enemy in s.Known.EnemySightings)
            {
                int distance = HexGridMath.Distance(target, enemy.Hex);
                if (distance <= 3)
                    exposure = Mathf.Max(exposure, 1f - distance / 4f);
            }
            return exposure;
        }

        private static float ResourceCostSum(ResourceCost cost) => cost == null ? 0f
            : ResourceBundle.All.Sum(t => Mathf.Max(0, cost.Get(t)));

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
