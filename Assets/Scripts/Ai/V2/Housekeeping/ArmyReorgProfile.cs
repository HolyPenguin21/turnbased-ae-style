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
    //  ARMY REORG PROFILE + LOCAL FORCE GROUP — the planner's model of one hex.
    // ===========================================================================================
    //  An immutable, contextual projection of each local container for one Housekeeping pass, built
    //  from the owners' signals (AiArmyRoles role, AiPower strength, ActorCommitments ownership,
    //  ArmyData capacity); never written back. ReorgViability holds the roster rules the planner
    //  reads on it.
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
        // Hero operational-role signals (0 / Flexible for non-heroes).
        public float HeroCombatLeadership;
        public HeroOperationalRole HeroRole;
        public float Power;
        public int Range;
        public IReadOnlyList<UnitTypeTag> TypeTags = Array.Empty<UnitTypeTag>();
        public int ActivationApCost;
        // Remaining / maximum movement (ArmyData's slowest-member rule): an inbound member of a
        // moving operation is checked against its route.
        public int MoveCurrent;
        public int MoveMax;
        // Native movement and spent budget are required when projecting a DIFFERENT roster:
        // the donor's Transport bonus is never a permanent characteristic of this member.
        public int NativeMoveMax = -1;
        public int MovementSpent = -1;
        public int RuntimeIdentity = -1;
        public int TransportCarrierIdentity = -1;
        public bool IsAviation;
        public bool IsCommitted;
        // The unit's card identity (StrikeRoster.UnitKey) — matched against an Attack
        // preparation host's target roster.
        public string StrikeKey;
        // Turn-local contextual duty: this exact hero is the minimum operator set needed by a
        // Research/Production facility on the current hex. Not a persistent strategic role.
        public bool IsDevelopmentOperator;
        // A garrison hero (AiArmyRoles.IsGarrisonHero: the card's Support type tag). By day it goes
        // into the local garrison and never out of it.
        public bool IsGarrisonHero => AiArmyRoles.IsGarrisonHero(IsHero, TypeTags);
        // Exact immutable combat profile consumed by WorthIt. Heroes keep a profile for
        // diagnostics but are excluded from Ground Combat roster estimates.
        public WorthIt.DefenderProfile CombatProfile;
        // What this hero gives a formation it commands (initiative bonus, battle Fate).
        public WorthIt.SideCommander AsCommander;
        // An army support carrier (StrategicEffectRegistry.IsArmySupportCarrier's rule).
        public bool IsArmySupportCarrier =>
            StrategicEffectRegistry.HasArmySupportEffect(CombatProfile.Abilities, MoveMax);
    }

    public sealed class ReorgContainer
    {
        public int ArmyId;
        public ReorgPhysicalRole Role;
        public bool IsGarrison;
        public bool HasActivatedThisTurn;
        // Same-hex units whose activation share THIS army already paid this turn
        // (ArmyData.HasActivationCoverageFor) — joining back costs no AP.
        public HashSet<int> ActivationCoveredUnitKeys = new HashSet<int>();
        public List<ReorgUnit> Units = new List<ReorgUnit>();

        // ArmyData.RequiresActivationCharge for this roster, restricted to units that cost AP.
        public bool ChargesActivationFor(ReorgUnit u) =>
            HasActivatedThisTurn && u.ActivationApCost > 0 && !ActivationCoveredUnitKeys.Contains(u.Key);

        public bool CanDonate;
        public bool CanReceive;
        public bool CanChangeComposition;
        public bool SingletonExempt;
        // Bodies a garrison must hold (1) and the defence power it keeps when it releases one
        // (AiArmyRoles.GarrisonDefenceFloor / SpareableBodies — the same rule every donor reads).
        public int GarrisonNonHeroFloor;
        public float GarrisonPowerFloor;
        // The garrison's floor before the resident field armies' credit (AiArmyRoles.
        // GarrisonFloorBeforeResidents) and, on a field army, whether it is such a resident
        // (AiArmyRoles.IsHexResidentArmy): the planner re-reads the credit on its virtual roster
        // (ReorgViability.GarrisonFloor), exactly as the live release rule will at execution.
        public float GarrisonBaseFloor;
        public bool IsHexResident;
        // A claimed army whose task needs units (ArmyMutationContract.NeedsUnits): the garrison's
        // surplus above its floor joins it (planner step 0.9).
        public bool NeedsUnits;
        // The hex's fist: the free field army with a commanding hero and fighters, the widest command
        // then the strongest (FistMusterObjectiveEvaluator.FistHost's order). It takes the garrison's
        // surplus like a task (above the garrison's own share floor, as the muster Intake keeps it) and
        // the morning restores it after the night.
        public bool IsFist;
        // The fist only: how many garrison bodies it may still take on this hex (release rule and the
        // garrison's own share, SpareableBodies); they count toward its commander choice.
        public int FistGarrisonSpare;
        // The garrison's own share floor (FistMusterObjectiveEvaluator.OwnShareFloor): what it
        // keeps whoever else stands on its hex — the fist's intake never goes below it.
        public float GarrisonOwnShareFloor;

        // A claimed operation container whose contract lets it take free same-hex members
        // (ArmyMutationContract.MayReceive). It never donates, is never folded, swapped or deposited;
        // its own singleton / non-viable defects count, so it may be fixed by inbound members only.
        public bool IsMissionReceiver;
        // ArmyMutationContract.MayReleaseExcessHeroes (the Attack preparation host).
        public bool MayReleaseExcessHeroes;
        // The Attack preparation host's frozen target roster (AttackIntent.TargetRoster keys, a multiset)
        // and the held Unit card keys that could fill it; null for every other container.
        public IReadOnlyList<string> PreparationTargetKeys;
        public IReadOnlyList<string> PreparationHandKeys;
        public string MissionLabel;
        // Commander promotion among heroes already in the roster (free field/garrison containers
        // via CanChangeComposition; mission containers via their contract).
        public bool CanReorderCommander;
        // Route-bound operation: an inbound member must keep at least this remaining / maximum
        // movement (the army's values at analysis time). -1 = no floor.
        public int MovementFloor = -1;
        public int MaxMovementFloor = -1;

        public bool IsMutableGround =>
            Role == ReorgPhysicalRole.NormalFieldArmy || Role == ReorgPhysicalRole.EmptyReusableArmy;

        // A ground field container that may take members: a free one, or a mission receiver.
        public bool IsFieldReceiver => (IsMutableGround && CanReceive) || IsMissionReceiver;

        // A field formation whose own singleton / viability / strength the planner scores.
        public bool IsScoredField => (IsMutableGround && CanChangeComposition) || IsMissionReceiver;

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
        // The friendly non-hero keys this enemy can target as far as we know: every unit not in our
        // own stealth (UnitData.IsHidden; the enemy's detection is unknown, so never IsHiddenFrom),
        // read once off the live roster. A virtual transfer moves a key but never changes this fact, so
        // contact selection stays honest; a container with none of these keys is no contact candidate.
        public HashSet<int> TargetableUnitKeys = new HashSet<int>();
    }

    public sealed class LocalForceGroup
    {
        public int Q;
        public int R;
        public float HexDefenseBonus;
        // The structural part of HexDefenseBonus (the own Base / Citadel's Defense, weather-aware), which
        // an attacker's Disruption carrier removes — the split every own-defence estimate uses
        // (WorldAnalysis.Threat, ActiveDefence HoldChanceAtAsset).
        public float BuildingDefenseBonus;
        // The public weather of the group's hex: a fight there is fought under it.
        public Game.Combat.BattleEnvironment Environment;
        public List<ReorgContainer> Containers = new List<ReorgContainer>();
        public List<ReorgThreatBenchmark> ThreatBenchmarks = new List<ReorgThreatBenchmark>();

        public ReorgContainer Garrison => Containers.FirstOrDefault(c => c.IsGarrison);

    }

    public static class ReorgViability
    {
        internal static (int current, int maximum) Movement(IReadOnlyList<ReorgUnit> roster)
        {
            var army = ArmyData.CreateVisualSnapshot();
            foreach (ReorgUnit u in roster)
            {
                UnitData member = UnitData.CreateProjection(u.RuntimeIdentity);
                member.IsHero = u.IsHero;
                member.MoveMax = u.NativeMoveMax >= 0 ? u.NativeMoveMax : u.MoveMax;
                member.MoveCurrent = u.MoveCurrent;
                member.RestoreMovementSpent(u.MovementSpent >= 0 ? u.MovementSpent
                    : System.Math.Max(0, member.PermanentMoveMax - u.MoveCurrent));
                member.HitPointsCurrent = member.HitPointsMax = 1;
                member.TypeTags.UnionWith(u.TypeTags ?? System.Array.Empty<UnitTypeTag>());
                member.Abilities.UnionWith(u.CombatProfile.Abilities ?? System.Array.Empty<string>());
                army.Members.Add(member);
            }
            for (int i = 0; i < roster.Count; i++)
                if (roster[i].TransportCarrierIdentity > 0)
                {
                    UnitData carrier = army.Members.FirstOrDefault(u => u.RuntimeId == roster[i].TransportCarrierIdentity);
                    if (carrier != null) army.Members[i].SetTransportCarrier(carrier, army);
                }
            return (ArmyData.ComputeCurrentMovement(army.Members), ArmyData.ComputeMaxMovement(army.Members));
        }

        private static List<AiPower.PowerUnit> ToPowerUnits(IEnumerable<ReorgUnit> units)
        {
            if (units == null)
                return new List<AiPower.PowerUnit>();
            return units.Select(u => new AiPower.PowerUnit(u.Power, u.TypeTags, u.Range, u.IsHero)).ToList();
        }

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

        // A hero-only free ground container on the hex: besides the garrison, the place of an
        // operator / Support hero (ResearchProductionSystem.FindActors reads every own army on the
        // hex). It takes no garrison slot, never fights, and a departure that would take the
        // facility's operator is refused by its duty (OperatorDutyBlocksDeparture). The executor
        // asks the same container with its live members.
        public static bool IsHexBench(ReorgContainer meta, IReadOnlyList<ReorgUnit> units) =>
            IsBenchContainer(meta) && units != null && units.All(u => u.IsHero);

        public static bool IsBenchContainer(ReorgContainer meta) =>
            meta != null && !meta.IsGarrison && meta.IsMutableGround && !meta.IsMissionReceiver;

        public static bool IsNonExemptSingleton(ReorgContainer c) =>
            c != null && !c.SingletonExempt && c.IsScoredField && IsSingletonShape(c.Units);

        // The live release floor (AiArmyRoles.GarrisonDefenceFloor) on a virtual roster of the hex: the
        // garrison's base floor less the power of the resident field armies as they stand in `rosters`
        // (a floor frozen at analysis time would let a plan move a resident's body away and then
        // release a garrison body the live rule refuses).
        public static float GarrisonFloor(ReorgContainer garrison,
            IEnumerable<KeyValuePair<ReorgContainer, List<ReorgUnit>>> rosters)
        {
            float resident = 0f;
            foreach (KeyValuePair<ReorgContainer, List<ReorgUnit>> kv in rosters
                         ?? Enumerable.Empty<KeyValuePair<ReorgContainer, List<ReorgUnit>>>())
                if (kv.Key != null && !kv.Key.IsGarrison && kv.Key.IsHexResident && kv.Value != null)
                    resident += EffectivePower(kv.Value.Where(u => u != null && u.IsGroundBattleBody).ToList());
            return Math.Max(0f, garrison.GarrisonBaseFloor - resident);
        }

        // How far an Attack preparation host is from the positions of its target roster that have a
        // concrete source (a free same-hex body of a donor, a held card):
        //   pending sources + min(non-roster bodies, max(0, pending sources - free slots)).
        // Taking a roster body in lowers the first term, releasing a non-roster body that blocks a
        // slot lowers the second, taking it back raises it again — no ping-pong. A held card stays
        // pending until Phase A plays it. Zero without a target roster.
        public static int PreparationRosterWaste(ReorgContainer host, IReadOnlyList<ReorgUnit> hostUnits,
            IEnumerable<KeyValuePair<ReorgContainer, List<ReorgUnit>>> others)
        {
            PreparationRosterGap(host, hostUnits, others, out int pending, out int blocked, out _);
            return pending + blocked;
        }

        // The planner's comparison term: the waste plus the missing positions whose body stands in a
        // garrison that will not release it (outside its spare set, no exchange either). Without it a
        // free roster body put into the garrison would lower the waste and stay there for good.
        public static int PreparationRosterPenalty(ReorgContainer host, IReadOnlyList<ReorgUnit> hostUnits,
            IEnumerable<KeyValuePair<ReorgContainer, List<ReorgUnit>>> others)
        {
            PreparationRosterGap(host, hostUnits, others, out int pending, out int blocked, out int stranded);
            return pending + blocked + stranded;
        }

        // The second term alone: non-roster bodies that block a pending source's slot.
        public static int PreparationBlockedSlots(ReorgContainer host, IReadOnlyList<ReorgUnit> hostUnits,
            IEnumerable<KeyValuePair<ReorgContainer, List<ReorgUnit>>> others)
        {
            PreparationRosterGap(host, hostUnits, others, out _, out int blocked, out _);
            return blocked;
        }

        private static void PreparationRosterGap(ReorgContainer host, IReadOnlyList<ReorgUnit> hostUnits,
            IEnumerable<KeyValuePair<ReorgContainer, List<ReorgUnit>>> others, out int pending, out int blocked,
            out int stranded)
        {
            pending = 0;
            blocked = 0;
            stranded = 0;
            if (host?.PreparationTargetKeys == null || hostUnits == null)
                return;
            others = (others ?? Enumerable.Empty<KeyValuePair<ReorgContainer, List<ReorgUnit>>>()).ToList();
            var nonTargetBodies = new List<ReorgUnit>();
            Dictionary<string, int> need = RosterNeed(host.PreparationTargetKeys, hostUnits, nonTargetBodies);
            int nonTarget = nonTargetBodies.Count;
            int found = 0;
            bool Take(string key)
            {
                if (key == null || !need.TryGetValue(key, out int n) || n <= 0) return false;
                need[key] = n - 1;
                found++;
                return true;
            }
            foreach (KeyValuePair<ReorgContainer, List<ReorgUnit>> o in others
                         ?? Enumerable.Empty<KeyValuePair<ReorgContainer, List<ReorgUnit>>>())
            {
                if (o.Key == null || o.Key == host || !o.Key.CanDonate || o.Value == null)
                    continue;
                // A garrison body is a source only when the garrison may let it go (AiArmyRoles.SpareableBodies
                // over its power floor); otherwise the host would free a slot for a body that never comes.
                List<ReorgUnit> garrisonBodies = o.Key.IsGarrison
                    ? o.Value.Where(x => x != null && x.IsGroundCombatant).ToList() : null;
                HashSet<int> spare = garrisonBodies == null ? null
                    : AiArmyRoles.SpareableBodies(garrisonBodies,
                        set => EffectivePower(set.ToList()), GarrisonFloor(o.Key, others));
                foreach (ReorgUnit u in o.Value)
                    if (u != null && !u.IsHero && !u.IsAviation && !u.IsCommitted
                        && (spare == null || spare.Contains(garrisonBodies.IndexOf(u))))
                        Take(u.StrikeKey);
            }
            foreach (string k in host.PreparationHandKeys ?? Array.Empty<string>())
                Take(k);
            int released = found;
            // A garrison body the floor keeps is still a source by exchange: a non-roster body of the host
            // goes into the garrison in its place, the garrison keeping min(held, floor)
            // (AiArmyRoles.KeepsFloorAfterExchange). It counts as pending, but never as a reason to release
            // a host body into a free container: only the 1-for-1 exchange can bring it.
            var exchangeable = new List<ReorgUnit>(nonTargetBodies.Where(b => !b.IsCommitted)
                .OrderBy(b => b.Power).ThenBy(b => b.Key));
            foreach (KeyValuePair<ReorgContainer, List<ReorgUnit>> o in others
                         ?? Enumerable.Empty<KeyValuePair<ReorgContainer, List<ReorgUnit>>>())
            {
                if (exchangeable.Count == 0)
                    break;
                if (o.Key == null || o.Key == host || !o.Key.IsGarrison || !o.Key.CanChangeComposition
                    || o.Value == null)
                    continue;
                List<ReorgUnit> garrisonBodies = o.Value.Where(x => x != null && x.IsGroundCombatant).ToList();
                float floor = GarrisonFloor(o.Key, others);
                HashSet<int> spare = AiArmyRoles.SpareableBodies(garrisonBodies,
                    set => EffectivePower(set.ToList()), floor);
                foreach (ReorgUnit u in garrisonBodies.OrderByDescending(x => x.Power).ThenBy(x => x.Key))
                {
                    if (exchangeable.Count == 0)
                        break;
                    int index = garrisonBodies.IndexOf(u);
                    if (spare.Contains(index) || u.IsAviation || u.IsCommitted || u.StrikeKey == null
                        || !need.TryGetValue(u.StrikeKey, out int n) || n <= 0)
                        continue;
                    ReorgUnit swapIn = exchangeable.FirstOrDefault(b => AiArmyRoles.KeepsFloorAfterExchange(
                        garrisonBodies, index, b, set => EffectivePower(set.ToList()), floor));
                    if (swapIn == null)
                        continue;
                    exchangeable.Remove(swapIn);
                    Take(u.StrikeKey);
                }
            }
            int free = Math.Max(0, Capacity(hostUnits, host.IsGarrison) - hostUnits.Count);
            pending = found;
            blocked = Math.Min(nonTarget, Math.Max(0, released - free));
            // What is still missing and stands in a garrison is held there (not spare, no exchange).
            foreach (KeyValuePair<ReorgContainer, List<ReorgUnit>> o in others
                         ?? Enumerable.Empty<KeyValuePair<ReorgContainer, List<ReorgUnit>>>())
            {
                if (o.Key == null || o.Key == host || !o.Key.IsGarrison || o.Value == null)
                    continue;
                foreach (ReorgUnit u in o.Value)
                    if (u != null && !u.IsHero && !u.IsAviation && !u.IsCommitted && u.StrikeKey != null
                        && need.TryGetValue(u.StrikeKey, out int n) && n > 0)
                    {
                        need[u.StrikeKey] = n - 1;
                        stranded++;
                    }
            }
        }

        // The positions of a preparation's target roster (a multiset by card key) that `units` do not
        // fill yet. `nonRosterBodies`, when given, receives the non-hero bodies outside the roster,
        // strongest first; an army support carrier is never surplus (StrikeRoster.NonTargetBodies).
        internal static Dictionary<string, int> RosterNeed(IReadOnlyList<string> target,
            IEnumerable<ReorgUnit> units, List<ReorgUnit> nonRosterBodies = null)
        {
            var need = new Dictionary<string, int>();
            foreach (string k in target)
                if (k != null) { need.TryGetValue(k, out int n); need[k] = n + 1; }
            List<ReorgUnit> roster = units.Where(u => u != null).ToList();
            foreach (ReorgUnit u in roster.Where(u => !u.IsHero).OrderByDescending(u => u.Power).ThenBy(u => u.Key))
            {
                if (u.StrikeKey != null && need.TryGetValue(u.StrikeKey, out int n) && n > 0)
                    need[u.StrikeKey] = n - 1;
                else if (!u.IsArmySupportCarrier)
                    nonRosterBodies?.Add(u);
            }
            foreach (ReorgUnit u in roster.Where(u => u.IsHero))
                if (u.StrikeKey != null && need.TryGetValue(u.StrikeKey, out int n) && n > 0)
                    need[u.StrikeKey] = n - 1;
            return need;
        }

        // Two or more heroes: which of them commands is a real choice.
        public static bool HasCommanderChoice(ReorgContainer c) =>
            c != null && c.CanReorderCommander && c.Units.Count(u => u != null && u.IsHero) >= 2;

        // The live roster rules (ArmyData: capacity, sorted insert, garrison order) on the projected
        // roster, so the virtual plan and gameplay can never disagree.
        public static int Capacity(IReadOnlyList<ReorgUnit> units, bool isGarrison) =>
            ArmyData.ComputeCapacity(units, isGarrison, u => u.IsHero, u => u.CommandRating);

        public static void AddMemberSorted(List<ReorgUnit> roster, ReorgUnit unit, bool isGarrison)
        {
            if (isGarrison)
            {
                roster.Add(unit);
                List<ReorgUnit> ordered = NormalizeRoster(roster);
                roster.Clear();
                roster.AddRange(ordered);
                return;
            }
            roster.Insert(ArmyData.SortedInsertIndex(roster, unit.IsHero, u => u.IsHero), unit);
        }

        public static List<ReorgUnit> NormalizeRoster(IEnumerable<ReorgUnit> units) =>
            ArmyData.NormalizeRoster(units, u => u != null && u.IsHero, h => h.CommandRating,
                h => h.AsCommander.Fate);

        public static bool IsCanonicalGarrisonOrder(IReadOnlyList<ReorgUnit> units)
        {
            List<ReorgUnit> ordered = NormalizeRoster(units);
            for (int i = 0; i < ordered.Count; i++)
                if (!ReferenceEquals(ordered[i], units[i]))
                    return false;
            return true;
        }

        public static bool CanLeaveWithoutOvercrowding(IReadOnlyList<ReorgUnit> units, ReorgUnit leaving, bool isGarrison)
        {
            var remaining = new List<ReorgUnit>(units ?? Array.Empty<ReorgUnit>());
            remaining.Remove(leaving);
            if (isGarrison)
                remaining = NormalizeRoster(remaining);
            return Capacity(remaining, isGarrison) >= remaining.Count;
        }
    }
}
