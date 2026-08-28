using System.Collections.Generic;
using Game.Economy;
using Game.Players;
using UnityEngine;

namespace Game.Cards
{
    // Static design of one card — a plain embedded record, not its own asset. Lives inline
    // inside a FactionCardCatalog's `cards` list (one file per faction, see
    // FactionCardCatalog) rather than one ScriptableObject asset per card, so editing a
    // faction's whole card set means opening one file instead of jumping between many. A
    // deck/hand can hold several CardData instances all pointing at the same definition
    // (multiple copies of the same card). Stats here are what CardHandUI/HexSelectionController
    // (once played) will check — playing the actual unit/hero/facility onto the map from a
    // card is still a stub (see CardHandUI.FinishDrop).
    [System.Serializable]
    public class CardDefinition
    {
        // This card's index within the owning FactionCardCatalog's cards list — cosmetic/
        // editor-facing only; a deck (see StartingDeckCatalog) references this card by its
        // "<catalog.displayName>/<displayName>" key instead, since that stays unambiguous
        // across catalogs. Auto-synced by FactionCardCatalog.OnValidate on every inspector
        // change; not hand-edited.
        [ReadOnly] public int id;

        public string displayName;
        public Sprite art;
        // Shown in a unit's detail panel (ArmyViewerModalUI/BattleScreenUI.ShowUnitDetail) —
        // deliberately separate from `art` above (the compact card's own thumbnail, see CardUI/
        // ArmyUnitCardUI) per the user's own request: the detail view wants its own dedicated
        // portrait, not a stretched-up copy of the small card art. Left unassigned, HexSelection
        // Controller.SpawnUnit falls back to `art` so existing cards don't just show blank.
        public Sprite detailArt;
        // Faction.None for cards that aren't any faction's own — e.g. GameConfig.
        // extractionFacilityCards, which every player can build regardless of faction.
        public Faction faction;
        public CardType cardType;

        [Header("Cost")]
        public int apCost;
        public ResourceCost resourceCost = new ResourceCost();

        // Which building ability (see Game.Cards.UnitAbilities) a hex needs before this card
        // can be deployed there — only checked for Hero/Unit cards (see CardHandUI.TryPlayCard).
        // Empty for Facility cards, which don't go through that flow yet.
        [Header("Deployment")]
        public string requiredBuildingAbility;

        // How far the spawned unit can move per turn — same meaning as UnitData.MoveMax, just
        // the card's own copy of it (CardHandUI.TryPlayCard hands this straight to
        // HexSelectionController.SpawnUnit). Only meaningful for Hero/Unit cards.
        [Header("Stats")]
        public int moveMax = 1;
        // AP cost of the spawned unit's first move order each turn (see
        // UnitData.ActivationApCost) — per-card, not shared: heavier units (tanks, etc.) cost
        // more to activate than light infantry.
        public int activationApCost = 1;
        // Only meaningful for CardType.Hero — how many army slots this hero unlocks when
        // present in an army (see ArmyData.Capacity, UnitData.CommandRating). Ignored for
        // Unit/Facility cards.
        public int commandRating = 2;
        // Same meaning as UnitData.Fate — only meaningful for CardType.Hero.
        public int fate;

        // Combat stats (see Game.Combat.ChallengeResolver) — only meaningful for Hero/Unit
        // cards. attack is this card's dice-pool size when it attacks a Ground-to-Ground
        // Challenge; range is how many rows ahead it can reach; hitPoints is how much damage it
        // can take before being destroyed.
        public int attack;
        // Named defenseRating rather than defense — this is UnitData.Defense: the defender's
        // dice-pool size in a Ground-to-Ground Challenge. A CardType.Base card reads the exact
        // same field for its spawned BuildingData.Defense (see HexSelectionController.
        // SpawnBuilding/CitadelSetupController.SpawnCitadelMarker) rather than keeping a
        // separate copy — Hero/Unit and Base cards share this one Stats block.
        public int defenseRating = 1;
        // Named resistanceRating for the same reason as defenseRating — shared with
        // BuildingData.Resistance for a Base card the same way.
        public int resistanceRating = 1;
        public int range = 1;
        public int hitPoints = 1;
        // Same meaning as UnitData.Initiative — carried straight through at spawn time like
        // every other stat above.
        public int initiative = 1;

