namespace Game.Units
{
    // Where a unit stands in its army's battle line (see the manual's "Setup" under Tactical
    // Battle Module) — read once ranged-attack range checks exist. Front is the default for
    // every unit until a player rearranges cards ahead of a fight (Battle Setup UI, not built
    // yet — see Game.Combat).
    public enum BattleRow
    {
        Front,
        Back,
    }
}
