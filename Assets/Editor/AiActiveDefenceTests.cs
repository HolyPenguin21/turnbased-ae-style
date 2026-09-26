#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
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
            Assert.That(System.Enum.GetNames(typeof(ActiveDefencePhase)),
                Is.EquivalentTo(new[] { "Intercept", "Return" }));
        }

        [Test]
        public void ActiveDefenceScore_UsesCanonicalSlotsWithoutSeverityMultiplier()
        {
            var objective = new ActiveDefenceObjective
            {
                TaskScore = new TaskScore(strategicRelevance: 10f,
                    threatDirection: 4f, militaryTargetRelevance: 3f,
                    staleness: -1f, ownTerritoryProximity: 2f),
            };
            var actor = new ArmySnapshot
            {
                ActivationApCost = 2, HasActivatedThisTurn = false,
            };
            TaskScore score = ActiveDefenceObjectiveEvaluator.WithResponse(
                objective, actor, winChance: 0.75f, eta: 2, moverOpportunityCost: 1f);

            Assert.That(score.StrategicRelevance, Is.EqualTo(10f));
            Assert.That(score.ThreatDirection, Is.EqualTo(4f));
            Assert.That(score.MilitaryTargetRelevance, Is.EqualTo(3f));
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

        [Test]
        public void PursuitStopsOnlyWhenEveryGuardAgrees()
        {
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, true, true, false), Is.True);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                true, true, true, false), Is.False);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, false, true, false), Is.False);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, true, false, false), Is.False);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, true, true, true), Is.False);
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
        public void Continuity_StartedReturn_ContinuesAfterThreatVanishes_AndRetiresOnArrival()
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
                Assert.That(active, Does.Contain(withdrawal), "the walk continues without the threat");

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

                MissionContinuityLayer.ReconcileAfterTurn(player, 6, new List<MissionTurnOutcome>
                {
                    new MissionTurnOutcome
                    {
                        IntentKey = intercept.IntentKey,
                        MissionKind = MissionKind.ActiveDefence,
                        Outcome = ExecutionOutcome.Completed,
                        ObjectiveSatisfied = true,
                        MoverArmyId = 7,
                    },
                });

                Assert.That(state.TryGet(MissionIntentKey.ForActiveDefence(42), out _), Is.False);
                Assert.That(state.All, Is.Empty);
            }
            finally { MissionIntentRegistry.Clear(); }
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

        private static WorldSnapshot DefenceWorld(float bestStack, float totalPotential)
        {
            var enemy = new PlayerSetupData { Nickname = "DefenceEnemy", ColorIndex = 2 };
            var hex = new HexCoord(4, 0);
            var body = new WorthIt.DefenderProfile(3f, false, null, 4f, 8f, 2);
            var contact = new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    ArmyId = 42,
                    Owner = enemy,
                    EffectiveArmyPower = 7f,
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
    }
}
#endif
