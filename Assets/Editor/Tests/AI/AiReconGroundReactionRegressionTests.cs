#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiReconGroundReactionRegressionTests
    {
        private GameObject mapObject;
        private HexMap map;
        private PlayerSetupData player, enemy;
        private ArmyData scout;
        private readonly HexCoord targetHex = new HexCoord(1, 0);

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AiMapMemory.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null);
            ReconPatrolStateRegistry.ClearAll(); ScoutTrailRegistry.ClearAll();
            player = new PlayerSetupData { CitadelHexQ = 8, CitadelHexR = 0 };
            enemy = new PlayerSetupData();
            mapObject = new GameObject("ground-recon-reaction-regression");
            map = mapObject.AddComponent<HexMap>();
            SetMap(HexGridMath.HexesInRange(default, 3).ToDictionary(h => h,
                h => new TerrainTypeEntry { moveCost = 1 }));
            scout = Army(player, default, 30, 30, 10, true);
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AiMapMemory.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null);
            ReconPatrolStateRegistry.ClearAll(); ScoutTrailRegistry.ClearAll();
            UnityEngine.Object.DestroyImmediate(mapObject);
        }

        [TestCase(3, 2, false)]
        [TestCase(3, 3, true)]
        [TestCase(3, 4, true)]
        [TestCase(1, 0, false)]
        [TestCase(0, 0, false)]
        [TestCase(0, 1, true)]
        public void AdjacentAttack_RequiresTheCanonicalEntryCost(int terrainCost, int movement, bool attack)
        {
            ArmyData target = Army(enemy, targetHex, 1, 1, 1, true);
            map.SetTerrainAt(targetHex, new TerrainTypeEntry { moveCost = terrainCost });
            scout.Members[0].MoveCurrent = movement;
            ReconReactionDecision decision = Evaluate();
            Assert.That(decision.Action, Is.EqualTo(attack
                ? ReconReactionAction.AttackOpportunity : ReconReactionAction.Continue));
            if (attack) Assert.That(decision.TargetArmyId, Is.EqualTo(target.Id));
            Assert.That(scout.CurrentMovement, Is.EqualTo(movement));
        }

        // 0=fog, 1=neutral, 2=garrison, 3=prison, 4=non-scout,
        // 5=hidden target, 6=non-combat mover, 7=no damage coverage, 8=losing battle.
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
        [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)]
        public void AffordableAttack_PreservesExistingCombatRestrictions(int restriction)
        {
            ArmyData target = Army(enemy, targetHex, 1, 1, 1, restriction != 4);
            switch (restriction)
            {
                case 0: Visible().Remove(targetHex); break;
                case 1: enemy.IsNeutral = true; break;
                case 2: target.IsGarrison = true; break;
                case 3: target.IsPrison = true; break;
                case 5: target.Members[0].IsHidden = true; break;
                case 6: scout.Members[0].IsHero = true; break;
                case 7: scout.Members[0].Attack = 0; break;
                case 8:
                    target.Members[0].Attack = 100; target.Members[0].Defense = 100;
                    target.Members[0].HitPointsCurrent = target.Members[0].HitPointsMax = 100;
                    break;
            }
            Assert.That(Evaluate().Action, Is.EqualTo(ReconReactionAction.Continue));
        }

        [Test]
        public void AffordableAttack_StillRejectsAnUnsafePostCombatPosition()
        {
            Army(enemy, targetHex, 1, 1, 1, true);
            RememberThreat(new HexCoord(2, 0));
            Assert.That(WeakOpportunity(), Is.Null);
        }

        [Test]
        public void AffordableAttack_StillRejectsCriticalAfterBattleRisk()
        {
            ArmyData target = Army(enemy, targetHex, 1, 1, 1, true);
            UnitData own = scout.Members[0];
            own.HitPointsCurrent = 1; own.HitPointsMax = 20;
            var estimate = WorthIt.Estimate(scout,
                target.Members.Select(WorthIt.FromLiveUnit).ToList(), 0f);
            Assert.That(estimate.WinChance, Is.GreaterThanOrEqualTo(AiConfigV2.scoutReactionAttackWinChance));
            Assert.That(estimate.CriticalAfterBattleChance, Is.GreaterThan(AiConfigV2.scoutReactionAttackMaxCriticalAfter));
            Assert.That(WeakOpportunity(), Is.Null);
        }

        [Test]
        public void InaccessibleGarrison_DoesNotHideAnAlternativeSafeEscape()
        {
            var escape = new HexCoord(-1, 0);
            SetMap(new Dictionary<HexCoord, TerrainTypeEntry>
            {
                [default] = new TerrainTypeEntry { moveCost = 1 },
                [escape] = new TerrainTypeEntry { moveCost = 1 },
                [new HexCoord(8, 0)] = new TerrainTypeEntry { moveCost = 1 },
            });
            RememberThreat(targetHex);
            ReconReactionDecision reaction = Evaluate();
            Assert.That(reaction.Action, Is.EqualTo(ReconReactionAction.Flee));
            Assert.That(reaction.TargetHex, Is.EqualTo(escape));
            Assert.That(SafeStepPathing.FindNextSafeStep(map, scout, reaction.TargetHex.Value), Is.EqualTo(escape));
        }

        [TestCase(false)] [TestCase(true)]
        public void NoSafeNeighbor_FleeHasNoUnprovenDestination(bool unaffordable)
        {
            SetMap(new Dictionary<HexCoord, TerrainTypeEntry>
            {
                [default] = new TerrainTypeEntry { moveCost = 1 },
                [targetHex] = new TerrainTypeEntry { moveCost = unaffordable ? 3 : 1,
                    blocksGroundMovement = !unaffordable },
                [new HexCoord(8, 0)] = new TerrainTypeEntry { moveCost = 1 },
            });
            scout.Members[0].MoveCurrent = 1;
            RememberThreat(new HexCoord(0, 1));
            Assert.That(Evaluate().Action, Is.EqualTo(ReconReactionAction.Flee));
            Assert.That(Evaluate().TargetHex, Is.Null,
                "No local escape exists; a disconnected garrison must not be emitted as an executable target.");
        }

        [Test]
        public void PartiallyBlockedRoute_FleeUsesASafeDetour()
        {
            var goal = new HexCoord(-2, 0);
            var blocked = new HexCoord(-1, 0);
            var detour = new HexCoord(-1, 1);
            SetMap(new Dictionary<HexCoord, TerrainTypeEntry>
            {
                [default] = new TerrainTypeEntry { moveCost = 1 },
                [blocked] = new TerrainTypeEntry { moveCost = 1, blocksGroundMovement = true },
                [detour] = new TerrainTypeEntry { moveCost = 1 },
                [new HexCoord(-2, 1)] = new TerrainTypeEntry { moveCost = 1 },
                [goal] = new TerrainTypeEntry { moveCost = 1 },
            });
            RememberThreat(targetHex);
            ReconReactionDecision reaction = Evaluate();
            Assert.That(reaction.Action, Is.EqualTo(ReconReactionAction.Flee));
            HexCoord? step = SafeStepPathing.FindNextSafeStep(map, scout, reaction.TargetHex.Value);
            Assert.That(step, Is.EqualTo(detour));
            Assert.That(ScoutExecutionSafety.StepBlocked(player, scout, step.Value), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void OccupiedEscape_IsRejectedEvenByDestinationExemption(bool isHome)
        {
            SetMap(new Dictionary<HexCoord, TerrainTypeEntry>
            {
                [default] = new TerrainTypeEntry { moveCost = 1 },
                [targetHex] = new TerrainTypeEntry { moveCost = 1 },
            });
            if (isHome) { player.CitadelHexQ = targetHex.Q; player.CitadelHexR = targetHex.R; }
            RememberThreat(targetHex);
            Assert.That(Evaluate().Action, Is.EqualTo(ReconReactionAction.Flee));
            Assert.That(Evaluate().TargetHex, Is.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void TrappedScout_StopsWithoutACommandOrLoop_AndCanRetryAfterTheRouteOpens(bool continuousRun)
        {
            SetMap(new Dictionary<HexCoord, TerrainTypeEntry>
            { [default] = new TerrainTypeEntry { moveCost = 1 } });
            RememberThreat(targetHex);
            int movement = scout.CurrentMovement;
            long revision = WorldDeltaLifecycle.Current;
            for (int retry = 0; retry < 2; retry++)
            {
                var pm = new ProvisionedMission { MoverArmyId = scout.Id,
                    ScoutKind = ScoutTargetKind.Explore, FocusHex = new HexCoord(-3, 0) };
                var result = new ExecutionResult();
                var control = new ReconGroundExecutor.StepControl();
                var ctx = new AiTurnContext { Map = map, TurnNumber = 1 };
                Drain(continuousRun
                    ? ReconGroundExecutor.Run(player, null, ctx, pm, result, 0, new[] { pm }, 0)
                    : ReconGroundExecutor.RunStep(player, null, ctx, pm, result, 0,
                        new[] { pm }, 0, null, control));
                Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.NoSafeStep));
                if (!continuousRun) Assert.That(control.StopReason, Is.EqualTo(ExecutionStopReason.NoSafeStep));
                Assert.That(control.CanContinue, Is.False);
                Assert.That(control.CommandAttempted, Is.False);
                Assert.That(result.StepsMoved, Is.Zero);
                Assert.That(result.ApSpent, Is.Zero);
                Assert.That(scout.Hex, Is.EqualTo(default(HexCoord)));
                Assert.That(scout.CurrentMovement, Is.EqualTo(movement));
                Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(revision));
                var step = new MissionStepResult { MissionKind = MissionKind.Scout };
                MissionStepResultPolicy.Classify(result, step);
                Assert.That(step.Disposition, Is.EqualTo(MissionStepDisposition.Waiting));
            }
            SetMap(new Dictionary<HexCoord, TerrainTypeEntry>
            {
                [default] = new TerrainTypeEntry { moveCost = 1 },
                [new HexCoord(-1, 0)] = new TerrainTypeEntry { moveCost = 1 },
            });
            Assert.That(Evaluate().TargetHex, Is.EqualTo(new HexCoord(-1, 0)),
                "No permanent blocked flag or cached no-path result may survive changed map data.");
        }

        private void SetMap(Dictionary<HexCoord, TerrainTypeEntry> cells)
        {
            map.SetData(8, 1f, cells);
            // Freeze the observer's current visibility independently of registry notifications.
            var visible = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visible", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            visible[player] = new HashSet<HexCoord>(cells.Keys);
        }
        private ArmyData Army(PlayerSetupData owner, HexCoord hex, int attack, int defense, int hp, bool recce)
        {
            var unit = new UnitData { Owner = owner, Attack = attack, Defense = defense,
                HitPointsCurrent = hp, HitPointsMax = hp, MoveCurrent = 4, MoveMax = 4 };
            if (recce) unit.Abilities.Add("r3s0");
            var army = new ArmyData { Owner = owner, Hex = hex };
            army.Members.Add(unit);
            var registry = (Dictionary<HexCoord, List<ArmyData>>)typeof(ArmyRegistry)
                .GetField("ByHex", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            if (!registry.TryGetValue(hex, out var atHex)) registry[hex] = atHex = new List<ArmyData>();
            atHex.Add(army);
            return army;
        }

        private ReconReactionDecision Evaluate() => ReconReactionPolicy.Evaluate(player, map, scout,
            new ReconPatrolState { StrategicAnchor = new HexCoord(-3, 0), Mode = ReconMode.Explore });
        private ReconReactionDecision? WeakOpportunity() => (ReconReactionDecision?)typeof(ReconReactionPolicy)
            .GetMethod("FindWeakScoutOpportunity", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { player, map, scout });
        private HashSet<HexCoord> Visible() => ((Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
            .GetField("Visible", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))[player];

        // Inject an honest remembered roster, as in AiReconRuleOwnershipTests. No TrueWorld or
        // native visibility mutation is needed to represent the observed threat.
        private void RememberThreat(HexCoord hex)
        {
            Type type = typeof(AiMapMemory).GetNestedType("EnemySighting", BindingFlags.NonPublic);
            var record = Activator.CreateInstance(type, nonPublic: true);
            void Put(string name, object value) => type.GetField(name).SetValue(record, value);
            Put("ArmyId", 900); Put("Owner", enemy); Put("Hex", hex); Put("MemberCount", 1);
            Put("Defenders", new List<WorthIt.DefenderProfile> { WorthIt.FromLiveUnit(new UnitData
                { Attack = 100, Defense = 100, HitPointsCurrent = 100, HitPointsMax = 100 }) });
            IDictionary all = (IDictionary)typeof(AiMapMemory).GetField("EnemySightings",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var store = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int), type));
            store.Add(900, record); all[player] = store;
        }

        private static void Drain(IEnumerator routine)
        {
            int iterations = 0;
            var stack = new Stack<IEnumerator>(); stack.Push(routine);
            while (stack.Count > 0)
            {
                Assert.That(++iterations, Is.LessThan(16), "a blocked action must terminate promptly");
                IEnumerator current = stack.Peek();
                if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); stack.Pop(); }
                else if (current.Current is IEnumerator nested) stack.Push(nested);
                else Assert.Fail("A trapped scout must not schedule native movement or animation.");
            }
        }
    }
}
#endif
