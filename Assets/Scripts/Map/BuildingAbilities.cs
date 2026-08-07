namespace Game.Map
{
    // Open-ended building ability/tag names — plain strings rather than an enum, since there's
    // still only one real building (the citadel) and new abilities will keep getting added as
    // buildings do. CardDefinition.requiredBuildingAbility stores whichever one it needs by
    // name, matched against BuildingData.Abilities.
    public static class BuildingAbilities
    {
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
        public static bool IsFullCitadel(BuildingData building)
        {
            if (building == null)
                return false;
            foreach (string ability in CollectAbilities)
                if (!building.HasAbility(ability))
                    return false;
            return true;
        }
    }
}
