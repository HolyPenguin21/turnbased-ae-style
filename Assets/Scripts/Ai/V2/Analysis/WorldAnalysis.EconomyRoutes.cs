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
    // Economy route facts: walk-home routes and the safe builder routes (cost, return leg)
    // the snapshot carries for Demand. The same WorldAnalysis owner as WorldAnalysis.Economy.cs.
    public static partial class WorldAnalysis
    {
        internal static IReadOnlyDictionary<HexCoord, int> EconomyHomeRoutes(
            PlayerSetupData player, AiTurnContext ctx, HexCoord from, int maxMovement,
            IReadOnlyList<HexCoord> bases)
        {
            var costs = new Dictionary<HexCoord, int>();
            if (ctx?.Map == null || player == null) return costs;
            foreach (HexCoord home in bases ?? System.Array.Empty<HexCoord>())
            {
                int cost = SafeStepPathing.FindSafePathCost(ctx.Map, player, from, home, maxMovement);
                if (cost != int.MaxValue) costs[home] = cost;
            }
            return costs;
        }

        private static int EconomyMaximumStepCost(AiTurnContext ctx, IReadOnlyList<HexCoord> path)
        {
            int maximum = 0;
            foreach (HexCoord hex in path.Skip(1))
                maximum = Mathf.Max(maximum, ctx.Map.TryGetTerrainAt(hex, out TerrainTypeEntry entry)
                    ? Mathf.Max(1, entry.moveCost) : 1);
            return maximum;
        }

        // The target-independent half of EconomyBuilderRoutes: live armies, active Economy movers
        // and each garrison's sparable heroes with their extraction container. BuildEconomy asks
        // for every candidate site against the same unchanged world, so it prepares this once per
        // scan; only the route, return leg and threat witness depend on the target.
        internal sealed class EconomyBuilderPool
        {
            internal readonly Dictionary<int, ArmyData> LiveById;
            internal readonly HashSet<int> ActiveEconomyActors;
            private readonly PlayerSetupData _player;
            private readonly System.Func<ResearchProductionMode, HexCoord?> _preparationSite;
            private readonly Dictionary<int, IReadOnlyList<(UnitData Hero,
                ProvisioningManager.GarrisonExtractionCandidate Extraction)>> _garrisonHeroes =
                new Dictionary<int, IReadOnlyList<(UnitData, ProvisioningManager.GarrisonExtractionCandidate)>>();

            internal EconomyBuilderPool(WorldSnapshot snap, PlayerSetupData player, AiTurnContext ctx)
            {
                _player = player;
                LiveById = ArmyRegistry.AllForOwner(player)
                    .Where(a => a != null).ToDictionary(a => a.Id);
                ActiveEconomyActors = new HashSet<int>(
                    WorldSnapshot.IntentsOf(snap)
                        .Where(i => i != null && i.Status == IntentStatus.Active
                            && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId.HasValue)
                        .Select(i => i.PreferredMoverArmyId.Value));
                // The selected preparation site per mode is a pure read of the unchanged world.
                System.Func<ResearchProductionMode, HexCoord?> live =
                    DevelopmentOpportunityEvaluator.LivePreparationSite(
                        player, AiHandRegistry.Peek(player), ctx, snap.Intents);
                var sites = new Dictionary<ResearchProductionMode, HexCoord?>();
                _preparationSite = mode =>
                {
                    if (!sites.TryGetValue(mode, out HexCoord? site))
                        sites[mode] = site = live(mode);
                    return site;
                };
            }

            // Sparable heroes of `garrison` in EconomyHeroCandidates order, each with a resolved
            // (non-None) extraction; the same answers the per-target loop used to recompute.
            internal IReadOnlyList<(UnitData Hero, ProvisioningManager.GarrisonExtractionCandidate Extraction)>
                GarrisonHeroes(ArmyData garrison)
            {
                if (_garrisonHeroes.TryGetValue(garrison.Id, out var heroes))
                    return heroes;
                var list = new List<(UnitData, ProvisioningManager.GarrisonExtractionCandidate)>();
                foreach (UnitData sparableHero in AiArmyRoles.EconomyHeroCandidates(_player, garrison, _preparationSite))
                {
                    ProvisioningManager.GarrisonExtractionCandidate extraction =
                        ProvisioningManager.ResolveGarrisonExtractionCandidate(_player, garrison,
                            commitments: null, session: null, root: null,
                            ecoApEnvelopeRemaining: float.MaxValue, preparationSite: _preparationSite,
                            exactEconomyHeroId: sparableHero.RuntimeId);
                    if (extraction.Tier != ProvisioningManager.GarrisonExtractionTier.None)
                        list.Add((sparableHero, extraction));
                }
                _garrisonHeroes[garrison.Id] = list;
                return list;
            }
        }

        internal static IReadOnlyList<EconomyBuilderRouteSnapshot> EconomyBuilderRoutes(
            WorldSnapshot snap, PlayerSetupData player, AiTurnContext ctx, HexCoord target,
            ArmySnapshot projectedArmy = null) =>
            EconomyBuilderRoutes(snap, player, ctx, target, projectedArmy, null);

        internal static IReadOnlyList<EconomyBuilderRouteSnapshot> EconomyBuilderRoutes(
            WorldSnapshot snap, PlayerSetupData player, AiTurnContext ctx, HexCoord target,
            ArmySnapshot projectedArmy, EconomyBuilderPool pool)
        {
            var result = new List<EconomyBuilderRouteSnapshot>();
            if (snap?.Self?.Armies == null || player == null || ctx?.Map == null)
                return result;

            pool ??= new EconomyBuilderPool(snap, player, ctx);
            Dictionary<int, ArmyData> liveById = pool.LiveById;
            HashSet<int> activeEconomyActors = pool.ActiveEconomyActors;
            IEnumerable<ArmySnapshot> candidates = projectedArmy != null
                ? new[] { projectedArmy } : snap.Self.Armies;
            foreach (ArmySnapshot army in candidates
                         .Where(a => a != null)
                         .Select(a => !a.Hex.Equals(target) ? a.EconomyDeparture ?? a : a)
                         .OrderBy(a => a.ArmyId))
            {
                if (army.IsGarrison)
                {
                    if (!liveById.TryGetValue(army.ArmyId, out ArmyData liveGarrison))
                        continue;
                    foreach ((UnitData sparableHero, ProvisioningManager.GarrisonExtractionCandidate extraction)
                             in pool.GarrisonHeroes(liveGarrison))
                    {
                        if (army.Hex.Equals(target))
                        {
                            // A garrison is NEVER a mobile hero army. Even when it already occupies
                            // the build hex, Provisioning must extract a genuinely sparable hero into
                            // a field container before Economy can execute the build.
                            result.Add(new EconomyBuilderRouteSnapshot
                            {
                                ArmyId = army.ArmyId, TravelCost = 0,
                                ReturnTravelCost = AiArmyRoles.IsGarrisonHero(sparableHero)
                                    || HeroRoleEvaluator.HasSupportVocation(sparableHero)
                                    ? SafeStepPathing.FindNearestBaseReturnCost(ctx.Map, player,
                                        target, snap.Self.BaseHexes, sparableHero.MoveMax) : 0,
                                CurrentMovement = sparableHero.MoveMax, MaxMovement = sparableHero.MoveMax,
                                ActivationApCost = sparableHero.ActivationApCost, ArmySize = 1,
                                HasActivatedThisTurn = false,
                                EffectiveArmyPower = AiPower.ToPowerUnit(sparableHero).BasePower,
                                HasActiveEconomyCommitment = activeEconomyActors.Contains(army.ArmyId),
                                IsOnTarget = true, RequiresGarrisonExtraction = true,
                                BuilderHeroRuntimeId = sparableHero.RuntimeId,
                                BuilderHasHomeVocation = AiArmyRoles.IsGarrisonHero(sparableHero)
                                    || HeroRoleEvaluator.HasSupportVocation(sparableHero),
                                ExtractionContainerAvailable = true, ExtractionApCost = extraction.ApCost,
                                ExtractedHeroCommander = WorthIt.SideCommander.Of(sparableHero),
                                ExtractedHeroCapacity = sparableHero.CommandRating,
                                ExtractionContainerActivated = extraction.Container?.HasActivatedThisTurn == true,
                                PathHexes = new[] { target },
                                RouteThreats = KnownThreatsAffectingEconomyRoute(snap, new[] { target }),
                            });
                            continue;
                        }

                        // Price each canonical sparable hero on its own movement and exact
                        // Shell/Host/Create witness. Demand compares roles and delivery; execution
                        // later revalidates the selected runtime identity before extraction.
                        HexPath garrisonRoute = SafeStepPathing.FindSafePath(
                            ctx.Map, player, army.Hex, target, sparableHero.MoveMax);
                        if (garrisonRoute != null)
                        {
                            int garrisonReturnCost = SafeStepPathing.FindNearestBaseReturnCost(
                                ctx.Map, player, target, snap.Self.BaseHexes, sparableHero.MoveMax);
                            garrisonReturnCost = EconomyBuilderReturnCost(garrisonReturnCost,
                                AiArmyRoles.IsGarrisonHero(sparableHero)
                                    || HeroRoleEvaluator.HasSupportVocation(sparableHero),
                                () => SafeStepPathing.FindSafePathCost(ctx.Map, player,
                                    target, army.Hex, sparableHero.MoveMax));

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
                                BuilderHeroRuntimeId = sparableHero.RuntimeId,
                                BuilderHasHomeVocation = AiArmyRoles.IsGarrisonHero(sparableHero)
                                    || HeroRoleEvaluator.HasSupportVocation(sparableHero),
                                ExtractionContainerAvailable = true, ExtractionApCost = extraction.ApCost,
                                ExtractedHeroCommander = WorthIt.SideCommander.Of(sparableHero),
                                ExtractedHeroCapacity = sparableHero.CommandRating,
                                ExtractionContainerActivated = extraction.Container?.HasActivatedThisTurn == true,
                                MaximumStepCost = EconomyMaximumStepCost(ctx, garrisonRoute.Hexes),
                                PathHexes = garrisonRoute.Hexes.ToList(),
                                RouteThreats = KnownThreatsAffectingEconomyRoute(
                                    snap, garrisonRoute.Hexes),
                            });
                        }
                    }
                    continue;
                }
                // Shape only: BuildEconomy runs before CompleteDevelopmentPreparation writes
                // OperatorDutyBlocksDeparture, so the duty is judged by Demand / Provisioning, which
                // read the completed snapshot (and the live world) on the same witnessed routes.
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
                returnCost = EconomyBuilderReturnCost(returnCost, army.HeroIsHomeVocation,
                    () => SafeStepPathing.FindSafePathCost(ctx.Map, player,
                        target, army.Hex, army.MaxMovement));
                result.Add(new EconomyBuilderRouteSnapshot
                {
                    ArmyId = army.ArmyId,
                    TravelCost = cost,
                    BuilderHasHomeVocation = army.HeroIsHomeVocation,
                    ReturnTravelCost = returnCost,
                    CurrentMovement = army.CurrentMovement,
                    MaxMovement = army.MaxMovement,
                    ActivationApCost = army.ActivationApCost,
                    HasActivatedThisTurn = army.HasActivatedThisTurn,
                    ArmySize = army.MemberCount,
                    EffectiveArmyPower = army.EffectiveArmyPower,
                    HasActiveEconomyCommitment = activeEconomyActors.Contains(army.ArmyId),
                    IsOnTarget = army.Hex.Equals(target),
                    MaximumStepCost = EconomyMaximumStepCost(ctx, route.Hexes),
                    PathHexes = route.Hexes.ToList(),
                    RouteThreats = KnownThreatsAffectingEconomyRoute(
                        snap, route.Hexes),
                });
            }
            return result;
        }

        // Returning to a field origin is a valid ordinary-builder pricing fallback, but
        // cannot prove a Support job is close to an owned base.
        internal static int EconomyBuilderReturnCost(int homeCost, bool hasHomeVocation,
            System.Func<int> originReturnCost) => homeCost != int.MaxValue || hasHomeVocation
                ? homeCost : originReturnCost();

        // The ONE economy threat witness (builders, escorts and collectors): known mobile armies of
        // other players within 1 hex of the SafeStepPathing route or within 2 hexes of the site
        // (the route's last hex). Neutral armies never move — the route already avoids them and
        // KnownHostileAtHex covers one standing on the site. Garrisons cannot sortie. A listed
        // threat must be answered by an escort (EconomyRosterSafe), never by a score penalty.
        internal static IReadOnlyList<AiMapMemory.KnownEnemySighting>
            KnownThreatsAffectingEconomyRoute(WorldSnapshot snap,
                IReadOnlyList<HexCoord> pathHexes)
        {
            if (pathHexes == null || pathHexes.Count == 0)
                return System.Array.Empty<AiMapMemory.KnownEnemySighting>();

            HexCoord site = pathHexes[pathHexes.Count - 1];
            return (snap?.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(enemy => !enemy.IsGarrison && enemy.Owner?.IsNeutral != true
                    && (HexGridMath.Distance(site, enemy.Hex) <= AiConfigV2.economySiteThreatRadius
                        || pathHexes.Any(hex => HexGridMath.Distance(hex, enemy.Hex)
                            <= AiConfigV2.economyRouteThreatRadius)))
                .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                .ThenBy(x => x.ArmyId)
                .ToList();
        }
    }
}
