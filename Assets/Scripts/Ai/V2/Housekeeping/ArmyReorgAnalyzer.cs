using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // Same-turn ownership for operational capability created by StrategicManager. A force that was
    // materialized to satisfy a still-live strategic shortage must not be folded into garrison by
    // the later zero-AP housekeeping pass before that capability can be used. The lease is deliberately
    // turn-local: HousekeepingManager clears it after the final structural pass.
    internal static class StrategicCapabilityLeaseRegistry
    {
        private sealed class LeaseState
        {
            public int Turn;
            public readonly HashSet<int> ArmyIds = new HashSet<int>();
        }

        private static readonly Dictionary<PlayerSetupData, LeaseState> ByPlayer =
            new Dictionary<PlayerSetupData, LeaseState>();

        public static void Mark(PlayerSetupData player, int turn, CapabilityKind capability, IEnumerable<int> armyIds)
        {
            if (player == null || armyIds == null)
                return;
            if (!ByPlayer.TryGetValue(player, out LeaseState state) || state.Turn != turn)
                ByPlayer[player] = state = new LeaseState { Turn = turn };

            var added = new List<int>();
            foreach (int id in armyIds.Where(id => id >= 0).Distinct())
                if (state.ArmyIds.Add(id))
                    added.Add(id);

            if (added.Count > 0)
                AiDebugLog.Write($"[AI][V2][Lease] protect operational {capability} army(s) "
                    + $"[{string.Join(",", added)}] through housekeeping (turn {turn})");
        }

        // The turn gate is part of the contract, not a convenience: Mark() rebuilds the state when
        // the turn moves and Clear() only removes a state stamped with the turn being cleared, so a
        // reader without the same gate can observe LAST turn's lease set. That matters because the
        // readers are Phase A (MaterializationCandidateBuilder, at the START of a turn, before this
        // turn's first Mark) and Housekeeping — a stale lease would silently protect an army that
        // owns no operational role any more. Today HousekeepingManager.Clear happens to run at the
        // end of every turn; this makes the invariant structural instead of a consequence of that
        // call order.
        public static bool IsLeased(PlayerSetupData player, int turn, int armyId) =>
            player != null && ByPlayer.TryGetValue(player, out LeaseState state)
            && state.Turn == turn && state.ArmyIds.Contains(armyId);

        public static void Clear(PlayerSetupData player, int turn)
        {
            if (player != null && ByPlayer.TryGetValue(player, out LeaseState state) && state.Turn == turn)
                ByPlayer.Remove(player);
        }

        public static void ClearAll() => ByPlayer.Clear();
    }

    // ===========================================================================================
    //  ARMY REORG ANALYZER  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  LIVE post-Phase-B world -> immutable LocalForceGroup projections + executor back-maps.
    //  Reuses canonical signals instead of inventing a second source of truth:
    //    · role/protection   — AiArmyRoles / AviationRules / ActorCommitments / strategic leases
    //    · garrison safety   — AiConfig secure* floors, rechecked by AiArmyRoles at execution
    //    · strength/compo    — AiPower.PowerUnit from final live UnitData (Equipment already applied)
    //    · capacity ordering — the live ArmyData.Members order, because FIRST hero CommandRating wins
    //    · AP legality       — HasActivatedThisTurn + each member's effective ActivationApCost
    // ===========================================================================================
    public sealed class ArmyReorgAnalysis
    {
        public IReadOnlyList<LocalForceGroup> Groups;
        public IReadOnlyDictionary<int, UnitData> UnitByKey;
        public IReadOnlyDictionary<int, ArmyData> ArmyById;
    }

    public static class ArmyReorgAnalyzer
    {
        public static ArmyReorgAnalysis Analyze(PlayerSetupData player, ActorCommitments commitments,
            WorldSnapshot snapshot, AiTurnContext ctx)
        {
            var unitByKey = new Dictionary<int, UnitData>();
            var armyById = new Dictionary<int, ArmyData>();
            var groups = new List<LocalForceGroup>();
            if (player == null)
                return new ArmyReorgAnalysis { Groups = groups, UnitByKey = unitByKey, ArmyById = armyById };

            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            int nextKey = 0;
            // The turn this analysis belongs to — the strategic capability lease is turn-local and
            // is read below through the same gate Mark/Clear use.
            int turn = snapshot?.TurnNumber ?? ctx?.TurnNumber ?? 0;

            var byHex = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null)
                .GroupBy(a => (a.Hex.Q, a.Hex.R))
                .OrderBy(g => g.Key.Q).ThenBy(g => g.Key.R);

            foreach (var hexGroup in byHex)
            {
                var containers = new List<ReorgContainer>();
                foreach (ArmyData army in hexGroup.OrderBy(a => a.Id))
                {
                    armyById[army.Id] = army;
                    containers.Add(BuildContainer(player, turn, army, commitments, citadelHex, unitByKey, ref nextKey));
                }

                var groupHex = new HexCoord(hexGroup.Key.Q, hexGroup.Key.R);
                MarkDevelopmentOperators(player, groupHex, containers, unitByKey);
                var lfg = new LocalForceGroup
                {
                    Q = hexGroup.Key.Q,
                    R = hexGroup.Key.R,
                    HexDefenseBonus = WorthIt.HexDefenseBonus(groupHex, ctx?.Map),
                    Containers = containers,
                    ThreatBenchmarks = BuildThreatBenchmarks(snapshot, groupHex, containers, unitByKey),
                };
                if (lfg.WorthPlanning())
                    groups.Add(lfg);
            }

            return new ArmyReorgAnalysis { Groups = groups, UnitByKey = unitByKey, ArmyById = armyById };
        }

        private static ReorgContainer BuildContainer(PlayerSetupData player, int turn, ArmyData army,
            ActorCommitments commitments,
            HexCoord citadelHex, Dictionary<int, UnitData> unitByKey, ref int nextKey)
        {
            // Preserve the canonical live roster order. ArmyData.ComputeCapacity uses the FIRST hero,
            // and AddMemberSorted deliberately preserves hero insertion order; re-sorting heroes by
            // power here can therefore make the pure planner disagree with gameplay capacity.
            var container = new ReorgContainer
            {
                ArmyId = army.Id,
                IsGarrison = army.IsGarrison,
                HasActivatedThisTurn = army.HasActivatedThisTurn,
            };

            foreach (UnitData u in army.Members)
            {
                int key = nextKey++;
                unitByKey[key] = u;
                AiPower.PowerUnit pu = AiPower.ToPowerUnit(u);
                container.Units.Add(new ReorgUnit
                {
                    Key = key,
                    IsHero = u.IsHero,
                    IsGroundCombatant = u.IsGroundCombatant,
                    IsGroundBattleBody = AiArmyRoles.IsGroundBattleBody(u),
                    CommandRating = u.CommandRating,
                    HeroCombatLeadership = u.IsHero ? HeroRoleEvaluator.CombatLeadershipScore(u) : 0f,
                    HeroRole = u.IsHero ? HeroRoleEvaluator.Classify(u) : HeroOperationalRole.Flexible,
                    Power = pu.BasePower,
                    Range = pu.Range,
                    TypeTags = pu.Tags.ToList(),
                    ActivationApCost = u.ActivationApCost,
                    HasRecce = AbilityParams.UnitHasAnyRecce(u),
                    IsAviation = u.IsAviation,
                    IsCommitted = false,
                    CombatProfile = WorthIt.FromLiveUnit(u),
                    AsCommander = u.IsHero ? WorthIt.SideCommander.Of(u) : default,
                });
            }

            container.Role = ClassifyRole(player, turn, army, commitments);

            bool protectedOwner = container.Role == ReorgPhysicalRole.ProtectedMissionArmy;
            bool mutable = container.Role == ReorgPhysicalRole.NormalFieldArmy
                || container.Role == ReorgPhysicalRole.EmptyReusableArmy
                || container.Role == ReorgPhysicalRole.Garrison;

            container.CanChangeComposition = mutable && !protectedOwner;
            container.CanReceive = mutable && !protectedOwner;
            container.CanDonate = container.CanChangeComposition
                && (container.Role == ReorgPhysicalRole.NormalFieldArmy || container.Role == ReorgPhysicalRole.Garrison);

            // Empty reusable field containers are not permanently singleton-exempt: once the
            // virtual plan fills one, it is an ordinary field formation and must satisfy the same
            // structural rules as every other field army.
            container.SingletonExempt = container.Role != ReorgPhysicalRole.NormalFieldArmy
                && container.Role != ReorgPhysicalRole.EmptyReusableArmy;

            if (army.IsGarrison)
            {
                bool isCitadel = army.Hex.Equals(citadelHex);
                container.GarrisonNonHeroFloor = isCitadel
                    ? AiConfig.secureCitadelMinNonHeroUnits
                    : AiConfig.secureBaseMinNonHeroUnits;
            }

            return container;
        }

        // Select the minimum set of on-hex heroes that keeps every currently installed
        // Research/Production facility operable. Ability/facility compatibility stays owned by
        // ResearchProductionSystem; Housekeeping only marks the contextual cards it must protect.
        private static void MarkDevelopmentOperators(PlayerSetupData player, HexCoord hex,
            IReadOnlyList<ReorgContainer> containers, IReadOnlyDictionary<int, UnitData> unitByKey)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner != player)
                return;

            var actorsByMode = new Dictionary<ResearchProductionMode, HashSet<UnitData>>();
            foreach (ResearchProductionMode mode in new[]
                     { ResearchProductionMode.Research, ResearchProductionMode.Production })
            {
                if (!building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode)))
                    continue;
                actorsByMode[mode] = new HashSet<UnitData>(
                    ResearchProductionSystem.FindActors(player, hex, mode));
            }
            if (actorsByMode.Count == 0)
                return;

            var remaining = new HashSet<ResearchProductionMode>(actorsByMode.Keys);
            var candidates = containers.SelectMany(c => c.Units)
                .Where(u => u != null && u.IsHero && unitByKey.ContainsKey(u.Key))
                .ToList();

            while (remaining.Count > 0)
            {
                ReorgUnit selected = candidates
                    .Where(u => remaining.Any(mode => actorsByMode[mode].Contains(unitByKey[u.Key])))
                    .OrderByDescending(u => remaining.Count(
                        mode => actorsByMode[mode].Contains(unitByKey[u.Key])))
                    .ThenBy(u => u.HeroRole == HeroOperationalRole.SupportOperator ? 0
                        : u.HeroRole == HeroOperationalRole.Flexible ? 1 : 2)
                    .ThenBy(u => u.HeroCombatLeadership)
                    .ThenBy(u => u.Key)
                    .FirstOrDefault();
                if (selected == null)
                    break;

                selected.IsDevelopmentOperator = true;
                UnitData live = unitByKey[selected.Key];
                remaining.RemoveWhere(mode => actorsByMode[mode].Contains(live));
                candidates.Remove(selected);
            }
        }

        private static List<ReorgThreatBenchmark> BuildThreatBenchmarks(
            WorldSnapshot snapshot, HexCoord groupHex, IReadOnlyList<ReorgContainer> containers,
            IReadOnlyDictionary<int, UnitData> unitByKey)
        {
            var result = new List<ReorgThreatBenchmark>();
            IReadOnlyList<ArmySnapshot> enemies = snapshot?.TrueWorld?.EnemyArmies;
            if (enemies == null)
                return result;

            IReadOnlyList<HexCoord> bases = snapshot.Self?.BaseHexes
                ?? (IReadOnlyList<HexCoord>)System.Array.Empty<HexCoord>();

            // §Task4 — every friendly non-hero unit currently on this hex, keyed the same way the
            // planner's virtual roster keeps them. Visibility is evaluated once here, off the live
            // roster, and travels with the Key through every later virtual transfer/swap.
            List<ReorgUnit> nonHeroFriendlies = containers.SelectMany(c => c.Units)
                .Where(u => u != null && u.IsGroundCombatant).ToList();

            foreach (ArmySnapshot enemy in enemies.OrderBy(a => a?.ArmyId ?? int.MaxValue))
            {
                if (!HeroRoleEvaluator.IsCommandBenchmark(enemy))
                    continue;

                int move = Mathf.Max(1, enemy.MaxMovement);
                int groupEta = CeilDiv(HexGridMath.Distance(enemy.Hex, groupHex), move);
                int baseEta = groupEta;
                if (bases.Count > 0)
                    baseEta = bases.Min(b => CeilDiv(HexGridMath.Distance(enemy.Hex, b), move));

                var targetable = new HashSet<int>();
                foreach (ReorgUnit u in nonHeroFriendlies)
                    if (unitByKey.TryGetValue(u.Key, out UnitData live) && live != null
                        && !live.IsHidden)
                        targetable.Add(u.Key);

                result.Add(new ReorgThreatBenchmark
                {
                    ArmyId = enemy.ArmyId,
                    HiddenFromUs = enemy.IsHiddenFromUs,
                    EtaToGroup = groupEta,
                    EtaToNearestBase = baseEta,
                    Members = enemy.Members,
                    Commander = enemy.Commander,
                    TargetableUnitKeys = targetable,
                });
            }
            return result;
        }

        private static int CeilDiv(int value, int divisor) => AiV2Util.CeilDiv(value, divisor);

        private static ReorgPhysicalRole ClassifyRole(PlayerSetupData player, int turn, ArmyData army,
            ActorCommitments commitments)
        {
            if (army.IsGarrison)
                return ReorgPhysicalRole.Garrison;
            if (army.IsPrison)
                return ReorgPhysicalRole.SpecialExcludedContainer;
            if (AviationRules.IsAirfield(army) || AviationRules.IsAirArmy(army))
                return ReorgPhysicalRole.Aviation;
            if ((commitments != null && commitments.IsArmyClaimed(army.Id))
                || StrategicCapabilityLeaseRegistry.IsLeased(player, turn, army.Id))
                return ReorgPhysicalRole.ProtectedMissionArmy;
            // §P1 — the SoloRecce role protects a scout only while it actually has recon WORK: a
            // live ReconPatrolState (claim/lease already returned ProtectedMissionArmy above). An
            // idle scout-shaped army with no assignment is just an ordinary singleton the zero-AP
            // reorg pass may fold or reuse.
            if (AiArmyRoles.IsSoloRecce(army)
                && ReconPatrolStateRegistry.TryGet(player, army.Id, out _))
                return ReorgPhysicalRole.SoloRecce;
            // A solo collector belongs to itself, same call as SoloRecce above — it is never
            // folded into another army by zero-AP reorg. Unlike SoloRecce there is no separate
            // "still has work" registry to gate on: once it is this shape at all, housekeeping
            // leaves it alone (its own MobileCollection MissionIntent claim already protects it
            // via ProtectedMissionArmy while actively travelling/assigned; this covers the gap
            // once it is parked and passively earning, when that claim may no longer be live).
            if (AiArmyRoles.IsSoloCollector(army))
                return ReorgPhysicalRole.SoloCollector;
            if (army.Members.Count == 0)
                return ReorgPhysicalRole.EmptyReusableArmy;
            return ReorgPhysicalRole.NormalFieldArmy;
        }
    }
}
