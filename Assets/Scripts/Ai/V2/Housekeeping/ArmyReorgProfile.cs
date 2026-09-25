using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
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
        SoloCollector,
        Aviation,
        NormalFieldArmy,
        EmptyReusableArmy,
        SpecialExcludedContainer,
    }

    public sealed class ReorgUnit
    {
        public int Key;
        public bool IsHero;
        // The two canonical body rules, frozen from the live unit at Analyzer time:
        // UnitData.IsGroundCombatant (fights a ground battle) and AiArmyRoles.IsGroundBattleBody
        // (a transferable ground fighting body — combatant and not an aircraft). Body counts and
        // body pools read these; IsHero stays for commander / capacity identity only.
        public bool IsGroundCombatant;
        public bool IsGroundBattleBody;
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
        // Turn-local contextual duty: this exact hero is the minimum operator set needed by a
        // Research/Production facility on the current hex. Not a persistent strategic role.
        public bool IsDevelopmentOperator;
        // Exact immutable combat profile consumed by WorthIt. Heroes keep a profile for
        // diagnostics but are excluded from Ground Combat roster estimates.
        public WorthIt.DefenderProfile CombatProfile;
        // What this hero gives a formation it commands (initiative bonus, battle Fate).
        public WorthIt.SideCommander AsCommander;
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

    public sealed class ReorgThreatBenchmark
    {
        public int ArmyId;
        public bool HiddenFromUs;
        public int EtaToGroup;
        // Diagnostic only — how far the enemy is from any friendly base. Housekeeping is same-hex
        // and task-neutral: it must never let another base's threat pressure a group it cannot
        // move, so decision weighting uses EtaToGroup exclusively. Strategic reaction to base
        // threats belongs to Defence (Analysis/Threat → Defence Demand → Mission → Provisioning).
        public int EtaToNearestBase;
        public IReadOnlyList<WorthIt.DefenderProfile> Members = Array.Empty<WorthIt.DefenderProfile>();
        // The enemy army's commander as the snapshot knows it.
        public WorthIt.SideCommander Commander;
        // §Task4 — observer-specific projection: the friendly non-hero ReorgUnit.Key set this
        // specific enemy can actually target as far as WE know — every unit not in our own stealth
        // (UnitData.IsHidden; enemy detection is unknown to the owner, so never IsHiddenFrom),
        // computed once at Analyzer time off the live roster. A virtual transfer/swap moves a unit's Key into a
        // different container but never changes this fact, so contact selection during planning
        // stays honest about which enemy can see which unit. A container with none of its units in
        // this set is not a real contact candidate for this enemy.
        public HashSet<int> TargetableUnitKeys = new HashSet<int>();
    }

    public sealed class LocalForceGroup
    {
        public int Q;
        public int R;
        public float HexDefenseBonus;
        public List<ReorgContainer> Containers = new List<ReorgContainer>();
        public List<ReorgThreatBenchmark> ThreatBenchmarks = new List<ReorgThreatBenchmark>();

        public string HexKey => Q + "," + R;
        public ReorgContainer Garrison => Containers.FirstOrDefault(c => c.IsGarrison);

        public bool WorthPlanning()
        {
            // §7 — a commander reorder is a single-container operation, so it must not wait on
            // the multi-container gate below: a lone army/garrison with a choice of commander is
            // worth a zero-AP planning pass on its own (the planner's one commander evaluation
            // decides whether the current one is already the best).
            foreach (ReorgContainer c in Containers)
                if (ReorgViability.HasCommanderChoice(c))
                    return true;

            if (Containers.Count < AiConfigV2.housekeepingMinContainersForGroup)
                return false;

            ReorgContainer localGarrison = Garrison;
            if (localGarrison != null && localGarrison.CanReceive
                && Containers.Any(c => !c.IsGarrison && c.IsMutableGround
                    && c.Units.Any(u => u != null && u.IsDevelopmentOperator)))
                return true;

            // §9 — a heroless OR support-led viable field formation plus a benched combat hero
            // that could lead it is worth a planning pass even if nothing else is degraded.
            bool benchedCombatHero = Containers.Any(c => c.CanChangeComposition && c.Units.Any(u =>
                u != null && u.IsHero && u.HeroRole != HeroOperationalRole.SupportOperator
                && (c.IsGarrison ? c.Units.Count > 1 : c.Units.Count == 1)));
            bool leadershipDefect = Containers.Any(c => c.IsMutableGround && c.CanChangeComposition
                && !c.SingletonExempt && c.Units.Count >= 2 && ReorgViability.IsViable(c.Units)
                && (c.Units.All(u => !u.IsHero)
                    || c.Units.FirstOrDefault(u => u.IsHero)?.HeroRole == HeroOperationalRole.SupportOperator));
            if (benchedCombatHero && leadershipDefect)
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
            units != null && units.Count == 1 && units[0].IsGroundCombatant;

        public static bool IsNonExemptSingleton(ReorgContainer c) =>
            c != null && !c.SingletonExempt && c.IsMutableGround && IsSingletonShape(c.Units);

        // §7 — the container holds >= 2 heroes, so which of them commands is a real choice.
        public static bool HasCommanderChoice(ReorgContainer c) =>
            c != null && c.CanChangeComposition && c.Units.Count(u => u != null && u.IsHero) >= 2;

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
