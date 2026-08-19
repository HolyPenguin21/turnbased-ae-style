using Game.Cards;

namespace Game.Core
{
    // One entry in GameConfig.abilityAbbreviations — the short form shown wherever a card/
    // building/unit's abilities are listed in a description area (see GameConfig.
    // FormatAbilities), instead of the raw tag name (see Game.Cards.UnitAbilities). ability is
    // synced from UnitAbilities.All (see GameConfig.SyncAbilityAbbreviations) rather than hand-
    // typed, so it can never drift from the real ability tag; abbreviation is the only field
    // ever hand-edited here. The full display name shown in detail panels (see GameConfig.
    // FormatAbilitiesDetailed) is derived straight from the tag itself (see
    // UnitAbilities.PrettyName) rather than a separate hand-typed copy that could drift from
    // it. The description itself isn't duplicated here either — it's pulled from
    // Game.Cards.UnitAbilityCatalog.knownAbilities, the single already-populated source for it.
    [System.Serializable]
    public class AbilityAbbreviation
    {
        [ReadOnly] public string ability;
        public string abbreviation;
    }
}
