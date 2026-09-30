#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Game.Ai;
using Game.Ai.V2;
using Game.Map;
using Game.Terrain;
using Game.Units;
using UnityEngine;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiReconRuleOwnershipTests
    {
        [TestCase(0, 0f)]
        [TestCase(1, 0.5f)]
        [TestCase(2, 1f)]
        [TestCase(3, 1f)]
        public void DetectionRisk_LiveFrozenAndEnumerableAgree(int detectors, float expected)
        {
            var player = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            var focus = new HexCoord(4, 3);
            // Inject honest remembered observations, without relying on native scene visibility.
            // Near-memory includes neutrals/ownerless encounters; the live owner must exclude them.
            Type recordType = typeof(AiMapMemory).GetNestedType("EnemySighting", BindingFlags.NonPublic);
            IDictionary byPlayer = (IDictionary)typeof(AiMapMemory)
                .GetField("EnemySightings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var store = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>)
                .MakeGenericType(typeof(int), recordType));
            var honest = new List<AiMapMemory.KnownEnemySighting>();
            for (int id = 0; id < detectors + 3; id++)
            {
                PlayerSetupData owner = id < detectors || id == detectors + 2 ? enemy
                    : id == detectors ? new PlayerSetupData { IsNeutral = true } : null;
                HexCoord hex = id == detectors + 2 ? new HexCoord(30, 30) : focus;
                object record = Activator.CreateInstance(recordType, nonPublic: true);
                recordType.GetField("ArmyId").SetValue(record, id);
                recordType.GetField("Owner").SetValue(record, owner);
                recordType.GetField("Hex").SetValue(record, hex);
                store.Add(id, record);
                if (owner == enemy)
                    honest.Add(new AiMapMemory.KnownEnemySighting(hex, owner, "known", 1, 1f, 1f,
                        null, armyId: id));
            }
            byPlayer.Add(player, store);
            try
            {
                var snap = new WorldSnapshot { Known = new KnownSnapshot { EnemySightings = honest } };
                Assert.That(ScoutRiskModel.DetectorRiskLive(player, focus), Is.EqualTo(expected));
                Assert.That(ScoutRiskModel.DetectorRisk(snap, focus), Is.EqualTo(expected));
                Assert.That(ScoutRiskModel.DetectorRisk(honest, focus), Is.EqualTo(expected));
            }
            finally { byPlayer.Remove(player); }
        }

        [Test]
        public void DiscoveryIdentity_DeduplicatesAndPreservesArmyZero()
        {
            var hex = new HexCoord(0, 0);
            var sightings = new[] { 0, 7, 0, 9, 7 }.Select(id =>
                new AiMapMemory.KnownEnemySighting(hex, null, "known", 1, 0f, 0f, null, armyId: id));
            Assert.That(AiV2Util.KnownArmyIds(sightings), Is.EquivalentTo(new[] { 0, 7, 9 }));
            Assert.That(AiV2Util.KnownArmyIds(Array.Empty<AiMapMemory.KnownEnemySighting>()), Is.Empty);
        }

        [TestCase(false, false, false, 1, true)]
        [TestCase(true, false, false, 1, false)]
        [TestCase(false, true, false, 1, false)]
        [TestCase(false, false, true, 1, false)]
        [TestCase(false, false, false, 0, false)]
        public void GroundShape_ExcludesNonScoutsPrisonsAircraftAndEmptyActors(
            bool notScout, bool prison, bool air, int members, bool expected)
        {
            var actor = new ArmySnapshot { IsSoloRecce = !notScout, IsPrison = prison,
                IsAir = air, MemberCount = members, CurrentMovement = 0 };
            Assert.That(ScoutMoverSelector.IsGroundScout(actor), Is.EqualTo(expected));
            Assert.That(ScoutMoverSelector.IsGroundScout(null), Is.False);
        }

        [TestCase(false, 2, (int)ReconAirPhase.Hold)]
        [TestCase(true, 2, (int)ReconAirPhase.Outbound)]
        [TestCase(true, 0, (int)ReconAirPhase.Return)]
        public void HoldProjection_UsesSamePhaseAsExecution(bool newTurn, int safeEnds, int expected)
        {
            Assert.That(ReconAirSortieLifecycle.PhaseAfterHold(ReconAirPhase.Hold, newTurn, safeEnds),
                Is.EqualTo((ReconAirPhase)expected));
            Assert.That(ReconAirSortieLifecycle.PhaseAfterHold(ReconAirPhase.Turning, newTurn, safeEnds),
                Is.EqualTo(ReconAirPhase.Turning));
        }

        [Test]
        public void ModeProjection_UsesDurablePatrolAndDoesNotCreateAnAssignment()
        {
            var player = new PlayerSetupData();
            try
            {
                Assert.That(AirReconModePolicy.EffectiveMode(player, 7, ReconMode.Refresh),
                    Is.EqualTo(ReconMode.Refresh));
                Assert.That(ReconPatrolStateRegistry.TryGet(player, 7, out _), Is.False);
                ReconPatrolStateRegistry.GetOrCreate(player, 7, default, new HexCoord(4, 0), ReconMode.Explore, 1);
                Assert.That(AirReconModePolicy.EffectiveMode(player, 7, ReconMode.Refresh),
                    Is.EqualTo(ReconMode.Explore));
            }
            finally { ReconPatrolStateRegistry.ClearAll(); }
        }

        [TestCase(4, 4, 4, 1)]
        [TestCase(0, 4, 4, 2)]
        [TestCase(1, 8, 4, 3)]
        [TestCase(0, 0, 4, 1)]
        public void TravelEstimate_PairCostAndVantageRankingAgree(
            int remaining, int distance, int movement, int expected)
        {
            var vantage = new HexCoord(distance, 0);
            var mover = new ArmySnapshot { Hex = default, CurrentMovement = remaining,
                MaxMovement = movement, EffectiveVisionRadius = 1 };
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = new[] { mover } },
                MapKnowledge = new MapKnowledgeSnapshot { AllHexes = new[] { vantage } },
            };
            var target = new ScoutMissionTarget { Kind = ScoutTargetKind.Surveil,
                FocusHex = new HexCoord(distance + 1, 0) };
            Assert.That(ScoutCostModel.PairCost(snap, mover, vantage, false).EtaTurns,
                Is.EqualTo(expected));
            Assert.That(SurveilVantageSelector.Rank(snap, mover, target).Single().EtaTurns,
                Is.EqualTo(expected));
        }

        [Test]
        public void FirstRouteStep_ExcludesTheOccupiedCellAndHandlesMissingPaths()
        {
            var start = new HexCoord(0, 0);
            var next = new HexCoord(1, 0);
            Assert.That(AiAirSortiePlanner.FirstRouteStep((HexPath)null), Is.Null);
            Assert.That(AiAirSortiePlanner.FirstRouteStep(new HexPath(new List<HexCoord> { start }, 0)), Is.Null);
            Assert.That(AiAirSortiePlanner.FirstRouteStep(new HexPath(new List<HexCoord> { start, next }, 1)),
                Is.EqualTo(next));
        }

        // Native map components are intentionally exercised in Unity, not replaced with an
        // alternate physical model in the managed test harness.
        [TestCase(0)] // available
        [TestCase(1)] // landing capacity taken
        [TestCase(2)] // captured by an enemy
        [TestCase(3)] // movement no longer sufficient
        public void ReturnPlannerAndDirector_AgreeAfterLandingChanges(int change)
        {
            var player = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            var mapObject = new GameObject("recon-recovery-parity");
            try
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
                HexMap map = mapObject.AddComponent<HexMap>();
                var terrain = new TerrainTypeEntry { moveCost = 1 };
                var hexes = new Dictionary<HexCoord, TerrainTypeEntry>();
                for (int q = 0; q <= 4; q++) hexes[new HexCoord(q, 0)] = terrain;
                map.SetData(4, 1f, hexes);
                var oldHome = new HexCoord(0, 0);
                var alternative = new HexCoord(4, 0);
                var oldBase = new BuildingData { Owner = player, Hex = oldHome, IsBase = true,
                    AirfieldCapacity = 1 };
                BuildingRegistry.Register(oldHome, oldBase);
                BuildingRegistry.Register(alternative, new BuildingData { Owner = player,
                    Hex = alternative, IsBase = true, AirfieldCapacity = 1 });
                var air = new ArmyData { Owner = player, Hex = new HexCoord(2, 0), IsAirArmy = true };
                air.AddMemberSorted(new UnitData { Owner = player, IsAviation = true,
                    MoveMax = 4, MoveCurrent = change == 3 ? 1 : 4, TurnsWithoutRefuel = 0 });
                ArmyRegistry.Register(air);
                if (change == 1)
                {
                    var other = new ArmyData { Owner = player, Hex = oldHome, IsAirArmy = true };
                    other.AddMemberSorted(new UnitData { Owner = player, IsAviation = true });
                    ArmyRegistry.Register(other);
                }
                if (change == 2) oldBase.Owner = enemy;
                Assert.That(AiAirSortiePlanner.CanReturnThisTurnTo(player, map, air, oldHome),
                    Is.EqualTo(change == 0));
                HexCoord? planned = AiAirSortiePlanner.TryReplan(air, map, player);
                var sortie = new ReconAirSortieState { Phase = ReconAirPhase.Return,
                    ChosenLandingHex = oldHome, HasChosenLanding = true };
                object[] args = { player, map, air, sortie, default(HexCoord), null };
                var step = (HexCoord?)typeof(AirReconStepDirector)
                    .GetMethod("PickReturnStep", AllMethods).Invoke(null, args);
                Assert.That(step.HasValue, Is.EqualTo(planned.HasValue));
                if (step.HasValue)
                {
                    var landing = (HexCoord)args[4];
                    Assert.That(AiAirSortiePlanner.CanReturnThisTurnTo(player, map, air, landing), Is.True);
                    Assert.That(step, Is.EqualTo(AiAirSortiePlanner.FirstRouteStep(map, air.Hex, landing)));
                    Assert.That(landing, Is.EqualTo(change == 0 ? oldHome : alternative));
                }
            }
            finally
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
                UnityEngine.Object.DestroyImmediate(mapObject);
            }
        }

        [Test]
        public void AirAndGroundDirection_UseTheSameSanitizedConcentration()
        {
            var player = new PlayerSetupData();
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot { Citadel = default,
                    Armies = new[] { new ArmySnapshot { Owner = player } } },
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[]
                {
                    new ArmySnapshot { Hex = new HexCoord(4, 0), ArmyId = 20 },
                    null,
                    new ArmySnapshot { Hex = new HexCoord(8, 0), ArmyId = 30 },
                    new ArmySnapshot { Hex = new HexCoord(-4, 0), ArmyId = 40 },
                } },
            };
            ReconDirectionSnapshot direction = ReconDirectionModel.Build(snap);
            AirReconAnchorSet air = AirReconAnchorModel.Build(snap, player, 1);
            Assert.That(direction.EnemyPresenceWeight, Is.EqualTo(3));
            Assert.That(direction.EnemyDirectionSectors[ReconSector.E], Is.EqualTo(2f / 3f));
            Assert.That(direction.EnemyDirectionSectors[ReconSector.W], Is.EqualTo(1f / 3f));
            foreach (AirReconStrategicAnchor anchor in air.Anchors
                .Where(a => a.Kind == AirReconAnchorKind.EnemyConcentration))
            {
                Assert.That(anchor.Weight, Is.EqualTo(direction.EnemyDirectionSectors[anchor.Sector]
                    * AiConfigV2.airReconAnchorConcentrationWeight));
                Assert.That(anchor.HasFocus, Is.False, "hidden army positions remain sector-only");
            }
        }

        [Test]
        public void KnownCitadel_UsesFirstHonestForeignCitadel()
        {
            var player = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            var hex = new HexCoord(4, 0);
            var snap = new WorldSnapshot { Known = new KnownSnapshot { Buildings = new[]
            {
                new AiMapMemory.KnownBuilding(default, player, true, null),
                new AiMapMemory.KnownBuilding(new HexCoord(2, 0), enemy, false, null),
                new AiMapMemory.KnownBuilding(hex, enemy, true, null),
                new AiMapMemory.KnownBuilding(new HexCoord(8, 0), enemy, true, null),
            } } };
            Assert.That(ReconDirectionModel.KnownEnemyCitadel(snap, player).Value.Hex, Is.EqualTo(hex));
            Assert.That(ReconDirectionModel.KnownEnemyCitadel(null, player), Is.Null);
        }

        // Pin actual compiled call dependencies, rather than reimplementing the deleted formulas
        // in test helpers. These tests fail if a consumer forks its own rule again.
        [Test]
        public void GroundPlannerAndReaction_CallTheRiskOwner()
        {
            AssertCalls(typeof(ReconGroundStepPlanner), "TryScoreImmediate", typeof(ScoutRiskModel), "DetectorRiskLive");
            AssertCalls(typeof(ReconGroundStepPlanner), "Lookahead", typeof(ScoutRiskModel), "DetectorRiskLive");
            AssertCalls(typeof(ReconReactionPolicy), "PickLowerDetectorRiskStep", typeof(ScoutRiskModel), "DetectorRiskLive");
            AssertCalls(typeof(ReconGroundExecutor), "LegDetectionRisk", typeof(ScoutRiskModel), "DetectorRisk");
            Assert.That(typeof(ReconGroundStepPlanner).GetMethod("DetectorRisk", AllMethods), Is.Null);
            Assert.That(typeof(ReconReactionPolicy).GetMethod("CurrentDetectorRisk", AllMethods), Is.Null);
        }

        [Test]
        public void GroundSizingAndEnumeration_CallTheSameShapeOwner()
        {
            AssertCalls(typeof(ReconCapacitySnapshot), "Build", typeof(ScoutMoverSelector), "IsGroundScout");
            AssertCalls(typeof(ScoutMoverSelector), "Eligible", typeof(ScoutMoverSelector), "IsGroundScout");
            AssertCalls(typeof(ScoutCostModel), "PairCost", typeof(ScoutCostModel), "TravelTurns");
            AssertCalls(typeof(SurveilVantageSelector), "Rank", typeof(ScoutCostModel), "TravelTurns");
        }

        [Test]
        public void AirPolicyAndRecovery_CallPhysicalAndLifecycleOwners()
        {
            AssertCalls(typeof(AirReconStepDirector), "ApplyLandingHysteresis", typeof(AiAirSortiePlanner), "CanReturnThisTurnTo");
            AssertCalls(typeof(AiAirSortiePlanner), "TryReplan", typeof(AiAirSortiePlanner), "CanReturnThisTurnTo");
            AssertCalls(typeof(AirReconStepDirector), "PickReturnStep", typeof(AiAirSortiePlanner), "FirstRouteStep");
            AssertCalls(typeof(AirReconStepDirector), "PlanStep", typeof(ReconAirSortieLifecycle), "PhaseAfterHold");
            AssertCalls(typeof(ReconAirReservationPrepass), "ProjectScoringSortie", typeof(ReconAirSortieLifecycle), "PhaseAfterHold");
            AssertCalls(typeof(ReconAirReservationPrepass), "BuildScoringContextForWing", typeof(AirReconModePolicy), "EffectiveMode");
            AssertCalls(typeof(AirReconStepDirector), "PlanStep", typeof(AirReconModePolicy), "EffectiveMode");
            AssertCalls(typeof(AirReconAnchorModel), "Build", typeof(ReconDirectionModel), "EnemyConcentration");
            AssertCalls(typeof(ReconDirectionModel), "Build", typeof(ReconDirectionModel), "EnemyConcentration");
            AssertCalls(typeof(AirReconAnchorModel), "Build", typeof(ReconDirectionModel), "KnownEnemyCitadel");
            AssertCalls(typeof(ReconAssignmentPlanner), "AppendAirCandidates", typeof(ReconAirReservationPrepass), "EvaluateAirStructuralFeasibility");
            AssertCalls(typeof(ReconAssignmentPlanner), "AirActorProgressesAnObjective", typeof(ReconAirReservationPrepass), "EvaluateAirStructuralFeasibility");
            AssertCalls(typeof(ReconAirReservationPrepass), "EvaluateAirStructuralFeasibility", typeof(ReconAirStepPlanner), "Pick");
            AssertCalls(typeof(ReconAirStepPlanner), "Pick", typeof(AiAirSortiePlanner), "TryPlanSortie");
            AssertCalls(typeof(ReconAirStepPlanner), "Pick", typeof(AiAirSortiePlanner), "TryPlanMultiTurnSortie");
        }

        [Test]
        public void BothExecutors_UseSharedDiscoveryAndCompletionRules()
        {
            AssertCalls(typeof(ReconGroundExecutor), "MaybeEnterOptionalStealth", typeof(StrategicSpendability), "SpendableAp");
            AssertCalls(typeof(ReconGroundExecutor), "RunPreparedStep", typeof(AiV2Util), "KnownArmyIds");
            AssertCalls(typeof(ReconAirExecutor), "RecordDiscoveries", typeof(AiV2Util), "KnownArmyIds");
            AssertCalls(typeof(ReconGroundExecutor), "RefreshObjectiveSatisfied", typeof(ScoutObjectiveEvaluator), "IsSatisfiedLive");
            AssertCalls(typeof(ReconAirExecutor), "ObjectiveSatisfied", typeof(ScoutObjectiveEvaluator), "IsSatisfiedLive");
        }

        private const BindingFlags AllMethods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static void AssertCalls(Type consumer, string method, Type owner, string rule)
        {
            MethodInfo caller = consumer.GetMethods(AllMethods).Where(m => m.Name == method)
                .OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
            Assert.That(caller, Is.Not.Null, consumer.Name + "." + method);
            Assert.That(CalledMethods(caller).Any(m => m.DeclaringType == owner && m.Name == rule),
                Is.True, consumer.Name + "." + method + " must call " + owner.Name + "." + rule);
        }

        private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
        {
            var opcodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
                .ToDictionary(o => unchecked((ushort)o.Value));
            IteratorStateMachineAttribute iterator = method.GetCustomAttribute<IteratorStateMachineAttribute>();
            if (iterator != null)
                method = iterator.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            byte[] il = method.GetMethodBody().GetILAsByteArray();
            for (int i = 0; i < il.Length;)
            {
                ushort value = il[i++];
                if (value == 0xfe) value = (ushort)(0xfe00 | il[i++]);
                OpCode op = opcodes[value];
                if (op.OperandType == OperandType.InlineMethod)
                    yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: i++; break;
                    case OperandType.InlineVar: i += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: i += 8; break;
                    case OperandType.InlineSwitch:
                        i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                    default: i += 4; break;
                }
            }
        }
    }
}
#endif
