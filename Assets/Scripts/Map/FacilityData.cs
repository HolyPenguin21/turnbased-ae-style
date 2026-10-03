using System.Collections.Generic;
using Game.Cards;
using UnityEngine;

namespace Game.Map
{
    // A Facility card placed into one of a BuildingData's FacilitySlots — the UnitData-
    // equivalent for a placed Facility, but far smaller: Facilities have no behavior yet (see
    // CardHandUI.TryDeployIntoBaseModal), just identity and a stub upgrade counter that
    // BaseSlotCardUI's hover "Improve" button increments with no cost or effect. Abilities is
    // the same open-tag pattern as BuildingData.Abilities (see UnitAbilities) — populated
    // from the placed card's own CardDefinition.grantedAbilities, no behavior wired yet.
    public class FacilityData
    {
        public string Name;
        public Sprite Art;
        // Carried over from CardDefinition.detailArt (falling back to Art itself when the card
        // never set one — same convention as BuildingData.DetailArt/UnitData.DetailArt) — the
        // image BaseViewerModalUI's detail panel shows for a placed Facility.
        public Sprite DetailArt;

        // Runtime copy of the same five building-card values shown by CardUI for a Facility:
        // Level / Defense / Structure Points / Resistance / Fate. UpgradeLevel remains the
        // existing zero-based progression counter; Level is its one-based presentation value.
        public int UpgradeLevel;
        public int Level => UpgradeLevel + 1;
        public int StructurePointsCurrent;
        public int StructurePointsMax;
        public int Defense;
        public int Resistance;
        public int Fate;

        public readonly HashSet<string> Abilities = new HashSet<string>();

        public bool HasAbility(string ability) => Abilities.Contains(ability);

        // Shared by both places a Facility card gets placed — the open modal grid
        // (BaseViewerModalUI.TryPlaceFacility) and a direct hand-to-hex drop
        // (CardHandUI.TryDeployFacilityToHex) — so they can't drift on which fields get copied.
        public static FacilityData FromDefinition(CardDefinition definition)
        {
            var facility = new FacilityData
            {
                Name = definition.displayName,
                Art = definition.art,
                DetailArt = definition.detailArt != null ? definition.detailArt : definition.art,
                UpgradeLevel = 0,
                StructurePointsCurrent = definition.hitPoints,
                StructurePointsMax = definition.hitPoints,
                Defense = definition.defenseRating,
                Resistance = definition.resistanceRating,
                Fate = definition.fate,
            };
            foreach (string ability in definition.grantedAbilities)
                facility.Abilities.Add(ability);
            return facility;
        }
    }
}
