using System.Collections.Generic;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Map
{
    // Plain data for one building on the map — same data/visual split as ArmyData/ArmyController.
    // Abilities is a small open-ended tag set (see BuildingAbilities) rather than a building-type
    // enum, since what a building can do is more naturally a set of tags than a rigid type
    // hierarchy. Doubles as the "Base" runtime record for BaseViewerModalUI (the citadel and any
    // player-built Base are both just a BuildingData tagged BuildingAbilities.Base) — same role
    // ArmyData plays for armies.
    public class BuildingData
    {
        public string Name;
        public HexCoord Hex;
        public PlayerSetupData Owner;
        public MapObjectVisual Visual;
        // The building's own card art (citadel's "Concord Citadel" art, or a player-built
        // Base's own card art) — shown on cell 0 of BaseViewerModalUI's grid.
        public Sprite Art;
        public readonly HashSet<string> Abilities = new HashSet<string>();

        public bool HasAbility(string ability) => Abilities.Contains(ability);

        // Set only by CitadelSetupController — true for the one building whose destruction ends
        // the game for its owner (see GameTurnController's BuildingRegistry.BuildingDestroyed
        // subscription). A later-built "Concord Citadel" card (see HexSelectionController.
        // SpawnBuilding) produces an otherwise-identical building — same abilities, same hex
        // resource-bonus stamp — but never carries this flag; only the originally placed one
        // does. That flag is the sole difference between the two.
        public bool IsStartingCitadel;

        // --- Base stats (only meaningful on a BuildingAbilities.Base-tagged building) --------

        public int Level = 1;
        public int StructurePointsCurrent;
        public int StructurePointsMax;
        public int Defense;
        public int Resistance;
        // Same idea as UnitData.Fate — comes into play defending a Siege Challenge when no
        // hero is present in a defending army on the hex (see the manual) — a hero's own Fate
        // is used instead when there is one. Not yet consumed anywhere, same as every other
        // not-yet-resolved combat stat. Carried over from CardDefinition.fate at spawn time.
        public int Fate;
        public ResourceYields ResourceYield;

        // Fixed at construction — index i is locked until UnlockedFacilitySlots > i, empty while
        // FacilitySlots[i] is null, otherwise filled. Never resized; a slot's identity (its
        // index) stays stable across upgrades. Per-instance rather than a shared constant since
        // a hero-built resource site (4 slots, one per resource type) differs from a citadel/
        // card-built Base (5, reached via tiered upgrades) — see HasTieredUnlock.
        public readonly int TotalFacilitySlots;
        public readonly FacilityData[] FacilitySlots;

        // The slot capacity every citadel/card-built Base gets (see SpawnBuilding/
        // CitadelSetupController, both use the BuildingData() default) — also what CardHandUI
        // checks a hero-built resource site's occupied facilities against before allowing a
        // dragged Base card to merge with/absorb it (see TryBuildBase).
        public const int DefaultTotalFacilitySlots = 5;

        // True for a citadel/card-built Base — UnlockedFacilitySlots grows via Level (see
        // GameConfig.baseUpgradeTiers), starting at 2 and reaching TotalFacilitySlots. False for
        // a hero-built resource site, whose slots are all open immediately with no Level
        // progression of its own.
        public bool HasTieredUnlock = true;

        public BuildingData(int totalFacilitySlots = DefaultTotalFacilitySlots)
        {
            TotalFacilitySlots = totalFacilitySlots;
            FacilitySlots = new FacilityData[totalFacilitySlots];
        }

        // A fresh Base already starts with 2 unlocked slots; each of the 3 upgrade tiers (see
        // GameConfig.baseUpgradeTiers) unlocks one more, so all 5 cells are reachable by Level 4.
        public int UnlockedFacilitySlots => HasTieredUnlock ? Mathf.Clamp(Level + 1, 0, TotalFacilitySlots) : TotalFacilitySlots;

        // Shared by every place a Facility card/definition gets placed here (modal drop, hex
        // drop, hero-built extraction) so they can't drift on what "the next open slot" means.
        public int FindFirstAvailableFacilitySlot()
        {
            for (int i = 0; i < UnlockedFacilitySlots; i++)
                if (FacilitySlots[i] == null)
                    return i;
            return -1;
        }

        // Used to block building a duplicate extraction Facility for a resource type this
        // building already collects (see HexSelectionController.TryBuildExtractionFacility) and
        // to decide which of the 4 possible resource-action buttons a hex still needs.
        public bool HasFacilityWithAbility(string ability)
        {
            foreach (FacilityData facility in FacilitySlots)
                if (facility != null && facility.HasAbility(ability))
                    return true;
            return false;
        }

        // How much of `type` this building collects per turn on its own — 1 for its own
        // baked-in CollectX ability (e.g. the citadel) plus 1 + UpgradeLevel for every placed
        // Facility with that ability. Doesn't know about the hex's actual yield — callers cap
        // against that themselves. Shared by GameTurnController's actual per-turn collection AND
        // HexSelectionController's resource-action button visibility, so the two can never
        // disagree about how much is already being collected (see BuildingAbilities.CollectAbilities).
        public int CollectedAmount(ResourceType type)
        {
            string ability = BuildingAbilities.CollectAbilityFor(type);
            int amount = HasAbility(ability) ? 1 : 0;
            foreach (FacilityData facility in FacilitySlots)
                if (facility != null && facility.HasAbility(ability))
                    amount += 1 + facility.UpgradeLevel;
            return amount;
        }
    }
}
