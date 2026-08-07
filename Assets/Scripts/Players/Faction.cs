namespace Game.Players
{
    // Only one real faction exists so far. Random is a placeholder selection that resolves
    // to an actual faction later (once there's more than one to pick from). None is for card
    // data that isn't any faction's own — e.g. GameConfig.extractionFacilityCards, which every
    // player can build regardless of faction — not a player-selectable option, so it's
    // deliberately appended last (existing IronConcord=0/Random=1 serialized values must not
    // shift).
    public enum Faction
    {
        IronConcord,
        Random,
        None
    }
}
