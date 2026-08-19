namespace Game.Cards
{
    // Every ability/skill tag in the game — Hero/Unit combat abilities AND Base-card/building
    // ones (formerly a separate Game.Map.BuildingAbilities) in one place, since nothing
    // downstream actually distinguishes them: CardDefinition.grantedAbilities is one shared
    // field for every CardType (see its own comment), UnitData.Abilities/BuildingData.Abilities
    // both just read this same field at spawn time, and both UnitAbilityCatalog.knownAbilities
    // and GameConfig.abilityAbbreviations list every tag here regardless of what kind of card
    // grants it. Keeping two parallel tag lists was what let them drift apart in the first place
    // (see the project owner's own report: a tag typed into UnitAbilityCatalog/
    // GameConfig.abilityAbbreviations with a space, e.g. "Rapid Reaction", silently never
    // matched the real UnitAbilities.RapidReaction constant checked at the actual effect site,
    // so the ability showed up in the UI but never worked) — folding everything into this one
    // class, plus All below driving every other list instead of being hand-typed, closes that
    // gap for good. Checked via UnitData.HasAbility/BuildingData.HasAbility at the actual effect
    // site (BattleAttackPopupUI/BattleScreenUI.Combat.cs/CardHandUI/HexSelectionController.Factory/
    // BuildingRegistry/...), not looked up here — this class only names them.
    public static class UnitAbilities
    {
        // --- Hero/Unit combat abilities (see UnitAbilityCatalog for their tunable magnitudes) -

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

        // "+2 damage against Bio-tagged targets, if the attack already dealt at least 1 damage"
        // — same pattern as Hyperkinetic, just keyed off UnitTypeTag.Bio instead of Armored. See
        // UnitAbilityCatalog.pyrokineticBonusDamage, ChallengeResult.ApplyAbilityModifiers.
        public const string Pyrokinetic = "Pyrokinetic";

        // Extends its army's own vision radius (see Game.Map.VisionSystem) beyond the flat
        // GameConfig.armyVisionRadius default — same shared-catalog-value pattern as every other
        // ability above (see UnitAbilityCatalog.recceRadius/recceStrength). An army with any
        // Recce-tagged member gets the flat bonus once, not summed per member (see ArmyData.
        // HasRecce) — "strength" itself has no effect yet, reserved for a future use per the
        // project owner's own call.
        public const string Recce = "Recce";

        // --- Base-card/building abilities (formerly Game.Map.BuildingAbilities) --------------
        // Open-ended, same as every tag above — still only one real building (the citadel) plus
        // hero-built Facilities, and new abilities will keep getting added as buildings do.
        // CardDefinition.requiredBuildingAbility stores whichever one a hex needs by name,
        // matched against BuildingData.Abilities.

        public const string Barracks = "Barracks";
        // Marks a building as a Base — grants access to BaseViewerModalUI (facility slots,
        // upgrades, repair). Both the auto-placed starting citadel and any player-built Base
        // (from a CardType.Base card) carry this tag.
        public const string Base = "Base";
        // Carried by a placed FacilityData (e.g. Research Facility), not a BuildingData — same
        // open-tag pool, no behavior wired yet.
        public const string Lab = "Lab";
        // Carried by an extraction Facility (see GameConfig.extractionFacilityCards) or baked
        // directly onto the citadel — each resource type collected this way contributes
        // 1 + FacilityData.UpgradeLevel toward that resource's per-turn income, capped by the
        // hex's own effective yield (see GameTurnController.CollectResourceIncome).
        public const string CollectHuman = "CollectHuman";
        public const string CollectEnergy = "CollectEnergy";
        public const string CollectMaterials = "CollectMaterials";
        public const string CollectTech = "CollectTech";

        public static readonly string[] CollectAbilities =
        {
            CollectHuman, CollectEnergy, CollectMaterials, CollectTech,
        };

        // Index matches Game.Economy.ResourceType's declaration order (Human, Energy, Materials,
        // Tech) — see GameConfig.extractionFacilityCards, which is indexed the same way.
        public static string CollectAbilityFor(Game.Economy.ResourceType type) => CollectAbilities[(int)type];

        // "Is this a citadel" used to be its own separate tag — removed as pure redundant
        // bookkeeping, since collecting all 4 resource types on its own (no Facility needed) IS
        // already what makes a building a citadel (see HexSelectionController.SpawnBuilding,
        // the only place this was ever actually read). Not shown in any ability list, unlike
        // the tag it replaced.
        public static bool IsFullCitadel(Game.Map.BuildingData building)
        {
            if (building == null)
                return false;
            foreach (string ability in CollectAbilities)
                if (!building.HasAbility(ability))
                    return false;
            return true;
        }

        // --- Every tag above, in one place --------------------------------------------------
        // Symbol references, not fresh string literals — a typo here is a compile error, not a
        // silent runtime mismatch. Drives AbilityTagDrawer's dropdown and
        // UnitAbilityCatalog/GameConfig.abilityAbbreviations' own auto-sync (see their
        // OnValidate) instead of either being hand-typed, so a newly added ability only ever
        // needs a const above PLUS an entry here — every list that shows abilities picks it up
        // automatically from there on.
        public static readonly string[] All =
        {
            CriticalDamage, CeramicArmor, Berserk, RapidReaction, ShockAttack, Hyperkinetic, Pyrokinetic, Recce,
            Barracks, Base, Lab, CollectHuman, CollectEnergy, CollectMaterials, CollectTech,
        };

        // Human-readable form of a tag, derived purely from its own PascalCase spelling (a
        // space inserted before each internal capital) — e.g. "RapidReaction" -> "Rapid
        // Reaction". Used wherever a full ability name is shown (see GameConfig.
        // FormatAbilitiesDetailed) instead of a separately hand-typed copy that could drift
        // from the tag itself the same way GameConfig.abilityAbbreviations' old fullName field
        // once could.
        public static string PrettyName(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return tag;

            var result = new System.Text.StringBuilder();
            result.Append(tag[0]);
            for (int i = 1; i < tag.Length; i++)
            {
                if (char.IsUpper(tag[i]) && !char.IsUpper(tag[i - 1]))
                    result.Append(' ');
                result.Append(tag[i]);
            }
            return result.ToString();
        }
    }
}
