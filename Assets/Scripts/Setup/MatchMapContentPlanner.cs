using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using UnityEngine;

namespace Game.Setup
{
    public static class MatchMapContentPlanner
    {
        internal sealed class PlannedEvent
        {
            internal readonly HexCoord Hex;
            internal readonly EventDefinition Definition;
            internal readonly string GuardName;
            internal readonly List<(CardDefinition, int)> GuardMembers;
            internal readonly List<(RewardEntry, CardDefinition)> CardRewards;
            internal readonly List<RewardEntry> Rewards;

            internal PlannedEvent(HexCoord hex, EventDefinition definition, string guardName,
                List<(CardDefinition, int)> guardMembers, List<(RewardEntry, CardDefinition)> cardRewards,
                List<RewardEntry> rewards)
            {
                Hex = hex;
                Definition = definition;
                GuardName = guardName;
                GuardMembers = guardMembers;
                CardRewards = cardRewards;
                Rewards = rewards;
            }
        }

        private static HashSet<HexCoord> WithNeighbors(IEnumerable<HexCoord> centers)
            => new HashSet<HexCoord>(centers.SelectMany(hex => HexGridMath.Neighbors(hex).Prepend(hex)));

        internal static IReadOnlyList<(HexCoord Hex, ArmyDefinition Army)> PlanNeutralArmies(
            HexMap map, IReadOnlyList<HexCoord> citadels, IReadOnlyList<HexCoord> ruins,
            NeutralArmyCatalog neutralArmyCatalog, IMapGenerationRandom random, out int target)
        {
            var plan = new List<(HexCoord, ArmyDefinition)>();
            target = 0;
            List<ArmyDefinition> mapArmies = neutralArmyCatalog.MapArmies.ToList();
            if (mapArmies.Count == 0)
                return plan;

            HashSet<HexCoord> excluded = WithNeighbors(citadels);
            excluded.UnionWith(WithNeighbors(ruins));
            List<HexCoord> candidates = map.AllCoords.Where(h => map.CanEnter(h) && !excluded.Contains(h)).ToList();
            if (candidates.Count == 0)
                return plan;

            int hexCount = HexCountForRadius(map.FieldRadius);
            int min = Mathf.Max(1, CalibratedCount(3, 12, hexCount));
            int max = Mathf.Max(min, CalibratedCount(5, 15, hexCount));
            target = Mathf.Clamp(random.Range(min, max + 1), 0, Mathf.Min(candidates.Count, mapArmies.Count));

            List<ArmyDefinition> shuffledArmies = PickRandomDistinct(mapArmies, mapArmies.Count, random);

            var hexes = PlanNeutralArmyHexes(candidates, citadels, ruins, target, map.FieldRadius, random);
            for (int i = 0; i < hexes.Count; i++) plan.Add((hexes[i], shuffledArmies[i]));
            return plan;
        }

        internal static IReadOnlyList<(HexCoord Hex, ArmyDefinition Army)> PlanRuinsGarrisons(
            HexMap map, IReadOnlyList<HexCoord> citadels, NeutralArmyCatalog catalog,
            System.Func<HexCoord, bool> hasNeutralArmy, IMapGenerationRandom random)
        {
            var plan = new List<(HexCoord, ArmyDefinition)>();
            var armies = catalog.MapArmies.ToList();
            if (armies.Count == 0) return plan;
            var excluded = new HashSet<HexCoord>(citadels);
            foreach (HexCoord hex in map.AllCoords)
            {
                if (excluded.Contains(hex) || !map.IsCityRuins(hex) || hasNeutralArmy(hex)) continue;
                if (random.Value < CityRuinsGarrisonChance)
                    plan.Add((hex, armies[random.Range(0, armies.Count)]));
            }
            return plan;
        }

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

        private const float CityRuinsGarrisonChance = 0.3f;

        // Pure proposal phase within the existing content owner. Ruins count as potential
        // enemy concentrations with their unchanged garrison probability, never as fixed armies.
        public static List<HexCoord> PlanNeutralArmyHexes(IReadOnlyList<HexCoord> candidates,
            IReadOnlyList<HexCoord> citadels, IReadOnlyList<HexCoord> ruins, int target, int radius, IMapGenerationRandom random)
        {
            var proposals = new List<List<HexCoord>>();
            var scores = new List<float>();
            int bestCount = -1; float bestScore = float.PositiveInfinity;
            for (int attempt = 0; attempt < 128; attempt++)
            {
                var plan = new List<HexCoord>();
                foreach (var hex in PickRandomDistinct(candidates.ToList(), candidates.Count, random))
                {
                    if (plan.Count >= target) break;
                    if (plan.Any(p => HexGridMath.Distance(p, hex) < 2)) continue;
                    plan.Add(hex);
                }
                if (plan.Count < bestCount) continue;
                if (plan.Count > bestCount)
                { bestCount = plan.Count; bestScore = float.PositiveInfinity; proposals.Clear(); scores.Clear(); }
                float score = NeutralLayoutScore(plan, candidates, citadels, ruins, radius);
                proposals.Add(plan); scores.Add(score); bestScore = Mathf.Min(bestScore, score);
            }
            var acceptable = Enumerable.Range(0, proposals.Count).Where(i => scores[i] <= bestScore + .04f).ToList();
            return proposals[acceptable[random.Range(0, acceptable.Count)]];
        }

