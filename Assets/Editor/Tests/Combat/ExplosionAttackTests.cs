#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ExplosionAttackTests
    {
        private static readonly HexCoord Hex = new HexCoord(2, 2);
        private static UnitData Body(string name = "body", int hp = 20, params string[] abilities)
        {
            var card = new CardDefinition { cardType = CardType.Unit, displayName = name,
                attack = 1, defenseRating = 0, hitPoints = hp, range = 2, initiative = 2,
                grantedAbilities = abilities.ToList() };
            return AttachmentSlotTests.Body(card);
        }
        private static ArmyData Army(PlayerSetupData owner, params UnitData[] members)
        {
            var a = new ArmyData { Hex = Hex, Owner = owner };
            foreach (var u in members) { u.Owner = owner; a.Members.Add(u); }
            return a;
        }
        private static CardDefinition Bomb(string ability = UnitAbilities.Explosion3)
        {
            var card = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Attack, 0);
            card.grantedAbilities.Add(UnitAbilities.SingleUse);
            card.equipment.addAbilities.Add(ability);
            return card;
        }
        private static BattleChallengeRollResult Roll(int hits) => new BattleChallengeRollResult(
            Enumerable.Repeat(true, hits).ToArray(), Array.Empty<bool>(), 0, 0);
        private static void ResolveAll(ExplosionAttack attack, int damage = 0, BattleEngine battle = null)
        {
            foreach (var target in attack.Targets)
                if (target.IsAlive) attack.Resolve(target, target.Combatant(), Roll(damage),
                    AbilityMagnitudes.Default, battle: battle);
        }
        [TearDown] public void Cleanup() { ArmyRegistry.Clear(); BuildingRegistry.Clear(); }

        [TestCase(UnitAbilities.Explosion3, 3)]
        [TestCase(UnitAbilities.Explosion5, 5)]
        [TestCase(UnitAbilities.Explosion10, 10)]
        public void SelectsDistinctUniformOccupantsAcrossAllArmies(string skill, int limit)
        {
            var owner = new PlayerSetupData(); var enemy = new PlayerSetupData();
            var actor = Body("bomber", 50, skill);
            var own = Army(owner, actor, Body("ally"));
            var other = Army(enemy, Enumerable.Range(0, 12).Select(i => Body("enemy" + i)).ToArray());
            var attack = new ExplosionAttack(actor, Hex, new Random(44), own, other, other);
            Assert.That(attack.Targets.Count, Is.EqualTo(limit));
            Assert.That(attack.Targets.Select(t => t.Unit).Distinct().Count(), Is.EqualTo(limit));
            Assert.That(attack.Actor.Attack, Is.EqualTo(10));
            Assert.That(actor.Attack, Is.EqualTo(1), "The printed/runtime Attack is never overwritten.");
            Assert.That(ExplosionAttack.TargetLimit(new[] { skill, UnitAbilities.Explosion10, UnitAbilities.Explosion3 }), Is.EqualTo(10));
            var chosenOwners = new HashSet<PlayerSetupData>();
            for (int seed = 0; seed < 30; seed++)
                foreach (var t in new ExplosionAttack(actor, Hex, new Random(seed), own, other).Targets)
                    chosenOwners.Add(t.Army.Owner);
            Assert.That(chosenOwners, Is.EquivalentTo(new[] { owner, enemy }));
        }

        [Test]
        public void FewerOccupantsMeansOneChallengeEach_AndNoPrimaryBonusAttack()
        {
            var actor = Body("bomber", 50); var gear = Bomb(UnitAbilities.Explosion10);
            EquipmentSystem.ApplyAttachments(actor, gear, null);
            var army = Army(new PlayerSetupData(), actor, Body("ally"), Body("other"));
            var attack = new ExplosionAttack(actor, Hex, new Random(1), army);
            Assert.That(attack.Targets.Count, Is.EqualTo(3));
            foreach (var t in attack.Targets)
            {
                attack.Resolve(t, t.Unit, Roll(10), AbilityMagnitudes.Default);
                Assert.That(actor.Equipment, Is.SameAs(gear), "All targets use the armed snapshot.");
            }
            Assert.That(army.Members.Select(u => u.HitPointsCurrent), Is.EquivalentTo(new[] { 40, 10, 10 }));
            attack.Complete(); attack.Complete();
            Assert.That(actor.Equipment, Is.Null);
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(40));
            Assert.That(actor.HasAbility(UnitAbilities.Explosion10), Is.False);
            Assert.That(gear.equipment.addAbilities, Does.Contain(UnitAbilities.Explosion10));
        }

        [Test]
        public void InnateRocketSurvivesMissedChallengesAndIsRemovedOnlyAtSeriesEnd()
        {
            var actor = Body("rocket", 5, UnitAbilities.SingleUse, UnitAbilities.Explosion3);
            var owner = new PlayerSetupData(); var own = Army(owner, actor, Body("ally"));
            var attack = new ExplosionAttack(actor, Hex, new Random(1), own);
            Assert.That(attack.Targets.Select(t => t.Unit), Has.No.Member(actor));
            Assert.That(attack.Targets.Count, Is.EqualTo(1));
            ResolveAll(attack);
            Assert.That(own.Members, Does.Contain(actor));
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(5));
            attack.Complete();
            Assert.That(actor.HitPointsCurrent, Is.Zero);
            Assert.That(own.Members, Has.No.Member(actor));
            Assert.That(WorthIt.FromLiveUnit(actor).SingleUseUnit, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BombMarkerInEitherSelectorSpendsOnlyEquipmentAndPreservesInnateExplosion(bool payloadMarker)
        {
            var actor = Body("host", 20, UnitAbilities.Explosion3);
            var bomb = Bomb(UnitAbilities.Explosion10);
            if (payloadMarker)
            {
                bomb.grantedAbilities.Clear();
                bomb.equipment.addAbilities.Add(UnitAbilities.SingleUse);
            }
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.Defense, 2);
            mutator.equipment.addAbilities.Add(UnitAbilities.SingleUse);
            EquipmentSystem.ApplyAttachments(actor, bomb, mutator);
            var army = Army(new PlayerSetupData(), actor, Body("ally"));
            Assert.That(ExplosionAttack.IsSingleUseUnit(actor), Is.False);
            var attack = new ExplosionAttack(actor, Hex, new Random(8), army);
            ResolveAll(attack);
            Assert.That(actor.Equipment, Is.SameAs(bomb));
            attack.Complete(); attack.Complete();
            Assert.That(actor.Equipment, Is.Null);
            Assert.That(actor.Mutator, Is.SameAs(mutator));
            Assert.That(actor.Defense, Is.EqualTo(2));
            Assert.That(army.Members, Does.Contain(actor));
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(20));
            Assert.That(ExplosionAttack.TargetLimit(actor.Abilities), Is.EqualTo(3));
            Assert.That(ExplosionAttack.IsSingleUseUnit(actor), Is.False);
            Assert.That(WorthIt.FromLiveUnit(actor).SingleUseUnit, Is.False);
        }

        [Test]
        public void InnateMissileWithSingleUseBombCompletesOneSeriesBeforeBothAreSpent()
        {
            var actor = Body("rocket", 20, UnitAbilities.SingleUse, UnitAbilities.Explosion3);
            var bomb = Bomb(UnitAbilities.Explosion10);
            EquipmentSystem.ApplyAttachments(actor, bomb, null);
            var army = Army(new PlayerSetupData(), actor, Body("ally"), Body("other"));
            var attack = new ExplosionAttack(actor, Hex, new Random(1), army);
            Assert.That(attack.Targets.Count, Is.EqualTo(2));
            foreach (var target in attack.Targets)
            {
                attack.Resolve(target, target.Combatant(), Roll(0), AbilityMagnitudes.Default);
                Assert.That(army.Members, Does.Contain(actor));
                Assert.That(actor.Equipment, Is.SameAs(bomb));
            }
            attack.Complete(); attack.Complete();
            Assert.That(actor.Equipment, Is.Null);
            Assert.That(actor.HitPointsCurrent, Is.Zero);
            Assert.That(army.Members, Has.No.Member(actor));
            Assert.That(bomb.grantedAbilities, Does.Contain(UnitAbilities.SingleUse));
        }

        [TestCase(UnitAbilities.Explosion3, 3)]
        [TestCase(UnitAbilities.Explosion5, 5)]
        [TestCase(UnitAbilities.Explosion10, 10)]
        public void MissileNeverTargetsItselfOrUsesATargetSlot_OtherMissilesRemainEligible(string skill, int limit)
        {
            var actor = Body("rocket", 5, skill, UnitAbilities.SingleUse); actor.IsAviation = true;
            var otherMissile = Body("other rocket", 20, UnitAbilities.SingleUse, UnitAbilities.Explosion3);
            var own = Army(new PlayerSetupData(), actor, otherMissile);
            var enemy = Army(new PlayerSetupData(), Enumerable.Range(0, 12).Select(i => Body("enemy" + i)).ToArray());
            bool sawOtherMissile = false;
            for (int seed = 0; seed < 40; seed++)
            {
                var attack = new ExplosionAttack(actor, Hex, new Random(seed), own, enemy);
                Assert.That(attack.Targets.Count, Is.EqualTo(limit));
                Assert.That(attack.Targets.Select(t => t.Unit), Has.No.Member(actor));
                sawOtherMissile |= attack.Targets.Any(t => t.Unit == otherMissile);
            }
            Assert.That(sawOtherMissile, Is.True, "Only the launching missile is exempt, not other allies.");
        }

        [Test]
        public void IsolatedMissileDoesNotStartAChallengeOrExpendItself()
        {
            var actor = Body("rocket", 5, UnitAbilities.SingleUse, UnitAbilities.Explosion3);
            var own = Army(new PlayerSetupData(), actor);
            var attack = new ExplosionAttack(actor, Hex, new Random(1), own);
            Assert.That(attack.Targets, Is.Empty);
            attack.Complete();
            Assert.That(own.Members, Does.Contain(actor));
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(5));
        }

        [Test]
        public void MissilePreviewExcludesLauncherFromSelectionProbabilityButChargesSacrificeOnce()
        {
            var actor = Body("rocket", 1, UnitAbilities.SingleUse, UnitAbilities.Explosion3);
            Army(new PlayerSetupData(), actor);
            var enemy = new PlayerSetupData();
            var targets = Enumerable.Range(0, 4).Select(i => { var u = Body("enemy" + i); u.Owner = enemy; return u; }).ToArray();
            float withoutLauncher = ExplosionAttack.ExpectedNetValue(actor, targets);
            Assert.That(withoutLauncher, Is.GreaterThan(0));
            Assert.That(ExplosionAttack.ExpectedNetValue(actor, targets.Concat(new[] { actor })),
                Is.EqualTo(withoutLauncher).Within(0.00001f));
        }

        [Test]
        public void AirForecastUsesAllThreeMissileTargetsWithoutASelfChallenge()
        {
            var actor = Body("rocket", 1, UnitAbilities.Explosion3, UnitAbilities.SingleUse, UnitAbilities.CriticalDamage);
            Army(new PlayerSetupData(), actor);
            var targets = Enumerable.Repeat(new WorthIt.DefenderProfile(0, false, attack: 1, hitPoints: 100), 3).ToArray();
            var forecast = AviationCombatEstimator.EstimateAirStrike(new[] { actor }, targets, AirStrikePolicy.Standard);
            Assert.That(forecast.ExpectedDamage, Is.GreaterThan(25), "All three victims get a 10-die CriticalDamage challenge.");
            Assert.That(forecast.ExpectedFriendlyDamage, Is.EqualTo(1), "Only the single post-series sacrifice remains.");
        }

        [Test]
        public void ExplosionWithoutSingleUseRemainsReusable()
        {
            var actor = Body("host", 20);
            var gear = Bomb(); gear.grantedAbilities.Clear();
            EquipmentSystem.ApplyAttachments(actor, gear, null);
            var army = Army(new PlayerSetupData(), actor);
            for (int i = 0; i < 2; i++)
            {
                var attack = new ExplosionAttack(actor, Hex, new Random(i), army);
                ResolveAll(attack); attack.Complete();
                Assert.That(actor.Equipment, Is.SameAs(gear));
                Assert.That(army.Members, Does.Contain(actor));
            }
        }

        [Test]
        public void LaunchedSeriesContinuesAfterSelfDestructionAndRetainsItsModifiers()
        {
            var actor = Body("bomber", 1); var gear = Bomb(UnitAbilities.Explosion10);
            gear.equipment.addAbilities.Add(UnitAbilities.CriticalDamage);
            EquipmentSystem.ApplyAttachments(actor, gear, null);
            var own = Army(new PlayerSetupData(), actor, Body("ally", 30), Body("other", 30));
            var attack = new ExplosionAttack(actor, Hex, new Random(2), own);
            var self = attack.Targets.Single(t => t.Unit == actor);
            attack.Resolve(self, actor, Roll(2), AbilityMagnitudes.Default);
            Assert.That(own.Members, Has.No.Member(actor));
            foreach (var target in attack.Targets.Where(t => t != self))
                Assert.That(attack.Resolve(target, target.Unit, Roll(2), AbilityMagnitudes.Default).Damage, Is.EqualTo(4));
            Assert.That(attack.ResolvedCount, Is.EqualTo(3));
            attack.Complete();
            Assert.That(actor.Equipment, Is.Null);
        }

        [Test]
        public void NoChallengeDoesNotConsume_AndRepeatedTargetIsRejected()
        {
            var actor = Body(); var gear = Bomb(); EquipmentSystem.ApplyAttachments(actor, gear, null);
            var army = Army(new PlayerSetupData(), actor);
            var empty = new ExplosionAttack(actor, new HexCoord(8, 8), new Random(1));
            empty.Complete();
            Assert.That(actor.Equipment, Is.SameAs(gear));
            var attack = new ExplosionAttack(actor, Hex, new Random(1), army);
            attack.Resolve(attack.Targets[0], actor, Roll(0), AbilityMagnitudes.Default);
            Assert.Throws<InvalidOperationException>(() => attack.Resolve(attack.Targets[0], actor, Roll(0), AbilityMagnitudes.Default));
            attack.Complete(); Assert.That(actor.Equipment, Is.Null);
        }

        [Test]
        public void GroundExplosionBypassesInterceptionCollateralAndCounters_AndUpdatesTurnOrder()
        {
            var actor = Body("bomber", 50, UnitAbilities.Explosion10, UnitAbilities.Splash);
            var target = Body("guard", 1, UnitAbilities.Bodyguard, UnitAbilities.Counterattack);
            var friend = Body("ally", 1);
            var own = Army(new PlayerSetupData(), actor, friend);
            var enemy = Army(new PlayerSetupData(), target);
            var grid = new BattleGrid(); grid.Set(3, 1, actor); grid.Set(3, 2, friend); grid.Set(2, 1, target);
            var state = new BattleState(Hex, own, enemy, grid, 1)
            { TurnOrder = new List<UnitData> { actor, target, friend } };
            var battle = new BattleEngine(state, AbilityMagnitudes.Default);
            var attack = new ExplosionAttack(actor, Hex, new Random(1), own, enemy);
            foreach (var t in attack.Targets)
            {
                var result = attack.Resolve(t, t.Unit, Roll(1), AbilityMagnitudes.Default, battle: battle);
                Assert.That(result.Counterattacker, Is.Null);
                Assert.That(result.InterceptedBy, Is.Null);
                Assert.That(result.SecondaryHits, Is.Empty);
            }
            Assert.That(state.TurnOrder, Is.EqualTo(new[] { actor }));
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(49));
        }

        [Test]
        public void PreviewDetectsFriendlyFireAndDoesNotMutateSourceOrSpendLiveIdentities()
        {
            var actor = Body("bomber", 50, UnitAbilities.Explosion10);
            var own = new PlayerSetupData(); var enemy = new PlayerSetupData();
            Army(own, actor); var victim = Body("enemy", 10); victim.Owner = enemy;
            float enemies = ExplosionAttack.ExpectedNetValue(actor, new[] { victim });
            float allies = ExplosionAttack.ExpectedNetValue(actor, new[] { actor, victim });
            Assert.That(allies, Is.LessThan(enemies));
            int id = new UnitData().RuntimeId;
            ExplosionAttack.ExpectedNetValue(actor, new[] { victim });
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(id + 1));
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(50));
        }

        [Test]
        public void AirForecastPricesSeveralTargetsFriendlyFireAndOneUseAcrossPasses()
        {
            var actor = Body("rocket", 1, UnitAbilities.Explosion10, UnitAbilities.SingleUse);
            var own = Army(new PlayerSetupData(), actor); var enemy = new PlayerSetupData();
            var targets = Enumerable.Range(0, 4).Select(i => { var u = Body("target", 20); u.Owner = enemy; return WorthIt.FromLiveUnit(u); }).ToArray();
            var one = AviationCombatEstimator.EstimateAirStrike(own.Members, targets, AirStrikePolicy.Standard, 1);
            var three = AviationCombatEstimator.EstimateAirStrike(own.Members, targets, AirStrikePolicy.Standard, 3);
            Assert.That(one.ExpectedDamage, Is.GreaterThan(10));
            Assert.That(three.ExpectedDamage, Is.LessThan(one.ExpectedDamage * 1.5f), "A missile cannot attack on later passes.");
            Assert.That(one.ExpectedFriendlyDamage, Is.EqualTo(1));
            var friendly = new AviationCombatEstimator.ExplosionCollateral(targets[0], true);
            var risk = AviationCombatEstimator.EstimateAirStrike(own.Members, targets, AirStrikePolicy.Standard, 1, collateral: new[] { friendly });
            Assert.That(risk.ExpectedFriendlyDamage, Is.GreaterThan(one.ExpectedFriendlyDamage));
            Assert.That(actor.HitPointsCurrent, Is.EqualTo(1));
            Assert.That(own.Members, Does.Contain(actor));
        }

        [Test]
        public void SingleUseIdentityAndCollateralParticipateInForecastCacheKeys()
        {
            var p = new WorthIt.DefenderProfile(0, false, attack: 1, hitPoints: 50,
                abilities: new[] { UnitAbilities.Explosion3, UnitAbilities.SingleUse });
            var missile = new WorthIt.DefenderProfile(0, false, attack: 1, hitPoints: 50,
                abilities: p.Abilities, singleUseUnit: true);
            Assert.That(AviationCombatEstimator.BuildKey(new[] { p }, new[] { p }, AirStrikePolicy.Standard, 1, 0, AbilityMagnitudes.Default),
                Is.Not.EqualTo(AviationCombatEstimator.BuildKey(new[] { missile }, new[] { p }, AirStrikePolicy.Standard, 1, 0, AbilityMagnitudes.Default)));
            Assert.That(WorthIt.AfterAttack(missile).HitPoints, Is.Zero);
            Assert.That(WorthIt.AfterAttack(p).HitPoints, Is.EqualTo(50));
        }

        [Test]
        public void GroundKernelResolvesAllVictimsBeforeExpendableActorDisappears()
        {
            var rocket = new WorthIt.DefenderProfile(0, false, attack: 0, hitPoints: 1, initiative: 20,
                abilities: new[] { UnitAbilities.Explosion10, UnitAbilities.SingleUse }, singleUseUnit: true);
            var victims = Enumerable.Repeat(new WorthIt.DefenderProfile(0, false,
                attack: 1, hitPoints: 4, initiative: 0), 6).ToArray();
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var convert = typeof(WorthIt).GetMethod("ToBattleUnits", flags);
            object own = convert.Invoke(null, new object[] { new[] { rocket }, 0f, 0, default(WorthIt.SideCommander) });
            object enemy = convert.Invoke(null, new object[] { victims, 0f, 0, default(WorthIt.SideCommander) });
            typeof(WorthIt).GetMethod("SimulateOneBattle", flags).Invoke(null,
                new object[] { own, enemy, new Random(42), 0, 0, (AbilityMagnitudes?)AbilityMagnitudes.Default, null });
            float Hp(object u) => (float)u.GetType().GetField("Hp").GetValue(u);
            Assert.That(Hp(((IList)own)[0]), Is.Zero);
            Assert.That(((IList)enemy).Cast<object>().Sum(Hp), Is.LessThan(18),
                "The complete area attack ran; the actor was not spent after its first victim.");
        }

        [Test]
        public void AircraftEquipmentValuationPricesAreaDamageWithoutAttackStatBonus()
        {
            var victim = new WorthIt.DefenderProfile(0, false, attack: 2, hitPoints: 4);
            var ctx = new EfficiencyContext { Targets = Enumerable.Repeat(victim, 5).ToArray() };
            var stats = new EfficiencyStats(1, 0, 50, 2, 4, 3, 1, 0);
            var host = new CardDefinition { cardType = CardType.Unit, attack = 1, hitPoints = 50,
                defenseRating = 0, range = 2, moveMax = 4, initiative = 3, activationApCost = 1 };
            ctx.AfterConsumption = EquipmentSystem.Consumption(host, Bomb(UnitAbilities.Explosion10), null);
            float value = EquipmentEfficiency.AviationOffenseDelta(stats, Array.Empty<string>(), stats,
                new[] { UnitAbilities.Explosion10 }, ctx);
            Assert.That(value, Is.GreaterThan(0));
        }

        [Test]
        public void AiDoesNotDetonateAgainstItsOwnVulnerableStartingCitadel()
        {
            var actor = Body("bomber", 50, UnitAbilities.Explosion10);
            var owner = new PlayerSetupData(); Army(owner, actor);
            var citadel = new BuildingData { Owner = owner, IsStartingCitadel = true,
                StructurePointsCurrent = 5, StructurePointsMax = 30 };
            var targets = new[] { new ExplosionAttack.Target(citadel) };
            Assert.That(ExplosionAttack.ExpectedNetValue(actor, targets), Is.EqualTo(float.NegativeInfinity));
            var profile = new WorthIt.DefenderProfile(0, false, hitPoints: 5, isGroundCombatant: false);
            var collateral = new[] { new AviationCombatEstimator.ExplosionCollateral(profile, true, ownCitadel: true) };
            var enemy = new[] { new WorthIt.DefenderProfile(0, false, attack: 100, hitPoints: 20) };
            var forecast = AviationCombatEstimator.EstimateAirStrike(new[] { actor }, enemy,
                AirStrikePolicy.Standard, collateral: collateral);
            Assert.That(forecast.ExpectedDamage, Is.Zero);
            var ordinary = new[] { new AviationCombatEstimator.ExplosionCollateral(profile, true) };
            Assert.That(AviationCombatEstimator.EstimateAirStrike(new[] { actor }, enemy,
                AirStrikePolicy.Standard, collateral: ordinary).ExpectedDamage, Is.GreaterThan(0));
        }

        [Test]
        public void AirForecastAndLiveDecisionBothRejectAnUnprofitableMissileSacrifice()
        {
            var missile = Body("expensive rocket", 50, UnitAbilities.Explosion10, UnitAbilities.SingleUse);
            var target = Body("weak target", 1);
            Army(new PlayerSetupData(), missile); Army(new PlayerSetupData(), target);
            Assert.That(ExplosionAttack.ExpectedNetValue(missile, new[] { missile, target }), Is.LessThan(0));
            var forecast = AviationCombatEstimator.EstimateAirStrike(new[] { missile },
                new[] { WorthIt.FromLiveUnit(target) }, AirStrikePolicy.Standard, 3);
            Assert.That(forecast.ExpectedDamage, Is.Zero);
            Assert.That(forecast.ExpectedFriendlyDamage, Is.Zero, "The skipped missile must not be spent in the forecast.");
            Assert.That(missile.HitPointsCurrent, Is.EqualTo(50));
        }

        [Test]
        public void HandValuationDoesNotMistakeMutatorMarkerForDisposableHost()
        {
            var host = Body("host", 20).OriginatingCard;
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.Defense, 2);
            var hand = new CardData(host) { Mutator = mutator };
            var upgrade = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Attack, 3);
            var before = EquipmentUpgradeValuation.EquipmentDeltaParts(upgrade, hand);
            mutator.equipment.addAbilities.Add(UnitAbilities.SingleUse);
            var after = EquipmentUpgradeValuation.EquipmentDeltaParts(upgrade, hand);
            Assert.That(after.Combat, Is.EqualTo(before.Combat).Within(0.00001f));
            Assert.That(after.Tactical, Is.EqualTo(before.Tactical).Within(0.00001f));
            Assert.That(AiPower.ToDefenderProfile(host, null, mutator).SingleUseUnit, Is.False);
        }

        [Test]
        public void ObservedStructuresKeepTheirOwnDefenseAndCurrentFate()
        {
            var building = new BuildingData { Defense = 3, Fate = 4,
                StructurePointsCurrent = 12, StructurePointsMax = 20 };
            building.FacilitySlots[0] = new FacilityData { Defense = 2, Fate = 1,
                StructurePointsCurrent = 5, StructurePointsMax = 8 };
            var profiles = ExplosionAttack.StructureProfiles(building);
            Assert.That(profiles.Count, Is.EqualTo(2));
            Assert.That(profiles[0].Defense, Is.EqualTo(3));
            Assert.That(profiles[0].FateMax, Is.EqualTo(4));
            Assert.That(profiles[1].Defense, Is.EqualTo(2));
            Assert.That(profiles[1].FateMax, Is.EqualTo(1));
            building.Fate = 0; building.FacilitySlots[0].Fate = 0;
            Assert.That(profiles[0].FateMax, Is.EqualTo(4), "Observation profiles must stay frozen.");
        }

        [Test]
        public void ShadowCompletionPreservesSelfHitStatsAndFractionalWoundsWithoutTouchingLiveHost()
        {
            var live = Body("host", 10);
            var bomb = Bomb();
            bomb.equipment.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.HitPoints, amount = 4 });
            EquipmentSystem.ApplyAttachments(live, bomb, null);
            var shadow = live.CloneForCombat();
            var hp = new Dictionary<UnitData, float> { [shadow] = 6.5f };
            var attack = new Dictionary<UnitData, int> { [shadow] = 4 };
            var defense = new Dictionary<UnitData, int> { [shadow] = 2 };
            var grid = new BattleGrid(); grid.Set(3, 1, shadow);
            typeof(BattleAi).GetMethod("CompleteSimulatedAttack", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { grid, hp, shadow, attack, defense });
            Assert.That(hp[shadow], Is.EqualTo(2.5f));
            Assert.That(attack[shadow], Is.EqualTo(4));
            Assert.That(defense[shadow], Is.EqualTo(2));
            Assert.That(shadow.Equipment, Is.Null);
            Assert.That(live.Equipment, Is.SameAs(bomb));
            Assert.That(live.HitPointsCurrent, Is.EqualTo(14));
        }

        [Test, Category("UnityEngine")]
        public void StructuresAndFacilitiesAreSeparateTargets_DestructionIsPublishedAfterSeries()
        {
            var building = new BuildingData { Hex = Hex, Name = "base", StructurePointsCurrent = 1, StructurePointsMax = 1 };
            building.FacilitySlots[0] = new FacilityData { Name = "factory", StructurePointsCurrent = 1, StructurePointsMax = 1 };
            BuildingRegistry.Register(Hex, building);
            var actor = Body("bomber", 50, UnitAbilities.Explosion10);
            var own = Army(new PlayerSetupData(), actor);
            var attack = new ExplosionAttack(actor, Hex, new Random(3), own);
            Assert.That(attack.Targets.Count, Is.EqualTo(3));
            ResolveAll(attack, 1);
            Assert.That(BuildingRegistry.FindAt(Hex), Is.SameAs(building));
            attack.Complete();
            Assert.That(BuildingRegistry.FindAt(Hex), Is.Null);
        }

        [Test, Category("UnityEngine")]
        public void EquipmentValuationPricesExplosionWithoutAttackStatBonus()
        {
            var victim = new WorthIt.DefenderProfile(0, false, attack: 2, hitPoints: 4, initiative: 1);
            var targets = Enumerable.Repeat(victim, 5).ToArray();
            var ctx = new EfficiencyContext { Targets = targets,
                Battles = new[] { (IReadOnlyList<WorthIt.DefenderProfile>)targets } };
            var stats = new EfficiencyStats(1, 0, 10, 2, 4, 3, 1, 0);
            var host = new CardDefinition { cardType = CardType.Unit, attack = 1, hitPoints = 10,
                defenseRating = 0, range = 2, moveMax = 4, initiative = 3, activationApCost = 1 };
            var bomb = Bomb(UnitAbilities.Explosion10);
            ctx.AfterConsumption = EquipmentSystem.Consumption(host, bomb, null);
            var delta = EquipmentEfficiency.Utility(stats, Array.Empty<string>(), stats,
                new[] { UnitAbilities.Explosion10 }, ctx);
            Assert.That(delta.Combat, Is.GreaterThan(0), "Area damage is valued even when Attack does not change.");
        }

        [Test, Category("UnityEngine")]
        public void GroundCacheDistinguishesDisposableUnitAndReusesExactInputs()
        {
            var reusable = new WorthIt.DefenderProfile(0, false, attack: 1, hitPoints: 30,
                abilities: new[] { UnitAbilities.Explosion3, UnitAbilities.SingleUse });
            var missile = new WorthIt.DefenderProfile(0, false, attack: 1, hitPoints: 30,
                abilities: reusable.Abilities, singleUseUnit: true);
            var enemies = new[] { new WorthIt.DefenderProfile(0, false, attack: 1, hitPoints: 20) };
            WorthIt.BeginEstimateCacheScope();
            try
            {
                WorthIt.Estimate(new[] { reusable }, enemies, 0);
                WorthIt.Estimate(new[] { reusable }, enemies, 0);
                WorthIt.Estimate(new[] { missile }, enemies, 0);
                Assert.That(WorthIt.CurrentEstimateCacheStats.Misses, Is.EqualTo(2));
                Assert.That(WorthIt.CurrentEstimateCacheStats.Hits, Is.EqualTo(1));
            }
            finally { WorthIt.EndEstimateCacheScope(); }
        }
    }
}
#endif
