using System.Collections.Generic;
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
        public UnitData SpawnUnit(string unitName, PlayerSetupData owner, int moveMax, int activationApCost, bool isHero, int commandRating, Sprite art, IEnumerable<string> grantedAbilities = null, int attack = 0, int range = 1, int hitPoints = 1, int initiative = 1, int fate = 0, int defense = 1, int resistance = 1, IEnumerable<UnitTypeTag> typeTags = null, Sprite detailArt = null, int apCost = 0, ResourceCost resourceCost = null, bool isAviation = false, int launchEnergyCost = 0, int turnsWithoutRefuel = 0, int antiAirRadius = 1)
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
            UnitRepair.InitializeRepairCost(data);
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
            marker.SetSortingOrder(MapSortingOrder.ArmyCircle, MapSortingOrder.ArmyIcon);
            FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(army.Owner.Faction)
                : null;
            if (AviationRules.IsAirArmy(army) && ownerCatalog != null && ownerCatalog.airArmyIcon != null)
                marker.SetIcon(ownerCatalog.airArmyIcon);
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
            // Every empty army on its owner's Barracks hex remains as a reusable container.
            // This is deliberately shared by ground and air armies; air composition must not
            // make a formerly airborne stack an exception after its last card is destroyed.
            if (building != null && building.Owner == army.Owner && building.HasAbility(UnitAbilities.Barracks))
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
        // same buildingMarkerPrefab (and owner's own FactionCardCatalog.citadelIcon) the
        // auto-placed citadel already uses — no visual distinction yet between "the citadel" and
        // a player-built Base. Position/offset resolution is left entirely to the RestackArmiesOn
        // call at the end, same as CitadelSetupController relies on its own one-off
        // HexObjectLayout call before either the registry or RestackArmiesOn existed.
        public BuildingData SpawnBuilding(CardDefinition definition, HexCoord hex, PlayerSetupData owner)
        {
            if (gameConfig == null || gameConfig.buildingMarkerPrefab == null || map == null || owner == null || definition == null)
                return null;

            FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(owner.Faction)
                : null;

            var building = new BuildingData
            {
                Name = definition.displayName, Hex = hex, Owner = owner,
                Visual = CreateBuildingMarker(hex, owner, gameConfig.buildingMarkerPrefab, ownerCatalog?.citadelIcon),
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

            // Same rule as the auto-placed citadel (see CitadelSetupController.CreateGarrison):
            // a Barracks-tagged building needs its own garrison to receive Unit/Hero cards
            // deployed from hand — not every Base card grants Barracks, so BuildingRegistry.
            // EnsureGarrisonForBuilding (shared with the capture path — see its own comment)
            // no-ops unless the card's own grantedAbilities actually include it.
            BuildingRegistry.EnsureGarrisonForBuilding(building, this);

            // A "Concord Citadel" card played from hand is otherwise identical to the starting
            // citadel (same abilities, same stats) but per the user's own spec does NOT get the
            // permanent hex resource bonus — that belongs only to the hex the player chose at
            // game start (see CitadelSetupController.SpawnCitadelMarker/BuildingData.
            // IsStartingCitadel). A later citadel only ever collects whatever the hex's own
            // terrain actually yields.

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
            marker.SetSortingOrder(MapSortingOrder.BuildingCircle, MapSortingOrder.BuildingIcon);
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
                return false; // not this player's building — no hint, same as any other irrelevant target
            }

            string ability = definition.grantedAbilities.Find(a => System.Array.IndexOf(UnitAbilities.CollectAbilities, a) >= 0);
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
                FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                    ? cardHandUI.StartingDeckCatalog.GetCatalog(owner.Faction)
                    : null;
                building.Visual = CreateBuildingMarker(hex, owner, gameConfig.facilityMarkerPrefab, ownerCatalog?.facilityIcon);
                BuildingRegistry.Register(hex, building);
                RestackArmiesOn(hex, null);
            }

            if (_selectedHex.HasValue && _selectedHex.Value.Equals(hex))
                SelectHex(hex, preserveSelection: true);
            return true;
        }
    }
}
