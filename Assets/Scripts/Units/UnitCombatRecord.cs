using System;

namespace Game.Units
{
    // Lifetime combat record of one live card instance (project owner, 2026-11-19): shown at the
    // end of the unit detail panel and saved with the unit. A card that leaves the map and is
    // played again is a new body and starts from zero.
    // Damage is the hit points actually removed (overkill beyond the remaining HP is not counted),
    // so one hit adds the same amount to the attacker's DamageDealt and the victim's DamageTaken.
    // Hits on an own-side body (Splash/Scorcher, an Explosion launched into its own stack) count
    // as damage taken only. Kills count enemy units finished off by this unit's own attack.
    [Serializable]
    public struct UnitCombatRecord
    {
        public int BattlesSurvived;
        public int DamageDealt;
        public int DamageTaken;
        public int Kills;

        public bool IsValid => BattlesSurvived >= 0 && DamageDealt >= 0 && DamageTaken >= 0 && Kills >= 0;

        // One resolved hit. `hitPointsBefore` is the victim's HP right before the damage was
        // applied; the victim already carries the result. `victimIsUnit` is false for a structure
        // stand-in (ExplosionAttack.Target.Combatant): its damage counts, a destroyed structure
        // is not a killed unit.
        public static void RecordHit(UnitData attacker, UnitData victim, int hitPointsBefore, bool victimIsUnit = true)
        {
            if (victim == null) return;
            int after = Math.Max(0, victim.HitPointsCurrent);
            int removed = Math.Max(0, hitPointsBefore - after);
            if (removed == 0) return;
            victim.CombatRecord.DamageTaken += removed;
            if (attacker == null || ReferenceEquals(attacker, victim) || attacker.Owner == victim.Owner) return;
            attacker.CombatRecord.DamageDealt += removed;
            if (victimIsUnit && after == 0) attacker.CombatRecord.Kills++;
        }
    }
}
