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
            List<HexCoord> eventGuardHexes = AiMapMemory.KnownEventGuardHexes(player).ToList();
            var known = new KnownSnapshot
            {
                EnemySightings = AiMapMemory.AllKnownEnemySightings(player).ToList(),
                NeutralSightings = AiMapMemory.AllKnownNeutralSightings(player).ToList(),
                Buildings = AiMapMemory.AllKnownBuildings(player).ToList(),
                EventGuardHexes = eventGuardHexes,
                EventGuards = eventGuardHexes
                    .Select(h =>
                    {
                        AiMapMemory.GuardStrength? s = AiMapMemory.KnownEventGuardStrengthAt(player, h);
                        return s.HasValue
                            ? new KnownEventGuardSnapshot(h, s.Value, s.Value.Name, s.Value.Defenders?.Count ?? 0)
                            : (KnownEventGuardSnapshot?)null;
                    })
                    .Where(g => g.HasValue)
                    .Select(g => g.Value)
                    .ToList(),
                ResourceHexes = AiMapMemory.AllKnownResourceHexes(player).ToList(),
            };

            known.EnemyKnownStrength = known.EnemySightings.Sum(s => s.DefenseSum + s.AttackSum);

            int nearest = int.MaxValue;
            float nearBases = 0f;
            foreach (AiMapMemory.KnownEnemySighting s in known.EnemySightings)
            {
                int d = baseHexes != null && baseHexes.Count > 0
                    ? baseHexes.Min(b => HexGridMath.Distance(b, s.Hex))
                    : 99;
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

        // The AirSweep anchor — where an aviation observation pass should head. Deliberately a
        // CHEAT read of TrueWorld (owner-approved for aviation support, like the per-base cheat
        // contacts in WorldAnalysis.Threat): the enemy army concentration first, the enemy citadel
        // only as the fallback when no enemy army exists. Concentration = the enemy army hex with
        // the largest summed EffectiveArmyPower of enemy armies within airSweepClusterRadius;
        // ties go to the one nearer our citadel, then coordinates (deterministic).
        internal static bool TryAirSweepAnchor(WorldSnapshot snap, out HexCoord anchor,
            out bool armyConcentration)
        {
            anchor = default;
            armyConcentration = false;
            if (snap?.Self == null || snap.TrueWorld == null)
                return false;
            HexCoord home = snap.Self.Citadel;

            IReadOnlyList<ArmySnapshot> enemies = snap.TrueWorld.EnemyArmies;
            if (enemies != null && enemies.Count > 0)
            {
                float bestPower = float.NegativeInfinity;
                foreach (ArmySnapshot center in enemies)
                {
                    if (center == null)
                        continue;
                    float power = 0f;
                    foreach (ArmySnapshot e in enemies)
                        if (e != null && HexGridMath.Distance(e.Hex, center.Hex) <= AiConfigV2.airSweepClusterRadius)
                            power += Mathf.Max(0f, e.EffectiveArmyPower);
                    bool better = power > bestPower
                        || (Mathf.Approximately(power, bestPower) && (
                            HexGridMath.Distance(home, center.Hex) < HexGridMath.Distance(home, anchor)
                            || (HexGridMath.Distance(home, center.Hex) == HexGridMath.Distance(home, anchor)
                                && (center.Hex.Q < anchor.Q || (center.Hex.Q == anchor.Q && center.Hex.R < anchor.R)))));
                    if (better)
                    {
                        bestPower = power;
                        anchor = center.Hex;
                    }
                }
                if (bestPower > float.NegativeInfinity)
                {
                    armyConcentration = true;
                    return true;
                }
            }

            return TryEnemyCitadelAnchor(snap, out anchor);
        }

        // The nearest opponent citadel to our own, read from TrueWorld — the owner-approved cheat
        // anchor for "where is the enemy's citadel" (aviation sweep fallback above, and the
        // strike force's observation need, AttackObjectiveEvaluator.ObservationNeeds). Only the
        // coordinates cross the knowledge boundary: what defends it is known only once observed.
        internal static bool TryEnemyCitadelAnchor(WorldSnapshot snap, out HexCoord anchor)
        {
            anchor = default;
            if (snap?.Self == null || snap.TrueWorld == null)
                return false;
            HexCoord home = snap.Self.Citadel;
            bool found = false;
            foreach (OpponentSnapshot o in snap.TrueWorld.Opponents ?? new List<OpponentSnapshot>())
            {
                if (o?.Player == null || o.Player.IsEliminated
                    || !o.Player.CitadelHexQ.HasValue || !o.Player.CitadelHexR.HasValue)
                    continue;
                var citadel = new HexCoord(o.Player.CitadelHexQ.Value, o.Player.CitadelHexR.Value);
                if (!found || HexGridMath.Distance(home, citadel) < HexGridMath.Distance(home, anchor))
                {
                    anchor = citadel;
                    found = true;
                }
            }
            return found;
        }

        internal static BuildingSnapshot ToBuildingSnapshot(BuildingData b)
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
                IsBase = b.IsBase,
                Defense = b.Defense,
                BuildingAbilities = new HashSet<string>(b.Abilities),
                FacilityAbilities = abilities,
            };
        }

        private static MapKnowledgeSnapshot BuildMapKnowledge(PlayerSetupData player, AiTurnContext ctx, WorldSnapshot snap)
        {
            HexMap map = ctx.Map;
            var all = new List<HexCoord>();
            var visitedSet = new HashSet<HexCoord>();
            var everSeenSet = new HashSet<HexCoord>();
            var unvisitedRuins = new HashSet<HexCoord>();
            int visited = 0, visible = 0;
            foreach (HexCoord c in map.AllCoords)
            {
                all.Add(c);
                if (VisionSystem.IsVisited(player, c)) { visited++; visitedSet.Add(c); }
                else if (map.IsCityRuins(c)) unvisitedRuins.Add(c);
                if (VisionSystem.HasEverSeen(player, c)) everSeenSet.Add(c);
                if (VisionSystem.IsVisible(player, c)) visible++;
            }
            int total = all.Count;

            IReadOnlyList<HexCoord> baseHexes = snap.Self.BaseHexes;
            // Hexes a VISIBLE ground mover may not arrive on — the one AI arrival rule
            // (AiMapMemory.KnownGroundArrival) evaluated over every remembered army / foreign
            // building hex, so this frozen planning set is the same rule the execution gate and
            // the route blockers apply live. A fully hidden mover passes all of them.
            var visibleArrivalBlocked = new HashSet<HexCoord>();
            {
                var candidates = new HashSet<HexCoord>();
                foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings ?? new List<AiMapMemory.KnownEnemySighting>())
                    candidates.Add(s.Hex);
                foreach (AiMapMemory.KnownEnemySighting s in snap.Known.NeutralSightings ?? new List<AiMapMemory.KnownEnemySighting>())
                    candidates.Add(s.Hex);
                foreach (AiMapMemory.KnownBuilding b in snap.Known.Buildings ?? new List<AiMapMemory.KnownBuilding>())
                    if (b.Owner != player) candidates.Add(b.Hex);
                foreach (HexCoord h in candidates)
                    if (AiMapMemory.KnownGroundArrival(player, h, moverFullyHidden: false).HasOutcome)
                        visibleArrivalBlocked.Add(h);
            }
            List<AiMapMemory.KnownEnemySighting> nonNeutral =
                (snap.Known.EnemySightings ?? new List<AiMapMemory.KnownEnemySighting>()).ToList();

            bool OnMap(HexCoord h) => map.TryGetTerrainAt(h, out _);
            // Spec §19 — arrival outcomes (a known army to fight, a known undefended structure to
            // take over) are NOT a universal hard block. They are actor-state-aware (a fully-hidden
            // scout passes) and exported separately as VisibleArrivalBlockedHexes. HardBlocked is
            // only what blocks EVERY scout.
            bool HardBlocked(HexCoord h) =>
                !OnMap(h) || AiMapMemory.IsScoutDangerous(player, h);
            bool VisibleArrivalBlocked(HexCoord h) => visibleArrivalBlocked.Contains(h);
            // Exposure and detection are ScoutRiskModel's one rule (a garrison detects but cannot
            // engage — audit F1).
            bool EnemyExposed(HexCoord h) => ScoutRiskModel.IsExposed(nonNeutral, h);
            int DetectorsAt(HexCoord h) => ScoutRiskModel.CountDetectors(nonNeutral, h);
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
                // A frontier hex is a place a scout stands on next; keep hexes a visible scout may
                // not arrive on out of that set (conservative for waypoint choice) even though the
                // explorable flood below now flows THROUGH them for a hidden scout.
                if (VisionSystem.IsVisited(player, c) || HardBlocked(c) || VisibleArrivalBlocked(c)) continue;
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
            if (raw.Count > 0)
            {
                int nearestFrontierDist = raw.Min(f => f.DistanceFromNearestBase);
                int bandLimit = nearestFrontierDist + AiConfigV2.frontierWaveBand;
                foreach (FrontierHexSnapshot f in raw)
                {
                    if (f.DistanceFromNearestBase > bandLimit) continue;
                    frontier.Add(f);
                }
            }

            int explorable = 0;
            // The wave band limits this pass's waypoints, not the amount of reachable knowledge.
            // Seed every reachable frontier component so a nearby pocket cannot hide distant work.
            if (raw.Count > 0)
            {
                var darkSeen = new HashSet<HexCoord>(raw.Select(f => f.Hex));
                var darkQueue = new Queue<HexCoord>(darkSeen);
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
                VisibleArrivalBlockedHexes = visibleArrivalBlocked,
                VisitedHexSet = visitedSet,
                EverSeenHexSet = everSeenSet,
                UnvisitedRuinsHexes = unvisitedRuins,
            };
        }

    }
}
