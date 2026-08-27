using System.Collections.Generic;

namespace Game.Cards
{
    // What kind of card an Equipment card (CardType.Equipment) may be hung on. Checked against
    // the target's own identity in EquipmentSystem.CanAttach — a live UnitData's IsHero, or a
    // not-yet-spawned CardData's CardDefinition.cardType. Facility is listed for the future
    // building-equipment branch (see the spec's "вне первого шага") — nothing hands a Facility
    // target to CanAttach yet.
    public enum EquipmentHostKind
    {
        Unit,
        Hero,
        Facility,
    }

    // Which UnitData stat an EquipmentStatChange touches. A deliberate subset — only fields it
    // makes sense to modify with gear (no MoveCurrent, no HitPointsCurrent on its own: HitPoints
    // moves both Max and Current together, see EquipmentSystem.Apply).
    public enum EquipmentStat
    {
        Attack,
        Defense,
        Resistance,
        Range,
        HitPoints,
        MoveMax,
        Initiative,
        ActivationApCost,
        CommandRating,
        Fate,
    }

    // A parameterized-ability "family" for the "strip whatever the host currently has, then add
    // the new one" case (the beacon example: a unit carrying r1s4 or r1s5 gets it cleared and
    // r1s6 added). Matched via Game.Cards.AbilityParams (TryGetRecce / TryGetStealthLevel) so
    // the tag grammar still lives in exactly one place. None = do nothing here, work only off
    // EquipmentGrant.removeAbilities' exact-tag list.
    public enum AbilityFamily
    {
        None,
        Recce,
        Stealth,
    }

    [System.Serializable]
    public class EquipmentStatChange
    {
        public EquipmentStat stat;
        // isOverride == false: add `amount` to the host's current value.
        // isOverride == true:  set the host's value to `amount` outright (e.g. Range -> 1 to
        //                      turn a ranged unit into a melee one).
        public int amount;
        public bool isOverride;
    }

    // The payload of a CardType.Equipment card (see CardDefinition.equipment) — how it changes
    // whatever it's attached to. Applied once, permanently, by EquipmentSystem.Apply; there is
    // no un-attach (the manual: "Once placed an attachment card can never be removed").
    //
    // Application order (EquipmentSystem.Apply):
    //   1. clearAbilityFamilies — remove every ability tag of each listed family from the host
    //   2. removeAbilities      — remove each listed exact tag
    //   3. addAbilities         — add each listed tag
    //   4. statChanges          — all additive (isOverride == false) first, then all overrides
    [System.Serializable]
    public class EquipmentGrant
    {
        [UnityEngine.Header("Who it fits")]
        // Empty = fits any host of an allowed hostKind. Otherwise ANY match: the host needs at
        // least one of these among its own UnitTypeTags (see EquipmentSystem.CanAttach).
        public List<UnitTypeTag> hostTypeTags = new List<UnitTypeTag>();
        public List<EquipmentHostKind> hostKinds = new List<EquipmentHostKind> { EquipmentHostKind.Unit };

        [UnityEngine.Header("Abilities")]
        public List<AbilityFamily> clearAbilityFamilies = new List<AbilityFamily>();
        [AbilityTag] public List<string> removeAbilities = new List<string>();
        [AbilityTag] public List<string> addAbilities = new List<string>();

        [UnityEngine.Header("Stats")]
        public List<EquipmentStatChange> statChanges = new List<EquipmentStatChange>();
    }
}
