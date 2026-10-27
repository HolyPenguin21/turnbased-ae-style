using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Ai.V2.Initiative;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Turns;

namespace Game.Ai
{
    // Explicit owner checklist and in-memory capture boundary for a settled human-turn save.
    // This is not a disk serializer or a load entry point. World identities must be restored
    // first. A future format supplies detached, tagged objective DTOs through the mandatory
    // codec; this API never substitutes fresh planning for missing continuation/history.
    internal sealed class AiPersistentState<TObjective>
    {
        internal readonly AiMapMemory.PersistentState Map;
        internal readonly IReadOnlyDictionary<string, MissionIntentState.PersistentState<TObjective>> Missions;
        internal readonly IReadOnlyDictionary<string, IReadOnlyList<AirSortieRegistry.SortieState>> Sorties;
        internal readonly IReadOnlyDictionary<string, AiRadarStateRegistry.PersistentState> Radar;
        internal readonly IReadOnlyDictionary<string, float> ForceBaseline;
        internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, (int Turn, IReadOnlyList<string> CardKeys)>> PreparationDelivery;
        internal readonly AiReconMemory.PersistentState Recon;
        internal readonly AiReconIntelMemory.PersistentState Intel;
        internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, ReconPatrolStateRegistry.PatrolState>> Patrols;
        internal readonly ReconAirSortieRegistry.PersistentState ReconSorties;
        internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<HexCoord, IReadOnlyDictionary<int, int>>> ReconCoverage;
        internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>> ReconDeficits;
        internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, (HexCoord? JustLeft, IReadOnlyList<HexCoord> Recent)>> ScoutTrails;
        internal readonly IReadOnlyDictionary<string, ResourceStarvationRegistry.PersistentState> Starvation;
        internal readonly IReadOnlyDictionary<string, (int LastTurn, IReadOnlyDictionary<ResourceType, int> Streak, ResourceBundle Surplus)> DevelopmentGate;
        internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<StableMissionKey, MissionCooldownInfo>> Allocator;
        internal readonly IReadOnlyDictionary<string, IReadOnlyList<InitiativeTurnRecord>> Initiative;
        internal readonly IReadOnlyDictionary<string, IReadOnlyList<int>> PublicInitiative;
        internal readonly IReadOnlyDictionary<string, int> MobilizationHold;
        internal readonly IReadOnlyDictionary<string, IReadOnlyList<(int Turn, string CardKey)>> DevelopmentHistory;
        internal readonly IReadOnlyDictionary<string, IReadOnlyList<(int Turn, float Demand)>> ApPressure;
        internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<MissionIntentKey, int>> LifecycleWaits;

        internal AiPersistentState(Func<PlayerSetupData, string> playerId, IMissionObjectiveStateCodec<TObjective> objectiveCodec, Action<CardData> includeReferencedCard = null)
        {
            if (playerId == null) throw new ArgumentNullException(nameof(playerId));
            if (objectiveCodec == null) throw new ArgumentNullException(nameof(objectiveCodec));
            Map = AiMapMemory.CapturePersistentState(playerId);
            Missions = MissionIntentRegistry.CapturePersistentState(playerId, objectiveCodec, includeReferencedCard);
            Sorties = AirSortieRegistry.CapturePersistentState(playerId);
            Radar = AiRadarStateRegistry.CapturePersistentState(playerId);
            ForceBaseline = ForceBaselineRegistry.CapturePersistentState(playerId);
            PreparationDelivery = PreparationDeliveryMemory.CapturePersistentState(playerId);
            Recon = AiReconMemory.CapturePersistentState(playerId);
            Intel = AiReconIntelMemory.CapturePersistentState(playerId);
            Patrols = ReconPatrolStateRegistry.CapturePersistentState(playerId);
            ReconSorties = ReconAirSortieRegistry.CapturePersistentState(playerId);
            ReconCoverage = AirReconCoverageRegistry.CapturePersistentState(playerId);
            ReconDeficits = ReconCapacityDeficitRegistry.CapturePersistentState(playerId);
            ScoutTrails = ScoutTrailRegistry.CapturePersistentState(playerId);
            Starvation = ResourceStarvationRegistry.CapturePersistentState(playerId);
            DevelopmentGate = DevelopmentInvestmentGate.CapturePersistentState(playerId);
            Allocator = AiAllocatorStateRegistry.CapturePersistentState(playerId);
            Initiative = InitiativeAnalyticsHistory.CapturePersistentState(playerId);
            PublicInitiative = InitiativePublicHistory.CapturePersistentState(playerId);
            MobilizationHold = OperationContinuationWindow.CapturePersistentState(playerId);
            DevelopmentHistory = DevelopmentDiversity.CapturePersistentState(playerId);
            ApPressure = ApTurnPressure.CapturePersistentState(playerId);
            LifecycleWaits = LifecycleReturnPolicy.CapturePersistentState(playerId);
        }

        // Not transactional across owners: the future load pipeline must validate its complete
        // DTO graph before applying it inside a fresh match/restore scope. Individual owners
        // resolve their references before replacing their authoritative dictionary.
        internal void Restore(Func<string, PlayerSetupData> player, Func<int, ArmyData> army,
            Func<int, CardData> card, IMissionObjectiveStateCodec<TObjective> objectiveCodec, HexMap map)
        {
            if (!MatchRestoreScope.IsRestoring)
                throw new InvalidOperationException("AI restoration requires MatchRestoreScope");
            AiMapMemory.RestorePersistentState(Map, player, map);
            MissionIntentRegistry.RestorePersistentState(Missions, player, objectiveCodec, card);
            AirSortieRegistry.RestorePersistentState(Sorties, player, army);
            AiRadarStateRegistry.RestorePersistentState(Radar, player);
            ForceBaselineRegistry.RestorePersistentState(ForceBaseline, player);
            PreparationDeliveryMemory.RestorePersistentState(PreparationDelivery, player);
            AiReconMemory.RestorePersistentState(Recon, player);
            AiReconIntelMemory.RestorePersistentState(Intel, player);
            ReconPatrolStateRegistry.RestorePersistentState(Patrols, player);
            ReconAirSortieRegistry.RestorePersistentState(ReconSorties, player);
            AirReconCoverageRegistry.RestorePersistentState(ReconCoverage, player);
            ReconCapacityDeficitRegistry.RestorePersistentState(ReconDeficits, player);
            ScoutTrailRegistry.RestorePersistentState(ScoutTrails, player);
            ResourceStarvationRegistry.RestorePersistentState(Starvation, player);
            DevelopmentInvestmentGate.RestorePersistentState(DevelopmentGate, player);
            AiAllocatorStateRegistry.RestorePersistentState(Allocator, player);
            InitiativeAnalyticsHistory.RestorePersistentState(Initiative, player);
            InitiativePublicHistory.RestorePersistentState(PublicInitiative, player);
            OperationContinuationWindow.RestorePersistentState(MobilizationHold, player);
            DevelopmentDiversity.RestorePersistentState(DevelopmentHistory, player);
            ApTurnPressure.RestorePersistentState(ApPressure, player);
            LifecycleReturnPolicy.RestorePersistentState(LifecycleWaits, player);
        }
    }
}
