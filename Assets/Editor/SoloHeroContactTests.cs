#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class SoloHeroContactTests
    {
        private static ArmyData Army(PlayerSetupData owner, bool hero, bool hidden = false)
        {
            var army = new ArmyData { Owner = owner, Hex = new HexCoord(2, -1) };
            army.Members.Add(new UnitData { Owner = owner, IsHero = hero, IsHidden = hidden, HitPointsCurrent = 5 });
            return army;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SoloHeroContactRequiresVisibleHero(bool hidden)
        {
            ArmyData hero = Army(new PlayerSetupData(), true, hidden);
            Assert.That(BattleInitiator.CanInitiateContact(hero), Is.EqualTo(!hidden));
            Assert.That(BattleInitiator.IsCombatCapable(hero), Is.False);
        }

        [Test]
        public void VisibleHeroDoesNotExposeItsHiddenCombatForce()
        {
            ArmyData army = Army(new PlayerSetupData(), true);
            army.Members.Add(new UnitData { Owner = army.Owner, IsHidden = true });
            Assert.That(BattleInitiator.CanInitiateContact(army), Is.False);
        }

        [Test]
        public void VisibleCombatantWithHiddenHeroStillStartsBattle()
        {
            ArmyData army = Army(new PlayerSetupData(), true, true);
            army.Members.Add(new UnitData { Owner = army.Owner });
            Assert.That(BattleInitiator.CanInitiateContact(army), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AviationDoesNotTriggerGroundContact(bool airfield)
        {
            ArmyData army = Army(new PlayerSetupData(), true);
            army.Members[0].IsAviation = true;
            army.IsAirfield = airfield;
            Assert.That(BattleInitiator.CanInitiateContact(army), Is.False);
        }

        [Test]
        public void EmptyArmyDoesNotTriggerContact()
        {
            Assert.That(BattleInitiator.CanInitiateContact(new ArmyData()), Is.False);
            Assert.That(BattleInitiator.CanInitiateContact(null), Is.False);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void HeroArrivalUsesResidentAsHunter(bool neutral, bool humanHero)
        {
            ArmyData hero = Army(new PlayerSetupData { IsHuman = humanHero }, true);
            ArmyData hunter = Army(new PlayerSetupData { IsNeutral = neutral }, false, true);
            var original = new List<ArmyData> { hero, hunter };
            BattleEncounterContext encounter = BattleEncounterCoordinator.PrepareCommittedEncounter(hero.Hex, original, hero.Owner);

            Assert.That(encounter.Initiator, Is.SameAs(hunter));
            Assert.That(encounter.Target, Is.SameAs(hero));
            Assert.That(encounter.TargetHeroOnly, Is.True);
            Assert.That(encounter.Participants[0], Is.SameAs(hunter));
            Assert.That(encounter.Participants[1], Is.SameAs(hero));
            Assert.That(encounter.PresentationObserver, Is.SameAs(hero.Owner));
            Assert.That(original[0], Is.SameAs(hero), "Preparation must not mutate the caller's list.");
            Assert.That(hunter.Members[0].IsHidden, Is.False, "Classify after committed reveal.");

            BattleCaptureKillSequence sequence = BattleEngine.CreateTargetOnlyCaptureKillSequence(null, encounter.Initiator, encounter.Target);
            Assert.That(sequence.Count, Is.EqualTo(1));
            Assert.That(sequence.TryGetCurrent(out BattleCaptureKillTarget target), Is.True);
            Assert.That(target.Hero, Is.SameAs(hero.Members[0]));
            Assert.That(target.HunterArmy, Is.SameAs(hunter));
            Assert.That(target.HeroArmy, Is.SameAs(hero));

            // Delay records must use the normalized order: the existing drain requires a
            // combat-capable first participant. Re-preparing that record must keep the roles.
            var pending = new PendingBattle { Hex = hero.Hex, Participants = new List<ArmyData>(encounter.Participants) };
            var validate = typeof(Game.Turns.GameTurnController).GetMethod("IsStillAGenuineBattle",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That((bool)validate.Invoke(null, new object[] { pending }), Is.True);
            BattleEncounterContext resumed = BattleEncounterCoordinator.PrepareCommittedEncounter(pending.Hex, pending.Participants);
            Assert.That(resumed.Initiator, Is.SameAs(hunter));
            Assert.That(resumed.Target, Is.SameAs(hero));
            Assert.That(resumed.TargetHeroOnly, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CombatantArrivalKeepsOriginalRoles(bool heroTarget)
        {
            ArmyData mover = Army(new PlayerSetupData(), false);
            ArmyData target = Army(new PlayerSetupData(), heroTarget);
            BattleEncounterContext encounter = BattleEncounterCoordinator.PrepareCommittedEncounter(mover.Hex,
                new List<ArmyData> { mover, target });
            Assert.That(encounter.Initiator, Is.SameAs(mover));
            Assert.That(encounter.Target, Is.SameAs(target));
            Assert.That(encounter.TargetHeroOnly, Is.EqualTo(heroTarget));
        }

        [Test]
        public void MoveTraceAcceptsArrivingHeroAsTargetWithoutInventingHunterDeath()
        {
            ArmyData hero = Army(new PlayerSetupData(), true);
            ArmyData hunter = Army(new PlayerSetupData(), false);
            BattleEncounterContext encounter = BattleEncounterCoordinator.PrepareCommittedEncounter(hero.Hex,
                new List<ArmyData> { hero, hunter });
            var trace = new Game.Ai.AiMoveExecutionTrace();
            hero.Members.Clear(); // captured or killed; the moving army is the target
            trace.RecordResolvedEncounter(hero.Id, encounter.Initiator, encounter.Target);
            Assert.That(trace.WasDestroyedInOwnBattle(hunter.Id), Is.False);
            var hunterTrace = new Game.Ai.AiMoveExecutionTrace();
            hunterTrace.RecordResolvedEncounter(hunter.Id, encounter.Initiator, encounter.Target);
            Assert.That(hunterTrace.WasDestroyedInOwnBattle(hero.Id), Is.True);
        }
    }
}
#endif
