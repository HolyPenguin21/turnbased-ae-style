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

        // Parameterized reconnaissance tags (replaced the old bool "Recce" + shared
        // UnitAbilityCatalog.recceRadius/recceStrength — project owner's own call, see the
        // stealth design). Grammar is r<radius>s<spot>: army vision +<radius> hexes (see
        // Game.Map.VisionSystem), and <spot> spot dice brought to a stealth-detection
        // Challenge (see Game.Map.StealthSystem). Numbers are parsed by Game.Cards.
        // AbilityParams — nobody re-parses these strings. Radii/pools take the max across
        // members and across observers, never a sum. r1s0 widens vision and reveals
        // ordinary units but its 0-die pool can never detect a hidden unit.
        public const string R1S0 = "r1s0";
        public const string R1S4 = "r1s4";
        public const string R1S5 = "r1s5";
        public const string R1S6 = "r1s6";

        // A unit carrying this MAY be put into stealth (1 AP per unit to enter, 0 to leave)
        // — hide-dice pool is <level> plus a terrain bump (see Game.Map.StealthSystem.
        // HideDiceFor). Stealth state lives per-UnitData (UnitData.IsHidden), never on the
        // army. Not assigned to any shipped card yet — the project owner adds carriers.
        public const string Stealth4 = "Stealth4";

        // Anti-air range is configured per card (CardDefinition.antiAirRadius), while this one
        // tag says that the unit may perform the reaction at all.  Parsing stays in
        // Game.Aviation.AntiAirRules rather than duplicated by movement and UI.
        public const string AntiAir = "AA";

        // "+2 AP on this player's turn" — works on any card type (Unit/Hero/Facility/Base), same
        // as every ability here, but only while the carrier is actually IN PLAY: a member of one
        // of this player's own (non-Prison) armies, or a Base/Facility they own, per the project
        // owner's own spec — a copy of the card still sitting in hand grants nothing. Applied
        // once per carrier (a hero AND a base both granting it stack), right after the initiative
        // roll decides this turn's base AP by rank. See GameTurnController.
        // GrantApBonusActionPoints/ApBonusPerSource.
        public const string ApBonus = "ApBonus";

        // --- Base-card/building abilities (formerly Game.Map.BuildingAbilities) --------------
        // Open-ended, same as every tag above — still only one real building (the citadel) plus
        // hero-built Facilities, and new abilities will keep getting added as buildings do.
        // CardDefinition.requiredBuildingAbility stores whichever one a hex needs by name,
        // matched against BuildingData.Abilities.

        public const string Barracks = "Barracks";

        // --- Research / Production data contract (no gameplay effect yet) --------------------
        // Four semantic capability/role tags the next milestone (Research/Production) will gate
        // on. Nothing here changes AP, movement, combat, Facility placement, resources, AI or
        // any current player action — these only make the "can this hero use this building"
        // question answerable through the existing ability system, without probing displayName,
        // card ids or new boolean fields.
        //
        //   Research   — carried by a placed FacilityData (b_Lab). Checked with
        //                building.HasFacilityWithAbility(UnitAbilities.Research), which already
        //                walks the real FacilitySlots — no Facility-name knowledge needed.
        //   Researcher — carried by a deployed Hero (grantedAbilities -> UnitData.Abilities at
        //                spawn). Checked with hero.IsHero && hero.HasAbility(UnitAbilities.Researcher).
        //   Production — Facility counterpart of Research, carried by b_Factory.
        //   Assembler  — Hero counterpart of Researcher, fully symmetric.
        //
        // Contract for the next task: Research is available only when a deployed Hero is in an
        // army standing ON the building's hex — position read via ArmyData.Hex, NOT a new
        // UnitData.Hex — and both hero.IsHero && hero.HasAbility(Researcher) and
        // building.HasFacilityWithAbility(Research) hold. Production uses the symmetric
        // Assembler + Production pair.
        //
        // Replaces the never-wired "Lab" tag: that string was a Facility *name*, not the
        // capability the next mechanic depends on, so it is not kept as a parallel synonym.
        public const string Research = "Research";
        public const string Researcher = "Researcher";
        public const string Production = "Production";
        public const string Assembler = "Assembler";
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
            CriticalDamage, CeramicArmor, Berserk, RapidReaction, ShockAttack, Hyperkinetic, Pyrokinetic,
            R1S0, R1S4, R1S5, R1S6, Stealth4, AntiAir, ApBonus,
            Barracks, Research, Researcher, Production, Assembler,
            CollectHuman, CollectEnergy, CollectMaterials, CollectTech,
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
