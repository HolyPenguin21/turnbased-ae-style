#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Game.EditorTests
{
    public sealed class DisruptionTests
    {
        private readonly HexCoord _hex = new HexCoord(2, -1);
        private PlayerSetupData _attackerOwner;
        private PlayerSetupData _defenderOwner;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); AiMapMemory.Clear();
            _attackerOwner = new PlayerSetupData { Nickname = "Attacker" };
            _defenderOwner = new PlayerSetupData { Nickname = "Defender" };
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); AiMapMemory.Clear();
            WorthIt.EndEstimateCacheScope();
        }

        private UnitData Body(PlayerSetupData owner, bool disruption = false)
        {
            var body = new UnitData { Owner = owner, Attack = 4, Defense = 2,
                HitPointsCurrent = 5, HitPointsMax = 5, Range = 2 };
            if (disruption) body.Abilities.Add(UnitAbilities.Disruption);
            return body;
        }

        private ArmyData Army(PlayerSetupData owner, bool disruption = false)
        {
            var army = new ArmyData { Owner = owner, Hex = _hex };
            army.Members.Add(Body(owner, disruption));
            return army;
        }

        private BuildingData Fort(int defense = 3, bool citadel = false)
        {
            var building = new BuildingData { Hex = _hex, Owner = _defenderOwner,
                IsBase = true, IsStartingCitadel = citadel, Defense = defense };
            BuildingRegistry.Register(_hex, building);
            return building;
        }

        private BattleEngine Engine(ArmyData attacker, ArmyData defender, BattleGrid grid = null) =>
            new BattleEngine(new BattleState(_hex, attacker, defender, grid ?? new BattleGrid(), 71),
                AbilityMagnitudes.Default);

        [TestCase(false)]
        [TestCase(true)]
        public void IncomingUnregisteredCarrier_SuppressesBaseAndCitadel(bool citadel)
        {
            Fort(citadel: citadel);
            var attacker = Army(_attackerOwner, true);
            var defender = Army(_defenderOwner);
            attacker.Hex = new HexCoord(1, -1);
            var engine = Engine(attacker, defender);
            Assert.That(ArmyRegistry.IsRegistered(attacker), Is.False);
            Assert.That(engine.GetDefenderHexDefenseBonus(null), Is.Zero);
            Assert.That(engine.GetDefenseBreakdown(defender.Members[0], null).ConstructionBonus, Is.Zero);
        }

        [Test]
        public void ExternalHiddenCarrier_DoesNotRevealOrJoinBattle_AndDoesNotStack()
        {
            Fort();
            var source = Army(_attackerOwner, true);
            source.Members[0].IsHidden = true;
            ArmyRegistry.Register(source);
            var incoming = Army(_attackerOwner, true);
            var defender = Army(_defenderOwner);
            var engine = Engine(incoming, defender);
            Assert.That(engine.GetDefenderHexDefenseBonus(null), Is.Zero);
            Assert.That(source.Members[0].IsHidden, Is.True);
            Assert.That(engine.State.Attacker, Is.SameAs(incoming));
        }

        [TestCase("dead")]
        [TestCase("prisoner")]
        [TestCase("prison")]
        [TestCase("airUnit")]
        [TestCase("airArmy")]
        [TestCase("airfield")]
        [TestCase("friendly")]
        public void IneligibleSource_LeavesPositiveBuildingDefense(string kind)
        {
            Fort();
            var source = Army(kind == "friendly" ? _defenderOwner : _attackerOwner, true);
            if (kind == "dead") source.Members[0].HitPointsCurrent = 0;
            if (kind == "prisoner") source.Members[0].IsPrisoner = true;
            if (kind == "prison") source.IsPrison = true;
            if (kind == "airUnit") source.Members[0].IsAviation = true;
            if (kind == "airArmy") source.IsAirArmy = true;
            if (kind == "airfield") source.IsAirfield = true;
            ArmyRegistry.Register(source);
            Assert.That(Engine(Army(_attackerOwner), Army(_defenderOwner))
                .GetDefenderHexDefenseBonus(null), Is.EqualTo(3));
        }

        [Test]
        public void NegativeBuildingDefense_IsNotRemoved()
        {
            Fort(-2);
            Assert.That(Engine(Army(_attackerOwner, true), Army(_defenderOwner))
                .GetDefenderHexDefenseBonus(null), Is.EqualTo(-2));
        }

        [Test]
        public void CarrierDeathRemovalAndRetreat_DoNotRestoreCurrentEncounter_NextEncounterRechecks()
        {
            Fort();
            var source = Army(_attackerOwner, true);
            ArmyRegistry.Register(source);
            var incoming = Army(_attackerOwner);
            var defender = Army(_defenderOwner);
            var engine = Engine(incoming, defender);
            source.Members[0].HitPointsCurrent = 0;
            ArmyRegistry.Unregister(source);
            Assert.That(engine.GetDefenderHexDefenseBonus(null), Is.Zero);
            Assert.That(Engine(incoming, defender).GetDefenderHexDefenseBonus(null), Is.EqualTo(3));
            source.Members[0].HitPointsCurrent = 5;
            ArmyRegistry.Register(source);
            var second = Engine(incoming, defender);
            ArmyRegistry.MoveArmy(source, new HexCoord(3, -1));
            Assert.That(second.GetDefenderHexDefenseBonus(null), Is.Zero);
            Assert.That(Engine(incoming, defender).GetDefenderHexDefenseBonus(null), Is.EqualTo(3));
        }

        [Test]
        public void TerrainFormationAndOwnDefense_RemainInBreakdown()
        {
            Fort();
            var attacker = Army(_attackerOwner, true);
            var defender = Army(_defenderOwner);
            var guard = Body(_defenderOwner);
            defender.Members.Add(guard);
            var grid = new BattleGrid();
            grid.Set(BattleGrid.DefenderBackRow, 1, defender.Members[0]);
            grid.Set(BattleGrid.DefenderFrontRow, 1, guard);
            var go = new GameObject("Disruption terrain regression");
            try
            {
                var map = go.AddComponent<HexMap>();
                map.SetData(3, 1f, new Dictionary<HexCoord, TerrainTypeEntry>
                    { [_hex] = new TerrainTypeEntry { defenseModifier = 2 } });
                var engine = new BattleEngine(new BattleState(_hex, attacker, defender, grid, 1), AbilityMagnitudes.Default, map: map);
                BattleDefenseBreakdown bonus = engine.GetDefenseBreakdown(defender.Members[0], map);
                Assert.That(bonus.TerrainBonus, Is.EqualTo(2));
                Assert.That(bonus.FormationBonus, Is.EqualTo(BattleRules.GuardedDefenseBonus));
                Assert.That(bonus.ConstructionBonus, Is.Zero);
                Assert.That(engine.GetDefenderHexDefenseBonus(map), Is.EqualTo(2));
                Assert.That(defender.Members[0].Defense, Is.EqualTo(2));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        private static WorthIt.DefenderProfile Profile(bool disruption = false, float hp = 5) =>
            new WorthIt.DefenderProfile(2, false, attack: 4, hitPoints: hp,
                abilities: disruption ? new[] { UnitAbilities.Disruption } : Array.Empty<string>());

        [Test]
        public void EstimateAndCoverage_UseSameIncomingResolvedBonus_KeepTerrain()
        {
            var attackers = new[] { Profile(true) };
            var defenders = new[] { Profile() };
            var army = new WorthIt.DefendingArmy(defenders, default, 32, buildingDefenseBonus: 30);
            Assert.That(army.DefenseBonus(0, attackers), Is.EqualTo(2));
            Assert.That(army.DefenseBonus(0, new[] { Profile(true, 0) }), Is.EqualTo(32));
            Assert.That(WorthIt.CanDamageAll(new[] { Profile() }, new[] { army }), Is.False);
            Assert.That(WorthIt.CanDamageAll(attackers, new[] { army }), Is.True);
            Assert.That(WorthIt.CanDamageAll(attackers, new[] { army }),
                Is.EqualTo(WorthIt.CanDamageAll(attackers,
                    new[] { new WorthIt.DefendingArmy(defenders, default, 2) })));
            Assert.That(WorthIt.EstimateSequential(attackers, default, new[] { army }, 0).WinChance,
                Is.EqualTo(WorthIt.Estimate(attackers, defenders, 2).WinChance));
        }

        [Test]
        public void LiveAircraftAndPrisonerProfiles_CannotActivateIncomingDisruption()
        {
            var unit = Body(_attackerOwner, true);
            unit.IsAviation = true;
            Assert.That(DisruptionRules.HasCarrier(new[] { WorthIt.FromLiveUnit(unit) }), Is.False);
            unit.IsAviation = false; unit.IsPrisoner = true;
            Assert.That(DisruptionRules.HasCarrier(new[] { WorthIt.FromLiveUnit(unit) }), Is.False);
        }

        [Test]
        public void GroundHeroSupportProfile_ActivatesDisruptionForBodyOnlyEstimate()
        {
            var hero = Body(_attackerOwner, true);
            hero.IsHero = true;
            var commander = WorthIt.SideCommander.Of(hero);
            var defender = new WorthIt.DefendingArmy(new[] { Profile() }, default, 3,
                buildingDefenseBonus: 3);
            Assert.That(defender.DefenseBonus(0, new[] { Profile() }, commander), Is.Zero);
            Assert.That(WorthIt.CanDamageAll(new[] { Profile() }, new[] { defender },
                attackerCommander: commander), Is.True);
            Assert.That(StrategicEffectRegistry.HasArmySupportEffect(hero.Abilities, 0), Is.True);
        }

        [Test]
        public void Memory_UsesOwnHiddenCarrierAndRememberedEnemyButNotHiddenLiveForeignRegistry()
        {
            Fort();
            AiMapMemory.RememberBuildingForTest(_attackerOwner, new AiMapMemory.KnownBuilding(
                _hex, _defenderOwner, false, null, isBase: true, defense: 3));
            var hiddenThirdParty = Army(new PlayerSetupData { Nickname = "Third" }, true);
            hiddenThirdParty.Members[0].IsHidden = true;
            ArmyRegistry.Register(hiddenThirdParty);
            Assert.That(AiMapMemory.KnownBuildingDefenseBonusFor(_attackerOwner, null, _hex,
                _defenderOwner), Is.EqualTo(3), "hidden foreign live state must not change knowledge");
            AiMapMemory.RememberForTest(_attackerOwner, new AiMapMemory.KnownEnemySighting(
                _hex, hiddenThirdParty.Owner, "Observed source", 1, 2, 4,
                new[] { Profile(true) }, armyId: hiddenThirdParty.Id));
            Assert.That(AiMapMemory.KnownBuildingDefenseBonusFor(_attackerOwner, null, _hex,
                _defenderOwner), Is.Zero);
            AiMapMemory.ForgetSightingsForTest(_attackerOwner);
            var own = Army(_attackerOwner, true); own.Members[0].IsHidden = true;
            ArmyRegistry.Register(own);
            Assert.That(AiMapMemory.KnownBuildingDefenseBonusFor(_attackerOwner, null, _hex,
                _defenderOwner), Is.Zero);
        }

        [Test]
        public void SequentialCache_SeesDifferentBuildingComponentsAtEqualTotalDefense()
        {
            WorthIt.BeginEstimateCacheScope();
            var attackers = new[] { Profile(true) };
            var defenders = new[] { Profile() };
            var fort = new WorthIt.DefendingArmy(defenders, default, 4, buildingDefenseBonus: 3);
            var terrain = new WorthIt.DefendingArmy(defenders, default, 4);
            WorthIt.EstimateSequential(attackers, default, new[] { fort, fort }, 0);
            int misses = WorthIt.CurrentEstimateCacheStats.Misses;
            WorthIt.EstimateSequential(attackers, default, new[] { terrain, terrain }, 0);
            Assert.That(WorthIt.CurrentEstimateCacheStats.Misses, Is.GreaterThan(misses));
        }

        [Test]
        public void CitadelRetreatRestriction_RemainsAnUnconditionalGuard()
        {
            var assessment = BattleAi.AssessRetreat(null, null, null, true, AbilityMagnitudes.Default);
            Assert.That(assessment.IsCitadelDefense, Is.True);
            Assert.That(assessment.ShouldRetreat, Is.False);
        }
    }
}
#endif
