#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Ai;
using Game.Terrain;
using UnityEngine;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ATK-F04 — one physical handoff answer for the Attack gather projection, Provisioning's leg
    // check and the executed transfer (GroundCombatReinforcement.PlanAttackHandoff).
    public class AiAttackHandoffParityTests
    {
        private static readonly PlayerSetupData Us =
            new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly HexCoord Host = new HexCoord(5, 0);
        private static readonly HexCoord Far = new HexCoord(-2, -2);

        [SetUp]
        public void SetUp() => ArmyRegistry.Clear();

        [TearDown]
        public void TearDown() => ArmyRegistry.Clear();

        private static UnitData Body(string name, int attack, int hp) => new UnitData
        {
            Name = name, Owner = Us, Attack = attack, Defense = 2,
            HitPointsMax = hp, HitPointsCurrent = hp, Initiative = 2,
        };

        private static UnitData Hero(string name, int command) => new UnitData
        {
            Name = name, Owner = Us, IsHero = true, CommandRating = command,
            HitPointsMax = 6, HitPointsCurrent = 6, Fate = 3, FateMax = 3,
        };

        private static ArmyData Army(HexCoord hex, params UnitData[] members)
        {
            var army = new ArmyData { Owner = Us, Hex = hex };
            foreach (UnitData m in members)
                army.Members.Add(m);
            return army;
        }

        private static IReadOnlyList<WorthIt.DefendingArmy> Site() => new[]
        {
            new WorthIt.DefendingArmy(new[] { new WorthIt.DefenderProfile(2f, false, null, 3f, 8f, 2) },
                default(WorthIt.SideCommander)),
        };

        [Test]
        public void DepartureProjection_RemovesDonatedBodiesFromTheRemainingHomeDefence()
        {
            ArmyData host = Army(Host, Body("host", 3, 5));
            UnitData donated = Body("tank", 14, 16);
            ArmyData donor = Army(Host, Body("guard", 1, 3), donated);
            donor.IsGarrison = true;
            ArmyRegistry.Register(host);
            ArmyRegistry.Register(donor);
            var snap = new WorldSnapshot { Observer = Us, Self = new SelfSnapshot {
                Armies = new[] { Snapshot(host), Snapshot(donor) } } };
            snap.Self.Armies[1].IsGarrison = true;
            var plan = new GroundCombatAssemblyPlan { Feasible = true, NeedsAssembly = true,
                BaseArmyId = host.Id };
            plan.Transfers.Add(new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = donated });
            var projected = GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan);
            Assert.That(projected[host.Id].MemberCount, Is.EqualTo(2));
            Assert.That(projected[donor.Id].MemberCount, Is.EqualTo(1));
            Assert.That(projected[donor.Id].IsGarrison, Is.True);
            Assert.That(projected[donor.Id].Members.Single().Attack, Is.EqualTo(1f));
            Assert.That(donor.Members, Does.Contain(donated));
        }

        // Kryll T12: the host already acted this turn and today's AP is gone, while the support is
        // still walking. The arrival handoff strengthens the host; only its charge is today's.
        [Test]
        public void ArrivalHandoff_DoesNotDependOnTodaysActivationCharge()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            host.MarkActivated();
            ArmyData support = Army(Far, Body("Medium Tank", 8, 12));

            HandoffPlan projected = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: false, out string why);
            Assert.That(projected, Is.Not.Null, why);
            Assert.That(projected.Incoming.Select(u => u.Name), Is.EquivalentTo(new[] { "Medium Tank" }));
            Assert.That(GroundCombatReinforcement.ImprovesAttackForce(host, support, Site(), 0f, out _),
                Is.True, "the leg check is the same composition answer");
        }

        // The rendezvous step itself still pays today's charge (player root lookup: Unity-only).
        [Test]
        public void RendezvousHandoff_StillNeedsTodaysActivationCharge()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            host.MarkActivated();
            ArmyData support = Army(Host, Body("Medium Tank", 8, 12));
            HandoffPlan now = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: true, out string nowWhy);
            Assert.That(now, Is.Null, "no player root pays the newcomer's activation today");
            Assert.That(nowWhy, Does.Contain("resources are unavailable"));
        }

        // Kryll T18/T20: a support [hero, body] whose hero would take command but adds no power —
        // the bodies-only transfer that does raise the host is the plan, and the gather projection
        // (SupportImprovesPrimary on live armies) answers the same.
        [Test]
        public void NonStrengtheningHeroHandover_FallsBackToTheUsefulBody()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            ArmyData support = Army(Host, Hero("Aldric", 8), Body("Ash Drifter", 5, 6));
            float before = AiPower.EffectiveArmyPower(host.Members);

            HandoffPlan plan = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: false, out string why);
            Assert.That(plan, Is.Not.Null, why);
            var after = host.Members.Except(plan.Displaced).Concat(plan.Incoming).ToList();
            Assert.That(AiPower.EffectiveArmyPower(after), Is.GreaterThan(before),
                "a handoff that does not strengthen the fist is never the plan");
            Assert.That(plan.Incoming.Any(u => u.Name == "Ash Drifter"), Is.True);

        }

        // The gather projection on live armies (registry lookup: Unity-only) answers the same.
        [Test]
        public void GatherProjection_MatchesTheLivePlan()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            ArmyData useful = Army(Host, Hero("Aldric", 8), Body("Ash Drifter", 5, 6));
            ArmyData useless = Army(Host, Body("Weak", 1, 2));
            ArmyData full = Army(Host, Body("A", 6, 8), Body("B", 6, 8));
            foreach (ArmyData a in new[] { host, useful, useless, full })
                ArmyRegistry.Register(a);
            foreach ((ArmyData primary, ArmyData support) in new[] { (host, useful), (full, useless) })
                Assert.That(GroundCombatAssemblyPlanner.SupportImprovesPrimary(Snapshot(primary),
                        Snapshot(support), Site(), allowCommandHandover: true),
                    Is.EqualTo(GroundCombatReinforcement.PlanAttackHandoff(primary, support, Site(), 0f,
                        requireChargeNow: false, out _) != null));
        }

        [Test]
        public void FullHost_WithoutAStrongerBody_HasNoPlanAndAnExactReason()
        {
            ArmyData host = Army(Host, Body("A", 6, 8), Body("B", 6, 8));
            ArmyData support = Army(Host, Body("Weak", 1, 2));

            HandoffPlan plan = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: false, out string why);
            Assert.That(plan, Is.Null);
            Assert.That(why, Does.Contain("full"));
        }

        [TestCase(SafeRouteProfile.Attack)]
        [TestCase(SafeRouteProfile.Combat)]
        [Category("UnityEngine")]
        public void ReinforcementChoosesAnAdmissibleAlternativeInsteadOfStrandingThePrimary(SafeRouteProfile profile)
        {
            var mapObject = new GameObject("handoff-route");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                var destination = new HexCoord(Host.Q + 1, Host.R);
                map.SetData(2, 1f, new Dictionary<HexCoord, TerrainTypeEntry>
                {
                    [Host] = new TerrainTypeEntry { moveCost = 1 },
                    [destination] = new TerrainTypeEntry { moveCost = 2 },
                });
                var host = Army(Host, Body("host", 3, 5));
                var slow = Body("immobile tank", 14, 16);
                var fast = Body("mobile escort", 7, 9);
                host.Members[0].MoveMax = host.Members[0].MoveCurrent = 3;
                fast.MoveMax = fast.MoveCurrent = 2;
                var support = Army(Host, slow, fast);
                var admissible = GroundCombatReinforcement.PreserveTaskRoute(map, host, destination, profile);
                Assert.That(admissible, Is.Not.Null);
                HandoffPlan plan = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(), 0f,
                    false, out string why, rosterAdmissible: admissible);
                Assert.That(plan, Is.Not.Null, why);
                Assert.That(plan.Incoming, Is.EquivalentTo(new[] { fast }),
                    "the stronger immobile unit must not hide a useful reachable alternative");
                Assert.That(ArmyData.ComputeMaxMovement(GroundCombatReinforcement.FinalRoster(host, plan)),
                    Is.EqualTo(2), "a legitimate slowdown is admitted");
                Assert.That(ArmyData.RosterFits(support.Members.Except(plan.Incoming).Concat(plan.Displaced).ToList(), false),
                    Is.True, "the support container also remains legal");
                ArmyRegistry.Register(host); ArmyRegistry.Register(support);
                foreach (bool commandAllowed in new[] { false, true })
                    Assert.That(GroundCombatAssemblyPlanner.SupportImprovesPrimary(Snapshot(host),
                        Snapshot(support), Site(), allowCommandHandover: commandAllowed,
                        rosterAdmissible: admissible), Is.True,
                        "planning admits the same useful alternate roster as the handoff");
                support.Members.Remove(fast);
                Assert.That(GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(), 0f,
                    false, out _, rosterAdmissible: admissible), Is.Null);
            }
            finally { Object.DestroyImmediate(mapObject); }
        }

        [Test]
        [Category("UnityEngine")]
        public void StaticOrAlreadyUnreachableTasksMayTakeUsefulImmobileReinforcement()
        {
            var mapObject = new GameObject("static-handoff");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                var unreachable = new HexCoord(Host.Q + 1, Host.R);
                map.SetData(2, 1f, new Dictionary<HexCoord, TerrainTypeEntry>
                {
                    [Host] = new TerrainTypeEntry { moveCost = 1 },
                    [unreachable] = new TerrainTypeEntry { moveCost = 4 },
                });
                var host = Army(Host, Body("host", 3, 5));
                host.Members[0].MoveMax = host.Members[0].MoveCurrent = 3;
                var support = Army(Host, Body("static defence", 14, 16));
                foreach (var destination in new[] { Host, unreachable })
                {
                    var admissible = GroundCombatReinforcement.PreserveTaskRoute(map, host, destination,
                        SafeRouteProfile.Attack);
                    Assert.That(admissible, Is.Null, "no otherwise-feasible march is being destroyed");
                    var plan = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(), 0f,
                        false, out string why, rosterAdmissible: admissible);
                    Assert.That(plan, Is.Not.Null, why);
                    Assert.That(ArmyData.ComputeMaxMovement(GroundCombatReinforcement.FinalRoster(host, plan)),
                        Is.Zero);
                }
            }
            finally { Object.DestroyImmediate(mapObject); }
        }

        [Test]
        public void CommandHandoffProjectsThePromotedCommanderAndCanFallBackToBodies()
        {
            var current = Hero("current", 4);
            var incoming = Hero("incoming", 8);
            current.MoveMax = current.MoveCurrent = incoming.MoveMax = incoming.MoveCurrent = 3;
            var body = Body("escort", 7, 9);
            var host = Army(Host, current, Body("host", 3, 5));
            var support = Army(Host, incoming, body);
            var command = GroundCombatReinforcement.CommandHandover(host, support, Site(), 0f,
                null, rosterAdmissible: roster => roster[0] == incoming);
            Assert.That(command, Is.Not.Null, "the predicate sees the promoted commander first");
            var bodies = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(), 0f,
                false, out string why, rosterAdmissible: roster => !roster.Contains(incoming));
            Assert.That(bodies, Is.Not.Null, why);
            Assert.That(bodies.Promote, Is.Null);
            Assert.That(bodies.Incoming, Does.Contain(body));
        }

        [Test]
        public void GatherUsesReachableEscortWhenTheSameDonorsStrongerTankBlocksTheMarch()
        {
            var mapObject = new GameObject("gather route alternatives");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                var goal = new HexCoord(6, 0);
                var donorHex = new HexCoord(4, 0);
                map.SetData(8, 1f, new Dictionary<HexCoord, TerrainTypeEntry> {
                    [Host] = new TerrainTypeEntry { moveCost = 1 },
                    [donorHex] = new TerrainTypeEntry { moveCost = 1 },
                    [goal] = new TerrainTypeEntry { moveCost = 2 },
                });
                var commander = Hero("commander", 3);
                var original = Body("host", 3, 5);
                commander.MoveMax = commander.MoveCurrent = original.MoveMax = original.MoveCurrent = 3;
                var tank = Body("strong slow tank", 100, 16); tank.MoveMax = tank.MoveCurrent = 1;
                var escort = Body("fast escort", 14, 16); escort.MoveMax = escort.MoveCurrent = 3;
                var host = Army(Host, commander, original);
                var donor = Army(donorHex, tank, escort);
                ArmyRegistry.Register(host); ArmyRegistry.Register(donor);
                var snap = new WorldSnapshot { Observer = Us, Map = map,
                    Self = new SelfSnapshot { Armies = new[] { Snapshot(host), Snapshot(donor) } } };
                var gather = GroundCombatAssemblyPlanner.PlanGather(snap, Site(), 0f, goal,
                    new HashSet<int>(), .8f, pinnedHostArmyId: host.Id);
                Assert.That(gather.Feasible, Is.True, gather.Reason);
                Assert.That(gather.SupportArmyIds, Does.Contain(donor.Id));
                Assert.That(gather.DepartureArmies[host.Id].Members.Any(p => p.Attack == escort.Attack), Is.True);
                Assert.That(gather.DepartureArmies[host.Id].Members.Any(p => p.Attack == tank.Attack), Is.False);
                Assert.That(gather.DepartureArmies[host.Id].MaxMovement, Is.EqualTo(3));
                Assert.That(donor.Members, Does.Contain(escort), "planning must never perform the exchange");
            }
            finally { Object.DestroyImmediate(mapObject); }
        }

        [Test]
        [Category("UnityEngine")]
        public void PreparationSkipsStrandingHeroAndBodyButKeepsTheFastAlternatives()
        {
            var mapObject = new GameObject("preparation route alternatives");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                var goal = new HexCoord(6, 0);
                map.SetData(8, 1f, new Dictionary<HexCoord, TerrainTypeEntry> {
                    [Host] = new TerrainTypeEntry { moveCost = 1 },
                    [goal] = new TerrainTypeEntry { moveCost = 2 },
                });
                var original = Body("host", 3, 5);
                var slowHero = Hero("strong slow commander", 8);
                var fastHero = Hero("fast commander", 5);
                var tank = Body("strong slow tank", 100, 16);
                var escort = Body("fast escort", 14, 16);
                original.MoveMax = original.MoveCurrent = 3;
                fastHero.MoveMax = fastHero.MoveCurrent = escort.MoveMax = escort.MoveCurrent = 2;
                slowHero.MoveMax = slowHero.MoveCurrent = tank.MoveMax = tank.MoveCurrent = 1;
                var host = Army(Host, original);
                var slowCommand = Army(Host, slowHero);
                var fastCommand = Army(Host, fastHero);
                var donor = Army(Host, tank, escort);
                foreach (var army in new[] { host, slowCommand, fastCommand, donor }) ArmyRegistry.Register(army);
                var snap = new WorldSnapshot { Observer = Us, Map = map,
                    Self = new SelfSnapshot { Armies = new[] { Snapshot(host), Snapshot(slowCommand),
                        Snapshot(fastCommand), Snapshot(donor) } } };
                var admissible = GroundCombatReinforcement.PreserveTaskRoute(map, host, goal, SafeRouteProfile.Attack);
                var plan = GroundCombatAssemblyPlanner.PlanPreparationAssembly(snap, host, null, Site(), 0f,
                    rosterAdmissible: admissible);
                Assert.That(plan.Feasible, Is.True, plan.Reason);
                Assert.That(plan.Transfers.Select(t => t.Unit), Is.EquivalentTo(new[] { fastHero, escort }));
                Assert.That(ArmyData.ComputeMaxMovement(host.Members.Concat(plan.Transfers.Select(t => t.Unit)).ToList()),
                    Is.EqualTo(2), "a slower roster is legal while its target remains reachable");
                Assert.That(host.Members, Is.EquivalentTo(new[] { original }));
                Assert.That(donor.Members, Does.Contain(escort), "planning remains read-only");
            }
            finally { Object.DestroyImmediate(mapObject); }
        }

        [Test]
        [Category("UnityEngine")]
        public void PinnedPreparationRejectsTheWholeStrandingRosterBeforeAnyTransfer()
        {
            var mapObject = new GameObject("pinned preparation route");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                var goal = new HexCoord(6, 0);
                map.SetData(8, 1f, new Dictionary<HexCoord, TerrainTypeEntry> {
                    [Host] = new TerrainTypeEntry { moveCost = 1 },
                    [goal] = new TerrainTypeEntry { moveCost = 2 },
                });
                var commander = Hero("commander", 5);
                var original = Body("host", 3, 5);
                var escort = Body("fast first transfer", 14, 16);
                var tank = Body("stranding second transfer", 100, 16);
                commander.MoveMax = commander.MoveCurrent = original.MoveMax = original.MoveCurrent = 3;
                escort.MoveMax = escort.MoveCurrent = 2;
                tank.MoveMax = tank.MoveCurrent = 1;
                var host = Army(Host, commander, original);
                var donor = Army(Host, Hero("donor", 5), escort, tank);
                ArmyRegistry.Register(host); ArmyRegistry.Register(donor);
                var assembly = new GroundCombatAssemblyPlan { Feasible = true, BaseArmyId = host.Id,
                    NeedsAssembly = true };
                assembly.Transfers.Add(new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = escort });
                assembly.Transfers.Add(new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = tank });
                var pm = new ProvisionedMission { Kind = MissionKind.Attack,
                    AttackPreparationAssembly = assembly, AttackTarget = new AttackMissionTarget {
                        Phase = AttackMissionPhase.Gather, PreparationStep = AttackPreparationStep.Assemble,
                        PrimaryArmyId = host.Id, DestinationHex = Host,
                        Target = AttackTargetRef.For(goal, new PlayerSetupData(), AttackTargetKind.Base) } };
                var result = new ExecutionResult();
                var snap = new WorldSnapshot { Observer = Us, Map = map,
                    Self = new SelfSnapshot { Armies = new[] { Snapshot(host), Snapshot(donor) } } };
                Assert.That(AttackExecutor.TryRunPreparationStep(Us, new AiTurnContext { Map = map }, pm,
                    result, snap), Is.True);
                Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.MoveRejected));
                Assert.That(result.CombatChanged, Is.False);
                Assert.That(result.OperationStarted, Is.False);
                Assert.That(host.Members, Is.EqualTo(new[] { commander, original }));
                Assert.That(donor.Members, Is.EqualTo(new[] { donor.Commander, escort, tank }));
                Assert.That(host.HasActivatedThisTurn, Is.False);
                Assert.That(donor.HasActivatedThisTurn, Is.False);
            }
            finally { Object.DestroyImmediate(mapObject); }
        }

        private static ArmySnapshot Snapshot(ArmyData a) => new ArmySnapshot
        {
            ArmyId = a.Id, Owner = Us, Hex = a.Hex, IsStructuralRaidActor = true,
            MemberCount = a.Members.Count, Capacity = a.Capacity, MaxMovement = 3, CurrentMovement = 3,
            Members = a.Members.Where(u => !u.IsHero).Select(WorthIt.FromLiveUnit).ToList(),
            EffectiveArmyPower = AiPower.EffectiveArmyPower(a.Members),
        };
    }
}
#endif
