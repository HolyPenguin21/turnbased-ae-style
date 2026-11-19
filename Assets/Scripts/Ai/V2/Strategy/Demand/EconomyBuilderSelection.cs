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
            ArmySnapshot projectedArmy = null)
        {
            var ranked = RankEconomyBuilders(snap, target, routes, activeIntents, commitments,
                buildValue, buildApCost, includeReturn, requiresFoundingGarrison,
                projectedArmy);
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
            ArmySnapshot projectedArmy = null)
        {
            // Assess the EXACT candidate first: loan admission and final TaskScore must
            // price the same projected roster, outbound trip and return activations.
            // The old raw-hex penalty introduced a second incompatible delivery scorer.
            return EconomyBuilderCandidates(snap, target, routes, activeIntents, commitments,
                    projectedArmy)
                .Select(x => AssessEconomyArmy(snap, target, x.route, x.army,
                    buildApCost, includeReturn, requiresFoundingGarrison))
                .Where(x => LogIfIneligible(snap, target, x))
                .Select(x => new
                {
                    Choice = x,
                    Assignment = ActiveAssignment(activeIntents, x.Army.ArmyId),
                    // Continuity already pinned this army to this very site: the delivery and
                    // support horizons that admit a NEW builder do not apply to it.
                    PinnedHere = activeIntents?.Any(i => EconomyContinuity.HoldsEconomyBuildSite(i, target)
                        && i.PreferredMoverArmyId == x.Route.ArmyId) == true,
                    Opportunity = EconomyMissionOpportunityCost(x, activeIntents, target),
                })
                .Where(x => (EconomyExtractionDeliveryAllowed(x.Choice) || x.PinnedHere)
                    && (EconomySupportBuilderAllowed(x.Choice) || x.PinnedHere))
                .Where(x => x.Choice.Route.IsOnTarget
                    || x.Assignment == null
                    || x.Assignment.Kind == MissionKind.Economy
                    || EconomyLoanAllowed(x.Assignment, buildValue, x.Choice, buildApCost, out _))
                // Economy's own builders come first only while taking them is free (this
                // site's own build, or a builder already walking home); a builder busy on
                // another site is priced like any other donor below.
                .OrderBy(x => EconomyBuilderIsSupport(x.Choice) ? 1 : 0)
                .ThenByDescending(x => (x.Choice.Route.HasActiveEconomyCommitment
                        || x.Assignment?.Kind == MissionKind.Economy)
                    && x.Opportunity <= AiConfigV2.allocatorSliceEpsilon)
                .ThenBy(x => x.Choice.Suitability == EconomyArmySuitability.Ready ? 0
                    : x.Choice.Suitability == EconomyArmySuitability.LightenAtBase ? 1 : 2)
                // Among equally suitable builders use canonical delivery plus the ONE
                // donor interruption loss. Otherwise a cheaper AP loan can still lose
                // intrinsic value to a slightly dearer uncommitted actor.
                .ThenBy(x => TaskScoreEvaluator.Price(
                        Mathf.Max(0f, x.Choice.TotalAssignmentApCost - buildApCost))
                    + x.Opportunity)
                .ThenBy(x => x.Choice.TotalAssignmentApCost)
                .ThenBy(x => x.Choice.Route.EffectiveArmyPower)
                .ThenBy(x => x.Choice.Route.ArmySize)
                .ThenBy(x => x.Choice.Route.TravelCost + (includeReturn ? x.Choice.Route.ReturnTravelCost : 0))
                .ThenBy(x => x.Choice.Route.ArmyId)
                .Select(x => x.Choice)
                .ToList();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            Dictionary<(HexCoord, EconomyBuilderRouteSnapshot, ArmySnapshot, float, bool, bool), EconomyBuilderChoice>>
            EconomyAssessmentCache = new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
                Dictionary<(HexCoord, EconomyBuilderRouteSnapshot, ArmySnapshot, float, bool, bool), EconomyBuilderChoice>>();

        // The army's assessment for this site: one of three delivery shapes, each with the exact
        // roster it departs with (EconomyBuilderChoice), or Ineligible with the reason.
        //  · garrison extraction — the hero leaves a garrison (AssessGarrisonExtraction);
        //  · its own roster is safe — Ready, or LightenAtBase when a local garrison can take the
        //    bodies the trip does not need (AssessOwnRoster);
        //  · its own roster is not safe — ReinforceAtBase from the local garrison (AssessReinforcement).
        // Founding must deliver a ground body, not merely prefer one; a defender already on the
        // site is a frozen witness. Final Materialization/Execution revalidates the transfer.
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

            EconomyBuilderChoice Compute()
            {
                bool defendedSite = snap?.Self?.Armies?.Any(a => a != null && a.IsGarrison
                    && a.Hex.Equals(target) && a.Members?.Any(AiArmyRoles.IsGroundBattleBody) == true) == true;
                bool enforceGarrisonBody = founding && !defendedSite;
                var choice = new EconomyBuilderChoice
                {
                    Route = route,
                    Army = army,
                    FoundsBase = founding,
                    TotalAssignmentApCost = EstimateEconomyAssignmentAp(route, buildApCost, includeReturn),
                    Suitability = EconomyArmySuitability.Ineligible,
                    IneligibleReason = "insufficient_safe_escort",
                };
                if (army == null)
                    return choice;
                // Neutral sightings never call for an escort (KnownThreatsAffectingEconomyRoute).
                List<AiMapMemory.KnownEnemySighting> threats = (route.RouteThreats
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Where(t => t.Owner?.IsNeutral != true).ToList();
                if (route.RequiresGarrisonExtraction)
                    return AssessGarrisonExtraction(choice, target, threats, enforceGarrisonBody,
                        buildApCost, includeReturn);
                int minimumEscort = threats.Count == 0 && !enforceGarrisonBody ? 0 : 1;
                choice.MinimumEscortCount = minimumEscort;
                ArmySnapshot localGarrison = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                    && a.IsGarrison && a.Hex.Equals(army.Hex) && a != army);
                bool mayPrepare = snap?.Self?.BaseHexes?.Contains(army.Hex) == true
                    && localGarrison != null && !localGarrison.HasActivatedThisTurn
                    && !army.EconomyRosterProtected;
                List<int> currentIndices = Enumerable.Range(0, army.Members?.Count ?? 0)
                    .Where(i => AiArmyRoles.IsGroundBattleBody(army.Members[i])
                        && (i >= (army.NonHeroIsAviation?.Count ?? 0)
                            || !army.NonHeroIsAviation[i])).ToList();
                List<WorthIt.DefenderProfile> current = currentIndices
                    .Select(i => army.Members[i]).ToList();
                // 2026-11-19 (owner, FoundingSiteDefence) — a founding on a site the known enemy
                // reaches takes its whole roster: no body is left at home (LightenAtBase). Match
                // Tessek vs Draven: the builder dropped its Hazard Pioneer and founded (-2,0), two
                // hexes from a 63-power enemy Base, with one Crawler.
                bool keepWholeRoster = founding && FoundingSiteDefence.IsThreatened(snap, target);
                if (EconomyRosterSafe(current, army.Commander, threats, minimumEscort))
                    return AssessOwnRoster(choice, threats, minimumEscort, currentIndices,
                        mayPrepare && !keepWholeRoster ? localGarrison : null, buildApCost, includeReturn);
                if (!mayPrepare)
                {
                    if (localGarrison?.HasActivatedThisTurn == true)
                        choice.IneligibleReason = "escort_activated_this_turn";
                    return choice;
                }
                return AssessReinforcement(choice, threats, minimumEscort, currentIndices, current,
                    localGarrison, buildApCost, includeReturn);
            }
        }

        // The hero leaves a garrison (Analysis resolved the container). Under a threat, or when it
        // founds a Base, it takes one concrete body the garrison can spare (playtest 2026-10-18:
        // Thane's hero left the Citadel alone, then took its only defender as the escort).
        private static EconomyBuilderChoice AssessGarrisonExtraction(EconomyBuilderChoice choice,
            HexCoord target, List<AiMapMemory.KnownEnemySighting> threats, bool enforceGarrisonBody,
            float buildApCost, bool includeReturn)
        {
            EconomyBuilderRouteSnapshot route = choice.Route;
            ArmySnapshot army = choice.Army;
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
            ProjectRoute(choice, route.ActivationApCost, route.MaxMovement, null, buildApCost, includeReturn);
            if (enforceGarrisonBody || threats.Count > 0)
            {
                List<int> escortPick = route.ExtractedHeroCapacity < 2 ? null : WeakestSufficientSubset(
                    Enumerable.Range(0, army.Members?.Count ?? 0)
                        .Where(i => AiArmyRoles.IsGroundBattleBody(army.Members[i])).ToList(),
                    1, 1, subset =>
                    {
                        int i = subset[0];
                        int move = i < army.NonHeroMoveMax.Count ? army.NonHeroMoveMax[i] : route.MaxMovement;
                        if (!GarrisonMaySpare(army, subset)
                            || (i < army.NonHeroMoveMax.Count && move < route.MaximumStepCost)
                            || !EconomyRosterSafe(new[] { army.Members[i] }, route.ExtractedHeroCommander,
                                threats, 1))
                            return null;
                        return (BodyAp(army, i), move, BodyPower(army, i));
                    });
                if (escortPick == null)
                {
                    choice.Suitability = EconomyArmySuitability.Ineligible;
                    choice.IneligibleReason = "extracted_hero_needs_sparable_garrison_body";
                    return choice;
                }
                int escort = escortPick[0];
                choice.PreparationGarrison = army;
                choice.AddedIndices = escortPick;
                choice.MinimumEscortCount = 1;
                int bodyAp = BodyAp(army, escort);
                int bodyMove = escort < army.NonHeroMoveMax.Count ? army.NonHeroMoveMax[escort] : route.MaxMovement;
                if (route.ExtractionContainerActivated) choice.PreparationApCost += bodyAp;
                ProjectRoute(choice, route.ActivationApCost + bodyAp,
                    Mathf.Max(1, Mathf.Min(route.MaxMovement, bodyMove)),
                    Mathf.Min(route.CurrentMovement, escort < army.NonHeroCurrentMovement.Count
                        ? army.NonHeroCurrentMovement[escort] : bodyMove),
                    buildApCost, includeReturn, armySize: 2);
            }
            AiDebugLog.WriteDeduped($"{target}|{army.ArmyId}",
                $"[ECO][Builder] site=({target.Q},{target.R}) "
                + $"actor=#{army.ArmyId} source=Garrison route=VALID "
                + $"extraction=snapshot requiredAp={route.ExtractionApCost:0.##} "
                + "decision=READY");
            return choice;
        }

        // The army's own roster is safe: it goes as is (Ready), or — at a base whose garrison
        // can take them — leaves the bodies the trip does not need (LightenAtBase).
        private static EconomyBuilderChoice AssessOwnRoster(EconomyBuilderChoice choice,
            List<AiMapMemory.KnownEnemySighting> threats, int minimumEscort, List<int> currentIndices,
            ArmySnapshot localGarrison, float buildApCost, bool includeReturn)
        {
            EconomyBuilderRouteSnapshot route = choice.Route;
            ArmySnapshot army = choice.Army;
            List<int> retained = localGarrison != null
                ? MinimumSafeEconomyEscortIndices(army, threats, minimumEscort,
                    route.MaximumStepCost, localGarrison.Capacity > 0
                        ? Mathf.Max(0, localGarrison.Capacity - localGarrison.OccupiedBattleSlots)
                        : int.MaxValue, currentIndices.Count)
                : currentIndices;
            if (retained == null) return choice;
            choice.RetainedIndices = retained;
            choice.PreparationGarrison = localGarrison;
            choice.MinimumEscortCount = retained.Count;
            int knownBodyAp = army.NonHeroActivationApCosts?.Sum() ?? 0;
            int heroAp = army.HeroActivationApCost > 0
                ? army.HeroActivationApCost
                : Mathf.Max(0, route.ActivationApCost - knownBodyAp);
            int heroMove = army.HeroMoveMax > 0 ? army.HeroMoveMax : route.MaxMovement;
            int? currentMovement = army.NonHeroCurrentMovement.Count == army.Members.Count
                && army.NonHeroCurrentMovement.Count > 0
                    ? retained.Count == 0 ? army.HeroCurrentMovement
                        : Mathf.Min(army.HeroCurrentMovement, retained.Min(i => army.NonHeroCurrentMovement[i]))
                    : (int?)null;
            ProjectRoute(choice, heroAp + retained.Sum(i => BodyAp(army, i)),
                retained.Count == 0 ? heroMove
                    : Mathf.Min(heroMove, retained.Min(i => i < army.NonHeroMoveMax.Count
                        ? army.NonHeroMoveMax[i] : army.MaxMovement)),
                currentMovement, buildApCost, includeReturn);
            choice.Suitability = localGarrison != null && currentIndices.Count > retained.Count
                ? EconomyArmySuitability.LightenAtBase
                : EconomyArmySuitability.Ready;
            return choice;
        }

        // The army's own roster is not safe: at a base, the fewest bodies the local garrison can
        // spare that make it safe join it (ReinforceAtBase).
        private static EconomyBuilderChoice AssessReinforcement(EconomyBuilderChoice choice,
            List<AiMapMemory.KnownEnemySighting> threats, int minimumEscort, List<int> currentIndices,
            List<WorthIt.DefenderProfile> current, ArmySnapshot garrison, float buildApCost,
            bool includeReturn)
        {
            EconomyBuilderRouteSnapshot route = choice.Route;
            ArmySnapshot army = choice.Army;
            IReadOnlyList<WorthIt.DefenderProfile> reserve = garrison.Members
                ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
            List<int> reserveIndices = Enumerable.Range(0, reserve.Count)
                .Where(i => AiArmyRoles.IsGroundBattleBody(reserve[i])
                    && (i >= (garrison.NonHeroIsAviation?.Count ?? 0)
                        || !garrison.NonHeroIsAviation[i])).ToList();
            List<int> best = WeakestSufficientSubset(reserveIndices, 1, reserveIndices.Count, subset =>
            {
                // No economy escort strips the garrison's protected defence floor — the
                // same rule every other donor obeys (AiArmyRoles.CanSpareGarrisonMembers).
                if (!GarrisonMaySpare(garrison, subset)
                    || (army.Capacity > 0 && army.MemberCount + subset.Count > army.Capacity))
                    return null;
                var projected = new List<WorthIt.DefenderProfile>(current);
                projected.AddRange(subset.Select(i => reserve[i]));
                if (!EconomyRosterSafe(projected, army.Commander, threats, minimumEscort))
                    return null;
                int addedMove = subset.Min(i => i < garrison.NonHeroMoveMax.Count
                    ? garrison.NonHeroMoveMax[i] : garrison.MaxMovement);
                if (Mathf.Min(route.MaxMovement, addedMove) < route.MaximumStepCost)
                    return null;
                return (subset.Sum(i => BodyAp(garrison, i)), addedMove,
                    AiPower.EffectiveArmyPowerFromProfiles(subset.Select(i => reserve[i]).ToList()));
            });
            if (best == null)
                return choice;
            int bestAp = best.Sum(i => BodyAp(garrison, i));
            int bestMove = best.Min(i => i < garrison.NonHeroMoveMax.Count
                ? garrison.NonHeroMoveMax[i] : garrison.MaxMovement);
            choice.RetainedIndices = currentIndices;
            choice.AddedIndices = best;
            choice.PreparationGarrison = garrison;
            choice.PreparationApCost = army.HasActivatedThisTurn
                ? best.Where(i => i >= garrison.NonHeroRuntimeIds.Count
                    || !army.ActivationCoveredUnitRuntimeIds.Contains(garrison.NonHeroRuntimeIds[i]))
                    .Sum(i => BodyAp(garrison, i)) : 0f;
            choice.MinimumEscortCount = current.Count + best.Count;
            choice.Suitability = EconomyArmySuitability.ReinforceAtBase;
            ProjectRoute(choice, route.ActivationApCost + bestAp,
                Mathf.Min(route.MaxMovement, bestMove > 0 ? bestMove : route.MaxMovement),
                garrison.NonHeroCurrentMovement.Count == reserve.Count
                    ? Mathf.Min(route.CurrentMovement, best.Min(i => garrison.NonHeroCurrentMovement[i]))
                    : (int?)null,
                buildApCost, includeReturn);
            return choice;
        }

        // THE projection of a delivery roster onto its witnessed route: the roster's activation AP
        // and movement (current movement only when the roster's bodies report it), and the
        // assignment AP priced on that projected route.
        private static void ProjectRoute(EconomyBuilderChoice choice, int activationAp, int maxMovement,
            int? currentMovement, float buildApCost, bool includeReturn, int? armySize = null)
        {
            EconomyBuilderRouteSnapshot projected = choice.Route;
            choice.ProjectedActivationApCost = activationAp;
            choice.ProjectedMaxMovement = maxMovement;
            projected.ActivationApCost = activationAp;
            projected.MaxMovement = Mathf.Max(1, maxMovement);
            if (currentMovement.HasValue) projected.CurrentMovement = currentMovement.Value;
            if (armySize.HasValue) projected.ArmySize = armySize.Value;
            choice.Route = projected;
            choice.TotalAssignmentApCost = choice.PreparationApCost
                + EstimateEconomyAssignmentAp(projected, buildApCost, includeReturn);
        }

        private static int BodyAp(ArmySnapshot army, int index) =>
            index < (army?.NonHeroActivationApCosts?.Count ?? 0) ? army.NonHeroActivationApCosts[index] : 0;

        private static float BodyPower(ArmySnapshot army, int index) =>
            AiPower.EffectiveArmyPowerFromProfiles(new[] { army.Members[index] });

        // 2026-11-16 (owner) — THE escort pick of every delivery shape: the fewest bodies, then the
        // least activation AP, then the fastest, then the WEAKEST sufficient set — the strong
        // bodies stay available to the strike. `evaluate` returns null for an inadmissible set.
        private static List<int> WeakestSufficientSubset(IReadOnlyList<int> indices, int minCount,
            int maxCount, System.Func<List<int>, (int Ap, int Move, float Power)?> evaluate)
        {
            for (int count = Mathf.Max(0, minCount); count <= Mathf.Min(maxCount, indices.Count); count++)
            {
                List<int> best = null;
                (int Ap, int Move, float Power) bestKey = default;
                foreach (List<int> subset in Combinations(indices, count))
                {
                    (int Ap, int Move, float Power)? key = evaluate(subset);
                    if (!key.HasValue)
                        continue;
                    (int ap, int move, float power) = key.Value;
                    if (best == null || ap < bestKey.Ap
                        || (ap == bestKey.Ap && move > bestKey.Move)
                        || (ap == bestKey.Ap && move == bestKey.Move && power < bestKey.Power - 0.001f))
                    {
                        best = subset;
                        bestKey = key.Value;
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
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
                    >= AiConfigV2.economyEscortMinWinChance);
        }

        // The bodies of `army` it keeps for the trip (the rest unload into the local garrison):
        // the weakest sufficient set (WeakestSufficientSubset) the garrison has room to take the
        // others of, fast enough for the route's steepest step.
        internal static List<int> MinimumSafeEconomyEscortIndices(ArmySnapshot army,
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats, int minimumEscort,
            int minimumMovement = 0, int unloadRoom = int.MaxValue, int existingBodies = 0)
        {
            IReadOnlyList<WorthIt.DefenderProfile> pool = army?.Members
                ?? System.Array.Empty<WorthIt.DefenderProfile>();
            List<int> indices = Enumerable.Range(0, pool.Count)
                .Where(i => i >= (army.NonHeroIsAviation?.Count ?? 0)
                    || !army.NonHeroIsAviation[i]).ToList();
            return WeakestSufficientSubset(indices, minimumEscort, indices.Count, subset =>
            {
                if (existingBodies - subset.Count > unloadRoom) return null;
                int move = subset.Count == 0 ? army.HeroMoveMax
                    : subset.Min(i => i < army.NonHeroMoveMax.Count
                        ? army.NonHeroMoveMax[i] : army.MaxMovement);
                move = Mathf.Min(army.HeroMoveMax > 0 ? army.HeroMoveMax : army.MaxMovement, move);
                if (move < minimumMovement) return null;
                List<WorthIt.DefenderProfile> roster = subset.Select(i => pool[i]).ToList();
                if (!EconomyRosterSafe(roster, army.Commander, threats, minimumEscort)) return null;
                return (subset.Sum(i => BodyAp(army, i)), move, AiPower.EffectiveArmyPowerFromProfiles(roster));
            });
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
            int outboundTurns = DeliveryTurns(route);
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

        // THE outbound delivery time of a builder route in owner turns: this turn counts only when
        // its remaining movement moves the builder; every further turn brings a full MaxMovement.
        internal static int DeliveryTurns(EconomyBuilderRouteSnapshot route) =>
            route.TravelCost <= 0 ? 0
                : (route.CurrentMovement > 0 ? 1 : 0)
                    + Mathf.CeilToInt(Mathf.Max(0, route.TravelCost - route.CurrentMovement)
                        / (float)Mathf.Max(1, route.MaxMovement));

        internal static bool EconomyBuilderIsSupport(EconomyBuilderChoice choice) => choice != null
            && (choice.Route.BuilderHasHomeVocation ?? choice.Army?.HeroIsHomeVocation ?? false);

        internal static bool EconomySupportBuilderAllowed(EconomyBuilderChoice choice)
        {
            if (!EconomyBuilderIsSupport(choice)) return true;
            EconomyBuilderRouteSnapshot route = choice.Route;
            int move = Mathf.Max(1, route.MaxMovement);
            int home = route.ReturnTravelCost == int.MaxValue ? int.MaxValue
                : Mathf.CeilToInt(Mathf.Max(0, route.ReturnTravelCost) / (float)move);
            return DeliveryTurns(route) <= AiConfigV2.economySupportBuilderMaxDeliveryTurns
                && home <= AiConfigV2.economySupportBuilderMaxDeliveryTurns;
        }

        // Admission of a NEW extraction delivery, shared by existing-builder ranking and
        // prospective hero materialization. AP-free movement still consumes owner turns.
        internal static bool EconomyExtractionDeliveryAllowed(EconomyBuilderChoice choice)
        {
            if (choice == null) return false;
            if (choice.FoundsBase || choice.Route.IsOnTarget) return true;
            EconomyBuilderRouteSnapshot route = choice.Route;
            int turns = DeliveryTurns(route);
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
                ArmySnapshot projectedArmy = null)
        {
            IReadOnlyList<EconomyBuilderRouteSnapshot> witnessed = routes
                ?? System.Array.Empty<EconomyBuilderRouteSnapshot>();
            foreach (EconomyBuilderRouteSnapshot route in witnessed)
            {
                ArmySnapshot army = projectedArmy?.ArmyId == route.ArmyId ? projectedArmy
                    : snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == route.ArmyId);
                string rejection = CandidateRejection(snap, target, route, army, activeIntents, commitments);
                if (rejection == null)
                    yield return (route, !route.IsOnTarget ? army.EconomyDeparture ?? army : army);
                else if (rejection != "garrison_without_extraction_route"
                         && rejection != "not_mobile_economy_builder")
                    // Shape non-candidates stay silent (every garrison / field army per site).
                    AiDebugLog.WriteDeduped($"builder-candidate|{target.Q},{target.R}|{route.ArmyId}",
                        $"[ECO][Builder] T{snap?.TurnNumber} site=({target.Q},{target.R}) actor=#{route.ArmyId} "
                        + $"decision=REJECT reason={rejection}");
            }
        }

        // D0 (match 2026-11-13) — an Ineligible builder used to vanish silently (P4: no line for
        // the site at all). Printed once per turn per site and actor; never changes the filter.
        private static bool LogIfIneligible(WorldSnapshot snap, HexCoord target, EconomyBuilderChoice choice)
        {
            if (choice.Suitability != EconomyArmySuitability.Ineligible)
                return true;
            AiDebugLog.WriteDeduped($"builder-ineligible|{target.Q},{target.R}|{choice.Route.ArmyId}",
                $"[ECO][Builder] T{snap?.TurnNumber} site=({target.Q},{target.R}) actor=#{choice.Route.ArmyId} "
                + $"decision=INELIGIBLE reason={choice.IneligibleReason ?? "unspecified"}");
            return false;
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
            else if (!army.IsMobileEconomyBuilder
                && (route.IsOnTarget || army.EconomyDeparture?.IsMobileEconomyBuilder != true))
                return "not_mobile_economy_builder";
            else if (army.OperatorDutyBlocksDeparture && !route.IsOnTarget)
                return "operator_leaves_served_facility";

            MissionIntent assignment = ActiveAssignment(activeIntents, army.ArmyId);
            // Project owner, 2026-10-02: Vex's 20-power army was released from an intercept and
            // sent to found a base two turns before an enemy fist of 78 reached its citadel.
            // 2026-11-18 (owner, match Tessek vs Halden T3-T10): the fixed 25%-of-strike share
            // that answered it kept Halden's only field army (100% of its strike) from ever
            // building while it stood idle (T4: 1 of 10 AP spent) and his extraction stalled for
            // eight turns. A combat army is now a builder like any hero-led army — near the site
            // (the delivery horizon), with nothing else to do (assignment / claim gates below) —
            // unless an own stronghold needs it home: THE home-needs-the-army rule Attack's march
            // reads (AttackMarchPolicy.StrongholdNeedingArmy, the own-Citadel race included).
            if (!army.IsGarrison && !route.IsOnTarget
                && (assignment == null || assignment.Kind != MissionKind.Economy)
                && army.IsStructuralRaidActor
                && AttackMarchPolicy.StrongholdNeedingArmy(snap, army, out _) != null)
                return "stronghold_needs_the_army";
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
