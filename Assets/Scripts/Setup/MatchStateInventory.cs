using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Combat;
using Game.Map;
using Game.Turns;

namespace Game.Setup
{
    // What a save/load must do with one holder of static state.
    public enum MatchStateKind
    {
        World,            // authoritative match state: written by a save, restored by a load
        AiMemory,         // AI knowledge and commitments carried between turns: written by a save
        IdentitySequence, // runtime id counter: restored above the highest loaded id
        TurnScoped,       // lives inside one turn; empty at a between-turn save point
        Scoped,           // bound to an explicit scope object (snapshot, proposal, using-block)
        Cache,            // derived and self-invalidating: never saved
        Wiring,           // providers, catalogs and toggles set up by their owners
        Diagnostics,      // logs and telemetry with their own lifecycle
        Presentation,     // UI and visual singletons
        Profile,          // outside the match (collection, campaign), persisted by their own stores
        Constant,         // a lookup table built once; holds no state
    }

    public sealed class MatchStateEntry
    {
        public string TypeName { get; }
        public MatchStateKind Kind { get; }
        // Clears the holder for a new match. Null when ResetNote names who does it instead.
        public Action Reset { get; }
        public string ResetNote { get; }

        internal MatchStateEntry(string typeName, MatchStateKind kind, Action reset = null, string resetNote = null)
        {
            TypeName = typeName;
            Kind = kind;
            Reset = reset;
            ResetNote = resetNote;
        }

        // A new match must start from an empty holder of these kinds.
        public bool RequiresMatchReset =>
            Kind == MatchStateKind.World || Kind == MatchStateKind.AiMemory || Kind == MatchStateKind.TurnScoped;
    }

    // ===========================================================================================
    //  MATCH STATE INVENTORY - every type with static state, classified once.
    // ===========================================================================================
    //  The checklist a save/load feature works from (World + AiMemory + IdentitySequence are what a
    //  save writes) and the one place a new match is reset. MatchStateInventoryTests fails when a
    //  type gains static state without an entry here, or an entry outlives its state.
    //  Entries with a Reset run in table order (the order the setup scene always used); wiring
    //  that needs the live map or config (VisionSystem.Configure, StealthSystem providers,
    //  AiMapMemory.EnsureSubscribed) is done by CitadelSetupController right after.
    // ===========================================================================================
    public static class MatchStateInventory
    {
        private const string ByVisionClear = "VisionSystem.Clear";
        private const string ByReconClear = "AiReconMemory.Clear";
        private const string ByIntentClear = "MissionIntentRegistry.Clear";

