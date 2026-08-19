using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Game.Cards
{
    // Single place to write a description for every ability tag in the game (see
    // Game.Cards.UnitAbilities — Base-card ones like Barracks/Base/Lab/CollectX and Hero/Unit
    // ones like RapidReaction alike) — plus the tunable magnitudes for the fixed-value combat
    // abilities below. A separate small asset (Assets/Cards/UnitAbilityCatalog.asset) rather
    // than folding these into GameConfig, same reasoning as FactionCardCatalog living on its
    // own: this is card/combat design data, tuned by editing one asset in the Cards folder.
    // knownAbilities' own tag field is never hand-typed (see SyncKnownAbilities) — only
    // description is — so it can never again drift from UnitAbilities the way it used to (a
    // tag typed with a stray space silently never matched the real ability effect's own check).
    // The actual effect for each tag still lives wherever that ability is implemented
    // (BuildingRegistry/CitadelSetupController for the Base ones, BattleAttackPopupUI/
    // BattleScreenUI.Combat.cs/CardHandUI/HexSelectionController.Factory for the Unit ones).
    // Referenced directly by BattleAttackPopupUI (same pattern as BattleScreenUI's own direct
    // FactionCardCatalog reference) — every numeric value below has a hardcoded fallback if
    // this asset isn't assigned, so the abilities still work with the manual's own default
    // numbers even before it's wired up.
    [CreateAssetMenu(fileName = "UnitAbilityCatalog", menuName = "Game/Unit Ability Catalog")]
    public class UnitAbilityCatalog : ScriptableObject
    {
        [System.Serializable]
        public class AbilityEntry
        {
            // Synced from UnitAbilities.All (see SyncKnownAbilities) — never hand-edited, so
            // this can never drift from the real ability tag the way a free-typed field could.
            [ReadOnly] public string tag;
            public string description;
        }

        [Header("Every ability tag in the game — tag is synced automatically (see UnitAbilities.All), only description is ever hand-edited")]
        public List<AbilityEntry> knownAbilities = new List<AbilityEntry>();

        // Keeps knownAbilities in exact 1:1 sync with UnitAbilities.All on every inspector
        // refresh: adds an entry for a newly added ability, drops one for a removed/renamed
        // tag, and otherwise leaves each entry's own hand-written description untouched.
        private void OnValidate()
        {
            var descriptionsByTag = new Dictionary<string, string>();
            if (knownAbilities != null)
                foreach (AbilityEntry entry in knownAbilities)
                    if (entry != null && !string.IsNullOrEmpty(entry.tag))
                        descriptionsByTag[entry.tag] = entry.description;

            knownAbilities = UnitAbilities.All
                .Select(tag => new AbilityEntry
                {
                    tag = tag,
                    description = descriptionsByTag.TryGetValue(tag, out string description) ? description : string.Empty,
                })
                .ToList();
        }

        // Looked up by GameConfig.FormatAbilitiesDetailed for a unit/building's detail-panel
        // ability list — null if this tag has no entry (or the entry's description is empty),
        // so callers can skip the "- description" line instead of showing a blank one.
        public string GetDescription(string tag)
        {
            if (knownAbilities != null)
                foreach (AbilityEntry entry in knownAbilities)
                    if (entry != null && entry.tag == tag && !string.IsNullOrEmpty(entry.description))
                        return entry.description;
            return null;
        }

        [Header("Critical Damage (x2) — UnitAbilities.CriticalDamage")]
        public float criticalDamageMultiplier = 2f;

        [Header("Ceramic Armor -1 — UnitAbilities.CeramicArmor")]
        public int ceramicArmorReduction = 1;

        [Header("Berserk — UnitAbilities.Berserk")]
        public int berserkAttackGain = 1;
        public int berserkDefenseLoss = 1;

        [Header("Hyperkinetic +2 vs Armored — UnitAbilities.Hyperkinetic")]
        public int hyperkineticBonusDamage = 2;

        [Header("Pyrokinetic +2 vs Bio — UnitAbilities.Pyrokinetic")]
        public int pyrokineticBonusDamage = 2;

        [Header("Recce — UnitAbilities.Recce")]
        // Extra hex steps an army with a Recce-tagged member sees beyond GameConfig.
        // armyVisionRadius — a single shared magnitude like every other ability above (see
        // ArmyData.HasRecce/VisionSystem.RecomputeFor), not per-card any more.
        public int recceRadius = 1;
        // Reserved for a future use, no gameplay effect yet — per the project owner's own call.
        public int recceStrength;
    }
}
