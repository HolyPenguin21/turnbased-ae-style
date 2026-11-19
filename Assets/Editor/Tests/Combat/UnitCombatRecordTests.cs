#if UNITY_INCLUDE_TESTS
using System;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // UnitCombatRecord (project owner, 2026-11-19): lifetime battles survived, damage dealt/taken
    // and kills of one live body, written by the live combat paths.
    public sealed class UnitCombatRecordTests
    {
        private static readonly PlayerSetupData Own = new PlayerSetupData();
        private static readonly PlayerSetupData Foe = new PlayerSetupData();

        private static UnitData Body(PlayerSetupData owner, int hp = 6, int attack = 2) => new UnitData
        {
            Owner = owner, Attack = attack, Defense = 1, HitPointsCurrent = hp, HitPointsMax = hp, Range = 1, Initiative = 1,
        };

        private static ArmyData Army(PlayerSetupData owner, params UnitData[] members)
        {
            var army = new ArmyData { Owner = owner };
            army.Members.AddRange(members);
            return army;
        }

        private static BattleEngine Engine(ArmyData own, ArmyData foe, params (UnitData unit, int row, int col)[] placements)
        {
            var grid = new BattleGrid();
            foreach (var p in placements) grid.Set(p.row, p.col, p.unit);
            return new BattleEngine(new BattleState(default, own, foe, grid, 42), AbilityMagnitudes.Default, new Random(42));
        }

        [TearDown] public void TearDown() { ArmyRegistry.Clear(); BuildingRegistry.Clear(); }

        [Test]
        public void HitCountsRemovedHitPointsOnBothSidesAndAKillOnlyForAnEnemyUnit()
        {
            var attacker = Body(Own); var victim = Body(Foe, hp: 3);
            victim.HitPointsCurrent = 0;
            UnitCombatRecord.RecordHit(attacker, victim, 3);
            Assert.That(attacker.CombatRecord.DamageDealt, Is.EqualTo(3), "Overkill beyond the remaining HP is not counted.");
            Assert.That(attacker.CombatRecord.Kills, Is.EqualTo(1));
            Assert.That(victim.CombatRecord.DamageTaken, Is.EqualTo(3));

            var ally = Body(Own, hp: 5); ally.HitPointsCurrent = 0;
            UnitCombatRecord.RecordHit(attacker, ally, 2);
            Assert.That(ally.CombatRecord.DamageTaken, Is.EqualTo(2), "Own-side damage is still damage taken.");
            Assert.That(attacker.CombatRecord.DamageDealt, Is.EqualTo(3), "Own-side damage is not damage dealt.");
            Assert.That(attacker.CombatRecord.Kills, Is.EqualTo(1));

            var structure = Body(Foe, hp: 9); structure.HitPointsCurrent = 0;
            UnitCombatRecord.RecordHit(attacker, structure, 4, victimIsUnit: false);
            Assert.That(attacker.CombatRecord.DamageDealt, Is.EqualTo(7));
            Assert.That(attacker.CombatRecord.Kills, Is.EqualTo(1), "A destroyed structure is not a killed unit.");

            var untouched = Body(Foe);
            UnitCombatRecord.RecordHit(attacker, untouched, untouched.HitPointsCurrent);
            Assert.That(untouched.CombatRecord.DamageTaken, Is.Zero);
            Assert.That(attacker.CombatRecord.DamageDealt, Is.EqualTo(7));
        }

        [Test]
        public void GroundBattleRecordsHitsAndCreditsOnlySurvivorsOncePerEncounter()
        {
            var striker = Body(Own); var bystander = Body(Own);
            var wounded = Body(Foe, hp: 6); var doomed = Body(Foe, hp: 2);
            var own = Army(Own, striker, bystander); var foe = Army(Foe, wounded, doomed);
            var engine = Engine(own, foe, (striker, 2, 1), (bystander, 2, 2), (wounded, 1, 1), (doomed, 1, 2));

            BattleEngineTestDriver.ApplyHit(engine, striker, wounded, 4);
            BattleEngineTestDriver.ApplyHit(engine, striker, doomed, 5);
            BattleEngineTestDriver.ApplyHit(engine, striker, wounded, 9);
            Assert.That(striker.CombatRecord.DamageDealt, Is.EqualTo(8), "4 + 2 (doomed had 2) + 2 (wounded had 2).");
            Assert.That(striker.CombatRecord.Kills, Is.EqualTo(2));
            Assert.That(wounded.CombatRecord.DamageTaken, Is.EqualTo(6));
            Assert.That(doomed.CombatRecord.DamageTaken, Is.EqualTo(2));

            BattleEngine.FinalizeEncounter(engine.State, null);
            BattleEngine.FinalizeEncounter(engine.State, null);
            Assert.That(striker.CombatRecord.BattlesSurvived, Is.EqualTo(1), "One encounter is one battle.");
            Assert.That(bystander.CombatRecord.BattlesSurvived, Is.EqualTo(1), "Every present member took part.");
            Assert.That(wounded.CombatRecord.BattlesSurvived, Is.Zero);
            Assert.That(doomed.CombatRecord.BattlesSurvived, Is.Zero);
        }

        [Test]
        public void SummonedAndUnfoughtBodiesAreNotBattleSurvivors()
        {
            var striker = Body(Own); var summoned = Body(Own); summoned.IsSummoned = true;
            var foeBody = Body(Foe, hp: 1);
            var engine = Engine(Army(Own, striker, summoned), Army(Foe, foeBody),
                (striker, 2, 1), (summoned, 2, 2), (foeBody, 1, 1));
            BattleEngineTestDriver.ApplyHit(engine, striker, foeBody, 3);
            BattleEngine.FinalizeEncounter(engine.State, null);
            Assert.That(striker.CombatRecord.BattlesSurvived, Is.EqualTo(1));
            Assert.That(summoned.CombatRecord.BattlesSurvived, Is.Zero);

            // No tactical battle: a side that cannot fight never starts the encounter.
            var lone = Body(Own);
            var state = new BattleState(default, Army(Own, lone), Army(Foe), new BattleGrid(), 7);
            BattleEngine.FinalizeEncounter(state, null);
            Assert.That(lone.CombatRecord.BattlesSurvived, Is.Zero);
        }

        [Test]
        public void CombatCopyCarriesTheRecord()
        {
            var unit = Body(Own);
            unit.CombatRecord = new UnitCombatRecord { BattlesSurvived = 1, DamageDealt = 2, DamageTaken = 3, Kills = 4 };
            var copy = unit.CloneForCombat();
            Assert.That(copy.CombatRecord.Kills, Is.EqualTo(4));
            copy.CombatRecord.Kills = 9;
            Assert.That(unit.CombatRecord.Kills, Is.EqualTo(4), "The record is a value; the copy never writes back.");
        }
    }
}
#endif
