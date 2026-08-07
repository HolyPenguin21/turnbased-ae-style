using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Combat
{
    // "Initiating Battle" (see the manual) — for now, just the simplest of its seven listed
    // triggers: a non-stealthed combat capable army moves into a hex containing another
    // non-stealthed combat capable army. Stealth doesn't exist yet in this project, so every
    // army counts as non-stealthed — this reduces to "any enemy combat-capable army on the
    // hex". Siege, the empty-garrison rule, and delay-attack aren't handled yet either — see
    // HexSelectionController.TryIssueMoveOrder for where this gets called.
    public static class BattleInitiator
    {
        // "Not Combat Capable": a hero-only army (or an empty one) can't fight — see the
        // manual's Hero section. At least one non-hero unit is required.
        public static bool IsCombatCapable(ArmyData army)
        {
            if (army == null)
                return false;
            foreach (UnitData member in army.Members)
                if (!member.IsHero)
                    return true;
            return false;
        }

        // The first enemy combat-capable army found at `hex`, if any — null if the hex is clear
        // or only holds friendly/non-combat-capable armies.
        public static ArmyData FindEnemyAt(HexCoord hex, PlayerSetupData mover)
        {
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army.Owner != mover && IsCombatCapable(army))
                    return army;
            return null;
        }
    }
}
