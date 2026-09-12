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
    // Knowledge — Known / TrueWorld / MapKnowledge builders.
    // File-split (mechanical, no behaviour change) from WorldAnalysis.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 5. Still exactly the WorldAnalysis
    // class; only this snapshot family's slice moved to its own file.
    public static partial class WorldAnalysis
    {
        private static KnownSnapshot BuildKnown(PlayerSetupData player, IReadOnlyList<HexCoord> baseHexes)
        {
            var known = new KnownSnapshot
            {
                EnemySightings = AiMapMemory.AllKnownEnemySightings(player).ToList(),
                NeutralSightings = AiMapMemory.AllKnownNeutralSightings(player).ToList(),
                Buildings = AiMapMemory.AllKnownBuildings(player).ToList(),
                EventGuardHexes = AiMapMemory.KnownEventGuardHexes(player).ToList(),
                ResourceHexes = AiMapMemory.AllKnownResourceHexes(player).ToList(),
            };

            known.EnemyKnownStrength = known.EnemySightings.Sum(s => s.DefenseSum + s.AttackSum);

            int nearest = int.MaxValue;
            float nearBases = 0f;
            foreach (AiMapMemory.KnownEnemySighting s in known.EnemySightings)
            {
                int d = baseHexes.Min(b => HexGridMath.Distance(b, s.Hex));
                if (d < nearest) nearest = d;
                if (d <= AiConfig.raidThreatRadius + 2)
                    nearBases += s.DefenseSum + s.AttackSum;
            }
            known.NearestEnemyToBase = nearest == int.MaxValue ? 99 : nearest;
            known.EnemyStrengthNearBases = nearBases;

            return known;
        }

        private static TrueWorldSnapshot BuildTrueWorld(PlayerSetupData player, AiTurnContext ctx)
        {
            var tw = new TrueWorldSnapshot();
            var enemyArmies = new List<ArmySnapshot>();
            var neutralArmies = new List<ArmySnapshot>();
            var opponents = new List<OpponentSnapshot>();

            foreach (PlayerSetupData p in GameSession.Players ?? new List<PlayerSetupData>())
            {
                if (p == null || p == player) continue;

                List<ArmyData> armies = ArmyRegistry.AllForOwner(p)
                    .Where(a => a != null && !a.IsPrison && a.Members.Count > 0)
                    .ToList();
                var snaps = armies.Select(a => ToArmySnapshot(a, player, isOwn: false, ArmyVisionRadius(ctx))).ToList();

                if (p.IsNeutral)
                {
                    neutralArmies.AddRange(snaps);
                    continue;
                }

                enemyArmies.AddRange(snaps);

                if (!p.IsEliminated)
                {
                    PlayerRoot pr = PlayerRootRegistry.FindFor(p);
                    var opp = new OpponentSnapshot
                    {
                        Player = p,
                        ArmyCount = snaps.Count,
                        ArmyPower = snaps.Sum(s => s.EffectiveArmyPower),
                    };
                    foreach (ResourceType t in ResourceBundle.All)
                    {
                        opp.PerTurnIncome.Add(t, IncomeProjection.IncomeFor(p, t, ctx.Map));
                        opp.Stockpile.Add(t, pr != null ? pr.GetResource(t) : 0);
                    }
                    opponents.Add(opp);
                }
            }

            tw.EnemyArmies = enemyArmies;
            tw.NeutralArmies = neutralArmies;
            tw.Opponents = opponents;
            tw.AllBuildings = BuildingRegistry.AllBuildings()
                .Where(b => b != null)
                .Select(ToBuildingSnapshot)
                .ToList();
            return tw;
        }

        private static BuildingSnapshot ToBuildingSnapshot(BuildingData b)
        {
            var abilities = new HashSet<string>();
            foreach (FacilityData f in b.FacilitySlots)
                if (f != null)
                    abilities.UnionWith(f.Abilities);
            return new BuildingSnapshot
            {
                Hex = b.Hex,
                Owner = b.Owner,
                IsStartingCitadel = b.IsStartingCitadel,
                Defense = b.Defense,
                FacilityAbilities = abilities,
            };
        }

        private static MapKnowledgeSnapshot BuildMapKnowledge(PlayerSetupData player, AiTurnContext ctx, WorldSnapshot snap)
        {
            HexMap map = ctx.Map;
            var all = new List<HexCoord>();
            var visitedSet = new HashSet<HexCoord>();
            var everSeenSet = new HashSet<HexCoord>();
            int visited = 0, visible = 0;
            foreach (HexCoord c in map.AllCoords)
            {
                all.Add(c);
                if (VisionSystem.IsVisited(player, c)) { visited++; visitedSet.Add(c); }
                if (VisionSystem.HasEverSeen(player, c)) everSeenSet.Add(c);
                if (VisionSystem.IsVisible(player, c)) visible++;
            }
            int total = all.Count;

            IReadOnlyList<HexCoord> baseHexes = snap.Self.BaseHexes;
            var neutralHexes = new HashSet<HexCoord>(
                (snap.Known.NeutralSightings ?? new List<AiMapMemory.KnownEnemySighting>()).Select(s => s.Hex));
            List<AiMapMemory.KnownEnemySighting> nonNeutral =
                (snap.Known.EnemySightings ?? new List<AiMapMemory.KnownEnemySighting>()).ToList();
            int exposureR = AiConfigV2.frontierEnemyExposureRadius;

            bool OnMap(HexCoord h) => map.TryGetTerrainAt(h, out _);
            // Spec §19 — neutral occupancy is NOT a universal hard block any more. It is
            // actor-state-aware (a fully-hidden scout passes) and exported separately as
            // NeutralOccupiedHexes. HardBlocked is now only what blocks EVERY scout.
            bool HardBlocked(HexCoord h) =>
                !OnMap(h) || AiMapMemory.IsScoutDangerous(player, h);
            bool NeutralAt(HexCoord h) => neutralHexes.Contains(h);
            bool EnemyExposed(HexCoord h)
            {
                foreach (AiMapMemory.KnownEnemySighting e in nonNeutral)
                    if (HexGridMath.Distance(e.Hex, h) <= exposureR) return true;
                return false;
            }
            int DetectorsAt(HexCoord h)
            {
                int n = 0;
                foreach (AiMapMemory.KnownEnemySighting e in nonNeutral)
                    if (HexGridMath.Distance(e.Hex, h) <= exposureR && e.CanDetectStealthAt(h)) n++;
                return n;
            }
            int NearestBaseDist(HexCoord h) =>
                baseHexes.Count > 0 ? baseHexes.Min(b => HexGridMath.Distance(b, h)) : 0;

            var reachableVisited = new HashSet<HexCoord>();
            var queue = new Queue<HexCoord>();
            foreach (HexCoord b in baseHexes)
                if (OnMap(b) && reachableVisited.Add(b))
                    queue.Enqueue(b);
            while (queue.Count > 0)
            {
                HexCoord cur = queue.Dequeue();
                foreach (HexCoord n in HexGridMath.Neighbors(cur))
                {
                    if (reachableVisited.Contains(n) || HardBlocked(n)) continue;
                    if (!VisionSystem.IsVisited(player, n)) continue;
                    reachableVisited.Add(n);
                    queue.Enqueue(n);
                }
            }

            var raw = new List<FrontierHexSnapshot>();
            foreach (HexCoord c in all)
            {
                // A frontier hex is a place a scout stands on next; keep neutral-occupied hexes out
                // of that set (conservative for waypoint choice) even though the explorable flood
                // below now flows THROUGH them for a hidden scout.
                if (VisionSystem.IsVisited(player, c) || HardBlocked(c) || NeutralAt(c)) continue;
                bool touchesReachable = false;
                int fresh = 0;
                foreach (HexCoord n in HexGridMath.Neighbors(c))
                {
                    if (reachableVisited.Contains(n)) touchesReachable = true;
                    if (!VisionSystem.IsVisited(player, n) && !HardBlocked(n)) fresh++;
                }
                if (!touchesReachable) continue;
                bool exposed = EnemyExposed(c);
                raw.Add(new FrontierHexSnapshot
                {
                    Hex = c,
                    FreshNeighbors = fresh,
                    DistanceFromNearestBase = NearestBaseDist(c),
                    EnemyExposure = exposed,
                    StealthDetectionRisk = exposed && DetectorsAt(c) > 0,
                });
            }

            var frontier = new List<FrontierHexSnapshot>();
            var frontierSet = new HashSet<HexCoord>();
            if (raw.Count > 0)
            {
                int nearestFrontierDist = raw.Min(f => f.DistanceFromNearestBase);
                int bandLimit = nearestFrontierDist + AiConfigV2.frontierWaveBand;
                foreach (FrontierHexSnapshot f in raw)
                {
                    if (f.DistanceFromNearestBase > bandLimit) continue;
                    frontier.Add(f);
                    frontierSet.Add(f.Hex);
                }
            }

            int explorable = 0;
            if (frontierSet.Count > 0)
            {
                var darkSeen = new HashSet<HexCoord>(frontierSet);
                var darkQueue = new Queue<HexCoord>(frontierSet);
                while (darkQueue.Count > 0)
                {
                    HexCoord cur = darkQueue.Dequeue();
                    explorable++;
                    foreach (HexCoord n in HexGridMath.Neighbors(cur))
                    {
                        if (darkSeen.Contains(n) || HardBlocked(n)) continue;
                        if (VisionSystem.IsVisited(player, n)) continue;
                        darkSeen.Add(n);
                        darkQueue.Enqueue(n);
                    }
                }
            }

            return new MapKnowledgeSnapshot
            {
                TotalHexes = total,
                VisitedHexes = visited,
                VisibleHexes = visible,
                UnknownFrac = total > 0 ? 1f - (float)visited / total : 0f,
                Frontier = frontier,
                ExplorableUnknownFrac = total > 0 ? (float)explorable / total : 0f,
                AllHexes = all,
                ScoutHardBlockedHexes = new HashSet<HexCoord>(all.Where(HardBlocked)),
                NeutralOccupiedHexes = new HashSet<HexCoord>(all.Where(NeutralAt)),
                VisitedHexSet = visitedSet,
                EverSeenHexSet = everSeenSet,
            };
        }

    }
}
