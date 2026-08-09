using System.Collections.Generic;
using UnityEngine;

namespace Game.Cards
{
    // Single reference list of every ability tag a card's grantedAbilities can carry — both the
    // Base-card ones already implemented via Game.Map.BuildingAbilities (Barracks/Base/Lab/
    // CollectX) and the Hero/Unit ones in Game.Cards.UnitAbilities — plus the tunable magnitudes
    // for the fixed-value combat abilities. A separate small asset (Assets/Cards/
    // UnitAbilityCatalog.asset) rather than folding these into GameConfig, same reasoning as
    // FactionCardCatalog living on its own: this is card/combat design data, tuned by editing
    // one asset in the Cards folder. knownAbilities itself is documentation/reference only (an
    // Inspector cheat-sheet for whoever's typing tags into a CardDefinition's grantedAbilities
    // list) — the actual effect for each tag still lives wherever that ability is implemented
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
            public string tag;
            public string description;
        }

        [Header("Every ability tag in the game (reference only — see the tag's own const-string source for the real value)")]
        public List<AbilityEntry> knownAbilities = new List<AbilityEntry>();

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
    }
}
