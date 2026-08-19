namespace Game.Players
{
    // IronConcord and Ashen are the player-selectable factions. Random is a placeholder
    // selection that resolves to one of them later (see GameSetupController.
    // ResolveRandomFactions). None is for card data that isn't any faction's own — e.g.
    // GameConfig.extractionFacilityCards, which every player can build regardless of faction —
    // not a player-selectable option. Neutral is CardCatalog_Neutral's own faction (map-guard/
    // event armies, filled out over time the same way IronConcord's/Ashen's catalogs are) —
    // distinct from None, and from CitadelSetupController's _neutralPlayer, which deliberately
    // keeps using Faction.None (see PlayerSetupData.IsNeutral for why). None and Neutral were
    // appended after IronConcord=0/Random=1, and Ashen after those — existing serialized values
    // must never shift, so new factions are always appended at the end.
    public enum Faction
    {
        IronConcord,
        Random,
        None,
        Neutral,
        Ashen
    }
}
