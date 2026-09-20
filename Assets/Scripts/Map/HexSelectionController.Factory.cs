using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Styles;
using Game.Terrain;
using Game.Turns;
using Game.UI;
using Game.Units;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Game.Map
{
    // Gameplay spawning/building half of HexSelectionController. The public extraction
    // action is shared by the human resource buttons and InfrastructureActions for AI.
    public partial class HexSelectionController
    {
        public UnitData SpawnUnit(string unitName, PlayerSetupData owner, int moveMax, int activationApCost, bool isHero, int commandRating, Sprite art, IEnumerable<string> grantedAbilities = null, int attack = 0, int range = 1, int hitPoints = 1, int initiative = 1, int fate = 0, int defense = 1, int resistance = 1, IEnumerable<UnitTypeTag> typeTags = null, Sprite detailArt = null, int apCost = 0, ResourceCost resourceCost = null, bool isAviation = false, int launchEnergyCost = 0, int turnsWithoutRefuel = 0, int antiAirRadius = 1, CardDefinition sourceDefinition = null)
        {
            if (owner == null)
                return null;

            var data = new UnitData
            {
                Name = unitName, Owner = owner,
                MoveMax = moveMax, MoveCurrent = moveMax,
                ActivationApCost = activationApCost,
                IsHero = isHero, CommandRating = commandRating,
                Art = art, DetailArt = detailArt != null ? detailArt : art,
                Attack = attack, Defense = defense, Resistance = resistance, Range = range, HitPointsMax = hitPoints, HitPointsCurrent = hitPoints,
                Initiative = initiative, Fate = fate, FateMax = fate,
                ApCost = apCost, OriginalResourceCost = resourceCost,
                IsAviation = isAviation,
                LaunchEnergyCost = launchEnergyCost,
                TurnsWithoutRefuel = Mathf.Max(0, turnsWithoutRefuel),
                AntiAirRadius = Mathf.Max(1, antiAirRadius),
                OriginatingCard = sourceDefinition,
            };
            if (grantedAbilities != null)
                foreach (string ability in grantedAbilities)
                    data.Abilities.Add(ability);
            if (typeTags != null)
                foreach (UnitTypeTag tag in typeTags)
                    data.TypeTags.Add(tag);
            if (data.Abilities.Contains(UnitAbilities.RapidReaction))
                data.ActivationApCost = 0;
            UnitRepair.InitializeRepairCost(data);
            return data;
        }

        public ArmyController CreateArmyMarker(ArmyData army)
        {
            if (map == null || army == null || army.Owner == null)
                return null;
            FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(army.Owner.Faction)
                : null;
            if (ownerCatalog == null || ownerCatalog.armyPrefab == null)
                return null;

            MapObjectVisual prefab = AviationRules.IsAirArmy(army) && ownerCatalog.airArmyPrefab != null
                ? ownerCatalog.airArmyPrefab
                : ownerCatalog.armyPrefab;
            MapObjectVisual marker = Instantiate(prefab);
            ArmyController controller = marker.gameObject.AddComponent<ArmyController>();
            controller.SetData(army);
            army.Controller = controller;
            PlayerRoot root = PlayerRootRegistry.FindFor(army.Owner);
            if (root != null)
                marker.transform.SetParent(root.transform, worldPositionStays: true);
            marker.transform.position = map.HexToWorld(army.Hex);
            marker.SetColor(PlayerColorPalette.Colors[army.Owner.ColorIndex]);
            marker.SetSortingOrder(MapSortingOrder.ArmyCircle, MapSortingOrder.ArmyIcon);
            marker.SetVisible(false);
            RestackArmiesOn(army.Hex, null);
            return controller;
        }

        public void RefreshArmyAirLook(ArmyData army)
        {
            if (army?.Controller?.Visual == null || army.Owner == null)
                return;
            FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(army.Owner.Faction)
                : null;
            if (ownerCatalog?.airArmyPrefab != null)
                army.Controller.Visual.ApplyPrefabAppearance(ownerCatalog.airArmyPrefab);
        }

        public void DeleteArmyIfEmptied(ArmyData army)
        {
            if (army == null || army.Members.Count > 0)
                return;
            // Going empty is itself a content change any watcher needs, whether or not the shell
            // below survives — the Barracks/Airfield branch keeps the ArmyData/marker alive as a
            // persistent empty container, which used to mean callers relying solely on this method
            // (e.g. AviationActions.ReturnAircraftToDeck) never told anyone the hex just lost its
            // whole roster. ArmyRegistry.Unregister below already publishes on the normal path —
            // this makes the guarded "kept as a shell" path do the same instead of silently
            // skipping it.
            VisionSystem.NotifyContentChanged(army.Hex);
            BuildingData building = BuildingRegistry.FindAt(army.Hex);
            if (building != null && building.Owner == army.Owner
                && (building.HasAbility(UnitAbilities.Barracks)
                    || (army.IsAirfield && AviationRules.IsAirfieldBuilding(building, army.Owner))))
                return;
            ArmyRegistry.Unregister(army);
            if (_selectedArmy == army.Controller)
                SetSelectedArmy(null);
            if (army.Controller != null)
            {
                Destroy(army.Controller.gameObject);
                army.Controller = null;
            }
        }

        public BuildingData SpawnBuilding(CardDefinition definition, HexCoord hex, PlayerSetupData owner)
        {
            if (map == null || owner == null || definition == null)
                return null;
            FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(owner.Faction)
                : null;
            if (ownerCatalog == null || ownerCatalog.basePrefab == null)
                return null;

            var building = new BuildingData
            {
                Name = definition.displayName, Hex = hex, Owner = owner,
                Visual = CreateBuildingMarker(hex, owner, ownerCatalog.basePrefab, ownerCatalog.citadelIcon),
                Art = definition.art,
                DetailArt = definition.detailArt != null ? definition.detailArt : definition.art,
                Level = 1,
                StructurePointsMax = definition.hitPoints,
                StructurePointsCurrent = definition.hitPoints,
                Defense = definition.defenseRating,
                Resistance = definition.resistanceRating,
                Fate = definition.fate,
                AirfieldCapacity = Mathf.Max(0, definition.airfieldCapacity),
            };
            building.IsBase = true;
            foreach (string ability in definition.grantedAbilities)
                building.Abilities.Add(ability);
            BuildingRegistry.Register(hex, building);
            BuildingRegistry.EnsureGarrisonForBuilding(building, this);
            if (building.AirfieldCapacity > 0)
                AviationActions.EnsureAirfield(this, owner, hex);
            RestackArmiesOn(hex, null);
            StealthSystem.RunChecksForNewVisionSource(building);
            return building;
        }

        private MapObjectVisual CreateBuildingMarker(HexCoord hex, PlayerSetupData owner, MapObjectVisual prefab, Sprite icon = null)
        {
            MapObjectVisual marker = Instantiate(prefab);
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root != null)
                marker.transform.SetParent(root.transform, worldPositionStays: true);
            marker.transform.position = map.HexToWorld(hex);
            marker.SetColor(PlayerColorPalette.Colors[owner.ColorIndex]);
            if (icon != null)
                marker.SetIcon(icon);
            marker.SetSortingOrder(MapSortingOrder.BuildingCircle, MapSortingOrder.BuildingIcon);
            return marker;
        }

        public static bool HasOwnHeroArmyAt(HexCoord hex, PlayerSetupData player)
        {
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army.Owner == player && army.Members.Exists(m => m.IsHero))
                    return true;
            return false;
        }

        private void NotifyBuildBlocked(PlayerSetupData owner, string message)
        {
            if (owner != null && owner.IsHuman)
                turnController?.ShowSpawnHint(message);
            else
                AiDebugLog.Write($"[AI] {owner?.Nickname ?? "Neutral"}: extraction build rejected — {message}");
        }

        public bool TryBuildExtractionFacility(CardDefinition definition, HexCoord hex, PlayerSetupData owner)
        {
            if (definition == null || owner == null || gameConfig == null || turnController == null)
                return false;
            if (!HasOwnHeroArmyAt(hex, owner))
            {
                NotifyBuildBlocked(owner, $"Needs one of your armies with a Hero on this hex to build {definition.displayName}.");
                return false;
            }

            BuildingData building = BuildingRegistry.FindAt(hex);
            bool isNewSite = building == null;
            if (isNewSite)
            {
                building = new BuildingData(totalFacilitySlots: 4)
                {
                    Name = "Resource Site", Hex = hex, Owner = owner,
                    StructurePointsMax = gameConfig.resourceSiteStructurePoints,
                    StructurePointsCurrent = gameConfig.resourceSiteStructurePoints,
                    Defense = gameConfig.resourceSiteDefense,
                    Resistance = gameConfig.resourceSiteResistance,
                    Fate = gameConfig.resourceSiteFate,
                    HasTieredUnlock = false,
                };
            }
            else if (building.Owner != owner)
            {
                return false;
            }

            string ability = definition.grantedAbilities?.Find(
                a => System.Array.IndexOf(UnitAbilities.CollectAbilities, a) >= 0);
            int resourceIndex = System.Array.IndexOf(UnitAbilities.CollectAbilities, ability);
            if (resourceIndex < 0)
                return false;
            if (building.HasFacilityWithAbility(ability))
            {
                NotifyBuildBlocked(owner, $"{building.Name} already has a {definition.displayName}.");
                return false;
            }

            ResourceType resourceType = (ResourceType)resourceIndex;
            if (map == null || !map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain))
                return false;
            int effectiveYield = HexResourceCalculator.GetEffectiveYield(
                terrain, HexResourceBonusRegistry.GetBonus(hex)).Get(resourceType);
            int ownerArmyCollectors = ArmyRegistry.AllAt(hex)
                .Where(army => army != null && army.Owner == owner && army.Members != null)
                .Sum(army => army.Members.Count(member => member != null
                    && member.HasAbility(ability)));
            bool armiesCanCollect = BattleInitiator.FindEnemyAt(hex, owner) == null;
            int marginalGain = IncomeProjection.MarginalOwnerCollectionAtHex(
                effectiveYield, building.CollectedAmount(resourceType), 1,
                ownerArmyCollectors, armiesCanCollect);
            if (marginalGain <= 0)
            {
                NotifyBuildBlocked(owner,
                    $"{definition.displayName} would not increase {resourceType} income on this hex.");
                return false;
            }

            int slotIndex = building.FindFirstAvailableFacilitySlot();
            if (slotIndex < 0)
            {
                NotifyBuildBlocked(owner, $"{building.Name} has no free Facility slot for {definition.displayName}.");
                return false;
            }
            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null)
                return false;
            if (!root.CanSpendActionPoints(definition.apCost))
            {
                NotifyBuildBlocked(owner, $"Not enough action points to build {definition.displayName}.");
                return false;
            }
            if (!definition.resourceCost.CanAfford(root))
            {
                NotifyBuildBlocked(owner, $"Not enough resources to build {definition.displayName}.");
                return false;
            }

            ArmyData actingHeroArmy = ArmyRegistry.AllAt(hex)
                .Where(army => army != null && army.Owner == owner
                    && army.Members.Exists(member => member != null && member.IsHero))
                .OrderBy(army => army.CurrentMovement)
                .ThenBy(army => army.Id)
                .FirstOrDefault();
            FacilityData facility = FacilityData.FromDefinition(definition);

            root.SpendActionPoints(definition.apCost);
            definition.resourceCost.PayFrom(root);
            building.FacilitySlots[slotIndex] = facility;
            if (isNewSite)
            {
                FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                    ? cardHandUI.StartingDeckCatalog.GetCatalog(owner.Faction)
                    : null;
                building.Visual = CreateBuildingMarker(hex, owner, gameConfig.facilityMarkerPrefab, ownerCatalog?.facilityIcon);
                // The facility is already in the slot when Register publishes this site.
                BuildingRegistry.Register(hex, building);
                RestackArmiesOn(hex, null);
            }
            if (_selectedHex.HasValue && _selectedHex.Value.Equals(hex))
                SelectHex(hex, preserveSelection: true);
            StealthSystem.RunChecksForNewVisionSource(building, facility);
            ApplyExtractionBuilderConsequences(actingHeroArmy);
            // A newly registered site was already published with its facility in place.
            // Adding to an existing building changes its slots/income without registration.
            if (!isNewSite)
                VisionSystem.NotifyContentChanged(hex);
            return true;
        }

        internal static void ApplyExtractionBuilderConsequences(ArmyData actingHeroArmy)
        {
            if (actingHeroArmy == null)
                return;
            foreach (UnitData member in actingHeroArmy.Members)
                if (member != null)
                    member.MoveCurrent = 0;
            foreach (UnitData member in actingHeroArmy.Members)
                if (member != null && member.IsHero)
                    StealthSystem.ExitStealth(member);
        }
    }
}