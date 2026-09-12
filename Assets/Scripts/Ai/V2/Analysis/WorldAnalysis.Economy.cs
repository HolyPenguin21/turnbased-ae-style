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
            var extraction = new List<EconomyExtractionOpportunity>();
            foreach ((HexCoord Hex, ResourceType Type, int Yield) site
                     in KnownExtractionYields(snap))
            {
                ResourceType resourceType = site.Type;
                int effectiveYield = site.Yield;

                int currentCollection = 0;
                if (knownBuildings.TryGetValue(site.Hex, out AiMapMemory.KnownBuilding building))
                {
                    // BuildingPlayExecutor can only add a Facility to our own building, and only
                    // while an unlocked slot was last observed free.
                    if (building.Owner != player || building.FreeFacilitySlots <= 0)
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
                    NearbyResourceClusterValue = EconomyResourceClusterValue(
                        snap, site.Hex, standings),
                    BuilderRoutes = EconomyBuilderRoutes(snap, player, ctx, site.Hex),
                });
            }
            eco.ExtractionOpportunities = extraction;

            // Base opportunities are structural site facts only. Card-specific value/cost remains
            // Strategy/Demand's responsibility, but Analysis owns the one legal candidate set so
            // the desire gate and demand emission cannot disagree.
            var baseOpportunities = new List<EconomyBaseOpportunity>();
            List<CardData> baseCards = (snap.Self.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition?.cardType == CardType.Base).ToList();
            if (baseCards.Count > 0 && snap.Self.BaseHexes != null)
            {
                var occupied = knownBuildings;
                var knownSites = new HashSet<HexCoord>((snap.Known?.ResourceHexes
                    ?? System.Array.Empty<AiMapMemory.KnownResourceHex>()).Select(x => x.Hex));
                var knownMapHexes = new HashSet<HexCoord>(
                    snap.MapKnowledge?.EverSeenHexSet
                    ?? snap.MapKnowledge?.VisitedHexSet
                    ?? (ISet<HexCoord>)new HashSet<HexCoord>());
                int ownedExtractionSites = knownBuildings.Values.Count(b => b.Owner == player
                    && !b.IsBase && knownSites.Contains(b.Hex));
                float infrastructurePressure = Mathf.Clamp01(ownedExtractionSites
                    / Mathf.Max(1f, snap.Self.BaseHexes.Count * 3f));
                var directionalSites = new HashSet<HexCoord>();
                bool hasDirection = TrySelectBaseExpansionDirection(snap, player,
                    out HexCoord targetCitadel, out HexCoord anchor);
                if (hasDirection)
                    directionalSites.UnionWith(knownMapHexes);
                foreach (HexCoord hex in directionalSites.OrderBy(x => x.Q).ThenBy(x => x.R))
                    {
                        if (!knownMapHexes.Contains(hex)
                            || !MeetsBaseSpacing(snap.Self.BaseHexes, hex)
                            || !hasDirection
                            || !IsForwardBaseCandidate(snap.Self.BaseHexes, anchor,
                                targetCitadel, hex))
                            continue;
                        bool hasBuilding = occupied.TryGetValue(hex,
                            out AiMapMemory.KnownBuilding knownBuilding);
                        bool convertsOwnedExtraction = hasBuilding && knownSites.Contains(hex)
                            && knownBuilding.Owner == player && !knownBuilding.IsBase;
                        if (hasBuilding && !convertsOwnedExtraction)
                            continue;
                        float lostExtractionIncome = 0f;
                        if (convertsOwnedExtraction)
                            foreach (ResourceType lostType in ResourceBundle.All)
                                lostExtractionIncome += knownBuilding.CollectedAmount(lostType);
                        bool knownHostileAtTarget = (snap.Known?.EnemySightings
                                ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                            .Concat(snap.Known?.NeutralSightings
                                ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                            .Any(contact => contact.Hex.Equals(hex));
                        if (knownHostileAtTarget)
                            continue;
                        if (ctx?.Map != null && !snap.Self.BaseHexes.Any(home =>
                                SafeStepPathing.FindSafePathCost(
                                    ctx.Map, player, home, hex) < int.MaxValue))
                            continue;

                        int supportDistance = snap.Self.BaseHexes
                            .Min(baseHex => HexGridMath.Distance(baseHex, hex));
                        float logistics = 1f - Mathf.Clamp01(
                            (supportDistance - AiConfigV2.economyBaseMinSpacing)
                            / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
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
                            HexYield = knownSites.Contains(hex)
                                ? EconomyKnownHexYield(snap, hex) : default(ResourceBundle),
                            CapacityValue = convertsOwnedExtraction ? 1f : 0.5f,
                            NearbyResourceClusterValue = EconomyResourceClusterValue(
                                snap, hex, standings),
                            NetworkExpansionValue = EconomyBaseNetworkExpansionValue(
                                snap, hex, standings),
                            InfrastructurePressure = infrastructurePressure,
                            LogisticsValue = logistics,
                            ForwardProgressValue = forwardProgress,
                            CorridorAlignmentValue = corridorAlignment,
                            ConvertsOwnedExtractionSite = convertsOwnedExtraction,
                            LostExtractionIncome = lostExtractionIncome,
                            BuilderRoutes = EconomyBuilderRoutes(snap, player, ctx, hex),
                        });
                    }
            }
            eco.BaseOpportunities = baseOpportunities;
            bool baseActionable = baseOpportunities.Count > 0;
            bool extractionActionable = extraction.Any(site =>
                site.MarginalIncomeGain > AiConfigV2.allocatorSliceEpsilon);
            eco.HasActionableOpportunity = extractionActionable || baseActionable;

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

        private static IReadOnlyList<EconomyBuilderRouteSnapshot> EconomyBuilderRoutes(
            WorldSnapshot snap, PlayerSetupData player, AiTurnContext ctx, HexCoord target)
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
            foreach (ArmySnapshot army in snap.Self.Armies
                         .Where(a => a != null).OrderBy(a => a.ArmyId))
            {
                if (army.IsGarrison)
                {
                    if (army.HasHero && army.Hex.Equals(target))
                        result.Add(new EconomyBuilderRouteSnapshot
                        {
                            ArmyId = army.ArmyId, TravelCost = 0, ReturnTravelCost = 0,
                            CurrentMovement = army.CurrentMovement, MaxMovement = army.MaxMovement,
                            ActivationApCost = army.ActivationApCost, ArmySize = army.MemberCount,
                            HasActivatedThisTurn = army.HasActivatedThisTurn,
                            EffectiveArmyPower = army.EffectiveArmyPower,
                            HasActiveEconomyCommitment = activeEconomyActors.Contains(army.ArmyId),
                            IsOnTarget = true,
                        });
                    continue;
                }
                if (!army.IsMobileEconomyBuilder
                    || !liveById.TryGetValue(army.ArmyId, out ArmyData live))
                    continue;
                // maxMovement hard-blocks any hex this army could never enter in one step (see
                // SafeStepPathing.FindSafePath, generalising the old post-hoc bd283fb reject into
                // the search itself), so the route returned — if any — is already guaranteed
                // usable; the per-hex re-check below is now just a defensive safety net.
                HexPath route = SafeStepPathing.FindSafePath(
                    ctx.Map, live.Owner, live.Hex, target, army.MaxMovement);
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
                int returnCost = int.MaxValue;
                foreach (HexCoord home in snap.Self.BaseHexes ?? System.Array.Empty<HexCoord>())
                {
                    int candidate = SafeStepPathing.FindSafePathCost(
                        ctx.Map, player, target, home, army.MaxMovement);
                    if (candidate < returnCost) returnCost = candidate;
                }
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
                });
            }
            return result;
        }

        private static float EconomyBaseNetworkSynergy(WorldSnapshot snap, HexCoord target)
        {
            if (snap?.Self?.BaseHexes == null || snap.Self.BaseHexes.Count == 0)
                return 0f;
            int distance = snap.Self.BaseHexes.Min(h => HexGridMath.Distance(h, target));
            return 1f - Mathf.Clamp01((distance - 1f)
                / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
        }

        private static float EconomyResourceClusterValue(WorldSnapshot snap,
            HexCoord target,
            IReadOnlyDictionary<ResourceType, EconomyResourceStanding> standings)
        {
            if (snap?.Known?.ResourceHexes == null)
                return 0f;
            float value = 0f;
            foreach (HexCoord site in snap.Known.ResourceHexes.Select(x => x.Hex).Distinct())
            {
                if (HexGridMath.Distance(target, site) > AiConfigV2.economyResourceClusterRadius)
                    continue;
                ResourceBundle yield = EconomyKnownHexYield(snap, site);
                foreach (ResourceType type in ResourceBundle.All)
                    if (yield.Get(type) > 0f
                        && standings.TryGetValue(type, out EconomyResourceStanding standing))
                        value += yield.Get(type) * Mathf.Max(0.1f, standing.DeficitScore);
            }
            return value;
        }

        private static float EconomyBaseNetworkExpansionValue(WorldSnapshot snap,
            HexCoord target, IReadOnlyDictionary<ResourceType, EconomyResourceStanding> standings)
        {
            if (snap?.Known?.ResourceHexes == null || snap.Self?.BaseHexes == null)
                return 0f;
            float value = 0f;
            foreach (HexCoord site in snap.Known.ResourceHexes.Select(x => x.Hex).Distinct())
            {
                if (HexGridMath.Distance(target, site) > AiConfigV2.economyBaseFoundScanRadius
                    || snap.Self.BaseHexes.Any(baseHex => HexGridMath.Distance(baseHex, site)
                        <= AiConfigV2.economyBaseFoundScanRadius))
                    continue;
                ResourceBundle yield = EconomyKnownHexYield(snap, site);
                foreach (ResourceType type in ResourceBundle.All)
                    if (standings.TryGetValue(type, out EconomyResourceStanding standing))
                        value += yield.Get(type) * Mathf.Max(0.25f, standing.DeficitScore);
            }
            return value;
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
