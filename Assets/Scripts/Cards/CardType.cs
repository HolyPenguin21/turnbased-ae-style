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
        // Match-only event rewards, held in PlayerSetupData.Tactics and used during a challenge.
        // Excluded from the normal deck and main-hand capacity.
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