        private static float NeutralLayoutScore(List<HexCoord> plan, IReadOnlyList<HexCoord> candidates,
            IReadOnlyList<HexCoord> citadels, IReadOnlyList<HexCoord> ruins, int radius)
        {
            if (plan.Count == 0) return 0;
            float scale = Mathf.Max(1, radius);
            float score = candidates.Average(h => (float)plan.Min(p => HexGridMath.Distance(p, h))) / scale;
            if (plan.Count > 1)
                score += 1.5f * plan.Average(h => 1f / plan.Where(p => !p.Equals(h)).Min(p => HexGridMath.Distance(p, h)));
            if (citadels.Count > 1)
            {
                var pressure = citadels.Select(c => plan.Sum(p => 1f / Mathf.Max(1, HexGridMath.Distance(c, p)))).ToList();
                score += (pressure.Max() - pressure.Min()) / Mathf.Max(.001f, pressure.Max());
            }
            if (ruins.Count > 0)
                score += CityRuinsGarrisonChance * plan.Average(h => 1f / Mathf.Max(1, ruins.Min(r => HexGridMath.Distance(h, r))));
            return score;
        }

        // Shared by content proposals and events — up to `count` distinct entries
        // picked without replacement from `pool` (fewer than `count` if the pool runs out).
        private static List<T> PickRandomDistinct<T>(List<T> pool, int count, IMapGenerationRandom random)
        {
            var working = new List<T>(pool);
            var result = new List<T>(Mathf.Min(count, working.Count));
            while (result.Count < count && working.Count > 0)
            {
                int index = random.Range(0, working.Count);
                result.Add(working[index]);
                working.RemoveAt(index);
            }
            return result;
        }
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
        internal static IReadOnlyList<PlannedEvent> PlanEvents(HexMap map, IReadOnlyList<HexCoord> citadels,
            IReadOnlyList<HexCoord> ruins, EventCatalog eventCatalog, NeutralArmyCatalog neutralArmyCatalog,
            System.Func<HexCoord, bool> hasNeutralArmy, System.Func<HexCoord, bool> hasResources,
            IMapGenerationRandom random)
        {
            var plan = new List<PlannedEvent>();
            if (eventCatalog?.events == null || eventCatalog.events.Count == 0) return plan;

            var excluded = new HashSet<HexCoord>(citadels);
            var baseExcluded = WithNeighbors(citadels);
            bool CanPlaceEventAt(HexCoord hex, EventDefinition definition) =>
                definition != null && map.CanEnter(hex) && !(definition.grantsBase ? baseExcluded : excluded).Contains(hex);
            List<HexCoord> ruinsHexes = ruins.Where(h => !excluded.Contains(h)).ToList();
            var ruinsSet = new HashSet<HexCoord>(ruinsHexes);

            List<HexCoord> armyHexes = map.AllCoords
                .Where(h => !excluded.Contains(h) && !ruinsSet.Contains(h) && hasNeutralArmy(h))
                .Where(_ => random.Value < NeutralArmyEventChance)
                .ToList();

            List<HexCoord> guaranteedHexes = ruinsHexes.Concat(armyHexes).ToList();
            var guaranteedSet = new HashSet<HexCoord>(guaranteedHexes);

            List<HexCoord> candidates = map.AllCoords.Where(h => map.CanEnter(h))
                .Where(h => !excluded.Contains(h) && !hasResources(h) && !guaranteedSet.Contains(h))
                .ToList();
            EventDefinition library = eventCatalog.events.FirstOrDefault(e => e?.grantsRandomCollectionCard == true);
            // The mandatory Library still needs a home on resource-saturated maps. Share
            // one resource hex only when neither an ordinary nor a guaranteed site is legal.
            // The one reserved candidate is consumed by the Library before ordinary events.
            if (library != null && candidates.Count == 0 && !guaranteedHexes.Any(h => CanPlaceEventAt(h, library)))
            {
                List<HexCoord> fallback = map.AllCoords.Where(h => CanPlaceEventAt(h, library)).ToList();
                if (fallback.Count > 0)
                    candidates.Add(fallback[random.Range(0, fallback.Count)]);
            }
            if (candidates.Count == 0 && guaranteedHexes.Count == 0)
                return plan;

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
            int target = Mathf.Clamp(random.Range(min, max + 1), guaranteedHexes.Count,
                Mathf.Min(candidates.Count + guaranteedHexes.Count, eventCatalog.events.Count));

            // Reserve one place in the existing budget for the Library. Place it first so
            // ordinary events cannot use up its legal hexes through the spacing rule.
            List<EventDefinition> chosenEvents = PickRandomDistinct(
                eventCatalog.events.Where(e => e != library).ToList(), target - (library != null ? 1 : 0), random);
            if (library != null && target > 0)
                chosenEvents.Insert(0, library);
            if (chosenEvents.Count == 0)
                return plan;

            var placedHexes = new List<HexCoord>();
            int eventIndex = 0;

            // Guaranteed hexes claim compatible chosenEvents first, in random order (reusing
            // PickRandomDistinct purely as a shuffle here) — the plain pass below draws from
            // whatever's left, so the two passes never compete for the same EventDefinition.
            // Fill the most constrained guaranteed hexes first so a distant ruin does not
            // take the last ordinary event that a citadel's neighbour could receive.
            foreach (HexCoord hex in PickRandomDistinct(guaranteedHexes, guaranteedHexes.Count, random)
                .OrderBy(h => chosenEvents.Count(definition => CanPlaceEventAt(h, definition))))
            {
                if (eventIndex >= chosenEvents.Count)
                    break;
                int eligibleIndex = chosenEvents.FindIndex(eventIndex, definition => CanPlaceEventAt(hex, definition));
                if (eligibleIndex < 0)
                    continue;
                // Leave a restricted event in the pool for a later, legal hex rather than
                // discarding it or removing ordinary events from a citadel's neighbouring ruins.
                (chosenEvents[eventIndex], chosenEvents[eligibleIndex]) =
                    (chosenEvents[eligibleIndex], chosenEvents[eventIndex]);
                placedHexes.Add(hex);
                plan.Add(ResolveEvent(hex, chosenEvents[eventIndex], eventCatalog, neutralArmyCatalog, random));
                eventIndex++;
            }

            for (; eventIndex < chosenEvents.Count; eventIndex++)
            {
                List<HexCoord> pool = candidates.Where(h =>
                    CanPlaceEventAt(h, chosenEvents[eventIndex]) && !placedHexes.Contains(h) &&
                    !placedHexes.Any(p => HexGridMath.Neighbors(p).Contains(h)))
                    .ToList();
                if (pool.Count == 0)
                    continue;

                HexCoord hex = pool[random.Range(0, pool.Count)];
                placedHexes.Add(hex);
                plan.Add(ResolveEvent(hex, chosenEvents[eventIndex], eventCatalog, neutralArmyCatalog, random));
            }
            return plan;
        }

