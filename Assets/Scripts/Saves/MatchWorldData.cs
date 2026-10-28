using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Progression;
using Game.Units;

namespace Game.Saves
{
    // File DTOs deliberately contain only Unity-serializable fields. Owner-boundary types
    // containing nullable values, tuples or readonly HexCoord fields never enter the file.
    [Serializable] internal sealed class MatchWorldData
    {
        public List<MatchPlayerData> players = new List<MatchPlayerData>();
        public List<CardState> cards = new List<CardState>();
        public List<MatchArmyData> armies = new List<MatchArmyData>();
        public List<MatchUnitData> units = new List<MatchUnitData>();
        public List<MatchBuildingData> buildings = new List<MatchBuildingData>();
        public List<MatchBonusData> bonuses = new List<MatchBonusData>();
        public List<MatchEventData> events = new List<MatchEventData>();
        public List<MatchBattleData> battles = new List<MatchBattleData>();
        public MatchAntiAirData antiAir = new MatchAntiAirData();
        public StealthState stealth = new StealthState();
    }
    [Serializable] internal sealed class MatchPlayerData
    {
        public string id, nickname, selectedDeckId, aiDeckName, aiGrantedLeaderKey;
        public int colorIndex;
        public Faction faction;
        public bool human, neutral, eliminated, hasCitadel, hasAiBlueprints;
        public int citadelQ, citadelR;
        public List<string> aiBlueprints = new List<string>();
        public bool hasLoadout, hasQuota, hasHand, hasEconomy;
        public SavedDeck loadout;
        public BlueprintQuotaState quota;
        public MatchHandData hand;
        public List<int> tactics = new List<int>();
        public int tacticsVersion;
        public PlayerEconomyState economy;
    }
    [Serializable] internal sealed class MatchHandData
    {
        public bool ai;
        public int capacity, currentTurn, mutationVersion;
        public List<int> cards = new List<int>();
        public List<string> deck = new List<string>();
    }
    [Serializable] internal sealed class MatchArmyData
    {
        public int id, q, r;
        public string name, owner, markerPrefab, markerIcon;
        public bool registered, garrison, prison, airfield, airArmy, wingLanded;
        public int carrierUnit = -1, launchCarrier = -1;
        public List<int> members = new List<int>();
        public ArmyActivationState activation;
        public List<MatchArrangementData> arrangement = new List<MatchArrangementData>();
        public bool hasLastStrike, lastStrikeAttacked, hasStrikePolicy, hasStrikeTarget;
        public int lastStrikeQ, lastStrikeR, strikeKind, strikeTarget, minimumSurvivors;
    }
    [Serializable] internal sealed class MatchArrangementData { public int unitId, row, col; }
    [Serializable] internal sealed class MatchUnitData
    {
        public int id;
        public string name, owner, capturedFrom, origin, equipment, mutator, art, detailArt;
        public List<string> abilities = new List<string>();
        public List<UnitTypeTag> typeTags = new List<UnitTypeTag>();
        public bool hidden, enteredStealth, hero, prisoner, summoned, aviation;
        public BattleRow row;
        public int attack, defense, range, hpMax, hp, initiative, fateMax, fate, commandRating;
        public int activationApCost, apCost, berserkStacks, berserkDefenseLost, drillAttackBonus;
        public int moveMax, legacyMove, spent;
        public bool hasSpent;
        public int transportCarrier = -1, transportArmy = -1, airfieldArmy = -1, flightDeckArmy = -1;
        public int launchEnergyCost, turnsWithoutRefuel, antiAirRadius, airfieldCapacity, unlandedEnds;
        public bool emergencyPenalty, airAttacked, sortiePaid;
        public bool hasOriginalCost, hasRepairCost, hasAttachmentBase, hasAttachmentApplied, hasAttachmentResources;
        public ResourceCost originalCost, repairCost;
        public MatchAttachmentData attachmentBase, attachmentApplied;
        public MatchAttachmentResourceData attachmentResources;
    }
    [Serializable] internal sealed class MatchAttachmentData
    {
        public List<EquipmentStatChange> stats = new List<EquipmentStatChange>();
        public List<string> abilities = new List<string>();
    }
    [Serializable] internal sealed class MatchAttachmentResourceData
    { public int hpSpent, moveSpent, fateSpent, lastHp, lastMove, lastFate; }
    [Serializable] internal sealed class MatchBuildingData
    {
        public int q, r;
        public string name, owner, art, detailArt, markerPrefab, markerIcon;
        public List<string> abilities = new List<string>();
        public bool isBase, startingCitadel, tiered;
        public int level, hp, hpMax, defense, fate, airfieldCapacity;
        // Null entries retain the exact authored slot identity.
        public List<MatchFacilityData> facilities = new List<MatchFacilityData>();
    }
    [Serializable] internal sealed class MatchFacilityData
    {
        public bool occupied;
        public string name, art, detailArt;
        public int upgradeLevel, hp, hpMax, defense, fate, attack, antiAirRadius;
        public List<string> abilities = new List<string>();
    }
    [Serializable] internal sealed class MatchBonusData { public int q, r; public ResourceYields bonus; }
    [Serializable] internal sealed class MatchBattleData { public int q, r; public List<int> armies = new List<int>(); }
    [Serializable] internal sealed class MatchAntiAirData
    {
        public List<int> fired = new List<int>();
        public List<Game.Aviation.AntiAirPromptState> prompted = new List<Game.Aviation.AntiAirPromptState>();
        public List<MatchGunnerData> gunners = new List<MatchGunnerData>();
    }
    [Serializable] internal sealed class MatchGunnerData { public int q, r, slot, unitId; }
    [Serializable] internal sealed class MatchEventData
    {
        public int q, r;
        public string definition, guardName, guardOwner, consumedBy, rewardError;
        public bool consumed, triggered, rewardPending, skipped;
        public List<DeckCardEntry> guard = new List<DeckCardEntry>();
        public List<string> discovered = new List<string>(), known = new List<string>();
        public List<RewardEntry> rewards = new List<RewardEntry>();
        public List<int> selectedRewards = new List<int>();
        public List<MatchResolvedRewardData> resolvedRewards = new List<MatchResolvedRewardData>();
    }
    [Serializable] internal sealed class MatchResolvedRewardData { public int rewardIndex; public string card; }
}
