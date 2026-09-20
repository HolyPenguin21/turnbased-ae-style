using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Economy — BuildEconomy and its Economy-only helpers.
    // File-split (mechanical, no behaviour change) from WorldAnalysis.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 5. Still exactly the WorldAnalysis
    // class; only this snapshot family's slice moved to its own file.
    public static partial class WorldAnalysis
    {
        private static EconomyStanding BuildEconomy(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snap)
        {
            var eco = new EconomyStanding();
            var perType = new List<EconomyResourceStanding>();

            List<PlayerSetupData> others = (GameSession.Players ?? new List<PlayerSetupData>())
                .Where(p => p != null && p != player && !p.IsNeutral && !p.IsEliminated)
                .ToList();

            var handNeed = new ResourceBundle();
            var deckNeed = new ResourceBundle();
            AccumulateCardCosts(snap.Self.Hand, ref handNeed);
            AccumulateCardCosts(snap.Self.Deck, ref deckNeed);
            eco.HandResourceNeed = handNeed;
            eco.RemainingDeckResourceNeed = deckNeed;
            var allNeed = new ResourceBundle();
            foreach (ResourceType t in ResourceBundle.All)
                allNeed.Add(t, handNeed.Get(t) + deckNeed.Get(t));
            eco.DeckResourceNeed = allNeed;

            var reservedNeed = new ResourceBundle();
            var spendableStock = new ResourceBundle();
            foreach (ResourceType t in ResourceBundle.All)
            {
                float own = snap.Self.PerTurnIncome.Get(t);
                var otherIncomes = others.Select(p => (float)IncomeProjection.IncomeFor(p, t, ctx.Map)).ToList();
                float median = Median(otherIncomes);
                float reserved = StrategicResourceReservationLedger.Active(
                    player, ctx.TurnNumber, StrategicResourceReservationLedger.Map(t));
                float spendable = StrategicSpendability.SpendableAmount(player, root, ctx, t);
                reservedNeed.Add(t, reserved);
                spendableStock.Add(t, spendable);
                perType.Add(EconomyStanding.CalculateResource(t, own, median,
                    handNeed.Get(t), deckNeed.Get(t), reserved, spendable,
                    ResourceStarvationRegistry.Pressure(player, t)));
            }
            eco.PerType = perType;
            eco.ReservedOperationalNeed = reservedNeed;
            eco.SpendableStockpile = spendableStock;
            var incomeTarget = new ResourceBundle();
            foreach (EconomyResourceStanding rs in perType)
                incomeTarget.Add(rs.Type, rs.IncomeTarget);
            eco.IncomeTarget = incomeTarget;
            EconomyResourceStanding worst = perType
                .OrderByDescending(x => x.DeficitScore).ThenBy(x => x.Type).First();
            eco.MostDeficientResource = worst.Type;
            eco.MaxDeficitScore = worst.DeficitScore;
            eco.MeanDeficitScore = perType.Average(x => x.DeficitScore);
            eco.BottleneckPressure = eco.MaxDeficitScore;
            eco.AbsFloor = perType.Average(x => x.RunwayCoverage);
            eco.RelativePressure = 1f - 2f * perType.Average(x => x.RelativeIncomeGap);
            eco.EconomicSecurity = Mathf.Clamp01(1f - (
                AiConfigV2.economyDesireMaxWeight * eco.MaxDeficitScore
                + AiConfigV2.economyDesireMeanWeight * eco.MeanDeficitScore));

            var standings = perType.ToDictionary(x => x.Type, x => x);
            var knownBuildings = (snap.Known?.Buildings
                ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .GroupBy(x => x.Hex).ToDictionary(g => g.Key, g => g.First());
            // The same hex may yield several resource types AND qualify as a Base site. Its
            // army routes/threat witnesses are identical within this immutable world scan;
            // compute them only once without caching across snapshots or projected-army calls.
            var builderRoutesByHex = new Dictionary<HexCoord, IReadOnlyList<EconomyBuilderRouteSnapshot>>();
            IReadOnlyList<EconomyBuilderRouteSnapshot> BuilderRoutesFor(HexCoord hex)
            {
                if (!builderRoutesByHex.TryGetValue(hex, out IReadOnlyList<EconomyBuilderRouteSnapshot> routes))
                {
                    routes = EconomyBuilderRoutes(snap, player, ctx, hex);
                    builderRoutesByHex[hex] = routes;
                }
                return routes;
            }
            var extraction = new List<EconomyExtractionOpportunity>();
            foreach ((HexCoord Hex, ResourceType Type, int Yield) site
                     in KnownExtractionYields(snap))
            {
                ResourceType resourceType = site.Type;
                int effectiveYield = site.Yield;

                if (KnownHostileAtHex(snap, site.Hex))
                    continue;

                int currentCollection = 0;
                if (knownBuildings.TryGetValue(site.Hex, out AiMapMemory.KnownBuilding building))
                {
                    // BuildingPlayExecutor can only add a Facility to our own building, and only
                    // while an unlocked slot was last observed free. A slot being free does not
                    // mean this resource type is still buildable there: HexSelectionController.
                    // Factory.TryBuildExtractionFacility separately refuses a second Facility
                    // with the same collect ability on one building — mirror that same check here
                    // (KnownBuilding.HasFacilityWithAbility, same method the live building uses)
                    // so this candidate list never proposes a site the executor will only reject.
                    if (building.Owner != player || building.FreeFacilitySlots <= 0
                        || building.HasFacilityWithAbility(UnitAbilities.CollectAbilityFor(resourceType)))
                        continue;
                    currentCollection = building.CollectedAmount(resourceType);
                }

                int ownArmyCollectors = Mathf.RoundToInt((snap.Self.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && a.Hex.Equals(site.Hex))
                    .Sum(a => a.CollectionCapacity.Get(resourceType)));
                bool armiesCanCollect = !(snap.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Any(enemy => enemy.Hex.Equals(site.Hex));
                int marginal = IncomeProjection.MarginalOwnerCollectionAtHex(
                    effectiveYield, currentCollection, 1,
                    ownArmyCollectors, armiesCanCollect);
                if (marginal <= 0)
                    continue;
                extraction.Add(new EconomyExtractionOpportunity
                {
                    Hex = site.Hex,
                    ResourceType = resourceType,
                    EffectiveYield = effectiveYield,
                    CurrentBuildingCollection = currentCollection,
                    MarginalIncomeGain = marginal,
                    BaseNetworkSynergy = EconomyBaseNetworkSynergy(snap, site.Hex),
                    BuilderRoutes = BuilderRoutesFor(site.Hex),
                });
            }
            eco.ExtractionOpportunities = extraction;

            var mobileCollection = new List<MobileCollectionOpportunity>();
            var committedCollectors = new HashSet<int>(MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active
                    && i.PreferredMoverArmyId.HasValue)
                .Select(i => i.PreferredMoverArmyId.Value));
            foreach ((HexCoord Hex, ResourceType Type, int Yield) site in KnownExtractionYields(snap))
            {
                if (!eco.IsIncomeDeficient(snap.Self, site.Type)
                    || KnownHostileAtHex(snap, site.Hex))
                    continue;

                int buildingCollection = 0;
                if (knownBuildings.TryGetValue(site.Hex, out AiMapMemory.KnownBuilding known)
                    && known.Owner == player)
                    buildingCollection = known.CollectedAmount(site.Type);
                int armiesAlreadyThere = Mathf.RoundToInt((snap.Self.Armies
                        ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && a.Hex.Equals(site.Hex))
                    .Sum(a => a.CollectionCapacity.Get(site.Type)));

                EconomyResourceStanding standing = standings[site.Type];
                float priority = TaskScoreEvaluator.ResourcePriority(standing,
                    ResourceStarvationRegistry.Pressure(player, site.Type));
                MobileCollectionOpportunity? best = null;
                foreach (ArmySnapshot collector in (snap.Self.Armies
                             ?? System.Array.Empty<ArmySnapshot>()).Where(a => a != null
                             && !a.IsAir && !a.IsAirfield && !a.IsGarrison && !a.IsPrison
                             && !committedCollectors.Contains(a.ArmyId)
                             && !a.Hex.Equals(site.Hex)
                             && a.CollectionCapacity.Get(site.Type) > 0f)
                         .OrderBy(a => a.ArmyId))
                {
                    HexPath route = SafeStepPathing.FindSafePath(ctx.Map, player,
                        collector.Hex, site.Hex, collector.MaxMovement);
                    if (route == null)
                        continue;
                    IReadOnlyList<AiMapMemory.KnownEnemySighting> threats =
                        KnownThreatsAffectingEconomyRoute(snap, route.Hexes);
                    float exposure = threats.Count > 0 ? 1f : 0f;
                    if (exposure > AiConfigV2.mobileCollectionMaxThreatExposure)
                        continue;

                    HexCoord? safeReturn = null;
                    int returnCost = int.MaxValue;
                    foreach (HexCoord home in snap.Self.BaseHexes
                                 ?? System.Array.Empty<HexCoord>())
                    {
                        int cost = SafeStepPathing.FindSafePathCost(ctx.Map, player,
                            site.Hex, home, collector.MaxMovement);
                        if (cost < returnCost)
                        {
                            returnCost = cost;
                            safeReturn = home;
                        }
                    }
                    if (!safeReturn.HasValue || returnCost == int.MaxValue)
                        continue;

                    int capacity = Mathf.RoundToInt(collector.CollectionCapacity.Get(site.Type));
                    int marginal = IncomeProjection.MarginalOwnerCollectionAtHex(
                        site.Yield, buildingCollection, capacity, armiesAlreadyThere, true);
                    if (marginal < AiConfigV2.mobileCollectionMinMarginalYield)
                        continue;

                    int remaining = Mathf.Max(0, route.TotalCost - collector.CurrentMovement);
                    int turnsToArrival = Mathf.CeilToInt(remaining
                        / (float)Mathf.Max(1, collector.MaxMovement));
                    int firstIncome = Mathf.Max(1, turnsToArrival + 1);
                    float benefit = TaskScoreEvaluator.EconomicHexBenefit(marginal, priority)
                        * AiConfigV2.mobileCollectionBenefitFactor;
                    float score = new TaskScore(
                        economicHexBenefit: benefit,
                        payback: TaskScoreEvaluator.Payback(firstIncome),
                        delivery: TaskScoreEvaluator.DeliveryFromEta(
                            collector.ActivationApCost, firstIncome, 1f),
                        moverOpportunityCost: collector.EffectiveArmyPower
                            * AiConfigV2.mobileCollectionPowerOpportunityScale,
                        hexThreatRisk: exposure).Value;
                    if (score <= AiConfigV2.allocatorSliceEpsilon)
                        continue;
                    var candidate = new MobileCollectionOpportunity(site.Hex, site.Type,
                        marginal, collector.ArmyId, route.TotalCost, firstIncome, exposure,
                        score, safeReturn.Value);
                    if (!best.HasValue || candidate.UsefulMarginalGain
                        > best.Value.UsefulMarginalGain)
                        best = candidate;
                }
                if (best.HasValue)
                    mobileCollection.Add(best.Value);
            }
            eco.MobileCollectionOpportunities = mobileCollection;

            // Base opportunities are structural site facts only. Card-specific value/cost remains
            // Strategy/Demand's responsibility, but Analysis owns the one legal candidate set so
            // the desire gate and demand emission cannot disagree. Built independently of whether a
            // physical Base card is currently in hand — a Generated Base (built via Challenge, with
            // no card in hand yet at analysis time) and a hand-played Base must target from the same
            // structural set (Task: unify Base targeting — Phase B no longer rescans the map itself).
            // Actionability (HasActionableOpportunity below) still requires a real playable carrier,
            // so storage and actionable-availability are checked separately here on purpose.
            var baseOpportunities = new List<EconomyBaseOpportunity>();
            List<CardData> baseCards = (snap.Self.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition?.cardType == CardType.Base).ToList();
            if (snap.Self.BaseHexes != null)
            {
                var occupied = knownBuildings;
                var knownSites = new HashSet<HexCoord>((snap.Known?.ResourceHexes
                    ?? System.Array.Empty<AiMapMemory.KnownResourceHex>()).Select(x => x.Hex));
                var knownMapHexes = new HashSet<HexCoord>(
                    snap.MapKnowledge?.EverSeenHexSet
                    ?? snap.MapKnowledge?.VisitedHexSet
                    ?? (ISet<HexCoord>)new HashSet<HexCoord>());
                var directionalSites = new HashSet<HexCoord>();
                bool hasDirection = TrySelectBaseExpansionDirection(snap, player,
                    out HexCoord targetCitadel, out HexCoord anchor);
                if (hasDirection)
                    directionalSites.UnionWith(knownMapHexes);
                foreach (HexCoord hex in directionalSites.OrderBy(x => x.Q).ThenBy(x => x.R))
                    {
                        // Direction is a SCORE input (ForwardProgressValue/CorridorAlignmentValue
                        // below), not an admission gate: IsForwardBaseCandidate's own "strictly
                        // closer to the enemy citadel than our anchor" rule used to also veto
                        // candidates here, before those same two score terms ever got to weigh a
                        // resource-rich hex that happened to sit slightly off the direct line.
                        // MeetsBaseSpacing (own-base clearance, a real structural constraint) is
                        // the only hard gate that stays; IsForwardBaseCandidate itself is kept for
                        // its own unit coverage (AiEconomyDecisionTests) but no longer called here.
                        if (!knownMapHexes.Contains(hex)
                            || !MeetsBaseSpacing(snap.Self.BaseHexes, hex)
                            || !hasDirection)
                            continue;
                        bool hasBuilding = occupied.TryGetValue(hex,
                            out AiMapMemory.KnownBuilding knownBuilding);
                        bool convertsOwnedExtraction = hasBuilding && knownSites.Contains(hex)
                            && knownBuilding.Owner == player && !knownBuilding.IsBase;
                        if (hasBuilding && !convertsOwnedExtraction)
                            continue;
                        if (KnownHostileAtHex(snap, hex))
                            continue;
                        int supportDistance = snap.Self.BaseHexes
                            .Min(baseHex => HexGridMath.Distance(baseHex, hex));
                        // Economy expansion must remain connected to our support network. The
                        // same sanctioned starting-citadel fact that shapes direction also prevents
                        // an unsupported site inside the opponent's immediate base perimeter.
                        if (HexGridMath.Distance(hex, targetCitadel) <= AiConfigV2.economyBaseMinSpacing
                            && HexGridMath.Distance(hex, targetCitadel) < supportDistance)
                            continue;
                        // The starting citadel and later Bases are stationary. SafeStepPathing
                        // retains their forward cost fields across scans, building a field only
                        // on first demand or when route-relevant inputs change.
                        int preparationTravel = ctx?.Map != null
                            ? SafeStepPathing.FindSafeBasePreparationCost(ctx.Map, player,
                                snap.Self.BaseHexes, hex)
                            : snap.Self.BaseHexes.Min(home => HexGridMath.Distance(home, hex));
                        if (preparationTravel == int.MaxValue)
                            continue;

                        // Analysis reads the one canonical source (TerrainTypeEntry.defenseModifier,
                        // the same field combat/threat code already reads — see WorthIt.cs,
                        // HexSelectionController.Visuals.cs), Evaluation is the only place that
                        // weighs it.
                        float defenseBonus = 0f;
                        if (ctx?.Map != null && ctx.Map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain)
                            && terrain != null)
                            defenseBonus = Mathf.Clamp01(terrain.defenseModifier
                                / Mathf.Max(1f, AiConfigV2.economyBaseMaxDefenseModifier));

                        float forwardProgress = 0f;
                        float corridorAlignment = 0f;
                        if (hasDirection)
                        {
                            int directDistance = HexGridMath.Distance(anchor, targetCitadel);
                            int candidateDistance = HexGridMath.Distance(hex, targetCitadel);
                            forwardProgress = Mathf.Clamp01(
                                (directDistance - candidateDistance)
                                / Mathf.Max(1f, directDistance));
                            int routedDistance = HexGridMath.Distance(anchor, hex)
                                + candidateDistance;
                            int detour = Mathf.Max(0, routedDistance - directDistance);
                            corridorAlignment = 1f - Mathf.Clamp01(
                                detour / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
                        }

                        baseOpportunities.Add(new EconomyBaseOpportunity
                        {
                            Hex = hex,
                            PreparationTravelCost = preparationTravel,
                            HexYield = BaseUncollectedYield(snap, hex, knownSites, hasBuilding, knownBuilding),
                            ForwardProgressValue = forwardProgress,
                            CorridorAlignmentValue = corridorAlignment,
                            DefenseBonusValue = defenseBonus,
                            ConvertsOwnedExtractionSite = convertsOwnedExtraction,
                            BuilderRoutes = BuilderRoutesFor(hex),
                        });
                    }
            }
            eco.BaseOpportunities = baseOpportunities;
            // Unchanged semantics: a structural site with no playable Base carrier in hand must not
            // by itself raise Economy desire — actionable-availability still requires a real card.
            bool baseActionable = baseOpportunities.Count > 0 && baseCards.Count > 0;
            bool extractionActionable = extraction.Any(site =>
                site.MarginalIncomeGain > AiConfigV2.allocatorSliceEpsilon);
            eco.HasActionableOpportunity = extractionActionable || baseActionable
                || mobileCollection.Count > 0;

            return eco;
        }

        // AiMapMemory owns the complete last-observed resource line. Economy enumerates that
        // frozen knowledge once per positive type and never reconstructs it from the live map.
        internal static IReadOnlyList<(HexCoord Hex, ResourceType Type, int Yield)>
            KnownExtractionYields(WorldSnapshot snap)
        {
            var result = new List<(HexCoord, ResourceType, int)>();
            if (snap?.Known?.ResourceHexes == null)
                return result;
            foreach (AiMapMemory.KnownResourceHex known in snap.Known.ResourceHexes
                         .GroupBy(x => x.Hex).Select(g => g.First())
                         .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R))
            {
                result.AddRange(PositiveResourceYields(known.Hex,
                    ObservedResourceBundle(known.Yield)));
            }
            return result;
        }

        internal static IReadOnlyList<(HexCoord Hex, ResourceType Type, int Yield)>
            PositiveResourceYields(HexCoord hex, ResourceBundle yield)
        {
            var result = new List<(HexCoord, ResourceType, int)>();
            foreach (ResourceType type in ResourceBundle.All)
            {
                int amount = Mathf.RoundToInt(yield.Get(type));
                if (amount > 0)
                    result.Add((hex, type, amount));
            }
            return result;
        }

        internal static bool TrySelectBaseExpansionDirection(WorldSnapshot snap,
            PlayerSetupData player, out HexCoord targetCitadel, out HexCoord anchor)
        {
            targetCitadel = default;
            anchor = default;
            if (snap?.Self?.BaseHexes == null || snap.Self.BaseHexes.Count == 0)
                return false;
            List<HexCoord> targets = (snap.Known?.Buildings
                    ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Where(b => b.IsStartingCitadel && b.Owner != null && b.Owner != player
                    && !b.Owner.IsNeutral && !b.Owner.IsEliminated)
                .Select(b => b.Hex)
                .ToList();
            if (targets.Count == 0)
            {
                // Explicit Economy knowledge exception: initial expansion must not be disabled
                // until Recon happens to find a distant opponent. TrueWorld already isolates the
                // sanctioned cheat; only starting-citadel coordinates cross this boundary.
                targets = (snap.TrueWorld?.AllBuildings
                        ?? System.Array.Empty<BuildingSnapshot>())
                    .Where(b => b != null && b.IsStartingCitadel && b.Owner != null
                        && b.Owner != player && !b.Owner.IsNeutral && !b.Owner.IsEliminated)
                    .Select(b => b.Hex)
                    .ToList();
            }
            if (targets.Count == 0)
                return false;
            HexCoord citadel = targets
                .OrderBy(b => snap.Self.BaseHexes.Min(h => HexGridMath.Distance(h, b)))
                .ThenBy(b => b.Q).ThenBy(b => b.R)
                .First();
            targetCitadel = citadel;
            anchor = snap.Self.BaseHexes
                .OrderBy(h => HexGridMath.Distance(h, citadel))
                .ThenBy(h => h.Q).ThenBy(h => h.R).First();
            return true;
        }

        internal static bool IsForwardBaseCandidate(IReadOnlyList<HexCoord> ownBases,
            HexCoord anchor, HexCoord targetCitadel, HexCoord candidate)
        {
            if (ownBases == null || ownBases.Count == 0)
                return false;
            return MeetsBaseSpacing(ownBases, candidate)
                && HexGridMath.Distance(candidate, targetCitadel)
                    < HexGridMath.Distance(anchor, targetCitadel);
        }

        internal static bool MeetsBaseSpacing(IReadOnlyList<HexCoord> ownBases,
            HexCoord candidate) => ownBases != null && ownBases.Count > 0
            && ownBases.Min(h => HexGridMath.Distance(h, candidate))
                >= AiConfigV2.economyBaseMinSpacing;

        internal static IReadOnlyList<EconomyBuilderRouteSnapshot> EconomyBuilderRoutes(
            WorldSnapshot snap, PlayerSetupData player, AiTurnContext ctx, HexCoord target,
            ArmySnapshot projectedArmy = null)
        {
            var result = new List<EconomyBuilderRouteSnapshot>();
            if (snap?.Self?.Armies == null || player == null || ctx?.Map == null)
                return result;

            Dictionary<int, ArmyData> liveById = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null).ToDictionary(a => a.Id);
            HashSet<int> activeEconomyActors = new HashSet<int>(
                MissionIntentRegistry.GetOrCreate(player).All
                    .Where(i => i != null && i.Status == IntentStatus.Active
                        && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId.HasValue)
                    .Select(i => i.PreferredMoverArmyId.Value));
            IEnumerable<ArmySnapshot> candidates = projectedArmy != null
                ? new[] { projectedArmy } : snap.Self.Armies;
            foreach (ArmySnapshot army in candidates
                         .Where(a => a != null).OrderBy(a => a.ArmyId))
            {
                if (army.IsGarrison)
                {
                    if (!liveById.TryGetValue(army.ArmyId, out ArmyData liveGarrison))
                        continue;
                    UnitData sparableHero = AiArmyRoles.BestSparableEconomyHero(player, liveGarrison);
                    if (sparableHero == null)
                        continue;
                    if (army.Hex.Equals(target))
                    {
                        // A garrison is NEVER a mobile hero army. Even when it already occupies
                        // the build hex, Provisioning must extract a genuinely sparable hero into
                        // a field container before Economy can execute the build.
                        result.Add(new EconomyBuilderRouteSnapshot
                        {
                            ArmyId = army.ArmyId, TravelCost = 0, ReturnTravelCost = 0,
                            CurrentMovement = sparableHero.MoveMax, MaxMovement = sparableHero.MoveMax,
                            ActivationApCost = sparableHero.ActivationApCost, ArmySize = 1,
                            HasActivatedThisTurn = false,
                            EffectiveArmyPower = AiPower.ToPowerUnit(sparableHero).BasePower,
                            HasActiveEconomyCommitment = activeEconomyActors.Contains(army.ArmyId),
                            IsOnTarget = true, RequiresGarrisonExtraction = true,
                            PathHexes = new[] { target },
                            RouteThreats = KnownThreatsAffectingEconomyRoute(snap, new[] { target }),
                        });
                        continue;
                    }

                    // An idle hero sitting in this Garrison is a legitimate mobile_hero candidate
                    // too — priced with the exact same SafeStepPathing math as any field army below,
                    // just rooted at the hero's own MoveMax rather than an already-existing army's.
                    // AiArmyRoles.CanSpareGarrisonMember (the canonical predicate Raid's own donor
                    // path already trusts) gates which hero, if any, is even considered — the
                    // Citadel/base secure floor is never at risk. Analysis only prices the option;
                    // it never spends AP. The actual extraction (ArmyActions.TransferMember into an
                    // already-existing free reusable shell) happens later, transactionally, in
                    // ProvisioningManager — if no free shell exists this turn the candidate is
                    // simply not offered, and Economy falls back to its existing card-materialization
                    // path unchanged.
                    HexPath garrisonRoute = SafeStepPathing.FindSafePath(
                        ctx.Map, player, army.Hex, target, sparableHero.MoveMax);
                    if (garrisonRoute != null)
                    {
                        int garrisonReturnCost = SafeStepPathing.FindNearestBaseReturnCost(
                            ctx.Map, player, target, snap.Self.BaseHexes, sparableHero.MoveMax);
                        if (garrisonReturnCost == int.MaxValue)
                            garrisonReturnCost = HexGridMath.Distance(target, army.Hex);

                        result.Add(new EconomyBuilderRouteSnapshot
                        {
                            ArmyId = army.ArmyId,
                            TravelCost = garrisonRoute.TotalCost,
                            ReturnTravelCost = garrisonReturnCost,
                            CurrentMovement = sparableHero.MoveMax,
                            MaxMovement = sparableHero.MoveMax,
                            ActivationApCost = sparableHero.ActivationApCost,
                            HasActivatedThisTurn = false,
                            ArmySize = 1,
                            EffectiveArmyPower = AiPower.ToPowerUnit(sparableHero).BasePower,
                            HasActiveEconomyCommitment = false,
                            IsOnTarget = false,
                            RequiresGarrisonExtraction = true,
                            PathHexes = garrisonRoute.Hexes.ToList(),
                            RouteThreats = KnownThreatsAffectingEconomyRoute(
                                snap, garrisonRoute.Hexes),
                        });
                    }
                    continue;
                }
                if (!army.IsMobileEconomyBuilder
                    || (projectedArmy == null && !liveById.ContainsKey(army.ArmyId)))
                    continue;
                // maxMovement hard-blocks any hex this army could never enter in one step (see
                // SafeStepPathing.FindSafePath, generalising the old post-hoc bd283fb reject into
                // the search itself), so the route returned — if any — is already guaranteed
                // usable; the per-hex re-check below is now just a defensive safety net.
                HexPath route = SafeStepPathing.FindSafePath(
                    ctx.Map, player, army.Hex, target, army.MaxMovement);
                if (route == null)
                    continue;
                int cost = route.TotalCost;
                bool everyStepAffordable = true;
                for (int i = 1; i < route.Hexes.Count && everyStepAffordable; i++)
                {
                    if (!ctx.Map.TryGetTerrainAt(route.Hexes[i], out TerrainTypeEntry stepEntry))
                        continue;
                    int stepCost = Mathf.Max(1, stepEntry.moveCost);
                    if (stepCost > army.MaxMovement)
                        everyStepAffordable = false;
                }
                if (!everyStepAffordable)
                    continue;
                int returnCost = SafeStepPathing.FindNearestBaseReturnCost(
                    ctx.Map, player, target, snap.Self.BaseHexes, army.MaxMovement);
                if (returnCost == int.MaxValue)
                    returnCost = HexGridMath.Distance(target, army.Hex);
                result.Add(new EconomyBuilderRouteSnapshot
                {
                    ArmyId = army.ArmyId,
                    TravelCost = cost,
                    ReturnTravelCost = returnCost,
                    CurrentMovement = army.CurrentMovement,
                    MaxMovement = army.MaxMovement,
                    ActivationApCost = army.ActivationApCost,
                    HasActivatedThisTurn = army.HasActivatedThisTurn,
                    ArmySize = army.MemberCount,
                    EffectiveArmyPower = army.EffectiveArmyPower,
                    HasActiveEconomyCommitment = activeEconomyActors.Contains(army.ArmyId),
                    IsOnTarget = army.Hex.Equals(target),
                    PathHexes = route.Hexes.ToList(),
                    RouteThreats = KnownThreatsAffectingEconomyRoute(
                        snap, route.Hexes),
                });
            }
            return result;
        }

        // Route exposure is derived from the exact SafeStepPathing witness. Known neutral
        // armies are stationary blockers and only matter when they occupy the route itself;
        // mobile enemy armies can threaten an adjacent route hex. Both inputs remain fog-honest.
        internal static IReadOnlyList<AiMapMemory.KnownEnemySighting>
            KnownThreatsAffectingEconomyRoute(WorldSnapshot snap,
                IReadOnlyList<HexCoord> pathHexes)
        {
            if (pathHexes == null || pathHexes.Count == 0)
                return System.Array.Empty<AiMapMemory.KnownEnemySighting>();

            var path = new HashSet<HexCoord>(pathHexes);
            var threats = new List<AiMapMemory.KnownEnemySighting>();
            foreach (AiMapMemory.KnownEnemySighting enemy in snap?.Known?.EnemySightings
                         ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                if (path.Any(hex => HexGridMath.Distance(hex, enemy.Hex) <= 1))
                    threats.Add(enemy);
            foreach (AiMapMemory.KnownEnemySighting neutral in snap?.Known?.NeutralSightings
                         ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                if (path.Contains(neutral.Hex))
                    threats.Add(neutral);
            return threats
                .OrderBy(x => x.Owner?.IsNeutral == true ? 1 : 0)
                .ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                .ThenBy(x => x.ArmyId)
                .ToList();
        }

        // A build target with a live enemy or neutral sighting exactly ON it is not an escort
        // problem — SafeStepPathing always exempts the destination hex from its own blocking (see
        // that class's own comment), so nothing upstream stops a mover from walking straight onto
        // an occupied hex and building there. This is the one gate that actually prevents it.
        // Shared between the Base and Extraction candidate loops below so the rule (and its
        // fog-of-war honesty) lives in exactly one place.
        internal static bool KnownHostileAtHex(WorldSnapshot snap, HexCoord hex) =>
            (snap?.Known?.EnemySightings ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap?.Known?.NeutralSightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Any(contact => contact.Hex.Equals(hex));

        private static float EconomyBaseNetworkSynergy(WorldSnapshot snap, HexCoord target)
        {
            if (snap?.Self?.BaseHexes == null || snap.Self.BaseHexes.Count == 0)
                return 0f;
            int distance = snap.Self.BaseHexes.Min(h => HexGridMath.Distance(h, target));
            return 1f - Mathf.Clamp01((distance - 1f)
                / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
        }

        // Structural site fact only — how much of this hex's yield is left uncollected by
        // whatever building already sits here (raw yield minus its CollectedAmount, per type).
        // Deliberately NOT "how much a Base would add": that depends on which Base card's own
        // grantedAbilities actually get baked onto the new building (BuildingData.CollectedAmount
        // has no more special-cased IsBase branch — a Base earns 1 per its own baked Collect
        // ability, same as any building, plus 1 + UpgradeLevel per placed Facility), and this
        // opportunity record is card-agnostic by design. StrategicCardEvaluator uses
        // BaseCardMarginalGain to apply the card and subtract any owned army collection.
        // Zero for a hex with no known resource site, same as before this was split out.
        private static ResourceBundle BaseUncollectedYield(WorldSnapshot snap, HexCoord hex,
            HashSet<HexCoord> knownSites, bool hasBuilding, AiMapMemory.KnownBuilding building)
        {
            if (!knownSites.Contains(hex))
                return default(ResourceBundle);
            ResourceBundle rawYield = EconomyKnownHexYield(snap, hex);
            var remaining = new ResourceBundle();
            foreach (ResourceType type in ResourceBundle.All)
            {
                int currentCollection = hasBuilding ? building.CollectedAmount(type) : 0;
                remaining.Add(type, Mathf.Max(0f, rawYield.Get(type) - currentCollection));
            }
            return remaining;
        }

        private static ResourceBundle EconomyKnownHexYield(WorldSnapshot snap, HexCoord hex) =>
            snap?.Known?.ResourceHexes == null
                ? default(ResourceBundle)
                : snap.Known.ResourceHexes
                    .Where(x => x.Hex.Equals(hex))
                    .Select(x => ObservedResourceBundle(x.Yield))
                    .FirstOrDefault();

        private static ResourceBundle ObservedResourceBundle(ResourceYields yield) =>
            yield == null ? default(ResourceBundle) : new ResourceBundle
            {
                Human = yield.Get(ResourceType.Human),
                Energy = yield.Get(ResourceType.Energy),
                Materials = yield.Get(ResourceType.Materials),
                Tech = yield.Get(ResourceType.Tech),
            };

        private static void AccumulateCardCosts(IEnumerable<CardData> cards, ref ResourceBundle need)
        {
            foreach (CardData card in cards)
            {
                CardDefinition d = card?.Definition;
                if (d == null) continue;
                if (d.cardType != CardType.Unit && d.cardType != CardType.Hero
                    && d.cardType != CardType.Facility && d.cardType != CardType.Base)
                    continue;
                ResourceCost cost = CardCostRules.PlayResources(card);
                if (cost == null) continue;
                foreach (ResourceType t in ResourceBundle.All)
                    need.Add(t, cost.Get(t));
            }
        }

        private static void AccumulateCardCosts(IEnumerable<CardDefinition> defs, ref ResourceBundle need)
        {
            foreach (CardDefinition d in defs)
            {
                if (d == null || d.resourceCost == null) continue;
                if (d.cardType != CardType.Unit && d.cardType != CardType.Hero
                    && d.cardType != CardType.Facility && d.cardType != CardType.Base)
                    continue;
                foreach (ResourceType t in ResourceBundle.All)
                    need.Add(t, d.resourceCost.Get(t));
            }
        }

        private static float Median(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            int n = values.Count;
            return n % 2 == 1 ? values[n / 2] : 0.5f * (values[n / 2 - 1] + values[n / 2]);
        }

    }
}
