using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Aviation
{
    [Serializable] public sealed class AntiAirPromptState { public int UnitId; public int AirArmyId; }
    [Serializable] public sealed class FacilityGunnerState { public HexCoord BuildingHex; public int Slot; public int UnitId; }
    [Serializable] public sealed class AntiAirTurnState
    {
        public List<int> FiredUnitIds = new List<int>();
        public List<AntiAirPromptState> Prompted = new List<AntiAirPromptState>();
        public List<FacilityGunnerState> FacilityGunners = new List<FacilityGunnerState>();
    }
    public static partial class AntiAirState
    {
        public static AntiAirTurnState CaptureState(Action<UnitData> includeReferencedUnit = null)
        {
            var result = new AntiAirTurnState();
            foreach (var unit in FiredThisTurn)
            { includeReferencedUnit?.Invoke(unit); result.FiredUnitIds.Add(unit.RuntimeId); }
            foreach (var pair in PromptedThisTurn)
            { includeReferencedUnit?.Invoke(pair.aaUnit); result.Prompted.Add(new AntiAirPromptState { UnitId = pair.aaUnit.RuntimeId, AirArmyId = pair.airArmyId }); }
            foreach (var building in BuildingRegistry.AllBuildings())
                for (int slot = 0; slot < building.FacilitySlots.Length; slot++)
                    if (building.FacilitySlots[slot]?.AntiAirGunner != null)
                    {
                        includeReferencedUnit?.Invoke(building.FacilitySlots[slot].AntiAirGunner);
                        result.FacilityGunners.Add(new FacilityGunnerState { BuildingHex = building.Hex, Slot = slot, UnitId = building.FacilitySlots[slot].AntiAirGunner.RuntimeId });
                    }
            return result;
        }
        // Rebind stable stand-in gunners as well as deployed units. A facility must retain the
        // same gunner used in the turn ledger; lazily minting a new one would grant another shot.
        public static void RestoreState(AntiAirTurnState state, Func<int, UnitData> resolveUnit)
        {
            if (state?.FiredUnitIds == null || state.Prompted == null || state.FacilityGunners == null) throw new ArgumentException("Missing anti-air state.");
            var fired = new HashSet<UnitData>(); var prompted = new HashSet<(UnitData, int)>();
            var gunners = new Dictionary<FacilityData, UnitData>(); int maxArmyId = -1;
            foreach (int id in state.FiredUnitIds)
                if (!fired.Add(StateReference.Unit(id, resolveUnit))) throw new ArgumentException("Duplicate fired unit.");
            foreach (var pair in state.Prompted)
            {
                if (pair == null || pair.AirArmyId < 0 || pair.AirArmyId == int.MaxValue) throw new ArgumentException("Invalid prompted army.");
                if (!prompted.Add((StateReference.Unit(pair.UnitId, resolveUnit), pair.AirArmyId))) throw new ArgumentException("Duplicate anti-air prompt.");
                maxArmyId = Math.Max(maxArmyId, pair.AirArmyId);
            }
            foreach (var entry in state.FacilityGunners)
            {
                if (entry == null) throw new ArgumentException("Missing facility binding.");
                var building = BuildingRegistry.FindAt(entry.BuildingHex);
                if (building == null || entry.Slot < 0 || entry.Slot >= building.FacilitySlots.Length || building.FacilitySlots[entry.Slot] == null)
                    throw new ArgumentException("Unresolved facility gunner slot.");
                var facility = building.FacilitySlots[entry.Slot];
                if (gunners.ContainsKey(facility)) throw new ArgumentException("Duplicate facility gunner.");
                gunners.Add(facility, StateReference.Unit(entry.UnitId, resolveUnit));
            }
            FiredThisTurn.Clear(); FiredThisTurn.UnionWith(fired); PromptedThisTurn.Clear(); PromptedThisTurn.UnionWith(prompted);
            foreach (var building in BuildingRegistry.AllBuildings()) foreach (var facility in building.FacilitySlots) if (facility != null) facility.AntiAirGunner = null;
            foreach (var pair in gunners) pair.Key.AntiAirGunner = pair.Value;
            if (maxArmyId >= 0) ArmyData.ReserveIdentity(maxArmyId);
        }
    }
}
