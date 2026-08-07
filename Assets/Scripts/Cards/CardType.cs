namespace Game.Cards
{
    // What a card represents — drives which stats matter for it and lets FactionCardCatalog
    // (and any future deck-building UI) filter/group by type instead of only by faction.
    public enum CardType
    {
        Hero,
        Unit,
        Facility,
        Base,
        // Never drawn from the normal deck (see FactionCardCatalog/CardHandUI's deckIndices —
        // simply never listing a Tactic card's catalog index there keeps it out of the main
        // hand entirely, same "exists in the catalog but undrawable" precedent already used for
        // GameConfig.extractionFacilityCards). Granted directly by a hero during battle instead
        // (not built yet) and shown only in the battle screen's own vertical hand — see
        // Game.UI.BattleHandUI.
        Tactic
    }
}
