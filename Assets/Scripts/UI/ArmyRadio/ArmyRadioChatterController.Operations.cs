using System;
using System.Collections.Generic;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.UI
{
    public sealed partial class ArmyRadioChatterController
    {
        private sealed class BattleObservation
        {
            public ArmyData Army;
            public readonly List<UnitData> Members = new List<UnitData>();
        }
        private readonly ArmyRadioDispatch _dispatch = new ArmyRadioDispatch();
        private readonly Dictionary<BattleState, BattleObservation> _battles = new Dictionary<BattleState, BattleObservation>();
        private readonly HashSet<BuildingData> _seenBases = new HashSet<BuildingData>();
        private readonly Dictionary<ArmyData, HexCoord> _exploredOnMove = new Dictionary<ArmyData, HexCoord>();
        // Presentation identities for observed flights; never a second fuel/endurance state.
        private readonly Dictionary<ArmyData, object> _sorties = new Dictionary<ArmyData, object>();
        private readonly Dictionary<ArmyData, ArmyRadioCandidate> _onAir = new Dictionary<ArmyData, ArmyRadioCandidate>();
        private Func<ArmyRadioCandidate, bool> _validCandidate, _readyCandidate;
        private Action<BuildingData> _observeBase;
        private ArmyData _samplingArmy;
        private bool _primeObservation = true;

        private void SubscribeOperations()
        {
            BattleEngine.EncounterStarted += OnEncounterStarted;
            BattleEngine.EncounterFinalized += OnEncounterFinalized;
            BattleEngine.ArmyRetreated += OnRetreated;
            BattleEngine.HeroImprisoned += OnHeroImprisoned;
            BuildingRegistry.BaseCaptured += OnBaseCaptured;
            InfrastructureActions.BaseFounded += OnBaseFounded;
            HexEventRegistry.EventDiscovered += OnEventDiscovered;
            VisionSystem.GroundHexVisited += OnGroundVisited;
            ArmyActions.UnitDeployed += OnUnitDeployed;
            UnitRepair.Repaired += OnRepaired;
            EquipmentSystem.LiveAttachmentReceived += OnEquipmentReceived;
            AviationActions.AircraftLanded += OnAircraftLanded;
            AviationCombatPresenter.OperationCompleted += OnFlightOperation;
        }
        private void UnsubscribeOperations()
        {
            BattleEngine.EncounterStarted -= OnEncounterStarted;
            BattleEngine.EncounterFinalized -= OnEncounterFinalized;
            BattleEngine.ArmyRetreated -= OnRetreated;
            BattleEngine.HeroImprisoned -= OnHeroImprisoned;
            BuildingRegistry.BaseCaptured -= OnBaseCaptured;
            InfrastructureActions.BaseFounded -= OnBaseFounded;
            HexEventRegistry.EventDiscovered -= OnEventDiscovered;
            VisionSystem.GroundHexVisited -= OnGroundVisited;
            ArmyActions.UnitDeployed -= OnUnitDeployed;
            UnitRepair.Repaired -= OnRepaired;
            EquipmentSystem.LiveAttachmentReceived -= OnEquipmentReceived;
            AviationActions.AircraftLanded -= OnAircraftLanded;
            AviationCombatPresenter.OperationCompleted -= OnFlightOperation;
        }
        private void ClearOperations()
        {
            _dispatch.Clear(); _battles.Clear(); _seenBases.Clear(); _sorties.Clear(); _onAir.Clear(); _exploredOnMove.Clear();
            _primeObservation = true;
            _validCandidate ??= IsCurrent; _readyCandidate ??= IsReady; _observeBase ??= ObserveBase;
        }
        private bool CanReceive(ArmyData army) => _settings != null && _settings.enabled
            && _turns != null && _turns.IsArmyRadioMatchActive
            && _viewer == VisionSystem.CurrentViewer && ArmyRegistry.IsRegistered(army)
            && !army.IsAirfield && ArmyRadioChatter.CanSpeak(army, _viewer);
        private void Enqueue(ArmyRadioCategory category, ArmyData army, object subject, HexCoord hex, ArmyData relatedArmy = null)
        {
            if (!CanReceive(army)) return;
            float now = Time.unscaledTime;
            float ttl = ArmyRadioDispatch.Priority(category) == 1 ? _settings.operationalTtl : _settings.minorEventTtl;
            var candidate = new ArmyRadioCandidate(category, army, _viewer, subject, hex, now, Mathf.Clamp(ttl, 1f, 12f), relatedArmy);
            _dispatch.Add(candidate, now, _settings.maximumPendingCandidates);
        }
        // A relay needs an actual own, living formation with direct knowledge of this hex.
        // Stored aircraft and empty garrison shells never become fictitious UI speakers.
        private ArmyData FindReporter(HexCoord hex, bool atHex)
        {
            if (_viewer == null || !VisionSystem.IsVisible(_viewer, hex)) return null;
            ArmyData best = null;
            foreach (var army in ArmyRegistry.AllForOwner(_viewer))
            {
                if (!ArmyRadioChatter.CanSpeak(army, _viewer) || army.IsAirfield
                    || !HasSpeakerVisual(army)) continue;
                var origin = army.Controller != null ? army.Controller.CurrentHex : army.Hex;
                if (atHex ? !origin.Equals(hex) : HexGridMath.Distance(origin, hex) > VisionSystem.ArmyVisionRadius(army)) continue;
                if (best == null || (best.Id <= _lastSpeakerId && army.Id > _lastSpeakerId)
                    || ((best.Id <= _lastSpeakerId) == (army.Id <= _lastSpeakerId) && army.Id < best.Id)) best = army;
            }
            return best;
        }
        private void OnEncounterStarted(BattleState state)
        {
            var army = CanReceive(state.Attacker) ? state.Attacker : CanReceive(state.Defender) ? state.Defender : null;
            if (army == null) return;
            if (_battles.Count >= 8) _battles.Clear();
            var observation = new BattleObservation { Army = army };
            foreach (var member in army.Members)
                if (member != null && !member.IsSummoned && !member.IsPrisoner && member.HitPointsCurrent > 0)
                    observation.Members.Add(member);
            _battles[state] = observation;
            Enqueue(ArmyRadioCategory.BattleStarted, army, state, state.BattleHex);
        }
        private void OnEncounterFinalized(BattleState state, BattleEncounterFinalization result)
        {
            if (_battles.TryGetValue(state, out var observation))
            { ReportLosses(state, observation); _battles.Remove(state); }
            if (result.Survivor != null)
                Enqueue(ArmyRadioCategory.BattleWon, result.Survivor, state, state.BattleHex);
            Invalidate();
        }
        private void ReportLosses(BattleState state, BattleObservation observation)
        {
            int lost = 0;
            foreach (var member in observation.Members)
                if (member.HitPointsCurrent <= 0 || member.IsPrisoner || !observation.Army.Members.Contains(member)) lost++;
            if (lost >= 2 && lost * 2 >= observation.Members.Count)
                Enqueue(ArmyRadioCategory.HeavyLosses, observation.Army, state, observation.Army.Hex);
        }
        private void OnRetreated(BattleState state, ArmyData army, BattleRetreatApplication result)
        {
            if (result.Relocated && !result.Destroyed)
                Enqueue(ArmyRadioCategory.Retreat, army, state ?? (object)army, result.Destination);
        }
        private void OnHeroImprisoned(ArmyData army, UnitData hero)
            => Enqueue(ArmyRadioCategory.HeroCaptured, army, hero, army.Hex);
        private void OnBaseCaptured(BuildingData building, PlayerSetupData previous)
        {
            if (building.Owner != _viewer || previous == _viewer) return;
            Enqueue(ArmyRadioCategory.BaseCaptured, FindReporter(building.Hex, true), building, building.Hex);
        }
        private void OnBaseFounded(BuildingData building)
        {
            if (building.Owner != _viewer) return;
            Enqueue(ArmyRadioCategory.BaseFounded, FindReporter(building.Hex, true), building, building.Hex);
        }
        private void OnEventDiscovered(HexCoord hex, PlayerSetupData player)
        {
            if (player != _viewer || !HexEventRegistry.IsKnownBy(hex, player)) return;
            Enqueue(ArmyRadioCategory.EventDiscovered, FindReporter(hex, true), HexEventRegistry.FindAt(hex), hex);
        }
        private void OnGroundVisited(PlayerSetupData player, HexCoord hex)
        {
            if (player != _viewer || _primeObservation) return;
            var army = FindReporter(hex, true);
            if (army != null && !army.IsAirArmy)
                _exploredOnMove[army] = hex;
        }
        private void OnUnitDeployed(ArmyData army, UnitData unit)
        {
            if (army.Members.Count > 1)
                Enqueue(ArmyRadioCategory.ReinforcementsArrived, army, unit, army.Hex);
            if (unit.Equipment != null) OnEquipmentReceived(army, unit, unit.Equipment);
        }
        private void OnRepaired(ArmyData army, UnitData unit)
            => Enqueue(ArmyRadioCategory.RepairsCompleted, army, unit, army.Hex);
        private void OnEquipmentReceived(ArmyData army, UnitData unit, CardDefinition gear)
        {
            if (gear != null && gear.attachmentSlot != AttachmentSlot.Mutator)
                Enqueue(ArmyRadioCategory.EquipmentReceived, army, unit, army.Hex);
        }
        private void OnAircraftLanded(ArmyData wing, ArmyData receiver, UnitData aircraft)
        {
            bool observedSortie = _sorties.ContainsKey(wing);
            bool stillOperating = false;
            foreach (var member in wing.Members)
                if (member != null && member.SortieLaunchPaid) { stillOperating = true; break; }
            if (!stillOperating) _sorties.Remove(wing);
            _dispatch.Prune(Time.unscaledTime, _validCandidate);
            if (observedSortie && receiver.Owner == _viewer)
                Enqueue(ArmyRadioCategory.AviationReturned, FindReporter(receiver.Hex, true), aircraft, receiver.Hex, receiver);
        }
        private void OnFlightOperation(ArmyData army)
        {
            if (!CanReceive(army) || !AviationRules.IsAirArmy(army) || _sorties.ContainsKey(army)) return;
            var identity = new object();
            _sorties.Add(army, identity);
            _dispatch.Prune(Time.unscaledTime, _validCandidate);
            Enqueue(ArmyRadioCategory.AviationSortie, army, identity, army.Hex);
        }
        private void ObserveBase(BuildingData building)
        {
            if (building == null || !building.IsBase || building.Owner == null || building.Owner == _viewer) return;
            if (_primeObservation) { _seenBases.Add(building); return; }
            if (!HasSpeakerVisual(_samplingArmy) || !_seenBases.Add(building)) return;
            Enqueue(ArmyRadioCategory.EnemyBaseDiscovered, _samplingArmy, building, building.Hex);
        }
        private static bool HasSpeakerVisual(ArmyData army) => army != null && army.Controller != null
            && army.Controller.isActiveAndEnabled && army.Controller.Visual != null && army.Controller.Visual.IsVisible
            && ArmyRadioChatter.DetailArt(ArmyRadioChatter.FirstUnit(army)) != null;
        private bool IsReady(ArmyRadioCandidate item) => !Busy(item.Speaker) && !IsReserved(item.Speaker)
            && _cadence.ArmyReady(item.Speaker.Id, Time.unscaledTime, _settings.armyCooldown)
            && CanPresent(item.Speaker, out _, out _);

        private bool IsCurrent(ArmyRadioCandidate item) => ArmyRadioContext.IsCurrent(item, _viewer,
            item.Subject is BattleState state && _battles.ContainsKey(state), _contacts.Contains(item.Speaker))
            && (item.Category != ArmyRadioCategory.AviationSortie
                || (_sorties.TryGetValue(item.Speaker, out var identity) && ReferenceEquals(item.Subject, identity)));
    }
}
