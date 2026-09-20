#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiRaidRecoveryTests
    {
        [Test]
        public void BaseRecovery_ChoosesExactRepairThatCrossesRaidThreshold()
        {
            var player = new PlayerSetupData { Nickname = "Repair" };
            HexCoord home = new HexCoord(0, 0);
            WorthIt.DefenderProfile wounded = Profile(attack: 20, defense: 0, hp: 1,
                maxHp: 10, initiative: 1);
            WorthIt.DefenderProfile healthy = Profile(attack: 20, defense: 0, hp: 10,
                maxHp: 10, initiative: 1);
            WorthIt.DefenderProfile defender = Profile(attack: 4, defense: 0, hp: 5,
                maxHp: 5, initiative: 10);
            ArmySnapshot primary = Army(0, player, home, capacity: 1,
                Member(101, 0, wounded, healthy, repairable: true,
                    repairCost: new ResourceVector(0, 1, 0, 0, 0)));
            WorldSnapshot snap = Snapshot(primary, human: 3);
            var raid = Raid(primary.ArmyId, new HexCoord(2, 0));
            float before = WorthIt.WinChance(new[] { wounded }, new[] { defender }, 0f);

            RaidRecoveryProjection plan = RaidRecoveryPlanner.ProjectBase(snap, raid,
                primary, new[] { defender }, new HashSet<int>(), before, home);

            Assert.That(before, Is.LessThan(AiConfigV2.raidMinViableWinChance),
                "fixture must start below the existing raid gate");
            Assert.That(plan.Viable, Is.True);
            Assert.That(plan.Phase, Is.EqualTo(RaidMissionPhase.Refit));
            Assert.That(plan.FirstRefitAction.Kind, Is.EqualTo(RaidRefitActionKind.RepairUnit));
            Assert.That(plan.FirstRefitAction.UnitRuntimeId, Is.EqualTo(101));
            Assert.That(plan.ProjectedWinChance, Is.GreaterThanOrEqualTo(
                AiConfigV2.raidMinViableWinChance));
        }

        [Test]
        public void BaseRecovery_ScoreIsCanonicalTaskScoreFold()
        {
            var player = new PlayerSetupData { Nickname = "Canonical score" };
            HexCoord home = new HexCoord(0, 0);
            WorthIt.DefenderProfile wounded = Profile(20, 0, 1, 10, 1);
            WorthIt.DefenderProfile healthy = Profile(20, 0, 10, 10, 1);
            WorthIt.DefenderProfile defender = Profile(4, 0, 5, 5, 10);
            ArmySnapshot primary = Army(3, player, home, 1,
                Member(111, 0, wounded, healthy, repairable: true,
                    repairCost: new ResourceVector(0, 1, 0, 0, 0)));
            primary.ActivationApCost = 2;
            WorldSnapshot snap = Snapshot(primary, human: 3);
            var raid = Raid(primary.ArmyId, new HexCoord(2, 0));
            float before = WorthIt.WinChance(new[] { wounded }, new[] { defender }, 0f);

            RaidRecoveryProjection plan = RaidRecoveryPlanner.ProjectBase(snap, raid,
                primary, new[] { defender }, new HashSet<int>(), before, home);

            var expected = new TaskScore(
                winChance: TaskScoreEvaluator.WinChance(plan.ProjectedWinChance),
                cardPrice: TaskScoreEvaluator.CardPrice(plan.ApCost,
                    plan.ResourceCost.Human + plan.ResourceCost.Energy
                    + plan.ResourceCost.Materials + plan.ResourceCost.Tech),
                delivery: 0f,
                moverOpportunityCost: System.Math.Max(0, plan.BlockedActors - 1));
            Assert.That(plan.Score.Delivery, Is.Zero,
                "atomic refit AP is CardPrice, not a fabricated extra travel turn");
            Assert.That(plan.Score.Value, Is.EqualTo(expected.Value).Within(0.0001f));
        }

        [Test]
        public void BaseRecovery_FullRosterChoosesSwapWhenRepairCannotHelp()
        {
            var player = new PlayerSetupData { Nickname = "Swap" };
            HexCoord home = new HexCoord(0, 0);
            WorthIt.DefenderProfile weak = Profile(2, 0, 1, 2, 1);
            WorthIt.DefenderProfile strong = Profile(20, 0, 10, 10, 1);
            WorthIt.DefenderProfile defender = Profile(4, 0, 5, 5, 10);
            ArmySnapshot primary = Army(7, player, home, 1,
                Member(201, 0, weak, weak, repairable: false));
            ArmySnapshot donor = Army(8, player, home, 2,
                Member(301, 0, strong, strong, repairable: false, canSpare: true));
            donor.MemberCount = 2; // one retained body is represented only by the aggregate count
            WorldSnapshot snap = Snapshot(new[] { primary, donor }, human: 0);
            var raid = Raid(primary.ArmyId, new HexCoord(2, 0));
            float before = WorthIt.WinChance(new[] { weak }, new[] { defender }, 0f);

            RaidRecoveryProjection plan = RaidRecoveryPlanner.ProjectBase(snap, raid,
                primary, new[] { defender }, new HashSet<int>(), before, home);

            Assert.That(plan.Viable, Is.True);
            Assert.That(plan.FirstRefitAction.Kind, Is.EqualTo(RaidRefitActionKind.SwapUnit));
            Assert.That(plan.FirstRefitAction.DonorArmyId, Is.EqualTo(8));
            Assert.That(plan.FirstRefitAction.UnitRuntimeId, Is.EqualTo(301));
            Assert.That(plan.FirstRefitAction.DisplacedUnitRuntimeId, Is.EqualTo(201));
        }

        [Test]
        public void RefitTransferAp_UsesIncomingUnitAndExactTargetCoverage()
        {
            var player = new PlayerSetupData { Nickname = "Activation" };
            ArmySnapshot primary = Army(1, player, new HexCoord(0, 0), 2,
                Member(101, 0, Profile(2, 0, 2, 2, 1), Profile(2, 0, 2, 2, 1),
                    repairable: false));
            primary.HasActivatedThisTurn = true;
            RaidRecoveryMemberSnapshot incoming = Member(202, 0,
                Profile(8, 0, 5, 5, 1), Profile(8, 0, 5, 5, 1),
                repairable: false, canSpare: true, activationAp: 3);

            Assert.That(RaidRecoveryPlanner.JoinActivationAp(primary, incoming), Is.EqualTo(3),
                "the incoming unit's AP, not the donor army aggregate, is authoritative");

            primary.ActivationCoveredUnitRuntimeIds = new[] { 202 };
            Assert.That(RaidRecoveryPlanner.JoinActivationAp(primary, incoming), Is.Zero,
                "a same-turn rejoin already covered by this target army must not pay twice");
        }

        [Test]
        public void RecoveryChoice_SkipsStructurallyUnreachableFieldSupport()
        {
            var player = new PlayerSetupData { Nickname = "Route" };
            WorthIt.DefenderProfile weak = Profile(1, 0, 2, 2, 1);
            WorthIt.DefenderProfile strong = Profile(20, 0, 10, 10, 2);
            WorthIt.DefenderProfile defender = Profile(5, 0, 6, 6, 1);
            ArmySnapshot primary = Army(10, player, new HexCoord(0, 0), 2,
                Member(101, 0, weak, weak, repairable: false));
            ArmySnapshot support = Army(11, player, new HexCoord(2, 0), 2,
                Member(201, 0, strong, strong, repairable: false, canSpare: true));
            support.MemberCount = 2;
            WorldSnapshot snap = Snapshot(new[] { primary, support }, 0);
            snap.Self.BaseHexes = System.Array.Empty<HexCoord>();
            var raid = Raid(primary.ArmyId, new HexCoord(4, 0));

            RaidRecoveryProjection plan = RaidRecoveryPlanner.Choose(snap, raid,
                new HashSet<int>(), safeRouteCost: (from, to, maxMovement) => int.MaxValue);

            Assert.That(plan.Viable, Is.False,
                "geometrically close support is not viable without a safe structural route");
        }

        [Test]
        public void BaseRecovery_RejectsThresholdClearingRefitWhenTargetRouteIsDeadEnd()
        {
            var player = new PlayerSetupData { Nickname = "DeadEnd" };
            HexCoord home = new HexCoord(0, 0);
            HexCoord target = new HexCoord(3, 0);
            WorthIt.DefenderProfile wounded = Profile(20, 0, 1, 10, 1);
            WorthIt.DefenderProfile healthy = Profile(20, 0, 10, 10, 1);
            WorthIt.DefenderProfile defender = Profile(4, 0, 5, 5, 10);
            ArmySnapshot primary = Army(15, player, home, 1,
                Member(251, 0, wounded, healthy, repairable: true,
                    repairCost: new ResourceVector(0, 1, 0, 0, 0)));
            WorldSnapshot snap = Snapshot(primary, human: 3);
            var raid = Raid(primary.ArmyId, target);
            float before = WorthIt.WinChance(new[] { wounded }, new[] { defender }, 0f);

            RaidRecoveryProjection plan = RaidRecoveryPlanner.ProjectBase(snap, raid,
                primary, new[] { defender }, new HashSet<int>(), before, home,
                (from, to, maxMovement) => to.Equals(target)
                    ? int.MaxValue : HexGridMath.Distance(from, to));

            Assert.That(plan.Viable, Is.False,
                "repair alone is not a complete recovery plan without a safe return to the target");
        }

        [Test]
        public void RecoveryChoice_ConsidersAllBasesAndRequiresRouteBackToTarget()
        {
            var player = new PlayerSetupData { Nickname = "Bases" };
            HexCoord near = new HexCoord(0, 0);
            HexCoord useful = new HexCoord(2, 0);
            HexCoord target = new HexCoord(4, 0);
            WorthIt.DefenderProfile weak = Profile(1, 0, 2, 2, 1);
            WorthIt.DefenderProfile strong = Profile(20, 0, 10, 10, 2);
            WorthIt.DefenderProfile defender = Profile(5, 0, 6, 6, 1);
            ArmySnapshot primary = Army(20, player, near, 2,
                Member(301, 0, weak, weak, repairable: false));
            primary.ReachableOwnBaseHexes = new[] { near, useful };
            ArmySnapshot donor = Army(21, player, useful, 2,
                Member(401, 0, strong, strong, repairable: false, canSpare: true));
            donor.MemberCount = 2;
            WorldSnapshot snap = Snapshot(new[] { primary, donor }, 0);
            snap.Self.BaseHexes = new[] { near, useful };
            var raid = Raid(primary.ArmyId, target);

            int Route(HexCoord from, HexCoord to, int maxMovement)
            {
                if (from.Equals(near) && to.Equals(target))
                    return int.MaxValue;
                return HexGridMath.Distance(from, to);
            }

            RaidRecoveryProjection plan = RaidRecoveryPlanner.Choose(snap, raid,
                new HashSet<int>(), safeRouteCost: Route);

            Assert.That(plan.Viable, Is.True);
            Assert.That(plan.BaseHex, Is.EqualTo(useful),
                "the planner must reject the nearest dead-end base and select the complete plan");
            Assert.That(plan.FirstRefitAction.DonorArmyId, Is.EqualTo(21));
        }

        [Test]
        public void RefitStableKey_DistinguishesActionAndExactUnit()
        {
            RaidMissionTarget repair = Target(RaidRefitActionKind.RepairUnit, 11);
            RaidMissionTarget transfer = Target(RaidRefitActionKind.TransferUnit, 12);

            Assert.That(StableMissionKey.ForRaid(repair), Is.Not.EqualTo(
                StableMissionKey.ForRaid(transfer)));
        }

        [Test]
        public void RecoveryCommitments_ClaimArmyZeroAndFrozenDonor()
        {
            var player = new PlayerSetupData { Nickname = "Claims" };
            WorldSnapshot snap = Snapshot(new[]
            {
                new ArmySnapshot { ArmyId = 0, Owner = player, MemberCount = 1 },
                new ArmySnapshot { ArmyId = 2, Owner = player, MemberCount = 2 },
            }, 0);
            var intent = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Status = IntentStatus.Active,
                Objective = new RaidIntent
                {
                    Target = RaidTargetRef.ForNeutralArmy(9),
                    PrimaryArmyId = 0,
                    Phase = RaidMissionPhase.Refit,
                    PendingRefitAction = new RaidRefitAction
                    {
                        Kind = RaidRefitActionKind.TransferUnit,
                        PrimaryArmyId = 0,
                        DonorArmyId = 2,
                        UnitRuntimeId = 88,
                    },
                },
            };

            ActorCommitments claims = ActorCommitments.FromIntents(
                new[] { intent }, snap, null);

            Assert.That(claims.IsArmyClaimed(0), Is.True, "army id zero is a valid primary");
            Assert.That(claims.IsArmyClaimed(2), Is.True);
        }

        private static RaidMissionTarget Target(RaidRefitActionKind kind, int unitId) =>
            new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Refit,
                PrimaryArmyId = 0,
                Target = RaidTargetRef.ForNeutralArmy(5),
                DestinationHex = new HexCoord(1, 1),
                RefitAction = new RaidRefitAction
                {
                    Kind = kind,
                    PrimaryArmyId = 0,
                    UnitRuntimeId = unitId,
                    BaseHex = new HexCoord(1, 1),
                },
            };

        private static RaidIntent Raid(int primaryId, HexCoord target) => new RaidIntent
        {
            Target = RaidTargetRef.ForNeutralArmy(99),
            LastKnownHex = target,
            TargetIsNeutral = true,
            PrimaryArmyId = primaryId,
            OperationStarted = true,
        };

        private static WorldSnapshot Snapshot(ArmySnapshot army, float human) =>
            Snapshot(new[] { army }, human);

        private static WorldSnapshot Snapshot(IReadOnlyList<ArmySnapshot> armies, float human) =>
            new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Armies = armies,
                    BaseHexes = new[] { new HexCoord(0, 0) },
                    Stockpile = new ResourceBundle { Human = human },
                },
            };

        private static ArmySnapshot Army(int id, PlayerSetupData owner, HexCoord hex,
            int capacity, params RaidRecoveryMemberSnapshot[] members) => new ArmySnapshot
        {
            ArmyId = id,
            Owner = owner,
            Hex = hex,
            Capacity = capacity,
            MemberCount = members.Length,
            MaxMovement = 3,
            CurrentMovement = 3,
            IsStructuralRaidActor = true,
            RecoveryMembers = members,
            Members = new List<WorthIt.DefenderProfile>(),
        };

        private static RaidRecoveryMemberSnapshot Member(int id, int index,
            WorthIt.DefenderProfile current, WorthIt.DefenderProfile full,
            bool repairable, ResourceVector repairCost = default, bool canSpare = false,
            int activationAp = 0) =>
            new RaidRecoveryMemberSnapshot(id, index, false, false, canSpare, activationAp,
                current, full, repairable, repairCost);

        private static WorthIt.DefenderProfile Profile(float attack, float defense,
            float hp, float maxHp, int initiative) =>
            new WorthIt.DefenderProfile(defense, false, attack: attack,
                hitPoints: hp, maxHitPoints: maxHp, initiative: initiative);
    }
}
#endif
