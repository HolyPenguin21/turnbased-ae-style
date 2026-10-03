using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Setup
{
    // Post-generation map content — resources, neutral armies, and two not-yet-implemented
    // hooks (random events, special hexes), in that order, per the user's own spec. Split out
    // of CitadelSetupController.cs purely for file size, same reasoning as HexSelectionController's
    // own multi-file split. Runs once, from FinishAllPlacements, after every citadel hex is
    // finalized — resource/army placement both need to know where those are so they can steer
    // clear (see GenerateResources/GenerateNeutralArmies's own comments).
    public partial class CitadelSetupController
    {
        // Registered under PlayerRootRegistry in CreatePlayerRoots — every neutral army spawned
        // below is owned by this, never by null (see that method's own comment on why).
        private PlayerSetupData _neutralPlayer;

        // Two data points the user supplied directly (Normal was the old 12x9 rectangular map,
        // and a larger 16x13 one — 108 and 208 hexes respectively) — every count below is
        // linearly interpolated/extrapolated from total hex count through those two points, so
        // a custom map size still gets a proportional answer instead of a hardcoded number. The
        // field itself is a hexagon-of-hexes now (see HexCountForRadius), but these two
        // calibration points are still just hex counts, not tied to the old rectangle shape.
        // Two points fully determine a line; recalibrate by changing these pairs if a third data
        // point ever narrows things down further.
        private const int CalibrationSmallHexes = 12 * 9;
        private const int CalibrationLargeHexes = 16 * 13;

        private static int CalibratedCount(int smallValue, int largeValue, int hexCount)
        {
            float t = (hexCount - CalibrationSmallHexes) / (float)(CalibrationLargeHexes - CalibrationSmallHexes);
            return Mathf.RoundToInt(Mathf.Lerp(smallValue, largeValue, t));
        }

        // Total hex count of a hexagon-of-hexes field of the given ring radius (the field shape
        // HexMapGenerator actually builds now) — feeds CalibratedCount above the same way
        // width*height once did for the old rectangular field; the calibration constants
        // themselves don't need to change; only where hexCount comes from does.
        private static int HexCountForRadius(int radius) => 1 + 3 * radius * (radius + 1);

        // Near-zone types only — Tech no longer spawns next to a citadel at all (the user's own
        // later call): a player's own base now guarantees Human/Energy/Materials nearby but has
        // to go find Tech elsewhere on the map, same as every other player.
        private static readonly ResourceType[] NearResourceTypes =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials };

        // Resource layout:
        //  - Near each citadel: exactly one Human, one Energy and one Materials hex. These are
        //    restricted to distance 2-3 from every citadel (never adjacent to a citadel) and no
        //    two near-resource hexes may be adjacent to each other, including across players.
        //  - Far H/E/M: only at distance 4+ from every citadel. The total is
        //    players + (mapRadius - 4), so R5..R8 produce P+1..P+4 hexes. Each hex carries exactly
        //    one ordinary resource, with H/E/M counts kept as even as possible (difference <= 1).
        //  - Far Tech: only at distance 4+ from every citadel. Two-player maps get 2 Tech hexes;
        //    maps with 3+ players get players-1. Tech may not be adjacent to any near H/E/M hex.
        // All outside resource hexes keep at least one empty hex between them. Resource hexes may
        // still be occupied by neutral armies ("the army guards the resource").
        private void GenerateResources()
        {
            if (map == null || gameConfig == null)
                return;

            List<PlayerSetupData> citadelPlayers = _allPlayers
                .Where(p => p.CitadelHexQ.HasValue && p.CitadelHexR.HasValue)
                .ToList();
            if (citadelPlayers.Count == 0)
                return;

            // RefreshAll already ran once, back when HexMapGenerator first built the map (all
            // zero yield, since baseline was reset to 0 — see the user's own change) — every hex
            // this pass touches needs the same per-hex redraw CitadelSetupController.
            // FinalizePlayer already does for its own citadel bonus, or the icon never appears.
            MapResourceDisplay resourceDisplay = map.GetComponent<MapResourceDisplay>();
            var mapHexes = new HashSet<HexCoord>(map.AllCoords.Where(h => map.CanEnter(h)));
            var nearZone = new HashSet<HexCoord>();
            var citadelHexes = citadelPlayers
                .Select(p => new HexCoord(p.CitadelHexQ.Value, p.CitadelHexR.Value))
                .ToList();
            var placedNear = new List<HexCoord>();

            foreach (HexCoord citadel in citadelHexes)
            {
                nearZone.UnionWith(HexGridMath.HexesInRange(citadel, 3).Where(mapHexes.Contains));

                List<HexCoord> band = HexGridMath.HexesInRange(citadel, 3)
                    .Where(h => mapHexes.Contains(h)
                        && HexGridMath.Distance(citadel, h) >= 2
                        && citadelHexes.All(c => HexGridMath.Distance(c, h) >= 2))
                    .ToList();

                foreach (ResourceType type in PickRandomDistinct(NearResourceTypes.ToList(), NearResourceTypes.Length))
                {
                    List<HexCoord> pool = band
                        .Where(h => HexResourceBonusRegistry.GetBonus(h) == null
                            && placedNear.All(p => HexGridMath.Distance(h, p) >= 2))
                        .ToList();
                    if (pool.Count == 0)
                        continue; // unusually cramped/overlapping start zones: skip rather than crash

                    HexCoord hex = pool[Random.Range(0, pool.Count)];
                    placedNear.Add(hex);
                    var yields = new ResourceYields();
                    AddYieldUnit(yields, type);
                    HexResourceBonusRegistry.Set(hex, yields);
                    resourceDisplay?.RefreshHex(hex);
                }
            }

            List<HexCoord> outsideCandidates = map.AllCoords.Where(h => map.CanEnter(h))
                .Where(h => !nearZone.Contains(h) && HexResourceBonusRegistry.GetBonus(h) == null)
                .ToList();

            int farTarget = Mathf.Max(0, citadelPlayers.Count + map.FieldRadius - 4);
            int techTarget = citadelPlayers.Count == 2
                ? 2
                : Mathf.Max(0, citadelPlayers.Count - 1);

            List<ResourceType> farTypes = BuildBalancedFarTypes(farTarget);
            List<List<HexCoord>> rawSectors = BuildEvenSectors(outsideCandidates, farTarget + techTarget);
            List<List<HexCoord>> sectors = PickRandomDistinct(rawSectors, rawSectors.Count);

            var placedOutside = new List<HexCoord>();
            int sectorIndex = 0;

            // Tech is the scarcer strategic resource, so reserve its well-spaced locations first.
            int techPlaced = 0;
            while (techPlaced < techTarget && sectorIndex < sectors.Count)
            {
                HexCoord? hex = PickFromSector(sectors[sectorIndex], placedOutside, placedNear);
                sectorIndex++;
                if (hex == null)
                    continue;

                var yields = new ResourceYields();
                AddYieldUnit(yields, ResourceType.Tech);
                HexResourceBonusRegistry.Set(hex.Value, yields);
                resourceDisplay?.RefreshHex(hex.Value);
                placedOutside.Add(hex.Value);
                techPlaced++;
            }

            int farPlaced = 0;
            while (farPlaced < farTypes.Count && sectorIndex < sectors.Count)
            {
                HexCoord? hex = PickFromSector(sectors[sectorIndex], placedOutside, null);
                sectorIndex++;
                if (hex == null)
                    continue;

                var yields = new ResourceYields();
                AddYieldUnit(yields, farTypes[farPlaced]);
                HexResourceBonusRegistry.Set(hex.Value, yields);
                resourceDisplay?.RefreshHex(hex.Value);
                placedOutside.Add(hex.Value);
                farPlaced++;
            }

            // Sector rejection is possible near map edges/start zones. Preserve exact target counts
            // with a global fallback while keeping the same spacing rules.
            while (techPlaced < techTarget)
            {
                HexCoord? hex = PickFromSector(outsideCandidates, placedOutside, placedNear);
                if (hex == null)
                    break;

                var yields = new ResourceYields();
                AddYieldUnit(yields, ResourceType.Tech);
                HexResourceBonusRegistry.Set(hex.Value, yields);
                resourceDisplay?.RefreshHex(hex.Value);
                placedOutside.Add(hex.Value);
                techPlaced++;
            }

            while (farPlaced < farTypes.Count)
            {
                HexCoord? hex = PickFromSector(outsideCandidates, placedOutside, null);
                if (hex == null)
                    break;

                var yields = new ResourceYields();
                AddYieldUnit(yields, farTypes[farPlaced]);
                HexResourceBonusRegistry.Set(hex.Value, yields);
                resourceDisplay?.RefreshHex(hex.Value);
                placedOutside.Add(hex.Value);
                farPlaced++;
            }
        }

        // Splits `candidates` into up to `sectorCount` angular buckets around the field's (0,0)
        // centre (see BucketByAngleAroundCenter in CitadelSetupController.cs, shared with
        // starting-hex assignment) so a caller can draw one hex per bucket instead of purely at
        // random and get a result that's actually spread across the map (the user's own later
        // call). Empty buckets are dropped, so the result can hold fewer than `sectorCount`
        // entries if candidates are sparse or clumped in one region.
        private List<List<HexCoord>> BuildEvenSectors(List<HexCoord> candidates, int sectorCount)
        {
            var sectors = new List<List<HexCoord>>();
            if (sectorCount <= 0 || candidates.Count == 0)
                return sectors;

            foreach (List<HexCoord> bucket in BucketByAngleAroundCenter(candidates, sectorCount))
                if (bucket.Count > 0)
                    sectors.Add(bucket);

            return sectors;
        }

        // One random outside candidate, keeping all outside resource hexes at least two steps
        // apart. When `forbiddenAdjacent` is supplied (Tech placement), the candidate must also
        // not touch any near H/E/M resource.
        private static HexCoord? PickFromSector(
            List<HexCoord> sector,
            List<HexCoord> placed,
            List<HexCoord> forbiddenAdjacent)
        {
            List<HexCoord> pool = sector.Where(h =>
                HexResourceBonusRegistry.GetBonus(h) == null &&
                placed.All(p => HexGridMath.Distance(h, p) >= 2) &&
                (forbiddenAdjacent == null || forbiddenAdjacent.All(p => HexGridMath.Distance(h, p) >= 2)))
                .ToList();
            return pool.Count == 0 ? (HexCoord?)null : pool[Random.Range(0, pool.Count)];
        }

        private static void AddYieldUnit(ResourceYields yields, ResourceType type)
        {
            switch (type)
            {
                case ResourceType.Human: yields.human++; break;
                case ResourceType.Energy: yields.energy++; break;
                case ResourceType.Materials: yields.materials++; break;
                case ResourceType.Tech: yields.tech++; break;
            }
        }

        private static List<ResourceType> BuildBalancedFarTypes(int count)
        {
            var result = new List<ResourceType>();
            if (count <= 0)
                return result;

            int each = count / NearResourceTypes.Length;
            int remainder = count % NearResourceTypes.Length;

            foreach (ResourceType type in NearResourceTypes)
                for (int i = 0; i < each; i++)
                    result.Add(type);

            List<ResourceType> extraOrder =
                PickRandomDistinct(NearResourceTypes.ToList(), NearResourceTypes.Length);
            for (int i = 0; i < remainder; i++)
                result.Add(extraOrder[i]);

            return PickRandomDistinct(result, result.Count);
        }

        // Neutral armies: 3-5 on a 12x9 map, 12-15 on a 16x13 one (see CalibratedCount) — never
        // on a player's own citadel hex or one of its immediate neighbours (the user's own
        // call, "Recommended" option), and never adjacent to another neutral army either (2.1,
        // the user's own call — at least 1 empty hex of gap between any two), otherwise
        // anywhere. Composition is no longer rolled — each placed army is one whole,
        // hand-authored ArmyDefinition from neutralArmyCatalog.MapArmies, one per hex;
        // event-only guards are excluded from these ordinary map rolls.
        private void GenerateNeutralArmies()
        {
            if (map == null || gameConfig == null || neutralArmyCatalog == null || hexSelectionController == null || _neutralPlayer == null)
                return;
            List<ArmyDefinition> mapArmies = neutralArmyCatalog.MapArmies.ToList();
            if (mapArmies.Count == 0)
                return;

            HashSet<HexCoord> excluded = BuildCitadelExclusion();
            excluded.UnionWith(BuildCityRuinsExclusion());
            List<HexCoord> candidates = map.AllCoords.Where(h => map.CanEnter(h) && !excluded.Contains(h)).ToList();
            if (candidates.Count == 0)
                return;

            int hexCount = HexCountForRadius(map.FieldRadius);
            int min = Mathf.Max(1, CalibratedCount(3, 12, hexCount));
            int max = Mathf.Max(min, CalibratedCount(5, 15, hexCount));
            int target = Mathf.Clamp(Random.Range(min, max + 1), 0, Mathf.Min(candidates.Count, mapArmies.Count));

            List<ArmyDefinition> shuffledArmies = PickRandomDistinct(mapArmies, mapArmies.Count);

            var placedHexes = new List<HexCoord>();
            int armyIndex = 0;
            while (placedHexes.Count < target && armyIndex < shuffledArmies.Count)
            {
                List<HexCoord> pool = candidates.Where(h =>
                    !placedHexes.Contains(h) &&
                    !placedHexes.Any(p => HexGridMath.Neighbors(p).Contains(h)))
                    .ToList();
                if (pool.Count == 0)
                    break;

                HexCoord hex = pool[Random.Range(0, pool.Count)];
                placedHexes.Add(hex);
                SpawnNeutralArmy(hex, shuffledArmies[armyIndex]);
                armyIndex++;
            }
        }

        // Every "City ruins" hex plus its immediate neighbours (project owner's own call,
        // 2026-08-22 — neutral armies/events shouldn't cluster right next to a ruins outpost).
        // A ruins hex itself still gets exactly one garrisoned army (GenerateCityRuinsGarrisons)
        // and exactly one guaranteed event (GenerateRandomEvents' guaranteedHexes tier) — this
        // exclusion only keeps everything ELSE off the hex and its ring, same shape as
        // BuildCitadelExclusion below. Reuses GetCityRuinsHexes (CitadelSetupController.cs) —
        // terrain is already fully painted on `map` before this whole setup step starts, see
        // that method's own comment.
        private HashSet<HexCoord> BuildCityRuinsExclusion()
        {
            var excluded = new HashSet<HexCoord>();
            foreach (HexCoord ruin in GetCityRuinsHexes())
            {
                excluded.Add(ruin);
                foreach (HexCoord neighbor in HexGridMath.Neighbors(ruin))
                    excluded.Add(neighbor);
            }
            return excluded;
        }

        // Every player's citadel hex plus its immediate neighbours — used by GenerateNeutralArmies
        // only (GenerateRandomEvents used to share this too, but now uses the narrower
        // BuildCitadelHexExclusion below instead — the user's own later call to let events land
        // next to a starting citadel, just never on it).
        private HashSet<HexCoord> BuildCitadelExclusion()
        {
            var excluded = new HashSet<HexCoord>();
            foreach (PlayerSetupData player in _allPlayers)
            {
                if (!player.CitadelHexQ.HasValue || !player.CitadelHexR.HasValue)
                    continue;
                var citadelHex = new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value);
                excluded.Add(citadelHex);
                foreach (HexCoord neighbor in HexGridMath.Neighbors(citadelHex))
                    excluded.Add(neighbor);
            }
            return excluded;
        }

        // Just every player's citadel hex itself — no neighbours — used by GenerateRandomEvents
        // (the user's own later call: events may now land adjacent to a starting citadel, only
        // the citadel's own hex stays off-limits).
        private HashSet<HexCoord> BuildCitadelHexExclusion()
        {
            var excluded = new HashSet<HexCoord>();
            foreach (PlayerSetupData player in _allPlayers)
                if (player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue)
                    excluded.Add(new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value));
            return excluded;
        }

        // The "City ruins" terrain name driving GenerateCityRuinsGarrisons below is owned by
        // HexMap.CityRuinsTerrainName / HexMap.IsCityRuins (the AI reads the same rule).

        // Chance that any single eligible "City ruins" hex becomes a garrisoned outpost at all
        // (the user's own later call, 2026-08-23) — a miss leaves that ruins hex with no neutral
        // army and, since GenerateRandomEvents only guarantees an event on hexes already carrying
        // a neutral-owned army, no event either.
        private const float CityRuinsGarrisonChance = 0.3f;

        // Every eligible "City ruins" hex on the map has a CityRuinsGarrisonChance shot at being a
        // garrisoned outpost (the user's own later call, in addition to
        // GenerateNeutralArmies/GenerateRandomEvents above) — a real neutral defending army,
        // unless a citadel already sits there (can't hostile-garrison a player's own base) or
        // GenerateNeutralArmies already placed one here (no point stacking a second army on top).
        // Deliberately doesn't place an event directly: any hex carrying a neutral-owned army
        // already becomes a GUARANTEED event target in GenerateRandomEvents below (same check,
        // "any army here owned by _neutralPlayer") — must run after GenerateNeutralArmies and
        // before GenerateRandomEvents so that hookup actually fires.
        private void GenerateCityRuinsGarrisons()
        {
            if (map == null || neutralArmyCatalog == null || hexSelectionController == null || _neutralPlayer == null)
                return;
            List<ArmyDefinition> mapArmies = neutralArmyCatalog.MapArmies.ToList();
            if (mapArmies.Count == 0)
                return;

            var citadelHexes = new HashSet<HexCoord>();
            foreach (PlayerSetupData player in _allPlayers)
                if (player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue)
                    citadelHexes.Add(new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value));

            foreach (HexCoord hex in map.AllCoords)
            {
                if (citadelHexes.Contains(hex))
                    continue;
                if (!map.IsCityRuins(hex))
                    continue;
                if (ArmyRegistry.AllAt(hex).Any(a => a.Owner == _neutralPlayer))
                    continue;
                if (Random.value >= CityRuinsGarrisonChance)
                    continue;

                ArmyDefinition definition = mapArmies[Random.Range(0, mapArmies.Count)];
                SpawnNeutralArmy(hex, definition);
            }
        }

        // Returns the ArmyData it just built (or null if every entry failed to resolve, in which
        // case nothing was actually placed). Only ever called for a real, on-the-map army any
        // more (GenerateNeutralArmies) — a Hex Event's own guard is no longer spawned here at
        // all (see PlaceEvent's own comment on why), so unlike before this always gets a marker.
        private ArmyData SpawnNeutralArmy(HexCoord hex, ArmyDefinition definition)
        {
            if (!map.CanEnter(hex)) return null;
            var army = new ArmyData { Name = definition.name, Hex = hex, Owner = _neutralPlayer };
            ArmyRegistry.Register(army);

            foreach (ArmyUnitEntry entry in definition.members)
            {
                CardDefinition card = neutralArmyCatalog.ResolveCard(entry?.cardKey);
                if (card == null)
                    continue;
                for (int i = 0; i < entry.count; i++)
                {
                    UnitData spawned = SpawnNeutralUnit(card, isHero: card.cardType == CardType.Hero);
                    if (spawned != null)
                        army.AddMemberSorted(spawned);
                }
            }

            if (army.Members.Count == 0)
                return null; // every entry failed to resolve — nothing to actually show on this hex

            // Only now, once every member's already in — CreateArmyMarker's very first
            // RestackArmiesOn needs a non-empty army to have anything to show (see
            // HexSelectionController.NonEmptyArmiesAt).
            hexSelectionController.CreateArmyMarker(army);
            return army;
        }

        private UnitData SpawnNeutralUnit(CardDefinition definition, bool isHero)
        {
            return hexSelectionController.SpawnUnit(definition.displayName, _neutralPlayer, definition.moveMax,
                definition.activationApCost, isHero, definition.commandRating, definition.art, definition.grantedAbilities,
                definition.attack, definition.range, definition.hitPoints, definition.initiative, definition.fate,
                definition.defenseRating, definition.resistanceRating, definition.unitTypeTags, definition.detailArt,
                definition.apCost, definition.resourceCost);
        }

        // Chance that any single hex carrying a neutral army (GenerateNeutralArmies or
        // GenerateCityRuinsGarrisons) becomes a guaranteed event target — down from a flat 100%,
        // per the project owner's own later call (2026-09-13): City ruins alone (below) now cover
        // the "always guaranteed" tier, so a plain neutral-army camp doesn't need to as well.
        private const float NeutralArmyEventChance = 0.5f;

        // Events: 12-15 hexes on a 12x9 map, 24-30 on a 16x13 one (see CalibratedCount). Raised
        // from the previous 6-12/24-30 per the project owner's own later call (2026-09-13) to
        // absorb City ruins now always guaranteeing an event (below) without starving the plain
        // fill on a small map. Never on a citadel hex itself, but — unlike GenerateNeutralArmies —
        // its immediate neighbours are fair game (BuildCitadelHexExclusion, not
        // BuildCitadelExclusion; the user's own earlier call).
        //
        // Two guaranteed tiers, both exempt from the "no resource bonus" rule below (an army or a
        // ruins hex sharing its hex with a resource is already an accepted stack, see
        // GenerateResources's own comment, and now the event stacks with both):
        //  - every City ruins hex, ALWAYS (the project owner's own later call — independent of
        //    whether GenerateCityRuinsGarrisons actually rolled a garrison there).
        //  - every other hex carrying a neutral army, each independently rolling
        //    NeutralArmyEventChance.
        // Neither tier's own members are checked against each other for adjacency — both are
        // guaranteed by definition, so two guaranteed hexes landing next to each other (e.g. a
        // ruins hex beside an army camp) still both get their event. Only the remaining budget's
        // plain (army-free, resource-free, non-ruins) fill is barred from landing adjacent to any
        // already-placed event this pass — City ruins no longer need their own separate exclusion
        // ring for that (see this method's previous revision): being a guaranteed hex now already
        // makes them "already-placed" by the time the plain pass runs, so the general adjacency
        // check covers them for free.
        // The event still spawns its own separate guard on a guaranteed hex — two armies
        // coexisting on one hex, same as ArmyRegistry already supports — it never reuses that
        // unrelated army as its own guard.
        private void GenerateRandomEvents()
        {
            if (map == null || gameConfig == null || eventCatalog == null || eventCatalog.events == null || eventCatalog.events.Count == 0)
                return;

            HashSet<HexCoord> excluded = BuildCitadelHexExclusion();

            List<HexCoord> ruinsHexes = GetCityRuinsHexes().Where(h => !excluded.Contains(h)).ToList();
            var ruinsSet = new HashSet<HexCoord>(ruinsHexes);

            List<HexCoord> armyHexes = map.AllCoords
                .Where(h => !excluded.Contains(h) && !ruinsSet.Contains(h) && ArmyRegistry.AllAt(h).Any(a => a.Owner == _neutralPlayer))
                .Where(_ => Random.value < NeutralArmyEventChance)
                .ToList();

            List<HexCoord> guaranteedHexes = ruinsHexes.Concat(armyHexes).ToList();
            var guaranteedSet = new HashSet<HexCoord>(guaranteedHexes);

            List<HexCoord> candidates = map.AllCoords.Where(h => map.CanEnter(h))
                .Where(h => !excluded.Contains(h) && HexResourceBonusRegistry.GetBonus(h) == null && !guaranteedSet.Contains(h))
                .ToList();
            if (candidates.Count == 0 && guaranteedHexes.Count == 0)
                return;

            int hexCount = HexCountForRadius(map.FieldRadius);
            int min = Mathf.Max(1, CalibratedCount(12, 24, hexCount));
            int max = Mathf.Max(min, CalibratedCount(15, 30, hexCount));
            // The guaranteed army hexes draw from the same total budget rather than stacking on
            // top of it (matches the existing calibration's intent of "roughly this many event
            // hexes total") — the lower clamp bound just makes sure that budget is never rolled
            // smaller than what the guaranteed hexes alone already need. If eventCatalog has
            // fewer distinct definitions than there are guaranteed hexes, PickRandomDistinct
            // below simply can't cover all of them — a catalog-content limit, not something this
            // pass can work around.
            int target = Mathf.Clamp(Random.Range(min, max + 1), guaranteedHexes.Count,
                Mathf.Min(candidates.Count + guaranteedHexes.Count, eventCatalog.events.Count));

            List<EventDefinition> chosenEvents = PickRandomDistinct(eventCatalog.events, target);
            if (chosenEvents.Count == 0)
                return;

            var placedHexes = new List<HexCoord>();
            int eventIndex = 0;

            // Guaranteed hexes claim the front of chosenEvents first, in random order (reusing
            // PickRandomDistinct purely as a shuffle here) — the plain pass below draws from
            // whatever's left, so the two passes never compete for the same EventDefinition.
            foreach (HexCoord hex in PickRandomDistinct(guaranteedHexes, guaranteedHexes.Count))
            {
                if (eventIndex >= chosenEvents.Count)
                    break;
                placedHexes.Add(hex);
                PlaceEvent(hex, chosenEvents[eventIndex]);
                eventIndex++;
            }

            for (; eventIndex < chosenEvents.Count; eventIndex++)
            {
                List<HexCoord> pool = candidates.Where(h =>
                    !placedHexes.Contains(h) &&
                    !placedHexes.Any(p => HexGridMath.Neighbors(p).Contains(h)))
                    .ToList();
                if (pool.Count == 0)
                    continue;

                HexCoord hex = pool[Random.Range(0, pool.Count)];
                placedHexes.Add(hex);
                PlaceEvent(hex, chosenEvents[eventIndex]);
            }
        }

        // guardArmyName resolves through neutralArmyCatalog (same [ArmyTag] convention every
        // other map-guard reference uses, see EventDefinition's own comment) but is deliberately
        // NEVER spawned here — only its composition is resolved once, right now (mirrors
        // ResolvedCardRewards below, same "EventDefinition has no back-reference to resolve
        // against later" reason). HexSelectionController.Events.cs's own SpawnEventGuard builds
        // the real ArmyData from this, but only once the player actually commits to Explore — a
        // guard that physically existed on the map from generation onward (even with its marker
        // hidden) was still a live ArmyRegistry entry BattleInitiator.FindEnemyAt could find: a
        // red move-preview arrow leaked its presence before the player ever got near it, and a
        // "collision hex" (an unrelated pre-existing neutral army sharing this event's hex, see
        // GenerateRandomEvents) looked permanently un-cleared even after that unrelated army was
        // actually beaten (see the user's own report). Any RewardType.Card reward is resolved to
        // a CardDefinition once, right here too, since EventDefinition has no back-reference to
        // eventCatalog for HexEventRewardGranter to resolve it again later (see
        // HexEventRegistry.Entry.ResolvedCardRewards).
        private void PlaceEvent(HexCoord hex, EventDefinition definition)
        {
            if (!map.CanEnter(hex)) return;
            EventVariant variant = definition.variants != null && definition.variants.Count > 0
                ? definition.variants[Random.Range(0, definition.variants.Count)] : null;
            string chosenGuard = variant != null ? variant.guardArmyName : definition.guardArmyName;
            // Resolve the random resource mix once, at placement. Skip, retreat and a later
            // revisit must all see the same guard and payout.
            var chosenRewards = variant != null
                ? new List<RewardEntry>(variant.rewards ?? new List<RewardEntry>())
                : new List<RewardEntry>(definition.rewards ?? new List<RewardEntry>());
            if (variant != null && variant.resourceCount > 0)
            {
                var resources = new ResourceYields();
                for (int i = 0; i < variant.resourceCount; i++)
                    switch (Random.Range(0, 4))
                    {
                        case 0: resources.human++; break;
                        case 1: resources.energy++; break;
                        case 2: resources.materials++; break;
                        default: resources.tech++; break;
                    }
                chosenRewards.Insert(0, new RewardEntry { type = RewardType.Resources, resources = resources });
            }
            string guardArmyName = null;
            var resolvedGuardMembers = new List<(CardDefinition, int)>();
            if (!string.IsNullOrEmpty(chosenGuard) && neutralArmyCatalog != null)
            {
                ArmyDefinition armyDef = neutralArmyCatalog.GetArmy(chosenGuard);
                if (armyDef != null)
                {
                    guardArmyName = armyDef.name;
                    foreach (ArmyUnitEntry entry in armyDef.members)
                    {
                        CardDefinition card = neutralArmyCatalog.ResolveCard(entry?.cardKey);
                        if (card != null)
                            resolvedGuardMembers.Add((card, entry.count));
                    }
                }
            }

            var resolvedCardRewards = new List<(RewardEntry, CardDefinition)>();
            if (chosenRewards != null)
                foreach (RewardEntry reward in chosenRewards)
                    if (reward.type == RewardType.Card)
                    {
                        CardDefinition card = eventCatalog.ResolveCard(reward.cardKey);
                        if (card != null && (card.cardType == CardType.Unit || card.cardType == CardType.Equipment))
                            resolvedCardRewards.Add((reward, card));
                    }

            HexEventRegistry.Set(hex, definition, guardArmyName, resolvedGuardMembers, _neutralPlayer,
                resolvedCardRewards, chosenRewards);
        }

        // Placeholder pass — no special hexes exist in this project yet. Same idea as
        // GenerateRandomEvents.
        private void GenerateSpecialHexes()
        {
        }

        // Shared by GenerateResources/GenerateNeutralArmies — up to `count` distinct entries
        // picked without replacement from `pool` (fewer than `count` if the pool runs out).
        private static List<T> PickRandomDistinct<T>(List<T> pool, int count)
        {
            var working = new List<T>(pool);
            var result = new List<T>(Mathf.Min(count, working.Count));
            while (result.Count < count && working.Count > 0)
            {
                int index = Random.Range(0, working.Count);
                result.Add(working[index]);
                working.RemoveAt(index);
            }
            return result;
        }
    }
}
