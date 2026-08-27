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
        // Never drawn from the normal deck (see FactionCardCatalog/StartingDeckCatalog —
        // simply never listing a Tactic card's key in a StartingDeck keeps it out of the main
        // hand entirely, same "exists in the catalog but undrawable" precedent already used for
        // GameConfig.extractionFacilityCards). Granted directly by a hero during battle instead
        // (not built yet) and shown only in the battle screen's own vertical hand — see
        // Game.UI.BattleHandUI.
        Tactic,
        // The manual's "Attachment" — a permanent modifier a player hangs onto one of their
        // own Unit/Hero cards (later: Facility) via right-click → pick target, never dragged
        // onto the map (this project has drag-and-drop for playing units, so the manual's
        // drag-attach gesture would collide — see the project owner's own call). Carries an
        // EquipmentGrant (see CardDefinition.equipment) that adds/overwrites the host's
        // abilities and stats. Intended to reach the hand as a challenge/event reward, not a
        // normal deck draw (temporarily allowed in a StartingDeck for testing). Stats block
        // above is meaningless for it, same as for Facility/Tactic.
        Equipment
    }
}
