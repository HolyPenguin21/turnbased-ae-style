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
        // `includeEveryGroup` — every own hex, not only the ones worth a day pass (the night fold
        // reads hexes the day planner has nothing to change on).
        public static ArmyReorgAnalysis Analyze(PlayerSetupData player, ActorCommitments commitments,
            WorldSnapshot snapshot, AiTurnContext ctx, bool includeEveryGroup = false)
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

                foreach (ReorgContainer c in containers)
                {
                    ArmyData army = armyById[c.ArmyId];
                    foreach (ReorgContainer other in containers)
                        foreach (ReorgUnit u in other.Units)
                            if (army.HasActivationCoverageFor(unitByKey[u.Key]))
                                c.ActivationCoveredUnitKeys.Add(u.Key);
                }

                var groupHex = new HexCoord(hexGroup.Key.Q, hexGroup.Key.R);
                MarkDevelopmentOperators(player, groupHex, containers, unitByKey);
                MarkFist(player, containers, armyById);
                var lfg = new LocalForceGroup
                {
                    Q = hexGroup.Key.Q,
                    R = hexGroup.Key.R,
                    HexDefenseBonus = WorthIt.HexDefenseBonus(groupHex, ctx?.Map, player),
                    // The building part of that same total (its own source, weather-aware).
                    BuildingDefenseBonus = Game.Combat.BattleEnvironment.At(groupHex, ctx?.Map).HexDefense(
                        Game.Combat.DisruptionRules.KnownBuildingDefenseFor(groupHex, player, null)),
                    Environment = Game.Combat.BattleEnvironment.At(groupHex, ctx?.Map),
                    Containers = containers,
                    ThreatBenchmarks = BuildThreatBenchmarks(snapshot, groupHex, containers, unitByKey),
                };
                if (includeEveryGroup || lfg.WorthPlanning())
                    groups.Add(lfg);
            }

            return new ArmyReorgAnalysis { Groups = groups, UnitByKey = unitByKey, ArmyById = armyById };
        }

        private static ReorgContainer BuildContainer(PlayerSetupData player, int turn, ArmyData army,
            ActorCommitments commitments,
            HexCoord citadelHex, Dictionary<int, UnitData> unitByKey, ref int nextKey)
        {
            // Preserve the canonical live roster order. ArmyData.ComputeCapacity uses the FIRST hero,
            // and a field army's AddMemberSorted preserves hero insertion order (a garrison is
            // arranged by CommandRating); re-sorting heroes by power here can therefore make the
            // pure planner disagree with gameplay capacity.
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
                    MoveCurrent = AviationRules.EffectiveMoveCurrent(u),
                    MoveMax = u.MoveMax,
                    NativeMoveMax = u.PermanentMoveMax,
                    MovementSpent = u.MovementSpent,
                    RuntimeIdentity = u.RuntimeId,
                    TransportCarrierIdentity = u.TransportCarrier?.RuntimeId ?? -1,
                    HasRecce = AbilityParams.UnitHasAnyRecce(u),
                    IsAviation = u.IsAviation,
                    IsCommitted = false,
                    StrikeKey = StrikeRoster.UnitKey(u),
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
            container.CanReorderCommander = container.CanChangeComposition;

            // T05 — a claimed operation protects its FUNCTION, not a frozen roster: its contract
            // (ActorCommitments, the one claim authority) says whether free members may join,
            // whether its commander may be promoted and whether its route speed is binding.
            if (protectedOwner)
            {
                ArmyMutationContract contract = MutationContractFor(player, turn, army, commitments)
                    ?? ArmyMutationContract.FullyProtected;
                container.MissionLabel = contract.Label;
                container.IsMissionReceiver = contract.MayReceive;
                container.NeedsUnits = contract.NeedsUnits;
                container.CanReceive = contract.MayReceive;
                container.CanReorderCommander = contract.MayReorderCommander;
                container.MayReleaseExcessHeroes = contract.MayReleaseExcessHeroes;
                if (contract.MayReleaseExcessHeroes)
                {
                    IReadOnlyList<string> target = contract.TargetRosterKeys(army);
                    if (target != null && target.Count > 0)
                    {
                        container.PreparationTargetKeys = target.ToList();
                        container.PreparationHandKeys = contract.ReservedDeploymentKeys(army)?.ToList();
                    }
                }
                if (contract.KeepsMovement && army.Members.Count > 0)
                {
                    container.MovementFloor = army.CurrentMovement;
                    container.MaxMovementFloor = army.MaxMovement;
                }
            }

            // Empty reusable field containers are not permanently singleton-exempt: once the
            // virtual plan fills one, it is an ordinary field formation and must satisfy the same
            // structural rules as every other field army. A mission receiver is scored like one
            // too (it can only be fixed by inbound members).
            container.SingletonExempt = container.Role != ReorgPhysicalRole.NormalFieldArmy
                && container.Role != ReorgPhysicalRole.EmptyReusableArmy
                && !container.IsMissionReceiver;

            if (army.IsGarrison)
            {
                bool isCitadel = army.Hex.Equals(citadelHex);
                // 2026-09-30 — one body always stays; the defence itself is a power floor read
                // only when a body leaves (a small shortfall bought for a strong attacker is
                // legal, so it is never a deficit Housekeeping refills).
                container.GarrisonNonHeroFloor = 1;
                container.GarrisonPowerFloor = AiArmyRoles.GarrisonDefenceFloor(player, army.Hex, isCitadel);
                container.GarrisonBaseFloor = AiArmyRoles.GarrisonFloorBeforeResidents(player, army.Hex, isCitadel);
                container.GarrisonOwnShareFloor = AiArmyRoles.GarrisonShareFloor(player, isCitadel);
            }
            else
            {
                container.IsHexResident = AiArmyRoles.IsHexResidentArmy(player, army);
            }

            return container;
        }

        // 2026-11-26 (owner) — the hex's fist, on a hex with an own garrison: a free field army
        // (FistMusterObjectiveEvaluator.IsLiveFistContainer) led by a hero with fighters, the
        // widest command first, then the strongest (FistHost's order).
        private static void MarkFist(PlayerSetupData player, List<ReorgContainer> containers,
            Dictionary<int, ArmyData> armyById)
        {
            if (!containers.Any(c => c.IsGarrison))
                return;
            ReorgContainer fist = containers
                .Where(c => c.Role == ReorgPhysicalRole.NormalFieldArmy
                    && c.Units.Any(u => u.IsHero) && c.Units.Any(u => u.IsGroundCombatant)
                    && FistMusterObjectiveEvaluator.IsLiveFistContainer(player, armyById[c.ArmyId]))
                .OrderByDescending(c => ReorgViability.Capacity(c.Units, false))
                .ThenByDescending(c => ReorgViability.EffectivePower(c.Units))
                .ThenBy(c => c.ArmyId)
                .FirstOrDefault();
            if (fist == null)
                return;
            fist.IsFist = true;
            ReorgContainer g = containers.First(c => c.IsGarrison);
            List<ReorgUnit> bodies = g.Units.Where(u => u != null && u.IsGroundCombatant).ToList();
            fist.FistGarrisonSpare = AiArmyRoles.SpareableBodies(bodies,
                set => ReorgViability.EffectivePower(set.ToList()),
                System.Math.Max(g.GarrisonPowerFloor, g.GarrisonOwnShareFloor)).Count;
        }

        // Select the minimum set of on-hex heroes that keeps every currently installed
        // Research/Production facility operable. Ability/facility compatibility stays owned by
        // ResearchProductionSystem; Housekeeping only marks the contextual cards it must protect.
        private static void MarkDevelopmentOperators(PlayerSetupData player, HexCoord hex,
            IReadOnlyList<ReorgContainer> containers, IReadOnlyDictionary<int, UnitData> unitByKey)
        {
            var actorsByMode = new Dictionary<ResearchProductionMode, HashSet<UnitData>>();
            foreach (ResearchProductionMode mode in AiArmyRoles.FacilityModes(player, hex))
                actorsByMode[mode] = new HashSet<UnitData>(ResearchProductionSystem.FindActors(player, hex, mode));
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

            // Owner decision 2026-11-05: a hidden enemy (fog/stealth cheat) only benchmarks the
            // Citadel's own group, so its garrison is never packaged as an easy win; anywhere
            // else Housekeeping packages against honestly seen enemies only. It never creates a
            // threat or blocks a mission either way (Housekeeping owns neither).
            // The starting Citadel only, while held: Self.Citadel falls back to another base once
            // it is lost (and to a default hex with no base at all).
            bool citadelGroup = snapshot.Self != null && snapshot.Self.HoldsStartingCitadel
                && groupHex.Equals(snapshot.Self.Citadel);
            foreach (ArmySnapshot enemy in enemies.OrderBy(a => a?.ArmyId ?? int.MaxValue))
            {
                if (!HeroRoleEvaluator.IsCommandBenchmark(enemy))
                    continue;
                if (enemy.IsHiddenFromUs && !citadelGroup)
                    continue;

                int move = Mathf.Max(1, enemy.MaxMovement);
                int groupEta = AiV2Util.CeilDiv(HexGridMath.Distance(enemy.Hex, groupHex), move);
                int baseEta = groupEta;
                if (bases.Count > 0)
                    baseEta = bases.Min(b => AiV2Util.CeilDiv(HexGridMath.Distance(enemy.Hex, b), move));

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

        // T05 — the claim's contract, intersected with a strategic capability lease (a leased,
        // unclaimed army may be reinforced but never taken apart). Null for a free army.
        // Same precedence as ClassifyRole: a garrison is a garrison whatever leased it (a card
        // played into it), and a leased scout / collector keeps its solo shape (no inbound).
        internal static ArmyMutationContract MutationContractFor(PlayerSetupData player, int turn,
            ArmyData army, ActorCommitments commitments)
        {
            ArmyMutationContract contract = MissionActorPolicy.PhysicalMutationContract(player, turn, army, commitments);
            return contract?.ProtectsSoloRole == true ? null : contract;
        }

        private static ReorgPhysicalRole ClassifyRole(PlayerSetupData player, int turn, ArmyData army,
            ActorCommitments commitments)
        {
            if (army.IsGarrison)
                return ReorgPhysicalRole.Garrison;
            if (army.IsPrison)
                return ReorgPhysicalRole.SpecialExcludedContainer;
            if (AviationRules.IsAviationContainer(army))
                return ReorgPhysicalRole.Aviation;
            ArmyMutationContract protection = MissionActorPolicy.PhysicalMutationContract(player, turn, army, commitments);
            if (protection != null && !protection.ProtectsSoloRole)
                return ReorgPhysicalRole.ProtectedMissionArmy;
            // §P1 — the SoloRecce role protects a scout only while it actually has recon WORK: a
            // live ReconPatrolState (claim/lease already returned ProtectedMissionArmy above). An
            // idle scout-shaped army with no assignment is just an ordinary singleton the zero-AP
            // reorg pass may fold or reuse.
            if (AiArmyRoles.IsSoloRecce(army) && protection?.ProtectsSoloRole == true)
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
