namespace Game.Players
{
    // Only one real player-selectable faction exists so far. Random is a placeholder selection
    // that resolves to an actual faction later (once there's more than one to pick from). None
    // is for card data that isn't any faction's own — e.g. GameConfig.extractionFacilityCards,
    // which every player can build regardless of faction — not a player-selectable option.
    // Neutral is CardCatalog_Neutral's own faction (map-guard/event armies, filled out over
    // time the same way IronConcord's catalog is) — distinct from None, and from
    // CitadelSetupController's _neutralPlayer, which deliberately keeps using Faction.None (see
    // PlayerSetupData.IsNeutral for why). Both None and Neutral are deliberately appended last
    // (existing IronConcord=0/Random=1/None=2 serialized values must not shift).
    public enum Faction
    {
        IronConcord,
        Random,
        None,
        Neutral
    }
}
