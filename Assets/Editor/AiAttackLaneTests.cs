#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
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
                Is.EqualTo(AiGroundMoveAuthority.Transit),
                "an approach step must never capture a structure it happens to cross");
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
                Is.EqualTo(AiGroundMoveAuthority.Transit),
                "walking toward the contact is ordinary Transit");
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

            // Cannot clear, but a strong free army could join -> Reinforcement, no demand yet.
            MissionIntent needsHelp = AttackIntent(AttackMissionPhase.Assault, 7);
            MissionContinuityLayer.ResolveAttackIntent(Us,
                DefendedSite(new[] { Army(7, EnRoute, Weak()), Army(8, EnRoute, Strong()) },
                    new[] { OurBase }),
                needsHelp, needsHelp.Attack, null, out _);
            Assert.That(needsHelp.Attack.Phase, Is.EqualTo(AttackMissionPhase.Reinforcement));
            Assert.That(needsHelp.Attack.ReinforcementRequestedTurn, Is.EqualTo(-1));
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

        [Test]
        public void LostAttackSupport_WeakPrimaryStaysRequestableForTheCurrentPass()
        {
            var primary = new ArmyData { Owner = Us, Hex = EnRoute };
            primary.Members.Add(new UnitData { Owner = Us });
            ArmyRegistry.Register(primary);
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement,
                primary.Id, supportId: 9999);
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

        [Test]
        public void NoReinforcementProspect_WithdrawsAStartedOperationAndRetiresAnUnstartedOne()
        {
            MissionIntent started = AttackIntent(AttackMissionPhase.Reinforcement, 7);
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

        [Test]
        public void AppendAttack_ProducesOneProposalNamingTheAssignedPrimaryAndTheSite()
        {
            WorldSnapshot snap = DefendedSite(new[] { Army(7, EnRoute, Strong()) },
                new[] { OurBase }, alsoOwnBuilding: true);
            var proposals = new List<MissionProposal>();

            AggressionMissionLayer.AppendAttack(snap, Array.Empty<MissionIntent>(),
                new HashSet<int>(), proposals, null);

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
                new HashSet<int> { 7 }, proposals, null);

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
                new HashSet<int>(), fresh, null);
            Assert.That(((AttackMissionTarget)fresh[0].Target).OpportunisticStrikeTurn,
                Is.EqualTo(0),
                "a fresh objective has taken no strike, and 0 can never equal a real turn");

            MissionIntent incumbent = AttackIntent(AttackMissionPhase.Assault, 7);
            incumbent.Attack.LastOpportunisticStrikeTurn = snap.TurnNumber;
            var carried = new List<MissionProposal>();
            AggressionMissionLayer.AppendAttack(snap, new[] { incumbent },
                new HashSet<int>(), carried, null);

            Assert.That(((AttackMissionTarget)carried[0].Target).OpportunisticStrikeTurn,
                Is.EqualTo(snap.TurnNumber),
                "the executor must read the marker as a frozen leg fact, not from intent state");
        }

        [Test]
        public void CreateAttackIntent_IsBornHavingAlreadySpentTheTurnsStrike()
        {
            var state = new MissionIntentState();
            MissionTurnOutcome outcome = AttackOutcome(strikeSpent: true);

            MissionContinuityLayer.CreateAttackIntent(state, outcome, turn: 6);

            Assert.That(state.TryGet(outcome.IntentKey, out MissionIntent created), Is.True);
            Assert.That(created.Attack.LastOpportunisticStrikeTurn, Is.EqualTo(6),
                "an operation that BEGAN with its diversion must not get a second one this turn");
        }

        [Test]
        public void CreateAttackIntent_WithoutAStrike_InheritsTheLegsMarker()
        {
            var state = new MissionIntentState();
            MissionTurnOutcome outcome = AttackOutcome(strikeSpent: false);

            MissionContinuityLayer.CreateAttackIntent(state, outcome, turn: 6);

            Assert.That(state.TryGet(outcome.IntentKey, out MissionIntent created), Is.True);
            Assert.That(created.Attack.LastOpportunisticStrikeTurn, Is.EqualTo(0),
                "no strike this turn leaves the operation free to take one");
        }

        // ---- §49/§73 Attack is preemptable by ActiveDefence ------------------------------

        [Test]
        public void AttackCountsAsAnOffensiveOperationADefenceMayBorrowFrom()
        {
            Assert.That(MissionContinuityLayer.IsOffensiveGroundCombatIntent(
                    AttackIntent(AttackMissionPhase.Assault, 7)), Is.True);
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
            MissionIntent intent = AttackIntent(AttackMissionPhase.Reinforcement, 7);
            var proposals = new List<MissionProposal>();

            AggressionMissionLayer.AppendAttack(snap, new[] { intent },
                new HashSet<int> { 7 }, proposals, null);

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
                new HashSet<int> { 7 }, proposals, null);

            Assert.That(proposals.Exists(p => p.Target is AttackMissionTarget t
                    && t.Phase == AttackMissionPhase.Reinforcement), Is.False,
                "asking for a NEW capability is the Demand layer's decision, not the planner's");
        }

        // ---- ATK review P1-4 — ActiveDefence may borrow an Attack primary -----------------

        [Test]
        public void OffensiveAssaultOperation_CoversBothOffensiveLanes()
        {
            Assert.That(MissionContinuityLayer.TryOffensiveAssaultOperation(
                    AttackIntent(AttackMissionPhase.Assault, 7), out int primary,
                    out HexCoord hex), Is.True);
            Assert.That(primary, Is.EqualTo(7));
            Assert.That(hex, Is.EqualTo(RedBase), "the site is the operation's own hex");

            Assert.That(MissionContinuityLayer.TryOffensiveAssaultOperation(
                    AttackIntent(AttackMissionPhase.Reinforcement, 7), out _, out _), Is.False,
                "a leg mid-handoff is not borrowable");
            Assert.That(MissionContinuityLayer.TryOffensiveAssaultOperation(
                    AttackIntent(AttackMissionPhase.RecoveryReturn, 7, recoveryBase: OurBase),
                    out _, out _), Is.False,
                "an army already walking home is not borrowable");
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

        // ---- helpers ---------------------------------------------------------------------

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

        private static MissionTurnOutcome AttackOutcome(bool strikeSpent)
        {
            AttackTargetRef target = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base);
            return new MissionTurnOutcome
            {
                MissionKind = MissionKind.Attack,
                IntentKey = MissionIntentKey.ForAttack(target),
                MoverArmyId = 7,
                HasAttackPayload = true,
                OperationStarted = true,
                AttackOpportunisticStrike = strikeSpent,
                AttackTarget = new AttackMissionTarget
                {
                    Phase = AttackMissionPhase.Assault,
                    Target = target,
                    PrimaryArmyId = 7,
                    DestinationHex = RedBase,
                },
            };
        }

        private static MissionIntent AttackIntent(AttackMissionPhase phase, int primaryId,
            bool started = true, int? supportId = null, HexCoord? recoveryBase = null)
        {
            var payload = new Game.Ai.V2.AttackIntent
            {
                Target = AttackTargetRef.For(RedBase, Red, AttackTargetKind.Base),
                Phase = phase,
                OperationStarted = started,
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
    }
}
#endif
