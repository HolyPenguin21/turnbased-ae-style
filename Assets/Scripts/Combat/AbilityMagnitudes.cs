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
        public readonly int BerserkAttackGain;
        public readonly int BerserkDefenseLoss;
        public readonly int IonicBonusDamage;
        public readonly int DrillAttackBonus;

        // Ionic/Drill are trailing optionals with the manual defaults so every older call site
        // (tests pinning the first six numbers) keeps compiling and meaning the same thing.
        public AbilityMagnitudes(float criticalDamageMultiplier, int hyperkineticBonusDamage,
            int ceramicArmorReduction, int pyrokineticBonusDamage,
            int berserkAttackGain, int berserkDefenseLoss,
            int ionicBonusDamage = 2, int drillAttackBonus = 1)
        {
            CriticalDamageMultiplier = criticalDamageMultiplier;
            HyperkineticBonusDamage = hyperkineticBonusDamage;
            CeramicArmorReduction = ceramicArmorReduction;
            PyrokineticBonusDamage = pyrokineticBonusDamage;
            BerserkAttackGain = berserkAttackGain;
            BerserkDefenseLoss = berserkDefenseLoss;
            IonicBonusDamage = ionicBonusDamage;
            DrillAttackBonus = drillAttackBonus;
        }

        // Same manual-default fallback numbers every live call site uses when the catalog is absent.
        public static readonly AbilityMagnitudes Default = new AbilityMagnitudes(2f, 2, 1, 2, 1, 1, 2, 1);
    }
}
