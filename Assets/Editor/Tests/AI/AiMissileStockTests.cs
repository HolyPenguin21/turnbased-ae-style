#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiMissileStockTests
    {
        private PlayerSetupData owner, enemy;
        private readonly HexCoord target = new HexCoord(4, 0);
        private CardDefinition missile;

        private static CardDefinition Read(string path, string key)
        {
            string block = AttachmentContentTestData.Blocks(path)
                .Single(b => AttachmentContentTestData.Scalar(b, "authoredKey") == key);
            var card = AttachmentContentTestData.Read(block);
            card.launchEnergyCost = AttachmentContentTestData.Number(block, "launchEnergyCost");
            card.turnsWithoutRefuel = AttachmentContentTestData.Number(block, "turnsWithoutRefuel");
            return card;
        }

        [SetUp]
        public void Setup()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null); AiMapMemory.Clear();
            MissionIntentRegistry.Clear(); StrategicResourceReservationLedger.ClearAll();
            owner = new PlayerSetupData(); enemy = new PlayerSetupData();
            missile = Read(AttachmentContentTestData.NeutralPath, "neutral.production.unit.tactical-missile");
        }

        [TearDown]
        public void Cleanup()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null); AiMapMemory.Clear();
            MissionIntentRegistry.Clear(); StrategicResourceReservationLedger.ClearAll();
        }

        private WorthIt.DefenderProfile[] Infantry(int count) => Enumerable.Range(0, count)
            .Select(_ => WorthIt.FromLiveUnit(AttachmentSlotTests.Body(Read(
                "Assets/Cards/IronConcord/CardCatalog_IronConcord.asset", "concord.card.light-infantry")))).ToArray();

        private AiMapMemory.KnownAirSighting Sighting(WorthIt.DefenderProfile[] roster,
            int id = 42, HexCoord? hex = null, int seen = 10) => new AiMapMemory.KnownAirSighting(
                hex ?? target, enemy, seen, true, new AviationCombatEstimator.DefendingAirArmy(id, roster, new int[roster.Length]));

        private WorldSnapshot Snapshot(params AiMapMemory.KnownAirSighting[] contacts)
        {
            var bodies = contacts.FirstOrDefault().Roster.Units ?? Array.Empty<WorthIt.DefenderProfile>();
            float power = AiPower.EffectiveArmyPowerFromProfiles(bodies);
            var stock = new ResourceBundle { Human = 50, Energy = 50, Materials = 50, Tech = 50 };
            return new WorldSnapshot { Observer = owner, TurnNumber = 10,
                Self = new SelfSnapshot { Hand = Array.Empty<CardData>(), Deck = Array.Empty<CardDefinition>(),
                    ActionPoints = 20, Stockpile = stock, FieldPower = power, BestStackPotential = power, TotalPower = power,
                    Armies = new[] { new ArmySnapshot { ArmyId = 7, Owner = owner, MemberCount = bodies.Count,
                        EffectiveArmyPower = power, Members = bodies, MembersWithHeroes = bodies } },
                    PerTurnIncome = new ResourceBundle { Human = 3, Energy = 3, Materials = 3, Tech = 3 },
                    ApEconomy = new ApActionEconomySnapshot { BaseActionPoints = 20, WitnessedApDemand = 2 } },
                Known = new KnownSnapshot { AirSightings = contacts, EnemyKnownStrength = contacts.Sum(s => AiPower.EffectiveArmyPowerFromProfiles(s.Roster.Units)),
                    EnemySightings = contacts.Select(s => new AiMapMemory.KnownEnemySighting(s.Hex,
                        s.Owner, "observed", s.Roster.Units.Count, s.Roster.Units.Sum(p => p.Defense),
                        s.Roster.Units.Sum(p => p.Attack), s.Roster.Units,
                        seenTurn: s.SeenTurn, armyId: s.ArmyId)).ToArray() },
                Economy = new EconomyStanding { SpendableStockpile = stock },
                Development = new DevelopmentReadiness(),
            };
        }

        [Test]
        public void EqualLargeArmiesWantReadyMissileBeforeAnyAssaultOrThreat()
        {
            var snap = Snapshot(Sighting(Infantry(10)));
            var executable = AviationRebasePlanner.CombatSupportTargets(snap, Array.Empty<MissionIntent>());
            Assert.That(executable, Has.Count.EqualTo(1));
            Assert.That(executable.Single().IsStockPreparation, Is.True);
            Assert.That(executable.Single().Key, Is.EqualTo(MissionIntentKey.ForActiveDefenceAirSupport(42)));
            var requests = NonCombatCardPlayer.KnownStrikeTargets(snap, owner, null, null, Array.Empty<ReconObjective>());
            Assert.That(requests, Has.Count.EqualTo(1), "A known large army must justify preparation before Assault");
            Assert.That(requests.Single().Target, Is.EqualTo(target));
        }

        [Test]
        public void PreparationStrikeIsAirOnlyAndUsesTheExistingEnemyOperationIdentity()
        {
            var snap = Snapshot(Sighting(Infantry(10)), Sighting(Infantry(10), 43));
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(snap), Is.Empty);
            var objective = GroundCombatAirSupport.AirSupportObjectives(snap).Single();
            Assert.That(objective.Target.InterceptPurpose, Is.EqualTo(ActiveDefenceInterceptPurpose.PreparationStrike));
            Assert.That(objective.Target.PrimaryArmyId, Is.Null);
            Assert.That(objective.Target.EnemyEta, Is.Null);
            Assert.That(objective.Target.ProtectedAssetValue, Is.Zero);
            Assert.That(objective.Target.IntentKey(), Is.EqualTo(MissionIntentKey.ForActiveDefenceAirSupport(42)));
            Assert.That((int)ActiveDefenceInterceptPurpose.StrategicDefence, Is.Zero);
            Assert.That((int)ActiveDefenceInterceptPurpose.ImmediateOpportunity, Is.EqualTo(1));
        }

        [Test]
        public void PreparationDoesNotDuplicateAnExistingAttackSupportAssignment()
        {
            var snap = Snapshot(Sighting(Infantry(10)));
            var held = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Attack = new AttackIntent { Target = new AttackTarget { Hex = target }, AirSupportArmyId = 9 } };
            Assert.That(GroundCombatAirSupport.AirSupportObjectives(snap, new[] { held }), Is.Empty);
            Assert.That(AviationRebasePlanner.CombatSupportTargets(snap, new[] { held }), Is.Empty);
        }

        [Test]
        public void PreparationNeverSacrificesAnOwnedSecondaryBase()
        {
            var snap = Snapshot(Sighting(Infantry(100)));
            snap.Known.Buildings = new[] { new AiMapMemory.KnownBuilding(target, owner, false,
                Array.Empty<string>(), isBase: true) };
            Assert.That(GroundCombatAirSupport.MissileStockValue(snap, Aircraft(), target), Is.NegativeInfinity);
            Assert.That(Gaps(snap), Is.Empty);
        }

        private List<UnitData> Aircraft() => NonCombatCardPlayer.ProjectedAircraft(missile, owner);
        private List<CombatAirSupportRequest> Gaps(WorldSnapshot snap, IReadOnlyList<CardData> hand = null,
            CardData self = null) => GroundCombatAirSupport.UncoveredMissileStock(snap, Aircraft(),
                GroundCombatAirSupport.MissileStockTargets(snap), hand, snap.Map, self);

        [Test]
        public void ThresholdUsesExactLiveBlastValueAndSaturatesAtTenTargets()
        {
            float previous = float.NegativeInfinity;
            foreach (int count in new[] { 1, 3, 5, 6, 7, 10, 20 })
            {
                var snap = Snapshot(Sighting(Infantry(count)));
                var bodies = Enumerable.Range(0, count).Select(_ => {
                    var unit = AttachmentSlotTests.Body(Read("Assets/Cards/IronConcord/CardCatalog_IronConcord.asset",
                        "concord.card.light-infantry")); unit.Owner = enemy; return unit;
                }).ToArray();
                float net = GroundCombatAirSupport.MissileStockValue(snap, Aircraft(), target);
                Assert.That(net, Is.EqualTo(ExplosionAttack.ExpectedNetValue(Aircraft()[0], bodies)).Within(.001f));
                Assert.That(net, Is.GreaterThanOrEqualTo(previous));
                Assert.That(Gaps(snap).Count, Is.EqualTo(count < 7 ? 0 : 1));
                TestContext.WriteLine($"infantry={count}; net={net:R}");
                previous = net;
            }
            float ten = GroundCombatAirSupport.MissileStockValue(Snapshot(Sighting(Infantry(10))), Aircraft(), target);
            Assert.That(previous, Is.EqualTo(ten).Within(.001f));
        }

        [Test]
        public void StockIsHonestRecentEnemyPlayerDemandAndOneSlotPerHex()
        {
            var snap = Snapshot(Sighting(Infantry(10)), Sighting(Infantry(10), 43));
            snap.TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] {
                new ArmySnapshot { Members = Infantry(1000) } } };
            Assert.That(Gaps(snap), Has.Count.EqualTo(1));
            enemy.IsNeutral = true;
            Assert.That(Gaps(snap), Is.Empty);
            enemy.IsNeutral = false; enemy.IsEliminated = true;
            Assert.That(Gaps(snap), Is.Empty);
            enemy.IsEliminated = false;
            Assert.That(Gaps(Snapshot(Sighting(Infantry(10), seen: 7))), Is.Empty);
            snap.Known.AirSightings = Array.Empty<AiMapMemory.KnownAirSighting>();
            Assert.That(Gaps(snap), Is.Empty, "TrueWorld must never invent a missile target");
        }

        [Test]
        public void HandStockExcludesBlueprintAndDeploysOldestWithoutSelfBlocking()
        {
            var snap = Snapshot(Sighting(Infantry(10)));
            var blueprint = new CardData(Read(AttachmentContentTestData.NeutralPath, "neutral.tactic.tactical-missile"));
            var first = ResearchProductionSystem.MintCard(missile);
            var second = ResearchProductionSystem.MintCard(missile);
            Assert.That(Gaps(snap, new[] { blueprint }), Has.Count.EqualTo(1));
            Assert.That(Gaps(snap, new[] { first }), Is.Empty);
            Assert.That(Gaps(snap, new[] { first }, first), Has.Count.EqualTo(1));
            Assert.That(Gaps(snap, new[] { first, second }, first), Has.Count.EqualTo(1));
            Assert.That(Gaps(snap, new[] { first, second }, second), Is.Empty);
        }

        [TestCase(AirSortieKind.Strike)]
        [TestCase(AirSortieKind.Rebase)]
        public void PhysicalMissileCountsOnceAndConsumptionReopensStock(AirSortieKind kind)
        {
            var snap = Snapshot(Sighting(Infantry(10)));
            var unit = Aircraft()[0];
            var army = new ArmyData { Owner = owner, Hex = default, IsAirArmy = true };
            army.Members.Add(unit); ArmyRegistry.Register(army);
            AirSortieRegistry.Add(owner, new AirSortie { Army = army, Kind = kind, TargetHex = target, LandingHex = default });
            Assert.That(Gaps(snap), Is.Empty);
            unit.HitPointsCurrent = 0;
            Assert.That(Gaps(snap), Has.Count.EqualTo(1));
        }

        [Test]
        public void OneCommittedMissileCoversOnlyOneOfTwoTargetsAndForecastDoesNotMutateBank()
        {
            var other = new HexCoord(8, 0);
            var snap = Snapshot(Sighting(Infantry(10)), Sighting(Infantry(10), 43, other));
            var army = new ArmyData { Owner = owner, IsAirArmy = true };
            army.Members.Add(Aircraft()[0]); ArmyRegistry.Register(army);
            var sortie = new AirSortie { Army = army, Kind = AirSortieKind.Strike, TargetHex = target,
                StrikePolicy = new AirStrikePolicy(AirStrikePolicyKind.Standard, 42) };
            AirSortieRegistry.Add(owner, sortie);
            StrategicResourceReservationLedger.BeginTurn(owner, 10);
            StrategicResourceReservationLedger.Upsert(owner, 10, new StrategicResourceReservation {
                Owner = "existing-operation", Reason = StrategicReservationReason.StrategicReactionPass,
                Resource = StrategicReservedResource.Energy, Amount = 7,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn });
            int version = WorldDeltaLifecycle.Current;
            for (int i = 0; i < 3; i++)
                Assert.That(Gaps(snap).Single().Target, Is.EqualTo(other));
            Assert.That(StrategicResourceReservationLedger.Active(owner, 10, StrategicReservedResource.Energy), Is.EqualTo(7));
            Assert.That(AirSortieRegistry.ForArmy(owner, army), Is.SameAs(sortie));
            Assert.That(army.Members, Has.Count.EqualTo(1));
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(version));
        }

        [Test]
        public void ProtectedHeroesDoNotDiluteBlastAndFriendlyCitadelRejectsIt()
        {
            var infantry = Infantry(10);
            var hero = new WorthIt.DefenderProfile(0, false, hitPoints: 100, maxHitPoints: 100,
                isHero: true, fateMax: 20, isGroundCombatant: false);
            float ordinary = GroundCombatAirSupport.MissileStockValue(Snapshot(Sighting(infantry)), Aircraft(), target);
            var snap = Snapshot(Sighting(infantry.Concat(new[] { hero }).ToArray()));
            Assert.That(GroundCombatAirSupport.MissileStockValue(snap, Aircraft(), target), Is.EqualTo(ordinary).Within(.001f));
            snap.Known.Buildings = new[] { new AiMapMemory.KnownBuilding(target, owner, true,
                Array.Empty<string>(), explosionStructures: new[] {
                    new WorthIt.DefenderProfile(0, false, hitPoints: 5, maxHitPoints: 5) }) };
            Assert.That(Gaps(snap), Is.Empty);
        }

        [Test]
        public void BomberWithSingleUseEquipmentDoesNotBecomeAMissile()
        {
            var plane = Aircraft()[0];
            plane.OriginatingCard = new CardDefinition { cardType = CardType.Unit, isAviation = true,
                grantedAbilities = new List<string>() };
            Assert.That(GroundCombatAirSupport.IsStockMissileGroup(new[] { plane }), Is.False);
            var snap = Snapshot(Sighting(Infantry(10)));
            Assert.That(GroundCombatAirSupport.UncoveredMissileStock(snap, new[] { plane },
                GroundCombatAirSupport.MissileStockTargets(snap), null, null), Is.Empty);
        }

        private WorthIt.DefenderProfile[] MixedArmy()
        {
            var cards = new[] { ("light-infantry", 4), ("light-tank", 3), ("medium-tank", 3) };
            return cards.SelectMany(c => Enumerable.Range(0, c.Item2).Select(_ => WorthIt.FromLiveUnit(
                AttachmentSlotTests.Body(Read("Assets/Cards/IronConcord/CardCatalog_IronConcord.asset", "concord.card." + c.Item1))))).ToArray();
        }

        [TestCase("IronConcord", "concord.card.bolt-jax", "ghost-genome", 1)]
        [TestCase("IronConcord", "concord.card.bolt-jax", "hunter-genome", 1)]
        [TestCase("IronConcord", "concord.card.bolt-jax", "reflex-genome", 1)]
        [TestCase("IronConcord", "concord.card.bolt-jax", "fortunate-genome", 2)]
        [TestCase("TheAshen", "ashen.card.rusty-miller", "ghost-genome", 1)]
        [TestCase("TheAshen", "ashen.card.rusty-miller", "hunter-genome", 1)]
        [TestCase("TheAshen", "ashen.card.rusty-miller", "reflex-genome", 1)]
        [TestCase("TheAshen", "ashen.card.rusty-miller", "fortunate-genome", 2)]
        public void AuthoredMutagensImproveFullProductionScoreOnTheSameForecastScale(
            string faction, string heroKey, string genomeKey, int gain)
        {
            var snap = Snapshot(Sighting(MixedArmy()));
            var heroDef = Read($"Assets/Cards/{faction}/CardCatalog_{faction}.asset", heroKey);
            var hero = AttachmentSlotTests.Body(heroDef); hero.Owner = owner;
            Assert.That(hero.HasAbility(UnitAbilities.Assembler), Is.True);
            var blueprint = Read(AttachmentContentTestData.NeutralPath, "neutral.tactic.tactical-missile");
            var genome = Read(AttachmentContentTestData.NeutralPath, "neutral.mutator." + genomeKey);
            Assert.That(blueprint.fate, Is.EqualTo(6));
            Assert.That(ResearchProductionSystem.AttemptApCost(blueprint), Is.EqualTo(6));
            Assert.That(blueprint.resourceCost.energy, Is.EqualTo(5));
            Assert.That(blueprint.resourceCost.materials, Is.EqualTo(6));
            Assert.That(blueprint.resourceCost.tech, Is.EqualTo(5));
            GenerationStep Source() => new GenerationStep { CardDef = missile, BlueprintDef = blueprint,
                Hero = hero, Mode = ResearchProductionMode.Production,
                CardKey = "missile-test", SuccessChance = ResearchProductionSystem.EstimateSuccessChance(hero, blueprint) };
            var source = Source();
            float net = GroundCombatAirSupport.MissileStockValue(snap, Aircraft(), target);
            var service = GroundCombatAirSupport.ServiceScore(GroundCombatAirSupport.MissileStockTargets(snap).Single().Score,
                GroundCombatAirSupport.MissileStockEffect(net, Aircraft()[0]), 1, 1, 1);
            StrategicCardUseCandidate Score(GenerationStep step) => StrategicCardEvaluator.ScoreNonCombat(
                NonCombatRole.Aviation, ResearchProductionSystem.MintCard(missile), snap, null, null, 0,
                generation: step, player: owner, operationalTask: service);
            var before = Score(source);
            string key = DevelopmentAdmission.OperatorForecastFacts(snap, owner);
            snap.Development.ForecastSources = new[] { source };
            var stock = snap.Economy.SpendableStockpile;
            EquipmentSystem.ApplyAttachments(hero, null, genome);
            Assert.That(hero.Fate, Is.EqualTo(heroDef.fate + gain));
            var afterSource = Source();
            var after = Score(afterSource);
            float utility = NonCombatCardPlayer.OperatorAviationServiceValue(snap, source, service);
            float deployPrice = ActionPrice.ToCardScore(missile.activationApCost);
            Assert.That(after.NetScore - before.NetScore, Is.EqualTo(
                (afterSource.SuccessChance - source.SuccessChance) * (utility - deployPrice)).Within(.001f));
            Assert.That(after.NetScore, Is.GreaterThan(before.NetScore));
            Assert.That(after.NetScore, Is.GreaterThan(AiConfigV2.devEvMargin));
            Assert.That(DevelopmentAdmission.OperatorForecastFacts(snap, owner), Is.Not.EqualTo(key));
            foreach (ResourceType type in ResourceBundle.All)
                Assert.That(snap.Economy.SpendableStockpile.Get(type), Is.EqualTo(stock.Get(type)));
            Assert.That(snap.Self.Hand, Is.Empty);
            TestContext.WriteLine($"{heroKey}/{genomeKey}: p={source.SuccessChance:R}->{afterSource.SuccessChance:R}; "
                + $"blast={net:R}; score={before.NetScore:R}->{after.NetScore:R}");
        }

        [Test]
        public void CacheFactsSeeRosterHandAndSortieChangesWithoutResourceDelta()
        {
            var snap = Snapshot(Sighting(Infantry(10)));
            string key = DevelopmentAdmission.OperatorForecastFacts(snap, owner);
            snap.Known.AirSightings = new[] { Sighting(Infantry(7)) };
            Assert.That(DevelopmentAdmission.OperatorForecastFacts(snap, owner), Is.Not.EqualTo(key));
            key = DevelopmentAdmission.OperatorForecastFacts(snap, owner);
            snap.Self.Hand = new[] { ResearchProductionSystem.MintCard(missile) };
            Assert.That(DevelopmentAdmission.OperatorForecastFacts(snap, owner), Is.Not.EqualTo(key));
            var army = new ArmyData { Owner = owner, IsAirArmy = true };
            army.Members.Add(Aircraft()[0]); ArmyRegistry.Register(army);
            key = DevelopmentAdmission.OperatorForecastFacts(snap, owner);
            AirSortieRegistry.Add(owner, new AirSortie { Army = army, Kind = AirSortieKind.Rebase,
                LandingHex = new HexCoord(2, 0) });
            Assert.That(DevelopmentAdmission.OperatorForecastFacts(snap, owner), Is.Not.EqualTo(key));
        }

        [Test]
        public void FriendlyCollateralCanCloseOtherwiseProfitableStockDemand()
        {
            var snap = Snapshot(Sighting(Infantry(10)));
            Assert.That(Gaps(snap), Has.Count.EqualTo(1));
            snap.Self.Armies[0].Hex = target;
            Assert.That(Gaps(snap), Is.Empty, "Equal friendly and enemy bodies cancel before the missile sacrifice");
        }

        [Test]
        public void StructureSnapshotAndLiveValueChargeFriendlyDamageEqually()
        {
            var building = new BuildingData { Owner = owner, Hex = target, StructurePointsMax = 20,
                StructurePointsCurrent = 20, Defense = 1 };
            var actor = Aircraft()[0];
            var live = new ExplosionAttack.Target(building).Combatant();
            var profile = ExplosionAttack.StructureProfiles(building).Single();
            float actual = ExplosionAttack.ExpectedNetValue(actor, new[] { live });
            float projected = ExplosionAttack.ExpectedNetValue(WorthIt.FromLiveUnit(actor),
                new[] { new AviationCombatEstimator.ExplosionCollateral(profile, true) });
            Assert.That(projected, Is.EqualTo(actual).Within(.001f));
            Assert.That(projected, Is.LessThan(0));
        }

        private sealed class EngineWorld : IDisposable
        {
            private readonly UnityEngine.GameObject mapObject = new UnityEngine.GameObject("missile-stock-map");
            private readonly UnityEngine.GameObject selectorObject = new UnityEngine.GameObject("missile-stock-selector");
            internal readonly HexMap Map;
            internal readonly PlayerRoot Root;
            internal readonly AiTurnContext Context;
            internal EngineWorld(PlayerSetupData player, int length = 12)
            {
                Map = mapObject.AddComponent<HexMap>();
                var terrain = new Game.Terrain.TerrainTypeEntry { moveCost = 1 };
                Map.SetData(length, 1f, Enumerable.Range(0, length + 1)
                    .ToDictionary(q => new HexCoord(q, 0), _ => terrain));
                Root = PlayerRoot.Create(player, "missile-stock-player");
                Root.ActionPoints = 20;
                foreach (ResourceType type in ResourceBundle.All) Root.AddResource(type, 50);
                PlayerRootRegistry.Register(player, Root);
                selectorObject.SetActive(false);
                var selector = selectorObject.AddComponent<HexSelectionController>();
                typeof(HexSelectionController).GetField("map", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic).SetValue(selector, Map);
                Context = new AiTurnContext { Map = Map, HexSelection = selector, TurnNumber = 10 };
            }
            internal ArmyData Airfield(PlayerSetupData player, int q)
            {
                var hex = new HexCoord(q, 0);
                BuildingRegistry.Register(hex, new BuildingData { Owner = player, Hex = hex,
                    IsBase = true, AirfieldCapacity = 3 });
                var army = new ArmyData { Owner = player, Hex = hex, IsAirfield = true };
                ArmyRegistry.Register(army); return army;
            }
            public void Dispose()
            {
                PlayerRootRegistry.Clear();
                UnityEngine.Object.DestroyImmediate(selectorObject);
                UnityEngine.Object.DestroyImmediate(mapObject);
                UnityEngine.Object.DestroyImmediate(Root.gameObject);
            }
        }

        private static List<MissionProposal> AirProposals(WorldSnapshot snap, IReadOnlyList<MissionIntent> intents = null)
        {
            var proposals = new List<MissionProposal>();
            typeof(AggressionMissionLayer).GetMethod("AppendActiveDefenceAirSupport",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null,
                new object[] { snap, intents ?? Array.Empty<MissionIntent>(), new HashSet<int>(), proposals, null });
            return proposals;
        }

        private void Observe(ArmyData army)
        {
            var visible = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem).GetField("Visible",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            visible[owner] = new HashSet<HexCoord> { army.Hex };
            AiMapMemory.OnTurnStarted(owner, 10);
            AiMapMemory.RefreshVisibleForTest(owner);
        }

        private ArmyData EnemyArmy(int bodies = 10)
        {
            var army = new ArmyData { Owner = enemy, Hex = target, Name = "known missile target" };
            for (int i = 0; i < bodies; ++i)
            {
                var unit = AttachmentSlotTests.Body(Read("Assets/Cards/IronConcord/CardCatalog_IronConcord.asset",
                    "concord.card.light-infantry"));
                unit.Owner = enemy; army.Members.Add(unit);
            }
            ArmyRegistry.Register(army);
            Observe(army);
            return army;
        }

        private WorldSnapshot LiveMissileWorld(EngineWorld world)
        {
            var snap = Snapshot();
            snap.Map = world.Map;
            snap.Known = WorldAnalysis.BuildKnown(owner, Array.Empty<HexCoord>());
            snap.Self.Armies = OwnWorld.Of(owner).Armies
                .Select(a => WorldAnalysis.ToArmySnapshot(a, owner, true, 0, null, world.Context)).ToList();
            return snap;
        }

        [Test, Category("UnityEngine")]
        public void StoredMissileGetsFundedProvisionedAndClaimedOnceBeforeAnyAssault()
        {
            using var world = new EngineWorld(owner);
            var field = world.Airfield(owner, 0); field.Members.Add(Aircraft()[0]);
            EnemyArmy();
            var snap = LiveMissileWorld(world);
            Assert.That(ActiveDefenceObjectiveEvaluator.Enumerate(snap), Is.Empty);
            var proposal = AirProposals(snap).Single();
            var target = (ActiveDefenceMissionTarget)proposal.Target;
            Assert.That(target.InterceptPurpose, Is.EqualTo(ActiveDefenceInterceptPurpose.PreparationStrike));
            Assert.That(proposal.PreferredMoverArmyId, Is.EqualTo(field.Id));
            var funded = ResourceAllocator.BeginTurn(snap, Radar.Even(), new List<MissionProposal> { proposal },
                new List<Commitment>(), owner).Pack().Funded.Single();
            Assert.That(funded.Tentative.Ap, Is.GreaterThanOrEqualTo(1));
            Assert.That(funded.PhysicalDraw.Energy, Is.GreaterThanOrEqualTo(1));
            using var session = new ProvisioningSession(snap);
            var provision = ActiveDefenceProvisioner.Provision(owner, world.Root, world.Context, session, funded);
            Assert.That(provision.Success, Is.True, provision.Failure.Detail);
            Assert.That(provision.Provisioned.AircraftRuntimeIds, Is.EqualTo(new[] { field.Members.Single().RuntimeId }));
            session.RegisterSuccess(provision.Provisioned.Key, provision.Provisioned);
            session.RegisterSuccess(provision.Provisioned.Key, provision.Provisioned);
            Assert.That(session.ApClaimed, Is.EqualTo(provision.Provisioned.ClaimedAp));
            Assert.That(session.EnergyClaimed, Is.EqualTo(provision.Provisioned.ClaimedEnergy));
            Assert.That(ActiveDefenceProvisioner.Provision(owner, world.Root, world.Context, session, funded).Success, Is.False);
            Assert.That(Gaps(snap), Is.Empty, "physical inventory also covers production, once");
            var formed = new ExecutionResult();
            var wing = GroundCombatLegStep.MaterializeSupportWing(owner, world.Context, provision.Provisioned, formed, field);
            Assert.That(wing.Members.Count, Is.EqualTo(1));
            Assert.That(field.Members, Is.Empty);
            Assert.That(formed.ActualActorArmyId, Is.EqualTo(wing.Id));
            Assert.That(AirSortieRegistry.For(owner), Is.Empty, "formation is followed by the funded executor flight");
        }

        [Test, Category("UnityEngine")]
        public void PreparationRechecksBlastValueAndOtherOwnersEnergyHoldBeforePayment()
        {
            using var world = new EngineWorld(owner);
            var field = world.Airfield(owner, 0); field.Members.Add(Aircraft()[0]);
            ArmyData enemyArmy = EnemyArmy();
            var snap = LiveMissileWorld(world);
            var proposal = AirProposals(snap).Single();
            var funded = new FundedEntry { Mission = proposal, Tentative = new ResourceVector(20),
                PhysicalDraw = new ResourceVector(0, 0, 20, 0, 0) };
            using var session = new ProvisioningSession(snap);
            int ap = world.Root.ActionPoints;
            StrategicResourceReservationLedger.BeginTurn(owner, 10);
            StrategicResourceReservationLedger.Upsert(owner, 10, new StrategicResourceReservation {
                Owner = "other-operation", Resource = StrategicReservedResource.Energy,
                Amount = world.Root.GetResource(ResourceType.Energy), Reason = StrategicReservationReason.StrategicReactionPass,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn });
            Assert.That(ActiveDefenceProvisioner.Provision(owner, world.Root, world.Context, session, funded).Success, Is.False);
            StrategicResourceReservationLedger.ClearAll();
            enemyArmy.Members.RemoveRange(2, enemyArmy.Members.Count - 2);
            Observe(enemyArmy);
            Assert.That(ActiveDefenceProvisioner.Provision(owner, world.Root, world.Context, session, funded).Success, Is.False,
                "the old funded snapshot cannot justify a now-useless missile");
            Assert.That(world.Root.ActionPoints, Is.EqualTo(ap));
            Assert.That(field.Members.Count, Is.EqualTo(1));
            Assert.That(AirSortieRegistry.For(owner), Is.Empty);
        }

        [Test, Category("UnityEngine")]
        public void PreparationKeepsItsWingWithoutAThreatAndARealConsumedStrikeCompletes()
        {
            using var world = new EngineWorld(owner);
            var actorObject = new UnityEngine.GameObject("preparation-missile-wing");
            var presenterObject = new UnityEngine.GameObject("preparation-missile-presenter");
            try
            {
                world.Airfield(owner, 0);
                ArmyData enemyArmy = EnemyArmy();
                var wing = new ArmyData { Owner = owner, Hex = target, IsAirArmy = true };
                wing.Members.Add(Aircraft()[0]); ArmyRegistry.Register(wing);
                wing.Controller = actorObject.AddComponent<ArmyController>();
                var presenter = presenterObject.AddComponent<AviationCombatPresenter>();
                typeof(HexSelectionController).GetField("aviationCombatPresenter", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic).SetValue(world.Context.HexSelection, presenter);
                var snap = LiveMissileWorld(world);
                var proposal = AirProposals(snap).Single();
                var funded = ResourceAllocator.BeginTurn(snap, Radar.Even(), new List<MissionProposal> { proposal },
                    new List<Commitment>(), owner).Pack().Funded.Single();
                using var session = new ProvisioningSession(snap);
                var provision = ActiveDefenceProvisioner.Provision(owner, world.Root, world.Context, session, funded);
                Assert.That(provision.Success, Is.True, provision.Failure.Detail);
                var t = provision.Provisioned.ActiveDefenceTarget;
                var intent = new MissionIntent { Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active,
                    IntentKey = t.IntentKey(), PreferredMoverArmyId = wing.Id,
                    ActiveDefence = new ActiveDefenceIntent { Phase = ActiveDefencePhase.AirSupport,
                        InterceptPurpose = t.InterceptPurpose, EnemyArmyId = enemyArmy.Id, LastKnownHex = target,
                        AirSupportArmyId = wing.Id, AirSupportBoundTurn = 10, AirSupportLandingHex = t.AirSupportLandingHex } };
                var state = MissionIntentRegistry.GetOrCreate(owner); state.Put(intent); snap.Intents = state;
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Does.Contain(intent));
                Assert.That(ActorCommitments.FromIntents(new[] { intent }, snap, null).IsArmyClaimed(wing.Id), Is.True);
                int ap = world.Root.ActionPoints, energy = world.Root.GetResource(ResourceType.Energy);
                float hp = enemyArmy.Members.Sum(u => u.HitPointsCurrent);
                var execution = new ExecutionResult();
                AviationCombatPresenter.HeadlessRng = new Random(7);
                Drain(ActiveDefenceExecutor.RunActiveDefence(owner, world.Root, world.Context,
                    provision.Provisioned, execution, ap));
                Assert.That(execution.AirSupportStrikeSucceeded, Is.True);
                Assert.That(execution.ReachedGoal, Is.True, "a confirmed consumed missile completed its assignment");
                Assert.That(execution.StopReason, Is.EqualTo(ExecutionStopReason.ReachedGoal));
                Assert.That(enemyArmy.Members.Sum(u => u.HitPointsCurrent), Is.LessThan(hp));
                Assert.That(world.Root.ActionPoints, Is.EqualTo(ap - provision.Provisioned.ClaimedAp));
                Assert.That(world.Root.GetResource(ResourceType.Energy), Is.EqualTo(energy - provision.Provisioned.ClaimedEnergy));
                Observe(enemyArmy);
                snap = LiveMissileWorld(world); snap.Intents = state;
                Assert.That(MissionContinuityLayer.ResolveActive(owner, snap), Is.Empty);
                Assert.That(state.All, Is.Empty);
                Assert.That(ActorCommitments.FromIntents(state.All, snap, null).IsArmyClaimed(wing.Id), Is.False);
                Assert.That(AirSortieRegistry.For(owner), Is.Empty);
                Assert.That(AirProposals(snap), Is.Empty, "the consumed actor cannot launch a duplicate strike");
            }
            finally
            {
                AviationCombatPresenter.HeadlessRng = new Random();
                UnityEngine.Object.DestroyImmediate(presenterObject);
                UnityEngine.Object.DestroyImmediate(actorObject);
            }
        }

        private static void Drain(System.Collections.IEnumerator routine)
        {
            var stack = new Stack<System.Collections.IEnumerator>(); stack.Push(routine);
            int remaining = 10000;
            while (stack.Count > 0 && remaining-- > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is System.Collections.IEnumerator nested) stack.Push(nested);
            }
            Assert.That(remaining, Is.GreaterThan(0), "headless strike coroutine must terminate");
        }

        [Test, Category("UnityEngine")]
        public void RealPlacementForecastAndDeploymentShareOneReserveSlot()
        {
            using var world = new EngineWorld(owner);
            var snap = Snapshot(Sighting(MixedArmy())); snap.Map = world.Map;
            var field = world.Airfield(owner, 0);
            var hero = AttachmentSlotTests.Body(Read("Assets/Cards/IronConcord/CardCatalog_IronConcord.asset", "concord.card.bolt-jax"));
            hero.Owner = owner;
            var blueprint = Read(AttachmentContentTestData.NeutralPath, "neutral.tactic.tactical-missile");
            var source = new GenerationStep { CardDef = missile, BlueprintDef = blueprint, Hero = hero,
                Mode = ResearchProductionMode.Production, CardKey = "stock", SuccessChance = .5f };
            snap.Development.ForecastSources = new[] { source };
            var hand = new AiHandData(null, owner.Faction, 0);
            float investment = NonCombatCardPlayer.ProjectedAviationInvestmentValue(missile, source.Mode,
                default, hero, snap, owner, world.Root, hand, world.Context, source);
            Assert.That(investment, Is.GreaterThan(0));
            var forecast = EquipmentUpgradeValuation.OperatorOutputForecast(snap, null, hero, 4, 6);
            Assert.That(forecast, Is.Not.Empty);
            Assert.That(forecast[0].CostIfSuccess, Is.EqualTo(ActionPrice.ToCardScore(missile.activationApCost)));
            var minted = ResearchProductionSystem.MintCard(missile);
            hand.Hand.Add(minted); snap.Self.Hand = hand.Hand;
            var play = NonCombatCardPlayer.PlayForGeneratedCard(minted, snap, owner, world.Root, hand, world.Context);
            Assert.That(play, Is.Not.Null);
            int ap = world.Root.ActionPoints;
            Assert.That(NonCombatPlayExecutor.Execute(play, snap, owner, world.Root, hand, world.Context).Played, Is.True);
            Assert.That(world.Root.ActionPoints, Is.EqualTo(ap - missile.activationApCost));
            Assert.That(field.Members, Has.Count.EqualTo(1));
            Assert.That(hand.Hand, Is.Empty);
            Assert.That(EquipmentUpgradeValuation.OperatorOutputForecast(snap, null, hero, 4, 6), Is.Empty);
            Assert.That(AviationRebasePlanner.BuildFormationPlan(snap, owner, world.Root, world.Context,
                Array.Empty<ReconObjective>(), Array.Empty<MissionIntent>()), Is.Null);
            Assert.That(AirSortieRegistry.For(owner), Is.Empty);
        }

        [Test, Category("UnityEngine")]
        public void StockRebaseUsesIntermediateBaseAndCarrierAndRespectsLaunchHold()
        {
            using var world = new EngineWorld(owner);
            var snap = Snapshot(Sighting(MixedArmy(), hex: new HexCoord(12, 0))); snap.Map = world.Map;
            var source = world.Airfield(owner, 0); source.Members.Add(Aircraft()[0]);
            world.Airfield(owner, 4);
            var carrierArmy = new ArmyData { Owner = owner, Hex = new HexCoord(8, 0) };
            var carrier = new UnitData { Owner = owner, AirfieldCapacity = 2, HitPointsCurrent = 10, HitPointsMax = 10 };
            carrierArmy.Members.Add(carrier); ArmyRegistry.Register(carrierArmy);
            var plan = AviationRebasePlanner.BuildPlan(snap, owner, world.Root, world.Context, Array.Empty<ReconObjective>());
            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.DestinationHex, Is.EqualTo(new HexCoord(4, 0)));
            Assert.That(plan.ActivationAp, Is.EqualTo(1)); Assert.That(plan.EnergyCost, Is.EqualTo(1));
            StrategicResourceReservationLedger.BeginTurn(owner, 10);
            StrategicResourceReservationLedger.Upsert(owner, 10, new StrategicResourceReservation {
                Owner = "other-launch", Reason = StrategicReservationReason.StrategicReactionPass,
                Resource = StrategicReservedResource.Energy, Amount = world.Root.GetResource(ResourceType.Energy),
                ExpirationStage = StrategicReservationExpiry.EndOfTurn });
            Assert.That(AviationRebasePlanner.BuildPlan(snap, owner, world.Root, world.Context, Array.Empty<ReconObjective>()), Is.Null);
            StrategicResourceReservationLedger.ClearAll();
            carrier.HitPointsCurrent = 0;
            Assert.That(AviationRebasePlanner.BuildPlan(snap, owner, world.Root, world.Context, Array.Empty<ReconObjective>()), Is.Null);
        }

        [TestCase(StrategicReservedResource.ActionPoints)]
        [TestCase(StrategicReservedResource.Energy)]
        [TestCase(StrategicReservedResource.Materials)]
        [TestCase(StrategicReservedResource.Tech)]
        [Category("UnityEngine")]
        public void NewReservationRejectsWholeMissileChainBeforePayment(StrategicReservedResource resource)
        {
            using var world = new EngineWorld(owner);
            var snap = Snapshot(Sighting(MixedArmy())); snap.Map = world.Map;
            var blueprint = Read(AttachmentContentTestData.NeutralPath, "neutral.tactic.tactical-missile");
            var source = new GenerationStep { CardDef = missile, BlueprintDef = blueprint };
            var card = ResearchProductionSystem.MintCard(missile);
            var hand = new AiHandData(null, owner.Faction, 0); hand.Hand.Add(card);
            var play = new NonCombatCardPlayer.NonCombatPlay { Card = card, Generation = source,
                Kind = NonCombatCardPlayer.PlayKind.Aviation,
                ApCost = ResearchProductionSystem.AttemptApCost(blueprint) + missile.activationApCost,
                ResCost = blueprint.resourceCost };
            StrategicResourceReservationLedger.BeginTurn(owner, 10);
            StrategicResourceReservationLedger.Upsert(owner, 10, new StrategicResourceReservation {
                Owner = "other-task", Reason = StrategicReservationReason.StrategicReactionPass,
                Resource = resource, Amount = resource == StrategicReservedResource.ActionPoints ? 14 : 50,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn });
            Assert.That(GenerationSource.FitsReservedAffordability(world.Root, owner, world.Context, blueprint),
                Is.EqualTo(resource == StrategicReservedResource.ActionPoints),
                "AP=6 permits only the attempt; the complete chain still needs one more AP");
            var result = NonCombatPlayExecutor.Execute(play, snap, owner, world.Root, hand, world.Context);
            Assert.That(result.GenerationAttempted, Is.False); Assert.That(result.StateChanged, Is.False);
            Assert.That(world.Root.ActionPoints, Is.EqualTo(20));
            foreach (ResourceType type in ResourceBundle.All) Assert.That(world.Root.GetResource(type), Is.EqualTo(50));
            Assert.That(hand.Hand.Single(), Is.SameAs(card));
            Assert.That(StrategicResourceReservationLedger.Active(owner, 10, resource),
                Is.EqualTo(resource == StrategicReservedResource.ActionPoints ? 14 : 50));
        }

        [Test, Category("UnityEngine")]
        public void ReservationPlacedByMintEventKeepsThePaidMissileInHand()
        {
            using var world = new EngineWorld(owner);
            var snap = Snapshot(Sighting(MixedArmy())); snap.Map = world.Map;
            var field = world.Airfield(owner, 0);
            var factory = new FacilityData(); factory.Abilities.Add(UnitAbilities.Production);
            BuildingRegistry.FindAt(default).FacilitySlots[0] = factory;
            var hero = AttachmentSlotTests.Body(Read("Assets/Cards/IronConcord/CardCatalog_IronConcord.asset", "concord.card.bolt-jax"));
            hero.Owner = owner;
            var operators = new ArmyData { Owner = owner }; operators.Members.Add(hero); ArmyRegistry.Register(operators);
            var blueprint = Read(AttachmentContentTestData.NeutralPath, "neutral.tactic.tactical-missile");
            // Make only the roll deterministic. The real blueprint's attempt and output prices remain.
            blueprint.fate = 0;
            var cards = UnityEngine.ScriptableObject.CreateInstance<FactionCardCatalog>();
            var catalog = UnityEngine.ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            try
            {
                cards.cards.Add(blueprint); cards.cards.Add(missile);
                catalog.cardCatalogs.Add(cards);
                catalog.productionCards.Add(new ResearchProductionEntry { cardKey = blueprint.authoredKey,
                    resultCardKey = missile.authoredKey });
                world.Context.ResearchProductionCatalog = catalog;
                var source = new GenerationStep { CardDef = missile, BlueprintDef = blueprint,
                    Hero = hero, Mode = ResearchProductionMode.Production, SuccessChance = 1f };
                var hand = new AiHandData(null, owner.Faction, 0);
                StrategicResourceReservationLedger.BeginTurn(owner, 10);
                hand.HandChanged += () =>
                {
                    if (hand.Hand.Count == 1)
                        StrategicResourceReservationLedger.Upsert(owner, 10, new StrategicResourceReservation {
                            Owner = "mint-event-operation", Reason = StrategicReservationReason.StrategicReactionPass,
                            Resource = StrategicReservedResource.ActionPoints, Amount = world.Root.ActionPoints,
                            ExpirationStage = StrategicReservationExpiry.EndOfTurn });
                };
                var play = new NonCombatCardPlayer.NonCombatPlay { Card = ResearchProductionSystem.MintCard(missile),
                    Generation = source, Kind = NonCombatCardPlayer.PlayKind.Aviation,
                    ApCost = ResearchProductionSystem.AttemptApCost(blueprint) + missile.activationApCost,
                    ResCost = blueprint.resourceCost };
                var result = NonCombatPlayExecutor.Execute(play, snap, owner, world.Root, hand, world.Context);
                Assert.That(result.GenerationAttempted, Is.True); Assert.That(result.Generated, Is.True);
                Assert.That(result.StateChanged, Is.True); Assert.That(result.Played, Is.False);
                Assert.That(result.ApSpent, Is.EqualTo(ResearchProductionSystem.AttemptApCost(blueprint)));
                Assert.That(world.Root.ActionPoints, Is.EqualTo(14));
                Assert.That(world.Root.GetResource(ResourceType.Energy), Is.EqualTo(45));
                Assert.That(world.Root.GetResource(ResourceType.Materials), Is.EqualTo(44));
                Assert.That(world.Root.GetResource(ResourceType.Tech), Is.EqualTo(45));
                Assert.That(hand.Hand.Single().Definition, Is.SameAs(missile));
                Assert.That(hand.Hand.Single().ResearchProductionCreated, Is.True);
                Assert.That(field.Members, Is.Empty);
                Assert.That(StrategicResourceReservationLedger.Active(owner, 10,
                    StrategicReservedResource.ActionPoints), Is.EqualTo(14));
            }
            finally { UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(cards); }
        }
    }
}
#endif
