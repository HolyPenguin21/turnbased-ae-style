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

            // Price only the roster that can actually depart. Preparation-site selection is
            // structural and does not depend on the Economy standings being built here.
            var preparationSite = DevelopmentOpportunityEvaluator.LivePreparationSite(
                player, AiHandRegistry.Peek(player), ctx, snap.Intents);
            ActorCommitments commitments = ActorCommitments.FromIntents(
                WorldSnapshot.IntentsOf(snap), snap, null);
            foreach (ArmySnapshot actor in snap.Self.Armies.Where(a => a != null
                         && !a.IsGarrison && !a.IsPrison && !a.IsAir && !a.IsAirfield
                         && !a.IsMobileEconomyBuilder))
            {
                ArmyData live = AiV2Util.ResolveArmy(player, actor.ArmyId);
                if (!LocalOperatorRelease.CanKeepOperatorsHome(player, live, commitments,
                        preparationSite, out _, out ArmyData departure)
                    || ReferenceEquals(live, departure) || !AiArmyRoles.IsHeroLed(departure))
                    continue;
                ArmySnapshot projected = ToArmySnapshot(departure, player, true, 0, snap.Intents);
                projected.ArmyId = actor.ArmyId;
                projected.EconomyRosterProtected = actor.EconomyRosterProtected;
                projected.ActivationCoveredUnitRuntimeIds = actor.ActivationCoveredUnitRuntimeIds;
                actor.EconomyDeparture = projected;
            }

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
            ResourceBundle forecastNeed = ResourceStarvationRegistry.ForecastOperationalNeed(
                player, ctx.TurnNumber);
            eco.ForecastOperationalNeed = forecastNeed;
            foreach (ResourceType t in ResourceBundle.All)
            {
                float own = snap.Self.PerTurnIncome.Get(t);
                // Do not ask IncomeProjection for another player's LIVE map income: doing so
                // changes Economy and Production desires after events hidden from this observer.
                // Known buildings and resource yields provide only a last-observed LOWER BOUND:
                // unseen collectors and Produce sources cannot be reconstructed from memory.
                // Unknown opponents contribute zero rather than a fabricated income estimate.
                var otherIncomes = others.Select(p => ObservedOpponentIncomeFloor(snap, p, t)).ToList();
                float median = Median(otherIncomes);
                float reserved = 0f;
                foreach (ResourceClaim claim in TurnResourceBook.LedgerClaims(
                             player, ctx.TurnNumber, StrategicResourceReservationLedger.Map(t)))
                    reserved += Mathf.Max(0f, claim.Amount);
                float spendable = StrategicSpendability.SpendableAmount(player, root, ctx, t);
                reservedNeed.Add(t, reserved);
                spendableStock.Add(t, spendable);
                perType.Add(EconomyStanding.CalculateResource(t, own, median,
                    handNeed.Get(t), deckNeed.Get(t), reserved, spendable,
                    ResourceStarvationRegistry.Pressure(player, t), forecastNeed.Get(t)));
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
            EconomyBuilderPool builderPool = null;
            IReadOnlyList<EconomyBuilderRouteSnapshot> BuilderRoutesFor(HexCoord hex)
            {
                if (!builderRoutesByHex.TryGetValue(hex, out IReadOnlyList<EconomyBuilderRouteSnapshot> routes))
                {
                    using (new ProfileScope("AI/Economy.BuilderRoutes"))
                    {
                        builderPool ??= new EconomyBuilderPool(snap, player, ctx);
                        routes = EconomyBuilderRoutes(snap, player, ctx, hex, null, builderPool);
                    }
                    builderRoutesByHex[hex] = routes;
                }
                return routes;
            }
            var extraction = new List<EconomyExtractionOpportunity>();
            // A capability fact, never a card choice: even the fastest known prospective hero
            // cannot beat this optimistic terrain-only preparation bound. Exact safety/escort
            // and the chosen card's route remain Materialization's responsibility.
            int prospectiveHeroMovement = (snap.Self.PoolCards
                    ?? System.Array.Empty<(CardDefinition Card, CardDefinition Equipment, CardDefinition Mutator, bool InHand)>())
                .Where(c => c.Card?.cardType == CardType.Hero)
                .Select(c => AiPower.EffectiveLine(c.Card, c.Equipment?.equipment, c.Mutator?.equipment).MoveMax)
                .DefaultIfEmpty(0).Max();
            var newBuilderDeliveryByHex = new Dictionary<HexCoord, int>();
            int MinimumNewBuilderTurns(HexCoord hex)
            {
                if (newBuilderDeliveryByHex.TryGetValue(hex, out int turns)) return turns;
                int travel = prospectiveHeroMovement > 0 && snap.Self.BaseHexes?.Count > 0
                    ? SafeStepPathing.FindSafeBasePreparationCost(ctx.Map, player, snap.Self.BaseHexes, hex)
                    : int.MaxValue;
                turns = travel == int.MaxValue ? int.MaxValue
                    : Mathf.CeilToInt(travel / (float)prospectiveHeroMovement);
                newBuilderDeliveryByHex[hex] = turns;
                return turns;
            }
            var collectorSites = new List<EconomyExtractionOpportunity>();
            var guardedExtraction = new List<EconomyExtractionOpportunity>();
            using (new ProfileScope("AI/Economy.ExtractionSites"))
            foreach ((HexCoord Hex, ResourceType Type, int Yield) site
                     in KnownExtractionYields(snap))
            {
                ResourceType resourceType = site.Type;
                int effectiveYield = site.Yield;
                if (KnownHostileAtHex(snap, site.Hex))
                {
                    // Only a neutral standing on it: not an Economy opportunity, but the price a
                    // Raid that clears it earns (the facility income, nothing else).
                    if (KnownNeutralOnlyAtHex(snap, site.Hex)
                        && !knownBuildings.ContainsKey(site.Hex))
                    {
                        (int guardedFacilityGain, _) = MarginalSiteCollectionGains(
                            effectiveYield, 0, 0, true);
                        if (guardedFacilityGain > 0)
                            guardedExtraction.Add(new EconomyExtractionOpportunity
                            {
                                Hex = site.Hex,
                                ResourceType = resourceType,
                                EffectiveYield = effectiveYield,
                                MarginalIncomeGain = guardedFacilityGain,
                                BaseNetworkSynergy = EconomyBaseNetworkSynergy(snap, site.Hex),
                                BuilderRoutes = System.Array.Empty<EconomyBuilderRouteSnapshot>(),
                            });
                    }
                    continue;
                }

                bool hasBuilding = knownBuildings.TryGetValue(site.Hex,
                    out AiMapMemory.KnownBuilding building);
                // Real income consumes the building's portion FIRST, even if its owner is
                // another player. An extraction Facility can found a NEW resource site;
                // only EXISTING hosts require our ownership/free slots. A field Collector is independent.
                int currentCollection = hasBuilding ? building.CollectedAmount(resourceType) : 0;
                int ownArmyCollectors = Mathf.RoundToInt((snap.Self.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && a.Hex.Equals(site.Hex))
                    .Sum(a => a.CollectionCapacity.Get(resourceType)));
                bool armiesCanCollect = !(snap.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Any(enemy => enemy.Hex.Equals(site.Hex));
                (int facilityGain, int collectorGain) = MarginalSiteCollectionGains(
                    effectiveYield, currentCollection, ownArmyCollectors, armiesCanCollect);

                if (collectorGain > 0)
                    collectorSites.Add(new EconomyExtractionOpportunity
                    {
                        Hex = site.Hex,
                        ResourceType = resourceType,
                        EffectiveYield = effectiveYield,
                        CurrentBuildingCollection = currentCollection,
                        MarginalIncomeGain = collectorGain,
                        BaseNetworkSynergy = EconomyBaseNetworkSynergy(snap, site.Hex),
                        BuilderRoutes = System.Array.Empty<EconomyBuilderRouteSnapshot>(),
                    });

                // HexSelectionController.TryBuildExtractionFacility may found a NEW resource site;
                // only EXISTING buildings require our ownership, free Facility slots,
                // and no duplicate collection facility. In particular,
                // none of those rules may filter the independent mobile collector list.
                if (facilityGain <= 0 || !IsExtractionHostStructurallyLegal(
                        hasBuilding, building, player, resourceType))
                    continue;
                extraction.Add(new EconomyExtractionOpportunity
                {
                    Hex = site.Hex,
                    ResourceType = resourceType,
                    EffectiveYield = effectiveYield,
                    CurrentBuildingCollection = currentCollection,
                    MarginalIncomeGain = facilityGain,
                    BaseNetworkSynergy = EconomyBaseNetworkSynergy(snap, site.Hex),
                    BuilderRoutes = BuilderRoutesFor(site.Hex),
                    MinimumNewBuilderDeliveryTurns = MinimumNewBuilderTurns(site.Hex),
                });
            }
            eco.CollectorSites = collectorSites;
            eco.ExtractionOpportunities = extraction;
            eco.GuardedExtractionSites = guardedExtraction;

            var mobileCollection = new List<MobileCollectionOpportunity>();
            var collectorRoutes = new Dictionary<(int, HexCoord), HexPath>();
            var committedCollectors = new HashSet<int>(WorldSnapshot.IntentsOf(snap)
                .Where(i => i != null && i.Status == IntentStatus.Active
                    && i.PreferredMoverArmyId.HasValue)
                .Select(i => i.PreferredMoverArmyId.Value));
            using (new ProfileScope("AI/Economy.Collectors"))
            foreach (EconomyExtractionOpportunity site in collectorSites)
            {
                // Demand admits a new collector for USEFUL income, not strictly for a
                // rate below IncomeTarget. Apply that same UsefulMarginalIncomeGain test
                // below to existing free actors, so a newly delivered collector gets work.
                int buildingCollection = site.CurrentBuildingCollection;
                int armiesAlreadyThere = Mathf.RoundToInt((snap.Self.Armies
                        ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && a.Hex.Equals(site.Hex))
                    .Sum(a => a.CollectionCapacity.Get(site.ResourceType)));

                EconomyResourceStanding standing = standings[site.ResourceType];
                foreach (ArmySnapshot collector in (snap.Self.Armies
                             ?? System.Array.Empty<ArmySnapshot>()).Where(a => a != null
                             && !a.IsAir && !a.IsAirfield && !a.IsGarrison && !a.IsPrison
                             && !committedCollectors.Contains(a.ArmyId)
                             && !a.Hex.Equals(site.Hex)
                             && a.CollectionCapacity.Get(site.ResourceType) > 0f)
                         .OrderBy(a => a.ArmyId))
                {
                    HexPath route = SafeStepPathing.FindSafePath(ctx.Map, player,
                        collector.Hex, site.Hex, collector.MaxMovement);
                    if (route == null)
                        continue;
                    collectorRoutes[(collector.ArmyId, site.Hex)] = route;
                    // A collector travels alone: any listed threat rejects the site.
                    if (KnownThreatsAffectingEconomyRoute(snap, route.Hexes).Count > 0)
                        continue;

                    IReadOnlyDictionary<HexCoord, int> homes = EconomyHomeRoutes(player,
                        ctx, site.Hex, collector.MaxMovement, snap.Self.BaseHexes);
                    HexCoord? safeReturn = EconomyContinuity.SelectEconomyHome(snap,
                        site.Hex, homes);
                    if (!safeReturn.HasValue)
                        continue;

                    int capacity = Mathf.RoundToInt(collector.CollectionCapacity.Get(site.ResourceType));
                    int marginal = Mathf.Max(0,
                        IncomeProjection.OwnerCollectionAtHex(site.EffectiveYield,
                            buildingCollection, armiesAlreadyThere + capacity, true)
                        - IncomeProjection.OwnerCollectionAtHex(site.EffectiveYield,
                            buildingCollection, armiesAlreadyThere, true));
                    if (marginal < AiConfigV2.mobileCollectionMinMarginalYield)
                        continue;
                    float usefulGain = standing.UsefulMarginalIncomeGain(marginal);
                    if (usefulGain <= AiConfigV2.allocatorSliceEpsilon)
                        continue;

                    int remaining = Mathf.Max(0, route.TotalCost - collector.CurrentMovement);
                    int turnsToArrival = Mathf.CeilToInt(remaining
                        / (float)Mathf.Max(1, collector.MaxMovement));
                    int firstIncome = Mathf.Max(1, turnsToArrival + 1);
                    // Analysis stops here: this is a viable physical/economic opportunity.
                    // EconomyMissionPlanner owns the one external TaskScore assembly and chooses
                    // which collector wins when multiple actors can serve the same site.
                    mobileCollection.Add(new MobileCollectionOpportunity(
                        site.Hex, site.ResourceType, marginal, collector.ArmyId,
                        route.TotalCost, firstIncome, safeReturn.Value));
                }
            }
            eco.MobileCollectionOpportunities = mobileCollection;

            // Free candidates already supplied their route above. Only bound site-holders need
            // an additional witness; never rescan every collector against the whole map.
            foreach (MissionIntent intent in WorldSnapshot.IntentsOf(snap).Where(i =>
                         i?.Economy?.Kind == EconomyTaskKind.MobileCollection && i.PreferredMoverArmyId.HasValue))
            {
                ArmySnapshot collector = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.ArmyId == intent.PreferredMoverArmyId.Value);
                if (collector == null) continue;
                HexCoord target = intent.Economy.TargetHex;
                HexPath route = SafeStepPathing.FindSafePath(ctx.Map, player,
                    collector.Hex, target, collector.MaxMovement);
                if (route != null) collectorRoutes[(collector.ArmyId, target)] = route;
            }
            eco.CollectorRoutes = collectorRoutes;

            // Base opportunities are structural site facts only. Card-specific value/cost remains
            // Strategy/Demand's responsibility, but Analysis owns the one legal candidate set so
            // the desire gate and demand emission cannot disagree. Built independently of whether a
            // physical Base card is currently in hand — a Generated Base (built via Challenge, with
            // no card in hand yet at analysis time) and a hand-played Base must target from the same
            // structural set (Task: unify Base targeting — Phase B no longer rescans the map itself).
            // Actionability (HasActionableOpportunity below) still requires a real playable carrier,
            // so storage and actionable-availability are checked separately here on purpose.
            var baseOpportunities = new List<EconomyBaseOpportunity>();
            var guardedBaseSites = new List<GuardedBaseSite>();
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
                bool hasDirection = TrySelectStrategicDirection(snap, player,
                    out HexCoord targetCitadel, out HexCoord anchor);
                if (hasDirection)
                    directionalSites.UnionWith(knownMapHexes);
                using (new ProfileScope("AI/Economy.BaseSites"))
                foreach (HexCoord hex in directionalSites.OrderBy(x => x.Q).ThenBy(x => x.R))
                    {
                        // Direction is a SCORE input (ForwardProgressValue/CorridorAlignmentValue
                        // below), not an admission gate: IsForwardBaseCandidate's own "strictly
                        // closer to the enemy citadel than our anchor" rule used to also veto
                        // candidates here, before those same two score terms ever got to weigh a
                        // resource-rich hex that happened to sit slightly off the direct line.
                        // Own-network distance is priced by FoundBase TaskScore, never a veto.
                        // Building occupancy and the enemy perimeter remain physical gates.
                        if (!knownMapHexes.Contains(hex)
                            || !hasDirection)
                            continue;
                        bool hasBuilding = occupied.TryGetValue(hex,
                            out AiMapMemory.KnownBuilding knownBuilding);
                        bool convertsOwnedExtraction = hasBuilding && knownSites.Contains(hex)
                            && knownBuilding.Owner == player && !knownBuilding.IsBase;
                        if (hasBuilding && !convertsOwnedExtraction)
                            continue;
                        if (KnownHostileAtHex(snap, hex))
                        {
                            if (!hasBuilding && KnownNeutralOnlyAtHex(snap, hex))
                                guardedBaseSites.Add(new GuardedBaseSite
                                {
                                    Hex = hex,
                                    NewResourceClusterHexes = CountNewResourceClusterHexes(
                                        hex, snap.Self.BaseHexes, knownSites),
                                });
                            continue;
                        }
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
                            forwardProgress = ForwardProgressToward(anchor, targetCitadel, hex);
                            corridorAlignment = CorridorAlignmentToward(anchor, targetCitadel, hex,
                                AiConfigV2.economyBaseFoundScanRadius);
                        }

                        baseOpportunities.Add(new EconomyBaseOpportunity
                        {
                            Hex = hex,
                            PreparationTravelCost = preparationTravel,
                            HexYield = BaseUncollectedYield(snap, hex, knownSites, hasBuilding, knownBuilding),
                            ForwardProgressValue = forwardProgress,
                            CorridorAlignmentValue = corridorAlignment,
                            DefenseBonusValue = defenseBonus,
                            NewResourceClusterHexes = CountNewResourceClusterHexes(
                                hex, snap.Self.BaseHexes, knownSites),
                            ConvertsOwnedExtractionSite = convertsOwnedExtraction,
                            BuilderRoutes = BuilderRoutesFor(hex),
                        });
                    }
            }
            eco.BaseOpportunities = baseOpportunities;
            eco.GuardedBaseSites = guardedBaseSites;
            // Unchanged semantics: a structural site with no playable Base carrier in hand must not
            // by itself raise Economy desire — actionable-availability still requires a real card.
            bool baseActionable = baseOpportunities.Count > 0 && baseCards.Count > 0;
            bool extractionActionable = extraction.Any(site =>
                site.MarginalIncomeGain > AiConfigV2.allocatorSliceEpsilon);
            // This is the same structural-opportunity interpretation as extractionActionable:
            // Phase A/Materialization remain the sole owners of physical card and route admission.
            bool collectorActionable = collectorSites.Any(site =>
                standings[site.ResourceType].UsefulMarginalIncomeGain(site.MarginalIncomeGain)
                    > AiConfigV2.allocatorSliceEpsilon);
            // A held global income carrier is a structural Economy opportunity too. Exact
            // placement, affordability and card choice remain with Materialization.
            bool globalSourceActionable = (snap.Self.Hand ?? System.Array.Empty<CardData>())
                .Any(c => c?.Definition != null && !c.Definition.isAviation
                    && (c.Definition.cardType == CardType.Unit || c.Definition.cardType == CardType.Hero
                        || c.Definition.cardType == CardType.Facility)
                    && StrategicEffectRegistry.HasGlobalRecurringEffect(
                        MaterializationChainMatching.EffectiveAbilities(c.Definition, c.Equipment, c.Mutator)));
            eco.HasActionableOpportunity = globalSourceActionable || extractionActionable || baseActionable
                || collectorActionable || mobileCollection.Count > 0;

            return eco;
        }

        // An observed building's collection is a lower bound on opponent income, not its
        // complete income. Clamp to the last-observed yield and never read live registries,
        // unobserved collector units or Produce sources at the Economy knowledge boundary.
        internal static float ObservedOpponentIncomeFloor(WorldSnapshot snap,
            PlayerSetupData opponent, ResourceType type)
        {
            if (opponent == null || snap?.Known?.Buildings == null)
                return 0f;
            return snap.Known.Buildings.Where(b => b.Owner == opponent)
                .Sum(b => Mathf.Min(b.CollectedAmount(type),
                    Mathf.Max(0f, EconomyKnownHexYield(snap, b.Hex).Get(type))));
        }

        // Mirrors the HOST constraints in the live TryBuildExtractionFacility primitive.
        // An empty resource hex can FOUND a site; only a pre-existing building
        // needs matching owner, one free slot, and no duplicate collection.
        internal static bool IsExtractionHostStructurallyLegal(
            bool hasBuilding, AiMapMemory.KnownBuilding building,
            PlayerSetupData player, ResourceType type) =>
            !hasBuilding || (building.Owner == player && building.FreeFacilitySlots > 0
                && !building.HasFacilityWithAbility(UnitAbilities.CollectAbilityFor(type)));

        // The only Analysis-level projection of the TWO different additions at a site.
        // IncomeProjection owns all resource physics; this preserves a Facility's net
        // owner gain when an army would merely lose the same slice, independently of
        // the gain from deploying an additional mobile collector onto the remainder.
        internal static (int FacilityGain, int CollectorGain) MarginalSiteCollectionGains(
            int effectiveYield, int buildingCollection, int ownArmyCollectors,
            bool armiesCanCollect)
        {
            int before = IncomeProjection.OwnerCollectionAtHex(effectiveYield,
                buildingCollection, ownArmyCollectors, armiesCanCollect);
            int facility = IncomeProjection.MarginalOwnerCollectionAtHex(effectiveYield,
                buildingCollection, 1, ownArmyCollectors, armiesCanCollect);
            int collector = Mathf.Max(0, IncomeProjection.OwnerCollectionAtHex(
                effectiveYield, buildingCollection, ownArmyCollectors + 1,
                armiesCanCollect) - before);
            return (facility, collector);
        }

        internal static int CollectorMarginalIncome(WorldSnapshot snap, ArmySnapshot actor,
            HexCoord target, ResourceType type)
        {
            if (actor == null || KnownHostileAtHex(snap, target)) return 0;
            int yield = KnownExtractionYields(snap).Where(x => x.Hex.Equals(target) && x.Type == type)
                .Select(x => x.Yield).DefaultIfEmpty(0).Max();
            int building = (snap?.Known?.Buildings ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Where(b => b.Hex.Equals(target)).Select(b => b.CollectedAmount(type)).DefaultIfEmpty(0).Max();
            int otherCollectors = Mathf.RoundToInt((snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.ArmyId != actor.ArmyId && a.Hex.Equals(target))
                .Sum(a => a.CollectionCapacity.Get(type)));
            int capacity = Mathf.RoundToInt(actor.CollectionCapacity.Get(type));
            return Mathf.Max(0, IncomeProjection.OwnerCollectionAtHex(yield, building,
                    otherCollectors + capacity, true)
                - IncomeProjection.OwnerCollectionAtHex(yield, building, otherCollectors, true));
        }

        internal static bool CollectorRouteSafe(WorldSnapshot snap, ArmySnapshot actor, HexCoord target)
        {
            if (actor == null || KnownHostileAtHex(snap, target)) return false;
            IReadOnlyList<HexCoord> path;
            if (actor.Hex.Equals(target)) path = new[] { target };
            else if (snap?.Economy?.CollectorRoutes != null)
            {
                if (!snap.Economy.CollectorRoutes.TryGetValue((actor.ArmyId, target), out HexPath route))
                    return false;
                path = route.Hexes;
            }
            else return false;
            return KnownThreatsAffectingEconomyRoute(snap, path).Count == 0;
        }

        internal static bool CollectorRouteSafeLive(PlayerSetupData player, AiTurnContext ctx,
            ArmyData actor, HexCoord target, ResourceType? resourceType)
        {
            if (actor == null || ctx?.Map == null) return false;
            if (!resourceType.HasValue || actor.IsGarrison || actor.IsPrison || actor.IsAirArmy || actor.IsAirfield
                || !actor.Members.Any(u => u != null && u.HasAbility(UnitAbilities.CollectAbilityFor(resourceType.Value))))
                return false;
            var knowledge = new WorldSnapshot { Known = new KnownSnapshot
            {
                EnemySightings = AiMapMemory.AllKnownEnemySightings(player).ToList(),
                NeutralSightings = AiMapMemory.AllKnownNeutralSightings(player).ToList(),
            } };
            if (KnownHostileAtHex(knowledge, target)) return false;
            HexPath route = SafeStepPathing.FindSafePath(ctx.Map, player, actor.Hex, target, actor.MaxMovement);
            return route != null && KnownThreatsAffectingEconomyRoute(knowledge, route.Hexes).Count == 0;
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

        // ATK §35 — the ONE strategic direction owner (anchor -> nearest known hostile starting
        // citadel) and the ONE pair of direction-derived score inputs derived from it. Economy's
        // Base expansion has always used this; Attack scores FrontProgress/CorridorAlignment from
        // exactly the same direction rather than inventing a second, differently-shaped notion of
        // "forward". Kept in Analysis because it is a world fact, not a per-lane preference.
        //
        // `allowTrueWorldFallback` — Economy's own sanctioned knowledge exception (see the branch
        // below) must NOT leak into an offensive decision: an Attack that scored its targets from
        // a TrueWorld-derived direction would be reasoning about an enemy capital this player has
        // never observed. Offensive callers pass false and simply have no direction until Recon
        // finds one, which is the honest answer.
        internal static bool TrySelectStrategicDirection(WorldSnapshot snap,
            PlayerSetupData player, out HexCoord targetCitadel, out HexCoord anchor,
            bool allowTrueWorldFallback = true)
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
            if (targets.Count == 0 && allowTrueWorldFallback)
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

        // How much closer to the chosen direction's target a candidate hex sits than our own
        // anchor does, in [0..1]. Verbatim the rule Economy's Base expansion has always used.
        internal static float ForwardProgressToward(HexCoord anchor, HexCoord targetCitadel,
            HexCoord candidate)
        {
            int directDistance = HexGridMath.Distance(anchor, targetCitadel);
            int candidateDistance = HexGridMath.Distance(candidate, targetCitadel);
            return Mathf.Clamp01((directDistance - candidateDistance)
                / Mathf.Max(1f, directDistance));
        }

        // How little the candidate makes us leave the direct anchor -> target corridor, in [0..1].
        // `detourScale` is how many hexes of detour the caller considers a full loss of alignment;
        // it is a per-lane tolerance, not a second definition of the corridor itself.
        internal static float CorridorAlignmentToward(HexCoord anchor, HexCoord targetCitadel,
            HexCoord candidate, float detourScale)
        {
            int directDistance = HexGridMath.Distance(anchor, targetCitadel);
            int routedDistance = HexGridMath.Distance(anchor, candidate)
                + HexGridMath.Distance(candidate, targetCitadel);
            int detour = Mathf.Max(0, routedDistance - directDistance);
            return 1f - Mathf.Clamp01(detour / Mathf.Max(1f, detourScale));
        }

        internal static bool IsForwardBaseCandidate(IReadOnlyList<HexCoord> ownBases,
            HexCoord anchor, HexCoord targetCitadel, HexCoord candidate)
        {
            if (ownBases == null || ownBases.Count == 0)
                return false;
            return !ownBases.Contains(candidate)
                && HexGridMath.Distance(candidate, targetCitadel)
                    < HexGridMath.Distance(anchor, targetCitadel);
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

        // KnownHostileAtHex's neutral half: the hex is blocked by a neutral army or event guard and
        // by no enemy player's army.
        internal static bool KnownNeutralOnlyAtHex(WorldSnapshot snap, HexCoord hex) =>
            !(snap?.Known?.EnemySightings ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Any(contact => contact.Hex.Equals(hex))
            && ((snap?.Known?.NeutralSightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Any(contact => contact.Hex.Equals(hex))
                || (snap?.Known?.EventGuards
                    ?? System.Array.Empty<KnownEventGuardSnapshot>())
                .Any(g => g.Hex.Equals(hex)));

        internal static float EconomyBaseNetworkSynergy(WorldSnapshot snap, HexCoord target)
        {
            if (snap?.Self?.BaseHexes == null || snap.Self.BaseHexes.Count == 0)
                return 0f;
            int distance = snap.Self.BaseHexes.Min(h => HexGridMath.Distance(h, target));
            return 1f - Mathf.Clamp01((distance - 1f)
                / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
        }

        // Structural Economy fact: how many KNOWN resource hexes this candidate would bring within
        // the founding scan radius that no OWNED base already reaches within that same radius —
        // i.e. a genuinely new cluster of the hexagon network, not a second claim on ground already
        // serviceable from an existing base. Deliberately counts known sites only (fog-of-war
        // symmetric with the rest of Analysis); a candidate with no direction/exploration nearby
        // yields 0, same as an already-fully-covered one.
        private static int CountNewResourceClusterHexes(HexCoord candidate,
            IReadOnlyList<HexCoord> ownedBaseHexes, HashSet<HexCoord> knownResourceSites)
        {
            if (knownResourceSites == null || knownResourceSites.Count == 0)
                return 0;
            int radius = AiConfigV2.economyBaseFoundScanRadius;
            int count = 0;
            foreach (HexCoord site in knownResourceSites)
            {
                if (HexGridMath.Distance(candidate, site) > radius)
                    continue;
                bool alreadyReachable = ownedBaseHexes != null
                    && ownedBaseHexes.Any(baseHex => HexGridMath.Distance(baseHex, site) <= radius);
                if (!alreadyReachable)
                    count++;
            }
            return count;
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
