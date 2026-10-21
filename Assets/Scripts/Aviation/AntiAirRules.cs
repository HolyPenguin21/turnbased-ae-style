using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // One AA unit's reaction opportunity against one specific air army — found either by
    // starting from the air army (CollectEntryReactions: "who can shoot AT me") or by starting
    // from a moving ground AA unit (CollectGroundOpportunities: "what can I shoot") — same
    // underlying (aaUnit, airArmy) pairing either way, just discovered from opposite ends.
    // An AA Facility reacts through its stand-in gunner: AaArmy is null, AaBuilding/AaSlot name it.
    public readonly struct AaReaction
    {
        public readonly UnitData AaUnit;
        public readonly ArmyData AaArmy;
        public readonly BuildingData AaBuilding;
        public readonly int AaSlot;
        public readonly ArmyData AirArmy;
        public readonly int Distance;

        public AaReaction(UnitData aaUnit, ArmyData aaArmy, ArmyData airArmy, int distance)
        {
            AaUnit = aaUnit;
            AaArmy = aaArmy;
            AaBuilding = null;
            AaSlot = -1;
            AirArmy = airArmy;
            Distance = distance;
        }

        public AaReaction(UnitData gunner, BuildingData building, int slot, ArmyData airArmy, int distance)
        {
            AaUnit = gunner;
            AaArmy = null;
            AaBuilding = building;
            AaSlot = slot;
            AirArmy = airArmy;
            Distance = distance;
        }

        public PlayerSetupData AaOwner => AaArmy != null ? AaArmy.Owner : AaBuilding?.Owner;
        public HexCoord AaHex => AaArmy != null ? AaArmy.Hex : AaBuilding.Hex;
    }

    // The suffix parser is intentionally tolerant: content authors can use AA for radius one
    // while the card catalogue later grows AA2/AA3 without a code or editor overhaul.
    public static class AntiAirRules
    {
        public static bool TryGetRadius(UnitData unit, out int radius)
        {
            radius = 0;
            return unit != null && TryGetRadius(unit.Abilities, unit.AntiAirRadius, out radius);
        }

        // The same rule for a PROJECTED ability list (an attachment not yet attached): plain AA uses the
        // unit own radius stat, AAn the suffix.
        public static bool TryGetRadius(IEnumerable<string> abilities, int unitRadius, out int radius)
        {
            radius = 0;
            if (abilities == null)
                return false;
            foreach (string ability in abilities)
            {
                if (ability == UnitAbilities.AntiAir)
                {
                    radius = unitRadius;
                    return true;
                }
                if (!string.IsNullOrEmpty(ability) && ability.StartsWith(UnitAbilities.AntiAir)
                    && int.TryParse(ability.Substring(UnitAbilities.AntiAir.Length), out int parsed) && parsed > 0)
                {
                    radius = parsed;
                    return true;
                }
            }
            return false;
        }

        // Every enemy AA unit anywhere on the map currently within its own radius of `hex` and
        // still free (see AntiAirState) to react against `airArmy` specifically — called once an
        // air army finishes entering a hex. Per the project owner's own 2026-08-26 correction,
        // an AA unit only reacts against a hex ITS OWN owner can currently see — a radius alone
        // is not a reaction, only a hidden AA unit's own visibility TO THE AIR ARMY's owner stays
        // irrelevant (the enemy doesn't need to have spotted the gun for the gun to fire back).
        // Radius is the cheaper check (Distance, no registry/vision lookup) so it runs first —
        // the AA owner's own vision of `enteredHex` is only checked for whoever's still in range.
        public static List<AaReaction> CollectEntryReactions(ArmyData airArmy, HexCoord enteredHex)
        {
            var reactions = new List<AaReaction>();
            if (airArmy == null)
                return reactions;

            foreach (HexCoord aaHex in ArmyRegistry.AllOccupiedHexes())
            {
                int distance = HexGridMath.Distance(aaHex, enteredHex);
                foreach (ArmyData army in ArmyRegistry.AllAt(aaHex))
                {
                    if (army.Owner == null || army.Owner == airArmy.Owner
                        || army.IsCarrierContainer || AviationRules.IsAirfield(army))
                        continue;
                    // A wing whose every aircraft is hidden from this AA owner is not a target.
                    if (!StealthSystem.HasAnyTargetableMember(airArmy, army.Owner))
                        continue;
                    foreach (UnitData member in army.Members)
                    {
                        if (!TryGetRadius(member, out int radius) || distance > radius)
                            continue;
                        if (!VisionSystem.IsVisible(army.Owner, enteredHex))
                            continue;
                        // A hidden AA unit does not react until it has been revealed (§5/§7):
                        // still in stealth at all, or hidden from the very air army it would
                        // be shooting at.
                        if (member.IsHidden || Game.Map.StealthSystem.IsHiddenFrom(member, airArmy.Owner))
                            continue;
                        if (!AntiAirState.CanReact(member, airArmy.Id))
                            continue;
                        reactions.Add(new AaReaction(member, army, airArmy, distance));
                    }
                }
            }
            // AA Facilities: same radius / owner-vision / per-turn rules; a building is never hidden.
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner == null || building.Owner == airArmy.Owner
                    || !StealthSystem.HasAnyTargetableMember(airArmy, building.Owner))
                    continue;
                int distance = HexGridMath.Distance(building.Hex, enteredHex);
                for (int slot = 0; slot < building.FacilitySlots.Length; slot++)
                {
                    if (!TryGetFacilityRadius(building, slot, out int radius) || distance > radius)
                        continue;
                    if (!VisionSystem.IsVisible(building.Owner, enteredHex))
                        continue;
                    UnitData gunner = FacilityGunner(building, building.FacilitySlots[slot]);
                    if (AntiAirState.CanReact(gunner, airArmy.Id))
                        reactions.Add(new AaReaction(gunner, building, slot, airArmy, distance));
                }
            }
            SortReactions(reactions);
            return reactions;
        }

        // The mirror query: called once a GROUND army carrying at least one AA-tagged member
        // finishes entering a hex — every enemy air army anywhere within that member's own
        // radius, so a passing air army can be shot at from the ground even though it never
        // itself entered the AA unit's hex (see HexSelectionController.Movement.cs's own
        // ground-mover call site).
        public static List<AaReaction> CollectGroundOpportunities(ArmyData groundArmy, HexCoord hex)
        {
            var reactions = new List<AaReaction>();
            if (groundArmy == null)
                return reactions;

            foreach (UnitData member in groundArmy.Members)
            {
                if (!TryGetRadius(member, out int radius))
                    continue;
                // A hidden unit takes no offensive action, an opportunity AA shot included
                // (§5) — it must voluntarily leave stealth first.
                if (member.IsHidden)
                    continue;
                foreach ((ArmyData airArmy, int distance) in AirArmiesInRange(member, groundArmy.Owner, hex, radius))
                    reactions.Add(new AaReaction(member, groundArmy, airArmy, distance));
            }
            SortReactions(reactions);
            return reactions;
        }

        // Start of `owner`'s turn (after AntiAirState.ResetForOwner): every enemy air army already
        // inside a radius. A wing that stays over or next to an AA unit/Facility and strikes
        // without moving never triggers an entry reaction, so the AA gets its shot here. The same
        // per-turn state applies: a shot fired now is that AA's one shot until its next turn.
        public static List<AaReaction> CollectTurnStartOpportunities(PlayerSetupData owner)
        {
            var reactions = new List<AaReaction>();
            if (owner == null)
                return reactions;

            foreach (ArmyData army in ArmyRegistry.AllForOwner(owner).ToList())
            {
                // Stored aircraft and carrier decks take no action, as in CollectEntryReactions.
                if (army.IsCarrierContainer || AviationRules.IsAirfield(army) || army.IsPrison)
                    continue;
                reactions.AddRange(CollectGroundOpportunities(army, army.Hex));
            }
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != owner)
                    continue;
                for (int slot = 0; slot < building.FacilitySlots.Length; slot++)
                {
                    if (!TryGetFacilityRadius(building, slot, out int radius))
                        continue;
                    UnitData gunner = FacilityGunner(building, building.FacilitySlots[slot]);
                    foreach ((ArmyData airArmy, int distance) in AirArmiesInRange(gunner, owner, building.Hex, radius))
                        reactions.Add(new AaReaction(gunner, building, slot, airArmy, distance));
                }
            }
            SortReactions(reactions);
            return reactions;
        }

        // Enemy air armies within `radius` of `hex` that `owner` can see and `aaUnit` may still
        // react to. Same visibility rule as CollectEntryReactions: radius alone doesn't earn a
        // shot, the AA owner must actually see the air army's hex and at least one of its
        // aircraft (stealth).
        private static IEnumerable<(ArmyData airArmy, int distance)> AirArmiesInRange(UnitData aaUnit,
            PlayerSetupData owner, HexCoord hex, int radius)
        {
            foreach (HexCoord airHex in ArmyRegistry.AllOccupiedHexes().ToList())
            {
                int distance = HexGridMath.Distance(hex, airHex);
                if (distance > radius || !VisionSystem.IsVisible(owner, airHex))
                    continue;
                foreach (ArmyData airArmy in ArmyRegistry.AllAt(airHex).ToList())
                {
                    if (airArmy.Owner == owner || airArmy.IsCarrierContainer
                        || !AviationRules.IsAirArmy(airArmy)
                        || !StealthSystem.HasAnyTargetableMember(airArmy, owner))
                        continue;
                    if (AntiAirState.CanReact(aaUnit, airArmy.Id))
                        yield return (airArmy, distance);
                }
            }
        }

        // A live Facility slot carrying AA (plain AA = the card radius floored at 1, AAn = n). The
        // Facility belongs to the building, so a captured Base hands its AA to the new owner.
        private static bool TryGetFacilityRadius(BuildingData building, int slot, out int radius)
        {
            radius = 0;
            FacilityData facility = building.FacilitySlots[slot];
            return facility != null && facility.StructurePointsCurrent > 0
                && TryGetRadius(facility.Abilities, facility.AntiAirRadius, out radius);
        }

        // The Facility's stable stand-in shooter, refreshed from the live Facility and building on
        // every use (owner after a capture, abilities/stats after an upgrade).
        public static UnitData FacilityGunner(BuildingData building, FacilityData facility)
        {
            UnitData gunner = facility.AntiAirGunner ??= new UnitData();
            gunner.Name = facility.Name;
            gunner.Owner = building.Owner;
            gunner.Art = facility.Art;
            gunner.DetailArt = facility.DetailArt;
            gunner.Attack = facility.Attack;
            gunner.Defense = facility.Defense;
            gunner.HitPointsMax = Mathf.Max(1, facility.StructurePointsMax);
            gunner.HitPointsCurrent = Mathf.Max(1, facility.StructurePointsCurrent);
            gunner.AntiAirRadius = facility.AntiAirRadius;
            gunner.Abilities.Clear();
            gunner.Abilities.UnionWith(facility.Abilities);
            return gunner;
        }

        // AntiAirState resets by UnitData.Owner; a captured Base's gunner must carry its new owner
        // before that owner's turn-start reset.
        public static void SyncFacilityGunnerOwners()
        {
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
                foreach (FacilityData facility in building.FacilitySlots)
                    if (facility?.AntiAirGunner != null)
                        facility.AntiAirGunner.Owner = building.Owner;
        }

        // Stable order per the design: hex distance, then army Id, then member slot index;
        // Facilities after armies at the same distance, by building hex then slot.
        private static void SortReactions(List<AaReaction> reactions)
        {
            reactions.Sort((a, b) =>
            {
                if (a.Distance != b.Distance)
                    return a.Distance.CompareTo(b.Distance);
                if ((a.AaArmy == null) != (b.AaArmy == null))
                    return a.AaArmy == null ? 1 : -1;
                int order;
                if (a.AaArmy == null)
                {
                    order = a.AaBuilding.Hex.Q.CompareTo(b.AaBuilding.Hex.Q);
                    if (order == 0) order = a.AaBuilding.Hex.R.CompareTo(b.AaBuilding.Hex.R);
                    if (order == 0) order = a.AaSlot.CompareTo(b.AaSlot);
                }
                else
                {
                    order = a.AaArmy.Id.CompareTo(b.AaArmy.Id);
                    if (order == 0)
                        order = a.AaArmy.Members.IndexOf(a.AaUnit).CompareTo(b.AaArmy.Members.IndexOf(b.AaUnit));
                }
                return order != 0 ? order : a.AirArmy.Id.CompareTo(b.AirArmy.Id);
            });
        }
    }
}
