using System;
using Game.Cards;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HERO OPERATIONAL-ROLE EVALUATOR  (Strategy V2 — spec §8)
    // ===========================================================================================
    //  ONE place that answers "is this hero better used commanding a field force, or kept for
    //  base / research / production support?". Reads ONLY canonical data already on the unit —
    //  CommandRating, Attack/Defense/HitPoints/Initiative, Fate, and the granted Researcher /
    //  Assembler support abilities. Never a card or display name.
    //
    //  The result is a PREFERENCE, not a prohibition: RaidAssembly / Housekeeping fall back to a
    //  SupportOperator rather than deadlock when it is the only usable hero.
    // ===========================================================================================
    public enum HeroOperationalRole
    {
        CombatLeader,     // well suited to lead a field army
        Flexible,         // acceptable for either use
        SupportOperator,  // production/research/base utility clearly outweighs combat leadership
    }

    public static class HeroRoleEvaluator
    {
        // Combat-leadership merit from canonical hero data: CommandRating (leadership capacity)
        // plus the hero's own AiPower contribution (HitPoints / Initiative / Resistance / Fate —
        // heroes carry no Attack/Defense). Higher = better field commander.
        public static float CombatLeadershipScore(UnitData hero)
        {
            if (hero == null || !hero.IsHero)
                return 0f;
            float ownContribution = AiPower.ToPowerUnit(hero).BasePower;
            return hero.CommandRating * AiConfigV2.heroRoleCommandWeight
                 + ownContribution * AiConfigV2.heroRoleCombatContributionWeight;
        }

        // A canonical production/research vocation granted by the hero's own abilities.
        public static bool HasSupportVocation(UnitData hero) =>
            hero != null && hero.IsHero
            && (hero.HasAbility(UnitAbilities.Researcher) || hero.HasAbility(UnitAbilities.Assembler));

        public static HeroOperationalRole Classify(UnitData hero)
        {
            if (hero == null || !hero.IsHero)
                return HeroOperationalRole.Flexible;

            bool support = HasSupportVocation(hero);
            float combat = CombatLeadershipScore(hero);

            if (support)
                return combat < AiConfigV2.heroRoleFlexibleCombatFloor
                    ? HeroOperationalRole.SupportOperator
                    : HeroOperationalRole.Flexible;

            return combat >= AiConfigV2.heroRoleCombatLeaderFloor
                ? HeroOperationalRole.CombatLeader
                : HeroOperationalRole.Flexible;
        }

        // Preference rank for leading a FIELD force: CombatLeader (2) > Flexible (1) >
        // SupportOperator (0).
        public static int FieldCommandPreference(UnitData hero)
        {
            switch (Classify(hero))
            {
                case HeroOperationalRole.CombatLeader: return 2;
                case HeroOperationalRole.Flexible: return 1;
                default: return 0;
            }
        }

        // Deterministic "who should lead the field force" ordering — most-preferred first.
        // Role preference, then combat score, then CommandRating, then Fate, then a stable
        // name tiebreak (ordinal) matching RaidAssemblyPlanner's existing donor-pick convention.
        public static int CompareForFieldCommand(UnitData a, UnitData b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            int c = FieldCommandPreference(b).CompareTo(FieldCommandPreference(a));
            if (c != 0) return c;
            c = CombatLeadershipScore(b).CompareTo(CombatLeadershipScore(a));
            if (c != 0) return c;
            c = b.CommandRating.CompareTo(a.CommandRating);
            if (c != 0) return c;
            c = b.Fate.CompareTo(a.Fate);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Name ?? string.Empty, b.Name ?? string.Empty);
        }
    }
}
