namespace Game.Cards
{
    // One step of a Base building's upgrade path (see GameConfig.baseUpgradeTiers) — the cost
    // to go from the tier before it to this one, and the Defense/Resistance it grants on top of
    // unlocking the next Facility slot. Index 0 in the array is the Level 1 -> 2 upgrade, and so
    // on; only 3 tiers exist this pass (see BuildingData.UnlockedFacilitySlots).
    [System.Serializable]
    public class BaseUpgradeTier
    {
        public int apCost;
        public ResourceCost cost = new ResourceCost();
        public int defenseGain = 1;
        public int resistanceGain = 1;
    }
}
