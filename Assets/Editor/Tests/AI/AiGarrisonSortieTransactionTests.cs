#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // Native Unity EditMode fixture. Do not run through the reference-DLL/Mono harness.
    public class AiGarrisonSortieTransactionTests
    {
        private static readonly HexCoord Home = new HexCoord(40, 40), Goal = new HexCoord(41, 40);
        private PlayerSetupData owner, enemyOwner;
        private PlayerRoot root;
        private GameObject scene;
        private HexMap map;
        private HexSelectionController selector;
        private FactionCardCatalog cards;
        private StartingDeckCatalog catalog;
        private AiTurnContext ctx;
        private ArmyData donor, shell, enemy;
        private UnitData strong, medium, cover;
        private WorldSnapshot snapshot;

        [SetUp] public void Setup()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); VisionSystem.Configure(null);
            PlayerRootRegistry.Clear(); MissionIntentRegistry.Clear(); StrategicResourceReservationLedger.ClearAll();
            AiMapMemory.Clear(); AiMapMemory.EnsureSubscribed();
            owner = new PlayerSetupData { Nickname = "sortie" };
            enemyOwner = new PlayerSetupData { Nickname = "enemy" };
            root = PlayerRoot.Create(owner, "sortie transaction"); root.ActionPoints = 20;
            PlayerRootRegistry.Register(owner, root);
            scene = new GameObject("sortie map"); map = scene.AddComponent<HexMap>();
            var terrain = HexGridMath.HexesInRange(Home, 3).ToDictionary(h => h,
                h => new TerrainTypeEntry { terrainName = "plain", moveCost = 1 });
            map.SetData(60, 1, terrain);
            selector = scene.AddComponent<HexSelectionController>(); selector.enabled = false;
            typeof(HexSelectionController).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(selector, map);
            cards = ScriptableObject.CreateInstance<FactionCardCatalog>(); cards.faction = owner.Faction;
            catalog = ScriptableObject.CreateInstance<StartingDeckCatalog>(); catalog.catalogs.Add(cards);
            ctx = new AiTurnContext { Map = map, HexSelection = selector, StartingDeckCatalog = catalog, TurnNumber = 9 };
            BuildingRegistry.Register(Home, new BuildingData { Owner = owner, Hex = Home, IsBase = true });
            donor = new ArmyData { Owner = owner, Hex = Home, IsGarrison = true };
            strong = Body(owner, 20, 10, 30); medium = Body(owner, 12, 7, 22); cover = Body(owner, 2, 2, 8);
            donor.Members.AddRange(new[] { strong, medium, cover }); ArmyRegistry.Register(donor);
            shell = new ArmyData { Owner = owner, Hex = Home }; ArmyRegistry.Register(shell);
            shell.Controller = scene.AddComponent<ArmyController>(); shell.Controller.SetData(shell);
            enemy = new ArmyData { Owner = enemyOwner, Hex = Goal };
            enemy.Members.Add(Body(enemyOwner, 1, 0, 2)); ArmyRegistry.Register(enemy);
            VisionSystem.RecomputeFor(owner);
            snapshot = Snapshot();
        }
        [TearDown] public void Cleanup()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); AiMapMemory.Clear();
            PlayerRootRegistry.Clear(); MissionIntentRegistry.Clear(); StrategicResourceReservationLedger.ClearAll();
            Object.DestroyImmediate(root.gameObject); Object.DestroyImmediate(scene);
            Object.DestroyImmediate(catalog); Object.DestroyImmediate(cards);
        }
        private static UnitData Body(PlayerSetupData p, int atk, int def, int hp) => new UnitData
        { Owner = p, Attack = atk, Defense = def, HitPointsCurrent = hp, HitPointsMax = hp,
            MoveCurrent = 3, MoveMax = 3, ActivationApCost = 2 };
        private WorldSnapshot Snapshot()
        {
            var profiles = donor.Members.Select(WorthIt.FromLiveUnit).ToList();
            var ds = new ArmySnapshot { ArmyId = donor.Id, Owner = owner, Hex = Home, IsGarrison = true,
                Members = profiles, MemberCount = profiles.Count, EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(profiles),
                NonHeroRuntimeIds = donor.Members.Select(u => u.RuntimeId).ToArray(),
                NonHeroSpareable = donor.Members.Select(u => AiArmyRoles.CanSpareGarrisonMember(owner, donor, u)).ToArray(),
                NonHeroActivationApCosts = donor.Members.Select(u => u.ActivationApCost).ToArray(),
                NonHeroCurrentMovement = new[] { 3, 3, 3 }, NonHeroMoveMax = new[] { 3, 3, 3 },
                NonHeroIsAviation = new[] { false, false, false } };
            var ep = enemy.Members.Select(WorthIt.FromLiveUnit).ToList();
            return new WorldSnapshot { Observer = owner, Map = map, TurnNumber = 9,
                Self = new SelfSnapshot { Citadel = Home, BaseHexes = new[] { Home }, Armies = new[] { ds,
                    new ArmySnapshot { ArmyId = shell.Id, Owner = owner, Hex = Home, Capacity = shell.Capacity } } },
                Known = WorldAnalysis.BuildKnown(owner, new[] { Home }),
                Threat = new ThreatModel { Contacts = new[] { new EnemyContactSnapshot { Knowledge = ContactKnowledge.Exact,
                    Position = Goal, Confidence = 1, Army = new ArmySnapshot { ArmyId = enemy.Id, Owner = enemyOwner,
                        Members = ep, MemberCount = ep.Count, EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(ep) } } },
                    Threats = Array.Empty<AssetThreatSnapshot>() } };
        }
        private ProvisionedMission Pending(bool create = false)
        {
            if (create) { ArmyRegistry.Unregister(shell); snapshot.Self.Armies = snapshot.Self.Armies.Take(1).ToArray(); }
            var objective = ActiveDefenceObjectiveEvaluator.EnumerateImmediate(snapshot).Single(o => o.Target.EnemyArmyId == enemy.Id);
            var response = ActiveDefenceObjectiveEvaluator.AssessResponse(snapshot, objective, null, null, null);
            Assert.That(response.Sortie, Is.Not.Null, "fixture must offer the canonical safe sortie");
            var t = objective.Target; t.Sortie = response.Sortie; t.PrimaryArmyId = response.Sortie.ShellArmyId;
            var p = new MissionProposal { Kind = MissionKind.ActiveDefence, Target = t,
                PreferredMoverArmyId = response.Sortie.Detachment.ArmyId };
            return new ProvisionedMission { Kind = MissionKind.ActiveDefence, Mission = p, Key = StableMissionKey.For(p),
                MoverArmyId = response.Sortie.Detachment.ArmyId, ActiveDefenceTarget = t,
                ClaimedAp = response.Sortie.TotalAp, FocusHex = Goal, ExecutionHex = Goal };
        }
        private static void Drain(IEnumerator sequence)
        { while (sequence.MoveNext()) if (sequence.Current is IEnumerator nested) Drain(nested); }

        [TestCase(false)] [TestCase(true)]
        public void TaskStep_ExtractsExactlyOnceAndReportsActualActorAndActualAp(bool create)
        {
            var pm = Pending(create); int before = root.ActionPoints; var output = new List<ExecutionResult>();
            Drain(TaskExecutor.ExecuteStep(owner, root, ctx, pm, output, snapshot, enforceFreshPlan: false));
            var r = output.Single(); Assert.That(r.ActorMaterialized, Is.True); Assert.That(r.Outcome.StateChanged, Is.True);
            Assert.That(r.StopReason, Is.EqualTo(ExecutionStopReason.StepCompleted)); Assert.That(r.StepsMoved, Is.Zero);
            Assert.That(r.ApSpent, Is.EqualTo(create ? ArmyActions.CreateArmyApCost : 0));
            var actor = ArmyRegistry.AllForOwner(owner).Single(a => a.Id == r.ActualActorArmyId);
            Assert.That(actor.Hex, Is.EqualTo(Home)); Assert.That(actor.IsGarrison, Is.False);
            Assert.That(actor.Members, Is.EquivalentTo(new[] { strong })); Assert.That(donor.Members, Does.Contain(cover));
            Assert.That(pm.ActiveDefenceTarget.Sortie, Is.Not.Null); Assert.That(pm.ActiveDefenceTarget.IsMaterializedSortie, Is.True);
            Assert.That(root.ActionPoints, Is.EqualTo(before - r.ApSpent)); Assert.That(actor.HasActivatedThisTurn, Is.False);
            Assert.That(ArmyActions.TryPayActivation(actor, root), Is.True);
            Assert.That(root.ActionPoints, Is.EqualTo(before - r.ApSpent - strong.ActivationApCost));
            var ignored = new ExecutionResult();
            Assert.That(ActiveDefenceExecutor.TryMaterializeSortie(owner, root, ctx, pm, ignored, snapshot), Is.False);
        }
        [TestCase(false)] [TestCase(true)]
        public void Provision_IsReadOnlyAndRequiresTheFullActivationEnvelope(bool create)
        {
            var pm = Pending(create); using var session = new ProvisioningSession(snapshot);
            var funded = new FundedEntry { Mission = pm.Mission, Tentative = new ResourceVector(pm.ClaimedAp) };
            int before = root.ActionPoints, armies = ArmyRegistry.AllForOwner(owner).Count();
            var result = ActiveDefenceProvisioner.Provision(owner, root, ctx, session, funded);
            Assert.That(result.Success, Is.True); Assert.That(result.Provisioned.ClaimedAp, Is.EqualTo(pm.ClaimedAp));
            Assert.That(root.ActionPoints, Is.EqualTo(before)); Assert.That(donor.Members.Count, Is.EqualTo(3));
            Assert.That(ArmyRegistry.AllForOwner(owner).Count(), Is.EqualTo(armies));
            funded.Tentative = new ResourceVector(pm.ClaimedAp - 1);
            Assert.That(ActiveDefenceProvisioner.Provision(owner, root, ctx, session, funded).Success, Is.False);
        }
        [TestCase("ap")] [TestCase("body")] [TestCase("cover")] [TestCase("shell")] [TestCase("activated")]
        [TestCase("moved")] [TestCase("hidden")] [TestCase("turn")] [TestCase("cooccupant")] [TestCase("floor")]
        public void StalePendingPlan_FailsWithoutApOrPartialExtraction(string change)
        {
            var pm = Pending();
            switch (change)
            {
                case "ap": root.ActionPoints = 0; break;
                case "body": donor.Members.Remove(strong); break;
                case "cover": donor.Members.Remove(cover); break;
                case "shell": shell.Members.Add(Body(owner, 2, 2, 5)); break;
                case "activated": shell.MarkActivated(); break;
                case "moved": ArmyRegistry.MoveArmy(enemy, new HexCoord(42, 40)); break;
                case "hidden": typeof(VisionSystem).GetField("Visible", BindingFlags.Static | BindingFlags.NonPublic)
                    .GetValue(null).GetType().GetMethod("Clear").Invoke(typeof(VisionSystem)
                    .GetField("Visible", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), null); break;
                case "turn": ctx.TurnNumber++; break;
                case "cooccupant": var other = new ArmyData { Owner = enemyOwner, Hex = Goal };
                    other.Members.Add(Body(enemyOwner, 100, 100, 200)); ArmyRegistry.Register(other); break;
                case "floor": var reserve = new ArmyData { Owner = owner, Hex = new HexCoord(39, 40) };
                    reserve.Members.Add(Body(owner, 1000, 1000, 2000)); ArmyRegistry.Register(reserve); break;
            }
            int ap = root.ActionPoints; var before = donor.Members.ToArray(); int count = ArmyRegistry.AllForOwner(owner).Count();
            var result = new ExecutionResult();
            Assert.That(ActiveDefenceExecutor.TryMaterializeSortie(owner, root, ctx, pm, result, snapshot), Is.True);
            Assert.That(result.ActorMaterialized, Is.False); Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.TargetInvalidated));
            Assert.That(root.ActionPoints, Is.EqualTo(ap)); Assert.That(donor.Members, Is.EqualTo(before));
            Assert.That(ArmyRegistry.AllForOwner(owner).Count(), Is.EqualTo(count));
        }
        [TestCase(false)] [TestCase(true)]
        public void PinnedDonorAndShellCannotBeClaimedTwice(bool claimDonor)
        {
            var pm = Pending(); using var session = new ProvisioningSession(snapshot);
            session.ClaimedArmyIds.Add(claimDonor ? donor.Id : shell.Id);
            Assert.That(ActiveDefenceProvisioner.Provision(owner, root, ctx, session,
                new FundedEntry { Mission = pm.Mission, Tentative = new ResourceVector(pm.ClaimedAp) }).Success, Is.False);
            Assert.That(donor.Members.Count, Is.EqualTo(3)); Assert.That(root.ActionPoints, Is.EqualTo(20));
        }
        [TestCase("body")] [TestCase("added")] [TestCase("cover")] [TestCase("moved")] [TestCase("cooccupant")] [TestCase("floor")]
        public void MaterializedPlan_RechecksTheSameLiveWitnessBeforeAnyMovement(string change)
        {
            var pm = Pending(); var result = new ExecutionResult();
            ActiveDefenceExecutor.TryMaterializeSortie(owner, root, ctx, pm, result, snapshot);
            Assert.That(result.ActorMaterialized, Is.True);
            var actor = ArmyRegistry.AllForOwner(owner).Single(a => a.Id == pm.MoverArmyId);
            Assert.That(ActiveDefenceProvisioner.TryEvaluateSortie(owner, ctx, pm.ActiveDefenceTarget,
                actor.Hex, actor.Members, out _), Is.True);
            switch (change)
            {
                case "body": actor.Members.Clear(); break;
                case "added": actor.Members.Add(Body(owner, 1, 1, 5)); break;
                case "cover": donor.Members.Remove(cover); break;
                case "moved": ArmyRegistry.MoveArmy(enemy, new HexCoord(42, 40)); break;
                case "cooccupant": var other = new ArmyData { Owner = enemyOwner, Hex = Goal };
                    other.Members.Add(Body(enemyOwner, 100, 100, 200)); ArmyRegistry.Register(other); break;
                case "floor": var reserve = new ArmyData { Owner = owner, Hex = new HexCoord(39, 40) };
                    reserve.Members.Add(Body(owner, 1000, 1000, 2000)); ArmyRegistry.Register(reserve); break;
            }
            int ap = root.ActionPoints;
            Assert.That(ActiveDefenceProvisioner.TryEvaluateSortie(owner, ctx, pm.ActiveDefenceTarget,
                actor.Hex, actor.Members, out _), Is.False);
            Assert.That(root.ActionPoints, Is.EqualTo(ap));
        }
        [Test] public void DomainBatchCreation_RejectsOversizeRosterBeforeCreatingOrCharging()
        {
            var roster = donor.Members.ToList();
            roster.Add(Body(owner, 2, 2, 5)); donor.Members.Add(roster.Last());
            int before = root.ActionPoints, armies = ArmyRegistry.AllForOwner(owner).Count();
            Assert.That(ArmyActions.CreateArmyWithMembers(owner, Home, cards, donor, roster, selector, out _), Is.Null);
            Assert.That(root.ActionPoints, Is.EqualTo(before)); Assert.That(donor.Members, Is.EqualTo(roster));
            Assert.That(ArmyRegistry.AllForOwner(owner).Count(), Is.EqualTo(armies));
        }
    }
}
#endif
