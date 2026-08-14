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

        // Resources: 12-18 hexes on a 12x9 map, 40-50 on a 16x13 one (see CalibratedCount) —
        // roughly equal counts of each of the 4 resource types, citadel hexes left untouched
        // (they already carry their own fixed bonus, see FinalizePlayer/gameConfig.
        // citadelResourceBonus — the user's own call: leave that hex exactly as it already
        // works). A hex getting picked here doesn't exclude it from GenerateNeutralArmies —
        // sharing is fine, per the user's own call ("the army guards the resource").
        private void GenerateResources()
        {
            if (map == null || gameConfig == null)
                return;

            List<HexCoord> candidates = map.AllCoords
                .Where(h => HexResourceBonusRegistry.GetBonus(h) == null)
                .ToList();
            if (candidates.Count == 0)
                return;

            int hexCount = gameConfig.mapGeneration.width * gameConfig.mapGeneration.height;
            int min = Mathf.Max(4, CalibratedCount(12, 40, hexCount));
            int max = Mathf.Max(min, CalibratedCount(18, 50, hexCount));
            int target = Mathf.Clamp(Random.Range(min, max + 1), 0, candidates.Count);

            List<HexCoord> chosen = PickRandomDistinct(candidates, target);
            ResourceType[] types = BuildRoundRobinTypes(chosen.Count);

            // RefreshAll already ran once, back when HexMapGenerator first built the map (all
            // zero yield, since baseline was reset to 0 — see the user's own change) — every hex
            // this pass touches needs the same per-hex redraw CitadelSetupController.
            // FinalizePlayer already does for its own citadel bonus, or the icon never appears.
            MapResourceDisplay resourceDisplay = map.GetComponent<MapResourceDisplay>();

            for (int i = 0; i < chosen.Count; i++)
            {
                // Very rarely 2 instead of 1 — a flat 5% roll per hex, per the user's own call.
                int amount = Random.value < 0.05f ? 2 : 1;
                var yields = new ResourceYields();
                switch (types[i])
                {
                    case ResourceType.Human: yields.human = amount; break;
                    case ResourceType.Energy: yields.energy = amount; break;
                    case ResourceType.Materials: yields.materials = amount; break;
                    case ResourceType.Tech: yields.tech = amount; break;
                }
                HexResourceBonusRegistry.Set(chosen[i], yields);
                resourceDisplay?.RefreshHex(chosen[i]);
            }
        }

        // Neutral armies: 3-5 on a 12x9 map, 12-15 on a 16x13 one (see CalibratedCount) — never
        // on a player's own citadel hex or one of its immediate neighbours (the user's own
        // call, "Recommended" option), otherwise anywhere. Composition is no longer rolled —
        // each placed army is one whole, hand-authored ArmyDefinition from neutralArmyCatalog.
        // armies, one army per hex (the user's own call), so at most armies.Count hexes ever
        // get one.
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

            List<HexCoord> chosenHexes = PickRandomDistinct(candidates, target);
            List<ArmyDefinition> chosenArmies = PickRandomDistinct(neutralArmyCatalog.armies, target);

            for (int i = 0; i < chosenHexes.Count; i++)
                SpawnNeutralArmy(chosenHexes[i], chosenArmies[i]);
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
                definition.defenseRating, definition.resistanceRating, definition.unitTypeTags, definition.detailArt);
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

        // One of each of the 4 resource types repeated to fill `count` slots, shuffled — keeps
        // the actual per-type split exactly balanced (off by at most 1) while still landing on
        // a random hex, matching "roughly equal, can vary a little" without needing true
        // randomness in the split itself.
        private static ResourceType[] BuildRoundRobinTypes(int count)
        {
            ResourceType[] all = { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };
            var pool = new List<ResourceType>(count);
            for (int i = 0; i < count; i++)
                pool.Add(all[i % all.Length]);

            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            return pool.ToArray();
        }
    }
}
