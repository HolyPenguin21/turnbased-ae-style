namespace Game.Cards
{
    // One card instance currently in a player's hand — just which definition it is. Same
    // data/visual split used everywhere else in this project (ArmyData vs ArmyController,
    // PlayerSetupData vs PlayerRoot): CardUI is the visual, this is what it represents.
    public class CardData
    {
        public CardDefinition Definition;

        public CardData(CardDefinition definition)
        {
            Definition = definition;
        }
    }
}