        public static readonly IReadOnlyList<MatchStateEntry> All = new[]
        {
            // --- world ---------------------------------------------------------------------------
            World("Game.Map.BuildingRegistry", BuildingRegistry.Clear),
            World("Game.Map.HexResourceBonusRegistry", HexResourceBonusRegistry.Clear),
            World("Game.Map.HexEventRegistry", HexEventRegistry.Clear),
            World("Game.Map.ArmyRegistry", ArmyRegistry.Clear),
            World("Game.Map.PlayerRootRegistry", PlayerRootRegistry.Clear),
            Turn("Game.Aviation.AntiAirState", AntiAirState.Clear),
            // Coordinate-keyed: a leftover entry would report a pending battle at that hex in a
            // brand new game and be drained against the previous session's armies.
            Turn("Game.Combat.DelayedBattleRegistry", DelayedBattleRegistry.Clear),
            World("Game.Ai.AiHandRegistry", AiHandRegistry.Clear),
            World("Game.Map.VisionSystem", VisionSystem.Clear),
            new MatchStateEntry("Game.Map.HumanVisualMemory", MatchStateKind.World, resetNote: ByVisionClear),
            World("Game.Map.StealthSystem", StealthSystem.Clear),
            World("Game.Turns.InitiativePublicHistory", InitiativePublicHistory.Clear),
            new MatchStateEntry("Game.Core.GameSession", MatchStateKind.World,
                resetNote: "GameSession.TryPrepareMatch sets the players and match identity"),

            // --- AI memory carried between turns ------------------------------------------------
            Memory("Game.Ai.AiMapMemory", AiMapMemory.Clear),
            Memory("Game.Ai.V2.AirSortieRegistry", AirSortieRegistry.Clear),
            Memory("Game.Ai.V2.AiRadarStateRegistry", AiRadarStateRegistry.Clear),
            Memory("Game.Ai.V2.ForceBaselineRegistry", ForceBaselineRegistry.Clear),
            Memory("Game.Ai.V2.PreparationDeliveryMemory", PreparationDeliveryMemory.Clear),
            Memory("Game.Ai.V2.AiReconMemory", AiReconMemory.Clear),
            new MatchStateEntry("Game.Ai.V2.AiReconIntelMemory", MatchStateKind.AiMemory, resetNote: ByReconClear),
            new MatchStateEntry("Game.Ai.V2.ReconIntelSnapshotRegistry", MatchStateKind.AiMemory, resetNote: ByReconClear),
            new MatchStateEntry("Game.Ai.V2.ReconPatrolStateRegistry", MatchStateKind.AiMemory, resetNote: ByReconClear),
            new MatchStateEntry("Game.Ai.V2.ReconAirSortieRegistry", MatchStateKind.AiMemory, resetNote: ByReconClear),
            new MatchStateEntry("Game.Ai.V2.AirReconCoverageRegistry", MatchStateKind.AiMemory, resetNote: ByReconClear),
            new MatchStateEntry("Game.Ai.V2.ReconCapacityDeficitRegistry", MatchStateKind.AiMemory, resetNote: ByReconClear),
            new MatchStateEntry("Game.Ai.V2.ScoutTrailRegistry", MatchStateKind.AiMemory, resetNote: ByReconClear),
            Memory("Game.Ai.V2.ResourceStarvationRegistry", ResourceStarvationRegistry.Clear),
            Memory("Game.Ai.V2.DevelopmentInvestmentGate", DevelopmentInvestmentGate.Clear),
            Memory("Game.Ai.V2.AiAllocatorStateRegistry", AiAllocatorStateRegistry.Clear),
            Memory("Game.Ai.V2.MissionIntentRegistry", MissionIntentRegistry.Clear),
            Memory("Game.Ai.V2.Initiative.InitiativeAnalyticsHistory", Game.Ai.V2.Initiative.InitiativeAnalyticsHistory.Clear),
            // Mobilization hold spans turns; the settled/open stamps are turn-local.
            Memory("Game.Ai.V2.OperationContinuationWindow", OperationContinuationWindow.ClearAll),
            Memory("Game.Ai.V2.DevelopmentDiversity", DevelopmentDiversity.ClearAll),
            Memory("Game.Ai.V2.ApTurnPressure", ApTurnPressure.ClearAll),
            Memory("Game.Ai.V2.LifecycleReturnPolicy", LifecycleReturnPolicy.ClearAll),

            // --- inside one turn -----------------------------------------------------------------
            Turn("Game.Ai.V2.StrategicCapabilityLeaseRegistry", StrategicCapabilityLeaseRegistry.ClearAll),
            Turn("Game.Ai.V2.AviationObligationStallRegistry", AviationObligationStallRegistry.Clear),
            Turn("Game.Ai.V2.CapabilityPoolExhaustionRegistry", CapabilityPoolExhaustionRegistry.Clear),
            Turn("Game.Ai.V2.StrategicInterruptRegistry", StrategicInterruptRegistry.ClearAll),
            Turn("Game.Ai.V2.StrategicResourceReservationLedger", StrategicResourceReservationLedger.ClearAll),
            Turn("Game.Ai.V2.ReservationInvariants", ReservationInvariants.ClearAll),
            Turn("Game.Ai.V2.StrategicTempoBudget", StrategicTempoBudget.ClearAll),
            new MatchStateEntry("Game.Ai.V2.AiTurnSession", MatchStateKind.TurnScoped, resetNote: ByIntentClear),
            new MatchStateEntry("Game.Ai.V2.ReconTurnStateStore", MatchStateKind.TurnScoped, resetNote: ByIntentClear),
            new MatchStateEntry("Game.Ai.V2.WorldDeltaLifecycle", MatchStateKind.TurnScoped,
                resetNote: "monotonic revision counters; the mutation transaction closes with its step"),
            new MatchStateEntry("Game.Ai.V2.GroundCombatAdmissionRegistry", MatchStateKind.Scoped),
            new MatchStateEntry("Game.Ai.V2.StrategicCardEvaluator.EquipmentTargetMemo", MatchStateKind.Scoped),
            new MatchStateEntry("Game.Ai.V2.DevelopmentOpportunityEvaluator.RecipientEvaluationMemo", MatchStateKind.Scoped),

            // --- identities ------------------------------------------------------------------------
            new MatchStateEntry("Game.Map.ArmyData", MatchStateKind.IdentitySequence, resetNote: "ArmyRegistry.Clear"),
            new MatchStateEntry("Game.Units.UnitData", MatchStateKind.IdentitySequence),
            new MatchStateEntry("Game.Cards.CardData", MatchStateKind.IdentitySequence),

            // --- diagnostics -----------------------------------------------------------------------
            new MatchStateEntry("Game.Ai.V2.V2TurnActivityTelemetry", MatchStateKind.Diagnostics, V2TurnActivityTelemetry.Clear),
            new MatchStateEntry("Game.Ai.V2.AiV2Trace", MatchStateKind.Diagnostics, AiV2Trace.Clear),
            new MatchStateEntry("Game.Ai.V2.ApBudgetTelemetry", MatchStateKind.Diagnostics, ApBudgetTelemetry.ClearAll),
            new MatchStateEntry("Game.Ai.V2.DevelopmentOutcomeTelemetry", MatchStateKind.Diagnostics, DevelopmentOutcomeTelemetry.ClearAll),
            new MatchStateEntry("Game.Ai.V2.ReconAcceptanceAudit", MatchStateKind.Diagnostics, resetNote: ByReconClear),
            new MatchStateEntry("Game.Ai.AiDebugLog", MatchStateKind.Diagnostics),
            new MatchStateEntry("Game.Ai.AiMatchStats", MatchStateKind.Diagnostics),
            new MatchStateEntry("Game.Combat.BattleDebugLog", MatchStateKind.Diagnostics),

            // --- caches ----------------------------------------------------------------------------
            new MatchStateEntry("Game.Ai.SafeStepPathing", MatchStateKind.Cache),
            new MatchStateEntry("Game.Ai.V2.WorldAnalysis", MatchStateKind.Cache),
            new MatchStateEntry("Game.Ai.V2.CombatOpportunityAnalyzer", MatchStateKind.Cache),
            new MatchStateEntry("Game.Ai.V2.ForceNeedModel", MatchStateKind.Cache),
            new MatchStateEntry("Game.Ai.V2.DemandLayer", MatchStateKind.Cache),
            new MatchStateEntry("Game.Ai.V2.EquipmentEfficiency", MatchStateKind.Cache),
            new MatchStateEntry("Game.Ai.V2.Initiative.InitiativeOutcomeEvaluator", MatchStateKind.Cache),
            new MatchStateEntry("Game.Combat.WorthIt", MatchStateKind.Cache),
            new MatchStateEntry("Game.Combat.AviationCombatEstimator", MatchStateKind.Cache),

            // --- wiring, presentation, profile, constants -------------------------------------------
            new MatchStateEntry("Game.Ai.V2.AiConfigV2", MatchStateKind.Wiring),
            new MatchStateEntry("Game.Aviation.AviationRules", MatchStateKind.Wiring),
            new MatchStateEntry("Game.Cards.UnitAbilityCatalog", MatchStateKind.Wiring),
            new MatchStateEntry("Game.Core.GameAssetCatalog", MatchStateKind.Wiring),
            new MatchStateEntry("Game.Map.HexShaderHighlight", MatchStateKind.Wiring),
            new MatchStateEntry("Game.Audio.GameAudioManager", MatchStateKind.Presentation),
            new MatchStateEntry("Game.Aviation.AviationCombatPresenter", MatchStateKind.Presentation),
            new MatchStateEntry("Game.Campaign.CampaignRegionGraphic", MatchStateKind.Presentation),
            new MatchStateEntry("Game.Combat.BattleAiPhraseBank", MatchStateKind.Presentation),
            new MatchStateEntry("Game.Map.HexMapGenerator", MatchStateKind.Presentation),
            new MatchStateEntry("Game.Map.ResourceIconVisual", MatchStateKind.Presentation),
            new MatchStateEntry("Game.UI.BattleHealEffect", MatchStateKind.Presentation),
            new MatchStateEntry("Game.UI.CollectionMessageUI", MatchStateKind.Presentation),
            new MatchStateEntry("Game.UI.CollectionScreensUI", MatchStateKind.Presentation),
            new MatchStateEntry("Game.UI.CollectionUIElements", MatchStateKind.Presentation),
            new MatchStateEntry("Game.UI.GameMenuPanelUI", MatchStateKind.Presentation),
            new MatchStateEntry("Game.UI.UIFocusUtility", MatchStateKind.Presentation),
            new MatchStateEntry("Game.Progression.ProgressionContext", MatchStateKind.Profile),
            new MatchStateEntry("Game.Campaign.CampaignMatchBridge", MatchStateKind.Profile),
            new MatchStateEntry("Game.Ai.V2.StrategicEffectRegistry", MatchStateKind.Constant),
            new MatchStateEntry("Game.Ai.V2.MissionContinuityLayer", MatchStateKind.Constant),
        };

        public static readonly IReadOnlyDictionary<string, MatchStateEntry> ByType =
            All.ToDictionary(e => e.TypeName, StringComparer.Ordinal);

        // Clears every per-match holder in table order. Wiring is the caller's next step.
        public static void ResetForNewMatch()
        {
            foreach (MatchStateEntry entry in All)
                entry.Reset?.Invoke();
        }

        private static MatchStateEntry World(string type, Action reset) =>
            new MatchStateEntry(type, MatchStateKind.World, reset);
        private static MatchStateEntry Memory(string type, Action reset) =>
            new MatchStateEntry(type, MatchStateKind.AiMemory, reset);
        private static MatchStateEntry Turn(string type, Action reset) =>
            new MatchStateEntry(type, MatchStateKind.TurnScoped, reset);
    }
}
