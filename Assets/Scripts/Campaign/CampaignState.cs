using System;
using System.Collections.Generic;
using Game.Players;
using UnityEngine;

namespace Game.Campaign
{
    public enum CampaignPhase { AwaitingFactionAction, PreparingBattle, BattleInProgress, BattleResolved, ShowingResult, CampaignFinished }
    public enum CampaignOutcome { AttackerVictory, DefenderVictory, Draw }
    [Serializable] public sealed class RegionState
    {
        public int RegionId;
        public string Name;
        public Faction OwnerFaction;
        public List<Vector2> PolygonVertices = new List<Vector2>();
        public List<int> NeighborIds = new List<int>();
    }
    [Serializable] public sealed class CampaignFactionState
    {
        public Faction Faction;
        public bool Eliminated;
        // MVP resolves this reference through StartingDeckCatalog, independent of collection ownership.
        public string ActiveDeckReference = "standard";
    }
    [Serializable] public sealed class CampaignOperation
    {
        public string OperationId, MatchId, SelectedHumanDeckId;
        public Faction AttackerFaction, DefenderFaction;
        public int SourceRegionId, TargetRegionId, RandomSeed;
        public bool Manual, TestAutoResolve, ResultRecorded, OwnershipApplied, RewardAcknowledged;
        public CampaignOutcome Outcome;
        public double AttackerScore, DefenderScore, WinChance, Roll;
        public string AttackerDeckName, DefenderDeckName;
        // Unity's inline serializer represents a null custom class as its default
        // field values. Recognize only that exact placeholder, never a real receipt.
        internal bool IsEmptyInlineValue => string.IsNullOrEmpty(OperationId) && string.IsNullOrEmpty(MatchId)
            && string.IsNullOrEmpty(SelectedHumanDeckId) && string.IsNullOrEmpty(AttackerDeckName) && string.IsNullOrEmpty(DefenderDeckName)
            && AttackerFaction == default(Faction) && DefenderFaction == default(Faction)
            && SourceRegionId == 0 && TargetRegionId == 0 && RandomSeed == 0
            && !Manual && !TestAutoResolve && !ResultRecorded && !OwnershipApplied && !RewardAcknowledged
            && Outcome == default(CampaignOutcome) && AttackerScore == 0 && DefenderScore == 0 && WinChance == 0 && Roll == 0;
    }
    [Serializable] public sealed class CampaignBattleRecord
    {
        public string OperationId, MatchId, RegionName, AttackerDeckName, DefenderDeckName;
        public int Round, TargetRegionId;
        public Faction Attacker, Defender;
        public CampaignOutcome Outcome;
        public double WinChance;
        public bool Manual, Captured;
    }
    [Serializable] public sealed class CampaignState
    {
        public int SchemaVersion = 1;
        public string CampaignId, PlanetName;
        public int Seed, RandomSequence, RoundNumber = 1, CurrentTurnIndex;
        public Faction HumanFaction;
        public CampaignPhase Phase;
        public List<Faction> TurnOrder = new List<Faction>();
        public List<RegionState> Regions = new List<RegionState>();
        public List<CampaignFactionState> Factions = new List<CampaignFactionState>();
        public CampaignOperation PendingOperation;
        public string PendingNotification;
        // Compact records, no card definitions. Retained for accurate lifetime campaign statistics.
        public List<CampaignBattleRecord> BattleHistory = new List<CampaignBattleRecord>();
        internal static CampaignState Deserialize(string json)
        {
            var state = JsonUtility.FromJson<CampaignState>(json);
            if (state != null && (state.Phase == CampaignPhase.AwaitingFactionAction || state.Phase == CampaignPhase.CampaignFinished)
                && state.PendingOperation?.IsEmptyInlineValue == true) state.PendingOperation = null;
            return state;
        }
        public CampaignState Copy() => Deserialize(JsonUtility.ToJson(this));
        public Faction CurrentFaction => TurnOrder[CurrentTurnIndex];
    }
    // Explicit RNG algorithm gives cross-runtime replay. Never reads UnityEngine.Random.
    public sealed class CampaignRandom
    {
        private uint state;
        public CampaignRandom(int seed) { state = unchecked((uint)seed); if (state == 0) state = 0x9e3779b9; }
        public uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        public double Value() => Next() / 4294967296.0;
        public int Range(int count) => (int)(Value() * count);
        public static int Derive(int seed, int sequence)
        { unchecked { uint x = (uint)seed + 0x9e3779b9u * ((uint)sequence + 1); x = (x ^ (x >> 16)) * 0x85ebca6bu; x = (x ^ (x >> 13)) * 0xc2b2ae35u; return (int)(x ^ (x >> 16)); } }
    }
}
