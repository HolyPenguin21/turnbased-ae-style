using Game.Cards;
using Game.HexGrid;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MATERIALIZATION PLAN  (Strategy V2 — Strategic Manager, Step 8B)
    // ===========================================================================================
    //  ONE model of the COMPLETE action chain that produces a capability — never independent
    //  "is this generation good / is this equipment good / where do I put it" decisions. A chain
    //  is at most: one Research/Production generation step + one Equipment attachment + one final
    //  deployment. The four shapes Step 8B supports and nothing deeper (no recursive crafting, no
    //  multi-stage generation, no hero positioning):
    //
    //    Direct                 existing card -> deploy
    //    AttachDeploy           existing card + existing equipment -> attach -> deploy
    //    GenerateDeploy         R/P generate a deployable card -> deploy
    //    GenerateAttachDeploy   one generation step (the deployable OR the equipment) + one
    //                           attachment of the OTHER (existing) component -> deploy
    //
    //  StrategicManager scores the projected END RESULT of the whole chain against a Demand
    //  (Phase A) or FutureUtility (Phase B), reserves the whole chain's cost, then executes it
    //  through the canonical gameplay APIs via MaterializationExecutor.
    // ===========================================================================================
    public enum MaterializationChainKind
    {
        Direct,
        AttachDeploy,
        GenerateDeploy,
        GenerateAttachDeploy,
    }

    // One immediately-usable Research/Production generation step. It is a candidate ONLY when a
    // qualifying non-prisoner Researcher/Assembler Hero ALREADY stands on an own Facility hex this
    // turn — Step 8B adds no hero positioning and no multi-turn planning. The Challenge costs the
    // player NO action points (game rule); only its ResourceCost is charged. The Challenge is
    // probabilistic — a lost roll is a normal partial failure (MaterializationExecutor), not a
    // planner error.
    public sealed class GenerationStep
    {
        public ResearchProductionMode Mode;
        public HexCoord FacilityHex;
        public UnitData Hero;
        public CardDefinition CardDef;     // what a won Challenge mints
        public float SuccessChance;        // ResearchProductionSystem.EstimateSuccessChance
        public bool ProducesEquipment;     // CardDef.cardType == CardType.Equipment

        // Stable identity of the generator USE (one hero, one Facility, one mode) — a hero can run
        // exactly one Challenge per turn, so this is the limited resource claimed pass-locally and
        // the deterministic tie-break key, NOT the card.
        public string UseKey;
        public string CardKey;             // UseKey + card, for the "already attempted this pass" set
    }

    public sealed class MaterializationPlan
    {
        public MaterializationChainKind Kind;
        public DesireAxis? OwnerAxis;              // Phase A: the axis charged. null => Phase B surplus.
        public CapabilityKind FinalCapability;
        public TraitPreference ExpectedTraits;     // projected traits of the END result (only Stealth is proven)

        public GenerationStep Generation;          // null unless a Generate* kind

        // --- deploy host -----------------------------------------------------------------------
        //  Direct / AttachDeploy, and GenerateAttachDeploy where the GENERATED component is the
        //  equipment: BaseCardInHand is an existing hand card.
        //  GenerateDeploy, and GenerateAttachDeploy where the generated component is the
        //  DEPLOYABLE: GeneratedBaseDef is set and the real CardData is the mint result.
        public CardData BaseCardInHand;
        public CardDefinition GeneratedBaseDef;

        // --- optional equipment ------------------------------------------------------------
        //  AttachDeploy, and GenerateAttachDeploy where the generated component is the deployable:
        //  EquipmentInHand is an existing unattached CardType.Equipment card.
        //  GenerateAttachDeploy where the generated component IS the equipment: GeneratedEquipmentDef.
        public CardData EquipmentInHand;
        public CardDefinition GeneratedEquipmentDef;

        // --- deploy placement -----------------------------------------------------------
        //  For an existing base card this is a fully validated option. For a generated base it is
        //  the best option from a pre-mint enumeration; MaterializationExecutor re-validates it
        //  against the live world after minting and re-picks if it no longer holds.
        public PlacementOption Deploy;

        // --- whole-chain accounting ---------------------------------------------------
        public float ApCost;                      // CreateArmy + attach AP + deploy AP (generation adds 0 player AP)
        public ResourceCost ResCost;              // generation + attach + deploy resourceCost summed; null == none
        public int HandSlotsNeededAtPeak;         // free hand slots the chain needs at its most crowded moment (0 or 1)

        public float Score;                       // ranking value (Phase A) / FutureUtility (Phase B)
        public string StableKey;
        public string Explain;

        public bool UsesGenerator => Generation != null;
        public bool UsesEquipment => Kind == MaterializationChainKind.AttachDeploy
                                     || Kind == MaterializationChainKind.GenerateAttachDeploy;

        public override string ToString() => $"{Kind} {StableKey}";
    }

    public sealed class MaterializationResult
    {
        public bool StateChanged;
        public bool Deployed;
        public bool Generated;
        public bool Attached;
        public float ApSpent;                     // real PlayerRoot AP delta across the whole chain
        public string FailReason;

        // The generator USE that was attempted (win OR loss) — the pass must not retry it. null if
        // the chain had no generation step or never reached it.
        public string AttemptedGenerationUseKey;
    }
}
