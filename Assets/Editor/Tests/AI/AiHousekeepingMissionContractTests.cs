#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // T05 — Housekeeping protects a claimed operation's function, not a frozen roster: a mission
    // receiver takes free same-hex members, never gives any away, and a route-bound operation is
    // never slowed by an inbound member.
    public class AiHousekeepingMissionContractTests
    {
        private static int _key;

        [Test]
        public void PreparationRosterContractReadsItsDomainOwnerWithoutKeepingASecondRoster()
        {
            var player = new PlayerSetupData();
            var host = new ArmyData { Owner = player };
            var attack = new AttackIntent { Preparation = true, Phase = AttackMissionPhase.Gather,
                PrimaryArmyId = host.Id, TargetRoster = new List<StrikeRosterSlot>
                {
                    new StrikeRosterSlot("tank", false, 1, ForceSource.Map),
                    new StrikeRosterSlot("tank", false, 1, ForceSource.Hand),
                } };
            var intent = new MissionIntent { Kind = MissionKind.Attack, Objective = attack,
                IntentKey = new MissionIntentKey(MissionKind.Attack, 0, 17, 2, 3) };
            var state = MissionIntentRegistry.GetOrCreate(player);
            state.Put(intent);
            try
            {
                var contract = AttackContinuity.AttackPreparationMutationContract(4);
                Assert.That(contract.TargetRosterKeys(host), Is.EqualTo(new[] { "tank", "tank" }));
                attack.TargetRoster.Add(new StrikeRosterSlot("recce", false, 1, ForceSource.Deck));
                Assert.That(contract.TargetRosterKeys(host), Is.EqualTo(new[] { "tank", "tank", "recce" }));
                intent.Status = IntentStatus.Suspended;
                Assert.That(contract.TargetRosterKeys(host), Is.Null,
                    "the existing rule reads only active preparation hosts");
                intent.Status = IntentStatus.Active;
                state.Remove(intent.IntentKey);
                Assert.That(contract.TargetRosterKeys(host), Is.Null);
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void ASecondClaimCannotBeBypassedByADomainBodyReleasePermission()
        {
            int evaluations = 0;
            var domain = ArmyMutationContract.PreparationHost(_ => new[] { "tank" },
                _ => new[] { "tank" }, (_, __) => { evaluations++; return true; });
            Assert.That(domain.MayReleaseBody(null, null), Is.True);
            var protectedContract = domain.Intersect(ArmyMutationContract.FullyProtected);
            Assert.That(protectedContract.MayReleaseBody(null, null), Is.False);
            Assert.That(protectedContract.MayReceive, Is.False);
            Assert.That(evaluations, Is.EqualTo(1));
        }

        private static ReorgUnit Body(float power, int move = 3) => new ReorgUnit
        {
            Key = _key++, Power = power, IsGroundCombatant = true, IsGroundBattleBody = true,
            MoveCurrent = move, MoveMax = move,
        };

        private static ReorgUnit Hero(float power, int command = 4) => new ReorgUnit
        {
            Key = _key++, IsHero = true, Power = power, CommandRating = command,
            IsGroundCombatant = true, HeroRole = HeroOperationalRole.Flexible,
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
            HousekeepingStand.Plan(new LocalForceGroup
            {
                Containers = containers.ToList(),
            });

        private static ReorgUnit Operator(int command = 3)
        {
            ReorgUnit u = Hero(0f, command);
            u.IsDevelopmentOperator = true;
            u.HeroRole = HeroOperationalRole.SupportOperator;
            return u;
        }

        // 2026-10-08 — Halden T19-T21: a fallback support operator rode out with the host although
        // the army had its own commander. It stays at its facility's hex: in the local garrison.
        [Test]
        public void PreparationHost_LeavesItsFacilityOperatorInTheLocalGarrison()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgUnit op = Operator();
            ReorgUnit lead = Hero(0f, command: 7);
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                lead, op, Body(6f), Body(6f));
            host.MayReleaseExcessHeroes = true;
            ReorganizationPlan plan = Plan(garrison, host);
            Assert.That(plan.Transfers.Any(t => t.FromArmyId == 2 && t.ToArmyId == 1
                    && t.UnitKey == op.Key), Is.True, plan.DebugSummary());
            Assert.That(plan.ExpectedMembership[2], Does.Contain(lead.Key));
        }

        [Test]
        public void PreparationHost_NeverReleasesTheOperatorWhoIsItsOnlyCommanderOrToAFieldArmy()
        {
            ReorgContainer garrison = Garrison(1, Body(8f), Body(8f));
            ReorgUnit solo = Operator();
            ReorgContainer soloHost = Mission(2, receives: true, movementFloor: -1, solo, Body(6f));
            soloHost.MayReleaseExcessHeroes = true;
            Assert.That(Plan(garrison, soloHost).Transfers.Any(t => t.FromArmyId == 2), Is.False);

            ReorgContainer host = Mission(4, receives: true, movementFloor: -1,
                Hero(0f, command: 7), Operator(), Body(6f));
            host.MayReleaseExcessHeroes = true;
            ReorgContainer free = Free(5, Body(8f));
            Assert.That(Plan(host, free).Transfers.Any(t => t.FromArmyId == 4 && t.ToArmyId == 5), Is.False,
                "an operator is never released to a field container: it must stay at its facility");
        }

        private static ReorgUnit Keyed(ReorgUnit u, string key) { u.StrikeKey = key; return u; }

        // 2026-11-16 (owner decision) — the armies take their best fighters back: a garrison body
        // its floor keeps (no release) comes into a full preparation host by a 1-for-1 exchange
        // for the host's non-roster body, the garrison keeping min(held, floor).
        [Test]
        public void PreparationHost_ExchangesNonRosterBodyForAGarrisonRosterBody()
        {
            ReorgUnit tank = Keyed(Body(12f), "tank");
            ReorgContainer garrison = Garrison(1, tank, Keyed(Body(8f), "inf"));
            garrison.GarrisonPowerFloor = 9.5f;   // 0.55 x 18: the tank alone may never leave
            ReorgUnit heavy = Keyed(Body(10f), "heavy");
            ReorgContainer host = Mission(2, receives: true, movementFloor: -1,
                Keyed(Hero(0f, command: 3), "lead"), Keyed(Body(6f), "inf"), heavy);
            host.MayReleaseExcessHeroes = true;
            host.PreparationTargetKeys = new[] { "lead", "inf", "tank" };
            ReorganizationPlan plan = Plan(garrison, host);
            Assert.That(plan.ExpectedMembership[2], Does.Contain(tank.Key), plan.DebugSummary());
            Assert.That(plan.ExpectedMembership[1], Does.Contain(heavy.Key), plan.DebugSummary());
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
        public void Contract_GarrisonHasNone_LeasedGarrisonStaysDonor()
        {
            var owner = new Game.Players.PlayerSetupData();
            var garrison = new Game.Map.ArmyData { Owner = owner, IsGarrison = true };
            var commitments = new ActorCommitments();
            commitments.Claim(garrison.Id);
            StrategicCapabilityLeaseRegistry.Mark(owner, 3, DesireAxis.Aggression, CapabilityKind.FieldCombatPower,
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

