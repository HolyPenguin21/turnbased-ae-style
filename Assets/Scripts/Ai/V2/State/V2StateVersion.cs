namespace Game.Ai.V2
{
    // ARCH-02 §36/§43 — a single monotonic counter bumped by every V2 execution-tier operation
    // that actually mutates authoritative world state (a card played, a building founded, a unit
    // moved, a materialization chain deployed). Execution results stamp V2ActionOutcome.
    // StateVersionAfter from it so a caller can tell "the world moved under me since I planned"
    // without diffing the whole world. Process-lifetime scope; the absolute value is meaningless,
    // only that it changed.
    internal static class V2StateVersion
    {
        internal static int Current { get; private set; }

        internal static int Bump() => ++Current;
    }
}
