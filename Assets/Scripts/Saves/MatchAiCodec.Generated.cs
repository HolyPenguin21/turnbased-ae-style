// Generated explicit typed mappers; runtime serialization does not inspect types.
using System;
using System.Linq;
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
namespace Game.Saves
{
    internal static partial class MatchAiCodec
    {
        private static MatchAiData Write0(global::Game.Ai.AiPersistentState<MatchAiObjectiveData> value, Context c)
        {
            if (value == null) return new MatchAiData();
            return new MatchAiData { HasData = true, Map = Write1(value.Map, c), Missions = Write41(value.Missions, c), Sorties = Write73(value.Sorties, c), Radar = Write79(value.Radar, c), ForceBaseline = Write83(value.ForceBaseline, c), PreparationDelivery = Write84(value.PreparationDelivery, c), Recon = Write87(value.Recon, c), Intel = Write92(value.Intel, c), Patrols = Write94(value.Patrols, c), ReconSorties = Write102(value.ReconSorties, c), ReconCoverage = Write107(value.ReconCoverage, c), ReconDeficits = Write110(value.ReconDeficits, c), ScoutTrails = Write114(value.ScoutTrails, c), Starvation = Write118(value.Starvation, c), DevelopmentGate = Write129(value.DevelopmentGate, c), Allocator = Write131(value.Allocator, c), Initiative = Write134(value.Initiative, c), PublicInitiative = Write137(value.PublicInitiative, c), MobilizationHold = Write91(value.MobilizationHold, c), DevelopmentHistory = Write138(value.DevelopmentHistory, c), ApPressure = Write141(value.ApPressure, c), LifecycleWaits = Write144(value.LifecycleWaits, c) };
        }
        private static global::Game.Ai.AiPersistentState<MatchAiObjectiveData> Read0(MatchAiData data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.AiPersistentState<MatchAiObjectiveData>(true, Read1(data.Map, c), Read41(data.Missions, c), Read73(data.Sorties, c), Read79(data.Radar, c), Read83(data.ForceBaseline, c), Read84(data.PreparationDelivery, c), Read87(data.Recon, c), Read92(data.Intel, c), Read94(data.Patrols, c), Read102(data.ReconSorties, c), Read107(data.ReconCoverage, c), Read110(data.ReconDeficits, c), Read114(data.ScoutTrails, c), Read118(data.Starvation, c), Read129(data.DevelopmentGate, c), Read131(data.Allocator, c), Read134(data.Initiative, c), Read137(data.PublicInitiative, c), Read91(data.MobilizationHold, c), Read138(data.DevelopmentHistory, c), Read141(data.ApPressure, c), Read144(data.LifecycleWaits, c));
        }
        private static MatchAiPersistentState1 Write1(global::Game.Ai.AiMapMemory.PersistentState value, Context c)
        {
            if (value == null) return new MatchAiPersistentState1();
            return new MatchAiPersistentState1 { HasData = true, CurrentTurn = Write2(value.CurrentTurn, c), Players = Write3(value.Players, c) };
        }
        private static global::Game.Ai.AiMapMemory.PersistentState Read1(MatchAiPersistentState1 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.AiMapMemory.PersistentState(true, Read2(data.CurrentTurn, c), Read3(data.Players, c));
        }
        private static int Write2(int value, Context c)
        {
            return value;
        }
        private static int Read2(int data, Context c)
        {
            return data;
        }
        private static MatchAiMap3 Write3(global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.AiMapMemory.PlayerMemoryState> value, Context c)
        {
            return new MatchAiMap3 { HasData = value != null, Entries = value == null ? new List<MatchAiMap3.Entry>() : value.Select(x => new MatchAiMap3.Entry { Key = Write4(x.Key, c), Value = Write5(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.AiMapMemory.PlayerMemoryState> Read3(MatchAiMap3 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::Game.Ai.AiMapMemory.PlayerMemoryState>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read5(entry.Value, c)); }
            return result;
        }
        private static string Write4(string value, Context c)
        {
            return value;
        }
        private static string Read4(string data, Context c)
        {
            return data;
        }
        private static MatchAiPlayerMemoryState5 Write5(global::Game.Ai.AiMapMemory.PlayerMemoryState value, Context c)
        {
            if (value == null) return new MatchAiPlayerMemoryState5();
            if (value.DestroyedArmies != null) foreach (int id in value.DestroyedArmies) c.RecordArmy(id);
            return new MatchAiPlayerMemoryState5 { HasData = true, Resources = Write6(value.Resources, c), Enemies = Write11(value.Enemies, c), Aircraft = Write27(value.Aircraft, c), Buildings = Write32(value.Buildings, c), Guards = Write36(value.Guards, c), Events = Write38(value.Events, c), StructureLosses = Write39(value.StructureLosses, c), DestroyedArmies = Write31(value.DestroyedArmies, c), RefutedCitadels = Write40(value.RefutedCitadels, c) };
        }
        private static global::Game.Ai.AiMapMemory.PlayerMemoryState Read5(MatchAiPlayerMemoryState5 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.AiMapMemory.PlayerMemoryState(true, Read6(data.Resources, c), Read11(data.Enemies, c), Read27(data.Aircraft, c), Read32(data.Buildings, c), Read36(data.Guards, c), Read38(data.Events, c), Read39(data.StructureLosses, c), Read31(data.DestroyedArmies, c), Read40(data.RefutedCitadels, c));
        }
        private static MatchAiList6 Write6(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.KnownResourceHex> value, Context c)
        {
            return new MatchAiList6 { HasData = value != null, Values = value == null ? new List<MatchAiKnownResourceHex7>() : value.Select(x => Write7(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.KnownResourceHex> Read6(MatchAiList6 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read7(x, c)).ToList();
        }
        private static MatchAiKnownResourceHex7 Write7(global::Game.Ai.AiMapMemory.KnownResourceHex value, Context c)
        {
            return new MatchAiKnownResourceHex7 { HasData = true, Hex = Write8(value.Hex, c), DominantType = Write9(value.DominantType, c), Yield = Write10(value.Yield, c) };
        }
        private static global::Game.Ai.AiMapMemory.KnownResourceHex Read7(MatchAiKnownResourceHex7 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.KnownResourceHex(true, Read8(data.Hex, c), Read9(data.DominantType, c), Read10(data.Yield, c));
        }
        private static MatchAiHexCoord8 Write8(global::Game.HexGrid.HexCoord value, Context c)
        {
            return new MatchAiHexCoord8 { HasData = true, Q = Write2(value.Q, c), R = Write2(value.R, c) };
        }
        private static global::Game.HexGrid.HexCoord Read8(MatchAiHexCoord8 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.HexGrid.HexCoord(true, Read2(data.Q, c), Read2(data.R, c));
        }
        private static int Write9(global::Game.Economy.ResourceType value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Economy.ResourceType Read9(int data, Context c)
        {
            return CheckedEnum<global::Game.Economy.ResourceType>(data);
        }
        private static MatchAiResourceYields10 Write10(global::Game.Economy.ResourceYields value, Context c)
        {
            if (value == null) return new MatchAiResourceYields10();
            return new MatchAiResourceYields10 { HasData = true, human = Write2(value.human, c), energy = Write2(value.energy, c), materials = Write2(value.materials, c), tech = Write2(value.tech, c) };
        }
        private static global::Game.Economy.ResourceYields Read10(MatchAiResourceYields10 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Economy.ResourceYields { human = Read2(data.human, c), energy = Read2(data.energy, c), materials = Read2(data.materials, c), tech = Read2(data.tech, c) };
        }
        private static MatchAiList11 Write11(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting>> value, Context c)
        {
            return new MatchAiList11 { HasData = value != null, Values = value == null ? new List<MatchAiOwnedObservation12>() : value.Select(x => Write12(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting>> Read11(MatchAiList11 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read12(x, c)).ToList();
        }
        private static MatchAiOwnedObservation12 Write12(global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting> value, Context c)
        {
            return new MatchAiOwnedObservation12 { HasData = true, OwnerId = Write4(value.OwnerId, c), Observation = Write13(value.Observation, c) };
        }
        private static global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting> Read12(MatchAiOwnedObservation12 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting>(true, Read4(data.OwnerId, c), Read13(data.Observation, c));
        }
        private static MatchAiKnownEnemySighting13 Write13(global::Game.Ai.AiMapMemory.KnownEnemySighting value, Context c)
        {
            c.RecordArmy(value.ArmyId);
            return new MatchAiKnownEnemySighting13 { HasData = true, ArmyId = Write2(value.ArmyId, c), Hex = Write8(value.Hex, c), Owner = Write14(value.Owner, c), Name = Write4(value.Name, c), MemberCount = Write2(value.MemberCount, c), DefenseSum = Write15(value.DefenseSum, c), AttackSum = Write15(value.AttackSum, c), Defenders = Write16(value.Defenders, c), HasAntiAir = Write18(value.HasAntiAir, c), RecceRadius = Write2(value.RecceRadius, c), RecceSpotStrength = Write2(value.RecceSpotStrength, c), SeenTurn = Write2(value.SeenTurn, c), IsGarrison = Write18(value.IsGarrison, c), IsAir = Write18(value.IsAir, c), MaxMovement = Write2(value.MaxMovement, c), Commander = Write26(value.Commander, c) };
        }
        private static global::Game.Ai.AiMapMemory.KnownEnemySighting Read13(MatchAiKnownEnemySighting13 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.KnownEnemySighting(true, Read2(data.ArmyId, c), Read8(data.Hex, c), Read14(data.Owner, c), Read4(data.Name, c), Read2(data.MemberCount, c), Read15(data.DefenseSum, c), Read15(data.AttackSum, c), Read16(data.Defenders, c), Read18(data.HasAntiAir, c), Read2(data.RecceRadius, c), Read2(data.RecceSpotStrength, c), Read2(data.SeenTurn, c), Read18(data.IsGarrison, c), Read18(data.IsAir, c), Read2(data.MaxMovement, c), Read26(data.Commander, c));
        }
        private static MatchAiPlayerRef Write14(global::Game.Players.PlayerSetupData value, Context c)
        {
            return c.WritePlayer(value);
        }
        private static global::Game.Players.PlayerSetupData Read14(MatchAiPlayerRef data, Context c)
        {
            return c.ReadPlayer(data);
        }
        private static float Write15(float value, Context c)
        {
            return Finite(value);
        }
        private static float Read15(float data, Context c)
        {
            return Finite(data);
        }
        private static MatchAiList16 Write16(global::System.Collections.Generic.IReadOnlyList<global::Game.Combat.WorthIt.DefenderProfile> value, Context c)
        {
            return new MatchAiList16 { HasData = value != null, Values = value == null ? new List<MatchAiDefenderProfile17>() : value.Select(x => Write17(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Combat.WorthIt.DefenderProfile> Read16(MatchAiList16 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read17(x, c)).ToList();
        }
        private static MatchAiDefenderProfile17 Write17(global::Game.Combat.WorthIt.DefenderProfile value, Context c)
        {
            return new MatchAiDefenderProfile17 { HasData = true, SingleUseUnit = Write18(value.SingleUseUnit, c), CanDisruptBuilding = Write18(value.CanDisruptBuilding, c), Consumption = Write19(value.Consumption, c), Defense = Write15(value.Defense, c), HasCeramicArmor = Write18(value.HasCeramicArmor, c), TypeTags = Write24(value.TypeTags, c), Attack = Write15(value.Attack, c), HitPoints = Write15(value.HitPoints, c), MaxHitPoints = Write15(value.MaxHitPoints, c), Initiative = Write2(value.Initiative, c), Abilities = Write23(value.Abilities, c), IsGroundCombatant = Write18(value.IsGroundCombatant, c), IsHero = Write18(value.IsHero, c), FateMax = Write2(value.FateMax, c), IsSummoned = Write18(value.IsSummoned, c), Range = Write2(value.Range, c) };
        }
        private static global::Game.Combat.WorthIt.DefenderProfile Read17(MatchAiDefenderProfile17 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Combat.WorthIt.DefenderProfile(true, Read18(data.SingleUseUnit, c), Read18(data.CanDisruptBuilding, c), Read19(data.Consumption, c), Read15(data.Defense, c), Read18(data.HasCeramicArmor, c), Read24(data.TypeTags, c), Read15(data.Attack, c), Read15(data.HitPoints, c), Read15(data.MaxHitPoints, c), Read2(data.Initiative, c), Read23(data.Abilities, c), Read18(data.IsGroundCombatant, c), Read18(data.IsHero, c), Read2(data.FateMax, c), Read18(data.IsSummoned, c), Read2(data.Range, c));
        }
        private static bool Write18(bool value, Context c)
        {
            return value;
        }
        private static bool Read18(bool data, Context c)
        {
            return data;
        }
        private static MatchAiEquipmentConsumption19 Write19(global::Game.Cards.EquipmentConsumption value, Context c)
        {
            if (value == null) return new MatchAiEquipmentConsumption19();
            return new MatchAiEquipmentConsumption19 { HasData = true, Before = Write20(value.Before, c), After = Write20(value.After, c), HpSpentExcess = Write2(value.HpSpentExcess, c), FateSpentExcess = Write2(value.FateSpentExcess, c), Key = Write4(value.Key, c) };
        }
        private static global::Game.Cards.EquipmentConsumption Read19(MatchAiEquipmentConsumption19 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Cards.EquipmentConsumption(true, Read20(data.Before, c), Read20(data.After, c), Read2(data.HpSpentExcess, c), Read2(data.FateSpentExcess, c), Read4(data.Key, c));
        }
        private static MatchAiPredictedEquipmentState20 Write20(global::Game.Cards.PredictedEquipmentState value, Context c)
        {
            return new MatchAiPredictedEquipmentState20 { HasData = true, Stats = Write21(value.Stats, c), Abilities = Write23(value.Abilities, c) };
        }
        private static global::Game.Cards.PredictedEquipmentState Read20(MatchAiPredictedEquipmentState20 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Cards.PredictedEquipmentState(true, Read21(data.Stats, c), Read23(data.Abilities, c));
        }
        private static MatchAiMap21 Write21(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Cards.EquipmentStat, int> value, Context c)
        {
            return new MatchAiMap21 { HasData = value != null, Entries = value == null ? new List<MatchAiMap21.Entry>() : value.Select(x => new MatchAiMap21.Entry { Key = Write22(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Cards.EquipmentStat, int> Read21(MatchAiMap21 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Cards.EquipmentStat, int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read22(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static int Write22(global::Game.Cards.EquipmentStat value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Cards.EquipmentStat Read22(int data, Context c)
        {
            return CheckedEnum<global::Game.Cards.EquipmentStat>(data);
        }
        private static MatchAiList23 Write23(global::System.Collections.Generic.IReadOnlyList<string> value, Context c)
        {
            return new MatchAiList23 { HasData = value != null, Values = value == null ? new List<string>() : value.Select(x => Write4(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<string> Read23(MatchAiList23 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read4(x, c)).ToList();
        }
        private static MatchAiList24 Write24(global::System.Collections.Generic.IReadOnlyList<global::Game.Cards.UnitTypeTag> value, Context c)
        {
            return new MatchAiList24 { HasData = value != null, Values = value == null ? new List<int>() : value.Select(x => Write25(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Cards.UnitTypeTag> Read24(MatchAiList24 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read25(x, c)).ToList();
        }
        private static int Write25(global::Game.Cards.UnitTypeTag value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Cards.UnitTypeTag Read25(int data, Context c)
        {
            return CheckedEnum<global::Game.Cards.UnitTypeTag>(data);
        }
        private static MatchAiSideCommander26 Write26(global::Game.Combat.WorthIt.SideCommander value, Context c)
        {
            return new MatchAiSideCommander26 { HasData = true, Present = Write18(value.Present, c), Initiative = Write2(value.Initiative, c), Fate = Write2(value.Fate, c), SupportHeroes = Write16(value.SupportHeroes, c) };
        }
        private static global::Game.Combat.WorthIt.SideCommander Read26(MatchAiSideCommander26 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Combat.WorthIt.SideCommander(true, Read18(data.Present, c), Read2(data.Initiative, c), Read2(data.Fate, c), Read16(data.SupportHeroes, c));
        }
        private static MatchAiList27 Write27(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting>> value, Context c)
        {
            return new MatchAiList27 { HasData = value != null, Values = value == null ? new List<MatchAiOwnedObservation28>() : value.Select(x => Write28(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting>> Read27(MatchAiList27 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read28(x, c)).ToList();
        }
        private static MatchAiOwnedObservation28 Write28(global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting> value, Context c)
        {
            return new MatchAiOwnedObservation28 { HasData = true, OwnerId = Write4(value.OwnerId, c), Observation = Write29(value.Observation, c) };
        }
        private static global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting> Read28(MatchAiOwnedObservation28 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting>(true, Read4(data.OwnerId, c), Read29(data.Observation, c));
        }
        private static MatchAiKnownAirSighting29 Write29(global::Game.Ai.AiMapMemory.KnownAirSighting value, Context c)
        {
            return new MatchAiKnownAirSighting29 { HasData = true, Hex = Write8(value.Hex, c), Owner = Write14(value.Owner, c), SeenTurn = Write2(value.SeenTurn, c), IsGroundArmy = Write18(value.IsGroundArmy, c), Roster = Write30(value.Roster, c) };
        }
        private static global::Game.Ai.AiMapMemory.KnownAirSighting Read29(MatchAiKnownAirSighting29 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.KnownAirSighting(true, Read8(data.Hex, c), Read14(data.Owner, c), Read2(data.SeenTurn, c), Read18(data.IsGroundArmy, c), Read30(data.Roster, c));
        }
        private static MatchAiDefendingAirArmy30 Write30(global::Game.Combat.AviationCombatEstimator.DefendingAirArmy value, Context c)
        {
            c.RecordArmy(value.ArmyId);
            return new MatchAiDefendingAirArmy30 { HasData = true, ArmyId = Write2(value.ArmyId, c), Units = Write16(value.Units, c), CurrentFates = Write31(value.CurrentFates, c), CommanderIndex = Write2(value.CommanderIndex, c) };
        }
        private static global::Game.Combat.AviationCombatEstimator.DefendingAirArmy Read30(MatchAiDefendingAirArmy30 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Combat.AviationCombatEstimator.DefendingAirArmy(true, Read2(data.ArmyId, c), Read16(data.Units, c), Read31(data.CurrentFates, c), Read2(data.CommanderIndex, c));
        }
        private static MatchAiList31 Write31(global::System.Collections.Generic.IReadOnlyList<int> value, Context c)
        {
            return new MatchAiList31 { HasData = value != null, Values = value == null ? new List<int>() : value.Select(x => Write2(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<int> Read31(MatchAiList31 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read2(x, c)).ToList();
        }
        private static MatchAiList32 Write32(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding>> value, Context c)
        {
            return new MatchAiList32 { HasData = value != null, Values = value == null ? new List<MatchAiOwnedObservation33>() : value.Select(x => Write33(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding>> Read32(MatchAiList32 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read33(x, c)).ToList();
        }
        private static MatchAiOwnedObservation33 Write33(global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding> value, Context c)
        {
            return new MatchAiOwnedObservation33 { HasData = true, OwnerId = Write4(value.OwnerId, c), Observation = Write34(value.Observation, c) };
        }
        private static global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding> Read33(MatchAiOwnedObservation33 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding>(true, Read4(data.OwnerId, c), Read34(data.Observation, c));
        }
        private static MatchAiKnownBuilding34 Write34(global::Game.Ai.AiMapMemory.KnownBuilding value, Context c)
        {
            return new MatchAiKnownBuilding34 { HasData = true, ExplosionStructures = Write16(value.ExplosionStructures, c), Hex = Write8(value.Hex, c), Owner = Write14(value.Owner, c), IsStartingCitadel = Write18(value.IsStartingCitadel, c), IsBase = Write18(value.IsBase, c), FacilityAbilities = Write35(value.FacilityAbilities, c), CollectedAmounts = Write31(value.CollectedAmounts, c), FreeFacilitySlots = Write2(value.FreeFacilitySlots, c), Defense = Write15(value.Defense, c), SeenTurn = Write2(value.SeenTurn, c) };
        }
        private static global::Game.Ai.AiMapMemory.KnownBuilding Read34(MatchAiKnownBuilding34 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.KnownBuilding(true, Read16(data.ExplosionStructures, c), Read8(data.Hex, c), Read14(data.Owner, c), Read18(data.IsStartingCitadel, c), Read18(data.IsBase, c), Read35(data.FacilityAbilities, c), Read31(data.CollectedAmounts, c), Read2(data.FreeFacilitySlots, c), Read15(data.Defense, c), Read2(data.SeenTurn, c));
        }
        private static MatchAiList35 Write35(global::System.Collections.Generic.IReadOnlyCollection<string> value, Context c)
        {
            return new MatchAiList35 { HasData = value != null, Values = value == null ? new List<string>() : value.Select(x => Write4(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyCollection<string> Read35(MatchAiList35 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read4(x, c)).ToList();
        }
        private static MatchAiMap36 Write36(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::Game.Ai.AiMapMemory.GuardStrength> value, Context c)
        {
            return new MatchAiMap36 { HasData = value != null, Entries = value == null ? new List<MatchAiMap36.Entry>() : value.Select(x => new MatchAiMap36.Entry { Key = Write8(x.Key, c), Value = Write37(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::Game.Ai.AiMapMemory.GuardStrength> Read36(MatchAiMap36 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.HexGrid.HexCoord, global::Game.Ai.AiMapMemory.GuardStrength>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read8(entry.Key, c), Read37(entry.Value, c)); }
            return result;
        }
        private static MatchAiGuardStrength37 Write37(global::Game.Ai.AiMapMemory.GuardStrength value, Context c)
        {
            return new MatchAiGuardStrength37 { HasData = true, Defense = Write15(value.Defense, c), Attack = Write15(value.Attack, c), Defenders = Write16(value.Defenders, c), Name = Write4(value.Name, c), Commander = Write26(value.Commander, c), RewardTier = Write2(value.RewardTier, c) };
        }
        private static global::Game.Ai.AiMapMemory.GuardStrength Read37(MatchAiGuardStrength37 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.AiMapMemory.GuardStrength(true, Read15(data.Defense, c), Read15(data.Attack, c), Read16(data.Defenders, c), Read4(data.Name, c), Read26(data.Commander, c), Read2(data.RewardTier, c));
        }
        private static MatchAiMap38 Write38(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, bool> value, Context c)
        {
            return new MatchAiMap38 { HasData = value != null, Entries = value == null ? new List<MatchAiMap38.Entry>() : value.Select(x => new MatchAiMap38.Entry { Key = Write8(x.Key, c), Value = Write18(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, bool> Read38(MatchAiMap38 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.HexGrid.HexCoord, bool>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read8(entry.Key, c), Read18(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap39 Write39(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int> value, Context c)
        {
            return new MatchAiMap39 { HasData = value != null, Entries = value == null ? new List<MatchAiMap39.Entry>() : value.Select(x => new MatchAiMap39.Entry { Key = Write8(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int> Read39(MatchAiMap39 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.HexGrid.HexCoord, int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read8(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static MatchAiList40 Write40(global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> value, Context c)
        {
            return new MatchAiList40 { HasData = value != null, Values = value == null ? new List<MatchAiHexCoord8>() : value.Select(x => Write8(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Read40(MatchAiList40 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read8(x, c)).ToList();
        }
        private static MatchAiMap41 Write41(global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.MissionIntentState.PersistentState<MatchAiObjectiveData>> value, Context c)
        {
            return new MatchAiMap41 { HasData = value != null, Entries = value == null ? new List<MatchAiMap41.Entry>() : value.Select(x => new MatchAiMap41.Entry { Key = Write4(x.Key, c), Value = Write42(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.MissionIntentState.PersistentState<MatchAiObjectiveData>> Read41(MatchAiMap41 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::Game.Ai.V2.MissionIntentState.PersistentState<MatchAiObjectiveData>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read42(entry.Value, c)); }
            return result;
        }
        private static MatchAiPersistentState42 Write42(global::Game.Ai.V2.MissionIntentState.PersistentState<MatchAiObjectiveData> value, Context c)
        {
            if (value == null) return new MatchAiPersistentState42();
            return new MatchAiPersistentState42 { HasData = true, Intents = Write43(value.Intents, c), RetreatWitnesses = Write53(value.RetreatWitnesses, c), Economy = Write57(value.Economy, c), Development = Write70(value.Development, c) };
        }
        private static global::Game.Ai.V2.MissionIntentState.PersistentState<MatchAiObjectiveData> Read42(MatchAiPersistentState42 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.MissionIntentState.PersistentState<MatchAiObjectiveData>(true, Read43(data.Intents, c), Read53(data.RetreatWitnesses, c), Read57(data.Economy, c), Read70(data.Development, c));
        }
        private static MatchAiList43 Write43(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.IntentState<MatchAiObjectiveData>> value, Context c)
        {
            return new MatchAiList43 { HasData = value != null, Values = value == null ? new List<MatchAiIntentState44>() : value.Select(x => Write44(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.IntentState<MatchAiObjectiveData>> Read43(MatchAiList43 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read44(x, c)).ToList();
        }
        private static MatchAiIntentState44 Write44(global::Game.Ai.V2.MissionIntentState.IntentState<MatchAiObjectiveData> value, Context c)
        {
            if (value == null) return new MatchAiIntentState44();
            if (value.PreferredMoverArmyId.HasValue) c.RecordArmy(value.PreferredMoverArmyId.Value);
            return new MatchAiIntentState44 { HasData = true, IntentKey = Write45(value.IntentKey, c), LastAttemptKey = Write48(value.LastAttemptKey, c), Kind = Write46(value.Kind, c), Funding = Write49(value.Funding, c), Status = Write50(value.Status, c), Suspended = Write51(value.Suspended, c), CreatedTurn = Write2(value.CreatedTurn, c), TurnsActive = Write2(value.TurnsActive, c), LastReconciledTurn = Write2(value.LastReconciledTurn, c), LastProgressTurn = Write2(value.LastProgressTurn, c), LastProtectedTurn = Write2(value.LastProtectedTurn, c), StallTurns = Write2(value.StallTurns, c), LastIntrinsicValue = Write15(value.LastIntrinsicValue, c), CumulativeApSpent = Write15(value.CumulativeApSpent, c), StepsMovedTotal = Write2(value.StepsMovedTotal, c), PreferredMoverArmyId = Write52(value.PreferredMoverArmyId, c), Objective = Write195(value.Objective, c) };
        }
        private static global::Game.Ai.V2.MissionIntentState.IntentState<MatchAiObjectiveData> Read44(MatchAiIntentState44 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.MissionIntentState.IntentState<MatchAiObjectiveData>(true, Read45(data.IntentKey, c), Read48(data.LastAttemptKey, c), Read46(data.Kind, c), Read49(data.Funding, c), Read50(data.Status, c), Read51(data.Suspended, c), Read2(data.CreatedTurn, c), Read2(data.TurnsActive, c), Read2(data.LastReconciledTurn, c), Read2(data.LastProgressTurn, c), Read2(data.LastProtectedTurn, c), Read2(data.StallTurns, c), Read15(data.LastIntrinsicValue, c), Read15(data.CumulativeApSpent, c), Read2(data.StepsMovedTotal, c), Read52(data.PreferredMoverArmyId, c), Read195(data.Objective, c));
        }
        private static MatchAiMissionIntentKey45 Write45(global::Game.Ai.V2.MissionIntentKey value, Context c)
        {
            return new MatchAiMissionIntentKey45 { HasData = true, Kind = Write46(value.Kind, c), SubKind = Write2(value.SubKind, c), ObjectiveId = Write2(value.ObjectiveId, c), Q = Write2(value.Q, c), R = Write2(value.R, c), TargetKind = Write47(value.TargetKind, c) };
        }
        private static global::Game.Ai.V2.MissionIntentKey Read45(MatchAiMissionIntentKey45 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.MissionIntentKey(true, Read46(data.Kind, c), Read2(data.SubKind, c), Read2(data.ObjectiveId, c), Read2(data.Q, c), Read2(data.R, c), Read47(data.TargetKind, c));
        }
        private static int Write46(global::Game.Ai.V2.MissionKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.MissionKind Read46(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.MissionKind>(data);
        }
        private static int Write47(global::Game.Ai.V2.RaidTargetKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.RaidTargetKind Read47(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.RaidTargetKind>(data);
        }
        private static MatchAiStableMissionKey48 Write48(global::Game.Ai.V2.StableMissionKey value, Context c)
        {
            return new MatchAiStableMissionKey48 { HasData = true, Kind = Write46(value.Kind, c), SubKind = Write2(value.SubKind, c), TargetId = Write2(value.TargetId, c), Q = Write2(value.Q, c), R = Write2(value.R, c), TargetKind = Write47(value.TargetKind, c), ActorId = Write2(value.ActorId, c), DetailId = Write2(value.DetailId, c) };
        }
        private static global::Game.Ai.V2.StableMissionKey Read48(MatchAiStableMissionKey48 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.StableMissionKey(true, Read46(data.Kind, c), Read2(data.SubKind, c), Read2(data.TargetId, c), Read2(data.Q, c), Read2(data.R, c), Read47(data.TargetKind, c), Read2(data.ActorId, c), Read2(data.DetailId, c));
        }
        private static int Write49(global::Game.Ai.V2.CommitmentTier value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.CommitmentTier Read49(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.CommitmentTier>(data);
        }
        private static int Write50(global::Game.Ai.V2.IntentStatus value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.IntentStatus Read50(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.IntentStatus>(data);
        }
        private static int Write51(global::Game.Ai.V2.SuspendReason value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.SuspendReason Read51(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.SuspendReason>(data);
        }
        private static MatchAiOptional52 Write52(int? value, Context c)
        {
            return new MatchAiOptional52 { HasData = value.HasValue, Value = value.HasValue ? Write2(value.Value, c) : default };
        }
        private static int? Read52(MatchAiOptional52 data, Context c)
        {
            Required(data);
            return data.HasData ? (int?)Read2(data.Value, c) : null;
        }
        private static MatchAiList53 Write53(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.RetreatWitnessState> value, Context c)
        {
            return new MatchAiList53 { HasData = value != null, Values = value == null ? new List<MatchAiRetreatWitnessState54>() : value.Select(x => Write54(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.MissionIntentState.RetreatWitnessState> Read53(MatchAiList53 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read54(x, c)).ToList();
        }
        private static MatchAiRetreatWitnessState54 Write54(global::Game.Ai.V2.MissionIntentState.RetreatWitnessState value, Context c)
        {
            c.RecordArmy(value.OwnArmyId);
            c.RecordArmy(value.EnemyArmyId);
            return new MatchAiRetreatWitnessState54 { HasData = true, Target = Write55(value.Target, c), OwnArmyId = Write2(value.OwnArmyId, c), EnemyArmyId = Write2(value.EnemyArmyId, c), EnemyFingerprint = Write2(value.EnemyFingerprint, c), OwnFingerprint = Write2(value.OwnFingerprint, c), Turn = Write2(value.Turn, c), EnemyHex = Write8(value.EnemyHex, c), Reason = Write4(value.Reason, c) };
        }
        private static global::Game.Ai.V2.MissionIntentState.RetreatWitnessState Read54(MatchAiRetreatWitnessState54 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.MissionIntentState.RetreatWitnessState(true, Read55(data.Target, c), Read2(data.OwnArmyId, c), Read2(data.EnemyArmyId, c), Read2(data.EnemyFingerprint, c), Read2(data.OwnFingerprint, c), Read2(data.Turn, c), Read8(data.EnemyHex, c), Read4(data.Reason, c));
        }
        private static MatchAiRetreatTargetState55 Write55(global::Game.Ai.V2.MissionIntentState.RetreatTargetState value, Context c)
        {
            return new MatchAiRetreatTargetState55 { HasData = true, HasValue = Write18(value.HasValue, c), Hex = Write8(value.Hex, c), ExpectedOwnerId = Write4(value.ExpectedOwnerId, c), Kind = Write56(value.Kind, c) };
        }
        private static global::Game.Ai.V2.MissionIntentState.RetreatTargetState Read55(MatchAiRetreatTargetState55 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.MissionIntentState.RetreatTargetState(true, Read18(data.HasValue, c), Read8(data.Hex, c), Read4(data.ExpectedOwnerId, c), Read56(data.Kind, c));
        }
        private static int Write56(global::Game.Ai.V2.AttackTargetKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.AttackTargetKind Read56(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.AttackTargetKind>(data);
        }
        private static MatchAiPersistentState57 Write57(global::Game.Ai.V2.EconomyLifecycleState.PersistentState value, Context c)
        {
            if (value == null) return new MatchAiPersistentState57();
            return new MatchAiPersistentState57 { HasData = true, BaseDelivery = Write58(value.BaseDelivery, c), ExtractionDelivery = Write63(value.ExtractionDelivery, c), EndedOutboundBuilders = Write68(value.EndedOutboundBuilders, c) };
        }
        private static global::Game.Ai.V2.EconomyLifecycleState.PersistentState Read57(MatchAiPersistentState57 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.EconomyLifecycleState.PersistentState(true, Read58(data.BaseDelivery, c), Read63(data.ExtractionDelivery, c), Read68(data.EndedOutboundBuilders, c));
        }
        private static MatchAiDeliveryEvidence58 Write58(global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(int CardId, global::Game.HexGrid.HexCoord Hex)> value, Context c)
        {
            if (value == null) return new MatchAiDeliveryEvidence58();
            return new MatchAiDeliveryEvidence58 { HasData = true, Failures = Write59(value.Failures, c), SuppressedUntil = Write62(value.SuppressedUntil, c), ProgressTurns = Write62(value.ProgressTurns, c) };
        }
        private static global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(int CardId, global::Game.HexGrid.HexCoord Hex)> Read58(MatchAiDeliveryEvidence58 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(int CardId, global::Game.HexGrid.HexCoord Hex)>(true, Read59(data.Failures, c), Read62(data.SuppressedUntil, c), Read62(data.ProgressTurns, c));
        }
        private static MatchAiMap59 Write59(global::System.Collections.Generic.IReadOnlyDictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)> value, Context c)
        {
            return new MatchAiMap59 { HasData = value != null, Entries = value == null ? new List<MatchAiMap59.Entry>() : value.Select(x => new MatchAiMap59.Entry { Key = Write60(x.Key, c), Value = Write61(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)> Read59(MatchAiMap59 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read60(entry.Key, c), Read61(entry.Value, c)); }
            return result;
        }
        private static MatchAiTuple60 Write60((int CardId, global::Game.HexGrid.HexCoord Hex) value, Context c)
        {
            c.RecordCard(value.CardId);
            return new MatchAiTuple60 { HasData = true, CardId = Write2(value.CardId, c), Hex = Write8(value.Hex, c) };
        }
        private static (int CardId, global::Game.HexGrid.HexCoord Hex) Read60(MatchAiTuple60 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read2(data.CardId, c), Read8(data.Hex, c));
        }
        private static MatchAiTuple61 Write61((int Turn, int Count) value, Context c)
        {
            return new MatchAiTuple61 { HasData = true, Turn = Write2(value.Turn, c), Count = Write2(value.Count, c) };
        }
        private static (int Turn, int Count) Read61(MatchAiTuple61 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read2(data.Turn, c), Read2(data.Count, c));
        }
        private static MatchAiMap62 Write62(global::System.Collections.Generic.IReadOnlyDictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), int> value, Context c)
        {
            return new MatchAiMap62 { HasData = value != null, Entries = value == null ? new List<MatchAiMap62.Entry>() : value.Select(x => new MatchAiMap62.Entry { Key = Write60(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), int> Read62(MatchAiMap62 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<(int CardId, global::Game.HexGrid.HexCoord Hex), int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read60(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static MatchAiDeliveryEvidence63 Write63(global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex)> value, Context c)
        {
            if (value == null) return new MatchAiDeliveryEvidence63();
            return new MatchAiDeliveryEvidence63 { HasData = true, Failures = Write64(value.Failures, c), SuppressedUntil = Write67(value.SuppressedUntil, c), ProgressTurns = Write67(value.ProgressTurns, c) };
        }
        private static global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex)> Read63(MatchAiDeliveryEvidence63 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.EconomyLifecycleState.DeliveryEvidence<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex)>(true, Read64(data.Failures, c), Read67(data.SuppressedUntil, c), Read67(data.ProgressTurns, c));
        }
        private static MatchAiMap64 Write64(global::System.Collections.Generic.IReadOnlyDictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)> value, Context c)
        {
            return new MatchAiMap64 { HasData = value != null, Entries = value == null ? new List<MatchAiMap64.Entry>() : value.Select(x => new MatchAiMap64.Entry { Key = Write65(x.Key, c), Value = Write61(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)> Read64(MatchAiMap64 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), (int Turn, int Count)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read65(entry.Key, c), Read61(entry.Value, c)); }
            return result;
        }
        private static MatchAiTuple65 Write65((global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex) value, Context c)
        {
            return new MatchAiTuple65 { HasData = true, Resource = Write66(value.Resource, c), Hex = Write8(value.Hex, c) };
        }
        private static (global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex) Read65(MatchAiTuple65 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read66(data.Resource, c), Read8(data.Hex, c));
        }
        private static MatchAiOptional66 Write66(global::Game.Economy.ResourceType? value, Context c)
        {
            return new MatchAiOptional66 { HasData = value.HasValue, Value = value.HasValue ? Write9(value.Value, c) : default };
        }
        private static global::Game.Economy.ResourceType? Read66(MatchAiOptional66 data, Context c)
        {
            Required(data);
            return data.HasData ? (global::Game.Economy.ResourceType?)Read9(data.Value, c) : null;
        }
        private static MatchAiMap67 Write67(global::System.Collections.Generic.IReadOnlyDictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), int> value, Context c)
        {
            return new MatchAiMap67 { HasData = value != null, Entries = value == null ? new List<MatchAiMap67.Entry>() : value.Select(x => new MatchAiMap67.Entry { Key = Write65(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), int> Read67(MatchAiMap67 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<(global::Game.Economy.ResourceType? Resource, global::Game.HexGrid.HexCoord Hex), int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read65(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap68 Write68(global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, float Value)> value, Context c)
        {
            return new MatchAiMap68 { HasData = value != null, Entries = value == null ? new List<MatchAiMap68.Entry>() : value.Select(x => new MatchAiMap68.Entry { Key = Write2(x.Key, c), Value = Write69(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, float Value)> Read68(MatchAiMap68 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, (global::Game.HexGrid.HexCoord Site, float Value)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read69(entry.Value, c)); }
            return result;
        }
        private static MatchAiTuple69 Write69((global::Game.HexGrid.HexCoord Site, float Value) value, Context c)
        {
            return new MatchAiTuple69 { HasData = true, Site = Write8(value.Site, c), Value = Write15(value.Value, c) };
        }
        private static (global::Game.HexGrid.HexCoord Site, float Value) Read69(MatchAiTuple69 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read8(data.Site, c), Read15(data.Value, c));
        }
        private static MatchAiMap70 Write70(global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn)> value, Context c)
        {
            return new MatchAiMap70 { HasData = value != null, Entries = value == null ? new List<MatchAiMap70.Entry>() : value.Select(x => new MatchAiMap70.Entry { Key = Write2(x.Key, c), Value = Write71(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn)> Read70(MatchAiMap70 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, (global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read71(entry.Value, c)); }
            return result;
        }
        private static MatchAiTuple71 Write71((global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn) value, Context c)
        {
            return new MatchAiTuple71 { HasData = true, Site = Write8(value.Site, c), Mode = Write72(value.Mode, c), Turn = Write2(value.Turn, c) };
        }
        private static (global::Game.HexGrid.HexCoord Site, global::Game.Cards.ResearchProductionMode Mode, int Turn) Read71(MatchAiTuple71 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read8(data.Site, c), Read72(data.Mode, c), Read2(data.Turn, c));
        }
        private static int Write72(global::Game.Cards.ResearchProductionMode value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Cards.ResearchProductionMode Read72(int data, Context c)
        {
            return CheckedEnum<global::Game.Cards.ResearchProductionMode>(data);
        }
        private static MatchAiMap73 Write73(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.AirSortieRegistry.SortieState>> value, Context c)
        {
            return new MatchAiMap73 { HasData = value != null, Entries = value == null ? new List<MatchAiMap73.Entry>() : value.Select(x => new MatchAiMap73.Entry { Key = Write4(x.Key, c), Value = Write74(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.AirSortieRegistry.SortieState>> Read73(MatchAiMap73 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.AirSortieRegistry.SortieState>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read74(entry.Value, c)); }
            return result;
        }
        private static MatchAiList74 Write74(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.AirSortieRegistry.SortieState> value, Context c)
        {
            return new MatchAiList74 { HasData = value != null, Values = value == null ? new List<MatchAiSortieState75>() : value.Select(x => Write75(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.AirSortieRegistry.SortieState> Read74(MatchAiList74 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read75(x, c)).ToList();
        }
        private static MatchAiSortieState75 Write75(global::Game.Ai.V2.AirSortieRegistry.SortieState value, Context c)
        {
            c.RecordArmy(value.ArmyId);
            return new MatchAiSortieState75 { HasData = true, ArmyId = Write2(value.ArmyId, c), Kind = Write76(value.Kind, c), TargetHex = Write8(value.TargetHex, c), LandingHex = Write8(value.LandingHex, c), Outbound = Write18(value.Outbound, c), HeldTurn = Write52(value.HeldTurn, c), StrikePolicy = Write77(value.StrikePolicy, c) };
        }
        private static global::Game.Ai.V2.AirSortieRegistry.SortieState Read75(MatchAiSortieState75 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.AirSortieRegistry.SortieState(true, Read2(data.ArmyId, c), Read76(data.Kind, c), Read8(data.TargetHex, c), Read8(data.LandingHex, c), Read18(data.Outbound, c), Read52(data.HeldTurn, c), Read77(data.StrikePolicy, c));
        }
        private static int Write76(global::Game.Ai.V2.AirSortieKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.AirSortieKind Read76(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.AirSortieKind>(data);
        }
        private static MatchAiAirStrikePolicy77 Write77(global::Game.Aviation.AirStrikePolicy value, Context c)
        {
            if (value.ExactTargetArmyId.HasValue) c.RecordArmy(value.ExactTargetArmyId.Value);
            return new MatchAiAirStrikePolicy77 { HasData = true, Kind = Write78(value.Kind, c), ExactTargetArmyId = Write52(value.ExactTargetArmyId, c), MinimumSurvivors = Write2(value.MinimumSurvivors, c) };
        }
        private static global::Game.Aviation.AirStrikePolicy Read77(MatchAiAirStrikePolicy77 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Aviation.AirStrikePolicy(true, Read78(data.Kind, c), Read52(data.ExactTargetArmyId, c), Read2(data.MinimumSurvivors, c));
        }
        private static int Write78(global::Game.Aviation.AirStrikePolicyKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Aviation.AirStrikePolicyKind Read78(int data, Context c)
        {
            return CheckedEnum<global::Game.Aviation.AirStrikePolicyKind>(data);
        }
        private static MatchAiMap79 Write79(global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.AiRadarStateRegistry.PersistentState> value, Context c)
        {
            return new MatchAiMap79 { HasData = value != null, Entries = value == null ? new List<MatchAiMap79.Entry>() : value.Select(x => new MatchAiMap79.Entry { Key = Write4(x.Key, c), Value = Write80(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.AiRadarStateRegistry.PersistentState> Read79(MatchAiMap79 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::Game.Ai.V2.AiRadarStateRegistry.PersistentState>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read80(entry.Value, c)); }
            return result;
        }
        private static MatchAiPersistentState80 Write80(global::Game.Ai.V2.AiRadarStateRegistry.PersistentState value, Context c)
        {
            if (value == null) return new MatchAiPersistentState80();
            return new MatchAiPersistentState80 { HasData = true, LastTurn = Write2(value.LastTurn, c), Smoothed = Write81(value.Smoothed, c) };
        }
        private static global::Game.Ai.V2.AiRadarStateRegistry.PersistentState Read80(MatchAiPersistentState80 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.AiRadarStateRegistry.PersistentState(true, Read2(data.LastTurn, c), Read81(data.Smoothed, c));
        }
        private static MatchAiMap81 Write81(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.DesireAxis, float> value, Context c)
        {
            return new MatchAiMap81 { HasData = value != null, Entries = value == null ? new List<MatchAiMap81.Entry>() : value.Select(x => new MatchAiMap81.Entry { Key = Write82(x.Key, c), Value = Write15(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.DesireAxis, float> Read81(MatchAiMap81 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Ai.V2.DesireAxis, float>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read82(entry.Key, c), Read15(entry.Value, c)); }
            return result;
        }
        private static int Write82(global::Game.Ai.V2.DesireAxis value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.DesireAxis Read82(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.DesireAxis>(data);
        }
        private static MatchAiMap83 Write83(global::System.Collections.Generic.IReadOnlyDictionary<string, float> value, Context c)
        {
            return new MatchAiMap83 { HasData = value != null, Entries = value == null ? new List<MatchAiMap83.Entry>() : value.Select(x => new MatchAiMap83.Entry { Key = Write4(x.Key, c), Value = Write15(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, float> Read83(MatchAiMap83 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, float>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read15(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap84 Write84(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)>> value, Context c)
        {
            return new MatchAiMap84 { HasData = value != null, Entries = value == null ? new List<MatchAiMap84.Entry>() : value.Select(x => new MatchAiMap84.Entry { Key = Write4(x.Key, c), Value = Write85(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)>> Read84(MatchAiMap84 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read85(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap85 Write85(global::System.Collections.Generic.IReadOnlyDictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)> value, Context c)
        {
            return new MatchAiMap85 { HasData = value != null, Entries = value == null ? new List<MatchAiMap85.Entry>() : value.Select(x => new MatchAiMap85.Entry { Key = Write2(x.Key, c), Value = Write86(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)> Read85(MatchAiMap85 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read86(entry.Value, c)); }
            return result;
        }
        private static MatchAiTuple86 Write86((int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys) value, Context c)
        {
            return new MatchAiTuple86 { HasData = true, Turn = Write2(value.Turn, c), CardKeys = Write23(value.CardKeys, c) };
        }
        private static (int Turn, global::System.Collections.Generic.IReadOnlyList<string> CardKeys) Read86(MatchAiTuple86 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read2(data.Turn, c), Read23(data.CardKeys, c));
        }
        private static MatchAiPersistentState87 Write87(global::Game.Ai.V2.AiReconMemory.PersistentState value, Context c)
        {
            if (value == null) return new MatchAiPersistentState87();
            return new MatchAiPersistentState87 { HasData = true, Observations = Write88(value.Observations, c), ObservedTurns = Write91(value.ObservedTurns, c) };
        }
        private static global::Game.Ai.V2.AiReconMemory.PersistentState Read87(MatchAiPersistentState87 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.AiReconMemory.PersistentState(true, Read88(data.Observations, c), Read91(data.ObservedTurns, c));
        }
        private static MatchAiMap88 Write88(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState>> value, Context c)
        {
            return new MatchAiMap88 { HasData = value != null, Entries = value == null ? new List<MatchAiMap88.Entry>() : value.Select(x => new MatchAiMap88.Entry { Key = Write4(x.Key, c), Value = Write89(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState>> Read88(MatchAiMap88 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read89(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap89 Write89(global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState> value, Context c)
        {
            return new MatchAiMap89 { HasData = value != null, Entries = value == null ? new List<MatchAiMap89.Entry>() : value.Select(x => new MatchAiMap89.Entry { Key = Write2(x.Key, c), Value = Write90(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState> Read89(MatchAiMap89 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, global::Game.Ai.V2.AiReconMemory.ObservationState>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read90(entry.Value, c)); }
            return result;
        }
        private static MatchAiObservationState90 Write90(global::Game.Ai.V2.AiReconMemory.ObservationState value, Context c)
        {
            c.RecordArmy(value.ArmyId);
            return new MatchAiObservationState90 { HasData = true, ArmyId = Write2(value.ArmyId, c), LastObservedHex = Write8(value.LastObservedHex, c), LastObservedTurn = Write2(value.LastObservedTurn, c), MemberCount = Write2(value.MemberCount, c), AttackSum = Write15(value.AttackSum, c), DefenseSum = Write15(value.DefenseSum, c), HasAntiAir = Write18(value.HasAntiAir, c), RecceRadius = Write2(value.RecceRadius, c), RecceSpotStrength = Write2(value.RecceSpotStrength, c), IsGarrison = Write18(value.IsGarrison, c), IsAir = Write18(value.IsAir, c), MaxMovement = Write2(value.MaxMovement, c), Commander = Write26(value.Commander, c), OwnerId = Write4(value.OwnerId, c), Defenders = Write16(value.Defenders, c) };
        }
        private static global::Game.Ai.V2.AiReconMemory.ObservationState Read90(MatchAiObservationState90 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.AiReconMemory.ObservationState(true, Read2(data.ArmyId, c), Read8(data.LastObservedHex, c), Read2(data.LastObservedTurn, c), Read2(data.MemberCount, c), Read15(data.AttackSum, c), Read15(data.DefenseSum, c), Read18(data.HasAntiAir, c), Read2(data.RecceRadius, c), Read2(data.RecceSpotStrength, c), Read18(data.IsGarrison, c), Read18(data.IsAir, c), Read2(data.MaxMovement, c), Read26(data.Commander, c), Read4(data.OwnerId, c), Read16(data.Defenders, c));
        }
        private static MatchAiMap91 Write91(global::System.Collections.Generic.IReadOnlyDictionary<string, int> value, Context c)
        {
            return new MatchAiMap91 { HasData = value != null, Entries = value == null ? new List<MatchAiMap91.Entry>() : value.Select(x => new MatchAiMap91.Entry { Key = Write4(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, int> Read91(MatchAiMap91 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static MatchAiPersistentState92 Write92(global::Game.Ai.V2.AiReconIntelMemory.PersistentState value, Context c)
        {
            if (value == null) return new MatchAiPersistentState92();
            return new MatchAiPersistentState92 { HasData = true, Observations = Write93(value.Observations, c), CurrentTurns = Write91(value.CurrentTurns, c) };
        }
        private static global::Game.Ai.V2.AiReconIntelMemory.PersistentState Read92(MatchAiPersistentState92 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.AiReconIntelMemory.PersistentState(true, Read93(data.Observations, c), Read91(data.CurrentTurns, c));
        }
        private static MatchAiMap93 Write93(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int>> value, Context c)
        {
            return new MatchAiMap93 { HasData = value != null, Entries = value == null ? new List<MatchAiMap93.Entry>() : value.Select(x => new MatchAiMap93.Entry { Key = Write4(x.Key, c), Value = Write39(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int>> Read93(MatchAiMap93 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read39(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap94 Write94(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState>> value, Context c)
        {
            return new MatchAiMap94 { HasData = value != null, Entries = value == null ? new List<MatchAiMap94.Entry>() : value.Select(x => new MatchAiMap94.Entry { Key = Write4(x.Key, c), Value = Write95(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState>> Read94(MatchAiMap94 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read95(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap95 Write95(global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState> value, Context c)
        {
            return new MatchAiMap95 { HasData = value != null, Entries = value == null ? new List<MatchAiMap95.Entry>() : value.Select(x => new MatchAiMap95.Entry { Key = Write2(x.Key, c), Value = Write96(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState> Read95(MatchAiMap95 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read96(entry.Value, c)); }
            return result;
        }
        private static MatchAiPatrolState96 Write96(global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState value, Context c)
        {
            c.RecordArmy(value.PreferredMoverArmyId);
            return new MatchAiPatrolState96 { HasData = true, Mode = Write97(value.Mode, c), PreferredMoverArmyId = Write2(value.PreferredMoverArmyId, c), StrategicAnchor = Write8(value.StrategicAnchor, c), StrategicSector = Write98(value.StrategicSector, c), StartedTurn = Write2(value.StartedTurn, c), LastProgressTurn = Write2(value.LastProgressTurn, c), LastModeSwitchTurn = Write2(value.LastModeSwitchTurn, c), LastStrategicReassignmentTurn = Write2(value.LastStrategicReassignmentTurn, c), Escape = Write99(value.Escape, c) };
        }
        private static global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState Read96(MatchAiPatrolState96 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.ReconPatrolStateRegistry.PatrolState(true, Read97(data.Mode, c), Read2(data.PreferredMoverArmyId, c), Read8(data.StrategicAnchor, c), Read98(data.StrategicSector, c), Read2(data.StartedTurn, c), Read2(data.LastProgressTurn, c), Read2(data.LastModeSwitchTurn, c), Read2(data.LastStrategicReassignmentTurn, c), Read99(data.Escape, c));
        }
        private static int Write97(global::Game.Ai.V2.ReconMode value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ReconMode Read97(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ReconMode>(data);
        }
        private static int Write98(global::Game.Ai.V2.ReconSector value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ReconSector Read98(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ReconSector>(data);
        }
        private static MatchAiOptional99 Write99((global::Game.HexGrid.HexCoord FromHex, global::Game.Ai.V2.ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn)? value, Context c)
        {
            return new MatchAiOptional99 { HasData = value.HasValue, Value = value.HasValue ? Write100(value.Value, c) : default };
        }
        private static (global::Game.HexGrid.HexCoord FromHex, global::Game.Ai.V2.ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn)? Read99(MatchAiOptional99 data, Context c)
        {
            Required(data);
            return data.HasData ? ((global::Game.HexGrid.HexCoord FromHex, global::Game.Ai.V2.ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn)?)Read100(data.Value, c) : null;
        }
        private static MatchAiTuple100 Write100((global::Game.HexGrid.HexCoord FromHex, global::Game.Ai.V2.ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn) value, Context c)
        {
            if (value.ThreatArmyId.HasValue) c.RecordArmy(value.ThreatArmyId.Value);
            return new MatchAiTuple100 { HasData = true, FromHex = Write8(value.FromHex, c), Cause = Write101(value.Cause, c), Risk = Write15(value.Risk, c), ThreatArmyId = Write52(value.ThreatArmyId, c), Turn = Write2(value.Turn, c) };
        }
        private static (global::Game.HexGrid.HexCoord FromHex, global::Game.Ai.V2.ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn) Read100(MatchAiTuple100 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read8(data.FromHex, c), Read101(data.Cause, c), Read15(data.Risk, c), Read52(data.ThreatArmyId, c), Read2(data.Turn, c));
        }
        private static int Write101(global::Game.Ai.V2.ReconEscapeCause value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ReconEscapeCause Read101(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ReconEscapeCause>(data);
        }
        private static MatchAiPersistentState102 Write102(global::Game.Ai.V2.ReconAirSortieRegistry.PersistentState value, Context c)
        {
            if (value == null) return new MatchAiPersistentState102();
            return new MatchAiPersistentState102 { HasData = true, NextSortieId = Write2(value.NextSortieId, c), Players = Write103(value.Players, c) };
        }
        private static global::Game.Ai.V2.ReconAirSortieRegistry.PersistentState Read102(MatchAiPersistentState102 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.ReconAirSortieRegistry.PersistentState(true, Read2(data.NextSortieId, c), Read103(data.Players, c));
        }
        private static MatchAiMap103 Write103(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState>> value, Context c)
        {
            return new MatchAiMap103 { HasData = value != null, Entries = value == null ? new List<MatchAiMap103.Entry>() : value.Select(x => new MatchAiMap103.Entry { Key = Write4(x.Key, c), Value = Write104(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState>> Read103(MatchAiMap103 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read104(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap104 Write104(global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState> value, Context c)
        {
            return new MatchAiMap104 { HasData = value != null, Entries = value == null ? new List<MatchAiMap104.Entry>() : value.Select(x => new MatchAiMap104.Entry { Key = Write2(x.Key, c), Value = Write105(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState> Read104(MatchAiMap104 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, global::Game.Ai.V2.ReconAirSortieRegistry.SortieState>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read105(entry.Value, c)); }
            return result;
        }
        private static MatchAiSortieState105 Write105(global::Game.Ai.V2.ReconAirSortieRegistry.SortieState value, Context c)
        {
            return new MatchAiSortieState105 { HasData = true, SortieId = Write2(value.SortieId, c), Phase = Write106(value.Phase, c), LaunchHex = Write8(value.LaunchHex, c), ChosenLandingHex = Write8(value.ChosenLandingHex, c), HasChosenLanding = Write18(value.HasChosenLanding, c), ClaimedSector = Write98(value.ClaimedSector, c), HasClaim = Write18(value.HasClaim, c), BestOutboundStepScore = Write15(value.BestOutboundStepScore, c), LastProcessedTurn = Write2(value.LastProcessedTurn, c), LastDecisionReason = Write4(value.LastDecisionReason, c), Trail = Write40(value.Trail, c) };
        }
        private static global::Game.Ai.V2.ReconAirSortieRegistry.SortieState Read105(MatchAiSortieState105 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.ReconAirSortieRegistry.SortieState(true, Read2(data.SortieId, c), Read106(data.Phase, c), Read8(data.LaunchHex, c), Read8(data.ChosenLandingHex, c), Read18(data.HasChosenLanding, c), Read98(data.ClaimedSector, c), Read18(data.HasClaim, c), Read15(data.BestOutboundStepScore, c), Read2(data.LastProcessedTurn, c), Read4(data.LastDecisionReason, c), Read40(data.Trail, c));
        }
        private static int Write106(global::Game.Ai.V2.ReconAirPhase value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ReconAirPhase Read106(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ReconAirPhase>(data);
        }
        private static MatchAiMap107 Write107(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>>> value, Context c)
        {
            return new MatchAiMap107 { HasData = value != null, Entries = value == null ? new List<MatchAiMap107.Entry>() : value.Select(x => new MatchAiMap107.Entry { Key = Write4(x.Key, c), Value = Write108(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>>> Read107(MatchAiMap107 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read108(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap108 Write108(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>> value, Context c)
        {
            return new MatchAiMap108 { HasData = value != null, Entries = value == null ? new List<MatchAiMap108.Entry>() : value.Select(x => new MatchAiMap108.Entry { Key = Write8(x.Key, c), Value = Write109(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>> Read108(MatchAiMap108 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.HexGrid.HexCoord, global::System.Collections.Generic.IReadOnlyDictionary<int, int>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read8(entry.Key, c), Read109(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap109 Write109(global::System.Collections.Generic.IReadOnlyDictionary<int, int> value, Context c)
        {
            return new MatchAiMap109 { HasData = value != null, Entries = value == null ? new List<MatchAiMap109.Entry>() : value.Select(x => new MatchAiMap109.Entry { Key = Write2(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, int> Read109(MatchAiMap109 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap110 Write110(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>> value, Context c)
        {
            return new MatchAiMap110 { HasData = value != null, Entries = value == null ? new List<MatchAiMap110.Entry>() : value.Select(x => new MatchAiMap110.Entry { Key = Write4(x.Key, c), Value = Write111(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>> Read110(MatchAiMap110 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read111(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap111 Write111(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)> value, Context c)
        {
            return new MatchAiMap111 { HasData = value != null, Entries = value == null ? new List<MatchAiMap111.Entry>() : value.Select(x => new MatchAiMap111.Entry { Key = Write112(x.Key, c), Value = Write113(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)> Read111(MatchAiMap111 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Ai.V2.ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read112(entry.Key, c), Read113(entry.Value, c)); }
            return result;
        }
        private static int Write112(global::Game.Ai.V2.ReconDeficitKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ReconDeficitKind Read112(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ReconDeficitKind>(data);
        }
        private static MatchAiTuple113 Write113((int FirstSeenTurn, int LastSeenTurn) value, Context c)
        {
            return new MatchAiTuple113 { HasData = true, FirstSeenTurn = Write2(value.FirstSeenTurn, c), LastSeenTurn = Write2(value.LastSeenTurn, c) };
        }
        private static (int FirstSeenTurn, int LastSeenTurn) Read113(MatchAiTuple113 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read2(data.FirstSeenTurn, c), Read2(data.LastSeenTurn, c));
        }
        private static MatchAiMap114 Write114(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)>> value, Context c)
        {
            return new MatchAiMap114 { HasData = value != null, Entries = value == null ? new List<MatchAiMap114.Entry>() : value.Select(x => new MatchAiMap114.Entry { Key = Write4(x.Key, c), Value = Write115(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)>> Read114(MatchAiMap114 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read115(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap115 Write115(global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)> value, Context c)
        {
            return new MatchAiMap115 { HasData = value != null, Entries = value == null ? new List<MatchAiMap115.Entry>() : value.Select(x => new MatchAiMap115.Entry { Key = Write2(x.Key, c), Value = Write116(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)> Read115(MatchAiMap115 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<int, (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read2(entry.Key, c), Read116(entry.Value, c)); }
            return result;
        }
        private static MatchAiTuple116 Write116((global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent) value, Context c)
        {
            return new MatchAiTuple116 { HasData = true, JustLeft = Write117(value.JustLeft, c), Recent = Write40(value.Recent, c) };
        }
        private static (global::Game.HexGrid.HexCoord? JustLeft, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> Recent) Read116(MatchAiTuple116 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read117(data.JustLeft, c), Read40(data.Recent, c));
        }
        private static MatchAiOptional117 Write117(global::Game.HexGrid.HexCoord? value, Context c)
        {
            return new MatchAiOptional117 { HasData = value.HasValue, Value = value.HasValue ? Write8(value.Value, c) : default };
        }
        private static global::Game.HexGrid.HexCoord? Read117(MatchAiOptional117 data, Context c)
        {
            Required(data);
            return data.HasData ? (global::Game.HexGrid.HexCoord?)Read8(data.Value, c) : null;
        }
        private static MatchAiMap118 Write118(global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState> value, Context c)
        {
            return new MatchAiMap118 { HasData = value != null, Entries = value == null ? new List<MatchAiMap118.Entry>() : value.Select(x => new MatchAiMap118.Entry { Key = Write4(x.Key, c), Value = Write119(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState> Read118(MatchAiMap118 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read119(entry.Value, c)); }
            return result;
        }
        private static MatchAiPersistentState119 Write119(global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState value, Context c)
        {
            if (value == null) return new MatchAiPersistentState119();
            return new MatchAiPersistentState119 { HasData = true, Forecasts = Write120(value.Forecasts, c), Pressure = Write124(value.Pressure, c), CurrentBlocks = Write125(value.CurrentBlocks, c), VerifiedPending = Write127(value.VerifiedPending, c), LastPressureHitTurn = Write128(value.LastPressureHitTurn, c), RequireVerifiedEvidence = Write18(value.RequireVerifiedEvidence, c), LastDecayTurn = Write2(value.LastDecayTurn, c) };
        }
        private static global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState Read119(MatchAiPersistentState119 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.ResourceStarvationRegistry.PersistentState(true, Read120(data.Forecasts, c), Read124(data.Pressure, c), Read125(data.CurrentBlocks, c), Read127(data.VerifiedPending, c), Read128(data.LastPressureHitTurn, c), Read18(data.RequireVerifiedEvidence, c), Read2(data.LastDecayTurn, c));
        }
        private static MatchAiMap120 Write120(global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState> value, Context c)
        {
            return new MatchAiMap120 { HasData = value != null, Entries = value == null ? new List<MatchAiMap120.Entry>() : value.Select(x => new MatchAiMap120.Entry { Key = Write4(x.Key, c), Value = Write121(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState> Read120(MatchAiMap120 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read121(entry.Value, c)); }
            return result;
        }
        private static MatchAiForecastState121 Write121(global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState value, Context c)
        {
            if (value == null) return new MatchAiForecastState121();
            return new MatchAiForecastState121 { HasData = true, Turn = Write2(value.Turn, c), Needs = Write122(value.Needs, c) };
        }
        private static global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState Read121(MatchAiForecastState121 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.ResourceStarvationRegistry.ForecastState(true, Read2(data.Turn, c), Read122(data.Needs, c));
        }
        private static MatchAiMap122 Write122(global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceBundle> value, Context c)
        {
            return new MatchAiMap122 { HasData = value != null, Entries = value == null ? new List<MatchAiMap122.Entry>() : value.Select(x => new MatchAiMap122.Entry { Key = Write4(x.Key, c), Value = Write123(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.V2.ResourceBundle> Read122(MatchAiMap122 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::Game.Ai.V2.ResourceBundle>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read123(entry.Value, c)); }
            return result;
        }
        private static MatchAiResourceBundle123 Write123(global::Game.Ai.V2.ResourceBundle value, Context c)
        {
            return new MatchAiResourceBundle123 { HasData = true, Human = Write15(value.Human, c), Energy = Write15(value.Energy, c), Materials = Write15(value.Materials, c), Tech = Write15(value.Tech, c) };
        }
        private static global::Game.Ai.V2.ResourceBundle Read123(MatchAiResourceBundle123 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.ResourceBundle { Human = Read15(data.Human, c), Energy = Read15(data.Energy, c), Materials = Read15(data.Materials, c), Tech = Read15(data.Tech, c) };
        }
        private static MatchAiMap124 Write124(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, float> value, Context c)
        {
            return new MatchAiMap124 { HasData = value != null, Entries = value == null ? new List<MatchAiMap124.Entry>() : value.Select(x => new MatchAiMap124.Entry { Key = Write9(x.Key, c), Value = Write15(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, float> Read124(MatchAiMap124 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Economy.ResourceType, float>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read9(entry.Key, c), Read15(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap125 Write125(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, global::Game.Ai.V2.ResourceBlockEvidence> value, Context c)
        {
            return new MatchAiMap125 { HasData = value != null, Entries = value == null ? new List<MatchAiMap125.Entry>() : value.Select(x => new MatchAiMap125.Entry { Key = Write9(x.Key, c), Value = Write126(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, global::Game.Ai.V2.ResourceBlockEvidence> Read125(MatchAiMap125 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Economy.ResourceType, global::Game.Ai.V2.ResourceBlockEvidence>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read9(entry.Key, c), Read126(entry.Value, c)); }
            return result;
        }
        private static MatchAiResourceBlockEvidence126 Write126(global::Game.Ai.V2.ResourceBlockEvidence value, Context c)
        {
            return new MatchAiResourceBlockEvidence126 { HasData = true, Required = Write15(value.Required, c), AvailableAtBlock = Write15(value.AvailableAtBlock, c), IncomePerTurn = Write15(value.IncomePerTurn, c), DemandValue = Write15(value.DemandValue, c), Turn = Write2(value.Turn, c) };
        }
        private static global::Game.Ai.V2.ResourceBlockEvidence Read126(MatchAiResourceBlockEvidence126 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.ResourceBlockEvidence(true, Read15(data.Required, c), Read15(data.AvailableAtBlock, c), Read15(data.IncomePerTurn, c), Read15(data.DemandValue, c), Read2(data.Turn, c));
        }
        private static MatchAiList127 Write127(global::System.Collections.Generic.IReadOnlyList<global::Game.Economy.ResourceType> value, Context c)
        {
            return new MatchAiList127 { HasData = value != null, Values = value == null ? new List<int>() : value.Select(x => Write9(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Economy.ResourceType> Read127(MatchAiList127 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read9(x, c)).ToList();
        }
        private static MatchAiMap128 Write128(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> value, Context c)
        {
            return new MatchAiMap128 { HasData = value != null, Entries = value == null ? new List<MatchAiMap128.Entry>() : value.Select(x => new MatchAiMap128.Entry { Key = Write9(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Read128(MatchAiMap128 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Economy.ResourceType, int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read9(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap129 Write129(global::System.Collections.Generic.IReadOnlyDictionary<string, (int LastTurn, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Streak, global::Game.Ai.V2.ResourceBundle Surplus)> value, Context c)
        {
            return new MatchAiMap129 { HasData = value != null, Entries = value == null ? new List<MatchAiMap129.Entry>() : value.Select(x => new MatchAiMap129.Entry { Key = Write4(x.Key, c), Value = Write130(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, (int LastTurn, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Streak, global::Game.Ai.V2.ResourceBundle Surplus)> Read129(MatchAiMap129 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, (int LastTurn, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Streak, global::Game.Ai.V2.ResourceBundle Surplus)>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read130(entry.Value, c)); }
            return result;
        }
        private static MatchAiTuple130 Write130((int LastTurn, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Streak, global::Game.Ai.V2.ResourceBundle Surplus) value, Context c)
        {
            return new MatchAiTuple130 { HasData = true, LastTurn = Write2(value.LastTurn, c), Streak = Write128(value.Streak, c), Surplus = Write123(value.Surplus, c) };
        }
        private static (int LastTurn, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Economy.ResourceType, int> Streak, global::Game.Ai.V2.ResourceBundle Surplus) Read130(MatchAiTuple130 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read2(data.LastTurn, c), Read128(data.Streak, c), Read123(data.Surplus, c));
        }
        private static MatchAiMap131 Write131(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo>> value, Context c)
        {
            return new MatchAiMap131 { HasData = value != null, Entries = value == null ? new List<MatchAiMap131.Entry>() : value.Select(x => new MatchAiMap131.Entry { Key = Write4(x.Key, c), Value = Write132(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo>> Read131(MatchAiMap131 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read132(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap132 Write132(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo> value, Context c)
        {
            return new MatchAiMap132 { HasData = value != null, Entries = value == null ? new List<MatchAiMap132.Entry>() : value.Select(x => new MatchAiMap132.Entry { Key = Write48(x.Key, c), Value = Write133(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo> Read132(MatchAiMap132 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Ai.V2.StableMissionKey, global::Game.Ai.V2.MissionCooldownInfo>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read48(entry.Key, c), Read133(entry.Value, c)); }
            return result;
        }
        private static MatchAiMissionCooldownInfo133 Write133(global::Game.Ai.V2.MissionCooldownInfo value, Context c)
        {
            return new MatchAiMissionCooldownInfo133 { HasData = true, StartedTurn = Write2(value.StartedTurn, c), UntilTurn = Write2(value.UntilTurn, c), Reason = Write4(value.Reason, c) };
        }
        private static global::Game.Ai.V2.MissionCooldownInfo Read133(MatchAiMissionCooldownInfo133 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.MissionCooldownInfo(true, Read2(data.StartedTurn, c), Read2(data.UntilTurn, c), Read4(data.Reason, c));
        }
        private static MatchAiMap134 Write134(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.Initiative.InitiativeTurnRecord>> value, Context c)
        {
            return new MatchAiMap134 { HasData = value != null, Entries = value == null ? new List<MatchAiMap134.Entry>() : value.Select(x => new MatchAiMap134.Entry { Key = Write4(x.Key, c), Value = Write135(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.Initiative.InitiativeTurnRecord>> Read134(MatchAiMap134 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.Initiative.InitiativeTurnRecord>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read135(entry.Value, c)); }
            return result;
        }
        private static MatchAiList135 Write135(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.Initiative.InitiativeTurnRecord> value, Context c)
        {
            return new MatchAiList135 { HasData = value != null, Values = value == null ? new List<MatchAiInitiativeTurnRecord136>() : value.Select(x => Write136(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.Initiative.InitiativeTurnRecord> Read135(MatchAiList135 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read136(x, c)).ToList();
        }
        private static MatchAiInitiativeTurnRecord136 Write136(global::Game.Ai.V2.Initiative.InitiativeTurnRecord value, Context c)
        {
            return new MatchAiInitiativeTurnRecord136 { HasData = true, InitiativeBaseAp = Write2(value.InitiativeBaseAp, c), TotalStartAp = Write2(value.TotalStartAp, c), ApSpent = Write2(value.ApSpent, c), EndAp = Write2(value.EndAp, c), ActionableArmyCountAtStart = Write2(value.ActionableArmyCountAtStart, c), UnactivatedActionableArmyCountAtEnd = Write2(value.UnactivatedActionableArmyCountAtEnd, c), HadPotentialApWorkAtEnd = Write18(value.HadPotentialApWorkAtEnd, c) };
        }
        private static global::Game.Ai.V2.Initiative.InitiativeTurnRecord Read136(MatchAiInitiativeTurnRecord136 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.Initiative.InitiativeTurnRecord(true, Read2(data.InitiativeBaseAp, c), Read2(data.TotalStartAp, c), Read2(data.ApSpent, c), Read2(data.EndAp, c), Read2(data.ActionableArmyCountAtStart, c), Read2(data.UnactivatedActionableArmyCountAtEnd, c), Read18(data.HadPotentialApWorkAtEnd, c));
        }
        private static MatchAiMap137 Write137(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<int>> value, Context c)
        {
            return new MatchAiMap137 { HasData = value != null, Entries = value == null ? new List<MatchAiMap137.Entry>() : value.Select(x => new MatchAiMap137.Entry { Key = Write4(x.Key, c), Value = Write31(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<int>> Read137(MatchAiMap137 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyList<int>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read31(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap138 Write138(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, string CardKey)>> value, Context c)
        {
            return new MatchAiMap138 { HasData = value != null, Entries = value == null ? new List<MatchAiMap138.Entry>() : value.Select(x => new MatchAiMap138.Entry { Key = Write4(x.Key, c), Value = Write139(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, string CardKey)>> Read138(MatchAiMap138 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, string CardKey)>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read139(entry.Value, c)); }
            return result;
        }
        private static MatchAiList139 Write139(global::System.Collections.Generic.IReadOnlyList<(int Turn, string CardKey)> value, Context c)
        {
            return new MatchAiList139 { HasData = value != null, Values = value == null ? new List<MatchAiTuple140>() : value.Select(x => Write140(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<(int Turn, string CardKey)> Read139(MatchAiList139 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read140(x, c)).ToList();
        }
        private static MatchAiTuple140 Write140((int Turn, string CardKey) value, Context c)
        {
            return new MatchAiTuple140 { HasData = true, Turn = Write2(value.Turn, c), CardKey = Write4(value.CardKey, c) };
        }
        private static (int Turn, string CardKey) Read140(MatchAiTuple140 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read2(data.Turn, c), Read4(data.CardKey, c));
        }
        private static MatchAiMap141 Write141(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, float Demand)>> value, Context c)
        {
            return new MatchAiMap141 { HasData = value != null, Entries = value == null ? new List<MatchAiMap141.Entry>() : value.Select(x => new MatchAiMap141.Entry { Key = Write4(x.Key, c), Value = Write142(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, float Demand)>> Read141(MatchAiMap141 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyList<(int Turn, float Demand)>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read142(entry.Value, c)); }
            return result;
        }
        private static MatchAiList142 Write142(global::System.Collections.Generic.IReadOnlyList<(int Turn, float Demand)> value, Context c)
        {
            return new MatchAiList142 { HasData = value != null, Values = value == null ? new List<MatchAiTuple143>() : value.Select(x => Write143(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<(int Turn, float Demand)> Read142(MatchAiList142 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read143(x, c)).ToList();
        }
        private static MatchAiTuple143 Write143((int Turn, float Demand) value, Context c)
        {
            return new MatchAiTuple143 { HasData = true, Turn = Write2(value.Turn, c), Demand = Write15(value.Demand, c) };
        }
        private static (int Turn, float Demand) Read143(MatchAiTuple143 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing tuple");
            return (Read2(data.Turn, c), Read15(data.Demand, c));
        }
        private static MatchAiMap144 Write144(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.MissionIntentKey, int>> value, Context c)
        {
            return new MatchAiMap144 { HasData = value != null, Entries = value == null ? new List<MatchAiMap144.Entry>() : value.Select(x => new MatchAiMap144.Entry { Key = Write4(x.Key, c), Value = Write145(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.MissionIntentKey, int>> Read144(MatchAiMap144 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<string, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.MissionIntentKey, int>>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read4(entry.Key, c), Read145(entry.Value, c)); }
            return result;
        }
        private static MatchAiMap145 Write145(global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.MissionIntentKey, int> value, Context c)
        {
            return new MatchAiMap145 { HasData = value != null, Entries = value == null ? new List<MatchAiMap145.Entry>() : value.Select(x => new MatchAiMap145.Entry { Key = Write45(x.Key, c), Value = Write2(x.Value, c) }).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyDictionary<global::Game.Ai.V2.MissionIntentKey, int> Read145(MatchAiMap145 data, Context c)
        {
            Required(data);
            Required(data.Entries); if (!data.HasData) { if (data.Entries.Count != 0) throw Invalid("Absent dictionary has entries"); return null; }
            var result = new Dictionary<global::Game.Ai.V2.MissionIntentKey, int>();
            foreach (var entry in data.Entries) { Required(entry); result.Add(Read45(entry.Key, c), Read2(entry.Value, c)); }
            return result;
        }
        private static MatchAiScoutIntent146 Write146(global::Game.Ai.V2.ScoutIntent value, Context c)
        {
            if (value == null) return new MatchAiScoutIntent146();
            return new MatchAiScoutIntent146 { HasData = true, Kind = Write147(value.Kind, c), RequiresStealth = Write18(value.RequiresStealth, c), FocusHex = Write8(value.FocusHex, c) };
        }
        private static global::Game.Ai.V2.ScoutIntent Read146(MatchAiScoutIntent146 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.ScoutIntent { Kind = Read147(data.Kind, c), RequiresStealth = Read18(data.RequiresStealth, c), FocusHex = Read8(data.FocusHex, c) };
        }
        private static int Write147(global::Game.Ai.V2.ScoutTargetKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ScoutTargetKind Read147(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ScoutTargetKind>(data);
        }
        private static MatchAiRaidIntent148 Write148(global::Game.Ai.V2.RaidIntent value, Context c)
        {
            if (value == null) return new MatchAiRaidIntent148();
            if (value.AirSupportArmyId.HasValue) c.RecordArmy(value.AirSupportArmyId.Value);
            if (value.PrimaryArmyId.HasValue) c.RecordArmy(value.PrimaryArmyId.Value);
            if (value.SupportArmyId.HasValue) c.RecordArmy(value.SupportArmyId.Value);
            return new MatchAiRaidIntent148 { HasData = true, Target = Write149(value.Target, c), LastKnownHex = Write8(value.LastKnownHex, c), TargetIsNeutral = Write18(value.TargetIsNeutral, c), OperationStarted = Write18(value.OperationStarted, c), CompletedTargetAwaitingFreshDecision = Write18(value.CompletedTargetAwaitingFreshDecision, c), AirSupportArmyId = Write52(value.AirSupportArmyId, c), AirSupportLandingHex = Write117(value.AirSupportLandingHex, c), AirSupportAttemptedTurn = Write2(value.AirSupportAttemptedTurn, c), AirSupportStrikeSucceeded = Write18(value.AirSupportStrikeSucceeded, c), ReturnHex = Write117(value.ReturnHex, c), SupportReturnHex = Write117(value.SupportReturnHex, c), RecoveryBaseHex = Write117(value.RecoveryBaseHex, c), ReinforcementRequestedTurn = Write2(value.ReinforcementRequestedTurn, c), Phase = Write150(value.Phase, c), PrimaryArmyId = Write52(value.PrimaryArmyId, c), SupportArmyId = Write52(value.SupportArmyId, c) };
        }
        private static global::Game.Ai.V2.RaidIntent Read148(MatchAiRaidIntent148 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.RaidIntent { Target = Read149(data.Target, c), LastKnownHex = Read8(data.LastKnownHex, c), TargetIsNeutral = Read18(data.TargetIsNeutral, c), OperationStarted = Read18(data.OperationStarted, c), CompletedTargetAwaitingFreshDecision = Read18(data.CompletedTargetAwaitingFreshDecision, c), AirSupportArmyId = Read52(data.AirSupportArmyId, c), AirSupportLandingHex = Read117(data.AirSupportLandingHex, c), AirSupportAttemptedTurn = Read2(data.AirSupportAttemptedTurn, c), AirSupportStrikeSucceeded = Read18(data.AirSupportStrikeSucceeded, c), ReturnHex = Read117(data.ReturnHex, c), SupportReturnHex = Read117(data.SupportReturnHex, c), RecoveryBaseHex = Read117(data.RecoveryBaseHex, c), Phase = Read150(data.Phase, c), PrimaryArmyId = Read52(data.PrimaryArmyId, c), SupportArmyId = Read52(data.SupportArmyId, c), ReinforcementRequestedTurn = Read2(data.ReinforcementRequestedTurn, c) };
        }
        private static MatchAiRaidTargetRef149 Write149(global::Game.Ai.V2.RaidTargetRef value, Context c)
        {
            c.RecordArmy(value.ArmyId);
            return new MatchAiRaidTargetRef149 { HasData = true, HasValue = Write18(value.HasValue, c), Kind = Write47(value.Kind, c), ArmyId = Write2(value.ArmyId, c), Hex = Write8(value.Hex, c) };
        }
        private static global::Game.Ai.V2.RaidTargetRef Read149(MatchAiRaidTargetRef149 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            var kind = Read47(data.Kind, c);
            return !data.HasValue ? RaidTargetRef.None : kind == RaidTargetKind.NeutralArmy ? RaidTargetRef.ForNeutralArmy(data.ArmyId) : RaidTargetRef.ForEventGuard(Read8(data.Hex, c));
        }
        private static int Write150(global::Game.Ai.V2.RaidMissionPhase value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.RaidMissionPhase Read150(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.RaidMissionPhase>(data);
        }
        private static MatchAiEconomyIntent151 Write151(global::Game.Ai.V2.EconomyIntent value, Context c)
        {
            if (value == null) return new MatchAiEconomyIntent151();
            if (value.BuilderArmyId.HasValue) c.RecordArmy(value.BuilderArmyId.Value);
            if (value.CollectorArmyId.HasValue) c.RecordArmy(value.CollectorArmyId.Value);
            if (value.CollectorSourceArmyId.HasValue) c.RecordArmy(value.CollectorSourceArmyId.Value);
            return new MatchAiEconomyIntent151 { HasData = true, Kind = Write152(value.Kind, c), TargetHex = Write8(value.TargetHex, c), ResourceType = Write66(value.ResourceType, c), BuilderArmyId = Write52(value.BuilderArmyId, c), CollectorArmyId = Write52(value.CollectorArmyId, c), CollectorSourceArmyId = Write52(value.CollectorSourceArmyId, c), ExpectedMarginalYield = Write2(value.ExpectedMarginalYield, c), SafeReturnHex = Write117(value.SafeReturnHex, c), ArrivalTurn = Write2(value.ArrivalTurn, c), LastIncomeOpportunityTurn = Write2(value.LastIncomeOpportunityTurn, c), BuildCard = Write153(value.BuildCard, c), BuildResourceCost = Write154(value.BuildResourceCost, c), BuildApCost = Write15(value.BuildApCost, c), IntrinsicValue = Write155(value.IntrinsicValue, c), BuildValue = Write15(value.BuildValue, c), MinimumFollowupAp = Write15(value.MinimumFollowupAp, c), Loaned = Write18(value.Loaned, c), LoanSource = Write45(value.LoanSource, c) };
        }
        private static global::Game.Ai.V2.EconomyIntent Read151(MatchAiEconomyIntent151 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.EconomyIntent { Kind = Read152(data.Kind, c), TargetHex = Read8(data.TargetHex, c), ResourceType = Read66(data.ResourceType, c), BuilderArmyId = Read52(data.BuilderArmyId, c), CollectorArmyId = Read52(data.CollectorArmyId, c), CollectorSourceArmyId = Read52(data.CollectorSourceArmyId, c), ExpectedMarginalYield = Read2(data.ExpectedMarginalYield, c), SafeReturnHex = Read117(data.SafeReturnHex, c), ArrivalTurn = Read2(data.ArrivalTurn, c), LastIncomeOpportunityTurn = Read2(data.LastIncomeOpportunityTurn, c), BuildCard = Read153(data.BuildCard, c), BuildResourceCost = Read154(data.BuildResourceCost, c), BuildApCost = Read15(data.BuildApCost, c), IntrinsicValue = Read155(data.IntrinsicValue, c), BuildValue = Read15(data.BuildValue, c), MinimumFollowupAp = Read15(data.MinimumFollowupAp, c), Loaned = Read18(data.Loaned, c), LoanSource = Read45(data.LoanSource, c) };
        }
        private static int Write152(global::Game.Ai.V2.EconomyTaskKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.EconomyTaskKind Read152(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.EconomyTaskKind>(data);
        }
        private static MatchAiCardRef Write153(global::Game.Cards.CardData value, Context c)
        {
            return c.WriteCard(value);
        }
        private static global::Game.Cards.CardData Read153(MatchAiCardRef data, Context c)
        {
            return c.ReadCard(data);
        }
        private static MatchAiResourceCost154 Write154(global::Game.Cards.ResourceCost value, Context c)
        {
            if (value == null) return new MatchAiResourceCost154();
            return new MatchAiResourceCost154 { HasData = true, human = Write2(value.human, c), energy = Write2(value.energy, c), materials = Write2(value.materials, c), tech = Write2(value.tech, c) };
        }
        private static global::Game.Cards.ResourceCost Read154(MatchAiResourceCost154 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Cards.ResourceCost { human = Read2(data.human, c), energy = Read2(data.energy, c), materials = Read2(data.materials, c), tech = Read2(data.tech, c) };
        }
        private static MatchAiOptional155 Write155(float? value, Context c)
        {
            return new MatchAiOptional155 { HasData = value.HasValue, Value = value.HasValue ? Write15(value.Value, c) : default };
        }
        private static float? Read155(MatchAiOptional155 data, Context c)
        {
            Required(data);
            return data.HasData ? (float?)Read15(data.Value, c) : null;
        }
        private static MatchAiDevelopmentIntent156 Write156(global::Game.Ai.V2.DevelopmentIntent value, Context c)
        {
            if (value == null) return new MatchAiDevelopmentIntent156();
            return new MatchAiDevelopmentIntent156 { HasData = true, FacilityHex = Write8(value.FacilityHex, c), Mode = Write72(value.Mode, c), Hero = Write157(value.Hero, c), HeroKey = Write4(value.HeroKey, c), IntrinsicValue = Write15(value.IntrinsicValue, c) };
        }
        private static global::Game.Ai.V2.DevelopmentIntent Read156(MatchAiDevelopmentIntent156 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.DevelopmentIntent { FacilityHex = Read8(data.FacilityHex, c), Mode = Read72(data.Mode, c), Hero = Read157(data.Hero, c), HeroKey = Read4(data.HeroKey, c), IntrinsicValue = Read15(data.IntrinsicValue, c) };
        }
        private static MatchAiUnitRef Write157(global::Game.Units.UnitData value, Context c)
        {
            return c.WriteUnit(value);
        }
        private static global::Game.Units.UnitData Read157(MatchAiUnitRef data, Context c)
        {
            return c.ReadUnit(data);
        }
        private static MatchAiActiveDefenceIntent158 Write158(global::Game.Ai.V2.ActiveDefenceIntent value, Context c)
        {
            if (value == null) return new MatchAiActiveDefenceIntent158();
            c.RecordArmy(value.EnemyArmyId);
            if (value.AirSupportArmyId.HasValue) c.RecordArmy(value.AirSupportArmyId.Value);
            if (value.PrimaryArmyId.HasValue) c.RecordArmy(value.PrimaryArmyId.Value);
            return new MatchAiActiveDefenceIntent158 { HasData = true, Phase = Write159(value.Phase, c), ReturnPurpose = Write160(value.ReturnPurpose, c), InterceptPurpose = Write161(value.InterceptPurpose, c), ImmediateTurn = Write2(value.ImmediateTurn, c), Sortie = Write162(value.Sortie, c), EnemyEta = Write52(value.EnemyEta, c), EnemyArmyId = Write2(value.EnemyArmyId, c), LastKnownHex = Write8(value.LastKnownHex, c), LastObservedTurn = Write2(value.LastObservedTurn, c), Confidence = Write15(value.Confidence, c), ProtectedAssetHex = Write8(value.ProtectedAssetHex, c), ProtectedAssetKind = Write187(value.ProtectedAssetKind, c), ProtectedAssetValue = Write15(value.ProtectedAssetValue, c), ThreatSeverity = Write15(value.ThreatSeverity, c), ReturnHex = Write117(value.ReturnHex, c), ProjectedWinChance = Write15(value.ProjectedWinChance, c), CoversAllDefenders = Write18(value.CoversAllDefenders, c), EstimatedEta = Write2(value.EstimatedEta, c), AirSupportArmyId = Write52(value.AirSupportArmyId, c), AirSupportLandingHex = Write117(value.AirSupportLandingHex, c), AirSupportBoundTurn = Write2(value.AirSupportBoundTurn, c), AirSupportSortieSeen = Write18(value.AirSupportSortieSeen, c), PrimaryArmyId = Write52(value.PrimaryArmyId, c) };
        }
        private static global::Game.Ai.V2.ActiveDefenceIntent Read158(MatchAiActiveDefenceIntent158 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.ActiveDefenceIntent { Phase = Read159(data.Phase, c), ReturnPurpose = Read160(data.ReturnPurpose, c), InterceptPurpose = Read161(data.InterceptPurpose, c), ImmediateTurn = Read2(data.ImmediateTurn, c), Sortie = Read162(data.Sortie, c), EnemyEta = Read52(data.EnemyEta, c), EnemyArmyId = Read2(data.EnemyArmyId, c), LastKnownHex = Read8(data.LastKnownHex, c), LastObservedTurn = Read2(data.LastObservedTurn, c), Confidence = Read15(data.Confidence, c), ProtectedAssetHex = Read8(data.ProtectedAssetHex, c), ProtectedAssetKind = Read187(data.ProtectedAssetKind, c), ProtectedAssetValue = Read15(data.ProtectedAssetValue, c), ThreatSeverity = Read15(data.ThreatSeverity, c), ReturnHex = Read117(data.ReturnHex, c), ProjectedWinChance = Read15(data.ProjectedWinChance, c), CoversAllDefenders = Read18(data.CoversAllDefenders, c), EstimatedEta = Read2(data.EstimatedEta, c), AirSupportArmyId = Read52(data.AirSupportArmyId, c), AirSupportLandingHex = Read117(data.AirSupportLandingHex, c), AirSupportBoundTurn = Read2(data.AirSupportBoundTurn, c), AirSupportSortieSeen = Read18(data.AirSupportSortieSeen, c), PrimaryArmyId = Read52(data.PrimaryArmyId, c) };
        }
        private static int Write159(global::Game.Ai.V2.ActiveDefencePhase value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ActiveDefencePhase Read159(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ActiveDefencePhase>(data);
        }
        private static int Write160(global::Game.Ai.V2.ActiveDefenceReturnPurpose value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ActiveDefenceReturnPurpose Read160(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ActiveDefenceReturnPurpose>(data);
        }
        private static int Write161(global::Game.Ai.V2.ActiveDefenceInterceptPurpose value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ActiveDefenceInterceptPurpose Read161(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ActiveDefenceInterceptPurpose>(data);
        }
        private static MatchAiGarrisonSortiePlan162 Write162(global::Game.Ai.V2.GarrisonSortiePlan value, Context c)
        {
            if (value == null) return new MatchAiGarrisonSortiePlan162();
            c.RecordUnit(value.CoverRuntimeId);
            c.RecordArmy(value.EnemyArmyId);
            if (value.ShellArmyId.HasValue) c.RecordArmy(value.ShellArmyId.Value);
            if (value.UnitRuntimeIds != null) foreach (int id in value.UnitRuntimeIds) c.RecordUnit(id);
            return new MatchAiGarrisonSortiePlan162 { HasData = true, DonorGarrisonId = Write2(value.DonorGarrisonId, c), CoverRuntimeId = Write2(value.CoverRuntimeId, c), EnemyArmyId = Write2(value.EnemyArmyId, c), RouteCost = Write2(value.RouteCost, c), ShellArmyId = Write52(value.ShellArmyId, c), StrongholdHex = Write8(value.StrongholdHex, c), EnemyHex = Write8(value.EnemyHex, c), UnitRuntimeIds = Write31(value.UnitRuntimeIds, c), Detachment = Write163(value.Detachment, c), WinChance = Write15(value.WinChance, c), CoversAllDefenders = Write18(value.CoversAllDefenders, c) };
        }
        private static global::Game.Ai.V2.GarrisonSortiePlan Read162(MatchAiGarrisonSortiePlan162 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.GarrisonSortiePlan(true, Read2(data.DonorGarrisonId, c), Read2(data.CoverRuntimeId, c), Read2(data.EnemyArmyId, c), Read2(data.RouteCost, c), Read52(data.ShellArmyId, c), Read8(data.StrongholdHex, c), Read8(data.EnemyHex, c), Read31(data.UnitRuntimeIds, c), Read163(data.Detachment, c), Read15(data.WinChance, c), Read18(data.CoversAllDefenders, c));
        }
        private static MatchAiArmySnapshot163 Write163(global::Game.Ai.V2.ArmySnapshot value, Context c)
        {
            if (value == null) return new MatchAiArmySnapshot163();
            c.RecordArmy(value.ArmyId);
            if (value.NonHeroRuntimeIds != null) foreach (int id in value.NonHeroRuntimeIds) c.RecordUnit(id);
            if (value.ActivationCoveredUnitRuntimeIds != null) foreach (int id in value.ActivationCoveredUnitRuntimeIds) c.RecordUnit(id);
            return new MatchAiArmySnapshot163 { HasData = true, EconomyDeparture = Write163(value.EconomyDeparture, c), ArmyId = Write2(value.ArmyId, c), Owner = Write14(value.Owner, c), Hex = Write8(value.Hex, c), IsGarrison = Write18(value.IsGarrison, c), RequiresGarrisonExtraction = Write18(value.RequiresGarrisonExtraction, c), IsPrison = Write18(value.IsPrison, c), IsAir = Write18(value.IsAir, c), IsAirfield = Write18(value.IsAirfield, c), MobileAirfieldCapacity = Write2(value.MobileAirfieldCapacity, c), RepairSourceCount = Write2(value.RepairSourceCount, c), IsCarrierContainer = Write18(value.IsCarrierContainer, c), CarrierCargoPower = Write15(value.CarrierCargoPower, c), CanServeReconAir = Write18(value.CanServeReconAir, c), StoredAircraft = Write164(value.StoredAircraft, c), MemberCount = Write2(value.MemberCount, c), HasHero = Write18(value.HasHero, c), HeroCount = Write2(value.HeroCount, c), BestHeroCommandRating = Write2(value.BestHeroCommandRating, c), Commander = Write26(value.Commander, c), HasAntiAir = Write18(value.HasAntiAir, c), StrategicCoverage = Write166(value.StrategicCoverage, c), AllyAuraEffects = Write167(value.AllyAuraEffects, c), MembersWithHeroes = Write16(value.MembersWithHeroes, c), NonHeroMutatorOccupied = Write177(value.NonHeroMutatorOccupied, c), NonHeroActivationApCosts = Write31(value.NonHeroActivationApCosts, c), NonHeroMoveMax = Write31(value.NonHeroMoveMax, c), NonHeroIsAviation = Write177(value.NonHeroIsAviation, c), NonHeroRuntimeIds = Write31(value.NonHeroRuntimeIds, c), NonHeroSpareable = Write177(value.NonHeroSpareable, c), GarrisonThreatFloor = Write15(value.GarrisonThreatFloor, c), NonHeroCurrentMovement = Write31(value.NonHeroCurrentMovement, c), NonHeroMovementSpent = Write31(value.NonHeroMovementSpent, c), HeroCurrentMovement = Write2(value.HeroCurrentMovement, c), EconomyRosterProtected = Write18(value.EconomyRosterProtected, c), EconomyHomeRouteCosts = Write39(value.EconomyHomeRouteCosts, c), HeroActivationApCost = Write2(value.HeroActivationApCost, c), HeroMoveMax = Write2(value.HeroMoveMax, c), HeroIsHomeVocation = Write18(value.HeroIsHomeVocation, c), HasResearchOperator = Write18(value.HasResearchOperator, c), HasProductionOperator = Write18(value.HasProductionOperator, c), IsHiddenFromUs = Write18(value.IsHiddenFromUs, c), AttackSum = Write15(value.AttackSum, c), DefenseSum = Write15(value.DefenseSum, c), EffectiveArmyPower = Write15(value.EffectiveArmyPower, c), CompositionQuality = Write15(value.CompositionQuality, c), MaxMovement = Write2(value.MaxMovement, c), SafeUnlandedEndsRemaining = Write2(value.SafeUnlandedEndsRemaining, c), Capacity = Write2(value.Capacity, c), OccupiedBattleSlots = Write2(value.OccupiedBattleSlots, c), RosterCapacity = Write2(value.RosterCapacity, c), SupportCarrierSlots = Write2(value.SupportCarrierSlots, c), CommanderRosterCandidate = Write178(value.CommanderRosterCandidate, c), CommanderCommandRating = Write2(value.CommanderCommandRating, c), ActivationApCost = Write2(value.ActivationApCost, c), ActivationEnergyCost = Write2(value.ActivationEnergyCost, c), HasActivatedThisTurn = Write18(value.HasActivatedThisTurn, c), PendingActivationApCost = Write2(value.PendingActivationApCost, c), PendingActivationEnergyCost = Write2(value.PendingActivationEnergyCost, c), ActivationCoveredUnitRuntimeIds = Write183(value.ActivationCoveredUnitRuntimeIds, c), CurrentMovement = Write2(value.CurrentMovement, c), IsSoloRecce = Write18(value.IsSoloRecce, c), IsMobileEconomyBuilder = Write18(value.IsMobileEconomyBuilder, c), OperatorDutyBlocksDeparture = Write18(value.OperatorDutyBlocksDeparture, c), IsStructuralRaidActor = Write18(value.IsStructuralRaidActor, c), IsHidden = Write18(value.IsHidden, c), CanEnterStealth = Write18(value.CanEnterStealth, c), StealthLevel = Write2(value.StealthLevel, c), EffectiveVisionRadius = Write2(value.EffectiveVisionRadius, c), CollectionCapacity = Write123(value.CollectionCapacity, c), ReachableOwnBaseHexes = Write40(value.ReachableOwnBaseHexes, c), RecoveryMembers = Write184(value.RecoveryMembers, c), Members = Write16(value.Members, c) };
        }
        private static global::Game.Ai.V2.ArmySnapshot Read163(MatchAiArmySnapshot163 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.ArmySnapshot { EconomyDeparture = Read163(data.EconomyDeparture, c), ArmyId = Read2(data.ArmyId, c), Owner = Read14(data.Owner, c), Hex = Read8(data.Hex, c), IsGarrison = Read18(data.IsGarrison, c), RequiresGarrisonExtraction = Read18(data.RequiresGarrisonExtraction, c), IsPrison = Read18(data.IsPrison, c), IsAir = Read18(data.IsAir, c), IsAirfield = Read18(data.IsAirfield, c), MobileAirfieldCapacity = Read2(data.MobileAirfieldCapacity, c), RepairSourceCount = Read2(data.RepairSourceCount, c), IsCarrierContainer = Read18(data.IsCarrierContainer, c), CarrierCargoPower = Read15(data.CarrierCargoPower, c), CanServeReconAir = Read18(data.CanServeReconAir, c), StoredAircraft = Read164(data.StoredAircraft, c), MemberCount = Read2(data.MemberCount, c), HasHero = Read18(data.HasHero, c), HeroCount = Read2(data.HeroCount, c), BestHeroCommandRating = Read2(data.BestHeroCommandRating, c), Commander = Read26(data.Commander, c), HasAntiAir = Read18(data.HasAntiAir, c), StrategicCoverage = Read166(data.StrategicCoverage, c), AllyAuraEffects = Read167(data.AllyAuraEffects, c), MembersWithHeroes = Read16(data.MembersWithHeroes, c), NonHeroMutatorOccupied = Read177(data.NonHeroMutatorOccupied, c), NonHeroActivationApCosts = Read31(data.NonHeroActivationApCosts, c), NonHeroMoveMax = Read31(data.NonHeroMoveMax, c), NonHeroIsAviation = Read177(data.NonHeroIsAviation, c), NonHeroRuntimeIds = Read31(data.NonHeroRuntimeIds, c), NonHeroSpareable = Read177(data.NonHeroSpareable, c), GarrisonThreatFloor = Read15(data.GarrisonThreatFloor, c), NonHeroCurrentMovement = Read31(data.NonHeroCurrentMovement, c), NonHeroMovementSpent = Read31(data.NonHeroMovementSpent, c), HeroCurrentMovement = Read2(data.HeroCurrentMovement, c), EconomyRosterProtected = Read18(data.EconomyRosterProtected, c), EconomyHomeRouteCosts = Read39(data.EconomyHomeRouteCosts, c), HeroActivationApCost = Read2(data.HeroActivationApCost, c), HeroMoveMax = Read2(data.HeroMoveMax, c), HeroIsHomeVocation = Read18(data.HeroIsHomeVocation, c), HasResearchOperator = Read18(data.HasResearchOperator, c), HasProductionOperator = Read18(data.HasProductionOperator, c), IsHiddenFromUs = Read18(data.IsHiddenFromUs, c), AttackSum = Read15(data.AttackSum, c), DefenseSum = Read15(data.DefenseSum, c), EffectiveArmyPower = Read15(data.EffectiveArmyPower, c), CompositionQuality = Read15(data.CompositionQuality, c), MaxMovement = Read2(data.MaxMovement, c), SafeUnlandedEndsRemaining = Read2(data.SafeUnlandedEndsRemaining, c), Capacity = Read2(data.Capacity, c), OccupiedBattleSlots = Read2(data.OccupiedBattleSlots, c), RosterCapacity = Read2(data.RosterCapacity, c), SupportCarrierSlots = Read2(data.SupportCarrierSlots, c), CommanderRosterCandidate = Read178(data.CommanderRosterCandidate, c), CommanderCommandRating = Read2(data.CommanderCommandRating, c), ActivationApCost = Read2(data.ActivationApCost, c), ActivationEnergyCost = Read2(data.ActivationEnergyCost, c), HasActivatedThisTurn = Read18(data.HasActivatedThisTurn, c), PendingActivationApCost = Read2(data.PendingActivationApCost, c), PendingActivationEnergyCost = Read2(data.PendingActivationEnergyCost, c), ActivationCoveredUnitRuntimeIds = Read183(data.ActivationCoveredUnitRuntimeIds, c), CurrentMovement = Read2(data.CurrentMovement, c), IsSoloRecce = Read18(data.IsSoloRecce, c), IsMobileEconomyBuilder = Read18(data.IsMobileEconomyBuilder, c), OperatorDutyBlocksDeparture = Read18(data.OperatorDutyBlocksDeparture, c), IsStructuralRaidActor = Read18(data.IsStructuralRaidActor, c), IsHidden = Read18(data.IsHidden, c), CanEnterStealth = Read18(data.CanEnterStealth, c), StealthLevel = Read2(data.StealthLevel, c), EffectiveVisionRadius = Read2(data.EffectiveVisionRadius, c), CollectionCapacity = Read123(data.CollectionCapacity, c), ReachableOwnBaseHexes = Read40(data.ReachableOwnBaseHexes, c), RecoveryMembers = Read184(data.RecoveryMembers, c), Members = Read16(data.Members, c) };
        }
        private static MatchAiList164 Write164(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.StoredAircraftLaunchCost> value, Context c)
        {
            return new MatchAiList164 { HasData = value != null, Values = value == null ? new List<MatchAiStoredAircraftLaunchCost165>() : value.Select(x => Write165(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.StoredAircraftLaunchCost> Read164(MatchAiList164 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read165(x, c)).ToList();
        }
        private static MatchAiStoredAircraftLaunchCost165 Write165(global::Game.Ai.V2.StoredAircraftLaunchCost value, Context c)
        {
            c.RecordUnit(value.RuntimeId);
            return new MatchAiStoredAircraftLaunchCost165 { HasData = true, RuntimeId = Write2(value.RuntimeId, c), Ap = Write2(value.Ap, c), Energy = Write2(value.Energy, c), Movement = Write2(value.Movement, c) };
        }
        private static global::Game.Ai.V2.StoredAircraftLaunchCost Read165(MatchAiStoredAircraftLaunchCost165 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.StoredAircraftLaunchCost(true, Read2(data.RuntimeId, c), Read2(data.Ap, c), Read2(data.Energy, c), Read2(data.Movement, c));
        }
        private static MatchAiRoleCoverage166 Write166(global::Game.Ai.V2.RoleCoverage value, Context c)
        {
            return new MatchAiRoleCoverage166 { HasData = true, Values = Enum.GetValues(typeof(IntendedRole)).Cast<IntendedRole>().Where(value.Has).Select(x => (int)x).ToList() };
        }
        private static global::Game.Ai.V2.RoleCoverage Read166(MatchAiRoleCoverage166 data, Context c)
        {
            Required(data);
            Required(data.Values); var result = RoleCoverage.None; var seen = new HashSet<int>();
            foreach (int value in data.Values) { if (!seen.Add(value)) throw Invalid("Duplicate coverage role"); result = result.With(CheckedEnum<IntendedRole>(value)); }
            return result;
        }
        private static MatchAiList167 Write167(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.StrategicEffect> value, Context c)
        {
            return new MatchAiList167 { HasData = value != null, Values = value == null ? new List<MatchAiStrategicEffect168>() : value.Select(x => Write168(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.StrategicEffect> Read167(MatchAiList167 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read168(x, c)).ToList();
        }
        private static MatchAiStrategicEffect168 Write168(global::Game.Ai.V2.StrategicEffect value, Context c)
        {
            return new MatchAiStrategicEffect168 { HasData = true, Role = Write169(value.Role, c), BaseFit = Write15(value.BaseFit, c), Context = Write170(value.Context, c), Field = Write171(value.Field, c), CountsAsCoverage = Write18(value.CountsAsCoverage, c), EligiblePredicate = Write172(value.EligiblePredicate, c), Scope = Write173(value.Scope, c), Magnitude = Write15(value.Magnitude, c), Probability = Write15(value.Probability, c), Timing = Write174(value.Timing, c), DurationRounds = Write2(value.DurationRounds, c), CapacityRequirement = Write2(value.CapacityRequirement, c), Stacking = Write175(value.Stacking, c), StackingKey = Write4(value.StackingKey, c), GlobalResource = Write176(value.GlobalResource, c), GlobalYieldPerTurn = Write15(value.GlobalYieldPerTurn, c), PrimaryExchange = Write18(value.PrimaryExchange, c), RequiresHit = Write18(value.RequiresHit, c), SecondaryTargets = Write2(value.SecondaryTargets, c), SecondaryPriority = Write2(value.SecondaryPriority, c), BodyOnly = Write18(value.BodyOnly, c) };
        }
        private static global::Game.Ai.V2.StrategicEffect Read168(MatchAiStrategicEffect168 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.StrategicEffect(true, Read169(data.Role, c), Read15(data.BaseFit, c), Read170(data.Context, c), Read171(data.Field, c), Read18(data.CountsAsCoverage, c), Read172(data.EligiblePredicate, c), Read173(data.Scope, c), Read15(data.Magnitude, c), Read15(data.Probability, c), Read174(data.Timing, c), Read2(data.DurationRounds, c), Read2(data.CapacityRequirement, c), Read175(data.Stacking, c), Read4(data.StackingKey, c), Read176(data.GlobalResource, c), Read15(data.GlobalYieldPerTurn, c), Read18(data.PrimaryExchange, c), Read18(data.RequiresHit, c), Read2(data.SecondaryTargets, c), Read2(data.SecondaryPriority, c), Read18(data.BodyOnly, c));
        }
        private static int Write169(global::Game.Ai.V2.IntendedRole value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.IntendedRole Read169(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.IntendedRole>(data);
        }
        private static int Write170(global::Game.Ai.V2.StrategicEffectContext value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.StrategicEffectContext Read170(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.StrategicEffectContext>(data);
        }
        private static int Write171(global::Game.Ai.V2.EffectField value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.EffectField Read171(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.EffectField>(data);
        }
        private static string Write172(global::System.Func<global::Game.Combat.WorthIt.DefenderProfile, bool> value, Context c)
        {
            return WritePredicate(value);
        }
        private static global::System.Func<global::Game.Combat.WorthIt.DefenderProfile, bool> Read172(string data, Context c)
        {
            return ReadPredicate(data);
        }
        private static int Write173(global::Game.Ai.V2.EffectScope value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.EffectScope Read173(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.EffectScope>(data);
        }
        private static int Write174(global::Game.Ai.V2.EffectTiming value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.EffectTiming Read174(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.EffectTiming>(data);
        }
        private static int Write175(global::Game.Ai.V2.EffectStacking value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.EffectStacking Read175(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.EffectStacking>(data);
        }
        private static int Write176(global::Game.Ai.V2.GlobalResourceKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.GlobalResourceKind Read176(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.GlobalResourceKind>(data);
        }
        private static MatchAiList177 Write177(global::System.Collections.Generic.IReadOnlyList<bool> value, Context c)
        {
            return new MatchAiList177 { HasData = value != null, Values = value == null ? new List<bool>() : value.Select(x => Write18(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<bool> Read177(MatchAiList177 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read18(x, c)).ToList();
        }
        private static MatchAiOptional178 Write178(global::Game.Ai.V2.StrikeRosterCandidate? value, Context c)
        {
            return new MatchAiOptional178 { HasData = value.HasValue, Value = value.HasValue ? Write179(value.Value, c) : default };
        }
        private static global::Game.Ai.V2.StrikeRosterCandidate? Read178(MatchAiOptional178 data, Context c)
        {
            Required(data);
            return data.HasData ? (global::Game.Ai.V2.StrikeRosterCandidate?)Read179(data.Value, c) : null;
        }
        private static MatchAiStrikeRosterCandidate179 Write179(global::Game.Ai.V2.StrikeRosterCandidate value, Context c)
        {
            return new MatchAiStrikeRosterCandidate179 { HasData = true, Slot = Write180(value.Slot, c), Unit = Write182(value.Unit, c) };
        }
        private static global::Game.Ai.V2.StrikeRosterCandidate Read179(MatchAiStrikeRosterCandidate179 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.StrikeRosterCandidate(true, Read180(data.Slot, c), Read182(data.Unit, c));
        }
        private static MatchAiStrikeRosterSlot180 Write180(global::Game.Ai.V2.StrikeRosterSlot value, Context c)
        {
            return new MatchAiStrikeRosterSlot180 { HasData = true, Key = Write4(value.Key, c), IsHero = Write18(value.IsHero, c), Power = Write15(value.Power, c), Source = Write181(value.Source, c) };
        }
        private static global::Game.Ai.V2.StrikeRosterSlot Read180(MatchAiStrikeRosterSlot180 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.StrikeRosterSlot(true, Read4(data.Key, c), Read18(data.IsHero, c), Read15(data.Power, c), Read181(data.Source, c));
        }
        private static int Write181(global::Game.Ai.V2.ForceSource value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.ForceSource Read181(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.ForceSource>(data);
        }
        private static MatchAiPowerUnit182 Write182(global::Game.Ai.V2.AiPower.PowerUnit value, Context c)
        {
            return new MatchAiPowerUnit182 { HasData = true, BasePower = Write15(value.BasePower, c), Tags = Write24(value.Tags, c), Range = Write2(value.Range, c), IsHero = Write18(value.IsHero, c), CommandRating = Write2(value.CommandRating, c) };
        }
        private static global::Game.Ai.V2.AiPower.PowerUnit Read182(MatchAiPowerUnit182 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.AiPower.PowerUnit(true, Read15(data.BasePower, c), Read24(data.Tags, c), Read2(data.Range, c), Read18(data.IsHero, c), Read2(data.CommandRating, c));
        }
        private static MatchAiList183 Write183(global::System.Collections.Generic.IReadOnlyCollection<int> value, Context c)
        {
            return new MatchAiList183 { HasData = value != null, Values = value == null ? new List<int>() : value.Select(x => Write2(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyCollection<int> Read183(MatchAiList183 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read2(x, c)).ToList();
        }
        private static MatchAiList184 Write184(global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.RaidRecoveryMemberSnapshot> value, Context c)
        {
            return new MatchAiList184 { HasData = value != null, Values = value == null ? new List<MatchAiRaidRecoveryMemberSnapshot185>() : value.Select(x => Write185(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.V2.RaidRecoveryMemberSnapshot> Read184(MatchAiList184 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read185(x, c)).ToList();
        }
        private static MatchAiRaidRecoveryMemberSnapshot185 Write185(global::Game.Ai.V2.RaidRecoveryMemberSnapshot value, Context c)
        {
            c.RecordUnit(value.RuntimeId);
            return new MatchAiRaidRecoveryMemberSnapshot185 { HasData = true, RuntimeId = Write2(value.RuntimeId, c), UnitIndex = Write2(value.UnitIndex, c), IsHero = Write18(value.IsHero, c), IsAviation = Write18(value.IsAviation, c), IsGroundBattleBody = Write18(value.IsGroundBattleBody, c), CanSpareForRaid = Write18(value.CanSpareForRaid, c), ActivationApCost = Write2(value.ActivationApCost, c), CurrentProfile = Write17(value.CurrentProfile, c), FullHealthProfile = Write17(value.FullHealthProfile, c), RepairCostInitialized = Write18(value.RepairCostInitialized, c), IsPrisoner = Write18(value.IsPrisoner, c), RepairCost = Write186(value.RepairCost, c) };
        }
        private static global::Game.Ai.V2.RaidRecoveryMemberSnapshot Read185(MatchAiRaidRecoveryMemberSnapshot185 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.RaidRecoveryMemberSnapshot(true, Read2(data.RuntimeId, c), Read2(data.UnitIndex, c), Read18(data.IsHero, c), Read18(data.IsAviation, c), Read18(data.IsGroundBattleBody, c), Read18(data.CanSpareForRaid, c), Read2(data.ActivationApCost, c), Read17(data.CurrentProfile, c), Read17(data.FullHealthProfile, c), Read18(data.RepairCostInitialized, c), Read18(data.IsPrisoner, c), Read186(data.RepairCost, c));
        }
        private static MatchAiResourceVector186 Write186(global::Game.Ai.V2.ResourceVector value, Context c)
        {
            return new MatchAiResourceVector186 { HasData = true, Ap = Write15(value.Ap, c), Human = Write15(value.Human, c), Energy = Write15(value.Energy, c), Materials = Write15(value.Materials, c), Tech = Write15(value.Tech, c) };
        }
        private static global::Game.Ai.V2.ResourceVector Read186(MatchAiResourceVector186 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            return new global::Game.Ai.V2.ResourceVector(true, Read15(data.Ap, c), Read15(data.Human, c), Read15(data.Energy, c), Read15(data.Materials, c), Read15(data.Tech, c));
        }
        private static int Write187(global::Game.Ai.V2.AssetKind value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.AssetKind Read187(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.AssetKind>(data);
        }
        private static MatchAiAttackIntent188 Write188(global::Game.Ai.V2.AttackIntent value, Context c)
        {
            if (value == null) return new MatchAiAttackIntent188();
            if (value.CommanderArmyId.HasValue) c.RecordArmy(value.CommanderArmyId.Value);
            if (value.GatherSupportArmyIds != null) foreach (int id in value.GatherSupportArmyIds) c.RecordArmy(id);
            if (value.AirSupportArmyId.HasValue) c.RecordArmy(value.AirSupportArmyId.Value);
            if (value.PrimaryArmyId.HasValue) c.RecordArmy(value.PrimaryArmyId.Value);
            if (value.SupportArmyId.HasValue) c.RecordArmy(value.SupportArmyId.Value);
            return new MatchAiAttackIntent188 { HasData = true, Target = Write189(value.Target, c), IntermediateTarget = Write189(value.IntermediateTarget, c), Phase = Write190(value.Phase, c), OperationStarted = Write18(value.OperationStarted, c), AssaultStarted = Write18(value.AssaultStarted, c), AssaultObservationWaitSinceTurn = Write2(value.AssaultObservationWaitSinceTurn, c), RefitBaseHex = Write117(value.RefitBaseHex, c), RefitCaptureTurn = Write2(value.RefitCaptureTurn, c), RefitBattleStopTurn = Write2(value.RefitBattleStopTurn, c), Preparation = Write18(value.Preparation, c), StagingHex = Write117(value.StagingHex, c), Secondary = Write18(value.Secondary, c), PreparationCardWaitSinceTurn = Write2(value.PreparationCardWaitSinceTurn, c), TargetRoster = Write191(value.TargetRoster, c), TargetRosterPeak = Write15(value.TargetRosterPeak, c), TargetRosterCommanderKey = Write4(value.TargetRosterCommanderKey, c), TargetRosterPower = Write15(value.TargetRosterPower, c), TargetRosterCapacity = Write2(value.TargetRosterCapacity, c), CommanderArmyId = Write52(value.CommanderArmyId, c), GatherSupportArmyIds = Write192(value.GatherSupportArmyIds, c), GatherReturns = Write193(value.GatherReturns, c), AirSupportArmyId = Write52(value.AirSupportArmyId, c), AirSupportLandingHex = Write117(value.AirSupportLandingHex, c), AirSupportBoundTurn = Write2(value.AirSupportBoundTurn, c), AirSupportSortieSeen = Write18(value.AirSupportSortieSeen, c), AirSupportAttemptedTurn = Write2(value.AirSupportAttemptedTurn, c), RecoveryBaseHex = Write117(value.RecoveryBaseHex, c), SupportReturnHex = Write117(value.SupportReturnHex, c), RendezvousHex = Write117(value.RendezvousHex, c), ReinforcementRequestedTurn = Write2(value.ReinforcementRequestedTurn, c), LastOpportunisticStrikeTurn = Write2(value.LastOpportunisticStrikeTurn, c), TacticalRetreat = Write18(value.TacticalRetreat, c), ProjectedWinChance = Write15(value.ProjectedWinChance, c), CoversAllDefenders = Write18(value.CoversAllDefenders, c), PrimaryArmyId = Write52(value.PrimaryArmyId, c), SupportArmyId = Write52(value.SupportArmyId, c) };
        }
        private static global::Game.Ai.V2.AttackIntent Read188(MatchAiAttackIntent188 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.AttackIntent { Target = Read189(data.Target, c), IntermediateTarget = Read189(data.IntermediateTarget, c), Phase = Read190(data.Phase, c), OperationStarted = Read18(data.OperationStarted, c), AssaultStarted = Read18(data.AssaultStarted, c), AssaultObservationWaitSinceTurn = Read2(data.AssaultObservationWaitSinceTurn, c), RefitBaseHex = Read117(data.RefitBaseHex, c), RefitCaptureTurn = Read2(data.RefitCaptureTurn, c), RefitBattleStopTurn = Read2(data.RefitBattleStopTurn, c), Preparation = Read18(data.Preparation, c), StagingHex = Read117(data.StagingHex, c), Secondary = Read18(data.Secondary, c), PreparationCardWaitSinceTurn = Read2(data.PreparationCardWaitSinceTurn, c), TargetRoster = Read191(data.TargetRoster, c), TargetRosterPeak = Read15(data.TargetRosterPeak, c), TargetRosterCommanderKey = Read4(data.TargetRosterCommanderKey, c), TargetRosterPower = Read15(data.TargetRosterPower, c), TargetRosterCapacity = Read2(data.TargetRosterCapacity, c), CommanderArmyId = Read52(data.CommanderArmyId, c), GatherSupportArmyIds = Read192(data.GatherSupportArmyIds, c), GatherReturns = Read193(data.GatherReturns, c), AirSupportArmyId = Read52(data.AirSupportArmyId, c), AirSupportLandingHex = Read117(data.AirSupportLandingHex, c), AirSupportBoundTurn = Read2(data.AirSupportBoundTurn, c), AirSupportSortieSeen = Read18(data.AirSupportSortieSeen, c), AirSupportAttemptedTurn = Read2(data.AirSupportAttemptedTurn, c), RecoveryBaseHex = Read117(data.RecoveryBaseHex, c), SupportReturnHex = Read117(data.SupportReturnHex, c), RendezvousHex = Read117(data.RendezvousHex, c), ReinforcementRequestedTurn = Read2(data.ReinforcementRequestedTurn, c), LastOpportunisticStrikeTurn = Read2(data.LastOpportunisticStrikeTurn, c), TacticalRetreat = Read18(data.TacticalRetreat, c), ProjectedWinChance = Read15(data.ProjectedWinChance, c), CoversAllDefenders = Read18(data.CoversAllDefenders, c), PrimaryArmyId = Read52(data.PrimaryArmyId, c), SupportArmyId = Read52(data.SupportArmyId, c) };
        }
        private static MatchAiAttackTargetRef189 Write189(global::Game.Ai.V2.AttackTargetRef value, Context c)
        {
            return new MatchAiAttackTargetRef189 { HasData = true, HasValue = Write18(value.HasValue, c), Hex = Write8(value.Hex, c), ExpectedOwner = Write14(value.ExpectedOwner, c), Kind = Write56(value.Kind, c) };
        }
        private static global::Game.Ai.V2.AttackTargetRef Read189(MatchAiAttackTargetRef189 data, Context c)
        {
            Required(data);
            if (!data.HasData) throw Invalid("Missing value");
            var kind = Read56(data.Kind, c);
            if (!data.HasValue) return AttackTargetRef.None;
            var owner = Read14(data.ExpectedOwner, c);
            if (owner == null) throw Invalid("Missing attack owner");
            return AttackTargetRef.For(Read8(data.Hex, c), owner, kind);
        }
        private static int Write190(global::Game.Ai.V2.AttackMissionPhase value, Context c)
        {
            return (int)value;
        }
        private static global::Game.Ai.V2.AttackMissionPhase Read190(int data, Context c)
        {
            return CheckedEnum<global::Game.Ai.V2.AttackMissionPhase>(data);
        }
        private static MatchAiList191 Write191(global::System.Collections.Generic.List<global::Game.Ai.V2.StrikeRosterSlot> value, Context c)
        {
            return new MatchAiList191 { HasData = value != null, Values = value == null ? new List<MatchAiStrikeRosterSlot180>() : value.Select(x => Write180(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.List<global::Game.Ai.V2.StrikeRosterSlot> Read191(MatchAiList191 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read180(x, c)).ToList();
        }
        private static MatchAiList192 Write192(global::System.Collections.Generic.List<int> value, Context c)
        {
            return new MatchAiList192 { HasData = value != null, Values = value == null ? new List<int>() : value.Select(x => Write2(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.List<int> Read192(MatchAiList192 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read2(x, c)).ToList();
        }
        private static MatchAiList193 Write193(global::System.Collections.Generic.List<global::Game.Ai.V2.AttackGatherReturn> value, Context c)
        {
            return new MatchAiList193 { HasData = value != null, Values = value == null ? new List<MatchAiAttackGatherReturn194>() : value.Select(x => Write194(x, c)).ToList() };
        }
        private static global::System.Collections.Generic.List<global::Game.Ai.V2.AttackGatherReturn> Read193(MatchAiList193 data, Context c)
        {
            Required(data);
            Required(data.Values); if (!data.HasData) { if (data.Values.Count != 0) throw Invalid("Absent list has values"); return null; }
            return data.Values.Select(x => Read194(x, c)).ToList();
        }
        private static MatchAiAttackGatherReturn194 Write194(global::Game.Ai.V2.AttackGatherReturn value, Context c)
        {
            if (value == null) return new MatchAiAttackGatherReturn194();
            c.RecordArmy(value.ArmyId);
            return new MatchAiAttackGatherReturn194 { HasData = true, ArmyId = Write2(value.ArmyId, c), BaseHex = Write117(value.BaseHex, c) };
        }
        private static global::Game.Ai.V2.AttackGatherReturn Read194(MatchAiAttackGatherReturn194 data, Context c)
        {
            Required(data);
            if (!data.HasData) return null;
            return new global::Game.Ai.V2.AttackGatherReturn { ArmyId = Read2(data.ArmyId, c), BaseHex = Read117(data.BaseHex, c) };
        }
    }
}
