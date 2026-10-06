using System.Threading;

namespace Game.Cards
{
    // One card instance currently in a player's hand — just which definition it is. Same
    // data/visual split used everywhere else in this project (ArmyData vs ArmyController,
    // PlayerSetupData vs PlayerRoot): CardUI is the visual, this is what it represents.
    public class CardData
    {
        // Runtime identity, like UnitData.RuntimeId: preserved when hand order changes.
        private static int _nextRuntimeId;
        public readonly int RuntimeId = Interlocked.Increment(ref _nextRuntimeId);

        public CardDefinition Definition;

        // Independent permanent attachments carried onto UnitData at deployment. Both kinds
        // remain CardType.Equipment; the attachment definition selects its destination slot.
        public CardDefinition Equipment;
        public CardDefinition Mutator;

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

        // Owner turn on which this instance last entered an AI hand (draw, event grant, returned
        // aircraft, Research/Production mint) — stamped by AiHandData.AddCard from the turn the
        // owner pushed in via AiHandData.SetCurrentTurn. Lets any scorer read "how long has this
        // card been sitting" off the instance itself (the immutable snapshot already carries the
        // hand's CardData), never off the live hand registry. -1 = never stamped (human hand,
        // test fixtures): age reads as 0.
        public int AcquiredTurn = -1;

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
