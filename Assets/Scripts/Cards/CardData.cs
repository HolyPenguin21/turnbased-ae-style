namespace Game.Cards
{
    // One card instance currently in a player's hand — just which definition it is. Same
    // data/visual split used everywhere else in this project (ArmyData vs ArmyController,
    // PlayerSetupData vs PlayerRoot): CardUI is the visual, this is what it represents.
    public class CardData
    {
        public CardDefinition Definition;

        // A CardType.Equipment card attached to THIS Unit/Hero card while it's still in hand
        // (see EquipmentSystem / the attach flow in CardHandUI) — one slot, per the project
        // owner's own call. Null until something's attached. Carried onto the spawned UnitData
        // (and applied to its stats/abilities) when this card is finally deployed — see
        // ArmyActions.DeployUnitFromCard. Meaningless on an Equipment card's own CardData.
        public CardDefinition Equipment;

        public CardData(CardDefinition definition)
        {
            Definition = definition;
        }
    }
}
