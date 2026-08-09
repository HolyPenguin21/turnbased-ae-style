namespace Game.Cards
{
    // The full set of unit classification tags (see CardDefinition.unitTypeTags/UnitData.
    // TypeTags) — a real enum rather than free-form strings (unlike grantedAbilities) so the
    // inspector shows a dropdown per entry instead of a text field, and a typo can't silently
    // create a tag nothing checks for. Add new values here as new tags are needed; only some
    // (see UnitAbilities.Hyperkinetic's Armored check) actually affect combat yet.
    public enum UnitTypeTag
    {
        Bio,
        Mechanical,
        Armored,
    }
}
