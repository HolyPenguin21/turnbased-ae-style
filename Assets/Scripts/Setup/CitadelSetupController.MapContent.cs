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
        // call, "Recommended" option), otherwise anywhere. Composition is fully random per
        // army — weak to strong, hero or not, drawn from the only card catalog that exists yet
        // (Iron Concord's own; the user's own note: dedicated neutral cards come later) — the
        // one rule that does hold is no hero repeats across neutral armies.
        private void GenerateNeutralArmies()
        {
            if (map == null || gameConfig == null || catalog == null || hexSelectionController == null || _neutralPlayer == null)
                return;

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

            List<HexCoord> candidates = map.AllCoords.Where(h => !excluded.Contains(h)).ToList();
            if (candidates.Count == 0)
                return;

            int hexCount = gameConfig.mapGeneration.width * gameConfig.mapGeneration.height;
            int min = Mathf.Max(1, CalibratedCount(3, 12, hexCount));
            int max = Mathf.Max(min, CalibratedCount(5, 15, hexCount));
            int target = Mathf.Clamp(Random.Range(min, max + 1), 0, candidates.Count);

            List<HexCoord> chosen = PickRandomDistinct(candidates, target);

            List<CardDefinition> allHeroes = catalog.ForType(CardType.Hero).ToList();
            List<CardDefinition> allUnits = catalog.ForType(CardType.Unit).ToList();
            var usedHeroNames = new HashSet<string>();
            var usedArmyNames = new List<string>();

            foreach (HexCoord hex in chosen)
                SpawnNeutralArmy(hex, allHeroes, allUnits, usedHeroNames, usedArmyNames);
        }

        private void SpawnNeutralArmy(HexCoord hex, List<CardDefinition> allHeroes, List<CardDefinition> allUnits,
            HashSet<string> usedHeroNames, List<string> usedArmyNames)
        {
            List<CardDefinition> availableHeroes = allHeroes.Where(c => !usedHeroNames.Contains(c.displayName)).ToList();
            CardDefinition hero = availableHeroes.Count > 0 && Random.value < 0.5f
                ? availableHeroes[Random.Range(0, availableHeroes.Count)]
                : null;
            if (hero != null)
                usedHeroNames.Add(hero.displayName);

            // Same capacity rule as ArmyData.ComputeCapacity: no hero -> 2 (hard cap for a
            // named, non-garrison army), a hero present -> its own CommandRating, one of those
            // slots being the hero itself.
            int capacity = hero != null ? Mathf.Max(1, hero.commandRating) : 2;
            int nonHeroSlots = hero != null ? Mathf.Max(0, capacity - 1) : capacity;
            // A hero-only army (0 rolled here) is a valid, already-supported case — see
            // BattleScreenUI.BeginCaptureKillEncounter. A no-hero army must have at least 1
            // member; there's no such thing as a completely empty "army" to place.
            int unitCount = hero != null ? Random.Range(0, nonHeroSlots + 1) : Random.Range(1, capacity + 1);
            if (allUnits.Count == 0)
                unitCount = 0;
            if (hero == null && unitCount == 0)
                return; // nothing left to actually spawn on this hex — skip it entirely

            string name = catalog.GetRandomArmyName(usedArmyNames);
            usedArmyNames.Add(name);

            var army = new ArmyData { Name = name, Hex = hex, Owner = _neutralPlayer };
            ArmyRegistry.Register(army);

            if (hero != null)
            {
                UnitData spawnedHero = SpawnNeutralUnit(hero, isHero: true);
                if (spawnedHero != null)
                    army.AddMemberSorted(spawnedHero);
            }
            for (int i = 0; i < unitCount; i++)
            {
                CardDefinition unitCard = allUnits[Random.Range(0, allUnits.Count)];
                UnitData spawned = SpawnNeutralUnit(unitCard, isHero: false);
                if (spawned != null)
                    army.AddMemberSorted(spawned);
            }

            // Only now, once every member's already in — CreateArmyMarker's very first
            // RestackArmiesOn needs a non-empty army to have anything to show (see
            // HexSelectionController.NonEmptyArmiesAt), same ordering SpawnTestArmy uses.
            hexSelectionController.CreateArmyMarker(army);
        }

        private UnitData SpawnNeutralUnit(CardDefinition definition, bool isHero)
        {
            return hexSelectionController.SpawnUnit(definition.displayName, _neutralPlayer, definition.moveMax,
                definition.activationApCost, isHero, definition.commandRating, definition.art, definition.grantedAbilities,
                definition.attack, definition.range, definition.hitPoints, definition.initiative, definition.fate,
                definition.defenseRating, definition.resistanceRating, definition.unitTypeTags);
        }

        // Placeholder pass — no random events exist in this project yet. Kept as its own no-op
        // call (rather than omitted entirely) so the four-pass pipeline's ordering is already
        // right, and future event-generation logic has an obvious slot to land in.
        private void GenerateRandomEvents()
        {
        }

        // Placeholder pass — no special hexes exist in this project yet. Same idea as
        // GenerateRandomEvents.
        private void GenerateSpecialHexes()
        {
        }

        // Shared by GenerateResources/GenerateNeutralArmies — up to `count` distinct hexes
        // picked without replacement from `pool` (fewer than `count` if the pool runs out).
        private static List<HexCoord> PickRandomDistinct(List<HexCoord> pool, int count)
        {
            var working = new List<HexCoord>(pool);
            var result = new List<HexCoord>(Mathf.Min(count, working.Count));
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
