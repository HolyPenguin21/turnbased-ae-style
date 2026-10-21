using System.Collections.Generic;
using Game.Cards;
using UnityEngine;

namespace Game.Map
{
    // A Facility card placed into one of a BuildingData's FacilitySlots — the runtime counterpart
    // of its CardDefinition. It keeps identity, abilities and the building-card stat block needed
    // by BaseViewerModalUI, while UpgradeLevel remains the existing progression counter used by
    // facility income upgrades.
    public class FacilityData
    {
        public string Name;
        public Sprite Art;
        // Carried over from CardDefinition.detailArt (falling back to Art itself when the card
        // never set one — same convention as BuildingData.DetailArt/UnitData.DetailArt) — the
        // image BaseViewerModalUI's detail panel shows for a placed Facility.
        public Sprite DetailArt;

        // Runtime copy of the same five building-card values shown by CardUI for a Facility:
        // Level / Defense / Structure Points / Fate. UpgradeLevel remains the
        // existing zero-based progression counter; Level is its one-based presentation value.
        public int UpgradeLevel;
        public int Level => UpgradeLevel + 1;
        public int StructurePointsCurrent;
        public int StructurePointsMax;
        public int Defense;
        public int Fate;
        // Anti-air stats, read only when Abilities carries UnitAbilities.AntiAir (AntiAirRules):
        // the card's Attack and radius, floored at 1 exactly like a spawned unit's.
        public int Attack;
        public int AntiAirRadius = 1;
        // The facility's stand-in shooter for AA reactions (AntiAirRules.FacilityGunner): one
        // stable UnitData per placed facility, so AntiAirState's per-turn limit and the shared
        // challenge popup treat it exactly like an AA unit.
        internal Game.Units.UnitData AntiAirGunner;

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
                Fate = definition.fate,
                Attack = definition.attack,
                AntiAirRadius = Mathf.Max(1, definition.antiAirRadius),
            };
            foreach (string ability in definition.grantedAbilities)
                facility.Abilities.Add(ability);
            return facility;
        }
    }
}
