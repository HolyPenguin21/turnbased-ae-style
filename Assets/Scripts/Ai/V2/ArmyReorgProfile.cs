using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ARMY REORG PROFILE + LOCAL FORCE GROUP  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  IMMUTABLE, CONTEXTUAL classification of one local container for one Housekeeping pass. It
    //  COMPOSES existing canonical signals (AiArmyRoles physical role, AiPower strength/composition,
    //  ActorCommitments ownership, ArmyData capacity) — it is NOT a new persistent role and never
    //  written back onto ArmyData / AiArmyRoles. Rebuilt from live gameplay state every time
    //  Housekeeping runs; nothing here survives a turn.
    // ===========================================================================================

    public enum ReorgPhysicalRole
    {
        Garrison,
        ProtectedMissionArmy,
        SoloRecce,
        Aviation,
        NormalFieldArmy,
        EmptyReusableArmy,
        SpecialExcludedContainer,
    }

    public sealed class ReorgUnit
    {
        public int Key;
        public bool IsHero;
        public int CommandRating;
        // §8 — canonical hero operational-role signals, 0 / Flexible for non-heroes.
        public float HeroCombatLeadership;
        public HeroOperationalRole HeroRole;
        public float Power;
        public int Range;
        public IReadOnlyList<UnitTypeTag> TypeTags = Array.Empty<UnitTypeTag>();
        public int ActivationApCost;
        public bool HasRecce;
        public bool IsAviation;
        public bool IsCommitted;
    }

    public sealed class ReorgContainer
    {
        public int ArmyId;
        public ReorgPhysicalRole Role;
        public bool IsGarrison;
        public bool HasActivatedThisTurn;
        public List<ReorgUnit> Units = new List<ReorgUnit>();

        public bool CanDonate;
        public bool CanReceive;
        public bool CanChangeComposition;
        public bool SingletonExempt;
        public int GarrisonNonHeroFloor;

        public bool IsMutableGround =>
            Role == ReorgPhysicalRole.NormalFieldArmy || Role == ReorgPhysicalRole.EmptyReusableArmy;

        public int MemberCount => Units.Count;
    }

    public sealed class LocalForceGroup
    {
        public int Q;
        public int R;
        public List<ReorgContainer> Containers = new List<ReorgContainer>();

        public string HexKey => Q + "," + R;
        public ReorgContainer Garrison => Containers.FirstOrDefault(c => c.IsGarrison);

        public bool WorthPlanning()
        {
            if (Containers.Count < AiConfigV2.housekeepingMinContainersForGroup)
                return false;

            // §7 — a container (field OR garrison) whose commander is not its highest-capacity
            // hero is worth a zero-AP planning pass on its own.
            foreach (ReorgContainer c in Containers)
                if (ReorgViability.HasCommanderUpgrade(c))
                    return true;

            int viableMutableFields = 0;
            foreach (ReorgContainer c in Containers)
            {
                if (!c.IsMutableGround || !c.CanChangeComposition)
                    continue;

                if (c.Units.Count > 0 &&
                    (ReorgViability.IsNonExemptSingleton(c) || !ReorgViability.IsViable(c.Units)))
                    return true;

                if (c.Units.Count > 0 && ReorgViability.IsViable(c.Units))
                    viableMutableFields++;
            }

            // Healthy formations can still have a strictly better local composition. The planner
            // will simply return no-op when no zero-AP redistribution/swap improves the tuple.
            return viableMutableFields >= 2;
        }
    }

    public static class ReorgViability
    {
        private static List<AiPower.PowerUnit> ToPowerUnits(IEnumerable<ReorgUnit> units)
        {
            if (units == null)
                return new List<AiPower.PowerUnit>();
            return units.Select(u => new AiPower.PowerUnit(u.Power, u.TypeTags, u.Range, u.IsHero)).ToList();
        }

        public static float StackPower(IEnumerable<ReorgUnit> units) =>
            units == null ? 0f : units.Sum(u => u.Power);

        // Shared V2 strength/composition model — no second Housekeeping-only tactical scalar.
        public static float EffectivePower(IReadOnlyList<ReorgUnit> units) =>
            units == null || units.Count == 0 ? 0f : AiPower.EffectiveArmyPower(ToPowerUnits(units));

        public static float CompositionQuality(IReadOnlyList<ReorgUnit> units) =>
            units == null || units.Count == 0 ? 0f : AiPower.CompositionQuality(ToPowerUnits(units));

        public static bool IsViable(IReadOnlyList<ReorgUnit> units)
        {
            if (units == null || units.Count < 2)
                return false;
            return EffectivePower(units) >= AiConfigV2.housekeepingViabilityPowerFloor;
        }

        public static bool IsSingletonShape(IReadOnlyList<ReorgUnit> units) =>
            units != null && units.Count == 1 && !units[0].IsHero;

        public static bool IsNonExemptSingleton(ReorgContainer c) =>
            c != null && !c.SingletonExempt && c.IsMutableGround && IsSingletonShape(c.Units);

        // §7 — the container holds >= 2 heroes and its current commander (first hero in roster
        // order, i.e. the one ComputeCapacity reads) does not have the highest CommandRating
        // available, so a zero-AP reorder would raise its legal capacity.
        public static bool HasCommanderUpgrade(ReorgContainer c)
        {
            if (c == null || !c.CanChangeComposition)
                return false;
            int first = -1;
            int best = 0;
            int heroes = 0;
            foreach (ReorgUnit u in c.Units)
            {
                if (u == null || !u.IsHero)
                    continue;
                heroes++;
                if (first < 0)
                    first = u.CommandRating;
                if (u.CommandRating > best)
                    best = u.CommandRating;
            }
            return heroes >= 2 && first >= 0 && best > first;
        }

        // Mirrors ArmyData.ComputeCapacity exactly: preserve roster order, first hero wins; for a
        // no-hero roster ask the canonical gameplay function for its default instead of duplicating
        // BaseCapacity/GarrisonBaseCapacity in V2 config.
        public static int Capacity(IReadOnlyList<ReorgUnit> units, bool isGarrison)
        {
            if (units != null)
                foreach (ReorgUnit u in units)
                    if (u.IsHero)
                        return u.CommandRating;
            return ArmyData.ComputeCapacity(Array.Empty<UnitData>(), isGarrison);
        }

        // Mirrors ArmyData.AddMemberSorted so virtual hero order — and therefore first-hero
        // CommandRating semantics — stays identical to the live gameplay roster after transfers.
        public static void AddMemberSorted(List<ReorgUnit> roster, ReorgUnit unit)
        {
            int index = unit.IsHero ? roster.Count(u => u.IsHero) : roster.Count;
            roster.Insert(index, unit);
        }

        public static bool CanLeaveWithoutOvercrowding(IReadOnlyList<ReorgUnit> units, ReorgUnit leaving, bool isGarrison)
        {
            var remaining = new List<ReorgUnit>(units ?? Array.Empty<ReorgUnit>());
            remaining.Remove(leaving);
            return Capacity(remaining, isGarrison) >= remaining.Count;
        }
    }
}
