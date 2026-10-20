#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class SingleUseEquipmentTests
    {
        private static CardDefinition Host(int attack = 2, int defense = 2, int hp = 10) => new CardDefinition
        {
            cardType = CardType.Unit, attack = attack, defenseRating = defense, hitPoints = hp,
            range = 1, initiative = 3, moveMax = 4, fate = 3, activationApCost = 1,
            unitTypeTags = new List<UnitTypeTag> { UnitTypeTag.Bio, UnitTypeTag.Infantry },
        };
        private static CardDefinition Item(int attack = 4, bool single = true, params string[] abilities)
        {
            var item = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Attack, attack);
            item.authoredKey = single ? "test_missile" : "test_gun";
            if (single) item.grantedAbilities.Add(UnitAbilities.SingleUse);
            item.equipment.addAbilities.AddRange(abilities);
            return item;
        }
        private static UnitData Body(CardDefinition host = null, CardDefinition item = null, CardDefinition mutator = null)
        {
            var unit = AttachmentSlotTests.Body(host ?? Host());
            EquipmentSystem.ApplyAttachments(unit, item, mutator);
            return unit;
        }
        private static BattleChallengeRollResult Roll(int successes) => new BattleChallengeRollResult(
            Enumerable.Repeat(true, successes).ToArray(), new bool[0], 0, 0);
        private static BattleAttackApplication Strike(UnitData a, UnitData b, int successes) =>
            BattleEngine.ResolveStandaloneAttack(a, b, Roll(successes), AbilityMagnitudes.Default);
        private static ArmyData Army(PlayerSetupData owner, params UnitData[] units)
        {
            var army = new ArmyData { Owner = owner };
            foreach (var unit in units) { unit.Owner = owner; army.Members.Add(unit); }
            return army;
        }
        private static EfficiencyStats Stats(int attack) => new EfficiencyStats(attack, 2, 10, 1, 4, 3, 1, 3);

        [Test]
        public void FirstShotUsesAllEffects_SecondShotUsesBaseAndMutator()
        {
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.Attack, 1);
            var item = Item(4, true, UnitAbilities.CriticalDamage);
            var host = Body(item: item, mutator: mutator); var target = Body(Host(hp: 100));
            Assert.That(host.Attack, Is.EqualTo(7));
            Assert.That(Strike(host, target, 2).Damage, Is.EqualTo(4));
            Assert.That(host.Equipment, Is.Null);
            Assert.That(host.Mutator, Is.SameAs(mutator));
            Assert.That(host.Attack, Is.EqualTo(3));
            Assert.That(host.HasAbility(UnitAbilities.CriticalDamage), Is.False);
            Assert.That(Strike(host, target, 2).Damage, Is.EqualTo(2));
            Assert.That(item.grantedAbilities, Does.Contain(UnitAbilities.SingleUse), "Never destroy the shared card definition");
        }

        [Test]
        public void MissConsumes_ReceivingAnAttackDoesNot()
        {
            var a = Body(item: Item()); var b = Body(item: Item());
            Assert.That(Strike(a, b, 0).Damage, Is.Zero);
            Assert.That(a.Equipment, Is.Null);
            Assert.That(b.Equipment, Is.Not.Null);
        }

        [Test]
        public void HostMarkerAndMutatorMarkerDoNotConsumeOrdinaryEquipment()
        {
            var definition = Host(); definition.grantedAbilities.Add(UnitAbilities.SingleUse);
            var gear = Item(single: false);
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator);
            mutator.equipment.addAbilities.Add(UnitAbilities.SingleUse);
            var unit = Body(definition, gear, mutator);
            Strike(unit, Body(), 0);
            Assert.That(unit.Equipment, Is.SameAs(gear));
            Assert.That(unit.Mutator, Is.SameAs(mutator));
        }

        [Test]
        public void EquipmentPayloadMarkerIsSupported()
        {
            var gear = Item(single: false); gear.equipment.addAbilities.Add(UnitAbilities.SingleUse);
            Assert.That(EquipmentSystem.IsSingleUse(gear), Is.True);
            var host = Body(item: gear); Strike(host, Body(), 0);
            Assert.That(host.Equipment, Is.Null);
        }

        [Test, Category("UnityEngine")]
        public void CardFaceShowsTheMarkerFromEitherAbilitySelector()
        {
            var gear = Item(single: false); gear.equipment.addAbilities.Add(UnitAbilities.SingleUse);
            Assert.That(EquipmentCardText.CardFace(gear, null), Does.Contain("Single Use"));
            Assert.That(EquipmentCardText.CardFace(Item(), null), Does.Contain("Single Use"));
        }

        [Test]
        public void PreviewsNeverSpendTheItem()
        {
            var gear = Item(); var host = Body(item: gear); var defender = Body();
            var session = new BattleChallengeSession(BattleChallengeMode.GroundCombat, host, defender,
                host.Attack, defender.Defense, 0, 0, AbilityMagnitudes.Default,
                rollTokens: n => Enumerable.Repeat(true, n).ToArray());
            session.Roll(); session.ResolveGroundOutcome(); session.ResolveGroundOutcome();
            BattleCombatOdds.Evaluate(host, defender, 0, AbilityMagnitudes.Default, defender.HitPointsCurrent);
            Assert.That(host.Equipment, Is.SameAs(gear));
        }

        [Test]
        public void ConsumptionRestoresOverridesAndRemovedSkills_PreservingRuntimeChanges()
        {
            var definition = Host(); definition.grantedAbilities.Add(UnitAbilities.R1S4);
            var gear = Item();
            gear.equipment.statChanges[0].isOverride = true;
            gear.equipment.statChanges[0].amount = 12;
            gear.equipment.clearAbilityFamilies.Add(AbilityFamily.Recce);
            gear.equipment.addAbilities.Add(UnitAbilities.R1S6);
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.Attack, 2);
            mutator.equipment.addAbilities.Add(UnitAbilities.CriticalDamage);
            var unit = Body(definition, gear, mutator);
            unit.Attack += 3; unit.DrillAttackBonus = 2; unit.BerserkStacks = 1;
            unit.Abilities.Add(UnitAbilities.Heal);
            unit.HitPointsCurrent -= 3; unit.MoveCurrent -= 2; unit.Fate -= 1;
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(unit.Attack, Is.EqualTo(7));
            Assert.That(unit.Abilities, Does.Contain(UnitAbilities.R1S4));
            Assert.That(unit.Abilities, Does.Not.Contain(UnitAbilities.R1S6));
            Assert.That(unit.Abilities, Does.Contain(UnitAbilities.CriticalDamage));
            Assert.That(unit.Abilities, Does.Contain(UnitAbilities.Heal));
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(7));
            Assert.That(unit.MoveCurrent, Is.EqualTo(2));
            Assert.That(unit.Fate, Is.EqualTo(2));
            Assert.That(EquipmentSystem.ConsumeAfterAttack(unit), Is.False);
        }

        [Test]
        public void SharedInnateAndMutatorSkillsSurviveConsumption()
        {
            var definition = Host(); definition.grantedAbilities.Add(UnitAbilities.CriticalDamage);
            var unit = Body(definition, Item(4, true, UnitAbilities.CriticalDamage));
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(unit.HasAbility(UnitAbilities.CriticalDamage), Is.True);
        }

        [Test]
        public void RearmingDoesNotCaptureDrillOrBerserkAsPermanentStats()
        {
            var unit = Body(item: Item()); unit.Attack += 3; unit.DrillAttackBonus = 2; unit.BerserkStacks = 1;
            EquipmentSystem.ConsumeAfterAttack(unit);
            EquipmentSystem.ApplyAttachments(unit, Item(5), null);
            Assert.That(unit.Attack, Is.EqualTo(10));
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(unit.Attack, Is.EqualTo(5));
            BattleEngine.RevertTemporaryBattleStats(Army(new PlayerSetupData(), unit), AbilityMagnitudes.Default);
            Assert.That(unit.Attack, Is.EqualTo(2));
        }

        [Test]
        public void HpFateAndMoveBonusesDisappearWithoutRefundingSpentResources()
        {
            var item = Item();
            foreach (var stat in new[] { EquipmentStat.HitPoints, EquipmentStat.Fate, EquipmentStat.MoveMax })
                item.equipment.statChanges.Add(new EquipmentStatChange { stat = stat, amount = 4 });
            var unit = Body(item: item);
            unit.HitPointsCurrent -= 2; unit.Fate -= 2; unit.MoveCurrent -= 2;
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(8));
            Assert.That(unit.Fate, Is.EqualTo(1));
            Assert.That(unit.MoveCurrent, Is.EqualTo(2));
        }

        [Test]
        public void ProjectionRetainsWoundsHiddenByALoweredMaximum()
        {
            var unit = Body(); unit.HitPointsCurrent -= 3;
            var item = Item(); item.equipment.statChanges.Add(new EquipmentStatChange
                { stat = EquipmentStat.HitPoints, amount = -8 });
            EquipmentSystem.ApplyAttachments(unit, item, null);
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(2));
            var remembered = WorthIt.FromLiveUnit(unit);
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(7));
            Assert.That(WorthIt.AfterAttack(remembered).HitPoints, Is.EqualTo(7));
        }

        [Test]
        public void IdenticalClampedHpWithDifferentWoundHistoryIsADifferentForecast()
        {
            var first = Body(); first.HitPointsCurrent -= 3;
            var second = Body(); second.HitPointsCurrent -= 5;
            var item = Item(); item.equipment.statChanges.Add(new EquipmentStatChange
                { stat = EquipmentStat.HitPoints, amount = -8 });
            EquipmentSystem.ApplyAttachments(first, item, null);
            EquipmentSystem.ApplyAttachments(second, item, null);
            var a = WorthIt.FromLiveUnit(first); var b = WorthIt.FromLiveUnit(second);
            Assert.That(a.HitPoints, Is.EqualTo(b.HitPoints));
            Assert.That(a.Consumption.Key, Is.Not.EqualTo(b.Consumption.Key));
            Assert.That(WorthIt.AfterAttack(a).HitPoints, Is.EqualTo(7));
            Assert.That(WorthIt.AfterAttack(b).HitPoints, Is.EqualTo(5));
        }

        [Test]
        public void ValuationDoesNotRefundFateHiddenByALoweredMaximum()
        {
            var unit = Body(); unit.Fate -= 2;
            var item = Item(); item.equipment.statChanges.Add(new EquipmentStatChange
                { stat = EquipmentStat.Fate, amount = -2 });
            EquipmentSystem.ApplyAttachments(unit, item, null);
            var observed = EquipmentSystem.Consumption(unit);
            var projected = EquipmentSystem.Project(unit.OriginatingCard, item, null);
            int predicted = observed.Apply(projected).Stats[EquipmentStat.Fate];
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(unit.Fate, Is.EqualTo(1));
            Assert.That(predicted, Is.EqualTo(unit.Fate));
        }

        [Test]
        public void GroundCollateralAndShockResolveBeforeTheItemDisappears()
        {
            var a = Body(item: Item(4, true, UnitAbilities.CriticalDamage, UnitAbilities.Splash, UnitAbilities.ShockAttack));
            var b = Body(); var neighbour = Body();
            var own = Army(new PlayerSetupData(), a); var foe = Army(new PlayerSetupData(), b, neighbour);
            var grid = new BattleGrid(); grid.Set(1, 1, a); grid.Set(2, 1, b); grid.Set(2, 2, neighbour);
            var state = new BattleState(default, own, foe, grid, 1);
            state.TurnOrder.Add(a); state.TurnOrder.Add(b); state.TurnOrder.Add(neighbour);
            var engine = new BattleEngine(state, AbilityMagnitudes.Default, new System.Random(1));
            var outcome = engine.ResolveAndApplyGroundAttack(a, b, Roll(2));
            Assert.That(outcome.Damage, Is.EqualTo(4));
            Assert.That(neighbour.HitPointsCurrent, Is.EqualTo(8));
            Assert.That(outcome.Shocked, Is.True);
            Assert.That(a.Equipment, Is.Null);
        }

        [Test]
        public void CounterattackConsumesTheCounterattackersItem()
        {
            var a = Body(); var b = Body(item: Item(4, true, UnitAbilities.Counterattack));
            var own = Army(new PlayerSetupData(), a); var foe = Army(new PlayerSetupData(), b);
            var grid = new BattleGrid(); grid.Set(1, 1, a); grid.Set(2, 1, b);
            var engine = new BattleEngine(new BattleState(default, own, foe, grid, 1), AbilityMagnitudes.Default);
            Assert.That(engine.ResolveAndApplyGroundAttack(a, b, Roll(0)).Counterattacker, Is.SameAs(b));
            Assert.That(b.Equipment, Is.Not.Null, "Defending alone must not spend it");
            engine.ResolveAndApplyGroundAttack(b, a, Roll(0), isCounterattack: true);
            Assert.That(b.Equipment, Is.Null);
        }

        [Test]
        public void ObservedTransitionIsFrozenAndMatchesLiveConsumption()
        {
            var item = Item(5, true, UnitAbilities.Ionic); var unit = Body(item: item);
            var observed = WorthIt.FromLiveUnit(unit);
            EquipmentSystem.ConsumeAfterAttack(unit);
            item.equipment.statChanges[0].amount = 90;
            var spent = WorthIt.AfterAttack(observed);
            Assert.That(spent.Attack, Is.EqualTo(unit.Attack));
            Assert.That(spent.Abilities, Is.EquivalentTo(unit.Abilities));
            Assert.That(observed.Attack, Is.EqualTo(7));
            Assert.That(observed.Consumption, Is.Not.Null);
            Assert.That(spent.Consumption, Is.Null);
        }

        [Test]
        public void SameArmedStatsWithDifferentSpentStatesHaveDifferentCacheKeys()
        {
            var first = WorthIt.FromLiveUnit(Body(Host(2), Item(4)));
            var second = WorthIt.FromLiveUnit(Body(Host(4), Item(2)));
            Assert.That(first.Attack, Is.EqualTo(second.Attack));
            var k1 = new List<int>(); var k2 = new List<int>();
            WorthIt.AppendConsumptionKey(k1, first.Consumption); WorthIt.AppendConsumptionKey(k2, second.Consumption);
            Assert.That(k1, Is.Not.EqualTo(k2));
            var target = WorthIt.FromLiveUnit(Body());
            Assert.That(AviationCombatEstimator.BuildKey(new[] { first }, new[] { target },
                default, 2, 0, AbilityMagnitudes.Default), Is.Not.EqualTo(
                AviationCombatEstimator.BuildKey(new[] { second }, new[] { target },
                    default, 2, 0, AbilityMagnitudes.Default)));
        }

        [Test]
        public void AircraftBombIsUsedOnceAcrossSeveralStrikePasses()
        {
            var target = WorthIt.FromLiveUnit(Body(Host(0, 0, 100)));
            var bomber = WorthIt.FromLiveUnit(Body(Host(0), Item(20)));
            var plain = WorthIt.FromLiveUnit(Body(Host(20)));
            float once = AviationCombatEstimator.EstimateAirStrike(new[] { bomber }, new[] { target }, default, 1).ExpectedDamage;
            float three = AviationCombatEstimator.EstimateAirStrike(new[] { bomber }, new[] { target }, default, 3).ExpectedDamage;
            float durable = AviationCombatEstimator.EstimateAirStrike(new[] { plain }, new[] { target }, default, 3).ExpectedDamage;
            Assert.That(three, Is.LessThan(once + 3f));
            Assert.That(durable, Is.GreaterThan(three + 10f));
        }

        [Test]
        public void GroundMonteCarloTransitionsToTheSpentStateWithinTheSameBattle()
        {
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var convert = typeof(WorthIt).GetMethod("ToBattleUnits", flags);
            var simulate = typeof(WorthIt).GetMethod("SimulateOneBattle", flags);
            int Run(WorthIt.DefenderProfile actor)
            {
                var own = convert.Invoke(null, new object[] { new[] { actor }, 0f, 0, default(WorthIt.SideCommander) });
                var enemy = convert.Invoke(null, new object[] {
                    new[] { WorthIt.FromLiveUnit(Body(Host(0, 0, 100))) }, 0f, 0, default(WorthIt.SideCommander) });
                return (int)simulate.Invoke(null, new object[] { own, enemy, new System.Random(8),
                    0, 0, (AbilityMagnitudes?)AbilityMagnitudes.Default, null });
            }
            Assert.That(Run(WorthIt.FromLiveUnit(Body(Host(0), Item(12)))), Is.EqualTo(0),
                "A single missile cannot remove 100 HP; its host is unarmed afterwards");
            Assert.That(Run(WorthIt.FromLiveUnit(Body(Host(12)))), Is.EqualTo(1),
                "A permanent weapon keeps firing over later rounds");
        }

        [Test]
        public void TacticalForecastSpendsOnlyPrivateCopies()
        {
            var item = Item(20); var a = Body(Host(0, 2, 100), item); var b = Body(Host(0, 0, 100));
            var own = Army(new PlayerSetupData(), a); var foe = Army(new PlayerSetupData(), b);
            var grid = new BattleGrid(); grid.Set(1, 1, a); grid.Set(2, 1, b);
            var one = BattleAi.SimulateRounds(grid, own, foe, 1, AbilityMagnitudes.Default, simulationTrials: 1, deterministic: true);
            var three = BattleAi.SimulateRounds(grid, own, foe, 3, AbilityMagnitudes.Default, simulationTrials: 1, deterministic: true);
            Assert.That(three.EnemyHpRemaining, Is.EqualTo(one.EnemyHpRemaining).Within(0.001f));
            Assert.That(a.Equipment, Is.SameAs(item));
            Assert.That(a.Attack, Is.EqualTo(20));
        }

        [Test]
        public void TacticalForecastKeepsAHerosDefensePoolOnFateMax()
        {
            // A hero defends with FateMax (BattleCombatOdds): the spent item's Defense change
            // must not move the shadow pool, its Fate change must.
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            CardDefinition HeroItem()
            {
                var item = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Defense, 10);
                item.equipment.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.Fate, amount = 2 });
                item.grantedAbilities.Add(UnitAbilities.SingleUse);
                return item;
            }
            var hero = Body(AttachmentSlotTests.Host(hero: true), HeroItem());
            var foe = Body(Host(hp: 100));
            var own = Army(new PlayerSetupData(), hero); var enemy = Army(new PlayerSetupData(), foe);
            var grid = new BattleGrid(); grid.Set(1, 1, hero); grid.Set(2, 1, foe);
            Assert.That(hero.FateMax, Is.EqualTo(6));
            var hp = new Dictionary<UnitData, float> { [hero] = hero.HitPointsCurrent, [foe] = foe.HitPointsCurrent };
            var attack = new Dictionary<UnitData, int> { [hero] = hero.Attack, [foe] = foe.Attack };
            var defense = new Dictionary<UnitData, int> { [hero] = hero.FateMax, [foe] = foe.Defense };
            var fate = new Dictionary<ArmyData, int> { [own] = hero.Fate, [enemy] = 0 };
            typeof(BattleAi).GetMethod("ApplySimAttack", flags).Invoke(null, new object[] { grid, hp, hero, foe,
                (float?)0f, false, AbilityMagnitudes.Default, null, 0, null, fate, attack, defense, own, enemy,
                new BattleReactionState(), null, 0f, false, null, null, -1 });
            Assert.That(hero.Equipment, Is.Null);
            Assert.That(defense[hero], Is.EqualTo(4));
            Assert.That(fate[own], Is.EqualTo(4));

            // The Counterattack answer faces the spent hero exactly like a hero that never had the item.
            var counter = typeof(BattleTargetSelector).GetMethod("ExpectedCounterDamage", flags);
            float Answer(UnitData actor, int? pool)
            {
                var victim = Body(Host(attack: 6, hp: 100));
                victim.Abilities.Add(UnitAbilities.Counterattack);
                var g = new BattleGrid(); g.Set(1, 1, actor); g.Set(2, 1, victim);
                var shadow = pool.HasValue ? new Dictionary<UnitData, int> { [actor] = pool.Value } : null;
                return (float)counter.Invoke(null, new object[] { g, actor, victim, 10f, AbilityMagnitudes.Default,
                    null, null, 0, null, shadow });
            }
            var plain = Body(AttachmentSlotTests.Host(hero: true));
            var armed = Body(AttachmentSlotTests.Host(hero: true), HeroItem());
            Assert.That(Answer(armed, null), Is.EqualTo(Answer(plain, null)).Within(0.0001f));
            Assert.That(Answer(armed, armed.FateMax), Is.EqualTo(Answer(plain, plain.FateMax)).Within(0.0001f));
            Assert.That(armed.Equipment, Is.Not.Null, "A target preview spends only a private copy");
        }

        [Test]
        public void MovementLookaheadDoesNotSpendEquipmentOrIssueLiveIdentities()
        {
            var gear = Item(10); var a = Body(Host(2), gear); var b = Body();
            var own = Army(new PlayerSetupData(), a); var foe = Army(new PlayerSetupData(), b);
            var grid = new BattleGrid(); grid.Set(0, 0, a); grid.Set(3, 3, b);
            int before = new UnitData().RuntimeId;
            var first = BattleAi.ChooseAction(grid, a, new Dictionary<UnitData, int>(), own, foe,
                AbilityMagnitudes.Default, new List<UnitData> { a, b }, 0);
            var second = BattleAi.ChooseAction(grid, a, new Dictionary<UnitData, int>(), own, foe,
                AbilityMagnitudes.Default, new List<UnitData> { a, b }, 0);
            Assert.That(first.Kind, Is.EqualTo(BattleAi.AiActionKind.Move));
            Assert.That((second.Row, second.Col), Is.EqualTo((first.Row, first.Col)));
            Assert.That(a.Equipment, Is.SameAs(gear));
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(before + 1));
        }

        [Test]
        public void CreationValueDistinguishesOneShotFromPermanentWeapon()
        {
            var host = Host(); var missile = Item(4); var gun = Item(4, false);
            float one = EquipmentUpgradeValuation.EquipmentUpgradeValue(
                EquipmentUpgradeValuation.EquipmentDeltaParts(missile, host));
            float permanent = EquipmentUpgradeValuation.EquipmentUpgradeValue(
                EquipmentUpgradeValuation.EquipmentDeltaParts(gun, host));
            Assert.That(one, Is.GreaterThan(0f));
            Assert.That(one, Is.LessThan(permanent));
            Assert.That(EquipmentUpgradeValuation.EquipmentUpgradeValue(
                EquipmentUpgradeValuation.EquipmentDeltaParts(Item(0), host)), Is.EqualTo(0f).Within(0.00001f));
        }

        [Test]
        public void AirValuationPricesOneBombRatherThanOneBombPerSortie()
        {
            var before = Stats(2); var after = Stats(6);
            var ctx = new EfficiencyContext { ExpectedActivations = 3f };
            float permanent = EquipmentEfficiency.AviationOffenseDelta(before, new string[0], after, new string[0], ctx);
            ctx.AfterConsumption = EquipmentSystem.Consumption(Host(), Item(), null);
            float single = EquipmentEfficiency.AviationOffenseDelta(before, new string[0], after, new string[0], ctx);
            Assert.That(single, Is.EqualTo(permanent / 3f).Within(0.00001f));
        }

        [Test]
        public void AaValuationSpendsTheMissileOnOnlyOneOfSeveralAvailableReactions()
        {
            var air = new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Aircraft }, 2, 5);
            var contacts = new[] { new AirContact(air, 0, true, false, 0),
                new AirContact(air, 0, true, false, 0), new AirContact(air, 0, true, false, 0) };
            var ctx = new EfficiencyContext { AirContacts = contacts };
            var abilities = new[] { UnitAbilities.AntiAir };
            float permanent = EquipmentEfficiency.Utility(Stats(2), new string[0], Stats(6), abilities, ctx,
                includeCombat: false).AntiAir;
            ctx.AfterConsumption = EquipmentSystem.Consumption(Host(), Item(4, true, UnitAbilities.AntiAir), null);
            float single = EquipmentEfficiency.Utility(Stats(2), new string[0], Stats(6), abilities, ctx,
                includeCombat: false).AntiAir;
            Assert.That(single, Is.EqualTo(permanent / 3f).Within(0.00001f));
        }

        [Test]
        public void DurableMutatorStillHasValueWhenTheOtherSlotIsConsumable()
        {
            var host = new CardData(Host()) { Equipment = Item(4) };
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.Attack, 1);
            Assert.That(EquipmentUpgradeValuation.EquipmentUpgradeValue(
                EquipmentUpgradeValuation.EquipmentDeltaParts(mutator, host)), Is.GreaterThan(0f));
        }

        [Test]
        public void SpentEnemyRetainsItsKnownNeighboursForSplashValuation()
        {
            var targetDef = Host(0, 0, 100); targetDef.initiative = 10;
            var plainTarget = WorthIt.FromLiveUnit(Body(targetDef));
            var armedTarget = WorthIt.FromLiveUnit(Body(targetDef, Item(0)));
            var neighbour = WorthIt.FromLiveUnit(Body(Host(0, 0, 2)));
            float Value(WorthIt.DefenderProfile target)
            {
                var ctx = new EfficiencyContext
                {
                    Targets = new[] { target }, KnownDistance = 1, SecondaryNeighbors = 0,
                    Battles = new IReadOnlyList<WorthIt.DefenderProfile>[] { new[] { target, neighbour } },
                    AfterConsumption = EquipmentSystem.Consumption(Host(), Item(4, true, UnitAbilities.Splash), null),
                };
                return EquipmentEfficiency.Utility(Stats(2), new string[0], Stats(6),
                    new[] { UnitAbilities.Splash }, ctx).Combat;
            }
            Assert.That(Value(armedTarget), Is.EqualTo(Value(plainTarget)).Within(0.00001f),
                "A zero-effect enemy item spent before our shot must not erase its known Splash neighbours");
        }

        [Test]
        public void NewConsumableNeedIsNotDampedAsAnotherPermanentUpgrade()
        {
            var player = new PlayerSetupData(); var missile = Item(); var gun = Item(single: false);
            DevelopmentDiversity.RecordAttempt(player, 5, missile); DevelopmentDiversity.RecordAttempt(player, 5, gun);
            Assert.That(DevelopmentDiversity.RepeatFactor(player, 5, missile, out _), Is.EqualTo(1f));
            Assert.That(DevelopmentDiversity.RepeatFactor(player, 5, gun, out _), Is.LessThan(1f));
            var unit = Body(item: missile);
            Assert.That(EquipmentSystem.CanAttachPreview(missile, unit, out _), Is.False);
            EquipmentSystem.ConsumeAfterAttack(unit);
            Assert.That(EquipmentSystem.CanAttachPreview(missile, unit, out _), Is.True);
        }
    }
}
#endif
