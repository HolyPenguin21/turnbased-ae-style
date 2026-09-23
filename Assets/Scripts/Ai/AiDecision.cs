using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Ai
{
    // A single executable AI action, produced by the V2 pipeline's executors and handed to
    // AiTurnController.MoveArmyRoutine. Post-ARCH-01 this is the move-order data object and its
    // one factory the V2 executors use — no scoring, no legacy task references.
    public enum AiActionKind
    {
        MoveArmy,
        PlayCard,
        PlayFacilityCard,
        AttachEquipment,
        ReserveArmy,
        DrawCard,
        BuildFacility,
        RepairUnit,
        SplitGarrisonArmy,
        CollapseAssembly,
        ConsolidateUnits,
        ConsolidateSwap,
        DetachCollector,
        SpawnReconArmy,
        AssembleRecceScout,
        RequestRaidArmy,
        AssembleRaidForce,
        DispatchReinforcement,
        ReinforceSwap,
        RequestDefendArmy,
        ActiveDefenceForce,
        StrengthenDefenceForce,
        BuildBase,
        SeedNewBaseGarrison,
        DispatchBaseReinforcement,
        DepositReinforcement,
        LaunchAirStrike,
        LaunchAirRecon,
        ExecuteAirStrikeAtCurrentHex,
        RunResearchProduction,
        Wait,
        Pass,
    }

    // Typed authorization for the side effects a ground move is deliberately allowed to seek.
    // This is AI intent only: the gameplay movement owner still resolves surprise contacts found
    // after entering fog exactly as the normal game does. Never infer this from Reason text.
    public enum AiGroundMoveAuthority
    {
        Transit,
        Combat,
        CombatAndCapture,
    }

    // ATK §26/§84 — the ONE rule for which authority a single step of a deliberate
    // structure-capture operation carries. Only the TERMINAL step into the target may seek a
    // takeover; every approach step is ordinary Transit, so an operation can never capture some
    // other structure it happens to walk across on the way (§27). Kept beside the enum it decides
    // because it is part of that contract, not a per-lane preference.
    public static class GroundMoveAuthorityPolicy
    {
        public static AiGroundMoveAuthority ForStructureAssaultStep(
            Game.HexGrid.HexCoord step, Game.HexGrid.HexCoord target) =>
            step.Equals(target)
                ? AiGroundMoveAuthority.CombatAndCapture
                : AiGroundMoveAuthority.Transit;
    }

    public class AiDecision
    {
        public AiActionKind Kind;
        public ArmyData ExistingArmy;
        public HexCoord TargetHex;
        public CardData Card;
        public string Reason;
        public IReadOnlyList<UnitData> UnitsToMove;
        public UnitData CollectorUnit;
        public ArmyData MergeTarget;
        public CardData EquipmentHostCard;
        public HexCoord? EconomyBuildHex;
        public ResourceType? EconomyResourceType;
        public IReadOnlyList<UnitData> AircraftToLaunch;
        public HexCoord AirActionHex;
        public HexCoord AirLandingHex;
        public UnitData DevelopHero;
        public ResearchProductionMode DevelopMode;
        public CardDefinition DevelopCard;

        public float Score;

        public bool IsRecoveryDraw;

        // Recon may already have prepared visible combat or budgeted optional/required stealth.
        // The shared mover must not override that decision. Other callers retain their existing
        // automatic preparation unless they explicitly opt out.
        public bool AllowAutomaticStealth = true;

        // Transit is deliberately the safe default: a caller must opt into deliberate combat or
        // an undefended-structure takeover. Air movement ignores this ground-only authorization.
        public AiGroundMoveAuthority GroundMoveAuthority = AiGroundMoveAuthority.Transit;
        public bool AllowsGroundCombat => GroundMoveAuthority != AiGroundMoveAuthority.Transit;
        public bool AllowsStructureTakeover => GroundMoveAuthority == AiGroundMoveAuthority.CombatAndCapture;

        public static AiDecision Move(ArmyData army, HexCoord hex, string reason, float score,
            AiGroundMoveAuthority groundMoveAuthority = AiGroundMoveAuthority.Transit) => new AiDecision
        {
            Kind = AiActionKind.MoveArmy, ExistingArmy = army, TargetHex = hex, Reason = reason, Score = score,
            GroundMoveAuthority = groundMoveAuthority,
        };
    }
}
