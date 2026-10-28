// Generated concrete JSON DTO schema. See Tools/match-save-verify/generate_ai_codec.py.
using System;
using System.Collections.Generic;
namespace Game.Saves
{
    // global::Game.Ai.AiPersistentState<string>
    [Serializable] internal sealed class MatchAiData
    {
        public bool HasData;
        public MatchAiPersistentState1 Map;
        public MatchAiMap41 Missions;
        public MatchAiMap73 Sorties;
        public MatchAiMap79 Radar;
        public MatchAiMap83 ForceBaseline;
        public MatchAiMap84 PreparationDelivery;
        public MatchAiPersistentState87 Recon;
        public MatchAiPersistentState92 Intel;
        public MatchAiMap94 Patrols;
        public MatchAiPersistentState102 ReconSorties;
        public MatchAiMap107 ReconCoverage;
        public MatchAiMap110 ReconDeficits;
        public MatchAiMap114 ScoutTrails;
        public MatchAiMap118 Starvation;
        public MatchAiMap129 DevelopmentGate;
        public MatchAiMap131 Allocator;
        public MatchAiMap134 Initiative;
        public MatchAiMap137 PublicInitiative;
        public MatchAiMap91 MobilizationHold;
        public MatchAiMap138 DevelopmentHistory;
        public MatchAiMap141 ApPressure;
        public MatchAiMap144 LifecycleWaits;
        public List<int> HistoricalArmyIds = new List<int>();
        public List<int> HistoricalUnitIds = new List<int>();
        public List<int> HistoricalCardIds = new List<int>();
    }
    // global::Game.Ai.AiMapMemory.PersistentState
    [Serializable] internal sealed class MatchAiPersistentState1
    {
        public bool HasData;
        public int CurrentTurn;
        public MatchAiMap3 Players;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.AiMapMemory.PlayerMemoryState>
    [Serializable] internal sealed class MatchAiMap3
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiPlayerMemoryState5 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.AiMapMemory.PlayerMemoryState
    [Serializable] internal sealed class MatchAiPlayerMemoryState5
    {
        public bool HasData;
        public MatchAiList6 Resources;
        public MatchAiList11 Enemies;
        public MatchAiList27 Aircraft;
        public MatchAiList32 Buildings;
        public MatchAiMap36 Guards;
        public MatchAiMap38 Events;
        public MatchAiMap39 StructureLosses;
        public MatchAiList31 DestroyedArmies;
        public MatchAiList40 RefutedCitadels;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.KnownResourceHex>
    [Serializable] internal sealed class MatchAiList6
    {
        public bool HasData;
        public List<MatchAiKnownResourceHex7> Values = new List<MatchAiKnownResourceHex7>();
    }
    // global::Game.Ai.AiMapMemory.KnownResourceHex
    [Serializable] internal sealed class MatchAiKnownResourceHex7
    {
        public bool HasData;
        public MatchAiHexCoord8 Hex;
        public int DominantType;
        public MatchAiResourceYields10 Yield;
    }
    // global::Game.HexGrid.HexCoord
    [Serializable] internal sealed class MatchAiHexCoord8
    {
        public bool HasData;
        public int Q;
        public int R;
    }
    // global::Game.Economy.ResourceYields
    [Serializable] internal sealed class MatchAiResourceYields10
    {
        public bool HasData;
        public int human;
        public int energy;
        public int materials;
        public int tech;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting>>
    [Serializable] internal sealed class MatchAiList11
    {
        public bool HasData;
        public List<MatchAiOwnedObservation12> Values = new List<MatchAiOwnedObservation12>();
    }
    // global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting>
    [Serializable] internal sealed class MatchAiOwnedObservation12
    {
        public bool HasData;
        public string OwnerId;
        public MatchAiKnownEnemySighting13 Observation;
    }
    // global::Game.Ai.AiMapMemory.KnownEnemySighting
    [Serializable] internal sealed class MatchAiKnownEnemySighting13
    {
        public bool HasData;
        public int ArmyId;
        public MatchAiHexCoord8 Hex;
        public MatchAiPlayerRef Owner;
        public string Name;
        public int MemberCount;
        public float DefenseSum;
        public float AttackSum;
        public MatchAiList16 Defenders;
        public bool HasAntiAir;
        public int RecceRadius;
        public int RecceSpotStrength;
        public int SeenTurn;
        public bool IsGarrison;
        public bool IsAir;
        public int MaxMovement;
        public MatchAiSideCommander26 Commander;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Combat.WorthIt.DefenderProfile>
    [Serializable] internal sealed class MatchAiList16
    {
        public bool HasData;
        public List<MatchAiDefenderProfile17> Values = new List<MatchAiDefenderProfile17>();
    }
    // global::Game.Combat.WorthIt.DefenderProfile
    [Serializable] internal sealed class MatchAiDefenderProfile17
    {
        public bool HasData;
        public bool SingleUseUnit;
        public bool CanDisruptBuilding;
        public MatchAiEquipmentConsumption19 Consumption;
        public float Defense;
        public bool HasCeramicArmor;
        public MatchAiList24 TypeTags;
        public float Attack;
        public float HitPoints;
        public float MaxHitPoints;
        public int Initiative;
        public MatchAiList23 Abilities;
        public bool IsGroundCombatant;
        public bool IsHero;
        public int FateMax;
        public bool IsSummoned;
        public int Range;
    }
    // global::Game.Cards.EquipmentConsumption
    [Serializable] internal sealed class MatchAiEquipmentConsumption19
    {
        public bool HasData;
        public MatchAiPredictedEquipmentState20 Before;
        public MatchAiPredictedEquipmentState20 After;
        public int HpSpentExcess;
        public int FateSpentExcess;
        public string Key;
    }
    // global::Game.Cards.PredictedEquipmentState
    [Serializable] internal sealed class MatchAiPredictedEquipmentState20
    {
        public bool HasData;
        public MatchAiMap21 Stats;
        public MatchAiList23 Abilities;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Cards.EquipmentStat, int>
    [Serializable] internal sealed class MatchAiMap21
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyList<string>
    [Serializable] internal sealed class MatchAiList23
    {
        public bool HasData;
        public List<string> Values = new List<string>();
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Cards.UnitTypeTag>
    [Serializable] internal sealed class MatchAiList24
    {
        public bool HasData;
        public List<int> Values = new List<int>();
    }
    // global::Game.Combat.WorthIt.SideCommander
    [Serializable] internal sealed class MatchAiSideCommander26
    {
        public bool HasData;
        public bool Present;
        public int Initiative;
        public int Fate;
        public MatchAiList16 SupportHeroes;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting>>
    [Serializable] internal sealed class MatchAiList27
    {
        public bool HasData;
        public List<MatchAiOwnedObservation28> Values = new List<MatchAiOwnedObservation28>();
    }
    // global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting>
    [Serializable] internal sealed class MatchAiOwnedObservation28
    {
        public bool HasData;
        public string OwnerId;
        public MatchAiKnownAirSighting29 Observation;
    }
    // global::Game.Ai.AiMapMemory.KnownAirSighting
    [Serializable] internal sealed class MatchAiKnownAirSighting29
    {
        public bool HasData;
        public MatchAiHexCoord8 Hex;
        public MatchAiPlayerRef Owner;
        public int SeenTurn;
        public bool IsGroundArmy;
        public MatchAiDefendingAirArmy30 Roster;
    }
    // global::Game.Combat.AviationCombatEstimator.DefendingAirArmy
    [Serializable] internal sealed class MatchAiDefendingAirArmy30
    {
        public bool HasData;
        public int ArmyId;
        public MatchAiList16 Units;
        public MatchAiList31 CurrentFates;
        public int CommanderIndex;
    }
    // global::System.Collections.Generic.IReadOnlyList<int>
    [Serializable] internal sealed class MatchAiList31
    {
        public bool HasData;
        public List<int> Values = new List<int>();
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding>>
    [Serializable] internal sealed class MatchAiList32
    {
        public bool HasData;
        public List<MatchAiOwnedObservation33> Values = new List<MatchAiOwnedObservation33>();
    }
    // global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding>
    [Serializable] internal sealed class MatchAiOwnedObservation33
    {
        public bool HasData;
        public string OwnerId;
        public MatchAiKnownBuilding34 Observation;
    }
    // global::Game.Ai.AiMapMemory.KnownBuilding
    [Serializable] internal sealed class MatchAiKnownBuilding34
    {
        public bool HasData;
        public MatchAiList16 ExplosionStructures;
        public MatchAiHexCoord8 Hex;
        public MatchAiPlayerRef Owner;
        public bool IsStartingCitadel;
        public bool IsBase;
        public MatchAiList35 FacilityAbilities;
        public MatchAiList31 CollectedAmounts;
        public int FreeFacilitySlots;
        public float Defense;
        public int SeenTurn;
    }
    // global::System.Collections.Generic.IReadOnlyCollection<string>
    [Serializable] internal sealed class MatchAiList35
    {
        public bool HasData;
        public List<string> Values = new List<string>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::Game.Ai.AiMapMemory.GuardStrength>
    [Serializable] internal sealed class MatchAiMap36
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiHexCoord8 Key; public MatchAiGuardStrength37 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.AiMapMemory.GuardStrength
    [Serializable] internal sealed class MatchAiGuardStrength37
    {
        public bool HasData;
        public float Defense;
        public float Attack;
        public MatchAiList16 Defenders;
        public string Name;
        public MatchAiSideCommander26 Commander;
        public int RewardTier;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, bool>
    [Serializable] internal sealed class MatchAiMap38
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiHexCoord8 Key; public bool Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int>
    [Serializable] internal sealed class MatchAiMap39
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiHexCoord8 Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord>
    [Serializable] internal sealed class MatchAiList40
    {
        public bool HasData;
        public List<MatchAiHexCoord8> Values = new List<MatchAiHexCoord8>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.MissionIntentState.PersistentState<string>>
    [Serializable] internal sealed class MatchAiMap41
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiPersistentState42 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.MissionIntentState.PersistentState<string>
    [Serializable] internal sealed class MatchAiPersistentState42
    {
        public bool HasData;
        public MatchAiList43 Intents;
        public MatchAiList53 RetreatWitnesses;
        public MatchAiPersistentState57 Economy;
        public MatchAiMap70 Development;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.IntentState<string>>
    [Serializable] internal sealed class MatchAiList43
    {
        public bool HasData;
        public List<MatchAiIntentState44> Values = new List<MatchAiIntentState44>();
    }
    // global::Game.Ai.V2.MissionIntentState.IntentState<string>
    [Serializable] internal sealed class MatchAiIntentState44
    {
        public bool HasData;
        public MatchAiMissionIntentKey45 IntentKey;
        public MatchAiStableMissionKey48 LastAttemptKey;
        public int Kind;
        public int Funding;
        public int Status;
        public int Suspended;
        public int CreatedTurn;
        public int TurnsActive;
        public int LastReconciledTurn;
        public int LastProgressTurn;
        public int LastProtectedTurn;
        public int StallTurns;
        public float LastIntrinsicValue;
        public float CumulativeApSpent;
        public int StepsMovedTotal;
        public MatchAiOptional52 PreferredMoverArmyId;
        public MatchAiObjectiveData Objective;
    }
    // global::Game.Ai.V2.MissionIntentKey
    [Serializable] internal sealed class MatchAiMissionIntentKey45
    {
        public bool HasData;
        public int Kind;
        public int SubKind;
        public int ObjectiveId;
        public int Q;
        public int R;
        public int TargetKind;
    }
    // global::Game.Ai.V2.StableMissionKey
    [Serializable] internal sealed class MatchAiStableMissionKey48
    {
        public bool HasData;
        public int Kind;
        public int SubKind;
        public int TargetId;
        public int Q;
        public int R;
        public int TargetKind;
        public int ActorId;
        public int DetailId;
    }
    // int?
    [Serializable] internal sealed class MatchAiOptional52
    {
        public bool HasData;
        public int Value;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.RetreatWitnessState>
    [Serializable] internal sealed class MatchAiList53
    {
        public bool HasData;
        public List<MatchAiRetreatWitnessState54> Values = new List<MatchAiRetreatWitnessState54>();
    }
    // global::Game.Ai.V2.MissionIntentState.RetreatWitnessState
    [Serializable] internal sealed class MatchAiRetreatWitnessState54
    {
        public bool HasData;
        public MatchAiRetreatTargetState55 Target;
        public int OwnArmyId;
        public int EnemyArmyId;
        public int EnemyFingerprint;
        public int OwnFingerprint;
        public int Turn;
        public MatchAiHexCoord8 EnemyHex;
        public string Reason;
    }
    // global::Game.Ai.V2.MissionIntentState.RetreatTargetState
    [Serializable] internal sealed class MatchAiRetreatTargetState55
    {
        public bool HasData;
        public bool HasValue;
        public MatchAiHexCoord8 Hex;
        public string ExpectedOwnerId;
        public int Kind;
    }
    // global::Game.Ai.V2.EconomyLifecycleState.PersistentState
    [Serializable] internal sealed class MatchAiPersistentState57
    {
        public bool HasData;
        public MatchAiDeliveryEvidence58 BaseDelivery;
        public MatchAiDeliveryEvidence63 ExtractionDelivery;
        public MatchAiMap68 EndedOutboundBuilders;
    }
    // global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(int CardId, global::Game.HexGrid.HexCoord Hex)>
    [Serializable] internal sealed class MatchAiDeliveryEvidence58
    {
        public bool HasData;
        public MatchAiMap59 Failures;
        public MatchAiMap62 SuppressedUntil;
        public MatchAiMap62 ProgressTurns;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)>
    [Serializable] internal sealed class MatchAiMap59
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiTuple60 Key; public MatchAiTuple61 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (int CardId, global::Game.HexGrid.HexCoord Hex)
    [Serializable] internal sealed class MatchAiTuple60
    {
        public bool HasData;
        public int CardId;
        public MatchAiHexCoord8 Hex;
    }
    // (int Turn, int Count)
    [Serializable] internal sealed class MatchAiTuple61
    {
        public bool HasData;
        public int Turn;
        public int Count;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), int>
    [Serializable] internal sealed class MatchAiMap62
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiTuple60 Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex)>
    [Serializable] internal sealed class MatchAiDeliveryEvidence63
    {
        public bool HasData;
        public MatchAiMap64 Failures;
        public MatchAiMap67 SuppressedUntil;
        public MatchAiMap67 ProgressTurns;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)>
    [Serializable] internal sealed class MatchAiMap64
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiTuple65 Key; public MatchAiTuple61 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex)
    [Serializable] internal sealed class MatchAiTuple65
    {
        public bool HasData;
        public MatchAiOptional66 Resource;
        public MatchAiHexCoord8 Hex;
    }
    // global::Game.Economy.ResourceType?
    [Serializable] internal sealed class MatchAiOptional66
    {
        public bool HasData;
        public int Value;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), int>
    [Serializable] internal sealed class MatchAiMap67
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiTuple65 Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, float Value)>
    [Serializable] internal sealed class MatchAiMap68
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiTuple69 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (global::Game.HexGrid.HexCoord Site, float Value)
    [Serializable] internal sealed class MatchAiTuple69
    {
        public bool HasData;
        public MatchAiHexCoord8 Site;
        public float Value;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn)>
    [Serializable] internal sealed class MatchAiMap70
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiTuple71 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn)
    [Serializable] internal sealed class MatchAiTuple71
    {
        public bool HasData;
        public MatchAiHexCoord8 Site;
        public int Mode;
        public int Turn;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.AirSortieRegistry.SortieState>>
    [Serializable] internal sealed class MatchAiMap73
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiList74 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.AirSortieRegistry.SortieState>
    [Serializable] internal sealed class MatchAiList74
    {
        public bool HasData;
        public List<MatchAiSortieState75> Values = new List<MatchAiSortieState75>();
    }
    // global::Game.Ai.V2.AirSortieRegistry.SortieState
    [Serializable] internal sealed class MatchAiSortieState75
    {
        public bool HasData;
        public int ArmyId;
        public int Kind;
        public MatchAiHexCoord8 TargetHex;
        public MatchAiHexCoord8 LandingHex;
        public bool Outbound;
        public MatchAiOptional52 HeldTurn;
        public MatchAiAirStrikePolicy77 StrikePolicy;
    }
    // global::Game.Aviation.AirStrikePolicy
    [Serializable] internal sealed class MatchAiAirStrikePolicy77
    {
        public bool HasData;
        public int Kind;
        public MatchAiOptional52 ExactTargetArmyId;
        public int MinimumSurvivors;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.AiRadarStateRegistry.PersistentState>
    [Serializable] internal sealed class MatchAiMap79
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiPersistentState80 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.AiRadarStateRegistry.PersistentState
    [Serializable] internal sealed class MatchAiPersistentState80
    {
        public bool HasData;
        public int LastTurn;
        public MatchAiMap81 Smoothed;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.DesireAxis, float>
    [Serializable] internal sealed class MatchAiMap81
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public float Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, float>
    [Serializable] internal sealed class MatchAiMap83
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public float Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)>>
    [Serializable] internal sealed class MatchAiMap84
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap85 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)>
    [Serializable] internal sealed class MatchAiMap85
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiTuple86 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)
    [Serializable] internal sealed class MatchAiTuple86
    {
        public bool HasData;
        public int Turn;
        public MatchAiList23 CardKeys;
    }
    // global::Game.Ai.V2.AiReconMemory.PersistentState
    [Serializable] internal sealed class MatchAiPersistentState87
    {
        public bool HasData;
        public MatchAiMap88 Observations;
        public MatchAiMap91 ObservedTurns;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState>>
    [Serializable] internal sealed class MatchAiMap88
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap89 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState>
    [Serializable] internal sealed class MatchAiMap89
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiObservationState90 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.AiReconMemory.ObservationState
    [Serializable] internal sealed class MatchAiObservationState90
    {
        public bool HasData;
        public int ArmyId;
        public MatchAiHexCoord8 LastObservedHex;
        public int LastObservedTurn;
        public int MemberCount;
        public float AttackSum;
        public float DefenseSum;
        public bool HasAntiAir;
        public int RecceRadius;
        public int RecceSpotStrength;
        public bool IsGarrison;
        public bool IsAir;
        public int MaxMovement;
        public MatchAiSideCommander26 Commander;
        public string OwnerId;
        public MatchAiList16 Defenders;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, int>
    [Serializable] internal sealed class MatchAiMap91
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.AiReconIntelMemory.PersistentState
    [Serializable] internal sealed class MatchAiPersistentState92
    {
        public bool HasData;
        public MatchAiMap93 Observations;
        public MatchAiMap91 CurrentTurns;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int>>
    [Serializable] internal sealed class MatchAiMap93
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap39 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState>>
    [Serializable] internal sealed class MatchAiMap94
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap95 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState>
    [Serializable] internal sealed class MatchAiMap95
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiPatrolState96 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState
    [Serializable] internal sealed class MatchAiPatrolState96
    {
        public bool HasData;
        public int Mode;
        public int PreferredMoverArmyId;
        public MatchAiHexCoord8 StrategicAnchor;
        public int StrategicSector;
        public int StartedTurn;
        public int LastProgressTurn;
        public int LastModeSwitchTurn;
        public int LastStrategicReassignmentTurn;
        public MatchAiOptional99 Escape;
    }
    // (global::Game.HexGrid.HexCoord FromHex, global::Game.Ai.V2.ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn)?
    [Serializable] internal sealed class MatchAiOptional99
    {
        public bool HasData;
        public MatchAiTuple100 Value;
    }
    // (global::Game.HexGrid.HexCoord FromHex, global::Game.Ai.V2.ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn)
    [Serializable] internal sealed class MatchAiTuple100
    {
        public bool HasData;
        public MatchAiHexCoord8 FromHex;
        public int Cause;
        public float Risk;
        public MatchAiOptional52 ThreatArmyId;
        public int Turn;
    }
    // global::Game.Ai.V2.ReconAirSortieRegistry.PersistentState
    [Serializable] internal sealed class MatchAiPersistentState102
    {
        public bool HasData;
        public int NextSortieId;
        public MatchAiMap103 Players;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState>>
    [Serializable] internal sealed class MatchAiMap103
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap104 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState>
    [Serializable] internal sealed class MatchAiMap104
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiSortieState105 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.ReconAirSortieRegistry.SortieState
    [Serializable] internal sealed class MatchAiSortieState105
    {
        public bool HasData;
        public int SortieId;
        public int Phase;
        public MatchAiHexCoord8 LaunchHex;
        public MatchAiHexCoord8 ChosenLandingHex;
        public bool HasChosenLanding;
        public int ClaimedSector;
        public bool HasClaim;
        public float BestOutboundStepScore;
        public int LastProcessedTurn;
        public string LastDecisionReason;
        public MatchAiList40 Trail;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>>>
    [Serializable] internal sealed class MatchAiMap107
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap108 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>>
    [Serializable] internal sealed class MatchAiMap108
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiHexCoord8 Key; public MatchAiMap109 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, int>
    [Serializable] internal sealed class MatchAiMap109
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>>
    [Serializable] internal sealed class MatchAiMap110
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap111 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>
    [Serializable] internal sealed class MatchAiMap111
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiTuple113 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (int FirstSeenTurn, int LastSeenTurn)
    [Serializable] internal sealed class MatchAiTuple113
    {
        public bool HasData;
        public int FirstSeenTurn;
        public int LastSeenTurn;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)>>
    [Serializable] internal sealed class MatchAiMap114
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap115 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)>
    [Serializable] internal sealed class MatchAiMap115
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiTuple116 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)
    [Serializable] internal sealed class MatchAiTuple116
    {
        public bool HasData;
        public MatchAiOptional117 JustLeft;
        public MatchAiList40 Recent;
    }
    // global::Game.HexGrid.HexCoord?
    [Serializable] internal sealed class MatchAiOptional117
    {
        public bool HasData;
        public MatchAiHexCoord8 Value;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState>
    [Serializable] internal sealed class MatchAiMap118
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiPersistentState119 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState
    [Serializable] internal sealed class MatchAiPersistentState119
    {
        public bool HasData;
        public MatchAiMap120 Forecasts;
        public MatchAiMap124 Pressure;
        public MatchAiMap125 CurrentBlocks;
        public MatchAiList127 VerifiedPending;
        public MatchAiMap128 LastPressureHitTurn;
        public bool RequireVerifiedEvidence;
        public int LastDecayTurn;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState>
    [Serializable] internal sealed class MatchAiMap120
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiForecastState121 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState
    [Serializable] internal sealed class MatchAiForecastState121
    {
        public bool HasData;
        public int Turn;
        public MatchAiMap122 Needs;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceBundle>
    [Serializable] internal sealed class MatchAiMap122
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiResourceBundle123 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.ResourceBundle
    [Serializable] internal sealed class MatchAiResourceBundle123
    {
        public bool HasData;
        public float Human;
        public float Energy;
        public float Materials;
        public float Tech;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, float>
    [Serializable] internal sealed class MatchAiMap124
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public float Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, global::Game.Ai.V2.ResourceBlockEvidence>
    [Serializable] internal sealed class MatchAiMap125
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public MatchAiResourceBlockEvidence126 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.ResourceBlockEvidence
    [Serializable] internal sealed class MatchAiResourceBlockEvidence126
    {
        public bool HasData;
        public float Required;
        public float AvailableAtBlock;
        public float IncomePerTurn;
        public float DemandValue;
        public int Turn;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Economy.ResourceType>
    [Serializable] internal sealed class MatchAiList127
    {
        public bool HasData;
        public List<int> Values = new List<int>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int>
    [Serializable] internal sealed class MatchAiMap128
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public int Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, (int LastTurn, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Streak, global::Game.Ai.V2.ResourceBundle Surplus)>
    [Serializable] internal sealed class MatchAiMap129
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiTuple130 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // (int LastTurn, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Streak, global::Game.Ai.V2.ResourceBundle Surplus)
    [Serializable] internal sealed class MatchAiTuple130
    {
        public bool HasData;
        public int LastTurn;
        public MatchAiMap128 Streak;
        public MatchAiResourceBundle123 Surplus;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo>>
    [Serializable] internal sealed class MatchAiMap131
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap132 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo>
    [Serializable] internal sealed class MatchAiMap132
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiStableMissionKey48 Key; public MatchAiMissionCooldownInfo133 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.MissionCooldownInfo
    [Serializable] internal sealed class MatchAiMissionCooldownInfo133
    {
        public bool HasData;
        public int StartedTurn;
        public int UntilTurn;
        public string Reason;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.Initiative.InitiativeTurnRecord>>
    [Serializable] internal sealed class MatchAiMap134
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiList135 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.Initiative.InitiativeTurnRecord>
    [Serializable] internal sealed class MatchAiList135
    {
        public bool HasData;
        public List<MatchAiInitiativeTurnRecord136> Values = new List<MatchAiInitiativeTurnRecord136>();
    }
    // global::Game.Ai.V2.Initiative.InitiativeTurnRecord
    [Serializable] internal sealed class MatchAiInitiativeTurnRecord136
    {
        public bool HasData;
        public int InitiativeBaseAp;
        public int TotalStartAp;
        public int ApSpent;
        public int EndAp;
        public int ActionableArmyCountAtStart;
        public int UnactivatedActionableArmyCountAtEnd;
        public bool HadPotentialApWorkAtEnd;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<int>>
    [Serializable] internal sealed class MatchAiMap137
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiList31 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, string CardKey)>>
    [Serializable] internal sealed class MatchAiMap138
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiList139 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyList<(int Turn, string CardKey)>
    [Serializable] internal sealed class MatchAiList139
    {
        public bool HasData;
        public List<MatchAiTuple140> Values = new List<MatchAiTuple140>();
    }
    // (int Turn, string CardKey)
    [Serializable] internal sealed class MatchAiTuple140
    {
        public bool HasData;
        public int Turn;
        public string CardKey;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, float Demand)>>
    [Serializable] internal sealed class MatchAiMap141
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiList142 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyList<(int Turn, float Demand)>
    [Serializable] internal sealed class MatchAiList142
    {
        public bool HasData;
        public List<MatchAiTuple143> Values = new List<MatchAiTuple143>();
    }
    // (int Turn, float Demand)
    [Serializable] internal sealed class MatchAiTuple143
    {
        public bool HasData;
        public int Turn;
        public float Demand;
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.MissionIntentKey, int>>
    [Serializable] internal sealed class MatchAiMap144
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public string Key; public MatchAiMap145 Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.MissionIntentKey, int>
    [Serializable] internal sealed class MatchAiMap145
    {
        public bool HasData;
        [Serializable] public sealed class Entry { public MatchAiMissionIntentKey45 Key; public int Value; }
        public List<Entry> Entries = new List<Entry>();
    }
    // global::Game.Ai.V2.ScoutIntent
    [Serializable] internal sealed class MatchAiScoutIntent146
    {
        public bool HasData;
        public int Kind;
        public bool RequiresStealth;
        public MatchAiHexCoord8 FocusHex;
    }
    // global::Game.Ai.V2.RaidIntent
    [Serializable] internal sealed class MatchAiRaidIntent148
    {
        public bool HasData;
        public MatchAiRaidTargetRef149 Target;
        public MatchAiHexCoord8 LastKnownHex;
        public bool TargetIsNeutral;
        public bool OperationStarted;
        public bool CompletedTargetAwaitingFreshDecision;
        public MatchAiOptional52 AirSupportArmyId;
        public MatchAiOptional117 AirSupportLandingHex;
        public int AirSupportAttemptedTurn;
        public bool AirSupportStrikeSucceeded;
        public MatchAiOptional117 ReturnHex;
        public MatchAiOptional117 SupportReturnHex;
        public MatchAiOptional117 RecoveryBaseHex;
        public int ReinforcementRequestedTurn;
        public int Phase;
        public MatchAiOptional52 PrimaryArmyId;
        public MatchAiOptional52 SupportArmyId;
    }
    // global::Game.Ai.V2.RaidTargetRef
    [Serializable] internal sealed class MatchAiRaidTargetRef149
    {
        public bool HasData;
        public bool HasValue;
        public int Kind;
        public int ArmyId;
        public MatchAiHexCoord8 Hex;
    }
    // global::Game.Ai.V2.EconomyIntent
    [Serializable] internal sealed class MatchAiEconomyIntent151
    {
        public bool HasData;
        public int Kind;
        public MatchAiHexCoord8 TargetHex;
        public MatchAiOptional66 ResourceType;
        public MatchAiOptional52 BuilderArmyId;
        public MatchAiOptional52 CollectorArmyId;
        public MatchAiOptional52 CollectorSourceArmyId;
        public int ExpectedMarginalYield;
        public MatchAiOptional117 SafeReturnHex;
        public int ArrivalTurn;
        public int LastIncomeOpportunityTurn;
        public MatchAiCardRef BuildCard;
        public MatchAiResourceCost154 BuildResourceCost;
        public float BuildApCost;
        public MatchAiOptional155 IntrinsicValue;
        public float BuildValue;
        public float MinimumFollowupAp;
        public bool Loaned;
        public MatchAiMissionIntentKey45 LoanSource;
    }
    // global::Game.Cards.ResourceCost
    [Serializable] internal sealed class MatchAiResourceCost154
    {
        public bool HasData;
        public int human;
        public int energy;
        public int materials;
        public int tech;
    }
    // float?
    [Serializable] internal sealed class MatchAiOptional155
    {
        public bool HasData;
        public float Value;
    }
    // global::Game.Ai.V2.DevelopmentIntent
    [Serializable] internal sealed class MatchAiDevelopmentIntent156
    {
        public bool HasData;
        public MatchAiHexCoord8 FacilityHex;
        public int Mode;
        public MatchAiUnitRef Hero;
        public string HeroKey;
        public float IntrinsicValue;
    }
    // global::Game.Ai.V2.ActiveDefenceIntent
    [Serializable] internal sealed class MatchAiActiveDefenceIntent158
    {
        public bool HasData;
        public int Phase;
        public int ReturnPurpose;
        public int InterceptPurpose;
        public int ImmediateTurn;
        public MatchAiGarrisonSortiePlan162 Sortie;
        public MatchAiOptional52 EnemyEta;
        public int EnemyArmyId;
        public MatchAiHexCoord8 LastKnownHex;
        public int LastObservedTurn;
        public float Confidence;
        public MatchAiHexCoord8 ProtectedAssetHex;
        public int ProtectedAssetKind;
        public float ProtectedAssetValue;
        public float ThreatSeverity;
        public MatchAiOptional117 ReturnHex;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public int EstimatedEta;
        public MatchAiOptional52 AirSupportArmyId;
        public MatchAiOptional117 AirSupportLandingHex;
        public int AirSupportBoundTurn;
        public bool AirSupportSortieSeen;
        public MatchAiOptional52 PrimaryArmyId;
    }
    // global::Game.Ai.V2.GarrisonSortiePlan
    [Serializable] internal sealed class MatchAiGarrisonSortiePlan162
    {
        public bool HasData;
        public int DonorGarrisonId;
        public int CoverRuntimeId;
        public int EnemyArmyId;
        public int RouteCost;
        public MatchAiOptional52 ShellArmyId;
        public MatchAiHexCoord8 StrongholdHex;
        public MatchAiHexCoord8 EnemyHex;
        public MatchAiList31 UnitRuntimeIds;
        public MatchAiArmySnapshot163 Detachment;
        public float WinChance;
        public bool CoversAllDefenders;
    }
    // global::Game.Ai.V2.ArmySnapshot
    [Serializable] internal sealed class MatchAiArmySnapshot163
    {
        public bool HasData;
        public MatchAiArmySnapshot163 EconomyDeparture;
        public int ArmyId;
        public MatchAiPlayerRef Owner;
        public MatchAiHexCoord8 Hex;
        public bool IsGarrison;
        public bool RequiresGarrisonExtraction;
        public bool IsPrison;
        public bool IsAir;
        public bool IsAirfield;
        public int MobileAirfieldCapacity;
        public int RepairSourceCount;
        public bool IsCarrierContainer;
        public float CarrierCargoPower;
        public bool CanServeReconAir;
        public MatchAiList164 StoredAircraft;
        public int MemberCount;
        public bool HasHero;
        public int HeroCount;
        public int BestHeroCommandRating;
        public MatchAiSideCommander26 Commander;
        public bool HasAntiAir;
        public MatchAiRoleCoverage166 StrategicCoverage;
        public MatchAiList167 AllyAuraEffects;
        public MatchAiList16 MembersWithHeroes;
        public MatchAiList177 NonHeroMutatorOccupied;
        public MatchAiList31 NonHeroActivationApCosts;
        public MatchAiList31 NonHeroMoveMax;
        public MatchAiList177 NonHeroIsAviation;
        public MatchAiList31 NonHeroRuntimeIds;
        public MatchAiList177 NonHeroSpareable;
        public float GarrisonThreatFloor;
        public MatchAiList31 NonHeroCurrentMovement;
        public MatchAiList31 NonHeroMovementSpent;
        public int HeroCurrentMovement;
        public bool EconomyRosterProtected;
        public MatchAiMap39 EconomyHomeRouteCosts;
        public int HeroActivationApCost;
        public int HeroMoveMax;
        public bool HeroIsHomeVocation;
        public bool HasResearchOperator;
        public bool HasProductionOperator;
        public bool IsHiddenFromUs;
        public float AttackSum;
        public float DefenseSum;
        public float EffectiveArmyPower;
        public float CompositionQuality;
        public int MaxMovement;
        public int SafeUnlandedEndsRemaining;
        public int Capacity;
        public int OccupiedBattleSlots;
        public int RosterCapacity;
        public int SupportCarrierSlots;
        public MatchAiOptional178 CommanderRosterCandidate;
        public int CommanderCommandRating;
        public int ActivationApCost;
        public int ActivationEnergyCost;
        public bool HasActivatedThisTurn;
        public int PendingActivationApCost;
        public int PendingActivationEnergyCost;
        public MatchAiList183 ActivationCoveredUnitRuntimeIds;
        public int CurrentMovement;
        public bool IsSoloRecce;
        public bool IsMobileEconomyBuilder;
        public bool OperatorDutyBlocksDeparture;
        public bool IsStructuralRaidActor;
        public bool IsHidden;
        public bool CanEnterStealth;
        public int StealthLevel;
        public int EffectiveVisionRadius;
        public MatchAiResourceBundle123 CollectionCapacity;
        public MatchAiList40 ReachableOwnBaseHexes;
        public MatchAiList184 RecoveryMembers;
        public MatchAiList16 Members;
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.StoredAircraftLaunchCost>
    [Serializable] internal sealed class MatchAiList164
    {
        public bool HasData;
        public List<MatchAiStoredAircraftLaunchCost165> Values = new List<MatchAiStoredAircraftLaunchCost165>();
    }
    // global::Game.Ai.V2.StoredAircraftLaunchCost
    [Serializable] internal sealed class MatchAiStoredAircraftLaunchCost165
    {
        public bool HasData;
        public int RuntimeId;
        public int Ap;
        public int Energy;
        public int Movement;
    }
    // global::Game.Ai.V2.RoleCoverage
    [Serializable] internal sealed class MatchAiRoleCoverage166
    {
        public bool HasData;
        public List<int> Values = new List<int>();
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.StrategicEffect>
    [Serializable] internal sealed class MatchAiList167
    {
        public bool HasData;
        public List<MatchAiStrategicEffect168> Values = new List<MatchAiStrategicEffect168>();
    }
    // global::Game.Ai.V2.StrategicEffect
    [Serializable] internal sealed class MatchAiStrategicEffect168
    {
        public bool HasData;
        public int Role;
        public float BaseFit;
        public int Context;
        public int Field;
        public bool CountsAsCoverage;
        public string EligiblePredicate;
        public int Scope;
        public float Magnitude;
        public float Probability;
        public int Timing;
        public int DurationRounds;
        public int CapacityRequirement;
        public int Stacking;
        public string StackingKey;
        public int GlobalResource;
        public float GlobalYieldPerTurn;
        public bool PrimaryExchange;
        public bool RequiresHit;
        public int SecondaryTargets;
        public int SecondaryPriority;
        public bool BodyOnly;
    }
    // global::System.Collections.Generic.IReadOnlyList<bool>
    [Serializable] internal sealed class MatchAiList177
    {
        public bool HasData;
        public List<bool> Values = new List<bool>();
    }
    // global::Game.Ai.V2.StrikeRosterCandidate?
    [Serializable] internal sealed class MatchAiOptional178
    {
        public bool HasData;
        public MatchAiStrikeRosterCandidate179 Value;
    }
    // global::Game.Ai.V2.StrikeRosterCandidate
    [Serializable] internal sealed class MatchAiStrikeRosterCandidate179
    {
        public bool HasData;
        public MatchAiStrikeRosterSlot180 Slot;
        public MatchAiPowerUnit182 Unit;
    }
    // global::Game.Ai.V2.StrikeRosterSlot
    [Serializable] internal sealed class MatchAiStrikeRosterSlot180
    {
        public bool HasData;
        public string Key;
        public bool IsHero;
        public float Power;
        public int Source;
    }
    // global::Game.Ai.V2.AiPower.PowerUnit
    [Serializable] internal sealed class MatchAiPowerUnit182
    {
        public bool HasData;
        public float BasePower;
        public MatchAiList24 Tags;
        public int Range;
        public bool IsHero;
        public int CommandRating;
    }
    // global::System.Collections.Generic.IReadOnlyCollection<int>
    [Serializable] internal sealed class MatchAiList183
    {
        public bool HasData;
        public List<int> Values = new List<int>();
    }
    // global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.RaidRecoveryMemberSnapshot>
    [Serializable] internal sealed class MatchAiList184
    {
        public bool HasData;
        public List<MatchAiRaidRecoveryMemberSnapshot185> Values = new List<MatchAiRaidRecoveryMemberSnapshot185>();
    }
    // global::Game.Ai.V2.RaidRecoveryMemberSnapshot
    [Serializable] internal sealed class MatchAiRaidRecoveryMemberSnapshot185
    {
        public bool HasData;
        public int RuntimeId;
        public int UnitIndex;
        public bool IsHero;
        public bool IsAviation;
        public bool IsGroundBattleBody;
        public bool CanSpareForRaid;
        public int ActivationApCost;
        public MatchAiDefenderProfile17 CurrentProfile;
        public MatchAiDefenderProfile17 FullHealthProfile;
        public bool RepairCostInitialized;
        public bool IsPrisoner;
        public MatchAiResourceVector186 RepairCost;
    }
    // global::Game.Ai.V2.ResourceVector
    [Serializable] internal sealed class MatchAiResourceVector186
    {
        public bool HasData;
        public float Ap;
        public float Human;
        public float Energy;
        public float Materials;
        public float Tech;
    }
    // global::Game.Ai.V2.AttackIntent
    [Serializable] internal sealed class MatchAiAttackIntent188
    {
        public bool HasData;
        public MatchAiAttackTargetRef189 Target;
        public MatchAiAttackTargetRef189 IntermediateTarget;
        public int Phase;
        public bool OperationStarted;
        public bool AssaultStarted;
        public int AssaultObservationWaitSinceTurn;
        public MatchAiOptional117 RefitBaseHex;
        public int RefitCaptureTurn;
        public int RefitBattleStopTurn;
        public bool Preparation;
        public MatchAiOptional117 StagingHex;
        public bool Secondary;
        public int PreparationCardWaitSinceTurn;
        public MatchAiList191 TargetRoster;
        public float TargetRosterPeak;
        public string TargetRosterCommanderKey;
        public float TargetRosterPower;
        public int TargetRosterCapacity;
        public MatchAiOptional52 CommanderArmyId;
        public MatchAiList192 GatherSupportArmyIds;
        public MatchAiList193 GatherReturns;
        public MatchAiOptional52 AirSupportArmyId;
        public MatchAiOptional117 AirSupportLandingHex;
        public int AirSupportBoundTurn;
        public bool AirSupportSortieSeen;
        public int AirSupportAttemptedTurn;
        public MatchAiOptional117 RecoveryBaseHex;
        public MatchAiOptional117 SupportReturnHex;
        public MatchAiOptional117 RendezvousHex;
        public int ReinforcementRequestedTurn;
        public int LastOpportunisticStrikeTurn;
        public bool TacticalRetreat;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public MatchAiOptional52 PrimaryArmyId;
        public MatchAiOptional52 SupportArmyId;
    }
    // global::Game.Ai.V2.AttackTargetRef
    [Serializable] internal sealed class MatchAiAttackTargetRef189
    {
        public bool HasData;
        public bool HasValue;
        public MatchAiHexCoord8 Hex;
        public MatchAiPlayerRef ExpectedOwner;
        public int Kind;
    }
    // global::System.Collections.Generic.List<global::Game.Ai.V2.StrikeRosterSlot>
    [Serializable] internal sealed class MatchAiList191
    {
        public bool HasData;
        public List<MatchAiStrikeRosterSlot180> Values = new List<MatchAiStrikeRosterSlot180>();
    }
    // global::System.Collections.Generic.List<int>
    [Serializable] internal sealed class MatchAiList192
    {
        public bool HasData;
        public List<int> Values = new List<int>();
    }
    // global::System.Collections.Generic.List<global::Game.Ai.V2.AttackGatherReturn>
    [Serializable] internal sealed class MatchAiList193
    {
        public bool HasData;
        public List<MatchAiAttackGatherReturn194> Values = new List<MatchAiAttackGatherReturn194>();
    }
    // global::Game.Ai.V2.AttackGatherReturn
    [Serializable] internal sealed class MatchAiAttackGatherReturn194
    {
        public bool HasData;
        public int ArmyId;
        public MatchAiOptional117 BaseHex;
    }
}
