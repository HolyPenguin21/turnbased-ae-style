using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
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

        // Two data points the user supplied directly (Normal 12x9 map, and a larger 16x13 one)
        // — every count below is linearly interpolated/extrapolated from hex count (width *
        // height) through those two points, so a custom map size still gets a proportional
        // answer instead of a hardcoded number. Two points fully determine a line; recalibrate
        // by changing these pairs if a third data point ever narrows things down further.
        private const int CalibrationSmallHexes = 12 * 9;
        private const int CalibrationLargeHexes = 16 * 13;

        private static int CalibratedCount(int smallValue, int largeValue, int hexCount)
        {
            float t = (hexCount - CalibrationSmallHexes) / (float)(CalibrationLargeHexes - CalibrationSmallHexes);
            return Mathf.RoundToInt(Mathf.Lerp(smallValue, largeValue, t));
        }

        private static readonly ResourceType[] AllResourceTypes =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        // Resources, per the user's own spec (2.2) — two groups per citadel'd player, not a
        // single size-calibrated random spread any more:
        //  - "Near": one hex of EACH of the 4 resource types (amount 1, always), 1-3 hex steps
        //    from that player's own citadel (never the citadel itself, never beyond radius 3 —
        //    no other resource spawns inside that radius at all, the user's own explicit call).
        //  - "Far": 2 more hexes per player, anywhere else on the map, each with up to 2
        //    resource units of random type (see RollFarYields) — never adjacent to another far
        //    hex, checked map-wide rather than just within one player's own pair (the user's own
        //    call, since two different players' far hexes could otherwise still end up touching).
        // Throughout both groups: two resource hexes of the SAME type are never adjacent (2.2.1,
        // see HexHasAdjacentType) — citadel hexes themselves are skipped automatically since they
        // already carry their own fixed bonus (see FinalizePlayer/gameConfig.
        // citadelResourceBonus) and every near-zone hex is excluded from the far pass regardless.
        // A hex getting picked here doesn't exclude it from GenerateNeutralArmies — sharing is
        // fine, per the user's own earlier call ("the army guards the resource").
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
            var mapHexes = new HashSet<HexCoord>(map.AllCoords);
            var nearZone = new HashSet<HexCoord>();

            foreach (PlayerSetupData player in citadelPlayers)
            {
                var citadel = new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value);
                nearZone.UnionWith(HexGridMath.HexesInRange(citadel, 3).Where(mapHexes.Contains));

                List<HexCoord> band = HexGridMath.HexesInRange(citadel, 3)
                    .Where(h => !h.Equals(citadel) && mapHexes.Contains(h))
                    .ToList();

                foreach (ResourceType type in PickRandomDistinct(AllResourceTypes.ToList(), AllResourceTypes.Length))
                {
                    List<HexCoord> pool = band
                        .Where(h => HexResourceBonusRegistry.GetBonus(h) == null && !HexHasAdjacentType(h, type))
                        .ToList();
                    if (pool.Count == 0)
                        continue; // band exhausted at this radius — extremely unlikely, skip rather than crash

                    HexCoord hex = pool[Random.Range(0, pool.Count)];
                    var yields = new ResourceYields();
                    AddYieldUnit(yields, type);
                    HexResourceBonusRegistry.Set(hex, yields);
                    resourceDisplay?.RefreshHex(hex);
                }
            }

            List<HexCoord> farCandidates = map.AllCoords
                .Where(h => !nearZone.Contains(h) && HexResourceBonusRegistry.GetBonus(h) == null)
                .ToList();
            var farHexes = new List<HexCoord>();
            int farTarget = citadelPlayers.Count * 2;

            while (farHexes.Count < farTarget)
            {
                List<HexCoord> pool = farCandidates.Where(h =>
                    HexResourceBonusRegistry.GetBonus(h) == null &&
                    !farHexes.Any(f => f.Equals(h) || HexGridMath.Neighbors(f).Contains(h)))
                    .ToList();
                if (pool.Count == 0)
                    break;

                HexCoord hex = pool[Random.Range(0, pool.Count)];
                ResourceYields yields = RollFarYields(hex);
                if (!yields.HasAnyYield)
                {
                    farCandidates.Remove(hex); // boxed in by adjacent types on every draw — try elsewhere
                    continue;
                }

                HexResourceBonusRegistry.Set(hex, yields);
                resourceDisplay?.RefreshHex(hex);
                farHexes.Add(hex);
            }
        }

        // 2.2.1: true if placing `type` on `hex` would put it next to another resource hex that
        // already carries that same type.
        private static bool HexHasAdjacentType(HexCoord hex, ResourceType type)
        {
            foreach (HexCoord neighbor in HexGridMath.Neighbors(hex))
            {
                ResourceYields bonus = HexResourceBonusRegistry.GetBonus(neighbor);
                if (bonus != null && bonus.Get(type) > 0)
                    return true;
            }
            return false;
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

        // 2.2.2.2: up to 2 resource units on a "far" hex, each drawn independently and uniformly
        // from whichever of the 4 types wouldn't violate 2.2.1 on this hex — landing on the same
        // type twice gives a single-type "2" (e.g. 2h0e0m0t), landing on two different types
        // splits 1/1 (e.g. 1h1e0m0t), matching the user's own examples. Empty result (every type
        // boxed in by a neighbor) is possible but rare — caller re-rolls a different hex instead.
        private ResourceYields RollFarYields(HexCoord hex)
        {
            List<ResourceType> allowed = AllResourceTypes.Where(t => !HexHasAdjacentType(hex, t)).ToList();
            var yields = new ResourceYields();
            if (allowed.Count == 0)
                return yields;

            AddYieldUnit(yields, allowed[Random.Range(0, allowed.Count)]);
            AddYieldUnit(yields, allowed[Random.Range(0, allowed.Count)]);
            return yields;
        }

        // Neutral armies: 3-5 on a 12x9 map, 12-15 on a 16x13 one (see CalibratedCount) — never
        // on a player's own citadel hex or one of its immediate neighbours (the user's own
        // call, "Recommended" option), and never adjacent to another neutral army either (2.1,
        // the user's own call — at least 1 empty hex of gap between any two), otherwise
        // anywhere. Composition is no longer rolled — each placed army is one whole,
        // hand-authored ArmyDefinition from neutralArmyCatalog.armies, one army per hex (the
        // user's own call), so at most armies.Count hexes ever get one.
        private void GenerateNeutralArmies()
        {
            if (map == null || gameConfig == null || neutralArmyCatalog == null || hexSelectionController == null || _neutralPlayer == null)
                return;
            if (neutralArmyCatalog.armies == null || neutralArmyCatalog.armies.Count == 0)
                return;

            HashSet<HexCoord> excluded = BuildCitadelExclusion();
            List<HexCoord> candidates = map.AllCoords.Where(h => !excluded.Contains(h)).ToList();
            if (candidates.Count == 0)
                return;

            int hexCount = gameConfig.mapGeneration.width * gameConfig.mapGeneration.height;
            int min = Mathf.Max(1, CalibratedCount(3, 12, hexCount));
            int max = Mathf.Max(min, CalibratedCount(5, 15, hexCount));
            int target = Mathf.Clamp(Random.Range(min, max + 1), 0, Mathf.Min(candidates.Count, neutralArmyCatalog.armies.Count));

            List<ArmyDefinition> shuffledArmies = PickRandomDistinct(neutralArmyCatalog.armies, neutralArmyCatalog.armies.Count);

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

        // Every player's citadel hex plus its immediate neighbours — shared by
        // GenerateNeutralArmies and GenerateRandomEvents so the exclusion rule can never drift
        // apart between the two passes.
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

        // Returns the ArmyData it just built (or null if every entry failed to resolve, in which
        // case nothing was actually placed). Only ever called for a real, on-the-map army any
        // more (GenerateNeutralArmies) — a Hex Event's own guard is no longer spawned here at
        // all (see PlaceEvent's own comment on why), so unlike before this always gets a marker.
        private ArmyData SpawnNeutralArmy(HexCoord hex, ArmyDefinition definition)
        {
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

        // Events: 6-12 hexes on a 12x9 map, 24-30 on a 16x13 one (see CalibratedCount — 3x the
        // original starting calibration, per the user's own explicit call). Never on a
        // citadel hex/its neighbours (same exclusion GenerateNeutralArmies uses). Every hex
        // already carrying a neutral army from the pass above (GenerateNeutralArmies, which
        // always runs first — see FinishAllPlacements) is a GUARANTEED event target, per the
        // user's own explicit call — not just eligible like a plain candidate, it always gets
        // one (and unlike a plain candidate, it's exempt from the "no resource bonus" rule
        // below: an army sharing its hex with a resource is already an accepted stack, see
        // GenerateResources's own comment, and now the event stacks with both). The event still
        // spawns its own separate guard there — two armies coexisting on one hex, same as
        // ArmyRegistry already supports — it never reuses that unrelated army as its own guard.
        // The remaining event budget then fills from plain (army-free, resource-free) hexes,
        // same as before, each additionally barred from landing adjacent to an already-placed
        // event this same pass.
        private void GenerateRandomEvents()
        {
            if (map == null || gameConfig == null || eventCatalog == null || eventCatalog.events == null || eventCatalog.events.Count == 0)
                return;

            HashSet<HexCoord> excluded = BuildCitadelExclusion();

            List<HexCoord> guaranteedHexes = map.AllCoords
                .Where(h => !excluded.Contains(h) && ArmyRegistry.AllAt(h).Any(a => a.Owner == _neutralPlayer))
                .ToList();

            List<HexCoord> candidates = map.AllCoords
                .Where(h => !excluded.Contains(h) && HexResourceBonusRegistry.GetBonus(h) == null && !guaranteedHexes.Contains(h))
                .ToList();
            if (candidates.Count == 0 && guaranteedHexes.Count == 0)
                return;

            int hexCount = gameConfig.mapGeneration.width * gameConfig.mapGeneration.height;
            int min = Mathf.Max(1, CalibratedCount(6, 24, hexCount));
            int max = Mathf.Max(min, CalibratedCount(12, 30, hexCount));
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
            string guardArmyName = null;
            var resolvedGuardMembers = new List<(CardDefinition, int)>();
            if (!string.IsNullOrEmpty(definition.guardArmyName) && neutralArmyCatalog != null)
            {
                ArmyDefinition armyDef = neutralArmyCatalog.GetArmy(definition.guardArmyName);
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
            if (definition.rewards != null)
                foreach (RewardEntry reward in definition.rewards)
                    if (reward.type == RewardType.Card)
                        resolvedCardRewards.Add((reward, eventCatalog.ResolveCard(reward.cardKey)));

            HexEventRegistry.Set(hex, definition, guardArmyName, resolvedGuardMembers, _neutralPlayer, resolvedCardRewards);
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
