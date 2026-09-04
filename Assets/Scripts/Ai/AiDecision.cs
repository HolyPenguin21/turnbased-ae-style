using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Ai
{
    // A single executable AI action, produced by the V2 pipeline's executors and handed to
    // AiTurnController.MoveArmyRoutine. Historically this also carried the V1 arbiter's scoring
    // and dozens of category-specific factories; those went with V1 (ARCH-01). What remains is
    // the move-order data object plus its one factory the V2 executors use.
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

    public class AiDecision
    {
        public AiActionKind Kind;
        public ArmyData ExistingArmy;
        public HexCoord TargetHex;
        public CardData Card;
        public string Reason;
        public AiTaskCategory? Category;
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

        // Set whenever this decision advances/starts a persistent AiTask.
        public AiTask Task;

        public float Score;

        public bool IsRecoveryDraw;

        public AiTask PreemptedTask;
        public AiTask PreemptedHexTask;

        public static AiDecision Move(ArmyData army, HexCoord hex, string reason, AiTask task, float score, AiTaskCategory category) => new AiDecision
        {
            Kind = AiActionKind.MoveArmy, ExistingArmy = army, TargetHex = hex, Reason = reason, Task = task, Score = score, Category = category,
        };
    }
}
