#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiActiveDefenceTests
    {
        // Ysolde T9 (2026-09-28): the Citadel's only defender left to intercept one enemy while
        // another, one turn away, walked into the empty Citadel.
        [Test]
        public void LastDefenderOfStrongholdWithEnemyOneTurnAway_IsPinned()
        {
            var us = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            var citadel = new HexCoord(-3, -2);
            var defender = new ArmySnapshot
            {
                ArmyId = 26, Owner = us, Hex = citadel, EffectiveArmyPower = 20f,
                IsStructuralRaidActor = true, CurrentMovement = 3,
            };
            AssetThreatSnapshot Threat(int eta) => new AssetThreatSnapshot
            {
                Asset = new StrategicAssetSnapshot { Hex = citadel, Kind = AssetKind.Citadel },
                Contact = new EnemyContactSnapshot
                {
                    Army = new ArmySnapshot { ArmyId = 24, Owner = enemy },
                    Position = new HexCoord(-2, 0),
                },
                CanDamage = true, EnemyEta = eta,
            };
            WorldSnapshot Snap(int eta, params ArmySnapshot[] armies) => new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = armies, BaseHexes = new[] { citadel } },
                Threat = new ThreatModel { Threats = new[] { Threat(eta) } },
            };

            Assert.That(ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(
                Snap(1, defender), defender), Is.True);
            Assert.That(GroundCombatActorEligibility.EligibleReadyArmies(Snap(1, defender), null),
                Is.Empty, "a nomination never takes the pinned defender off the Citadel");
            Assert.That(GroundCombatActorEligibility.EligibleArmies(Snap(1, defender), null,
                requireMovementNow: false), Has.Count.EqualTo(1), "its power still counts");
            Assert.That(ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(
                Snap(3, defender), defender), Is.False, "a distant threat does not pin");

            var scout = new ArmySnapshot
            {
                ArmyId = 28, Owner = us, Hex = citadel, EffectiveArmyPower = 2f, IsSoloRecce = true,
            };
            Assert.That(ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(
                Snap(1, defender, scout), defender), Is.True, "a scout on the hex is not a defender");

            var second = new ArmySnapshot
            {
                ArmyId = 27, Owner = us, Hex = citadel, EffectiveArmyPower = 10f,
            };
            Assert.That(ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(
                Snap(1, defender, second), defender), Is.False, "another defender keeps the hex held");
        }

        [Test]
        public void StableIdentity_UsesEnemyArmyIdIncludingZero_NotMovingHex()
        {
            MissionIntentKey zero = MissionIntentKey.ForActiveDefence(0);
            var first = new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
                Target = new ActiveDefenceMissionTarget
                {
                    EnemyArmyId = 0, LastKnownHex = new HexCoord(1, 2),
                },
            };
            var moved = new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
                Target = new ActiveDefenceMissionTarget
                {
                    EnemyArmyId = 0, LastKnownHex = new HexCoord(8, 9),
                },
            };

            Assert.That(zero.ObjectiveId, Is.Zero);
            Assert.That(MissionIntentKey.For(first), Is.EqualTo(MissionIntentKey.For(moved)));
            Assert.That(StableMissionKey.For(first), Is.EqualTo(StableMissionKey.For(moved)));
        }

        // Case 8 — three armies withdrawing from ONE threat are three independent legs: each
        // Return is identified by its own mover and destination, never by the enemy.
        [Test]
        public void ReturnIdentity_IsPerMoverAndDestination_NotPerThreat()
        {
            var citadel = new HexCoord(0, 0);
            MissionProposal Return(int mover) => new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
                PreferredMoverArmyId = mover,
                Target = new ActiveDefenceMissionTarget
                {
                    Phase = ActiveDefencePhase.Return, EnemyArmyId = 42,
                    PrimaryArmyId = mover, ReturnHex = citadel,
                },
            };
            MissionProposal[] legs = { Return(5), Return(8), Return(11) };

            Assert.That(new HashSet<StableMissionKey>(
                System.Array.ConvertAll(legs, l => StableMissionKey.For(l))).Count, Is.EqualTo(3));
            Assert.That(new HashSet<MissionIntentKey>(
                System.Array.ConvertAll(legs, l => MissionIntentKey.For(l))).Count, Is.EqualTo(3));
            Assert.That(MissionIntentKey.For(legs[0]),
                Is.Not.EqualTo(MissionIntentKey.ForActiveDefence(42)),
                "a withdrawal never takes the intercept's identity");
            Assert.That(MissionIntentKey.For(legs[0]), Is.Not.EqualTo(MissionIntentKey.For(
                new MissionProposal
                {
                    Kind = MissionKind.ActiveDefence,
                    Target = new ActiveDefenceMissionTarget
                    {
                        Phase = ActiveDefencePhase.Intercept, EnemyArmyId = 5,
                    },
                })), "mover #5's Return and an intercept of enemy #5 are different operations");

            var intent = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence,
                Objective = new ActiveDefenceIntent
                {
                    Phase = ActiveDefencePhase.Return, EnemyArmyId = 42,
                    PrimaryArmyId = 8, ReturnHex = citadel,
                },
            };
            Assert.That(MissionIntentKey.For(intent), Is.EqualTo(MissionIntentKey.For(legs[1])),
                "the durable intent keys exactly like the proposal that created it");
        }

        // The ActiveDefence operation owns exactly one army: no support state exists on it.
        [Test]
        public void ActiveDefenceIntent_HoldsNoSupportArmy()
        {
            IGroundCombatOperation defence = new ActiveDefenceIntent { PrimaryArmyId = 7 };
            Assert.That(defence.SupportArmyId, Is.Null);
            Assert.That(typeof(ActiveDefenceIntent).GetField("SupportArmyId"), Is.Null);
            Assert.That(typeof(ActiveDefenceMissionTarget).GetField("SupportArmyId"), Is.Null);
            // AirSupport (2026-10-04) holds an aircraft wing in its own field, never a ground
            // support army; the ground phases keep their numeric values.
            Assert.That(System.Enum.GetNames(typeof(ActiveDefencePhase)),
                Is.EquivalentTo(new[] { "Intercept", "Return", "AirSupport" }));
            Assert.That((int)ActiveDefencePhase.Intercept, Is.Zero);
            Assert.That((int)ActiveDefencePhase.Return, Is.EqualTo(1));
        }

        [Test]
        public void ActiveDefenceScore_UsesCanonicalSlotsWithoutSeverityMultiplier()
        {
            var objective = new ActiveDefenceObjective
            {
                TaskScore = new TaskScore(strategicRelevance: 10f,
                    threatDirection: 4f, preventedDamage: 3f,
                    intelAgePenalty: 1f, ownTerritoryProximity: 2f),
            };
            var actor = new ArmySnapshot
            {
                ActivationApCost = 2, HasActivatedThisTurn = false,
            };
            TaskScore score = ActiveDefenceObjectiveEvaluator.WithResponse(
                objective, actor, winChance: 0.75f, eta: 2, moverOpportunityCost: 1f);

            Assert.That(score.StrategicRelevance, Is.EqualTo(10f));
            Assert.That(score.ThreatDirection, Is.EqualTo(4f));
            Assert.That(score.PreventedDamage, Is.EqualTo(3f));
            Assert.That(score.IntelAgePenalty, Is.EqualTo(1f));
            Assert.That(score.MoverOpportunityCost, Is.EqualTo(1f));
        }

        [Test]
        public void ActiveDefence_RemainsInAggressionLane()
        {
            Assert.That(MissionAdmissionPolicy.LaneFor(new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
            }), Is.EqualTo(ExecutionLane.Aggression));
        }

        [Test]
        public void ActiveDefenceValue_IsIndependentOfMilitaryPotentialRealization()
        {
            WorldSnapshot low = DefenceWorld(bestStack: 10f, totalPotential: 100f);
            WorldSnapshot high = DefenceWorld(bestStack: 90f, totalPotential: 100f);

            ActiveDefenceObjective lowDefence = ActiveDefenceObjectiveEvaluator.Enumerate(low)[0];
            ActiveDefenceObjective highDefence = ActiveDefenceObjectiveEvaluator.Enumerate(high)[0];

            Assert.That(highDefence.BaseValue, Is.EqualTo(lowDefence.BaseValue));
            Assert.That(highDefence.TaskScore.Value, Is.EqualTo(lowDefence.TaskScore.Value));
        }

        // 2026-10-01 (user decision) — Intercept answers armies, not scouts: by roster power.
        [Test]
        public void ActiveDefence_SkipsContactBelowMinimumPower()
        {
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(
                DefenceWorld(10f, 100f, enemyPower: AiConfigV2.activeDefenceMinEnemyPower - 0.1f)),
                Is.Empty);
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(
                DefenceWorld(10f, 100f, enemyPower: AiConfigV2.activeDefenceMinEnemyPower)),
                Has.Count.EqualTo(1));
        }

        // 2026-10-01 (user decision) — past the leash radius a pursuit loses desire per hex
        // (a TaskScore penalty, never a gate): the objective still exists.
        [Test]
        public void ActiveDefence_FarPursuitIsPenalizedNotForbidden()
        {
            int leash = AiConfigV2.activeDefenceLeashHexes;
            ActiveDefenceObjective atLeash = ActiveDefenceObjectiveEvaluator.Enumerate(
                DefenceWorld(10f, 100f, enemyHex: new HexCoord(leash, 0)))[0];
            ActiveDefenceObjective beyond = ActiveDefenceObjectiveEvaluator.Enumerate(
                DefenceWorld(10f, 100f, enemyHex: new HexCoord(leash + 2, 0)))[0];
            float slopeOnly = TaskScoreEvaluator.OwnTerritoryProximity(leash + 2);
            Assert.That(beyond.TaskScore.OwnTerritoryProximity, Is.EqualTo(
                slopeOnly - 2 * AiConfigV2.taskScoreActiveDefenceLeashPerHex).Within(1e-4f));
            Assert.That(atLeash.TaskScore.OwnTerritoryProximity, Is.EqualTo(
                TaskScoreEvaluator.OwnTerritoryProximity(leash)).Within(1e-4f));
        }

        [Test]
        public void ActiveDefenceIntercept_IsNotBorrowableByAttackGather()
        {
            var defence = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence,
                Status = IntentStatus.Active,
                LastIntrinsicValue = 12f,
                Objective = new ActiveDefenceIntent
                {
                    Phase = ActiveDefencePhase.Intercept,
                    EnemyArmyId = 42,
                    PrimaryArmyId = 7,
                },
            };
            var raid = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Status = IntentStatus.Active,
                LastIntrinsicValue = 9f,
                Objective = new RaidIntent
                {
                    Phase = RaidMissionPhase.Assault,
                    PrimaryArmyId = 9,
                },
            };

            Dictionary<int, float> prices =
                GroundCombatDonorPolicy.BorrowableDonorValues(new[] { defence, raid });

            Assert.That(prices.ContainsKey(7), Is.False,
                "an ActiveDefence responder remains owned by its threat");
            Assert.That(prices.ContainsKey(9), Is.True,
                "Raid donor behaviour is unchanged");
            Assert.That(prices[9], Is.EqualTo(9f),
                "a donor costs its operation's TaskScore value, not an AP figure");

            raid.Raid.Phase = RaidMissionPhase.Return;
            Assert.That(GroundCombatDonorPolicy.BorrowableDonorValues(new[] { raid })[9],
                Is.Zero, "a Raid already walking home loses nothing when its army is bought");
        }

        // ---- lifecycle: Continuity ends an Intercept, keeps a started Return -----------------

        // Case 6 — the threat is no longer honestly listed: the Intercept simply ends; no
        // automatic Return is created for its army.
        [Test]
        public void Continuity_InterceptWithoutObjective_RetiresWithoutReturn()
        {
            var player = new PlayerSetupData { Nickname = "DefenceLifecycle6" };
            MissionIntentRegistry.Clear();
            try
            {
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                MissionIntent intercept = DefenceIntent(ActiveDefencePhase.Intercept, 42, 7, null);
                state.Put(intercept);

                List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player,
                    LifecycleWorld(player, new HexCoord(3, 0)));

                Assert.That(active, Is.Empty);
                Assert.That(state.All, Is.Empty, "no stabilisation, no post-defence Return");
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        // Case 7 — a withdrawal that already started finishes, even though the threat that
        // triggered it is gone; it retires on arrival.
        [Test]
        public void Continuity_SafeWithdrawalWithoutCurrentDanger_ReleasesWithoutArrival()
        {
            var player = new PlayerSetupData { Nickname = "DefenceLifecycle7" };
            var citadel = new HexCoord(0, 0);
            MissionIntentRegistry.Clear();
            try
            {
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                MissionIntent withdrawal = DefenceIntent(ActiveDefencePhase.Return, 42, 7, citadel);
                state.Put(withdrawal);

                List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player,
                    LifecycleWorld(player, new HexCoord(3, 0)));
                Assert.That(active, Is.Empty, "a safe current position does not justify further activation");

                active = MissionContinuityLayer.ResolveActive(player, LifecycleWorld(player, citadel));
                Assert.That(active, Is.Empty);
                Assert.That(state.All, Is.Empty, "arrival releases the claim");
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        // Case 5 — a won intercept is removed at once; the next threat is a fresh objective.
        [Test]
        public void Reconcile_CompletedIntercept_RemovesTheIntent()
        {
            var player = new PlayerSetupData { Nickname = "DefenceLifecycle5" };
            MissionIntentRegistry.Clear();
            try
            {
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                MissionIntent intercept = DefenceIntent(ActiveDefencePhase.Intercept, 42, 7, null);
                state.Put(intercept);

                MissionContinuityLayer.ReconcileAfterTurn(player, 6, new List<MissionStepResult>
                {
                    new MissionStepResult
                    {
                        IntentKey = intercept.IntentKey,
                        MissionKind = MissionKind.ActiveDefence,
                        Disposition = MissionStepDisposition.Completed,
                        ObjectiveSatisfied = true,
                        MoverArmyId = 7,
                    },
                });

                Assert.That(state.TryGet(MissionIntentKey.ForActiveDefence(42), out _), Is.False);
                Assert.That(state.All, Is.Empty);
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        // ActiveDefence asset / ownership / timing regressions. All inputs below are honest
        // snapshots; no evaluator reads hidden enemy state or mutates the fixtures.
        private static readonly HexCoord Home = new HexCoord(3, 1);
        private static readonly HexCoord Secondary = new HexCoord(-1, 0);

        private static ArmySnapshot DefenceActor(PlayerSetupData owner, int id, HexCoord hex,
            float attack = 7f, float defence = 7f, float hp = 10f, bool garrison = false) =>
            new ArmySnapshot { Owner = owner, ArmyId = id, Hex = hex, MemberCount = 1,
                IsStructuralRaidActor = !garrison, IsGarrison = garrison,
                CurrentMovement = 3, MaxMovement = 3, ActivationApCost = 2,
                EffectiveArmyPower = attack + defence + hp,
                ReachableOwnBaseHexes = new[] { Home, Secondary },
                Members = new[] { new WorthIt.DefenderProfile(defence, false, null, attack, hp, 3) } };

        private static WorldSnapshot AssetWorld(params ArmySnapshot[] actors)
        {
            var owner = actors.FirstOrDefault()?.Owner ?? new PlayerSetupData();
            var enemy = new PlayerSetupData { Nickname = "hostile" };
            var contact = new EnemyContactSnapshot { Position = new HexCoord(-4, 0),
                LastObservedTurn = 8, Confidence = 1f, Knowledge = ContactKnowledge.Exact,
                Army = DefenceActor(enemy, 99, new HexCoord(-4, 0), 8f, 8f, 18f) };
            return new WorldSnapshot { Observer = owner, TurnNumber = 8,
                Self = new SelfSnapshot { Citadel = Home, BaseHexes = new[] { Home, Secondary }, Armies = actors },
                Known = new KnownSnapshot { EnemySightings = new[] {
                    new AiMapMemory.KnownEnemySighting(contact.Position.Value, enemy, "hostile", 1,
                        8f, 8f, contact.Army.Members, false, 0, 0, 8, 99) } },
                Threat = new ThreatModel { Contacts = new[] { contact }, Threats = new[] {
                    new AssetThreatSnapshot { Contact = contact, CanDamage = true, EnemyEta = 2,
                        AttackWinChance = 1f, PotentialDamage = 1f, Severity = 1f, Confidence = 1f,
                        Asset = new StrategicAssetSnapshot { Kind = AssetKind.Base, Hex = Secondary, Value = 10f } } } } };
        }

        private static ActiveDefenceObjective AssetObjective(WorldSnapshot snap) =>
            ActiveDefenceObjectiveEvaluator.Enumerate(snap).Single();

        [Test]
        public void FacilityOnly_CreatesNeitherObjectiveReserveNorDemand()
        {
            WorldSnapshot snap = AssetWorld();
            snap.Threat.Threats[0].Asset.Kind = AssetKind.Facility;
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(snap), Is.Empty);
            Assert.That(ForceNeedModel.DefensiveReserveForThreats(snap.Threat.Threats), Is.Zero);
            Assert.That(AggressionDemandEvaluator.BuildActiveDefenceDemands(snap, null,
                Array.Empty<MissionIntent>(), new ActorCommitments(), snap.Observer, out _), Is.Empty);
        }

        [Test]
        public void FacilityAndSeveralBases_FilterBeforeRankingAndDeduplicateEnemy()
        {
            WorldSnapshot snap = AssetWorld();
            AssetThreatSnapshot baseThreat = snap.Threat.Threats[0];
            float reserve = ForceNeedModel.DefensiveReserveForThreats(snap.Threat.Threats);
            snap.Threat.Threats = new[] { baseThreat,
                new AssetThreatSnapshot { Contact = baseThreat.Contact, Severity = 100f, PotentialDamage = 1f,
                    Asset = new StrategicAssetSnapshot { Kind = AssetKind.Facility, Hex = Home, Value = 1000f } },
                new AssetThreatSnapshot { Contact = baseThreat.Contact, Severity = 0.1f, PotentialDamage = 0.1f,
                    Asset = new StrategicAssetSnapshot { Kind = AssetKind.Citadel, Hex = Home, Value = 1f } } };
            Assert.That(AssetObjective(snap).Target.ProtectedAssetHex, Is.EqualTo(Secondary));
            Assert.That(ForceNeedModel.DefensiveReserveForThreats(snap.Threat.Threats), Is.EqualTo(reserve));
            snap.Self.BaseHexes = Array.Empty<HexCoord>();
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(snap), Is.Empty, "remembered ownership is insufficient");
        }

        [TestCase(0, 3, 0, true)]
        [TestCase(0, 0, 1, true)]
        [TestCase(-8, 3, 2, false)]
        public void ReinforcementTiming_UsesCurrentMpAndConservativeTurnOrder(int q, int mp, int eta, bool timely)
        {
            var owner = new PlayerSetupData();
            ArmySnapshot actor = DefenceActor(owner, 7, new HexCoord(q, 0));
            actor.CurrentMovement = mp;
            WorldSnapshot snap = AssetWorld(actor);
            Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, Secondary), Is.EqualTo(eta));
            Assert.That(ActiveDefenceObjectiveEvaluator.CanArriveBeforeThreat(snap, actor, Secondary, 2, out _), Is.EqualTo(timely));
            Assert.That(ActiveDefenceObjectiveEvaluator.CanArriveBeforeThreat(snap, actor, Secondary, eta, out _), Is.False);
        }

        [Test]
        public void UnreachableAndUnknownEta_DoNotInventReadinessOrImmediateAttack()
        {
            ArmySnapshot actor = DefenceActor(new PlayerSetupData(), 7, new HexCoord(0, 0));
            WorldSnapshot snap = AssetWorld(actor);
            actor.ReachableOwnBaseHexes = Array.Empty<HexCoord>();
            Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, Secondary), Is.EqualTo(int.MaxValue));
            snap.Threat.Threats[0].EnemyEta = null;
            ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap, AssetObjective(snap), null, null, null);
            Assert.That(response.Kind, Is.EqualTo(ActiveDefenceResponseKind.Defer));
            Assert.That(response.Reason, Is.EqualTo("enemy_eta_unknown"));
            Assert.That(response.Movers, Is.Empty);
        }

        [Test]
        public void SufficientCurrentGarrison_PreventsAnyAdditionalMovement()
        {
            var owner = new PlayerSetupData();
            WorldSnapshot snap = AssetWorld(DefenceActor(owner, 1, Secondary, 50, 50, 50, true),
                DefenceActor(owner, 2, new HexCoord(0, 0)));
            ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap, AssetObjective(snap), null, null, null);
            Assert.That(response.Kind, Is.EqualTo(ActiveDefenceResponseKind.Defer));
            Assert.That(response.Reason, Is.EqualTo("asset_holds"));
            Assert.That(response.Movers, Is.Empty);
        }

        [TestCase(9f, 12f, 1)]
        [TestCase(7f, 14f, 2)]
        public void Regroup_UsesOnlySufficientRosterAndTheThreatenedSecondaryBase(float attack, float hp, int needed)
        {
            var owner = new PlayerSetupData();
            WorldSnapshot snap = AssetWorld(DefenceActor(owner, 1, Secondary, attack, 7, hp),
                DefenceActor(owner, 2, new HexCoord(0, 0), attack, 7, hp), DefenceActor(owner, 3, new HexCoord(0, 1), attack, 7, hp),
                DefenceActor(owner, 4, Home, attack, 7, hp));
            ActiveDefenceObjective objective = AssetObjective(snap);
            ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap, objective, null, null, null);
            Assert.That(response.Kind, Is.EqualTo(ActiveDefenceResponseKind.Regroup));
            Assert.That(response.RegroupHex, Is.EqualTo(Secondary));
            Assert.That(response.Movers.Count, Is.EqualTo(needed));
            Assert.That(response.HoldWinChance, Is.GreaterThanOrEqualTo(AiConfigV2.activeDefenceHoldWinChance));
            foreach (ArmySnapshot chosen in response.Movers)
            {
                var without = response.Movers.Where(a => a != chosen).ToArray();
                Assert.That(ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset(snap, objective, Secondary,
                    without, response.Opposition), Is.LessThan(AiConfigV2.activeDefenceHoldWinChance));
            }
        }

        [Test]
        public void CitadelRegroup_UsesTheCitadelItselfAndAdjacentHexIsNotArrival()
        {
            var owner = new PlayerSetupData();
            var standing = DefenceActor(owner, 1, Home, 9, 7, 12);
            var mover = DefenceActor(owner, 2, new HexCoord(2, 1), 9, 7, 12);
            WorldSnapshot snap = AssetWorld(standing, mover);
            snap.Threat.Threats[0].Asset.Kind = AssetKind.Citadel;
            snap.Threat.Threats[0].Asset.Hex = Home;
            var response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap, AssetObjective(snap), null, null, null);
            Assert.That(response.Kind, Is.EqualTo(ActiveDefenceResponseKind.Regroup));
            Assert.That(response.RegroupHex, Is.EqualTo(Home));
            var intent = DefenceIntent(ActiveDefencePhase.Return, 99, 2, Home);
            intent.ActiveDefence.ReturnPurpose = ActiveDefenceReturnPurpose.RegroupForAsset;
            intent.ActiveDefence.ProtectedAssetKind = AssetKind.Citadel;
            intent.ActiveDefence.ProtectedAssetHex = Home;
            var state = MissionIntentRegistry.GetOrCreate(owner);
            state.Put(intent);
            try
            {
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Does.Contain(intent));
                mover.Hex = Home;
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Is.Empty);
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void PowerWithoutViableRoster_IsShortageAndDoesNotEvacuateSafeArmies()
        {
            var owner = new PlayerSetupData();
            ArmySnapshot actor = DefenceActor(owner, 7, new HexCoord(0, 0), 0, 0, 1);
            actor.EffectiveArmyPower = 10000f;
            WorldSnapshot snap = AssetWorld(actor);
            ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap, AssetObjective(snap), null, null, null);
            Assert.That(response.Kind, Is.EqualTo(ActiveDefenceResponseKind.Shortage));
            Assert.That(response.Movers, Is.Empty);
        }

        [TestCase(AttackMissionPhase.Gather)]
        [TestCase(AttackMissionPhase.Assault)]
        public void ClaimedAttackOnBase_IsNeitherDefenderNorUnavailableCapableExcuse(AttackMissionPhase phase)
        {
            var owner = new PlayerSetupData();
            var live = new Game.Map.ArmyData { Owner = owner, Hex = Secondary };
            live.Members.Add(new Game.Units.UnitData { Owner = owner, Attack = 50, Defense = 50,
                HitPointsCurrent = 50, HitPointsMax = 50, MoveMax = 3, MoveCurrent = 3 });
            Game.Map.ArmyRegistry.Register(live);
            ArmySnapshot actor = DefenceActor(owner, live.Id, Secondary, 50, 50, 50);
            WorldSnapshot snap = AssetWorld(actor);
            var attack = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Objective = new AttackIntent { PrimaryArmyId = live.Id, Phase = phase } };
            attack.IntentKey = MissionIntentKey.For(attack);
            var commitments = ActorCommitments.FromIntents(new[] { attack }, snap, null);
            ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap, AssetObjective(snap),
                commitments.ClaimedArmyIdSet, null, null, new[] { attack });
            Assert.That(response.Kind, Is.EqualTo(ActiveDefenceResponseKind.Shortage));
            Assert.That(response.Reason, Is.Not.Contains("capable_actor_unavailable"));
            Assert.That(response.HoldWinChance, Is.Zero);
            Assert.That(response.AvailablePower, Is.Zero);
            Assert.That(attack.Attack.Phase, Is.EqualTo(phase));
            Game.Map.ArmyRegistry.Clear();
            Assert.That(commitments.IsArmyClaimed(live.Id), Is.True);
        }

        [Test]
        public void AvailableFutureAttackHost_StillCompetesAndReleasedClaimIsReevaluated()
        {
            ArmySnapshot actor = DefenceActor(new PlayerSetupData(), 7, new HexCoord(0, 0), 50, 50, 50);
            WorldSnapshot snap = AssetWorld(actor);
            ActiveDefenceObjective objective = AssetObjective(snap);
            Assert.That(ActiveDefenceObjectiveEvaluator.AssessResponse(snap, objective, new HashSet<int> { 7 }, null, null).Kind,
                Is.EqualTo(ActiveDefenceResponseKind.Shortage));
            Assert.That(ActiveDefenceObjectiveEvaluator.AssessResponse(snap, objective, new HashSet<int>(), null, null).Kind,
                Is.EqualTo(ActiveDefenceResponseKind.Intercept));
        }

        [Test]
        public void RegroupContinuity_ReleasesOnLostThreatOrLostBase()
        {
            var owner = new PlayerSetupData();
            WorldSnapshot snap = AssetWorld(DefenceActor(owner, 7, new HexCoord(0, 0)));
            MissionIntent intent = DefenceIntent(ActiveDefencePhase.Return, 99, 7, Secondary);
            intent.ActiveDefence.ReturnPurpose = ActiveDefenceReturnPurpose.RegroupForAsset;
            intent.ActiveDefence.ProtectedAssetKind = AssetKind.Base;
            intent.ActiveDefence.ProtectedAssetHex = Secondary;
            var state = MissionIntentRegistry.GetOrCreate(owner);
            try
            {
                state.Put(intent);
                snap.Threat.Threats = Array.Empty<AssetThreatSnapshot>();
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Is.Empty);
                Assert.That(ActorCommitments.FromIntents(state.All, snap, null).IsArmyClaimed(7), Is.False);
                state.Put(intent);
                snap.Self.BaseHexes = new[] { Home };
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Is.Empty);
                Assert.That(state.All, Is.Empty);
                Assert.That(snap.Self.Armies[0].Hex, Is.EqualTo(new HexCoord(0, 0)), "cancellation never moves the army back");
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void SafeWithdrawal_RechecksCurrentPositionAndRetiresOnArrival()
        {
            var owner = new PlayerSetupData();
            ArmySnapshot actor = DefenceActor(owner, 7, new HexCoord(0, 0), 1, 1, 1);
            WorldSnapshot snap = AssetWorld(actor);
            AssetThreatSnapshot danger = snap.Threat.Threats[0];
            danger.Asset = new StrategicAssetSnapshot { Kind = AssetKind.Army, Hex = actor.Hex };
            MissionIntent intent = DefenceIntent(ActiveDefencePhase.Return, 42, 7, Home);
            var state = MissionIntentRegistry.GetOrCreate(owner);
            try
            {
                state.Put(intent);
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Does.Contain(intent), "a DIFFERENT current threat can justify withdrawal");
                snap.Threat.Threats = Array.Empty<AssetThreatSnapshot>();
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Is.Empty);
                state.Put(intent);
                actor.Hex = Home;
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Is.Empty);
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void SnapshotAndPlayerIsolation_NoDefenceResponseCacheNeedsReset()
        {
            var owner = new PlayerSetupData();
            ArmySnapshot actor = DefenceActor(owner, 7, new HexCoord(-8, 0), 0, 0, 1);
            WorldSnapshot snap = AssetWorld(actor);
            ActiveDefenceObjective objective = AssetObjective(snap);
            int eta = ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, Secondary);
            Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, Secondary), Is.EqualTo(eta));
            WorldSnapshot moved = AssetWorld(DefenceActor(owner, 7, new HexCoord(0, 0), 0, 0, 1));
            Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(moved, moved.Self.Armies[0], Secondary), Is.Zero);
            moved.Self.Armies[0].CurrentMovement = 0;
            Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(moved, moved.Self.Armies[0], Secondary), Is.EqualTo(1));
            moved.Threat.Threats[0].EnemyEta = 1;
            Assert.That(ActiveDefenceObjectiveEvaluator.CanArriveBeforeThreat(moved, moved.Self.Armies[0], Secondary,
                AssetObjective(moved).Target.EnemyEta, out _), Is.False);
            WorldSnapshot otherPlayer = AssetWorld();
            Assert.That(ReferenceEquals(snap.Observer, otherPlayer.Observer), Is.False);
            Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, Secondary), Is.EqualTo(eta));
            otherPlayer.Known.EnemySightings = Array.Empty<AiMapMemory.KnownEnemySighting>();
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(otherPlayer), Is.Empty);
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(snap), Has.Count.EqualTo(1));
        }

        [Test]
        public void CancellationAndRekey_ReleaseOnlyTheirOwnLeaseAndPreserveEconomySaving()
        {
            var owner = new PlayerSetupData();
            using var turn = AiTurnSession.Begin(owner, null, null, null, 8);
            WorldSnapshot snap = AssetWorld(DefenceActor(owner, 7, new HexCoord(0, 0)));
            var intent = DefenceIntent(ActiveDefencePhase.Return, 99, 7, Secondary);
            intent.ActiveDefence.ReturnPurpose = ActiveDefenceReturnPurpose.RegroupForAsset;
            intent.ActiveDefence.ProtectedAssetKind = AssetKind.Base;
            intent.ActiveDefence.ProtectedAssetHex = Secondary;
            var oldKey = intent.IntentKey;
            turn.PersistentState.Put(intent);
            var lease = turn.Leases.For(oldKey);
            lease.Claim(7);
            var economyKey = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, Home);
            var economy = turn.Leases.For(economyKey);
            economy.Reserve(StrategicReservationReason.EconomyDeferredBuild, StrategicReservedResource.Materials, 4f);
            intent.ActiveDefence.ReturnHex = Home;
            intent.IntentKey = MissionIntentKey.For(intent);
            turn.PersistentState.Remove(oldKey);
            turn.PersistentState.Put(intent);
            Assert.That(turn.Leases.For(oldKey).ActorClaims, Is.Empty);
            Assert.That(turn.Leases.For(intent.IntentKey).ActorClaims, Is.EqualTo(new[] { 7 }));
            Assert.That(economy.ResourceClaims.Single().Amount, Is.EqualTo(4f));
            snap.Threat.Threats = Array.Empty<AssetThreatSnapshot>();
            Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Is.Empty);
            Assert.That(turn.Leases.IsClaimed(7), Is.False);
            Assert.That(turn.Leases.For(intent.IntentKey).ResourceClaims, Is.Empty);
            Assert.That(economy.ResourceClaims.Single().Amount, Is.EqualTo(4f));
            turn.Leases.Retire(economyKey);
            StrategicResourceReservationLedger.AssertClearAtTurnEnd(owner, 8);
            Assert.That(ReservationInvariants.Violations(owner, 8), Is.Empty);
        }

        [TestCase(false, 5, 2, true)]
        [TestCase(true, 5, 0, true)]
        [TestCase(false, 1, 2, false)]
        public void ReturnApEnvelope_UsesOneCurrentActivationAndNeverFutureOrPhysicalCosts(
            bool activated, int physicalAp, int expectedCost, bool funded)
        {
            var owner = new PlayerSetupData();
            ArmySnapshot actor = DefenceActor(owner, 7, new HexCoord(0, 0));
            actor.HasActivatedThisTurn = activated;
            WorldSnapshot snap = AssetWorld(actor);
            snap.Self.ActionPoints = physicalAp;
            var requirements = GroundCombatLegs.PinnedLegRequirements(actor, Home, out _);
            var mission = new MissionProposal { Kind = MissionKind.ActiveDefence,
                BaseValue = 10f, LocalAdmissionScore = 10f, Requirements = requirements,
                Target = new ActiveDefenceMissionTarget { Phase = ActiveDefencePhase.Return,
                    ReturnPurpose = ActiveDefenceReturnPurpose.RegroupForAsset,
                    PrimaryArmyId = 7, ReturnHex = Home } };
            mission.Axes.Value[DesireAxis.Aggression] = 1f;
            var radar = Radar.Even();
            foreach (DesireAxis a in DesireAxes.All) radar.Weight[a] = a == DesireAxis.Aggression ? 1f : 0f;
            try
            {
                TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, radar,
                    new List<MissionProposal> { mission }, new List<Commitment>(), owner).Pack();
                Assert.That(requirements.ApMinimum, Is.EqualTo(expectedCost));
                Assert.That(requirements.HumanMinimum + requirements.EnergyMinimum
                    + requirements.MaterialsMinimum + requirements.TechMinimum, Is.Zero);
                Assert.That(allocation.Funded.Count > 0, Is.EqualTo(funded));
                Assert.That(allocation.Unused.Ap, Is.GreaterThanOrEqualTo(0f));
                if (funded)
                {
                    Assert.That(allocation.Funded.Single().Tentative.Ap, Is.EqualTo(expectedCost));
                    Assert.That(allocation.Funded.Single().PhysicalDraw.AnyPhysical, Is.False);
                }
            }
            finally { AiAllocatorStateRegistry.Clear(); }
        }

        [Test]
        public void AdmissionFingerprint_TracksReturnPurposeDestinationAssetAndPathingVersion()
        {
            WorldSnapshot snap = AssetWorld();
            var state = MissionIntentRegistry.GetOrCreate(snap.Observer);
            var intent = DefenceIntent(ActiveDefencePhase.Return, 99, 7, Secondary);
            state.Put(intent);
            try
            {
                string first = Pipeline.AggressionAdmissionFingerprint(snap, snap.Observer);
                intent.ActiveDefence.ReturnPurpose = ActiveDefenceReturnPurpose.RegroupForAsset;
                string purpose = Pipeline.AggressionAdmissionFingerprint(snap, snap.Observer);
                Assert.That(purpose, Is.Not.EqualTo(first));
                intent.ActiveDefence.ProtectedAssetHex = Secondary;
                string asset = Pipeline.AggressionAdmissionFingerprint(snap, snap.Observer);
                Assert.That(asset, Is.Not.EqualTo(purpose));
                intent.ActiveDefence.ReturnHex = Home;
                string destination = Pipeline.AggressionAdmissionFingerprint(snap, snap.Observer);
                Assert.That(destination, Is.Not.EqualTo(asset));
                snap.MapPathingVersion++;
                Assert.That(Pipeline.AggressionAdmissionFingerprint(snap, snap.Observer), Is.Not.EqualTo(destination));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void RegroupRoute_ReevaluatesActualTerrainAndPreservesTheCombatProfile()
        {
            var go = new UnityEngine.GameObject("active-defence-route");
            Game.Map.HexMap map = go.AddComponent<Game.Map.HexMap>();
            ArmySnapshot actor = DefenceActor(new PlayerSetupData(), 7, new HexCoord(0, 0));
            WorldSnapshot snap = AssetWorld(actor);
            snap.Map = map;
            var home = new HexCoord(3, 0);
            var terrain = new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry>();
            for (int q = 0; q <= 3; q++) terrain[new HexCoord(q, 0)] = new Game.Terrain.TerrainTypeEntry { moveCost = 2 };
            map.SetData(4, 1f, terrain);
            try
            {
                AiMapMemory.MarkScoutDanger(snap.Observer, new HexCoord(1, 0), 0, 99);
                Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, home), Is.EqualTo(2),
                    "Combat transit ignores scout danger but packs 2-MP steps into a 3-MP turn");
                terrain[new HexCoord(2, 0)].moveCost = 4;
                map.SetData(4, 1f, terrain);
                Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, home), Is.EqualTo(int.MaxValue));
                terrain[new HexCoord(2, 0)].moveCost = 1;
                map.SetData(4, 1f, terrain);
                Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, home), Is.EqualTo(1));
            }
            finally { AiMapMemory.Clear(); UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void RegroupKeepsOnlyNecessaryLeg_ThenReleasesWhenAnotherDefenderArrives()
        {
            var owner = new PlayerSetupData();
            ArmySnapshot standing = DefenceActor(owner, 1, Secondary, 9, 7, 12);
            ArmySnapshot mover = DefenceActor(owner, 2, new HexCoord(0, 0), 9, 7, 12);
            WorldSnapshot snap = AssetWorld(standing, mover, DefenceActor(owner, 3, new HexCoord(0, 1), 9, 7, 12));
            using var turn = AiTurnSession.Begin(owner, null, null, null, 8);
            MissionIntent intent = DefenceIntent(ActiveDefencePhase.Return, 99, 2, Secondary);
            intent.ActiveDefence.ReturnPurpose = ActiveDefenceReturnPurpose.RegroupForAsset;
            intent.ActiveDefence.ProtectedAssetHex = Secondary;
            intent.ActiveDefence.ProtectedAssetKind = AssetKind.Base;
            turn.PersistentState.Put(intent);
            turn.RefreshActors(turn.PersistentState.All.ToList(), snap, null);
            Assert.That(turn.Leases.IsClaimed(2), Is.True);
            intent.Status = IntentStatus.Suspended;
            intent.Suspended = SuspendReason.PoolExhausted;
            for (int i = 0; i < 3; i++)
            {
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Does.Contain(intent));
                Assert.That(intent.Status, Is.EqualTo(IntentStatus.Active));
                Assert.That(intent.ActiveDefence.ReturnHex, Is.EqualTo(Secondary), "identical facts never reverse the destination");
                Assert.That(turn.PersistentState.Count, Is.EqualTo(1));
            }
            WorldSnapshot reinforced = AssetWorld(standing, mover,
                DefenceActor(owner, 3, Secondary, 9, 7, 12));
            Assert.That(MissionContinuityLayer.ResolveActive(owner, reinforced), Is.Empty);
            Assert.That(turn.Leases.IsClaimed(2), Is.False);
            Assert.That(mover.Hex, Is.EqualTo(new HexCoord(0, 0)));
        }

        [Test]
        public void WithdrawalLostHome_RekeysOnceWithoutKeepingOldActorOrResourceOwner()
        {
            var owner = new PlayerSetupData();
            ArmySnapshot actor = DefenceActor(owner, 7, new HexCoord(0, 0), 1, 1, 1);
            WorldSnapshot snap = AssetWorld(actor);
            snap.Self.BaseHexes = new[] { Secondary };
            snap.Threat.Threats[0].Asset = new StrategicAssetSnapshot { Kind = AssetKind.Army, Hex = actor.Hex };
            using var turn = AiTurnSession.Begin(owner, null, null, null, 8);
            MissionIntent intent = DefenceIntent(ActiveDefencePhase.Return, 42, 7, Home);
            MissionIntentKey old = intent.IntentKey;
            turn.PersistentState.Put(intent);
            turn.Leases.For(old).Claim(7);
            Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Does.Contain(intent));
            Assert.That(intent.ActiveDefence.ReturnHex, Is.EqualTo(Secondary));
            Assert.That(turn.PersistentState.TryGet(old, out _), Is.False);
            Assert.That(turn.Leases.For(old).ActorClaims, Is.Empty);
            Assert.That(turn.Leases.For(intent.IntentKey).ActorClaims, Is.EqualTo(new[] { 7 }));
            Assert.That(turn.PersistentState.Count, Is.EqualTo(1));
        }

        [Test]
        public void ForceNeedCache_NewSnapshotsChangeDefensiveNeedWithoutMutatingOldResult()
        {
            WorldSnapshot first = AssetWorld();
            ForceNeed before = ForceNeedModel.JustifiedForceNeed(first);
            Assert.That(before.Defensive, Is.GreaterThan(0));
            Assert.That(ForceNeedModel.JustifiedForceNeed(first).Defensive, Is.EqualTo(before.Defensive));
            WorldSnapshot facility = AssetWorld();
            facility.Threat.Threats[0].Asset.Kind = AssetKind.Facility;
            Assert.That(ForceNeedModel.JustifiedForceNeed(facility).Defensive, Is.Zero);
            WorldSnapshot strong = AssetWorld();
            strong.Self.TotalPower = 10000f;
            Assert.That(ForceNeedModel.JustifiedForceNeed(strong).Defensive, Is.Zero);
            Assert.That(ForceNeedModel.JustifiedForceNeed(first).Defensive, Is.EqualTo(before.Defensive));
            Assert.That(ReferenceEquals(first.Observer, facility.Observer), Is.False);
        }

        [Test]
        public void MaterializedDefenderMustReachTheOwnedAssetBeforeDeadline()
        {
            var card = new Game.Cards.CardDefinition();
            {
                card.cardType = Game.Cards.CardType.Unit;
                card.moveMax = 2;
                WorldSnapshot snap = AssetWorld();
                var demand = new AxisDemand { Capability = CapabilityKind.FieldCombatPower,
                    ConsumerMissionKind = MissionKind.ActiveDefence,
                    DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                    TargetHex = Secondary, ActiveDefenceEnemyEta = 1 };
                var plan = new MaterializationPlan { GeneratedBaseDef = card,
                    Deploy = new PlacementOption(Home, DeploymentKind.NewArmy, null) };
                Assert.That(MaterializationDeliveryPolicy.CanDeliverDemandOperationally(plan, demand, snap, snap.Observer), Is.False);
                plan.Deploy = new PlacementOption(Secondary, DeploymentKind.NewArmy, null);
                Assert.That(MaterializationDeliveryPolicy.CanDeliverDemandOperationally(plan, demand, snap, snap.Observer), Is.True);
                snap.Self.BaseHexes = new[] { Home };
                Assert.That(MaterializationDeliveryPolicy.CanDeliverDemandOperationally(plan, demand, snap, snap.Observer), Is.False);
                demand.ConsumerMissionKind = MissionKind.Raid;
                Assert.That(MaterializationDeliveryPolicy.CanDeliverDemandOperationally(plan, demand, snap, snap.Observer), Is.True,
                    "Raid keeps its existing independent-field delivery contract");
            }
        }

        private static MissionIntent DefenceIntent(ActiveDefencePhase phase, int enemyId,
            int actorId, HexCoord? returnHex)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence,
                Status = IntentStatus.Active,
                Funding = CommitmentTier.Hard,
                Objective = new ActiveDefenceIntent
                {
                    Phase = phase, EnemyArmyId = enemyId, PrimaryArmyId = actorId,
                    ReturnHex = returnHex, LastKnownHex = new HexCoord(4, 0),
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        private static WorldSnapshot LifecycleWorld(PlayerSetupData player, HexCoord actorHex) =>
            new WorldSnapshot
            {
                TurnNumber = 6,
                Observer = player,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new[] { new HexCoord(0, 0) },
                    Armies = new[]
                    {
                        new ArmySnapshot
                        {
                            ArmyId = 7, Owner = player, Hex = actorHex,
                            IsStructuralRaidActor = true, MemberCount = 1,
                            CurrentMovement = 3, MaxMovement = 3,
                            Members = new[] { new WorthIt.DefenderProfile(1f, false, null, 1f, 1f, 0) },
                        },
                    },
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };

        private static WorldSnapshot DefenceWorld(float bestStack, float totalPotential,
            float enemyPower = 12f, HexCoord? enemyHex = null)
        {
            var enemy = new PlayerSetupData { Nickname = "DefenceEnemy", ColorIndex = 2 };
            var hex = enemyHex ?? new HexCoord(4, 0);
            var body = new WorthIt.DefenderProfile(3f, false, null, 4f, 8f, 2);
            var contact = new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    ArmyId = 42,
                    Owner = enemy,
                    EffectiveArmyPower = enemyPower,
                    Members = new[] { body },
                    MemberCount = 1,
                },
                Knowledge = ContactKnowledge.Exact,
                Position = hex,
                LastObservedTurn = 5,
                Confidence = 1f,
            };
            return new WorldSnapshot
            {
                TurnNumber = 5,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new[] { new HexCoord(0, 0) },
                    BestStackPotential = bestStack,
                    TotalMilitaryPotential = totalPotential,
                },
                Known = new KnownSnapshot
                {
                    EnemySightings = new[]
                    {
                        new AiMapMemory.KnownEnemySighting(hex, enemy, "enemy", 1,
                            3f, 4f, new List<WorthIt.DefenderProfile> { body },
                            hasAntiAir: false, recceRadius: 0, recceSpotStrength: 0,
                            seenTurn: 5, armyId: 42),
                    },
                },
                Threat = new ThreatModel
                {
                    Threats = new[]
                    {
                        new AssetThreatSnapshot
                        {
                            Contact = contact,
                            Asset = new StrategicAssetSnapshot
                            {
                                Kind = AssetKind.Base,
                                Hex = new HexCoord(0, 0),
                                Value = 10f,
                            },
                            EnemyEta = 1,
                            PotentialDamage = 0.5f,
                            Confidence = 1f,
                            Severity = 0.8f,
                        },
                    },
                },
            };
        }
        private static MissionProposal RegroupLeg(WorldSnapshot snap, int actorId)
        {
            var target = AssetObjective(snap).Target;
            target.Phase = ActiveDefencePhase.Return;
            target.ReturnPurpose = ActiveDefenceReturnPurpose.RegroupForAsset;
            target.ReturnHex = target.ProtectedAssetHex;
            target.PrimaryArmyId = actorId;
            return new MissionProposal { Kind = MissionKind.ActiveDefence, Target = target,
                PreferredMoverArmyId = actorId };
        }

        private static void PrepareRegroupBatch(ProvisioningSession session, ActorCommitments commitments,
            params MissionProposal[] missions)
        {
            var allocation = new TentativeAllocation();
            allocation.Funded.AddRange(missions.Select(m => new FundedEntry { Mission = m,
                Tentative = new ResourceVector(2f, 0f, 0f, 0f, 0f) }));
            typeof(ProvisioningManager).GetMethod("PrepareGroundCombatAssignments",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { session, allocation, commitments });
        }

        [Test]
        public void FundedRegroupBatch_KeepsPeersInJointForecastWithoutSharingTheirActors()
        {
            var owner = new PlayerSetupData();
            var snap = AssetWorld(DefenceActor(owner, 1, Secondary, 7, 7, 14),
                DefenceActor(owner, 2, new HexCoord(0, 0), 7, 7, 14),
                DefenceActor(owner, 3, new HexCoord(0, 1), 7, 7, 14));
            var first = RegroupLeg(snap, 2);
            var second = RegroupLeg(snap, 3);
            using var session = new ProvisioningSession(snap);
            PrepareRegroupBatch(session, new ActorCommitments(), first, second);
            Assert.That(session.ExcludedForGroundCombat(first), Does.Contain(3),
                "the peer is NOT available to bind or donate to the current leg");
            Assert.That(ActiveDefenceObjectiveEvaluator.AssessResponse(snap, AssetObjective(snap),
                session.ExcludedForGroundCombat(first), null, null).Kind,
                Is.EqualTo(ActiveDefenceResponseKind.Shortage), "reproduce the original per-leg exclusion bug");
            var jointly = ActiveDefenceProvisioner.AssessRegroupForProvisioning(snap, first,
                session, Array.Empty<MissionIntent>());
            Assert.That(jointly.Kind, Is.EqualTo(ActiveDefenceResponseKind.Regroup));
            Assert.That(jointly.ReinforcementArmyIds, Is.EquivalentTo(new[] { 2, 3 }));
            session.RegisterSuccess(StableMissionKey.For(first), new ProvisionedMission {
                Mission = first, Kind = MissionKind.ActiveDefence, MoverArmyId = 2,
                ActiveDefenceTarget = (ActiveDefenceMissionTarget)first.Target, ClaimedAp = 2f });
            Assert.That(session.ExcludedForGroundCombat(second), Does.Contain(2));
            Assert.That(ActiveDefenceProvisioner.AssessRegroupForProvisioning(snap, second,
                session, Array.Empty<MissionIntent>()).Kind, Is.EqualTo(ActiveDefenceResponseKind.Regroup));
            Assert.That(session.ApClaimed, Is.EqualTo(2f), "forecasting must not claim or charge the peer again");
        }

        [Test]
        public void FundedRegroupBatch_DoesNotPromiseUnfundedOrContendedPeers()
        {
            var owner = new PlayerSetupData();
            var snap = AssetWorld(DefenceActor(owner, 1, Secondary, 7, 7, 14),
                DefenceActor(owner, 2, new HexCoord(0, 0), 7, 7, 14),
                DefenceActor(owner, 3, new HexCoord(0, 1), 7, 7, 14));
            var first = RegroupLeg(snap, 2);
            var second = RegroupLeg(snap, 3);
            using var session = new ProvisioningSession(snap);
            PrepareRegroupBatch(session, new ActorCommitments(), first);
            Assert.That(ActiveDefenceProvisioner.AssessRegroupForProvisioning(snap, first,
                session, Array.Empty<MissionIntent>()).Kind, Is.EqualTo(ActiveDefenceResponseKind.Shortage));
            PrepareRegroupBatch(session, new ActorCommitments(), first, second);
            Assert.That(ActiveDefenceProvisioner.AssessRegroupForProvisioning(snap, first,
                session, Array.Empty<MissionIntent>()).Kind, Is.EqualTo(ActiveDefenceResponseKind.Regroup));
            PrepareRegroupBatch(session, new ActorCommitments(), first);
            Assert.That(ActiveDefenceProvisioner.AssessRegroupForProvisioning(snap, first,
                session, Array.Empty<MissionIntent>()).Kind, Is.EqualTo(ActiveDefenceResponseKind.Shortage),
                "repack must discard the previous funded peer without manual cache reset");
            var otherOwner = new ActorCommitments();
            otherOwner.Claim(3);
            PrepareRegroupBatch(session, otherOwner, first, second);
            Assert.That(ActiveDefenceProvisioner.AssessRegroupForProvisioning(snap, first,
                session, Array.Empty<MissionIntent>()).Kind, Is.EqualTo(ActiveDefenceResponseKind.Shortage));
            Assert.That(otherOwner.IsArmyClaimed(3), Is.True);
            Assert.That(session.ApClaimed, Is.Zero);
        }

        [Test]
        public void WithdrawalContinuesForSurvivingGroundContainerAfterLosingCombatEligibility()
        {
            var actor = DefenceActor(new PlayerSetupData(), 7, new HexCoord(0, 0), 0, 0, 1);
            actor.IsStructuralRaidActor = false;
            var snap = AssetWorld(actor);
            snap.Threat.Threats[0].Asset = new StrategicAssetSnapshot { Kind = AssetKind.Army, Hex = actor.Hex };
            var intent = DefenceIntent(ActiveDefencePhase.Return, 99, 7, Home);
            var registry = MissionIntentRegistry.GetOrCreate(snap.Observer);
            registry.Put(intent);
            try
            {
                var commitments = ActorCommitments.FromIntents(registry.All, snap, null);
                Assert.That(commitments.IsArmyClaimed(7), Is.True);
                Assert.That(MissionContinuityLayer.ResolveActive(snap.Observer, snap), Does.Contain(intent));
                actor.MemberCount = 0;
                Assert.That(MissionContinuityLayer.ResolveActive(snap.Observer, snap), Is.Empty);
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void WithdrawalHome_DoesNotCountTheRetreatingArmyBeforeItCanArrive()
        {
            var actor = DefenceActor(new PlayerSetupData(), 7, new HexCoord(-8, 0), 50, 50, 50);
            var snap = AssetWorld(actor);
            snap.Self.BaseHexes = new[] { Secondary };
            snap.Threat.Threats[0].EnemyEta = 1;
            Assert.That(ActiveDefenceObjectiveEvaluator.ArrivalEta(snap, actor, Secondary), Is.EqualTo(2));
            Assert.That(ActiveDefenceObjectiveEvaluator.SafeWithdrawalBase(snap, actor, null), Is.Null,
                "a doomed base cannot become safe merely because a distant strong actor plans to return");
        }

    }
}
#endif