        private static PlannedEvent ResolveEvent(HexCoord hex, EventDefinition definition, EventCatalog eventCatalog,
            NeutralArmyCatalog neutralArmyCatalog, IMapGenerationRandom random)
        {
            EventVariant variant = definition.variants != null && definition.variants.Count > 0
                ? definition.variants[random.Range(0, definition.variants.Count)] : null;
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
                    switch (random.Range(0, 4))
                    {
                        case 0: resources.human++; break;
                        case 1: resources.energy++; break;
                        case 2: resources.materials++; break;
                        default: resources.tech++; break;
                    }
                chosenRewards.Insert(0, new RewardEntry { type = RewardType.Resources, resources = resources });
            }
            ArmyDefinition guard = string.IsNullOrEmpty(chosenGuard) || neutralArmyCatalog == null
                ? null : neutralArmyCatalog.GetArmy(chosenGuard);
            var resolvedGuardMembers = guard == null ? new List<(CardDefinition, int)>()
                : ResolveArmyMembers(neutralArmyCatalog, guard).ToList();

            var resolvedCardRewards = new List<(RewardEntry, CardDefinition)>();
            foreach (RewardEntry reward in chosenRewards)
                if (reward.type == RewardType.Card)
                {
                    CardDefinition card = eventCatalog.ResolveCard(reward.cardKey);
                    if (EventCatalog.CanGrantCard(card))
                        resolvedCardRewards.Add((reward, card));
                }

            return new PlannedEvent(hex, definition, guard?.name, resolvedGuardMembers, resolvedCardRewards, chosenRewards);
        }

        internal static IEnumerable<(CardDefinition Card, int Count)> ResolveArmyMembers(NeutralArmyCatalog catalog, ArmyDefinition army)
        {
            foreach (var entry in army.members)
            {
                var card = catalog.ResolveCard(entry?.cardKey);
                if (card != null) yield return (card, entry.count);
            }
        }
    }
}
