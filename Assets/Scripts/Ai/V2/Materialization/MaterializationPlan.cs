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

        // Deterministic actor/facility/mode prefix used for diagnostics and CardKey construction.
        // It is NOT a gameplay "one Challenge per hero" lock. The exact attempted combination is
        // CardKey below, matching V1's (hero, mode, card) retry semantics.
        public string UseKey;
        public string CardKey;             // UseKey + card, the exact "already attempted" identity

        // AI-MGR-02 §P1.4 — the ResourceCost a won/lost Challenge actually charges pre-mint (the
        // minted card's own EffectivePlayResourceCost is null once ResearchProductionCreated).
        public ResourceCost GenerationResourceCost => CardDef != null ? CardDef.resourceCost : null;
    }

    public sealed class MaterializationPlan
    {
        public MaterializationChainKind Kind;
        public DesireAxis? OwnerAxis;              // Phase A: the axis charged. null => Phase B surplus.
        public CapabilityKind FinalCapability;
        public TraitPreference ExpectedTraits;     // projected traits of the END result (only Stealth is proven)

        // Projected granted-ability set of the END result (base grantedAbilities + any equipment
        // grant). Transient — kept only so CapabilityQualityEvaluator can read Recce radius / spot
        // strength off the same list the feasibility gate used, without re-projecting equipment.
        public System.Collections.Generic.IReadOnlyList<string> ProjectedAbilities;

        // Diagnostic-only capability-quality decomposition, populated during scoring when the
        // final capability has a quality profile (Scout today). Never persisted or fed back.
        public MaterializationQualityBreakdown QualityBreakdown;

        // Diagnostic-only Card x IntendedUse decomposition from StrategicCardEvaluator (AI-MGR-01).
        // Populated during scoring for the winning candidate; never persisted or fed back — Score
        // stays the single authoritative number.
        public StrategicUseScoreBreakdown UseBreakdown;
        public IntendedRole? UseRole;

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

        // --- whole-chain accounting (ARCH-02 §13 — the canonical StrategicActionCost) -------------
        //  One cost description per plan, consumed identically by Phase A, Phase B, the reaction
        //  closure and the portfolio solver. AP = ApCost; Human/Energy/Materials/Tech = ResCost;
        //  GenerationAttempts = (Generation != null ? 1 : 0); HandSlotPeak = HandSlotsNeededAtPeak.
        //  No layer recomputes a "slightly different" cost of its own.
        public float ApCost;                      // CreateArmy + attach AP + deploy AP (generation adds 0 player AP)
        public ResourceCost ResCost;              // generation + attach + deploy resourceCost summed; null == none
        public int HandSlotsNeededAtPeak;         // free hand slots the chain needs at its most crowded moment (0 or 1)

        // AI-MGR-01 P1.4 — a plain field: the authoritative strategic score, set once by
        // StrategicCardEvaluator (which owns the Phase-B garrison-surplus correction via
        // SurplusPlacementBonus). No caller re-adjusts it on read.
        public float Score;
        public string StableKey;
        public string Explain;

        public bool UsesGenerator => Generation != null;
        public bool UsesEquipment => Kind == MaterializationChainKind.AttachDeploy
                                     || Kind == MaterializationChainKind.GenerateAttachDeploy;

        public override string ToString() => $"{Kind} {StableKey}";
    }

    public sealed class MaterializationResult : IV2ActionResult
    {
        public bool StateChanged;
        public bool Deployed;
        public bool Generated;
        public bool Attached;
        public bool ArmyCreated;                  // a new army shell was founded by this chain
        public float ApSpent;                     // real PlayerRoot AP delta across the whole chain
        public ResourceCost ResourcesSpent;       // real H/E/M/T delta across the whole chain (null = none)
        // ARCH-02 §35 — set when plan.Deploy no longer preflights at execution time. The chain did
        // NOT deploy and the executor did NOT substitute a placement; the caller must refresh the
        // world and replan (do not treat the demand as structurally blocked).
        public bool PlacementStale;
        public int StateVersionAfter = -1;
        public string FailReason;

        // Diagnostic identity of the actor/facility/mode prefix reached by the attempt. Exact retry
        // suppression is keyed by GenerationStep.CardKey, not by this prefix.
        public string AttemptedGenerationUseKey;

        public V2ActionOutcome Outcome => new V2ActionOutcome(
            succeeded: Deployed, stateChanged: StateChanged, apSpent: ApSpent,
            resourcesSpent: ResourcesSpent, played: Deployed, generated: Generated, attached: Attached,
            moved: false, created: ArmyCreated, needsReplan: PlacementStale,
            stateVersionAfter: StateVersionAfter, failReason: Deployed ? null : FailReason);
    }
}
