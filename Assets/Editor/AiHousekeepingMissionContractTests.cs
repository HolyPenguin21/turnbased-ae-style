#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // T05 — Housekeeping protects a claimed operation's function, not a frozen roster: a mission
    // receiver takes free same-hex members, never gives any away, and a route-bound operation is
    // never slowed by an inbound member.
    public class AiHousekeepingMissionContractTests
    {
        private static int _key;

        private static ReorgUnit Body(float power, int move = 3) => new ReorgUnit
        {
            Key = _key++, Power = power, IsGroundCombatant = true, IsGroundBattleBody = true,
            MoveCurrent = move, MoveMax = move,
        };

        private static ReorgUnit Hero(float power, int command = 4) => new ReorgUnit
        {
            Key = _key++, IsHero = true, Power = power, CommandRating = command,
            IsGroundCombatant = true, HeroRole = HeroOperationalRole.CombatLeader,
            MoveCurrent = 3, MoveMax = 3,
        };

        private static ReorgContainer Garrison(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.Garrison, IsGarrison = true,
            CanReceive = true, CanDonate = true, CanChangeComposition = true,
            CanReorderCommander = true, SingletonExempt = true, Units = units.ToList(),
        };

        private static ReorgContainer Free(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.NormalFieldArmy,
            CanReceive = true, CanDonate = true, CanChangeComposition = true,
            CanReorderCommander = true, Units = units.ToList(),
        };

        // The container the analyzer builds for a claimed army from its ArmyMutationContract.
        private static ReorgContainer Mission(int id, bool receives, int movementFloor,
            params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.ProtectedMissionArmy, MissionLabel = "test",
            IsMissionReceiver = receives, CanReceive = receives, CanReorderCommander = receives,
            SingletonExempt = !receives, MovementFloor = movementFloor,
            MaxMovementFloor = movementFloor, Units = units.ToList(),
        };

        private static ReorganizationPlan Plan(params ReorgContainer[] containers) =>
            ArmyReorganizationPlanner.Plan(new LocalForceGroup
            {
                Containers = containers.ToList(),
            });

        [Test]
        public void HeroOnlyPreparationHost_ReceivesFreeBody_InsteadOfGarrisonDeposit()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1, Hero(3f));
            ReorgContainer free = Free(3, Body(8f));
            ReorganizationPlan plan = Plan(garrison, host, free);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 3 && t.ToArmyId == 2), Is.True,
                plan.DebugSummary());
            Assert.That(plan.Transfers.Any(t => t.ToArmyId == 1), Is.False);
            Assert.That(plan.ExpectedMembership[2].Count, Is.EqualTo(2));
        }

        // ATK-F03 — Vashti T13: a container of two heroes folded into a 3/7 preparation host left
        // its power unchanged and took two fighter slots. A hero enters a claimed receiver only
        // as its better commander without losing body room.
        [Test]
        public void PreparationHost_DoesNotTakeHeroesThatOnlyOccupyFighterSlots()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                Hero(0f, command: 7), Body(6f), Body(6f));
            ReorgContainer heroes = Free(3, Hero(0f, command: 4), Hero(0f, command: 4));
            ReorganizationPlan plan = Plan(garrison, host, heroes);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 3 && t.ToArmyId == 2), Is.False,
                plan.DebugSummary());
            Assert.That(plan.ExpectedMembership[2].Count, Is.EqualTo(3));
        }

        [Test]
        public void HeroRaisingCapacity_MayStillLeadAHeroLessReceiver()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1, Body(6f), Body(6f));
            ReorgContainer bench = Free(3, Hero(0f, command: 5));
            ReorganizationPlan plan = Plan(garrison, host, bench);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 3 && t.ToArmyId == 2), Is.True,
                plan.DebugSummary());
        }

        // The already damaged host (T17-T20: three heroes, four bodies, 7/7) sheds the heroes that
        // are neither its commander nor its best leader into the local garrison, zero-AP.
        [Test]
        public void DamagedPreparationHost_ReleasesExcessHeroesToTheGarrison()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgUnit lead = Hero(0f, command: 7);
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                lead, Hero(0f, command: 4), Hero(0f, command: 4),
                Body(6f), Body(6f), Body(6f), Body(6f));
            host.MayReleaseExcessHeroes = true;
            ReorganizationPlan plan = Plan(garrison, host);
            Assert.That(plan.Transfers.Count(t => t.FromArmyId == 2 && t.ToArmyId == 1), Is.EqualTo(2),
                plan.DebugSummary());
            Assert.That(plan.ExpectedMembership[2], Does.Contain(lead.Key));
            Assert.That(plan.ExpectedMembership[2].Count, Is.EqualTo(5));
        }

        private static ReorgUnit Keyed(ReorgUnit u, string key) { u.StrikeKey = key; return u; }

        // 2026-10-01 — a full preparation host swaps a body its target roster does not need for
        // a free same-hex body of a missing position: the scout goes out, the tank comes in.
        [Test]
        public void PreparationHost_SwapsNonRosterBodyForMissingPosition()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgUnit scout = Keyed(Body(3f), "scout");
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                Keyed(Hero(0f, command: 3), "lead"), Keyed(Body(6f), "inf"), scout);
            host.MayReleaseExcessHeroes = true;
            host.PreparationTargetKeys = new[] { "lead", "inf", "tank" };
            ReorgUnit tank = Keyed(Body(12f), "tank");
            ReorgContainer free = Free(3, Hero(0f, command: 4), tank, Keyed(Body(5f), "inf"),
                Keyed(Body(5f), "inf"));
            ReorganizationPlan plan = Plan(garrison, host, free);
            Assert.That(plan.ExpectedMembership[2], Does.Contain(tank.Key), plan.DebugSummary());
            Assert.That(plan.ExpectedMembership[2], Has.No.Member(scout.Key), plan.DebugSummary());
        }

        // A garrison body the garrison may not spare is no source: no release, no ping-pong.
        [Test]
        public void PreparationHost_KeepsNonRosterBody_WhenTheGarrisonCannotSpareTheSource()
        {
            ReorgContainer garrison = Garrison(1, Keyed(Body(6f), "tank"));
            garrison.GarrisonNonHeroFloor = 1;
            garrison.GarrisonPowerFloor = 5f;
            ReorgUnit scout = Keyed(Body(3f), "scout");
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                Keyed(Hero(0f, command: 3), "lead"), Keyed(Body(6f), "inf"), scout);
            host.MayReleaseExcessHeroes = true;
            host.PreparationTargetKeys = new[] { "lead", "inf", "tank" };
            ReorganizationPlan plan = Plan(garrison, host);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 2), Is.False, plan.DebugSummary());
        }

        // With no source of a missing position the non-roster body stays: nothing to wait for.
        [Test]
        public void PreparationHost_KeepsNonRosterBodyWithoutAPendingSource()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgUnit scout = Keyed(Body(3f), "scout");
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                Keyed(Hero(0f, command: 3), "lead"), Keyed(Body(6f), "inf"), scout);
            host.MayReleaseExcessHeroes = true;
            host.PreparationTargetKeys = new[] { "lead", "inf", "tank" };
            ReorganizationPlan plan = Plan(garrison, host);
            Assert.That(plan.ExpectedMembership.TryGetValue(2, out var members)
                ? members.Contains(scout.Key) : true, Is.True, plan.DebugSummary());
        }

        // A held card of a missing position is a source too: the slot is freed for it.
        [Test]
        public void PreparationRosterWaste_CountsHeldCardAndFreeSlots()
        {
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                Keyed(Hero(0f, command: 3), "lead"), Keyed(Body(6f), "inf"), Keyed(Body(3f), "scout"));
            host.PreparationTargetKeys = new[] { "lead", "inf", "tank" };
            host.PreparationHandKeys = new[] { "tank" };
            var none = new KeyValuePair<ReorgContainer, List<ReorgUnit>>[0];
            // One pending source (the card) and the scout blocks its only possible slot.
            Assert.That(ReorgViability.PreparationRosterWaste(host, host.Units, none), Is.EqualTo(2));
            Assert.That(ReorgViability.PreparationBlockedSlots(host, host.Units, none), Is.EqualTo(1));
            host.PreparationHandKeys = new[] { "other" };
            Assert.That(ReorgViability.PreparationRosterWaste(host, host.Units, none), Is.EqualTo(0));
            // Room left: the card lands without a release.
            ReorgContainer roomy = Mission(4, receives: true, movementFloor: -1,
                Keyed(Hero(0f, command: 4), "lead"), Keyed(Body(6f), "inf"), Keyed(Body(3f), "scout"));
            roomy.PreparationTargetKeys = host.PreparationTargetKeys;
            roomy.PreparationHandKeys = new[] { "tank" };
            Assert.That(ReorgViability.PreparationBlockedSlots(roomy, roomy.Units, none), Is.EqualTo(0));
        }

        [Test]
        public void OtherMissionReceivers_StillNeverGiveAHeroAway()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgContainer raid = Mission(2, receives: true, movementFloor: 3,
                Hero(0f, command: 7), Hero(0f, command: 4), Body(6f));
            ReorganizationPlan plan = Plan(garrison, raid);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 2), Is.False, plan.DebugSummary());
        }

        [Test]
        public void RouteBoundMission_RejectsSlowerInboundBody()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgContainer raid = Mission(2, receives: true, movementFloor: 3, Hero(3f));
            ReorgContainer free = Free(3, Body(8f, move: 2));
            ReorganizationPlan plan = Plan(garrison, raid, free);
            Assert.That(plan.Transfers.Any(t => t.ToArmyId == 2), Is.False, plan.DebugSummary());
        }

        [Test]
        public void MissionArmy_NeverDonates_EvenToGarrisonFloor()
        {
            ReorgContainer garrison = Garrison(1);
            garrison.GarrisonNonHeroFloor = 2;
            ReorgContainer mission = Mission(2, receives: true, movementFloor: 3,
                Hero(3f), Body(8f), Body(8f));
            ReorganizationPlan plan = Plan(garrison, mission);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 2 || t.ToArmyId == 2 && t.IsSwap),
                Is.False, plan.DebugSummary());
        }

        [Test]
        public void FullyProtectedMission_ReceivesNothing()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgContainer locked = Mission(2, receives: false, movementFloor: 3, Hero(3f));
            ReorgContainer free = Free(3, Body(8f));
            ReorganizationPlan plan = Plan(garrison, locked, free);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 2 || t.ToArmyId == 2), Is.False,
                plan.DebugSummary());
        }

        [Test]
        public void Contract_GarrisonHasNone_LeasedGarrisonStaysDonor()
        {
            var owner = new Game.Players.PlayerSetupData();
            var garrison = new Game.Map.ArmyData { Owner = owner, IsGarrison = true };
            var commitments = new ActorCommitments();
            commitments.Claim(garrison.Id);
            StrategicCapabilityLeaseRegistry.Mark(owner, 3, CapabilityKind.FieldCombatPower,
                new[] { garrison.Id });
            try
            {
                Assert.That(ArmyReorgAnalyzer.MutationContractFor(owner, 3, garrison, commitments),
                    Is.Null);
            }
            finally
            {
                StrategicCapabilityLeaseRegistry.ClearAll();
            }
        }

        [Test]
        public void Contract_DefaultClaimLocks_TwoClaimsKeepOnlyWhatBothAllow()
        {
            var commitments = new ActorCommitments();
            Assert.That(commitments.MutationContractOf(7), Is.Null);

            commitments.Claim(5, ArmyMutationContract.PreparationHost());
            Assert.That(commitments.MutationContractOf(5).MayReceive, Is.True);
            Assert.That(commitments.MutationContractOf(5).KeepsMovement, Is.False);

            commitments.Claim(5);
            Assert.That(commitments.MutationContractOf(5).MayReceive, Is.False);
            Assert.That(commitments.MutationContractOf(5).KeepsMovement, Is.True);

            Assert.That(ArmyMutationContract.PreparationHost().MayReleaseExcessHeroes, Is.True);
            Assert.That(ArmyMutationContract.PreparationHost()
                .Intersect(ArmyMutationContract.Leased).MayReleaseExcessHeroes, Is.False,
                "a same-turn capability lease is never taken apart");
            Assert.That(commitments.MutationContractOf(5).MayReleaseExcessHeroes, Is.False);

            commitments.Claim(6, ArmyMutationContract.MovingOperation("Raid:March"));
            Assert.That(commitments.MutationContractOf(6).MayReleaseExcessHeroes, Is.False);
            Assert.That(commitments.MutationContractOf(6).MayReceive, Is.True);
            Assert.That(commitments.MutationContractOf(6).KeepsMovement, Is.True);
        }
    }
}
#endif
