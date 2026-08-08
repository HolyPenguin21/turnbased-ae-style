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
        // "Not Combat Capable": a hero-only army (or an empty one) can't fight a Ground Combat
        // round — see the manual's Hero section; heroes never act in BattleTurnOrder's own
        // acting queue and can't be targeted as a regular grid unit either. At least one
        // non-hero unit is required. Still used for exactly that narrower question (can this
        // army take part in the Tactical Battle Module / does it still have a real fighting
        // force) — see CheckBattleEnd, and the hunter-needs-units rule in
        // BattleScreenUI.Combat.cs.
        public static bool IsCombatCapable(ArmyData army)
        {
            if (army == null)
                return false;
            foreach (UnitData member in army.Members)
                if (!member.IsHero)
                    return true;
            return false;
        }

        // Whether `army` is a valid CONTACT target at all — merely non-empty, hero-only
        // included. Broader than IsCombatCapable on purpose: this used to just BE
        // IsCombatCapable, which made a hero-only army completely untouchable — that was only
        // ever a stand-in for not having built the Capture Kill Challenge yet (see the user's
        // own note), not a permanent rule. A hero-only army found this way never enters the
        // Tactical Battle Module (see HexSelectionController.Movement.cs's own branch) — it goes
        // straight to BattleScreenUI.BeginCaptureKillEncounter instead, since there's nothing
        // for a normal battle round to actually do against it.
        public static bool IsEngageable(ArmyData army) => army != null && army.Members.Count > 0;

        // The first enemy CONTACTABLE army found at `hex`, if any — null if the hex is clear or
        // only holds friendly/empty armies. See IsEngageable for what counts.
        public static ArmyData FindEnemyAt(HexCoord hex, PlayerSetupData mover)
        {
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army.Owner != mover && IsEngageable(army))
                    return army;
            return null;
        }
    }
}
