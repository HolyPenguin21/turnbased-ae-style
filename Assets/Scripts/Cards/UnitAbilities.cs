namespace Game.Cards
{
    // Tags for the manual's "Unit Special Abilities" (pg. 40-41) that this project actually
    // implements — same const-string-tag pattern as Game.Map.BuildingAbilities, just for
    // Hero/Unit cards' own CardDefinition.grantedAbilities instead of Base cards'. Checked via
    // UnitData.HasAbility at the actual effect site (BattleAttackPopupUI/BattleScreenUI.Combat.cs/
    // CardHandUI/HexSelectionController.Factory), not looked up here — this class only names them.
    public static class UnitAbilities
    {
        // "Damage inflicted by the unit in a successful attack is increased by a factor of 2.0"
        // — see UnitAbilityCatalog.criticalDamageMultiplier, BattleAttackPopupUI.ResolveDamage.
        public const string CriticalDamage = "CriticalDamage";

        // "Any damage received by the unit is modified by -1" — see
        // UnitAbilityCatalog.ceramicArmorReduction, BattleAttackPopupUI.ResolveDamage.
        public const string CeramicArmor = "CeramicArmor";

        // "The unit gains +1 Attack and -1 Def each time it is hit in a Ground Combat challenge
        // for the duration of the battle" — see UnitAbilityCatalog.berserkAttackGain/
        // berserkDefenseLoss, BattleAttackPopupUI.ResolveDamage, UnitData.BerserkStacks for how
        // the "for the duration of the battle" part is reverted afterward.
        public const string Berserk = "Berserk";

        // "The AP cost to deploy the unit is 0 and the unit costs no AP to move when in an army"
        // — see CardHandUI.DeployUnit (deploy cost) and HexSelectionController.Factory.SpawnUnit
        // (ActivationApCost forced to 0 at spawn time).
        public const string RapidReaction = "RapidReaction";

        // "A successful Ground Combat challenge by the unit results in the target unit being
        // committed for the remainder of the turn if not already committed" — this project has no
        // separate per-round "committed" flag (see MECHANICS_CHECKLIST.md), so the simplified
        // equivalent is dropping the target from the REST of this round's turn order outright if
        // it hasn't acted yet. See BattleScreenUI.Combat.cs's OnAttackResolved/
        // SkipRemainingTurnThisRound.
        public const string ShockAttack = "ShockAttack";

        // "+2 damage against Armored-tagged targets, if the attack already dealt at least 1
        // damage" — an attacker-side flat bonus keyed off the DEFENDER's UnitTypeTags.Armored,
        // not any stat of the attacker's own. See UnitAbilityCatalog.hyperkineticBonusDamage,
        // BattleAttackPopupUI.ResolveDamage.
        public const string Hyperkinetic = "Hyperkinetic";
    }
}
