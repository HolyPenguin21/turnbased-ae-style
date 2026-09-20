using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    internal readonly struct RaidRecoveryProjection
    {
        internal readonly bool Viable;
        internal readonly RaidMissionPhase Phase;
        internal readonly HexCoord? BaseHex;
        internal readonly int? SupportArmyId;
        internal readonly int EtaTurns;
        internal readonly float ApCost;
        internal readonly ResourceVector ResourceCost;
        internal readonly int BlockedActors;
        internal readonly float CurrentWinChance;
        internal readonly float ProjectedWinChance;
        internal readonly RaidRefitAction FirstRefitAction;
        internal readonly string Reason;

        internal RaidRecoveryProjection(bool viable, RaidMissionPhase phase, HexCoord? baseHex,
            int? supportArmyId, int etaTurns, float apCost, ResourceVector resourceCost,
            int blockedActors, float currentWinChance, float projectedWinChance,
            RaidRefitAction firstRefitAction, string reason)
        {
            Viable = viable;
            Phase = phase;
            BaseHex = baseHex;
            SupportArmyId = supportArmyId;
            EtaTurns = etaTurns;
            ApCost = apCost;
            ResourceCost = resourceCost;
            BlockedActors = blockedActors;
            CurrentWinChance = currentWinChance;
            ProjectedWinChance = projectedWinChance;
            FirstRefitAction = firstRefitAction;
            Reason = reason;
        }

        internal static RaidRecoveryProjection None(float currentWin, string reason) =>
            new RaidRecoveryProjection(false, RaidMissionPhase.Return, null, null,
                int.MaxValue, float.MaxValue, ResourceVector.Zero, int.MaxValue,
                currentWin, currentWin, default, reason);
    }

    // Pure comparison of the two ways an already-started Raid can regain the existing
    // raidMinViableWinChance. Combat/resources come from the immutable snapshot; Continuity supplies
    // the existing read-only SafeStepPathing oracle so route viability uses the same cached blocker
    // rules as execution. It returns one frozen next action and mutates no live state.
    internal static class RaidRecoveryPlanner
    {
        private sealed class SimMember
        {
            internal RaidRecoveryMemberSnapshot Source;
            internal WorthIt.DefenderProfile Profile;
        }

        private readonly struct Candidate
        {
            internal readonly RaidRefitAction Action;
            internal readonly float Gain;
            internal readonly float Efficiency;
            internal readonly bool Hero;
            internal readonly float FullValue;
            internal readonly int StableIndex;
            internal readonly int DonorArmyId;

            internal Candidate(RaidRefitAction action, float gain, bool hero,
                float fullValue, int stableIndex, int donorArmyId)
            {
                Action = action;
                Gain = gain;
                float scarcity = 1f + action.ApCost + PhysicalMagnitude(action.ResourceCost);
                Efficiency = gain / scarcity;
                Hero = hero;
                FullValue = fullValue;
                StableIndex = stableIndex;
                DonorArmyId = donorArmyId;
            }
        }

        internal static RaidRecoveryProjection Choose(WorldSnapshot snap, RaidIntent raid,
            ISet<int> unavailableArmyIds, HexCoord? fixedBase = null,
            Func<HexCoord, HexCoord, int, int> safeRouteCost = null)
        {
            if (snap?.Self?.Armies == null || raid == null || !raid.PrimaryArmyId.HasValue)
                return RaidRecoveryProjection.None(0f, "missing primary snapshot");
            ArmySnapshot primary = snap.Self.Armies.FirstOrDefault(a => a != null
                && a.ArmyId == raid.PrimaryArmyId.Value);
            if (primary == null)
                return RaidRecoveryProjection.None(0f, "primary no longer exists");
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                AiV2Util.KnownDefenders(snap, raid.Target);
            float currentWin = Win(CombatRoster(primary), defenders, out _);

            RaidRecoveryProjection field = fixedBase.HasValue
                ? RaidRecoveryProjection.None(currentWin, "field comparison suppressed by fixed recovery base")
                : ProjectField(snap, primary, defenders, unavailableArmyIds, currentWin,
                    safeRouteCost);
            RaidRecoveryProjection atBase = fixedBase.HasValue
                ? ProjectBase(snap, raid, primary, defenders, unavailableArmyIds, currentWin,
                    fixedBase, safeRouteCost)
                : ProjectBestBase(snap, raid, primary, defenders, unavailableArmyIds,
                    currentWin, safeRouteCost);
            if (!field.Viable) return atBase;
            if (!atBase.Viable) return field;
            return Compare(field, atBase) <= 0 ? field : atBase;
        }

        private static RaidRecoveryProjection ProjectBestBase(WorldSnapshot snap, RaidIntent raid,
            ArmySnapshot primary, IReadOnlyList<WorthIt.DefenderProfile> defenders,
            ISet<int> unavailableArmyIds, float currentWin,
            Func<HexCoord, HexCoord, int, int> safeRouteCost)
        {
            RaidRecoveryProjection best = RaidRecoveryProjection.None(currentWin,
                "no owned recovery base has a complete safe threshold-clearing plan");
            IEnumerable<HexCoord> bases = (snap.Self.BaseHexes ?? Array.Empty<HexCoord>())
                .Distinct().OrderBy(h => h.Q).ThenBy(h => h.R);
            foreach (HexCoord baseHex in bases)
            {
                RaidRecoveryProjection option = ProjectBase(snap, raid, primary, defenders,
                    unavailableArmyIds, currentWin, baseHex, safeRouteCost);
                if (option.Viable && (!best.Viable || Compare(option, best) < 0))
                    best = option;
            }
            return best;
        }

        internal static RaidRecoveryProjection ProjectBase(WorldSnapshot snap, RaidIntent raid,
            ArmySnapshot primary, IReadOnlyList<WorthIt.DefenderProfile> defenders,
            ISet<int> unavailableArmyIds, float currentWin, HexCoord? fixedBase = null,
            Func<HexCoord, HexCoord, int, int> safeRouteCost = null)
        {
            HexCoord? baseHex = fixedBase ?? MissionContinuityLayer.SelectReturnBase(
                snap, primary.Owner, primary.ArmyId);
            if (!baseHex.HasValue
                || !(snap.Self.BaseHexes ?? Array.Empty<HexCoord>()).Contains(baseHex.Value))
                return RaidRecoveryProjection.None(currentWin, "no owned recovery base");
            bool atBase = primary.Hex.Equals(baseHex.Value);
            if (!atBase && (primary.ReachableOwnBaseHexes == null
                || !primary.ReachableOwnBaseHexes.Contains(baseHex.Value)))
                return RaidRecoveryProjection.None(currentWin, "recovery base has no safe structural route");

            var roster = (primary.RecoveryMembers ?? Array.Empty<RaidRecoveryMemberSnapshot>())
                .Where(m => !m.IsHero && !m.IsAviation)
                .Select(m => new SimMember { Source = m, Profile = m.CurrentProfile })
                .ToList();
            int initialCombatBodyCount = roster.Count;
            if (roster.Count == 0)
                return RaidRecoveryProjection.None(currentWin, "primary has no recoverable combat body");

            var donors = (snap.Self.Armies ?? Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.ArmyId != primary.ArmyId
                    && a.Owner == primary.Owner && a.Hex.Equals(baseHex.Value)
                    && !a.IsPrison && !a.IsAir && !a.IsAirfield
                    && (unavailableArmyIds == null || !unavailableArmyIds.Contains(a.ArmyId)))
                .SelectMany(a => (a.RecoveryMembers ?? Array.Empty<RaidRecoveryMemberSnapshot>())
                    .Where(m => m.CanSpareForRaid && !m.IsHero && !m.IsAviation)
                    .Select(m => (Army: a, Member: m)))
                .OrderBy(x => x.Army.ArmyId).ThenBy(x => x.Member.UnitIndex)
                .ToList();

            ResourceVector spent = ResourceVector.Zero;
            float ap = 0f;
            int actions = 0;
            var usedDonors = new HashSet<int>();
            RaidRefitAction first = default;
            float win = currentWin;
            bool cover;
            GroundCombatFeasibility.Clears(roster.Select(x => x.Profile).ToList(), defenders,
                AiConfigV2.raidMinViableWinChance, out win, out cover);

            int bound = roster.Count + donors.Count;
            while (!(cover && win >= AiConfigV2.raidMinViableWinChance) && actions < bound)
            {
                List<Candidate> candidates = BuildCandidates(snap, primary, baseHex.Value,
                    roster, initialCombatBodyCount, donors, usedDonors, defenders, spent, win);
                Candidate best = candidates
                    .OrderByDescending(c => c.Efficiency)
                    .ThenByDescending(c => c.Gain)
                    .ThenByDescending(c => c.Hero)
                    .ThenByDescending(c => c.FullValue)
                    .ThenBy(c => c.DonorArmyId)
                    .ThenBy(c => c.StableIndex)
                    .FirstOrDefault();
                if (!best.Action.HasValue || best.Gain < AiConfigV2.raidRepairMinWinChanceGain)
                    break;
                if (!first.HasValue) first = best.Action;
                Apply(best.Action, roster, donors, usedDonors);
                spent += best.Action.ResourceCost;
                ap += best.Action.ApCost;
                actions++;
                GroundCombatFeasibility.Clears(roster.Select(x => x.Profile).ToList(), defenders,
                    AiConfigV2.raidMinViableWinChance, out win, out cover);
            }

            if (!first.HasValue || !cover || win < AiConfigV2.raidMinViableWinChance)
                return RaidRecoveryProjection.None(currentWin,
                    "no bounded repair/fill/swap sequence reaches the raid threshold");

            int moveBudget = Math.Max(1, primary.MaxMovement);
            int toBaseDistance = atBase ? 0 : RouteCost(primary.Hex, baseHex.Value, primary.MaxMovement,
                safeRouteCost, HexGridMath.Distance(primary.Hex, baseHex.Value));
            if (toBaseDistance == int.MaxValue)
                return RaidRecoveryProjection.None(currentWin,
                    "recovery base has no safe structural route");
            int toTargetDistance = RouteCost(baseHex.Value, raid.LastKnownHex, primary.MaxMovement,
                safeRouteCost, HexGridMath.Distance(baseHex.Value, raid.LastKnownHex));
            if (toTargetDistance == int.MaxValue)
                return RaidRecoveryProjection.None(currentWin,
                    "recovery base has no safe route back to the raid target");
            int toBase = atBase ? 0 : CeilTurns(primary, toBaseDistance);
            int toTarget = Math.Max(1, (toTargetDistance + moveBudget - 1) / moveBudget);
            int eta = toBase + actions + toTarget;
            int donorsBlocked = donors.Where(d => usedDonors.Contains(d.Member.RuntimeId))
                .Select(d => d.Army.ArmyId).Distinct().Count();
            return new RaidRecoveryProjection(true, atBase ? RaidMissionPhase.Refit
                    : RaidMissionPhase.RecoveryReturn, baseHex, null, eta, ap, spent,
                1 + donorsBlocked, currentWin, win, first,
                $"base recovery reaches {win:0.00} in {eta} turn-step(s) with {actions} action(s)");
        }

        private static RaidRecoveryProjection ProjectField(WorldSnapshot snap, ArmySnapshot primary,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, ISet<int> unavailableArmyIds,
            float currentWin, Func<HexCoord, HexCoord, int, int> safeRouteCost)
        {
            List<int> ids = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, primary.ArmyId, defenders, unavailableArmyIds);
            RaidRecoveryProjection best = RaidRecoveryProjection.None(currentWin,
                "no free field support can deliver a threshold-clearing roster");
            foreach (int id in ids)
            {
                ArmySnapshot support = snap.Self.Armies.FirstOrDefault(a => a != null && a.ArmyId == id);
                if (support == null) continue;
                List<WorthIt.DefenderProfile> supportBodies = (support.RecoveryMembers
                        ?? Array.Empty<RaidRecoveryMemberSnapshot>())
                    .Where(m => m.CanSpareForRaid && !m.IsHero && !m.IsAviation)
                    .OrderByDescending(m => ProfileValue(m.CurrentProfile))
                    .Select(m => m.CurrentProfile).ToList();
                if (!GroundCombatAssemblyPlanner.TryProjectReinforcement(CombatRoster(primary),
                        supportBodies, primary.Capacity, primary.MemberCount, defenders,
                        out List<WorthIt.DefenderProfile> projected, out _))
                    continue;
                bool clears = GroundCombatFeasibility.Clears(projected, defenders,
                    AiConfigV2.raidMinViableWinChance, out float after, out _);
                if (!clears) continue;
                int routeDistance = RouteCost(support.Hex, primary.Hex, support.MaxMovement, safeRouteCost,
                    HexGridMath.Distance(support.Hex, primary.Hex));
                if (routeDistance == int.MaxValue)
                    continue;
                int eta = CeilTurns(support, routeDistance) + 1;
                float ap = support.HasActivatedThisTurn ? 0f : support.ActivationApCost;
                var option = new RaidRecoveryProjection(true, RaidMissionPhase.Reinforcement,
                    null, support.ArmyId, eta, ap, ResourceVector.Zero, 2,
                    currentWin, after, default,
                    $"field support #{support.ArmyId} reaches {after:0.00} in {eta} turn-step(s)");
                if (!best.Viable || Compare(option, best) < 0) best = option;
            }
            return best;
        }

        private static List<Candidate> BuildCandidates(WorldSnapshot snap, ArmySnapshot primary,
            HexCoord baseHex, List<SimMember> roster, int initialCombatBodyCount,
            List<(ArmySnapshot Army, RaidRecoveryMemberSnapshot Member)> donors,
            HashSet<int> usedDonors, IReadOnlyList<WorthIt.DefenderProfile> defenders,
            ResourceVector spent, float winBefore)
        {
            var result = new List<Candidate>();
            foreach (SimMember member in roster)
            {
                RaidRecoveryMemberSnapshot source = member.Source;
                if (!source.RepairCostInitialized
                    || member.Profile.HitPoints >= source.FullHealthProfile.MaxHitPoints
                    || !Covers(snap.Self.Stockpile, spent + source.RepairCost))
                    continue;
                var projected = roster.Select(x => x.Profile).ToList();
                projected[roster.IndexOf(member)] = source.FullHealthProfile;
                float after = Win(projected, defenders, out _);
                float gain = after - winBefore;
                var action = new RaidRefitAction
                {
                    Kind = RaidRefitActionKind.RepairUnit,
                    PrimaryArmyId = primary.ArmyId,
                    UnitRuntimeId = source.RuntimeId,
                    BaseHex = baseHex,
                    ApCost = 1,
                    ResourceCost = source.RepairCost,
                    WinChanceBefore = winBefore,
                    WinChanceAfter = after,
                };
                result.Add(new Candidate(action, gain, source.IsHero,
                    source.FullCombatValue, source.UnitIndex, 0));
            }

            int currentMemberCount = primary.MemberCount + (roster.Count - initialCombatBodyCount);
            int free = Math.Max(0, primary.Capacity - currentMemberCount);
            foreach ((ArmySnapshot donorArmy, RaidRecoveryMemberSnapshot donor) in donors)
            {
                if (usedDonors.Contains(donor.RuntimeId)) continue;
                int alreadyTaken = donors.Count(d => d.Army.ArmyId == donorArmy.ArmyId
                    && usedDonors.Contains(d.Member.RuntimeId));
                if (alreadyTaken >= Math.Max(0, donorArmy.MemberCount - 1)) continue;
                if (free > 0)
                {
                    var projected = roster.Select(x => x.Profile).ToList();
                    projected.Add(donor.CurrentProfile);
                    float after = Win(projected, defenders, out _);
                    var action = new RaidRefitAction
                    {
                        Kind = RaidRefitActionKind.TransferUnit,
                        PrimaryArmyId = primary.ArmyId,
                        DonorArmyId = donorArmy.ArmyId,
                        UnitRuntimeId = donor.RuntimeId,
                        BaseHex = baseHex,
                        ApCost = JoinActivationAp(primary, donor),
                        ResourceCost = ResourceVector.Zero,
                        WinChanceBefore = winBefore,
                        WinChanceAfter = after,
                    };
                    result.Add(new Candidate(action, after - winBefore, false,
                        donor.FullCombatValue, donor.UnitIndex, donorArmy.ArmyId));
                    continue;
                }

                foreach (SimMember displaced in roster.Where(x => !x.Source.IsHero && !x.Source.IsAviation))
                {
                    if (ProfileValue(donor.CurrentProfile) <= ProfileValue(displaced.Profile)) continue;
                    var projected = roster.Select(x => x.Profile).ToList();
                    projected[roster.IndexOf(displaced)] = donor.CurrentProfile;
                    float after = Win(projected, defenders, out _);
                    int transferAp = JoinActivationAp(primary, donor)
                        + JoinActivationAp(donorArmy, displaced.Source);
                    var action = new RaidRefitAction
                    {
                        Kind = RaidRefitActionKind.SwapUnit,
                        PrimaryArmyId = primary.ArmyId,
                        DonorArmyId = donorArmy.ArmyId,
                        UnitRuntimeId = donor.RuntimeId,
                        DisplacedUnitRuntimeId = displaced.Source.RuntimeId,
                        BaseHex = baseHex,
                        ApCost = transferAp,
                        ResourceCost = ResourceVector.Zero,
                        WinChanceBefore = winBefore,
                        WinChanceAfter = after,
                    };
                    result.Add(new Candidate(action, after - winBefore, false,
                        donor.FullCombatValue, donor.UnitIndex, donorArmy.ArmyId));
                }
            }
            return result;
        }

        private static void Apply(RaidRefitAction action, List<SimMember> roster,
            List<(ArmySnapshot Army, RaidRecoveryMemberSnapshot Member)> donors,
            HashSet<int> usedDonors)
        {
            if (action.Kind == RaidRefitActionKind.RepairUnit)
            {
                SimMember unit = roster.First(x => x.Source.RuntimeId == action.UnitRuntimeId);
                unit.Profile = unit.Source.FullHealthProfile;
                return;
            }
            var donor = donors.First(x => x.Member.RuntimeId == action.UnitRuntimeId);
            usedDonors.Add(donor.Member.RuntimeId);
            var incoming = new SimMember { Source = donor.Member, Profile = donor.Member.CurrentProfile };
            if (action.Kind == RaidRefitActionKind.TransferUnit)
                roster.Add(incoming);
            else
            {
                int index = roster.FindIndex(x =>
                    x.Source.RuntimeId == action.DisplacedUnitRuntimeId);
                roster[index] = incoming;
            }
        }

        private static int Compare(RaidRecoveryProjection a, RaidRecoveryProjection b)
        {
            int c = a.EtaTurns.CompareTo(b.EtaTurns); if (c != 0) return c;
            c = a.ApCost.CompareTo(b.ApCost); if (c != 0) return c;
            c = PhysicalMagnitude(a.ResourceCost).CompareTo(PhysicalMagnitude(b.ResourceCost));
            if (c != 0) return c;
            c = a.BlockedActors.CompareTo(b.BlockedActors); if (c != 0) return c;
            c = b.ProjectedWinChance.CompareTo(a.ProjectedWinChance); if (c != 0) return c;
            c = Nullable.Compare(a.SupportArmyId, b.SupportArmyId); if (c != 0) return c;
            if (a.BaseHex.HasValue && b.BaseHex.HasValue)
            {
                c = a.BaseHex.Value.Q.CompareTo(b.BaseHex.Value.Q); if (c != 0) return c;
                c = a.BaseHex.Value.R.CompareTo(b.BaseHex.Value.R); if (c != 0) return c;
            }
            return 0;
        }

        private static List<WorthIt.DefenderProfile> CombatRoster(ArmySnapshot army) =>
            (army?.RecoveryMembers ?? Array.Empty<RaidRecoveryMemberSnapshot>())
            .Where(m => !m.IsHero && !m.IsAviation).Select(m => m.CurrentProfile).ToList();

        private static float Win(IReadOnlyList<WorthIt.DefenderProfile> roster,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, out bool cover)
        {
            GroundCombatFeasibility.Clears(roster, defenders,
                AiConfigV2.raidMinViableWinChance, out float win, out cover);
            return win;
        }

        internal static int JoinActivationAp(ArmySnapshot target,
            RaidRecoveryMemberSnapshot incoming)
        {
            if (target == null || !target.HasActivatedThisTurn)
                return 0;
            IReadOnlyCollection<int> covered = target.ActivationCoveredUnitRuntimeIds
                ?? Array.Empty<int>();
            return covered.Contains(incoming.RuntimeId) ? 0 : incoming.ActivationApCost;
        }

        private static int RouteCost(HexCoord from, HexCoord to, int maxMovement,
            Func<HexCoord, HexCoord, int, int> safeRouteCost, int fallbackDistance)
        {
            if (from.Equals(to)) return 0;
            return safeRouteCost != null
                ? safeRouteCost(from, to, Math.Max(1, maxMovement))
                : fallbackDistance;
        }

        private static int CeilTurns(ArmySnapshot army, int distance)
        {
            int remaining = Math.Max(0, army.CurrentMovement);
            int move = Math.Max(1, army.MaxMovement);
            return distance <= remaining ? 1 : 1 + (distance - remaining + move - 1) / move;
        }

        private static bool Covers(ResourceBundle stock, ResourceVector cost) =>
            stock.Human + 0.001f >= cost.Human && stock.Energy + 0.001f >= cost.Energy
            && stock.Materials + 0.001f >= cost.Materials && stock.Tech + 0.001f >= cost.Tech;

        private static float PhysicalMagnitude(ResourceVector v) =>
            v.Human + v.Energy + v.Materials + v.Tech;

        private static float ProfileValue(WorthIt.DefenderProfile p) =>
            p.Attack + p.Defense + p.HitPoints + 0.25f * p.Initiative;
    }
}
