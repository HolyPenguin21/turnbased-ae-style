namespace Game.Cards
{
    // The full set of unit classification tags (see CardDefinition.unitTypeTags/UnitData.
    // TypeTags) — a real enum rather than free-form strings (unlike grantedAbilities) so the
    // inspector shows a dropdown per entry instead of a text field, and a typo can't silently
    // create a tag nothing checks for. Add new values here as new tags are needed; only some
    // (see UnitAbilities.Hyperkinetic's Armored check) actually affect combat yet.
    //
    // Two loose axes share this one enum (same as the manual's own type list mixes both):
    //   - "nature": Bio / Mechanical / Armored — drives damage typing (Pyrokinetic vs Bio,
    //     Hyperkinetic vs Armored, a future EMP vs Mechanical).
    //   - "chassis/class": Infantry / Vehicle / Mecha / Aircraft / Support / Hero — nothing in
    //     combat reads these yet; they exist so an Equipment card's EquipmentGrant.hostTypeTags
    //     can say "this flamethrower fits Infantry" (ANY-match, see EquipmentSystem.CanAttach).
    // A card is normally tagged with both (e.g. [Bio, Infantry]).
    public enum UnitTypeTag
    {
        Bio,
        Mechanical,
        Armored,
        Infantry,
        Vehicle,
        Mecha,
        Aircraft,
        Support,
        Hero,
    }
}
