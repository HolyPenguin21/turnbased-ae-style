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
        public string displayName;
        public Sprite art;
        // Faction.None for cards that aren't any faction's own — e.g. GameConfig.
        // extractionFacilityCards, which every player can build regardless of faction.
        public Faction faction;
        public CardType cardType;

        [Header("Cost")]
        public int apCost;
        public ResourceCost resourceCost = new ResourceCost();

        // Which building ability (see Game.Map.BuildingAbilities) a hex needs before this card
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
        // Named defenseRating, not defense — that name's already taken below by the unrelated
        // Base Stats section (a BUILDING's defense, e.g. the citadel's). This is
        // UnitData.Defense: the defender's dice-pool size in a Ground-to-Ground Challenge.
        public int defenseRating = 1;
        // Named resistanceRating for the same reason as defenseRating — `resistance` below is
        // the unrelated BUILDING stat.
        public int resistanceRating = 1;
        public int range = 1;
        public int hitPoints = 1;
        // Same meaning as UnitData.Initiative — carried straight through at spawn time like
        // every other stat above.
        public int initiative = 1;

        // Free-form ability tags — the only skill/ability list a card has (see Game.Cards.
        // UnitAbilities for the fixed-value ones this project actually gives combat effects to,
        // and Game.Map.BuildingAbilities for the Base-card ones like Barracks/Lab/CollectX).
        // Carried into UnitData.Abilities for Hero/Unit cards (see HexSelectionController.
        // Factory.SpawnUnit) or BuildingData.Abilities for Base cards (see
        // HexSelectionController.Factory.SpawnBuilding) at spawn time either way — same field,
        // no separate "passive skill" list any more.
        [Header("Abilities")]
        public List<string> grantedAbilities = new List<string>();

        // Only meaningful for CardType.Base — starting stats for the BuildingData a Base card
        // spawns (see HexSelectionController.SpawnBuilding).
        [Header("Base Stats (CardType.Base only)")]
        public int structurePointsMax = 6;
        public int defense = 2;
        public int resistance = 1;
        public ResourceYields resourceYield = new ResourceYields();
    }
}
