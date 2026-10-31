#if UNITY_INCLUDE_TESTS
using System;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class HealAbilityTests
    {
        private static UnitData Body(int hp, int max, bool bio = true, bool hero = false)
        {
            var unit = new UnitData { HitPointsCurrent = hp, HitPointsMax = max, IsHero = hero, Attack = 1, Defense = 1 };
            if (bio) unit.TypeTags.Add(UnitTypeTag.Bio);
            return unit;
        }
        private static BattleEngine Engine(ArmyData own, ArmyData enemy)
        {
            var grid = new BattleGrid();
            int col = 0;
            foreach (UnitData u in own.Members) grid.Set(u.IsHero ? 4 : 3, col++, u);
            col = 0;
            foreach (UnitData u in enemy.Members) grid.Set(u.IsHero ? 0 : 1, col++, u);
            return new BattleEngine(new BattleState(default, own, enemy, grid, 42), AbilityMagnitudes.Default, new Random(42));
        }
        [Test]
        public void HeroHealsOneWoundedBioOncePerRound_IncludingSecondaryHeroes()
        {
            UnitData commander = Body(3, 3, hero: true), healer = Body(3, 3, hero: true), target = Body(1, 4);
            healer.Abilities.Add(UnitAbilities.Heal);
            var own = new ArmyData(); own.Members.AddRange(new[] { commander, healer, target });
            var enemy = new ArmyData(); enemy.Members.Add(Body(3, 3, false));
            BattleEngine engine = Engine(own, enemy); int events = 0; engine.UnitHealed += _ => events++;
            engine.StartRound(); engine.StartRound();
            Assert.That(target.HitPointsCurrent, Is.EqualTo(2)); Assert.That(events, Is.EqualTo(1));
            BattleEngineTestDriver.NextRound(engine);
            engine.StartRound(); Assert.That(target.HitPointsCurrent, Is.EqualTo(3));
            Assert.That(WorthIt.SideCommander.Of(own.Members).SupportHeroes.Count, Is.EqualTo(1));
        }
        [Test]
        public void UnitHealsAtOwnTurnStart_NotAtRoundStartWhenAnotherUnitActsFirst()
        {
            UnitData healer = Body(3, 3), target = Body(1, 4);
            healer.Abilities.Add(UnitAbilities.Heal); healer.Initiative = 1; target.Initiative = 20;
            var own = new ArmyData(); own.Members.AddRange(new[] { healer, target });
            var enemy = new ArmyData(); UnitData foe = Body(3, 3, false); foe.Initiative = 0; enemy.Members.Add(foe);
            BattleEngine engine = Engine(own, enemy); engine.StartRound();
            Assert.That(target.HitPointsCurrent, Is.EqualTo(1));
            Assert.That(engine.TryPass(engine.CurrentActor), Is.True);
            engine.AdvanceTurn(); Assert.That(target.HitPointsCurrent, Is.EqualTo(2));
        }
        [Test]
        public void CannotHealDeadMechanicalOrFullHealth_AndSelfHealingIsAllowed()
        {
            UnitData healer = Body(2, 3, hero: true), dead = Body(0, 4), machine = Body(1, 4, false), full = Body(4, 4);
            healer.Abilities.Add(UnitAbilities.Heal);
            var own = new ArmyData(); own.Members.AddRange(new[] { healer, dead, machine, full });
            var enemy = new ArmyData(); enemy.Members.Add(Body(3, 3, false));
            Engine(own, enemy).StartRound();
            Assert.That(healer.HitPointsCurrent, Is.EqualTo(3)); Assert.That(dead.HitPointsCurrent, Is.Zero);
            Assert.That(machine.HitPointsCurrent, Is.EqualTo(1)); Assert.That(full.HitPointsCurrent, Is.EqualTo(4));
        }
        [Test]
        public void PassiveHeroHealIsIncludedInBodyOnlyForecast_WithoutChangingLiveHp()
        {
            UnitData hero = Body(3, 3, hero: true), body = Body(4, 4), enemy = Body(4, 4, false);
            hero.Abilities.Add(UnitAbilities.Heal);
            // A valid battle-only roster skips the unrelated authored summon-catalog lookup.
            body.IsSummoned = enemy.IsSummoned = true;
            var profiles = new[] { WorthIt.FromLiveUnit(body) };
            var opposition = new[] { WorthIt.FromLiveUnit(enemy) };
            WorthIt.BattleEstimate plain = WorthIt.Estimate(profiles, opposition, 0f,
                new WorthIt.SideCommander(hero.Initiative, hero.Fate));
            WorthIt.BattleEstimate healed = WorthIt.Estimate(profiles, opposition, 0f, WorthIt.SideCommander.Of(hero));
            Assert.That(healed.WinChance, Is.GreaterThan(plain.WinChance));
            Assert.That(body.HitPointsCurrent, Is.EqualTo(4));
        }
        [Test]
        public void HealingCommanderSnapshotEqualityTracksHp_WithoutReferenceIdentityNoise()
        {
            UnitData hero = Body(2, 3, hero: true); hero.Abilities.Add(UnitAbilities.Heal);
            WorthIt.SideCommander first = WorthIt.SideCommander.Of(hero);
            Assert.That(first.Equals(WorthIt.SideCommander.Of(hero)), Is.True);
            hero.HitPointsCurrent++;
            Assert.That(first.Equals(WorthIt.SideCommander.Of(hero)), Is.False);
        }
        [Test]
        [Category("UnityEngine")]
        public void ShadowSimulationLeavesLiveHitPointsUntouched()
        {
            UnitData healer = Body(4, 4, hero: true), target = Body(1, 4);
            healer.Abilities.Add(UnitAbilities.Heal);
            var own = new ArmyData(); own.Members.AddRange(new[] { healer, target });
            var enemy = new ArmyData(); enemy.Members.Add(Body(4, 4, false));
            BattleEngine engine = Engine(own, enemy);
            BattleAi.SimulateRounds(engine.State.Grid, own, enemy, 2, AbilityMagnitudes.Default);
            Assert.That(target.HitPointsCurrent, Is.EqualTo(1));
        }
        [Test]
        public void SkillOverlayFadesWithinTheSharedAtlasPlayback()
        {
            Assert.That(Game.UI.BattleSkillEffect.AlphaAt(-0.01f), Is.Zero);
            Assert.That(Game.UI.BattleSkillEffect.AlphaAt(0f), Is.Zero);
            Assert.That(Game.UI.BattleSkillEffect.AlphaAt(0.12f), Is.EqualTo(0.95f).Within(0.001f));
            Assert.That(Game.UI.BattleSkillEffect.AlphaAt(0.5f), Is.EqualTo(0.95f).Within(0.001f));
            Assert.That(Game.UI.BattleSkillEffect.AlphaAt(0.79f), Is.EqualTo(0.475f).Within(0.001f));
            Assert.That(Game.UI.BattleSkillEffect.AlphaAt(Game.UI.BattleSkillEffect.Duration), Is.Zero);
        }

        [Test]
        public void EachHealingHeroActivatesOnceAtRoundStart_WithoutHealingTheOtherArmy()
        {
            UnitData first = Body(3, 3, hero: true), second = Body(3, 3, hero: true), target = Body(1, 4);
            first.Abilities.Add(UnitAbilities.Heal); second.Abilities.Add(UnitAbilities.Heal);
            var own = new ArmyData(); own.Members.AddRange(new[] { first, second, target });
            UnitData woundedEnemy = Body(1, 4);
            var enemy = new ArmyData(); enemy.Members.Add(woundedEnemy);
            BattleEngine engine = Engine(own, enemy); engine.StartRound(); engine.StartRound();
            Assert.That(target.HitPointsCurrent, Is.EqualTo(3));
            Assert.That(woundedEnemy.HitPointsCurrent, Is.EqualTo(1));
            Assert.That(WorthIt.SideCommander.Of(own.Members).SupportHeroes.Count, Is.EqualTo(2));
        }

        [Test]
        public void RetreatingSideDoesNotActivateHeroOrUnitHealing()
        {
            UnitData hero = Body(3, 3, hero: true), unit = Body(1, 4);
            hero.Abilities.Add(UnitAbilities.Heal); unit.Abilities.Add(UnitAbilities.Heal);
            var own = new ArmyData(); own.Members.AddRange(new[] { hero, unit });
            var enemy = new ArmyData(); enemy.Members.Add(Body(3, 3, false));
            BattleEngine engine = Engine(own, enemy); BattleEngineTestDriver.Retreat(engine.State, own); engine.StartRound();
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(1));
        }
    }
}
#endif
