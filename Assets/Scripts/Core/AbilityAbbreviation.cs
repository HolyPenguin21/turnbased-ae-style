namespace Game.Core
{
    // One entry in GameConfig.abilityAbbreviations — the short form shown wherever a card/
    // building/unit's abilities are listed in a description area (see GameConfig.
    // FormatAbilities), instead of the raw tag name (see Game.Map.BuildingAbilities).
    [System.Serializable]
    public class AbilityAbbreviation
    {
        public string ability;
        public string abbreviation;
    }
}
