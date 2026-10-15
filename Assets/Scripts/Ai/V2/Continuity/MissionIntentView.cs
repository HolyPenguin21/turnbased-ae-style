using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  The read-only face of the owner's durable intent store (MissionIntentState), handed to the
    //  planning layers and Analysis. It has no Put/Remove, no witness or lifecycle writers: a
    //  planning decision that needs to change the store cannot compile against it and reports the
    //  change instead (RetreatWitnessRevision). The owners that write — Continuity, Strategy
    //  Phase A transactions — receive the concrete MissionIntentState.
    //
    //  The intents themselves stay mutable objects owned by Continuity; the view does not promise
    //  deep immutability, only that the store is not changed through it. It is a live view: the
    //  same store, not a copy.
    // ===========================================================================================
    internal interface IMissionIntentView
    {
        IReadOnlyCollection<MissionIntent> All { get; }
        bool TryGet(MissionIntentKey k, out MissionIntent i);
        bool TryGetRetreatWitness(AttackTargetRef target, out AttackRetreatWitness witness);
        string RetreatWitnessDigest();
        IEconomyLifecycleView Economy { get; }
        IDevelopmentLifecycleView Development { get; }
    }

    internal interface IEconomyLifecycleView
    {
        bool IsBaseExpansionDeliverySuppressed(int turn, CardData card, HexCoord? target);
        bool IsExtractionDeliverySuppressed(int turn, ResourceType? resourceType, HexCoord target);
    }

    internal interface IDevelopmentLifecycleView
    {
        bool CanUseGeneratedOperatorAt(CardData card, HexCoord site, ResearchProductionMode mode, int turn);
        CardData GeneratedOperatorFor(HexCoord site, ResearchProductionMode mode, int turn);
        string GeneratedOperatorFacts(int turn);
    }
}
