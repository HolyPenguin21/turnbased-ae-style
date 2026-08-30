using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ARMY REORG PROFILE + LOCAL FORCE GROUP  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  IMMUTABLE, CONTEXTUAL classification of one local container for one Housekeeping pass. It
    //  COMPOSES existing canonical signals (AiArmyRoles physical role, AiPower strength,
    //  ActorCommitments ownership, ArmyData capacity) — it is NOT a new persistent role and never
    //  written back onto ArmyData / AiArmyRoles. Rebuilt from live gameplay state every time
    //  Housekeeping runs; nothing here survives a turn.
    //
    //  The planner (ArmyReorganizationPlanner) is a PURE function over these projections — it
    //  never touches a Unity type. ArmyReorgAnalyzer builds the projections from the live world
    //  and keeps the key->UnitData / ArmyId->ArmyData back-maps the executor needs.
    // ===========================================================================================

    // Physical classification — read from the container's CURRENT composition + ownership, never a
    // stored flag. WeakFieldArmy / SingletonFieldArmy from the design doc are DERIVED conditions
    // (see ArmyReorgProfile.IsSingleton / .IsViable), not roles, so they are not in this enum.
    public enum ReorgPhysicalRole
    {
        Garrison,
        ProtectedMissionArmy,     // ActorCommitments.IsArmyClaimed — composition is off-limits
        SoloRecce,                // AiArmyRoles.IsSoloRecce — canonical standalone scout, exempt
        Aviation,                 // AviationRules.IsAirfield / IsAirArmy — excluded from ground reorg
        NormalFieldArmy,          // ordinary ground field army with at least one member
        EmptyReusableArmy,        // paid, reusable empty ground shell — KEEP, never delete
        SpecialExcludedContainer, // Prison / anything gameplay forbids generic transfer for
    }

    // One combatant as the planner sees it. Key is a deterministic stable identifier assigned by
    // ArmyReorgAnalyzer (identical input state -> identical keys) so the plan and its tie-breaks
    // never depend on collection iteration order. Power is AiPower.UnitPower (base ranking scalar).
    public sealed class ReorgUnit
    {
        public int Key;
        public bool IsHero;
        public int CommandRating;   // sets a hero-led roster's capacity (ArmyData.ComputeCapacity)
        public float Power;
        public int Range;
        public bool HasRecce;
        public bool IsAviation;
        // Unit-level commitment inside an otherwise-unprotected army. Always false today
        // (ActorCommitments is army-grained); the planner already excludes committed units from
        // donor pools so a future granular commitment model needs no planner change.
        public bool IsCommitted;
    }

    // One friendly container on one hex. Units is the AUTHORITATIVE starting roster; the planner
    // works on private copies in its virtual state and never mutates this list.
    public sealed class ReorgContainer
    {
        public int ArmyId;
        public ReorgPhysicalRole Role;
        public bool IsGarrison;
        public List<ReorgUnit> Units = new List<ReorgUnit>();

        // Capabilities DERIVED from canonical ownership — not an independent mission policy.
        //   CanDonate           — a generic transfer may take a member OUT of this container
        //   CanReceive          — a generic transfer may put a member INTO this container
        //   CanChangeComposition — either of the above is allowed at all
        public bool CanDonate;
        public bool CanReceive;
        public bool CanChangeComposition;

        // This container is a non-exempt singleton candidate the planner should try to fix.
        public bool SingletonExempt;

        // Canonical secure floor for a garrison (secure*MinNonHeroUnits); 0 for a field army.
        // A donation from the garrison is legal only if the post-plan garrison still holds this
        // many non-hero members (and never loses its literal last member).
        public int GarrisonNonHeroFloor;

        public bool IsMutableGround =>
            Role == ReorgPhysicalRole.NormalFieldArmy || Role == ReorgPhysicalRole.EmptyReusableArmy;

        public int MemberCount => Units.Count;
    }

    // Analysis SCOPE for one exact hex — never a virtual super-container, never persisted. The
    // planner only ever moves members between containers listed here.
    public sealed class LocalForceGroup
    {
        public int Q;
        public int R;
        // Deterministic order: ascending ArmyId. Every downstream loop iterates this list as-is.
        public List<ReorgContainer> Containers = new List<ReorgContainer>();

        public string HexKey => Q + "," + R;

        public ReorgContainer Garrison => Containers.FirstOrDefault(c => c.IsGarrison);

        // At least MinContainersForGroup containers AND at least one mutable ground field army
        // that is either a non-exempt singleton or a non-viable occupied formation — otherwise
        // there is nothing for Housekeeping to do here.
        public bool WorthPlanning()
        {
            if (Containers.Count < AiConfigV2.housekeepingMinContainersForGroup)
                return false;
            foreach (ReorgContainer c in Containers)
            {
                if (c.Role != ReorgPhysicalRole.NormalFieldArmy || !c.CanChangeComposition)
                    continue;
                if (ReorgViability.IsNonExemptSingleton(c) || !ReorgViability.IsViable(c.Units))
                    return true;
            }
            return false;
        }
    }

    // Shared viability / structural predicates — the ONE place "weak" / "singleton" is defined,
    // built on existing combat primitives (per-unit AiPower.UnitPower). Conservative on purpose:
    // it is a structural floor, not a battle simulator (design doc §10).
    public static class ReorgViability
    {
        public static float StackPower(IEnumerable<ReorgUnit> units) =>
            units == null ? 0f : units.Sum(u => u.Power);

        // A viable occupied ground formation: at least two members AND enough combined ranking
        // power. A singleton or a lone hero is never viable.
        public static bool IsViable(IReadOnlyList<ReorgUnit> units)
        {
            if (units == null || units.Count < 2)
                return false;
            return StackPower(units) >= AiConfigV2.housekeepingViabilityPowerFloor;
        }

        // Exactly one non-hero member — an undesirable end-state if a legal same-hex fix exists.
        public static bool IsSingletonShape(IReadOnlyList<ReorgUnit> units) =>
            units != null && units.Count == 1 && !units[0].IsHero;

        public static bool IsNonExemptSingleton(ReorgContainer c) =>
            c != null && !c.SingletonExempt && c.Role == ReorgPhysicalRole.NormalFieldArmy
            && IsSingletonShape(c.Units);

        // Capacity rule as a pure function of a candidate roster — mirrors
        // Game.Map.ArmyData.ComputeCapacity (FIRST hero's CommandRating, else the base value).
        public static int Capacity(IReadOnlyList<ReorgUnit> units, bool isGarrison)
        {
            if (units != null)
                foreach (ReorgUnit u in units)
                    if (u.IsHero)
                        return u.CommandRating;
            return isGarrison ? AiConfigV2.garrisonBaseCapacityNoHero : AiConfigV2.armyBaseCapacityNoHero;
        }

        // Mirrors ArmyData.CanLeaveWithoutOvercrowding — after `leaving` is removed, does the
        // remaining roster still fit its (possibly now-lower) capacity?
        public static bool CanLeaveWithoutOvercrowding(IReadOnlyList<ReorgUnit> units, ReorgUnit leaving, bool isGarrison)
        {
            var remaining = units.Where(u => u != leaving).ToList();
            return Capacity(remaining, isGarrison) >= remaining.Count;
        }
    }
}
