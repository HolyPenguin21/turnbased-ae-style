using System;
using Game.Aviation;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.HexGrid;

namespace Game.UI
{
    public static class ArmyRadioContext
    {
        // Historical facts (battle result/losses) need freshness, not an invented current-state test.
        // Position/state reports require their specific live context as well as viewer authority.
        public static bool IsCurrent(ArmyRadioCandidate item, PlayerSetupData viewer, bool battleActive = false, bool contact = false)
        {
            if (item == null || item.Viewer != viewer || viewer != VisionSystem.CurrentViewer
                || !ArmyRegistry.IsRegistered(item.Speaker) || !ArmyRadioChatter.CanSpeak(item.Speaker, viewer)) return false;
            var army = item.Speaker;
            var unit = item.Subject as UnitData;
            var building = item.Subject as BuildingData;
            switch (item.Category)
            {
                case ArmyRadioCategory.BattleStarted:
                    return item.Subject is BattleState battle && battleActive && army.Hex.Equals(item.Hex);
                case ArmyRadioCategory.BattleWon:
                    return army.Hex.Equals(item.Hex) && !DelayedBattleRegistry.IsHexPending(item.Hex)
                        && BattleInitiator.FindEnemyAt(item.Hex, army) == null;
                case ArmyRadioCategory.HeavyLosses: return true;
                case ArmyRadioCategory.Retreat: return army.Hex.Equals(item.Hex);
                case ArmyRadioCategory.HeroCaptured:
                    return unit != null && unit.HitPointsCurrent > 0 && unit.IsPrisoner && unit.Owner == viewer
                        && viewer.CitadelHexQ.HasValue && viewer.CitadelHexR.HasValue
                        && InOwnPrison(unit, viewer);
                case ArmyRadioCategory.EnemyBaseDiscovered:
                    return VisionSystem.IsVisible(viewer, item.Hex) && building != null && building.Owner != viewer
                        && building.Owner != null && ReferenceEquals(BuildingRegistry.FindAt(item.Hex), building)
                        && HexGridMath.Distance(army.Controller != null ? army.Controller.CurrentHex : army.Hex, item.Hex) <= VisionSystem.ArmyVisionRadius(army);
                case ArmyRadioCategory.BaseCaptured: case ArmyRadioCategory.BaseFounded:
                    return army.Hex.Equals(item.Hex) && building != null && building.Owner == viewer
                        && ReferenceEquals(BuildingRegistry.FindAt(item.Hex), building);
                case ArmyRadioCategory.EventDiscovered:
                    return army.Hex.Equals(item.Hex) && VisionSystem.IsVisible(viewer, item.Hex)
                        && HexEventRegistry.IsKnownBy(item.Hex, viewer) && HexEventRegistry.HasActiveEvent(item.Hex)
                        && ReferenceEquals(HexEventRegistry.FindAt(item.Hex), item.Subject);
                case ArmyRadioCategory.TerritoryExplored: return !army.IsAirArmy && army.Hex.Equals(item.Hex) && VisionSystem.IsVisited(viewer, item.Hex);
                case ArmyRadioCategory.ReinforcementsArrived:
                    return army.Hex.Equals(item.Hex) && unit != null && army.Members.Contains(unit) && unit.HitPointsCurrent > 0;
                case ArmyRadioCategory.RepairsCompleted:
                    return unit != null && army.Members.Contains(unit) && unit.HitPointsCurrent > 0 && !UnitRepair.IsWounded(unit);
                case ArmyRadioCategory.EquipmentReceived:
                    return unit != null && army.Members.Contains(unit) && unit.HitPointsCurrent > 0 && unit.Equipment != null;
                case ArmyRadioCategory.AviationSortie: return army.IsAirArmy && !army.AirWingLanded;
                case ArmyRadioCategory.AviationReturned:
                    var storage = AviationRules.FindAirfieldAt(item.Hex, viewer);
                    return army.Hex.Equals(item.Hex) && unit != null && storage != null && storage.Owner == viewer && storage.IsAirfield
                        && storage.Hex.Equals(item.Hex) && storage.Members.Contains(unit) && unit.HitPointsCurrent > 0;
                case ArmyRadioCategory.Contact: return contact;
                case ArmyRadioCategory.Stealth: return StealthSystem.IsArmyFullyHidden(army);
                case ArmyRadioCategory.Garrison: return army.IsGarrison || army.IsAirfield;
                case ArmyRadioCategory.Airborne: return army.IsAirArmy;
                case ArmyRadioCategory.Atmosphere: return !contact && ArmyRadioChatter.CanIdleFlavor(army);
                case ArmyRadioCategory.Damaged:
                    foreach (var member in army.Members) if (member != null && member.HitPointsCurrent > 0 && UnitRepair.IsWounded(member)) return true;
                    return false;
                case ArmyRadioCategory.Moving: return army.Hex.Equals(item.Hex);
                default: return true;
            }
        }
        private static bool InOwnPrison(UnitData unit, PlayerSetupData viewer)
        {
            var hex = new HexCoord(viewer.CitadelHexQ.Value, viewer.CitadelHexR.Value);
            foreach (var army in ArmyRegistry.AllAt(hex))
                if (army.IsPrison && army.Owner == viewer && army.Members.Contains(unit)) return true;
            return false;
        }
    }
}
