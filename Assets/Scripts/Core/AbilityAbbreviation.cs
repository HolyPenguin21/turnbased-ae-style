namespace Game.Core
{
    // One entry in GameConfig.abilityAbbreviations — the short form shown wherever a card/
    // building/unit's abilities are listed in a description area (see GameConfig.
    // FormatAbilities), instead of the raw tag name (see Game.Map.BuildingAbilities).
    // fullName is the OTHER form, shown as the header in detail panels (see GameConfig.
    // FormatAbilitiesDetailed) — abbreviation stays the card-only form, never shown there.
    // The description itself isn't duplicated here — it's pulled from
    // Game.Cards.UnitAbilityCatalog.knownAbilities, the single already-populated source for it.
    [System.Serializable]
    public class AbilityAbbreviation
    {
        public string ability;
        public string abbreviation;
        public string fullName;
    }
}
