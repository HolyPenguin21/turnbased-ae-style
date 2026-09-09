namespace Game.Ai.V2
{
    // ARCH-02 §36/§43 — the single monotonic counter bumped by each completed V2 mutation
    // transaction. Multi-command operations that cannot safely expose an intermediate state
    // (currently raid assembly) bump once after commit; a complete rollback/no-op never bumps.
    // Process-lifetime scope is intentional: only equality/order matters, not the absolute value.
    internal static class V2StateVersion
    {
        internal static int Current { get; private set; }

        internal static bool IsCurrent(int plannedAtVersion) =>
            plannedAtVersion >= 0 && plannedAtVersion == Current;

        internal static int Bump() => ++Current;
    }
}
