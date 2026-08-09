using UnityEngine;

namespace Game.Core
{
    // One entry in GameConfig.abilityAbbreviations — the short form shown wherever a card/
    // building/unit's abilities are listed in a description area (see GameConfig.
    // FormatAbilities), instead of the raw tag name (see Game.Map.BuildingAbilities).
    // fullName/description are the OTHER form, shown only in detail panels (see GameConfig.
    // FormatAbilitiesDetailed) — abbreviation stays the card-only form, these never appear there.
    [System.Serializable]
    public class AbilityAbbreviation
    {
        public string ability;
        public string abbreviation;
        public string fullName;
        [TextArea]
        public string description;
    }
}
