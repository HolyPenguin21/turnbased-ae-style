using System.Collections.Generic;
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
    // Entity-creation half of HexSelectionController — building/spawning UnitData/ArmyData/
    // BuildingData and their map markers, plus the hero-built extraction Facility action. Split
    // out of the main file (click/selection orchestration) and HexSelectionController.Movement.cs
    // (hover preview/path/move orders) purely for file size — all three share the same fields and
    // private helpers (ResolveArmyOffset, RestackArmiesOn, etc.), which stay in the main file
    // since every part uses them, not just this one.
    public partial class HexSelectionController
    {
        // Builds a brand-new unit's data — used by CardHandUI when a Unit/Hero card is played
        // onto the map, and by CitadelSetupController's test-army spawning. Pure data only: a
        // unit has no map presence of its own at all (only its ArmyData does — see
        // ArmyData.Controller/CreateArmyMarker). The caller is responsible for adding the
        // returned UnitData to whichever ArmyData it belongs to (ArmyData.AddMemberSorted) and,
        // if that changes the army's own visibility (e.g. its first member ever), refreshing the
        // hex with RestackArmiesOn.
        public UnitData SpawnUnit(string unitName, PlayerSetupData owner, int moveMax, int activationApCost, bool isHero, int commandRating, Sprite art, IEnumerable<string> grantedAbilities = null, int attack = 0, int range = 1, int hitPoints = 1, int initiative = 1, int fate = 0, int defense = 1, int resistance = 1, IEnumerable<UnitTypeTag> typeTags = null)
        {
            if (owner == null)
                return null;

            var data = new UnitData
            {
                Name = unitName, Owner = owner,
                MoveMax = moveMax, MoveCurrent = moveMax,
                ActivationApCost = activationApCost,
                IsHero = isHero, CommandRating = commandRating,
                Art = art,
                Attack = attack, Defense = defense, Resistance = resistance, Range = range, HitPointsMax = hitPoints, HitPointsCurrent = hitPoints,
                Initiative = initiative, Fate = fate, FateMax = fate,
            };
            if (grantedAbilities != null)
                foreach (string ability in grantedAbilities)
                    data.Abilities.Add(ability);
            if (typeTags != null)
                foreach (UnitTypeTag tag in typeTags)
                    data.TypeTags.Add(tag);
            // UnitAbilities.RapidReaction: "costs no AP to move when in an army" — overrides
            // whatever activationApCost the card itself declared (see ArmyData.
            // ActivationApCost, which sums each member's own cost).
            if (data.Abilities.Contains(UnitAbilities.RapidReaction))
                data.ActivationApCost = 0;
            return data;
        }

        // The one and only place an army's map marker gets created — called once, right after
        // ArmyRegistry.Register, by every ArmyData creation site (CitadelSetupController.
        // CreateGarrison, SpawnBuilding's own garrison creation below, ArmyViewerModalUI's
        // Create Army). A freshly created army starts with zero members and its marker starts
        // invisible (see RestackArmiesOn, which never shows an empty army) — it becomes visible
        // the moment its first member is added and RestackArmiesOn is re-run.
        public ArmyController CreateArmyMarker(ArmyData army)
        {
            if (gameConfig == null || gameConfig.armyMarkerPrefab == null || map == null || army == null || army.Owner == null)
                return null;

            MapObjectVisual marker = Instantiate(gameConfig.armyMarkerPrefab);
            ArmyController controller = marker.gameObject.AddComponent<ArmyController>();
            controller.SetData(army);
            army.Controller = controller;

            PlayerRoot root = PlayerRootRegistry.FindFor(army.Owner);
            if (root != null)
                marker.transform.SetParent(root.transform, worldPositionStays: true);

            marker.transform.position = map.HexToWorld(army.Hex);
            marker.SetColor(PlayerColorPalette.Colors[army.Owner.ColorIndex]);
            marker.SetSortingOrder(gameConfig.armyCircleSortingOrder, gameConfig.armyIconSortingOrder);
            marker.SetVisible(false); // RestackArmiesOn below decides if it should actually show

            RestackArmiesOn(army.Hex, null);
            return controller;
        }

        // Called explicitly wherever a member is actually REMOVED from a named army (see
        // ArmyViewerModalUI.Hide, deferred until the modal actually closes) — a named army
        // that's just been left with nobody in it has served its purpose and is gone for good:
        // unregistered and its marker destroyed. Deliberately NOT folded into RestackArmiesOn's
        // own membership-change handling — that runs for every membership change on a hex,
        // including a brand-new army that was just created and hasn't received its first member
        // yet (see CreateArmyMarker), which must stay merely invisible, not be torn down for it.
        // Any army — garrison or named — sitting empty on its own owner's Barracks hex is left
        // alone indefinitely instead: a Barracks hex is a safe place to stage/empty an army, not
        // just the specific garrison building's own permanent landing pad.
        public void DeleteArmyIfEmptied(ArmyData army)
        {
            if (army == null || army.Members.Count > 0)
                return;

            BuildingData building = BuildingRegistry.FindAt(army.Hex);
            if (building != null && building.Owner == army.Owner && building.HasAbility(BuildingAbilities.Barracks))
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

        // Spawns a brand-new Base building at `hex` for `owner` — used by CardHandUI when a
        // CardType.Base card is played onto an empty hex (see CardHandUI.TryPlayCard). Uses the
        // same buildingMarkerPrefab/citadelIconSprite the auto-placed citadel already uses — no
        // visual distinction yet between "the citadel" and a player-built Base. Position/offset
        // resolution is left entirely to the RestackArmiesOn call at the end, same as
        // CitadelSetupController relies on its own one-off HexObjectLayout call before either
        // the registry or RestackArmiesOn existed.
        public BuildingData SpawnBuilding(CardDefinition definition, HexCoord hex, PlayerSetupData owner)
        {
            if (gameConfig == null || gameConfig.buildingMarkerPrefab == null || map == null || owner == null || definition == null)
                return null;

            var building = new BuildingData
            {
                Name = definition.displayName, Hex = hex, Owner = owner,
                Visual = CreateBuildingMarker(hex, owner, gameConfig.buildingMarkerPrefab, gameConfig.citadelIconSprite),
                Art = definition.art,
                Level = 1,
                StructurePointsMax = definition.structurePointsMax,
                StructurePointsCurrent = definition.structurePointsMax,
                Defense = definition.defense,
                Resistance = definition.resistance,
                Fate = definition.fate,
                ResourceYield = definition.resourceYield,
            };
            building.Abilities.Add(BuildingAbilities.Base);
            foreach (string ability in definition.grantedAbilities)
                building.Abilities.Add(ability);
            BuildingRegistry.Register(hex, building);

            // Same rule as the auto-placed citadel (see CitadelSetupController.CreateGarrison):
            // a Barracks-tagged building needs its own garrison to receive Unit/Hero cards
            // deployed from hand — not every Base card grants Barracks, so this only fires when
            // the card's own grantedAbilities actually include it.
            if (building.HasAbility(BuildingAbilities.Barracks))
            {
                var garrison = new ArmyData { Name = "Garrison", Hex = hex, Owner = owner, IsGarrison = true };
                ArmyRegistry.Register(garrison);
                CreateArmyMarker(garrison);
            }

            // A "Concord Citadel" card played from hand is otherwise identical to the starting
            // citadel (see BuildingData.IsStartingCitadel) — including the permanent hex bonus
            // (see CitadelSetupController.SpawnCitadelMarker/HexResourceBonusRegistry), so it
            // gets stamped here too whenever the card's own abilities add up to a full citadel
            // (see BuildingAbilities.IsFullCitadel).
            if (BuildingAbilities.IsFullCitadel(building))
                HexResourceBonusRegistry.Set(hex, gameConfig.citadelResourceBonus);

            // Now that BuildingRegistry actually has this building, re-resolve the layout for
            // the whole hex — positions the new marker correctly (and re-centres any armies
            // already sharing the hex, now that it has a building).
            RestackArmiesOn(hex, null);
            return building;
        }

        // Shared marker-instantiate-and-position logic for anything BuildingRegistry ends up
        // holding — SpawnBuilding (a dragged CardType.Base card) and TryBuildExtractionFacility
        // (a hero-built resource site) each use their own prefab (buildingMarkerPrefab vs.
        // facilityMarkerPrefab — distinct visuals, not just a different icon on the same one).
        // `icon` is optional: facilityMarkerPrefab bakes its own icon directly onto its
        // Object_Image sprite, so passing null there leaves that alone instead of blanking it.
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
            marker.SetSortingOrder(gameConfig.buildingCircleSortingOrder, gameConfig.buildingIconSortingOrder);
            return marker;
        }

        // Shared by CardHandUI (founding a Base) and this controller's own resource-action
        // buttons (founding/adding to a resource site) — a hero must be physically standing on
        // the hex for either action.
        public static bool HasOwnHeroArmyAt(HexCoord hex, PlayerSetupData player)
        {
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army.Owner == player && army.Members.Exists(m => m.IsHero))
                    return true;
            return false;
        }

        // The hero action behind each of HexInfoPanelUI's up-to-4 resource buttons (see
        // RefreshResourceActionRow) — builds `definition` (one of GameConfig.
        // extractionFacilityCards) directly into whatever building already sits on `hex`,
        // creating a brand-new minimal "resource site" building first if it's still bare. Never
        // touches CardHandUI/a hand slot — these cards are never drawn or held.
        public bool TryBuildExtractionFacility(CardDefinition definition, HexCoord hex, PlayerSetupData owner)
        {
            if (definition == null || owner == null || gameConfig == null || turnController == null)
                return false;
            if (!HasOwnHeroArmyAt(hex, owner))
            {
                turnController.ShowSpawnHint($"Needs one of your armies with a Hero on this hex to build {definition.displayName}.");
                return false;
            }

            BuildingData building = BuildingRegistry.FindAt(hex);
            bool isNewSite = building == null;
            if (isNewSite)
            {
                // A generic identity, not the triggering card's own name/art — cell 0 in the
                // shared modal shows this alongside the actual placed Facility (see
                // BaseSlotCardUI), so borrowing e.g. "Materials Extractor" for the SITE itself
                // reads as two identical entries once a Materials Extractor is also placed in a
                // slot. No Visual/registration yet — deferred until every affordability check
                // below passes, so a failed build never leaves an orphaned marker on the hex.
                building = new BuildingData(totalFacilitySlots: 4)
                {
                    Name = "Resource Site", Hex = hex, Owner = owner,
                    StructurePointsMax = gameConfig.startingStructurePoints,
                    StructurePointsCurrent = gameConfig.startingStructurePoints,
                    Defense = gameConfig.startingDefense,
                    Resistance = gameConfig.startingResistance,
                    Fate = gameConfig.startingFate,
                    HasTieredUnlock = false,
                };
            }
            else if (building.Owner != owner)
            {
                return false; // not this player's building — no hint, same as any other irrelevant target
            }

            string ability = definition.grantedAbilities.Find(a => System.Array.IndexOf(BuildingAbilities.CollectAbilities, a) >= 0);
            if (ability != null && building.HasFacilityWithAbility(ability))
            {
                turnController.ShowSpawnHint($"{building.Name} already has a {definition.displayName}.");
                return false;
            }

            int slotIndex = building.FindFirstAvailableFacilitySlot();
            if (slotIndex < 0)
            {
                turnController.ShowSpawnHint($"{building.Name} has no free Facility slot for {definition.displayName}.");
                return false;
            }

            PlayerRoot root = PlayerRootRegistry.FindFor(owner);
            if (root == null)
                return false;
            if (!root.CanSpendActionPoints(definition.apCost))
            {
                turnController.ShowSpawnHint($"Not enough action points to build {definition.displayName}.");
                return false;
            }
            if (!definition.resourceCost.CanAfford(root))
            {
                turnController.ShowSpawnHint($"Not enough resources to build {definition.displayName}.");
                return false;
            }

            root.SpendActionPoints(definition.apCost);
            definition.resourceCost.PayFrom(root);
            building.FacilitySlots[slotIndex] = FacilityData.FromDefinition(definition);

            // Building a facility is that hero's whole action for the turn — it costs whatever
            // move points its army had left, same as spending a full move order rather than just
            // the AP cost above. With 2+ of the owner's own hero-armies stacked on this hex
            // (nothing here picks which one actually acted), the one already lowest on move
            // points is the one charged — it has the least left to lose either way.
            ArmyData actingHeroArmy = null;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner != owner || !army.Members.Exists(m => m.IsHero))
                    continue;
                if (actingHeroArmy == null || army.CurrentMovement < actingHeroArmy.CurrentMovement)
                    actingHeroArmy = army;
            }
            if (actingHeroArmy != null)
                foreach (UnitData member in actingHeroArmy.Members)
                    member.MoveCurrent = 0;

            if (isNewSite)
            {
                building.Visual = CreateBuildingMarker(hex, owner, gameConfig.facilityMarkerPrefab);
                BuildingRegistry.Register(hex, building);
                RestackArmiesOn(hex, null);
            }

            if (_selectedHex.HasValue && _selectedHex.Value.Equals(hex))
                SelectHex(hex, preserveSelection: true);
            return true;
        }
    }
}
