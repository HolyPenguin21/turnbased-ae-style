using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Setup
{
    public partial class CitadelSetupController
    {
        private PlayerSetupData _neutralPlayer;

        private List<HexCoord> FinalCitadels() => _allPlayers
            .Where(p => p.CitadelHexQ.HasValue && p.CitadelHexR.HasValue)
            .Select(p => new HexCoord(p.CitadelHexQ.Value, p.CitadelHexR.Value)).ToList();

        private bool HasNeutralArmy(HexCoord hex) => ArmyRegistry.AllAt(hex).Any(a => a.Owner == _neutralPlayer);
        private static bool HasResources(HexCoord hex) => HexResourceBonusRegistry.GetBonus(hex) != null;

        // All budgets and typed assignments are resolved before the single registry pass.
        private void GenerateResources()
        {
            if (map == null || gameConfig == null) return;
            var citadels = FinalCitadels();
            if (citadels.Count == 0) return;
            var budget = MapGenerationSettings.ResourceBudget(citadels.Count, map.FieldRadius, GameSession.SelectedResourceConditions);
            var layout = MatchResourcePlacement.Plan(map, citadels, budget, UnityMapGenerationRandom.Shared, HasResources);
            var display = map.GetComponent<MapResourceDisplay>();
            foreach (var point in layout.Near.SelectMany(n => n).Concat(layout.Far.Points))
            {
                HexResourceBonusRegistry.Set(point.Hex, point.Yields);
                display?.RefreshHex(point.Hex);
            }
            for (int player = 0; player < citadels.Count; player++)
                if (layout.Near[player].Count < 3)
                    Debug.LogWarning($"Resource generation: placed {layout.Near[player].Count}/3 near resources for {citadels[player]}.");
            int tech = layout.Far.Points.Count(p => p.Tech);
            if (layout.Far.Points.Count < budget.FarTarget || tech < budget.TechTarget)
                Debug.LogWarning($"Resource generation: placed {layout.Far.Points.Count}/{budget.FarTarget} far hexes and {tech}/{budget.TechTarget} Tech; spacing/reachability limits.");
            if (layout.Far.Points.Count < 3)
                Debug.LogWarning("Resource generation: fewer than three far hexes; all ordinary types cannot be represented.");
        }

        private void GenerateNeutralArmies()
        {
            if (map == null || gameConfig == null || neutralArmyCatalog == null || hexSelectionController == null || _neutralPlayer == null)
                return;
            var plan = MatchMapContentPlanner.PlanNeutralArmies(map, FinalCitadels(), GetCityRuinsHexes(),
                neutralArmyCatalog, UnityMapGenerationRandom.Shared, out int target);
            foreach (var entry in plan) SpawnNeutralArmy(entry.Hex, entry.Army);
            if (plan.Count < target) Debug.LogWarning($"Neutral generation: placed {plan.Count}/{target}; no complete spaced layout found.");
        }

        private void GenerateCityRuinsGarrisons()
        {
            if (map == null || neutralArmyCatalog == null || hexSelectionController == null || _neutralPlayer == null)
                return;
            foreach (var entry in MatchMapContentPlanner.PlanRuinsGarrisons(map, FinalCitadels(), neutralArmyCatalog,
                HasNeutralArmy, UnityMapGenerationRandom.Shared))
                SpawnNeutralArmy(entry.Hex, entry.Army);
        }

        private void GenerateRandomEvents()
        {
            if (map == null || gameConfig == null) return;
            var plan = MatchMapContentPlanner.PlanEvents(map, FinalCitadels(), GetCityRuinsHexes(), eventCatalog,
                neutralArmyCatalog, HasNeutralArmy, HasResources, UnityMapGenerationRandom.Shared);
            foreach (var entry in plan)
                HexEventRegistry.Set(entry.Hex, entry.Definition, entry.GuardName, entry.GuardMembers, _neutralPlayer,
                    entry.CardRewards, entry.Rewards);
        }

        // Returns the ArmyData it just built (or null if every entry failed to resolve, in which
        // case nothing was actually placed). Only ever called for a real, on-the-map army any
        // more (GenerateNeutralArmies) — a Hex Event's own guard is no longer spawned here at
        // all (see PlaceEvent's own comment on why), so unlike before this always gets a marker.
        private ArmyData SpawnNeutralArmy(HexCoord hex, ArmyDefinition definition)
        {
            if (!map.CanEnter(hex)) return null;
            var army = new ArmyData { Name = definition.name, Hex = hex, Owner = _neutralPlayer };

            foreach (var (card, count) in MatchMapContentPlanner.ResolveArmyMembers(neutralArmyCatalog, definition))
                for (int i = 0; i < count; i++)
                {
                    UnitData spawned = SpawnNeutralUnit(card, isHero: card.cardType == CardType.Hero);
                    if (spawned != null) army.AddMemberSorted(spawned);
                }

            if (army.Members.Count == 0)
                return null; // every entry failed to resolve — nothing to actually show on this hex

            // A campaign's free leader can already see this hex. Publish the complete roster
            // through Register's visibility/content notification, never an empty placeholder.
            ArmyRegistry.Register(army);
            army.RefreshTransport();

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
                definition.defenseRating, definition.unitTypeTags, definition.detailArt,
                definition.apCost, definition.resourceCost, catalogDefinition: definition);
        }

    }
}
