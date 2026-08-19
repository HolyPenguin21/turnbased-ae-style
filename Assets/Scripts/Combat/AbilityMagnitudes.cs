namespace Game.Combat
{
    // Bundles the tunable "how big is this ability's numeric effect" magnitudes that used to be
    // threaded through BattleAi/ChallengeResult/FateDuelAi/BattleTargetSelector as separate
    // parameters at every one of their call sites — see BattleAttackPopupUI's own
    // Critical/Hyperkinetic/Ceramic/Pyrokinetic properties and its Magnitudes convenience
    // property, the single place each of these actually gets read from (with the manual's own
    // hardcoded fallback numbers if abilityCatalog isn't assigned). Plain data, no behavior —
    // ChallengeResult.ApplyAbilityModifiers still does all the actual math.
    public readonly struct AbilityMagnitudes
    {
        public readonly float CriticalDamageMultiplier;
        public readonly int HyperkineticBonusDamage;
        public readonly int CeramicArmorReduction;
        public readonly int PyrokineticBonusDamage;

        public AbilityMagnitudes(float criticalDamageMultiplier, int hyperkineticBonusDamage,
            int ceramicArmorReduction, int pyrokineticBonusDamage)
        {
            CriticalDamageMultiplier = criticalDamageMultiplier;
            HyperkineticBonusDamage = hyperkineticBonusDamage;
            CeramicArmorReduction = ceramicArmorReduction;
            PyrokineticBonusDamage = pyrokineticBonusDamage;
        }

        // Same manual-default fallback numbers every call site already used individually
        // (2f/2/1/2) — for a caller with no BattleAttackPopupUI reference to read from.
        public static readonly AbilityMagnitudes Default = new AbilityMagnitudes(2f, 2, 1, 2);
    }
}
