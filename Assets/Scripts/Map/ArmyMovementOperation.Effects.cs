using System;
using System.Collections;
using System.Collections.Generic;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    public sealed class ArmyMovementArrival
    {
        public HexCoord Hex { get; }
        public BattleStartResult Contact { get; }
        public bool HexEvent { get; }
        internal ArmyMovementArrival(HexCoord hex, BattleStartResult contact, bool hexEvent)
        { Hex = hex; Contact = contact; HexEvent = hexEvent; }
    }

    public sealed partial class ArmyMovementOperation
    {
        private Func<HexCoord, bool> entryEffects;
        private Func<HexCoord, HexCoord, ArmyController.StepResolutionOutcome, IEnumerator> defaultStepResolution;

        // Existing actions and the encounter/event entrypoints retain their ownership. The
        // operation sequences them; animation and selection only project the resulting facts.
        public static ArmyMovementOperation ForOrder(ArmyData army, HexMap map, IEnumerable<HexCoord> path,
            PlayerRoot root, HexCoord destination, HexSelectionController encounters,
            Action<ArmyMovementArrival> arrivalPresentation = null, Action<HexCoord> eventEncountered = null,
            bool allowUndefendedBuildingTakeover = true, bool allowAiEventExplore = true)
        {
            var effects = new EntryEffects(army, destination, encounters, arrivalPresentation, eventEncountered,
                allowUndefendedBuildingTakeover, allowAiEventExplore);
            ArmyMovementOperation operation = null;
            operation = ForMap(army, map, path, () => ArmyActions.TryPayActivation(army, root), () => effects.Arrive(operation));
            operation.entryEffects = hex => effects.Enter(operation, hex);
            operation.defaultStepResolution = (from, to, outcome) => ResolveHeadlessStep(from, to, outcome, army);
            return operation;
        }
        internal static IEnumerator ResolveHeadlessStep(HexCoord from, HexCoord to,
            ArmyController.StepResolutionOutcome outcome, ArmyData army)
        {
            AviationCombatHeadlessAdapter.Run(new AviationCombatOperation(AviationCombatPresenter.HeadlessRng)
                .ResolveStep(army, to, outcome));
            yield break;
        }
        private sealed class EntryEffects
        {
            private readonly ArmyData army;
            private readonly HexCoord destination;
            private readonly HexSelectionController encounters;
            private readonly Action<ArmyMovementArrival> presentation;
            private readonly Action<HexCoord> eventEncountered;
            private readonly bool takeover, explore;
            private readonly HashSet<(UnitData, PlayerSetupData, HexCoord)> stealthSeen = new HashSet<(UnitData, PlayerSetupData, HexCoord)>();
            private HexCoord? stealthChecked;
            private bool eventClaimed;
            internal EntryEffects(ArmyData army, HexCoord destination, HexSelectionController encounters,
                Action<ArmyMovementArrival> presentation, Action<HexCoord> eventEncountered, bool takeover, bool explore)
            {
                this.army = army; this.destination = destination; this.encounters = encounters;
                this.presentation = presentation; this.eventEncountered = eventEncountered;
                this.takeover = takeover; this.explore = explore;
            }
            internal bool Enter(ArmyMovementOperation operation, HexCoord hex)
            {
                if (!AviationRules.IsAirArmy(army) && HasUnclaimedCleanEventHex(army, hex))
                {
                    eventClaimed = true; eventEncountered?.Invoke(hex); return true;
                }
                bool newlySeen = !operation.LastStepWasVisible;
                // Registry committed visibility before the animation. Stealth follows AA and
                // strike resolution, exactly once per actual entry, using the surviving roster.
                StealthSystem.RunChecksForArrival(army, hex, stealthSeen);
                stealthChecked = hex;
                if (AviationRules.IsAirArmy(army)) return false;
                if (BattleInitiator.CanInitiateContact(army) && BattleInitiator.FindDefenderOnArrival(hex, army) != null)
                    return true;
                var building = BuildingRegistry.FindAt(hex);
                return newlySeen && building != null && building.Owner != null && building.Owner != army.Owner;
            }
            internal void Arrive(ArmyMovementOperation operation)
            {
                if (army.Members.Count == 0 || !ArmyRegistry.IsRegistered(army) || !army.Hex.Equals(operation.CurrentHex)) return;
                HexCoord hex = operation.CurrentHex;
                if (!stealthChecked.HasValue || !stealthChecked.Value.Equals(hex))
                    StealthSystem.RunChecksForArrival(army, hex, stealthSeen);
                if (!AviationRules.IsAirArmy(army) && takeover)
                    BuildingRegistry.CaptureOrDestroyIfUndefended(hex, army.Owner, encounters, army);
                if (eventClaimed)
                    encounters?.BeginCleanHexEvent(army, hex, destination, army.Controller, takeover, explore);
                BattleStartResult contact = eventClaimed || AviationRules.IsAirArmy(army)
                    ? BattleStartResult.NoContact : encounters != null ? encounters.TryBeginBattleAt(hex, army) : BattleStartResult.NoContact;
                presentation?.Invoke(new ArmyMovementArrival(hex, contact, eventClaimed));
            }
        }
        private static bool HasUnclaimedCleanEventHex(ArmyData mover, HexCoord hex)
        {
            var entry = HexEventRegistry.FindAt(hex);
            if (entry == null || entry.Consumed || entry.Definition?.CanVisit(mover?.Owner) == false) return false;
            foreach (var other in ArmyRegistry.At(hex))
                if (other.Owner != mover.Owner && BattleInitiator.IsEngageable(other, mover.Owner)) return false;
            return true;
        }
    }
}
