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

        // True ONLY for a CardData minted by a successful Research/Production Challenge (see
        // BattleAttackPopupUI.BeginResearchProduction and HexSelectionController's R/P
        // transaction). Its ResourceCost was already paid at Create time, so this instance must
        // never be charged ResourceCost a second time when it is finally played, and its
        // play-time AP cost is Definition.activationApCost rather than Definition.apCost.
        //
        // Instance-level on purpose: the same shared CardDefinition can be in a deck AND have
        // been produced through Research/Production, and the ordinary deck copy must keep its
        // normal cost behaviour. Never express this by mutating CardDefinition. Default false —
        // every starting-deck / drawn / event-reward / returned-aircraft CardData keeps the
        // exact 1:1 cost behaviour it always had.
        public bool ResearchProductionCreated;

        public CardData(CardDefinition definition)
        {
            Definition = definition;
        }

        // Play-time AP cost of THIS instance — activationApCost for a Research/Production card,
        // the definition's own apCost otherwise. RapidReaction's "deploy AP is 0" override for
        // Unit/Hero cards still layers on top of this in ArmyActions.EffectiveDeployApCost.
        public int EffectivePlayApCost =>
            Definition == null ? 0
            : ResearchProductionCreated ? Definition.activationApCost
            : Definition.apCost;

        // Play-time ResourceCost of THIS instance — null for a Research/Production card (already
        // paid at Create), the definition's own resourceCost otherwise. Callers treat null as
        // "nothing to check, nothing to charge".
        public ResourceCost EffectivePlayResourceCost =>
            ResearchProductionCreated ? null : (Definition != null ? Definition.resourceCost : null);
    }
}
