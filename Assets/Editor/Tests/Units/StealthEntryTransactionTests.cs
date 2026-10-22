#if UNITY_INCLUDE_TESTS
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class StealthEntryTransactionTests
    {
        private PlayerSetupData _player;
        private PlayerRoot _root;
        private UnitData _unit;

        [SetUp]
        public void SetUp()
        {
            PlayerRootRegistry.Clear();
            StealthSystem.Clear();
            _player = new PlayerSetupData();
            _root = PlayerRoot.Create(_player, "stealth transaction owner");
            PlayerRootRegistry.Register(_player, _root);
            _unit = new UnitData { Owner = _player };
            _unit.Abilities.Add(UnitAbilities.Stealth4);
        }

        [TearDown]
        public void TearDown()
        {
            StealthSystem.Clear();
            PlayerRootRegistry.Clear();
            if (_root != null) Object.DestroyImmediate(_root.gameObject);
        }

        [Test]
        public void SuccessfulEntryDebitsExactlyOnceAndHidesUnit()
        {
            _root.ActionPoints = 3;

            Assert.That(StealthSystem.TryEnterStealth(_unit, _root), Is.True);
            Assert.That(_unit.IsHidden, Is.True);
            Assert.That(_root.ActionPoints, Is.EqualTo(2));

            Assert.That(StealthSystem.TryEnterStealth(_unit, _root), Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(2),
                "A repeated entry attempt must not debit AP after the unit is already hidden.");
        }

        [Test]
        public void InsufficientApLeavesBothAccountAndStealthStateUntouched()
        {
            _root.ActionPoints = 0;

            Assert.That(StealthSystem.TryEnterStealth(_unit, _root), Is.False);
            Assert.That(_unit.IsHidden, Is.False);
            Assert.That(_root.ActionPoints, Is.Zero);
        }

        [Test]
        public void PreserveApPreventsEntryThatWouldStrandImmediateFollowup()
        {
            _root.ActionPoints = 2;

            Assert.That(StealthSystem.TryEnterStealth(_unit, _root, preserveActionPoints: 2), Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(2));
            Assert.That(_unit.IsHidden, Is.False);

            _root.ActionPoints = 3;
            Assert.That(StealthSystem.TryEnterStealth(_unit, _root, preserveActionPoints: 2), Is.True);
            Assert.That(_root.ActionPoints, Is.EqualTo(2));
            Assert.That(_unit.IsHidden, Is.True);
        }

        [Test]
        public void AlternateSameOwnerRootCannotPayForEntry()
        {
            _root.ActionPoints = 3;
            PlayerRoot alternate = PlayerRoot.Create(_player, "noncanonical stealth account");
            alternate.ActionPoints = 3;
            try
            {
                Assert.That(StealthSystem.TryEnterStealth(_unit, alternate), Is.False);
                Assert.That(_unit.IsHidden, Is.False);
                Assert.That(alternate.ActionPoints, Is.EqualTo(3));
                Assert.That(_root.ActionPoints, Is.EqualTo(3));
            }
            finally
            {
                Object.DestroyImmediate(alternate.gameObject);
            }
        }

        // Project owner, 2026-10-22: stealth is entered at most once per own turn — a unit that
        // revealed itself (a sabotage, a strike) cannot vanish again until its next turn.
        [Test]
        public void EntryIsOncePerOwnTurn()
        {
            _root.ActionPoints = 5;
            Assert.That(StealthSystem.TryEnterStealth(_unit, _root), Is.True);
            StealthSystem.ExitStealth(_unit);

            Assert.That(StealthSystem.CanEnterStealth(_unit), Is.False);
            Assert.That(StealthSystem.TryEnterStealth(_unit, _root), Is.False);
            Assert.That(_unit.IsHidden, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(4), "a refused re-entry debits nothing");

            _unit.HasEnteredStealthThisTurn = false; // the owner's next turn (ReplenishMoveForOwner)
            Assert.That(StealthSystem.TryEnterStealth(_unit, _root), Is.True);
        }

        [Test]
        public void VoluntaryExitRemainsFree()
        {
            _root.ActionPoints = 2;
            Assert.That(StealthSystem.TryEnterStealth(_unit, _root), Is.True);
            int afterEntry = _root.ActionPoints;

            StealthSystem.ExitStealth(_unit);

            Assert.That(_unit.IsHidden, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(afterEntry));
        }
    }
}
#endif
