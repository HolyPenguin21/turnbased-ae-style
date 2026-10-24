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
    // Economy builder selection, split from DemandLayer.Economy: which army delivers a build
    // (ready builder, lighten/reinforce at a base, minimum safe escort), its assignment AP and
    // the support/loan/donor rules. DemandLayer turns the choice into the Economy demand.
    internal static class EconomyBuilderSelection
    {
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
            public bool FoundsBase;
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

        internal static EconomyBuilderChoice SelectEconomyBuilder(WorldSnapshot snap,
            HexCoord target, IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            float buildValue, float buildApCost, bool includeReturn,
            int? pinnedBuilderArmyId = null, bool? requiresFoundingGarrison = null,
            ArmySnapshot projectedArmy = null, float? projectedFieldStrikePotential = null)
        {
            var ranked = RankEconomyBuilders(snap, target, routes, activeIntents, commitments,
                buildValue, buildApCost, includeReturn, requiresFoundingGarrison,
                projectedArmy, projectedFieldStrikePotential);
            if (pinnedBuilderArmyId.HasValue)
                return ranked.FirstOrDefault(x => x.Army?.ArmyId == pinnedBuilderArmyId.Value);
            // An ordinary hero whose delivered task loses money is not a usable alternative.
            return ranked.FirstOrDefault(x => buildValue
                    - TaskScoreEvaluator.Price(Mathf.Max(0f, x.TotalAssignmentApCost - buildApCost))
                    - EconomyMissionOpportunityCost(x, activeIntents, target) > AiConfigV2.allocatorSliceEpsilon)
                ?? ranked.FirstOrDefault();
        }

        internal static IReadOnlyList<EconomyBuilderChoice> RankEconomyBuilders(WorldSnapshot snap,
            HexCoord target, IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            float buildValue, float buildApCost, bool includeReturn, bool? requiresFoundingGarrison = null,
            ArmySnapshot projectedArmy = null, float? projectedFieldStrikePotential = null)
        {
            // Assess the EXACT candidate first: loan admission and final TaskScore must
            // price the same projected roster, outbound trip and return activations.
            // The old raw-hex penalty introduced a second incompatible delivery scorer.
            return EconomyBuilderCandidates(snap, target, routes, activeIntents, commitments,
                    projectedArmy, projectedFieldStrikePotential)
                .Select(x => AssessEconomyArmy(snap, target, x.route, x.army,
                    buildApCost, includeReturn, requiresFoundingGarrison))
                .Where(x => x.Suitability != EconomyArmySuitability.Ineligible)
                .Where(x => EconomyExtractionDeliveryAllowed(x)
                    || activeIntents?.Any(i => EconomyContinuity.HoldsEconomyBuildSite(i, target)
                        && i.PreferredMoverArmyId == x.Route.ArmyId) == true)
                .Where(x => EconomySupportBuilderAllowed(x)
                    || activeIntents?.Any(i => EconomyContinuity.HoldsEconomyBuildSite(i, target)
                        && i.PreferredMoverArmyId == x.Route.ArmyId) == true)
                .Where(x => x.Route.IsOnTarget
                    || ActiveAssignment(activeIntents, x.Army.ArmyId) == null
                    || ActiveAssignment(activeIntents, x.Army.ArmyId).Kind == MissionKind.Economy
                    || EconomyLoanAllowed(ActiveAssignment(activeIntents, x.Army.ArmyId), buildValue,
                        x, buildApCost, out _))
                // Economy's own builders come first only while taking them is free (this
                // site's own build, or a builder already walking home); a builder busy on
                // another site is priced like any other donor below.
                .OrderBy(x => EconomyBuilderIsSupport(x) ? 1 : 0)
                .ThenByDescending(x => (x.Route.HasActiveEconomyCommitment
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
                .ThenBy(x => x.TotalAssignmentApCost)
                .ThenBy(x => x.Route.EffectiveArmyPower)
                .ThenBy(x => x.Route.ArmySize)
                .ThenBy(x => x.Route.TravelCost + (includeReturn ? x.Route.ReturnTravelCost : 0))
                .ThenBy(x => x.Route.ArmyId)
                .ToList();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            Dictionary<(HexCoord, EconomyBuilderRouteSnapshot, ArmySnapshot, float, bool, bool), EconomyBuilderChoice>>
            EconomyAssessmentCache = new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
                Dictionary<(HexCoord, EconomyBuilderRouteSnapshot, ArmySnapshot, float, bool, bool), EconomyBuilderChoice>>();

        internal static EconomyBuilderChoice AssessEconomyArmy(WorldSnapshot snap,
            HexCoord target, EconomyBuilderRouteSnapshot route, ArmySnapshot army,
            float buildApCost, bool includeReturn, bool? requiresFoundingGarrison = null)
        {
            if (!route.IsOnTarget) army = army?.EconomyDeparture ?? army;
            bool founding = requiresFoundingGarrison ?? !includeReturn;
            if (snap == null)
                return Compute();
            var cache = EconomyAssessmentCache.GetOrCreateValue(snap);
            var key = (target, route, army, buildApCost, includeReturn, founding);
            if (!cache.TryGetValue(key, out EconomyBuilderChoice assessed))
                cache[key] = assessed = Compute();
            // The cached decision belongs to this snapshot. A consumer may reprice its local
            // copy, but must never overwrite the result observed by the next reader.
            return assessed.DetachedDecision();

            // Founding must deliver a ground body, not merely prefer one. Extraction keeps its
            // threat-driven escort, drawn only from bodies the garrison floor spares. A defender
            // already on the site is a frozen witness; final Materialization/Execution revalidates the physical transfer before spending.
            EconomyBuilderChoice Compute()
            {
                bool defendedSite = snap?.Self?.Armies?.Any(a => a != null && a.IsGarrison
                    && a.Hex.Equals(target) && a.Members?.Any(AiArmyRoles.IsGroundBattleBody) == true) == true;
                return ComputeCore(founding && !defendedSite);
            }

            EconomyBuilderChoice ComputeCore(bool enforceGarrisonBody)
            {
                var choice = new EconomyBuilderChoice
                {
                    Route = route,
                    Army = army,
                    FoundsBase = founding,
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
                    List<AiMapMemory.KnownEnemySighting> extractionThreats = (route.RouteThreats
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                        .Where(t => t.Owner?.IsNeutral != true).ToList();
                    if (enforceGarrisonBody || extractionThreats.Count > 0)
                    {
                        // Extracting the hero alone cannot satisfy founding, nor walk a threatened
                        // route (the field path's minimum escort is 1 under any enemy threat). Pin a
                        // concrete body that the original garrison can spare, then use the existing
                        // composition preparation to put it into the resolved field container
                        // before departure. Playtest 2026-10-18: Thane's hero left the Citadel
                        // alone, then took its only defender as the escort.
                        int escort = Enumerable.Range(0, army.Members?.Count ?? 0)
                            .Where(i => AiArmyRoles.IsGroundBattleBody(army.Members[i])
                                && GarrisonMaySpare(army, new List<int> { i })
                                && route.ExtractedHeroCapacity >= 2
                                && (i >= army.NonHeroMoveMax.Count || army.NonHeroMoveMax[i] >= route.MaximumStepCost)
                                && EconomyRosterSafe(new[] { army.Members[i] }, route.ExtractedHeroCommander,
                                    extractionThreats, 1))
                            .OrderBy(i => i < army.NonHeroActivationApCosts.Count ? army.NonHeroActivationApCosts[i] : 0)
                            .ThenBy(i => i).DefaultIfEmpty(-1).First();
                        if (escort < 0)
                        {
                            choice.Suitability = EconomyArmySuitability.Ineligible;
                            choice.IneligibleReason = "extracted_hero_needs_sparable_garrison_body";
                            return choice;
                        }
                        choice.PreparationGarrison = army;
                        choice.AddedIndices = new List<int> { escort };
                        choice.MinimumEscortCount = 1;
                        int bodyAp = escort < army.NonHeroActivationApCosts.Count ? army.NonHeroActivationApCosts[escort] : 0;
                        int bodyMove = escort < army.NonHeroMoveMax.Count ? army.NonHeroMoveMax[escort] : route.MaxMovement;
                        projectedRoute.ActivationApCost += bodyAp;
                        projectedRoute.MaxMovement = Mathf.Max(1, Mathf.Min(route.MaxMovement, bodyMove));
                        projectedRoute.CurrentMovement = Mathf.Min(route.CurrentMovement,
                            escort < army.NonHeroCurrentMovement.Count ? army.NonHeroCurrentMovement[escort] : bodyMove);
                        projectedRoute.ArmySize = 2;
                        choice.Route = projectedRoute;
                        choice.ProjectedActivationApCost = projectedRoute.ActivationApCost;
                        choice.ProjectedMaxMovement = projectedRoute.MaxMovement;
                        if (route.ExtractionContainerActivated) choice.PreparationApCost += bodyAp;
                        choice.TotalAssignmentApCost = choice.PreparationApCost + EstimateEconomyAssignmentAp(
                            projectedRoute, buildApCost, includeReturn);
                    }
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
                if (enforceGarrisonBody)
                    minimumEscort = Mathf.Max(minimumEscort, 1);
                choice.MinimumEscortCount = minimumEscort;

                List<int> currentIndices = Enumerable.Range(0, army.Members?.Count ?? 0)
                    .Where(i => AiArmyRoles.IsGroundBattleBody(army.Members[i])
                        && (i >= (army.NonHeroIsAviation?.Count ?? 0)
                            || !army.NonHeroIsAviation[i])).ToList();
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
                    .Where(i => AiArmyRoles.IsGroundBattleBody(reserve[i])
                        && (i >= (garrison.NonHeroIsAviation?.Count ?? 0)
                            || !garrison.NonHeroIsAviation[i])).ToList();
                for (int add = 1; add <= reserve.Count; add++)
                {
                    List<int> best = null;
                    int bestAp = int.MaxValue;
                    int bestMove = int.MinValue;
                    foreach (List<int> subset in Combinations(reserveIndices, add))
                    {
                        // No economy escort strips the garrison's protected defence floor — the
                        // same rule every other donor obeys (AiArmyRoles.CanSpareGarrisonMembers).
                        if (!GarrisonMaySpare(garrison, subset))
                            continue;
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

        // Whether the garrison snapshot lets the bodies at `indices` (its NonHero order) leave without
        // crossing its protected defence floor — read from the scan-time witness, never from the live
        // registry. The preference takes one body at a time.
        private static bool GarrisonMaySpare(ArmySnapshot garrison, List<int> indices)
        {
            if (garrison?.NonHeroSpareable == null)
                return true;
            foreach (int i in indices)
                if (i < 0 || i >= garrison.NonHeroSpareable.Count || !garrison.NonHeroSpareable[i])
                    return false;
            return indices.Count <= 1;
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

        internal static bool EconomyBuilderIsSupport(EconomyBuilderChoice choice) => choice != null
            && (choice.Route.BuilderHasHomeVocation ?? choice.Army?.HeroIsHomeVocation ?? false);

        internal static bool EconomySupportBuilderAllowed(EconomyBuilderChoice choice)
        {
            if (!EconomyBuilderIsSupport(choice)) return true;
            EconomyBuilderRouteSnapshot route = choice.Route;
            int move = Mathf.Max(1, route.MaxMovement);
            int delivery = (route.CurrentMovement > 0 && route.TravelCost > 0 ? 1 : 0)
                + Mathf.CeilToInt(Mathf.Max(0, route.TravelCost - route.CurrentMovement) / (float)move);
            int home = route.ReturnTravelCost == int.MaxValue ? int.MaxValue
                : Mathf.CeilToInt(Mathf.Max(0, route.ReturnTravelCost) / (float)move);
            return delivery <= AiConfigV2.economySupportBuilderMaxDeliveryTurns
                && home <= AiConfigV2.economySupportBuilderMaxDeliveryTurns;
        }

        // Admission of a NEW extraction delivery, shared by existing-builder ranking and
        // prospective hero materialization. AP-free movement still consumes owner turns.
        internal static bool EconomyExtractionDeliveryAllowed(EconomyBuilderChoice choice)
        {
            if (choice == null) return false;
            if (choice.FoundsBase || choice.Route.IsOnTarget) return true;
            EconomyBuilderRouteSnapshot route = choice.Route;
            int turns = route.TravelCost <= 0 ? 0
                : (route.CurrentMovement > 0 ? 1 : 0)
                    + Mathf.CeilToInt(Mathf.Max(0, route.TravelCost - route.CurrentMovement)
                        / (float)Mathf.Max(1, route.MaxMovement));
            bool allowed = turns <= AiConfigV2.economyExtractionMaxDeliveryTurns;
            if (!allowed)
                AiDebugLog.WriteDeduped($"delivery-horizon|{route.ArmyId}|{route.TravelCost}",
                    $"[ECO][Builder] actor=#{route.ArmyId} routeCost={route.TravelCost} "
                    + $"deliveryTurns={turns} max={AiConfigV2.economyExtractionMaxDeliveryTurns} "
                    + "decision=REJECT reason=extraction_delivery_horizon");
            return allowed;
        }

        internal static IEnumerable<(EconomyBuilderRouteSnapshot route, ArmySnapshot army)>
            EconomyBuilderCandidates(WorldSnapshot snap, HexCoord target,
                IReadOnlyList<EconomyBuilderRouteSnapshot> routes,
                IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
                ArmySnapshot projectedArmy = null, float? projectedFieldStrikePotential = null)
        {
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = routes
                ?? System.Array.Empty<EconomyBuilderRouteSnapshot>();
            foreach (EconomyBuilderRouteSnapshot route in witnessed)
            {
                ArmySnapshot army = projectedArmy?.ArmyId == route.ArmyId ? projectedArmy
                    : snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == route.ArmyId);
                if (CandidateRejection(snap, target, route, army, activeIntents, commitments,
                        projectedFieldStrikePotential) == null)
                    yield return (route, !route.IsOnTarget ? army.EconomyDeparture ?? army : army);
            }
        }

        // Why a witnessed route's army is not a structural builder candidate for `target`, or
        // null when it is. The ONE candidate gate of EconomyBuilderCandidates, also printed by
        // Provisioning's diagnostic trace (EconomyBuilderCandidateRejection).
        private static string CandidateRejection(WorldSnapshot snap, HexCoord target,
            EconomyBuilderRouteSnapshot route, ArmySnapshot army,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            float? projectedFieldStrikePotential = null)
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
            else if (!army.IsMobileEconomyBuilder
                && (route.IsOnTarget || army.EconomyDeparture?.IsMobileEconomyBuilder != true))
                return "not_mobile_economy_builder";
            else if (army.OperatorDutyBlocksDeparture && !route.IsOnTarget)
                return "operator_leaves_served_facility";

            MissionIntent assignment = ActiveAssignment(activeIntents, army.ArmyId);
            // Project owner, 2026-10-02: a strong field army belongs to defence/offence, not to
            // economy. Vex's 20-power army (a hero + 2 bodies, its best of 34 field power) was
            // released from an intercept and sent to found a base two turns before an enemy fist
            // of 78 reached its citadel. A free army that is a real share of the player's field
            // strike potential is never an Economy builder unless it already stands on the site or
            // is already this site's Economy builder; the demand falls back to a hero-only builder.
            if (!army.IsGarrison && !route.IsOnTarget
                && (assignment == null || assignment.Kind != MissionKind.Economy)
                && army.IsStructuralRaidActor && snap?.Self != null
                && (projectedFieldStrikePotential ?? snap.Self.FieldStrikePotential) > 0f
                && army.EffectiveArmyPower >= AiConfigV2.economyBuilderFistShare
                    * (projectedFieldStrikePotential ?? snap.Self.FieldStrikePotential))
                return "combat_fist_not_a_builder";
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

        internal static MissionIntent ActiveAssignment(
            IReadOnlyList<MissionIntent> activeIntents, int armyId) =>
            activeIntents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                && i.PreferredMoverArmyId == armyId);

        // What taking this builder off its current task costs this build: the displaced task's
        // own value (MissionIntent.DisplacementValue), whatever axis it belongs to. Only
        // continuing the build of this same site is free; a return leg is free by construction.
        internal static float EconomyMissionOpportunityCost(EconomyBuilderChoice builder,
            IReadOnlyList<MissionIntent> activeIntents, HexCoord site)
        {
            if (builder?.Army == null)
                return 0f;
            return TaskScoreEvaluator.MoverOpportunityCost(MissionIntent.DisplacementValueOf(
                activeIntents, builder.Army.ArmyId,
                isOwn: i => EconomyContinuity.HoldsEconomyBuildSite(i, site)));
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
                return donor.Scout != null;
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
            return builder != null && EconomyDonorStructurallyEligible(donor)
                && builder.Route.TravelCost <= builder.Route.CurrentMovement
                && netValue >= AiConfigV2.taskScoreEconomyLoanHysteresisThreshold;
        }
    }
}