        // Aircraft are still ordinary Unit cards, but cannot be mixed with ground troops or
        // heroes in one ArmyData.  The runtime copy on UnitData keeps per-card fuel/attack
        // state; these fields stay immutable card-design inputs.
        [Header("Aviation (Unit/Base only)")]
        public bool isAviation;
        public int launchEnergyCost;
        public int turnsWithoutRefuel;
        // Radius used when this card carries UnitAbilities.AntiAir.  Keeping its magnitude as a
        // number avoids inventing one ability tag per range and keeps the inspector dropdown
        // useful to content authors.
        public int antiAirRadius = 1;
        // A Barracks-capable Base with a positive value is also an airfield.  Kept on the Base
        // card so Citadel/Base capacities are data, not a hard-coded building-name table.
        public int airfieldCapacity;

        // Classification tags (Bio/Mechanical/Armored/...) — only meaningful for Hero/Unit
        // cards, and only ever shown in a unit's detail panel (ArmyViewerModalUI.
        // ShowUnitDetail), never on the compact card itself (unlike grantedAbilities, which
        // CardUI/ArmyUnitCardUI also show abbreviated). A List<UnitTypeTag> rather than
        // List<string> — see UnitTypeTag's own comment for why — so this shows as a per-entry
        // dropdown in the inspector instead of a free-text field. Carried into UnitData.
        // TypeTags at spawn time (see HexSelectionController.Factory.SpawnUnit). Only some tags
        // affect combat so far (see UnitAbilities.Hyperkinetic).
        [Header("Type Tags")]
        public List<UnitTypeTag> unitTypeTags = new List<UnitTypeTag>();

        // Ability tags — the only skill/ability list a card has (see Game.Cards.UnitAbilities
        // for the fixed-value ones this project actually gives combat effects to, and
        // Game.Cards.UnitAbilities for the Base-card ones like Barracks/Research/CollectX). Still
        // a List<string>, not an enum like unitTypeTags — the choices are UnitAbilityCatalog.
        // knownAbilities (tunable data, not compiled code) — but [AbilityTag] (see
        // Assets/Editor/AbilityTagDrawer.cs) gives it the same per-entry dropdown in the
        // inspector. Carried into UnitData.Abilities for Hero/Unit cards (see
        // HexSelectionController.Factory.SpawnUnit) or BuildingData.Abilities for Base cards
        // (see HexSelectionController.Factory.SpawnBuilding) at spawn time either way — same
        // field, no separate "passive skill" list any more.
        [Header("Abilities")]
        [AbilityTag]
        public List<string> grantedAbilities = new List<string>();

        // Only meaningful for CardType.Base — the BuildingData a Base card spawns (see
        // HexSelectionController.SpawnBuilding/CitadelSetupController.SpawnCitadelMarker, both
        // now read the same shared Stats block above: hitPoints/defenseRating/resistanceRating/
        // fate) instead of a separate Base-only copy of those same four numbers.
        //
        // Resource collection is skill/ability-driven now (see UnitAbilities.
        // CollectAbilities) — this isn't wired into BuildingData/spawn at all, kept only for a
        // future card design that grants a flat resource on its own.
        [Header("Resource Yield (CardType.Base only, not yet wired to gameplay)")]
        public ResourceYields resourceYield = new ResourceYields();

        // Only meaningful for CardType.Equipment — the modifier this card applies to whatever
        // it's hung on (see Game.Cards.EquipmentGrant / EquipmentSystem). Every Stats/Cost
        // field above still applies the normal way: apCost/resourceCost is what attaching it
        // costs, everything else (attack/defenseRating/moveMax/...) is ignored for Equipment,
        // same as it already is for Facility/Tactic.
        [Header("Equipment (CardType.Equipment only)")]
        public EquipmentGrant equipment = new EquipmentGrant();
    }
}
