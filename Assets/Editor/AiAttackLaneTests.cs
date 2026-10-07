#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  ATK stage 3 — THE ATTACK LANE: intent model, mission proposal, phase machine, recovery,
    //  movement authority and the equipment witness.
    //
    //  Covers §83 groups D (movement authority, assault AND tactical strike), I/J (main-target
    //  continuity), K (capture behaviour), N (lost base retarget), R (one army, one ground-combat
    //  mission) and S (only a proven shortage may demand), plus the §22 one-primary-field model,
    //  §24 phase machine, §47 recovery, §78 equipment proof and the §17 once-per-turn side-strike
    //  marker.
    //
    //  The §12/§15/§18 arithmetic of the side strike itself (significance, route economics,
    //  candidate ordering) is exercised by Tools/attack-tactical-sim against the same production
    //  methods; those are pure functions and do not belong in a Unity test run.
    // ===========================================================================================
    public class AiAttackLaneTests
    {
        private static readonly PlayerSetupData Us =
            new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly PlayerSetupData Red =
            new PlayerSetupData { Nickname = "Red", ColorIndex = 2 };
        private static readonly PlayerSetupData Blue =
            new PlayerSetupData { Nickname = "Blue", ColorIndex = 3 };

        private static readonly HexCoord OurBase = new HexCoord(0, 0);
        private static readonly HexCoord AltBase = new HexCoord(2, 0);
        private static readonly HexCoord RedBase = new HexCoord(6, 0);
        private static readonly HexCoord EnRoute = new HexCoord(4, 0);

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
        }

        // A roster that comfortably beats SiteDefenders, and one that cannot.
        private static List<WorthIt.DefenderProfile> Strong() => new List<WorthIt.DefenderProfile>
        {
            Body(14, 5, 16, 5), Body(13, 5, 15, 4),
        };

        private static List<WorthIt.DefenderProfile> Weak() => new List<WorthIt.DefenderProfile>
        {
            Body(1, 1, 4, 1),
        };

        private static WorthIt.DefenderProfile[] SiteDefenders() => new[]
        {
            Body(6, 3, 12, 4), Body(6, 3, 12, 3),
        };

        [Test]
        public void AttackReinforcement_UsesSingletonFieldSupport_WhileRaidRetainsIt()
        {
            ArmySnapshot primary = Army(7, OurBase, Weak());
            primary.Capacity = 3;
            ArmySnapshot singleton = Army(8, OurBase, new[] { Body(14, 5, 16, 5) });
            var opposition = new[]
            {
                new WorthIt.DefendingArmy(SiteDefenders(), default(WorthIt.SideCommander)),
            };

            Assert.That(GroundCombatAssemblyPlanner.SupportImprovesPrimary(primary, singleton,
                opposition, allowCommandHandover: false, allowCompleteTransfer: false), Is.False,
                "Raid leaves a member in its support army");
            Assert.That(GroundCombatAssemblyPlanner.SupportImprovesPrimary(primary, singleton,
                opposition, allowCommandHandover: true, allowCompleteTransfer: true), Is.True,
                "Attack can add the sole field body to its primary army");
        }

        [TestCase(AttackMissionPhase.Gather)]
        [TestCase(AttackMissionPhase.Assault)]
        [TestCase(AttackMissionPhase.Reinforcement)]
        public void LiveAttack_BlocksSecondFistEvenAfterPrimarySpentMovement(AttackMissionPhase phase)
        {
            var primary = new ArmyData { Owner = Us, Hex = EnRoute };
            primary.Members.Add(new UnitData { Owner = Us });
            ArmyRegistry.Register(primary);
            ArmySnapshot spent = Army(primary.Id, EnRoute, Strong()); spent.CurrentMovement = 0;
            ArmySnapshot free = Army(49, OurBase, Weak());
            free.HasHero = true;
            WorldSnapshot snap = DefendedSite(new[] { spent, free }, new[] { OurBase });
            MissionIntent intent = AttackIntent(phase, primary.Id);
            intent.Attack.Target = AttackTargetRef.For(new HexCoord(9, 0), Blue, AttackTargetKind.Base);
            intent.IntentKey = MissionIntentKey.For(intent);
            var diag = new List<string>();
            var demands = new List<AxisDemand>();
            AggressionDemandEvaluator.AppendAttackDemands(snap, new[] { intent },
                ActorCommitments.FromIntents(new[] { intent }, snap, null),
                new CapabilityInventory(), diag, demands);
            Assert.That(demands, Is.Empty,
                "a different target and a free weak fist do not authorize a second operation");
            Assert.That(diag.Any(x => x.Contains("unbound_attack_owned_by_live_operation")), Is.True);
            Assert.That(intent.Status, Is.EqualTo(IntentStatus.Active));
        }

        // ---- §22 the model keeps ONE primary field ---------------------------------------

        [Test]
        public void AttackIntent_PreferredMover_IsAPassThroughToTheOnePrimaryField()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);

            Assert.That(intent.IntentKey.Kind, Is.EqualTo(MissionKind.Attack));
            Assert.That(intent.IntentKey.ObjectiveId, Is.EqualTo(Red.ColorIndex));
            Assert.That(intent.PreferredMoverArmyId, Is.EqualTo(7));

            intent.PreferredMoverArmyId = 9;

            Assert.That(intent.Attack.PrimaryArmyId, Is.EqualTo(9),
                "there is deliberately no second _preferredMoverArmyId copy for Attack");
            Assert.That(intent.PreferredMoverArmyId, Is.EqualTo(9));
        }

        // ---- §83 D / §26 movement authority ----------------------------------------------

        [Test]
        public void MoveAuthority_OnlyTheTerminalStepMaySeekATakeover()
        {
            Assert.That(GroundMoveAuthorityPolicy.ForStructureAssaultStep(EnRoute, RedBase),
                Is.EqualTo(AiGroundMoveAuthority.TransitCapture),
                "an approach step never seeks a fight; a known undefended structure on the way is crossed (the game rule takes it)");
            Assert.That(GroundMoveAuthorityPolicy.ForStructureAssaultStep(RedBase, RedBase),
                Is.EqualTo(AiGroundMoveAuthority.CombatAndCapture));
        }

        // §9/§26 — a side strike may fight and must NEVER capture. Together with
        // AiGroundMoveAuthorityTests.CombatDoesNotImplicitlyAuthorizeEmptyStructureTakeover this is
        // also the guard for the stage-5 Raid audit: Combat authority cannot take a building over,
        // so no lane but Attack's own terminal assault step can.
        [Test]
        public void MoveAuthority_ATacticalStrikeMayFightButNeverCapture()
        {
            Assert.That(GroundMoveAuthorityPolicy.ForTacticalStrikeStep(OurBase, EnRoute),
                Is.EqualTo(AiGroundMoveAuthority.TransitCapture),
                "walking toward the contact never seeks a fight");
            Assert.That(GroundMoveAuthorityPolicy.ForTacticalStrikeStep(EnRoute, EnRoute),
                Is.EqualTo(AiGroundMoveAuthority.Combat),
                "the contact step fights the army and may not take any structure over");
        }

        // ---- §83 K / §8 / §61 capture behaviour ------------------------------------------

        [Test]
        public void Capture_RetiresTheIntentAsSuccess_AndLeavesTheArmyWhereItIs()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            // The hex is now in our own Base topology — that IS the success condition.
            WorldSnapshot snap = Snap(new[] { Building(RedBase, Red) },
                new[] { OurBase, RedBase }, new[] { Army(7, RedBase, Strong()) });

            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, intent.Attack, null,
                out bool captured);

            Assert.That(keep, Is.False, "one intent is one Base; there is no re-orient after capture");
            Assert.That(captured, Is.True);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault),
                "no Return phase on success — the army stays on the base it took");
        }

        // ---- §83 J / §25 main-target owner changes ---------------------------------------

        [Test]
        public void TargetTakenByAThirdParty_RetiresTheIntentButNotAsSuccess()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            WorldSnapshot snap = Snap(new[] { Building(RedBase, Blue) },
                new[] { OurBase }, new[] { Army(7, EnRoute, Strong()) });

            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, intent.Attack, null,
                out bool captured);

            Assert.That(keep, Is.False);
            Assert.That(captured, Is.False,
                "a different enemy holding the hex is a NEW objective, not our victory");
        }

        [Test]
        public void VanishedPrimary_RetiresTheOperation()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            WorldSnapshot snap = Snap(new[] { Building(RedBase, Red) },
                new[] { OurBase }, Array.Empty<ArmySnapshot>());

            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, intent.Attack, null,
                out _), Is.False);
        }

        // ---- §24 the Assault / Reinforcement / Recovery phase machine ---------------------

        [Test]
        public void PhaseMachine_FollowsWhetherThePrimaryStillClearsTheSite()
        {
            // Clears -> back to Assault.
            MissionIntent capable = AttackIntent(AttackMissionPhase.Reinforcement, 7);
            MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Strong()) }, new[] { OurBase }),
                capable, capable.Attack, null, out _);
            Assert.That(capable.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));

            // Cannot clear (coverage required), but a strong free army on the same hex could join
            // -> Reinforcement bound to it, met right there, no demand.
            using (Coverage(required: true))
            {
                MissionIntent needsHelp = AttackIntent(AttackMissionPhase.Assault, 7);
                MissionContinuityLayer.ResolveAttackIntent(Us,
                    DefendedSite(new[] { Army(7, EnRoute, Weak()), Army(8, EnRoute, Strong()) },
                        new[] { OurBase }),
                    needsHelp, needsHelp.Attack, null, out _);
                Assert.That(needsHelp.Attack.Phase, Is.EqualTo(AttackMissionPhase.Reinforcement));
                Assert.That(needsHelp.Attack.SupportArmyId, Is.EqualTo(8));
                Assert.That(needsHelp.Attack.ReinforcementRequestedTurn, Is.EqualTo(-1));
            }
        }

        [Test]
        public void RewardAfterMarch_DoesNotRecallACombatCapablePrimary()
        {
            ArmySnapshot actor = Army(7, EnRoute, Strong());
            WorldSnapshot snap = DefendedSite(new[] { actor }, new[] { OurBase });
            snap.Self.TotalMilitaryPotential = actor.EffectiveArmyPower * 5f;
            MissionIntent marching = AttackIntent(AttackMissionPhase.Assault, 7);
            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us, snap, marching,
                marching.Attack, null, out _), Is.True);
            Assert.That(marching.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));

            MissionIntent staging = AttackIntent(AttackMissionPhase.Assault, 7);
            staging.Attack.AssaultStarted = false;
            MissionContinuityLayer.ResolveAttackIntent(Us, snap, staging,
                staging.Attack, null, out _);
            Assert.That(staging.Attack.Phase, Is.EqualTo(AttackMissionPhase.Reinforcement));
        }

        [TestCase(AttackMissionPhase.Assault)]
        [TestCase(AttackMissionPhase.Reinforcement)]
        [TestCase(AttackMissionPhase.SupportReturn)]
        [TestCase(AttackMissionPhase.RecoveryReturn)]
        public void ActorCommitments_ClaimsAttackPrimaryAndBoundSupport(AttackMissionPhase phase)
        {
            var primary = new ArmyData { Owner = Us, Hex = EnRoute };
            primary.Members.Add(new UnitData { Owner = Us });
            var support = new ArmyData { Owner = Us, Hex = EnRoute };
            support.Members.Add(new UnitData { Owner = Us });
            ArmyRegistry.Register(primary);
            ArmyRegistry.Register(support);
            MissionIntent intent = AttackIntent(phase, primary.Id, supportId: support.Id);
            WorldSnapshot snap = DefendedSite(
                new[] { Army(primary.Id, EnRoute, Strong()), Army(support.Id, EnRoute, Strong()) },
                new[] { OurBase });

            ActorCommitments commitments = ActorCommitments.FromIntents(
                new[] { intent }, snap, null);

            Assert.That(commitments.IsArmyClaimed(primary.Id), Is.True);
            bool supportLeg = phase == AttackMissionPhase.Reinforcement
                || phase == AttackMissionPhase.SupportReturn;
            Assert.That(commitments.IsArmyClaimed(support.Id), Is.EqualTo(supportLeg));
        }

        [Test]
        public void LostAttackSupport_IsClearedAndPrimaryIsReevaluated()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7, supportId: 8);
            intent.Attack.SupportReturnHex = OurBase;
            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Strong()) }, new[] { OurBase }),
                intent, intent.Attack, null, out _);

            Assert.That(keep, Is.True);
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
            Assert.That(intent.Attack.SupportReturnHex, Is.Null);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
        }

        // Before the assault march only: a committed Assault never asks Production for support
        // (CommittedAssault_FreshIntelNeverCreatesASupportArmyDemand).
        [Test]
        public void LostAttackSupport_WeakPrimaryStaysRequestableForTheCurrentPass()
        {
            using IDisposable coverage = Coverage(required: true);
            var primary = new ArmyData { Owner = Us, Hex = EnRoute };
            primary.Members.Add(new UnitData { Owner = Us });
            ArmyRegistry.Register(primary);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement,
                primary.Id, supportId: 9999);
            intent.Attack.AssaultStarted = false;
            intent.Attack.ReinforcementRequestedTurn = 5;
            WorldSnapshot snap = DefendedSite(
                new[] { Army(primary.Id, EnRoute, Weak()) }, new[] { OurBase });

            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent,
                intent.Attack, null, out _);

            Assert.That(keep, Is.True);
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
            Assert.That(intent.Attack.ReinforcementRequestedTurn, Is.EqualTo(-1));
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Reinforcement));

            ActorCommitments commitments = ActorCommitments.FromIntents(
                new[] { intent }, snap, null);
            var inventory = new CapabilityInventory
            {
                ReusableEmptyArmies = new[] { new ArmyData { Owner = Us, Hex = OurBase } },
            };
            var demands = new List<AxisDemand>();
            AggressionDemandEvaluator.AppendAttackDemands(snap, new[] { intent }, commitments,
                inventory, new List<string>(), demands);

            Assert.That(demands, Has.Count.EqualTo(1));
            Assert.That(demands[0].Capability, Is.EqualTo(CapabilityKind.FieldCombatPower));
            Assert.That(demands[0].ConsumerMissionKind, Is.EqualTo(MissionKind.Attack));
            Assert.That(demands[0].ConsumerIntentKey, Is.EqualTo(intent.IntentKey));
        }

        [Test]
        public void SuccessfulAttackSupportDelivery_BindsActorAndStampsDeliveryTurn()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7);
            MissionIntentRegistry.GetOrCreate(Us).Put(intent);
            WorldSnapshot snap = DefendedSite(
                new[] { Army(7, EnRoute, Weak()), Army(8, EnRoute, Strong()) },
                new[] { OurBase });
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                ConsumerIntentKey = intent.IntentKey,
                ConsumerMissionKind = MissionKind.Attack,
            };

            bool handedOff = CapabilityDeliveryEvaluator.TryHandoffGroundCombatSupport(
                Us, snap, demand, new[] { 8 }, turnNumber: 6);

            Assert.That(handedOff, Is.True);
            Assert.That(intent.Attack.SupportArmyId, Is.EqualTo(8));
            Assert.That(intent.Attack.ReinforcementRequestedTurn, Is.EqualTo(6));
        }

        // Before the assault march only (a Gather that fell apart): a committed Assault never
        // withdraws for weak odds (CommittedAssault_FreshIntelWithoutSupportContinuesTheAssault).
        [Test]
        public void NoReinforcementProspect_WithdrawsAStartedOperationAndRetiresAnUnstartedOne()
        {
            using IDisposable coverage = Coverage(required: true);
            MissionIntent started = AttackIntent(AttackMissionPhase.Reinforcement, 7);
            started.Attack.AssaultStarted = false;
            bool keepStarted = MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()) }, new[] { OurBase, AltBase }),
                started, started.Attack, null, out _);

            Assert.That(keepStarted, Is.True);
            Assert.That(started.Attack.Phase, Is.EqualTo(AttackMissionPhase.RecoveryReturn));
            Assert.That(started.Attack.RecoveryBaseHex.HasValue, Is.True,
                "recovery goes to the best reachable OWN base, never a hardcoded citadel");

            MissionIntent neverStarted = AttackIntent(AttackMissionPhase.Reinforcement, 7,
                started: false);
            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us,
                    DefendedSite(new[] { Army(7, EnRoute, Weak()) }, new[] { OurBase }),
                    neverStarted, neverStarted.Attack, null, out _),
                Is.False, "nothing was spent physically, so there is nothing to withdraw");
        }

        // ---- §83 N / §47 / §62 recovery base lifecycle -----------------------------------

        [Test]
        public void RecoveryBase_IsRetargetedWhenLost_AndEndsTheOperationOnArrival()
        {
            MissionIntent lost = AttackIntent(AttackMissionPhase.RecoveryReturn, 7,
                recoveryBase: AltBase);
            // AltBase left Self.BaseHexes — it is no longer ours.
            bool keepLost = MissionContinuityLayer.ResolveAttackIntent(Us,
                Snap(new[] { Building(RedBase, Red) }, new[] { OurBase },
                    new[] { Army(7, EnRoute, Weak()) }),
                lost, lost.Attack, null, out _);

            Assert.That(keepLost, Is.True);
            Assert.That(lost.Attack.RecoveryBaseHex, Is.EqualTo(OurBase));

            MissionIntent arrived = AttackIntent(AttackMissionPhase.RecoveryReturn, 7,
                recoveryBase: OurBase);
            bool keepArrived = MissionContinuityLayer.ResolveAttackIntent(Us,
                Snap(new[] { Building(RedBase, Red) }, new[] { OurBase },
                    new[] { Army(7, OurBase, Weak()) }),
                arrived, arrived.Attack, null, out bool arrivedSuccess);

            Assert.That(keepArrived, Is.False);
            Assert.That(arrivedSuccess, Is.False, "withdrawing home is not a capture");

            MissionIntent homeless = AttackIntent(AttackMissionPhase.RecoveryReturn, 7,
                recoveryBase: AltBase);
            Assert.That(MissionContinuityLayer.ResolveAttackIntent(Us,
                    Snap(new[] { Building(RedBase, Red) }, Array.Empty<HexCoord>(),
                        new[] { Army(7, EnRoute, Weak()) }),
                    homeless, homeless.Attack, null, out _),
                Is.False, "no own base left to withdraw to");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecoveryReturn_ContinuesWhenFormerTargetChanges(bool capturedByUs)
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.RecoveryReturn, 7,
                recoveryBase: OurBase);
            WorldSnapshot snap = Snap(
                capturedByUs ? new[] { Building(RedBase, Us) }
                    : new[] { Building(RedBase, Blue) },
                capturedByUs ? new[] { OurBase, RedBase } : new[] { OurBase },
                new[] { Army(7, EnRoute, Weak()) });

            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent,
                intent.Attack, null, out bool success);

            Assert.That(keep, Is.True, "withdrawal follows its own base destination");
            Assert.That(success, Is.False);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.RecoveryReturn));
            Assert.That(intent.Attack.RecoveryBaseHex, Is.EqualTo(OurBase));
        }

        [Test]
        public void UnboundAttack_SpentMovementIsNotACombatPowerShortage()
        {
            ArmySnapshot actor = Army(7, EnRoute, Strong());
            actor.CurrentMovement = 0;
            WorldSnapshot snap = Snap(new[] { Building(RedBase, Red) },
                new[] { OurBase }, new[] { actor });
            snap.Self.BestStackPotential = 20f;
            snap.Self.FieldPotential = 1f;
            var demands = new List<AxisDemand>();
            var diagnostics = new List<string>();

            AggressionDemandEvaluator.AppendAttackDemands(snap, Array.Empty<MissionIntent>(),
                null, null, diagnostics, demands);

            Assert.That(demands, Is.Empty);
            Assert.That(diagnostics.Exists(line =>
                line.Contains("existing_force_waits_for_movement")), Is.True);
        }

        [Test]
        public void UnboundAttackDelivery_CountsOnlyPowerAddedToItsNamedFist()
        {
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                ConsumerMissionKind = MissionKind.Attack,
            };
            var fist = new ArmyData { Owner = Us, Hex = OurBase };
            ArmyRegistry.Register(fist);
            int fistId = fist.Id;
            demand.AttackFistArmyId = fistId;
            var other = new ArmyData { Owner = Us, Hex = OurBase };
            ArmyRegistry.Register(other);
            var before = new WorldSnapshot { Self = new SelfSnapshot { Armies = new[]
            {
                new ArmySnapshot { ArmyId = fistId, EffectiveArmyPower = 10f },
            } } };
            var after = new WorldSnapshot { Self = new SelfSnapshot { Armies = new[]
            {
                new ArmySnapshot { ArmyId = fistId, EffectiveArmyPower = 15f },
                new ArmySnapshot { ArmyId = other.Id, EffectiveArmyPower = 30f },
            } } };
            var wrong = new MaterializationPlan
            {
                Deploy = new PlacementOption(OurBase, DeploymentKind.ExistingArmy, other),
            };
            var right = new MaterializationPlan
            {
                Deploy = new PlacementOption(OurBase, DeploymentKind.ExistingArmy, fist),
            };
            var ctx = new AiTurnContext { TurnNumber = 30 };
            Assert.That(MaterializationDeliveryPolicy.AssessDemandOperationally(
                wrong, demand, after, Us, ctx).FailureReason,
                Is.EqualTo(MaterializationDeliveryPolicy.DeliveryFailureReason.AttackFistNotStrengthened));
            Assert.That(CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(
                Us, ctx, after, wrong, demand, new CapabilityInventory(),
                new CapabilityInventory(), new HashSet<int> { fistId, other.Id },
                out float unrelated, before), Is.False);
            Assert.That(unrelated, Is.Zero);
            Assert.That(CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(
                Us, ctx, after, right, demand, new CapabilityInventory(),
                new CapabilityInventory(), new HashSet<int> { fistId, other.Id },
                out float added, before), Is.True);
            Assert.That(added, Is.EqualTo(5f));
        }

        [Test]
        public void SupportReturn_ReleasesTheSupportAndResumesTheAssault()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.SupportReturn, 7, supportId: 8);
            WorldSnapshot snap = DefendedSite(
                new[] { Army(7, EnRoute, Strong()), Army(8, OurBase, Weak()) }, new[] { OurBase });

            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, intent.Attack, null,
                out _);

            Assert.That(keep, Is.True);
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
        }

        // ---- §40 the mission proposal, and §83 R one army one mission ---------------------

        [TestCase(0.79f, false)]
        [TestCase(0.81f, true)]
        public void FreshAssault_WaitsForTheDynamicForceThreshold(float share, bool expected)
        {
            ArmySnapshot actor = Army(7, EnRoute, Strong());
            WorldSnapshot snap = DefendedSite(new[] { actor }, new[] { OurBase },
                alsoOwnBuilding: true);
            snap.Self.TotalMilitaryPotential = actor.EffectiveArmyPower / share;
            var proposals = new List<MissionProposal>();
            AggressionMissionLayer.AppendAttack(snap, Array.Empty<MissionIntent>(),
                new HashSet<int>(), proposals, null,
                new Dictionary<MissionIntentKey, string>());
            Assert.That(proposals.Any(p => p.Target is AttackMissionTarget t
                && t.Phase == AttackMissionPhase.Assault), Is.EqualTo(expected));
        }

        [Test]
        public void AppendAttack_ProducesOneProposalNamingTheAssignedPrimaryAndTheSite()
        {
            WorldSnapshot snap = DefendedSite(new[] { Army(7, EnRoute, Strong()) },
                new[] { OurBase }, alsoOwnBuilding: true);
            var proposals = new List<MissionProposal>();

            AggressionMissionLayer.AppendAttack(snap, Array.Empty<MissionIntent>(),
                new HashSet<int>(), proposals, null,
                new Dictionary<MissionIntentKey, string>());

            Assert.That(proposals.Count, Is.EqualTo(1));
            MissionProposal p = proposals[0];
            Assert.That(p.Kind, Is.EqualTo(MissionKind.Attack));
            Assert.That(p.PreferredMoverArmyId, Is.EqualTo(7));
            Assert.That(p.Axes.Value.ContainsKey(DesireAxis.Aggression), Is.True,
                "Attack serves the existing Aggression axis; there is no DesireAxis.Attack");
            Assert.That(MissionIntentKey.For(p).Kind, Is.EqualTo(MissionKind.Attack));
            var target = (AttackMissionTarget)p.Target;
            Assert.That(target.Phase, Is.EqualTo(AttackMissionPhase.Assault));
            Assert.That(target.Target.Hex, Is.EqualTo(RedBase));
            Assert.That(target.DefenderCount, Is.EqualTo(2),
                "the whole site garrison/army package is one fight");
        }

        [Test]
        public void AppendAttack_AnArmyClaimedByAnotherMissionYieldsNoProposal()
        {
            WorldSnapshot snap = DefendedSite(new[] { Army(7, EnRoute, Strong()) },
                new[] { OurBase }, alsoOwnBuilding: true);
            var proposals = new List<MissionProposal>();

            AggressionMissionLayer.AppendAttack(snap, Array.Empty<MissionIntent>(),
                new HashSet<int> { 7 }, proposals, null,
                new Dictionary<MissionIntentKey, string>());

            Assert.That(proposals, Is.Empty,
                "actor contention is the allocator's problem, never a second Attack path");
        }

        // ---- §17 the once-per-turn side-strike marker ----------------------------------

        [Test]
        public void AppendAttack_FreezesTheOperationsOwnStrikeMarkerIntoTheLeg()
        {
            WorldSnapshot snap = DefendedSite(new[] { Army(7, EnRoute, Strong()) },
                new[] { OurBase }, alsoOwnBuilding: true);

            var fresh = new List<MissionProposal>();
            AggressionMissionLayer.AppendAttack(snap, Array.Empty<MissionIntent>(),
                new HashSet<int>(), fresh, null,
                new Dictionary<MissionIntentKey, string>());
            Assert.That(((AttackMissionTarget)fresh[0].Target).OpportunisticStrikeTurn,
                Is.EqualTo(0),
                "a fresh objective has taken no strike, and 0 can never equal a real turn");

            MissionIntent incumbent = AttackIntent(AttackMissionPhase.Assault, 7);
            incumbent.Attack.LastOpportunisticStrikeTurn = snap.TurnNumber;
            var carried = new List<MissionProposal>();
            AggressionMissionLayer.AppendAttack(snap, new[] { incumbent },
                new HashSet<int>(), carried, null,
                new Dictionary<MissionIntentKey, string>());

            Assert.That(((AttackMissionTarget)carried[0].Target).OpportunisticStrikeTurn,
                Is.EqualTo(snap.TurnNumber),
                "the executor must read the marker as a frozen leg fact, not from intent state");
        }

        [Test]
        public void CreateAttackIntent_IsBornHavingAlreadySpentTheTurnsStrike()
        {
            var state = new MissionIntentState();
            MissionStepResult outcome = AttackOutcome(strikeSpent: true);

            MissionContinuityLayer.CreateAttackIntent(state, outcome, turn: 6);

            Assert.That(state.TryGet(outcome.IntentKey, out MissionIntent created), Is.True);
            Assert.That(created.Attack.LastOpportunisticStrikeTurn, Is.EqualTo(6),
                "an operation that BEGAN with its diversion must not get a second one this turn");
        }

        [Test]
        public void CreateAttackIntent_WithoutAStrike_InheritsTheLegsMarker()
        {
            var state = new MissionIntentState();
            MissionStepResult outcome = AttackOutcome(strikeSpent: false);

            MissionContinuityLayer.CreateAttackIntent(state, outcome, turn: 6);

            Assert.That(state.TryGet(outcome.IntentKey, out MissionIntent created), Is.True);
            Assert.That(created.Attack.LastOpportunisticStrikeTurn, Is.EqualTo(0),
                "no strike this turn leaves the operation free to take one");
        }

        // ---- ATK review P0-1 — the per-cycle provisioning key is per OPERATION ------------

        // Every Attack proposal used to fall through StableMissionKey.For onto one fallback key.
        // That single key is what PrepareGroundCombatAssignments maps actors by, what
        // AlreadyProvisioned/_rejectedThisTurn/cooldowns are recorded against, and what
        // ExcludedForGroundCombat compares to decide whether an assigned army is "ours" — so two
        // objectives silently shared one slot.
        [Test]
        public void StableMissionKey_TwoAttackObjectives_AreTwoDistinctKeys()
        {
            StableMissionKey red = StableMissionKey.For(AttackProposal(
                AttackMissionPhase.Assault, RedBase, Red, primaryId: 7));
            StableMissionKey blue = StableMissionKey.For(AttackProposal(
                AttackMissionPhase.Assault, new HexCoord(9, 0), Blue, primaryId: 8));

            Assert.That(red, Is.Not.EqualTo(blue));
            Assert.That(red.Kind, Is.EqualTo(MissionKind.Attack));
            Assert.That(red, Is.EqualTo(StableMissionKey.For(AttackProposal(
                    AttackMissionPhase.Assault, RedBase, Red, primaryId: 7))),
                "the same operation must key identically across a re-pack");
        }

        [Test]
        public void StableMissionKey_SameTargetUnderANewOwner_IsADifferentOperation()
        {
            Assert.That(
                StableMissionKey.For(AttackProposal(AttackMissionPhase.Assault, RedBase, Red, 7)),
                Is.Not.EqualTo(
                    StableMissionKey.For(AttackProposal(AttackMissionPhase.Assault, RedBase, Blue, 7))),
                "ownership is part of Attack identity, exactly as in MissionIntentKey.ForAttack");
        }

        [Test]
        public void StableMissionKey_LegsOfOneOperation_DoNotCollide()
        {
            StableMissionKey assault = StableMissionKey.For(
                AttackProposal(AttackMissionPhase.Assault, RedBase, Red, 7));
            StableMissionKey reinforcement = StableMissionKey.For(
                AttackProposal(AttackMissionPhase.Reinforcement, RedBase, Red, 7));
            StableMissionKey recovery = StableMissionKey.For(
                AttackProposal(AttackMissionPhase.RecoveryReturn, RedBase, Red, 7));

            Assert.That(new HashSet<StableMissionKey> { assault, reinforcement, recovery }.Count,
                Is.EqualTo(3));
        }

        // ---- ATK review P0-2 — the unpinned reinforcement leg -----------------------------

        // The demand layer deliberately answers "an existing free army can solve this, materialise
        // nothing". If the planner then also holds, nothing in the pipeline ever joins the two
        // armies and the operation sits in Reinforcement forever. This leg is what the batch
        // assignment and AttackProvisioner were already written to consume.
        [Test]
        public void AppendAttack_ReinforcementWithAFreeArmy_ProposesAnUnpinnedLeg()
        {
            WorldSnapshot snap = DefendedSite(
                new[] { Army(7, EnRoute, Weak()), Army(9, EnRoute, Strong()) },
                new[] { OurBase }, alsoOwnBuilding: true);
            // Before the assault march (a gather that fell apart): the batch solve picks the support.
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7);
            intent.Attack.AssaultStarted = false;
            var proposals = new List<MissionProposal>();

            AggressionMissionLayer.AppendAttack(snap, new[] { intent },
                new HashSet<int> { 7 }, proposals, null,
                new Dictionary<MissionIntentKey, string>());

            MissionProposal leg = proposals.Find(p => p.Target is AttackMissionTarget t
                && t.Phase == AttackMissionPhase.Reinforcement);
            Assert.That(leg, Is.Not.Null, "an existing free support must be proposed, not held");
            var target = (AttackMissionTarget)leg.Target;
            Assert.That(target.SupportArmyId, Is.Null,
                "the actor is the batch solve's decision, never a private free-army pick");
            Assert.That(target.PrimaryArmyId, Is.EqualTo(7));
            Assert.That(GroundCombatAdmissionRegistry.TryGet(leg, out HashSet<int> eligible),
                Is.True);
            Assert.That(eligible, Does.Contain(9));
        }

        [Test]
        public void AppendAttack_ReinforcementWithNoFreeArmy_HoldsForDemand()
        {
            WorldSnapshot snap = DefendedSite(new[] { Army(7, EnRoute, Weak()) },
                new[] { OurBase }, alsoOwnBuilding: true);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7);
            var proposals = new List<MissionProposal>();

            AggressionMissionLayer.AppendAttack(snap, new[] { intent },
                new HashSet<int> { 7 }, proposals, null,
                new Dictionary<MissionIntentKey, string>());

            Assert.That(proposals.Exists(p => p.Target is AttackMissionTarget t
                    && t.Phase == AttackMissionPhase.Reinforcement), Is.False,
                "asking for a NEW capability is the Demand layer's decision, not the planner's");
        }

        // 2026-10-04 — a committed Reinforcement proposes both halves: the primary walks along its
        // route to the rendezvous, the bound support walks to the same hex (not to the primary).
        [Test]
        public void AppendAttack_CommittedReinforcement_BothArmiesWalkToTheRendezvous()
        {
            HexCoord meet = new HexCoord(5, 0);
            WorldSnapshot snap = DefendedSite(
                new[] { Army(7, EnRoute, Weak()), Army(8, new HexCoord(3, 0), Strong()) },
                new[] { OurBase }, alsoOwnBuilding: true);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7, supportId: 8);
            intent.Attack.RendezvousHex = meet;
            var proposals = new List<MissionProposal>();

            AggressionMissionLayer.AppendAttack(snap, new[] { intent },
                new HashSet<int> { 7, 8 }, proposals, null,
                new Dictionary<MissionIntentKey, string>());

            List<MissionProposal> legProposals = proposals
                .Where(p => p.Target is AttackMissionTarget t && t.Phase == AttackMissionPhase.Reinforcement)
                .ToList();
            Assert.That(legProposals, Has.Count.EqualTo(2));
            List<AttackMissionTarget> legs = legProposals.Select(p => (AttackMissionTarget)p.Target).ToList();
            Assert.That(legs.Single(t => t.PrimaryRendezvousLeg).DestinationHex, Is.EqualTo(meet));
            Assert.That(legs.Single(t => !t.PrimaryRendezvousLeg).DestinationHex, Is.EqualTo(meet));
            Assert.That(MissionAdmissionPolicy.Conflicts(legProposals[0], legProposals[1]), Is.False,
                "the two halves are complementary, funded together");
            Assert.That(StableMissionKey.For(legProposals[0]),
                Is.Not.EqualTo(StableMissionKey.For(legProposals[1])));
        }

        // ---- ATK review P2-5/P2-6 — Attack rides the Aggression lane/axis and pool --------

        [Test]
        public void AttackBelongsToTheAggressionLaneAndAxis()
        {
            MissionProposal attack = AttackProposal(AttackMissionPhase.Assault, RedBase, Red, 7);
            var raid = new MissionProposal
            {
                Kind = MissionKind.Raid,
                Target = new RaidMissionTarget { Phase = RaidMissionPhase.Assault },
            };

            Assert.That(MissionAdmissionPolicy.LaneFor(attack),
                Is.EqualTo(MissionAdmissionPolicy.LaneFor(raid)));
            Assert.That(AiStrategyV2Scope.AxisOf(MissionKind.Attack),
                Is.EqualTo(DesireAxis.Aggression),
                "there is deliberately no DesireAxis.Attack, and it is not Development either");
            Assert.That(CapabilityPoolExhaustionRegistry.PoolFor(attack),
                Is.EqualTo(CapabilityPoolExhaustionRegistry.PoolFor(raid)));
        }

        // ---- 2026-10-04 committed Assault: fresh intel never revokes the operation -------------
        //
        // "Coverage required" stands in for "fresh defenders: the primary no longer clears the
        // site" (with Attack's zero win gate, coverage is the only thing a committed primary can
        // lose); the default test behavior (coverage off) is covered by the low-win case.

        [TestCase(true)]
        [TestCase(false)]
        public void CommittedAssault_FreshIntelWithoutSupportContinuesTheAssault(bool coverageRequired)
        {
            using IDisposable coverage = Coverage(coverageRequired);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()) }, new[] { OurBase, AltBase }),
                intent, intent.Attack, null, out bool captured);

            Assert.That(keep, Is.True);
            Assert.That(captured, Is.False);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault),
                "no existing reinforcement: the primary keeps marching, it neither waits nor withdraws");
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
            Assert.That(intent.Attack.RecoveryBaseHex, Is.Null);
        }

        [Test]
        public void CommittedAssault_FreshIntelNeverCreatesASupportArmyDemand()
        {
            using IDisposable coverage = Coverage(required: true);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            WorldSnapshot snap = DefendedSite(new[] { Army(7, EnRoute, Weak()) }, new[] { OurBase });
            MissionContinuityLayer.ResolveAttackIntent(Us, snap, intent, intent.Attack, null, out _);
            // A reusable shell exists: before commitment this would be an IndependentFieldArmy.
            var inventory = new CapabilityInventory
            {
                ReusableEmptyArmies = new[] { new ArmyData { Owner = Us, Hex = OurBase } },
            };
            var diag = new List<string>();
            var demands = new List<AxisDemand>();
            AggressionDemandEvaluator.AppendAttackDemands(snap, new[] { intent }, null, inventory,
                diag, demands);

            Assert.That(demands.Where(d => d.ConsumerIntentKey.Equals(intent.IntentKey)), Is.Empty);
            Assert.That(diag.Any(d => d.Contains("committed_assault_uses_existing_support_only")), Is.True);
        }

        [Test]
        public void CommittedAssault_UsefulSupportIsInterceptedAheadOnThePrimarysRoute()
        {
            using IDisposable coverage = Coverage(required: true);
            // Primary at (4,0) marching on (6,0); the support one hex behind it.
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()), Army(8, new HexCoord(3, 0), Strong()) },
                    new[] { OurBase }),
                intent, intent.Attack, null, out _);

            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Reinforcement));
            Assert.That(intent.Attack.SupportArmyId, Is.EqualTo(8));
            Assert.That(intent.Attack.RendezvousHex, Is.EqualTo(new HexCoord(5, 0)),
                "the support catches up ahead, the primary keeps advancing");
            Assert.That(HexGridMath.Distance(intent.Attack.RendezvousHex.Value, RedBase),
                Is.LessThanOrEqualTo(HexGridMath.Distance(EnRoute, RedBase)), "never behind the primary");
        }

        [Test]
        public void CommittedAssault_SupportThatDoesNotImproveThePrimaryIsIgnored()
        {
            using IDisposable coverage = Coverage(required: true);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()), Army(8, new HexCoord(3, 0), Weak()) },
                    new[] { OurBase }),
                intent, intent.Attack, null, out _);

            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
        }

        [Test]
        public void CommittedAssault_SupportTooFarBehindIsRejectedAndTheAssaultContinues()
        {
            using IDisposable coverage = Coverage(required: true);
            // Meeting it would hold the primary for several turns (or send it back): rejected.
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()), Army(8, new HexCoord(-4, 0), Strong()) },
                    new[] { OurBase }),
                intent, intent.Attack, null, out _);

            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
            Assert.That(intent.Attack.RendezvousHex, Is.Null);
        }

        [Test]
        public void Rendezvous_IsAlwaysOnThePrimarysRouteAndNeverTheTarget()
        {
            ArmySnapshot primary = Army(7, EnRoute, Weak());
            ArmySnapshot ahead = Army(8, new HexCoord(5, -1), Strong());
            RendezvousPlan? plan = GroundCombatRendezvous.SelectForward(null, primary, ahead, RedBase,
                AiConfigV2.attackReinforcementMaxWaitTurns, out _);

            Assert.That(plan.HasValue, Is.True);
            Assert.That(plan.Value.Hex, Is.Not.EqualTo(RedBase));
            Assert.That(plan.Value.WaitTurns, Is.EqualTo(0), "a support ahead waits for the primary");
            Assert.That(HexGridMath.Distance(plan.Value.Hex, RedBase),
                Is.LessThanOrEqualTo(HexGridMath.Distance(EnRoute, RedBase)));

            ArmySnapshot farBehind = Army(9, new HexCoord(-4, 0), Strong());
            Assert.That(GroundCombatRendezvous.SelectForward(null, primary, farBehind, RedBase,
                AiConfigV2.attackReinforcementMaxWaitTurns, out string why).HasValue, Is.False);
            Assert.That(why, Does.Contain("no non-retreat rendezvous"));
        }

        [Test]
        public void CommittedAssault_LostSupportFallsBackToTheAssaultNotRecovery()
        {
            using IDisposable coverage = Coverage(required: true);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7, supportId: 9999);
            intent.Attack.RendezvousHex = new HexCoord(5, 0);
            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()) }, new[] { OurBase, AltBase }),
                intent, intent.Attack, null, out _);

            Assert.That(keep, Is.True);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault));
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
            Assert.That(intent.Attack.RendezvousHex, Is.Null);
        }

        [Test]
        public void CommittedAssault_LostSupportIsReplacedByAnotherExistingOne()
        {
            using IDisposable coverage = Coverage(required: true);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7, supportId: 9999);
            intent.Attack.RendezvousHex = new HexCoord(5, 0);
            MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()), Army(8, EnRoute, Strong()) },
                    new[] { OurBase }),
                intent, intent.Attack, null, out _);

            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Reinforcement));
            Assert.That(intent.Attack.SupportArmyId, Is.EqualTo(8));
        }

        [Test]
        public void CommittedAssault_NonCombatRemnantStillWithdraws()
        {
            ArmySnapshot remnant = Army(7, EnRoute, Weak());
            remnant.IsStructuralRaidActor = false;
            MissionIntent intent = AttackIntent(AttackMissionPhase.Assault, 7);
            bool keep = MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { remnant }, new[] { OurBase, AltBase }),
                intent, intent.Attack, null, out _);

            Assert.That(keep, Is.True);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.RecoveryReturn),
                "a primary that can no longer fight at all is a structural end, not fresh intel");
        }

        [Test]
        public void CommittedHandoff_SupportWalksHomeBesideTheResumedAssault()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7, supportId: 8);
            intent.Attack.RendezvousHex = EnRoute;
            MissionIntentRegistry.GetOrCreate(Us).Put(intent);
            MissionStepResult outcome = AttackOutcome(strikeSpent: false);
            outcome.MoverArmyId = 8;
            outcome.MadeProgress = true;
            outcome.Disposition = MissionStepDisposition.Progress;
            outcome.GroundFactsForWrite().ReinforcementHandoffAttempted = true;
            AttackMissionTarget leg = outcome.AttackFacts().AttackTarget;
            leg.Phase = AttackMissionPhase.Reinforcement;
            leg.SupportArmyId = 8;
            leg.DestinationHex = EnRoute;
            outcome.AttackFactsForWrite().AttackTarget = leg;

            MissionContinuityLayer.ReconcileStep(Us, 6, outcome);

            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Assault),
                "the primary does not wait for the support's walk home");
            Assert.That(intent.Attack.SupportArmyId, Is.Null);
            Assert.That(intent.Attack.RendezvousHex, Is.Null);
            Assert.That(intent.Attack.GatherReturns.Select(r => r.ArmyId), Does.Contain(8));
        }

        [Test]
        public void GatherThenAssault_FirstExecutedMarchStepCommitsTheOperation()
        {
            MissionIntent intent = AttackIntent(AttackMissionPhase.Gather, 7);
            Assert.That(intent.Attack.AssaultStarted, Is.False);
            // Continuity turned the gather into an Assault; nothing has marched yet.
            intent.Attack.Phase = AttackMissionPhase.Assault;
            MissionIntentRegistry.GetOrCreate(Us).Put(intent);
            MissionStepResult outcome = AttackOutcome(strikeSpent: false);
            outcome.MadeProgress = true;
            outcome.StepsMoved = 1;
            outcome.Disposition = MissionStepDisposition.Progress;

            MissionContinuityLayer.ReconcileStep(Us, 6, outcome);

            Assert.That(intent.Attack.AssaultStarted, Is.True);
        }

        // ---- helpers ---------------------------------------------------------------------

        private sealed class CoverageSwitch : IDisposable
        {
            private readonly bool _previous;
            internal CoverageSwitch(bool required)
            {
                _previous = AiConfigV2.attackRequiresDefenderCoverage;
                AiConfigV2.attackRequiresDefenderCoverage = required;
            }
            public void Dispose() => AiConfigV2.attackRequiresDefenderCoverage = _previous;
        }

        private static IDisposable Coverage(bool required) => new CoverageSwitch(required);

        private static MissionProposal AttackProposal(AttackMissionPhase phase, HexCoord hex,
            PlayerSetupData owner, int primaryId)
        {
            AttackTargetRef target = AttackTargetRef.For(hex, owner, AttackTargetKind.Base);
            return new MissionProposal
            {
                Kind = MissionKind.Attack,
                PreferredMoverArmyId = primaryId,
                Target = new AttackMissionTarget
                {
                    Phase = phase,
                    Target = target,
                    PrimaryArmyId = primaryId,
                    DestinationHex = phase == AttackMissionPhase.Assault ? hex : OurBase,
                },
            };
        }

        private static WorthIt.DefenderProfile Body(float atk, float def, float hp, int init) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, init, null, hp);

        private static AiMapMemory.KnownBuilding Building(HexCoord hex, PlayerSetupData owner,
            bool isBase = true, bool citadel = false, int seenTurn = 5) =>
            new AiMapMemory.KnownBuilding(hex, owner, citadel, null, null, 0, isBase, 0f, seenTurn);

        private static AiMapMemory.KnownEnemySighting Sighting(int armyId, HexCoord hex,
            params WorthIt.DefenderProfile[] bodies) =>
            new AiMapMemory.KnownEnemySighting(hex, Red, "site", bodies.Length, 0f, 0f,
                new List<WorthIt.DefenderProfile>(bodies), false, 0, 0, 5, armyId);

        private static ArmySnapshot Army(int id, HexCoord hex,
            IReadOnlyList<WorthIt.DefenderProfile> members) => new ArmySnapshot
        {
            ArmyId = id, Owner = Us, Hex = hex, IsStructuralRaidActor = true,
            MemberCount = members.Count, MaxMovement = 3, CurrentMovement = 3, Members = members,
            EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
            ReachableOwnBaseHexes = new[] { OurBase, AltBase },
        };

        private static WorldSnapshot Snap(IEnumerable<AiMapMemory.KnownBuilding> buildings,
            IEnumerable<HexCoord> ownBases, IEnumerable<ArmySnapshot> armies,
            IEnumerable<AiMapMemory.KnownEnemySighting> sightings = null) => new WorldSnapshot
        {
            TurnNumber = 6,
            Observer = Us,
            Self = new SelfSnapshot
            {
                BaseHexes = new List<HexCoord>(ownBases),
                Citadel = OurBase,
                Armies = new List<ArmySnapshot>(armies),
                // Existing lane cases concern assembly/marching after mobilization. Closed-gate
                // behavior has an explicit case; an omitted field is not an open-gate witness.
                DeployedPower = 75f, AvailablePower = 100f,
                TotalMilitaryPotential = armies.Select(a => a.EffectiveArmyPower)
                    .DefaultIfEmpty(0f).Max(),
            },
            Known = new KnownSnapshot
            {
                Buildings = new List<AiMapMemory.KnownBuilding>(buildings),
                EnemySightings = sightings != null
                    ? new List<AiMapMemory.KnownEnemySighting>(sightings)
                    : new List<AiMapMemory.KnownEnemySighting>(),
            },
            Threat = new ThreatModel
            {
                Contacts = new List<EnemyContactSnapshot>(),
                Threats = new List<AssetThreatSnapshot>(),
            },
        };

        // RedBase, defended by a two-body package, with the given own armies.
        private static WorldSnapshot DefendedSite(IEnumerable<ArmySnapshot> armies,
            IEnumerable<HexCoord> ownBases, bool alsoOwnBuilding = false)
        {
            var buildings = new List<AiMapMemory.KnownBuilding>();
            if (alsoOwnBuilding)
                buildings.Add(Building(OurBase, Us));
            buildings.Add(Building(RedBase, Red));
            return Snap(buildings, ownBases, armies,
                new[] { Sighting(50, RedBase, SiteDefenders()) });
        }

        private static MissionStepResult AttackOutcome(bool strikeSpent)
        {
            AttackTargetRef target = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base);
            return new MissionStepResult
            {
                MissionKind = MissionKind.Attack,
                IntentKey = MissionIntentKey.ForAttack(target),
                MoverArmyId = 7,
            }
            .WithPayload(new GroundCombatStepPayload { OperationStarted = true })
            .WithPayload(new AttackStepPayload
            {
                HasAttackPayload = true,
                AttackOpportunisticStrike = strikeSpent,
                AttackTarget = new AttackMissionTarget
                {
                    Phase = AttackMissionPhase.Assault,
                    Target = target,
                    PrimaryArmyId = 7,
                    DestinationHex = RedBase,
                },
            });
        }

        private static MissionIntent AttackIntent(AttackMissionPhase phase, int primaryId,
            bool started = true, int? supportId = null, HexCoord? recoveryBase = null)
        {
            var payload = new Game.Ai.V2.AttackIntent
            {
                Target = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base),
                Phase = phase,
                OperationStarted = started,
                AssaultStarted = started && phase != AttackMissionPhase.Gather,
                PrimaryArmyId = primaryId,
                SupportArmyId = supportId,
                RecoveryBaseHex = recoveryBase,
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Attack,
                Status = IntentStatus.Active,
                Funding = CommitmentTier.Hard,
                Objective = payload,
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        // 2026-10-02 — internal selection priority: nearer (whole march-turn buckets) first, then the
        // less defended site, then the lower-bonus hex; the external score only breaks the last tie.
        [Test]
        public void TargetSelection_NearerThenLessDefended()
        {
            var near = new AttackObjectiveEvaluator.SelectionPriority(2, 50f, 0f, 1f);
            var far = new AttackObjectiveEvaluator.SelectionPriority(3, 5f, 0f, 99f);
            Assert.That(AttackObjectiveEvaluator.CompareSelection(near, far), Is.LessThan(0),
                "a nearer bucket beats a weaker but farther site and a higher score");

            var weak = new AttackObjectiveEvaluator.SelectionPriority(2, 10f, 4f, 1f);
            var strong = new AttackObjectiveEvaluator.SelectionPriority(2, 30f, 0f, 99f);
            Assert.That(AttackObjectiveEvaluator.CompareSelection(weak, strong), Is.LessThan(0));

            var open = new AttackObjectiveEvaluator.SelectionPriority(2, 10f, 0f, 1f);
            var walled = new AttackObjectiveEvaluator.SelectionPriority(2, 10f, 4f, 99f);
            Assert.That(AttackObjectiveEvaluator.CompareSelection(open, walled), Is.LessThan(0));

            var unknown = new AttackObjectiveEvaluator.SelectionPriority(2, float.MaxValue, 0f, 99f);
            Assert.That(AttackObjectiveEvaluator.CompareSelection(strong, unknown), Is.LessThan(0),
                "an unobserved site is treated as the most defended in its bucket");
        }

        // 2026-10-02 — Raid / Active Defence commander: after the fight and the capacity, a Rapid
        // (free-activation) non-support hero outranks the ordinary one, who outranks the Support one.
        [Test]
        public void CommanderChoice_RapidBeforeOrdinaryBeforeSupport()
        {
            var proj = new HeroRoleEvaluator.CommandProjection(1f, 5, null);
            var rapid = new HeroRoleEvaluator.CommandCandidate(proj, 1, 1f, 6, 1, 0, rapidPreference: 1);
            var ordinary = new HeroRoleEvaluator.CommandCandidate(proj, 2, 9f, 9, 3, 1);
            var support = new HeroRoleEvaluator.CommandCandidate(proj, 0, 1f, 6, 1, 2);
            Assert.That(HeroRoleEvaluator.CompareCandidates(rapid, ordinary), Is.LessThan(0));
            Assert.That(HeroRoleEvaluator.CompareCandidates(ordinary, support), Is.LessThan(0));
            var weakerFight = new HeroRoleEvaluator.CommandCandidate(
                new HeroRoleEvaluator.CommandProjection(0.5f, 5, null), 1, 1f, 6, 1, 0, rapidPreference: 1);
            Assert.That(HeroRoleEvaluator.CompareCandidates(ordinary, weakerFight), Is.LessThan(0),
                "the fight outranks the free activation");
        }

        // 2026-10-02 — mobilization hysteresis: a gate held open by a recent preparation step counts
        // as open although this pass's measurement is below the bars; the raw measurement is unchanged.
        [Test]
        public void MobilizationGate_HeldStaysOpenWhileRawIsClosed()
        {
            var closed = new SelfSnapshot { DeployedPower = 10f, AvailablePower = 100f };
            Assert.That(AttackForceReadiness.MobilizationOpen(closed), Is.False);
            var held = new SelfSnapshot { DeployedPower = 10f, AvailablePower = 100f, MobilizationHeld = true };
            Assert.That(AttackForceReadiness.MobilizationRawOpen(held), Is.False);
            Assert.That(AttackForceReadiness.MobilizationOpen(held), Is.True);
        }

        [Test]
        public void ClosedMobilization_DoesNotDemandReinforcementForANewTarget()
        {
            WorldSnapshot snap = DefendedSite(new[] { Army(49, OurBase, Weak()) }, new[] { OurBase });
            snap.Self.DeployedPower = 10f;
            snap.Self.AvailablePower = 100f;
            var diagnostics = new List<string>();
            var demands = new List<AxisDemand>();
            AggressionDemandEvaluator.AppendAttackDemands(snap, Array.Empty<MissionIntent>(),
                ActorCommitments.FromIntents(Array.Empty<MissionIntent>(), snap, null),
                new CapabilityInventory(), diagnostics, demands);
            Assert.That(demands, Is.Empty);
            Assert.That(diagnostics.Any(x => x.Contains("mobilization_closed")), Is.True);
        }
    }
}
#endif
