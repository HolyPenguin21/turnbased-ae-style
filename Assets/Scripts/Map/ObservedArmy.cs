using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // Immutable payload. Authored art/card definitions and player identities are references;
    // all changing roster, stats, movement, abilities and costs are owned historical values.
    // Compatibility consumers get a new detached projection each time, never this storage.
    public sealed class ObservedArmy
    {
        public string Name { get; }
        public PlayerSetupData Owner { get; }
        public HexCoord Hex { get; }
        public bool IsGarrison { get; }
        public bool IsPrison { get; }
        public bool IsAirfield { get; }
        public bool IsAirArmy { get; }
        public bool Activated { get; }
        public int? DefenseBonus { get; }
        public int? ConstructionDefense { get; }
        public int CurrentMovement { get; }
        public int MaxMovement { get; }
        public IReadOnlyList<ObservedUnit> Members { get; }

        private ObservedArmy(ArmyData source, HexCoord hex, PlayerSetupData viewer, bool historical)
        {
            Name = source.Name; Owner = source.Owner; Hex = hex;
            IsGarrison = source.IsGarrison; IsPrison = source.IsPrison; IsAirfield = source.IsAirfield;
            IsAirArmy = AviationRules.IsAirArmy(source); Activated = source.HasActivatedThisTurn;
            var members = source.Members.Where(u => historical || !StealthSystem.IsHiddenFrom(u, viewer))
                .Select(u => new ObservedUnit(u)).ToArray();
            Members = Array.AsReadOnly(members);
            CurrentMovement = historical && source.VisualSnapshotCurrentMovement.HasValue ? source.VisualSnapshotCurrentMovement.Value
                : members.Length == 0 ? 0 : members.Min(u => u.EffectiveMoveCurrent);
            MaxMovement = historical && source.VisualSnapshotMaxMovement.HasValue ? source.VisualSnapshotMaxMovement.Value
                : members.Length == 0 ? 0 : members.Min(u => u.MoveMax);
            if (historical)
            {
                DefenseBonus = source.VisualSnapshotDefenseBonus;
                ConstructionDefense = source.VisualSnapshotConstructionDefense;
            }
            else
            {
                var building = BuildingRegistry.FindAt(hex);
                ConstructionDefense = !IsAirArmy && building != null && building.IsBase ? building.Defense : 0;
                DefenseBonus = (IsAirArmy ? 0 : MapObservationSystem.TerrainDefenseAt(hex)) + ConstructionDefense.Value;
            }
        }
        private ObservedArmy(ObservedArmy observed, HexCoord hex)
        {
            Name = observed.Name; Owner = observed.Owner; Hex = hex;
            IsGarrison = observed.IsGarrison; IsPrison = observed.IsPrison; IsAirfield = observed.IsAirfield;
            IsAirArmy = observed.IsAirArmy; Activated = observed.Activated;
            DefenseBonus = observed.DefenseBonus; ConstructionDefense = observed.ConstructionDefense;
            CurrentMovement = observed.CurrentMovement; MaxMovement = observed.MaxMovement; Members = observed.Members;
        }
        internal ObservedArmy AtObservedEndpoint(HexCoord hex) => new ObservedArmy(this, hex);
        internal static ObservedArmy Capture(ArmyData source, HexCoord hex, PlayerSetupData viewer)
            => new ObservedArmy(source, hex, viewer, false);
        internal static ObservedArmy FromDisplay(ArmyData source) => new ObservedArmy(source, source.Hex, null, true);
        public ArmyData CreateDisplayArmy()
        {
            var display = ArmyData.CreateVisualSnapshot();
            display.Name = Name; display.Hex = Hex; display.Owner = Owner;
            display.IsGarrison = IsGarrison; display.IsPrison = IsPrison; display.IsAirfield = IsAirfield; display.IsAirArmy = IsAirArmy;
            display.VisualSnapshotDefenseBonus = DefenseBonus; display.VisualSnapshotConstructionDefense = ConstructionDefense;
            display.VisualSnapshotCurrentMovement = CurrentMovement; display.VisualSnapshotMaxMovement = MaxMovement;
            foreach (var unit in Members) display.Members.Add(unit.CreateDisplayUnit());
            display.SetVisualActivation(Activated);
            return display;
        }
    }

    public sealed class ObservedUnit
    {
        private readonly UnitData value;
        public int RuntimeId => value.RuntimeId;
        public string Name => value.Name;
        public PlayerSetupData Owner => value.Owner;
        public int MoveMax => value.MoveMax;
        public int MoveCurrent => value.MoveCurrent;
        public int EffectiveMoveCurrent { get; }
        public int Attack => value.Attack;
        public int Defense => value.Defense;
        public int HitPointsCurrent => value.HitPointsCurrent;
        public int HitPointsMax => value.HitPointsMax;
        public UnityEngine.Sprite Art => value.Art;
        public UnityEngine.Sprite DetailArt => value.DetailArt;
        public Game.Cards.CardDefinition Equipment => value.Equipment;
        public Game.Cards.CardDefinition Mutator => value.Mutator;
        public IReadOnlyCollection<string> Abilities { get; }
        public IReadOnlyCollection<Game.Cards.UnitTypeTag> TypeTags { get; }
        internal ObservedUnit(UnitData source)
        {
            value = HumanVisualMemory.CopyObservedUnit(source);
            EffectiveMoveCurrent = AviationRules.EffectiveMoveCurrent(source);
            Abilities = Array.AsReadOnly(value.Abilities.ToArray());
            TypeTags = Array.AsReadOnly(value.TypeTags.ToArray());
        }
        internal UnitData CreateDisplayUnit() => HumanVisualMemory.CopyObservedUnit(value);
    }
}
