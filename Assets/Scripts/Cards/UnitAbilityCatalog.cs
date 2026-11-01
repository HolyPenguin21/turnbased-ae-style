using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using UnityEngine;

namespace Game.Cards
{
    // Single place for every ability tag's display text — card abbreviation and detail-panel
    // description (see Game.Cards.UnitAbilities — Base-card ones like Barracks/Research/CollectX
    // and Hero/Unit ones like RapidReaction alike) — plus the tunable magnitudes for the
    // fixed-value combat abilities below. A separate small asset (Assets/Cards/
    // UnitAbilityCatalog.asset) rather than folding these into GameConfig, same reasoning as
    // FactionCardCatalog living on its own: this is card/combat design data, tuned by editing
    // one asset in the Cards folder. knownAbilities' own tag/title fields are never hand-typed
    // (see OnValidate) — only abbreviation/description are — so they can never again drift from
    // UnitAbilities the way they used to (a tag typed with a stray space silently never matched
    // the real ability effect's own check).
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
        // The authored combat catalog is a single project asset. Strategic combat estimators do
        // not own UI objects, so expose the loaded asset itself rather than duplicating summon
        // stats/templates in AI code. Battle UI still keeps its serialized reference as before.
        public static UnitAbilityCatalog Active { get; private set; }

        private void OnEnable()
        {
            Active = this;
        }

        private void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        [System.Serializable]
        public class AbilityEntry
        {
            // Synced from UnitAbilities.All (see OnValidate) — never hand-edited, so neither can
            // drift from the real ability tag the way a free-typed field could.
            [ReadOnly] public string tag;
            [ReadOnly] public string title;
            // Short form shown on the compact card itself; empty falls back to title.
            public string abbreviation;
            // Shown under the title in detail panels (see FormatAbilitiesDetailed).
            [TextArea(2, 5)] public string description;
        }

        [Header("Every ability tag in the game - tag/title are synced automatically (see UnitAbilities.All)")]
        public List<AbilityEntry> knownAbilities = new List<AbilityEntry>();

        // Keeps knownAbilities in exact 1:1 sync with UnitAbilities.All on every inspector
        // refresh, sorted by title: adds an entry for a newly added ability, drops one for a
        // removed/renamed tag, and otherwise keeps each entry's hand-written abbreviation and
        // description untouched.
        private void OnValidate()
        {
            var entriesByTag = new Dictionary<string, AbilityEntry>();
            if (knownAbilities != null)
                foreach (AbilityEntry entry in knownAbilities)
                    if (entry != null && !string.IsNullOrEmpty(entry.tag))
                        entriesByTag[entry.tag] = entry;

            knownAbilities = UnitAbilities.All
                .Select(tag =>
                {
                    entriesByTag.TryGetValue(tag, out AbilityEntry old);
                    return new AbilityEntry
                    {
                        tag = tag,
                        title = UnitAbilities.PrettyName(tag),
                        abbreviation = old?.abbreviation ?? string.Empty,
                        description = old?.description ?? string.Empty,
                    };
                })
                .OrderBy(entry => entry.title, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private AbilityEntry Find(string tag)
        {
            if (knownAbilities != null)
                foreach (AbilityEntry entry in knownAbilities)
                    if (entry != null && entry.tag == tag)
                        return entry;
            return null;
        }

        // Full display name, derived from the tag itself (see UnitAbilities.PrettyName).
        public static string GetTitle(string tag) => UnitAbilities.PrettyName(tag);

        // Short card form; falls back to the title when no abbreviation is authored.
        public string GetAbbreviation(string tag)
        {
            AbilityEntry entry = Find(tag);
            return entry != null && !string.IsNullOrEmpty(entry.abbreviation) ? entry.abbreviation : GetTitle(tag);
        }

        // Null when the tag has no entry or an empty description, so callers skip the indented
        // description line instead of showing a blank one.
        public string GetDescription(string tag)
        {
            AbilityEntry entry = Find(tag);
            return entry != null && !string.IsNullOrEmpty(entry.description) ? entry.description : null;
        }

        // Compact card form (CardUI, ArmyUnitCardUI, BaseSlotCardUI, BattleGridCellUI, ...):
        // space-separated abbreviations, empty string for a null set.
        public string FormatAbilities(IEnumerable<string> abilities)
            => abilities == null ? string.Empty : string.Join(" ", abilities.Select(GetAbbreviation));

        // Detail-panel form: each ability is preceded by an empty line, then its title, then
        // "  description" when one is authored.
        public string FormatAbilitiesDetailed(IEnumerable<string> abilities)
        {
            if (abilities == null)
                return string.Empty;
            var parts = new List<string>();
            foreach (string ability in abilities)
            {
                string description = GetDescription(ability);
                parts.Add(description == null ? $"\n{GetTitle(ability)}" : $"\n{GetTitle(ability)}\n  {description}");
            }
            return string.Join("\n", parts);
        }

        [Header("Critical Damage (x2) - UnitAbilities.CriticalDamage")]
        public float criticalDamageMultiplier = 2f;

        [Header("Ceramic Armor -1 - UnitAbilities.CeramicArmor")]
        public int ceramicArmorReduction = 1;

        [Header("Berserk - UnitAbilities.Berserk")]
        public int berserkAttackGain = 1;
        public int berserkDefenseLoss = 1;

        [Header("Hyperkinetic +2 vs Armored - UnitAbilities.Hyperkinetic")]
        public int hyperkineticBonusDamage = 2;

        [Header("Pyrokinetic +2 vs Bio - UnitAbilities.Pyrokinetic")]
        public int pyrokineticBonusDamage = 2;

        [Header("Ionic +2 vs Mechanical - UnitAbilities.Ionic")]
        public int ionicBonusDamage = 2;

        [Header("Drill - UnitAbilities.Drill (Attack for one round)")]
        public int drillAttackBonus = 1;

        [Header("Raise The Rots - UnitAbilities.RaiseTheRots")]
        // ANY faction's card catalog — the Neutral one, a faction's own, another faction's,
        // whatever — and the card within it, matched by CardDefinition.authoredKey (falling back
        // to displayName). See FactionCardCatalog.ResolveCard.
        public FactionCardCatalog raiseTheRotsUnitCatalog;
        public string raiseTheRotsUnitKey;
        // How many copies EACH carrier of UnitAbilities.RaiseTheRots on a side summons at the
        // start of a battle (capped by the free space actually left on that side of the grid).
        public int raiseTheRotsUnitsPerSummoner = 2;

        // Canonical authored combat magnitudes. Every live battle/simulation/strategic estimate
        // should consume this bundle rather than re-reading the individual fields independently.
        public AbilityMagnitudes Magnitudes => new AbilityMagnitudes(
            criticalDamageMultiplier, hyperkineticBonusDamage, ceramicArmorReduction,
            pyrokineticBonusDamage, berserkAttackGain, berserkDefenseLoss,
            ionicBonusDamage, drillAttackBonus);

        // Null if the catalog isn't wired or the key no longer resolves — BattleScreenUI just
        // logs and skips the summon in that case.
        public CardDefinition ResolveRaiseTheRotsCard() =>
            raiseTheRotsUnitCatalog != null ? raiseTheRotsUnitCatalog.ResolveCard(raiseTheRotsUnitKey) : null;

        // Recce is parameterized now (r1s0/r1s4/r1s5/r1s6) — its radius/spot-strength live
        // in the tag string itself and are read via Game.Cards.AbilityParams, so there is no
        // shared magnitude field here any more. Stealth4's hide tokens are likewise encoded in
        // the tag. knownAbilities above still carries a hand-written abbreviation/description for each.
    }
}
