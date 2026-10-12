#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class TransportAbilityTests
    {
        private static UnitData Unit(int move, bool transport = false, bool infantry = false)
        {
            var u = new UnitData { MoveMax = move, MoveCurrent = move, HitPointsCurrent = 3, HitPointsMax = 3 };
            if (transport) u.Abilities.Add(UnitAbilities.Transport);
            if (infantry) u.TypeTags.Add(UnitTypeTag.Infantry);
            return u;
        }
        private static ArmyData Army(params UnitData[] members)
        {
            var a = new ArmyData(); a.Members.AddRange(members); a.RefreshTransport(); return a;
        }
        [Test]
        public void OneCarrierSupportsExactlyOneInfantry_AndAnotherSlowBodyStillLimitsArmy()
        {
            UnitData carrier = Unit(5, true), first = Unit(2, infantry: true), second = Unit(2, infantry: true);
            ArmyData army = Army(carrier, first, second);
            Assert.That(army.Members.Count(u => u.TransportCarrier == carrier), Is.EqualTo(1));
            Assert.That(army.MaxMovement, Is.EqualTo(2));
            UnitData selected = army.Members.Single(u => u.TransportCarrier == carrier);
            army.Members.Reverse(); army.RefreshTransport();
            Assert.That(selected.TransportCarrier, Is.SameAs(carrier));
        }
        [Test]
        public void MovingCarrierClearsOldBonusAndImmediatelySupportsNewArmy()
        {
            UnitData carrier = Unit(5, true), oldBody = Unit(2, infantry: true), newBody = Unit(3, infantry: true);
            ArmyData old = Army(carrier, oldBody), next = Army(newBody);
            old.Members.Remove(carrier); next.Members.Add(carrier);
            old.RefreshTransport(); next.RefreshTransport();
            Assert.That(oldBody.MoveMax, Is.EqualTo(2)); Assert.That(newBody.MoveMax, Is.EqualTo(5));
            Assert.That(next.CurrentMovement, Is.EqualTo(5));
        }
        [TestCase(true)]
        [TestCase(false)]
        public void DeadOrTransferredRecipientIsReplaced(bool death)
        {
            UnitData carrier = Unit(5, true), first = Unit(2, infantry: true), second = Unit(3, infantry: true);
            ArmyData army = Army(carrier, first, second);
            UnitData selected = army.Members.Single(u => u.TransportCarrier == carrier);
            if (death) selected.HitPointsCurrent = 0; else army.Members.Remove(selected);
            army.RefreshTransport();
            Assert.That(selected.TransportCarrier, Is.Null);
            Assert.That(army.Members.Where(u => u != selected).Single(u => u.TransportCarrier == carrier).MoveMax, Is.EqualTo(5));
        }
        [Test]
        public void BonusRemovalAndReturnNeverRefundSpentMovement()
        {
            UnitData carrier = Unit(5, true), body = Unit(2, infantry: true);
            ArmyData army = Army(carrier, body);
            carrier.MoveCurrent -= 4; body.MoveCurrent -= 4;
            army.Members.Remove(carrier); army.RefreshTransport();
            Assert.That(body.MoveCurrent, Is.Zero); body.MoveCurrent = 0;
            army.Members.Add(carrier); army.RefreshTransport();
            Assert.That(body.MoveCurrent, Is.EqualTo(1)); Assert.That(carrier.MoveCurrent, Is.EqualTo(1));
            body.ReplenishMoveForNewTurn(); Assert.That(body.MoveCurrent, Is.EqualTo(5));
        }
        [Test]
        public void DeadCarrierOrLostSkillRemovesBonus()
        {
            UnitData carrier = Unit(5, true), body = Unit(2, infantry: true);
            ArmyData army = Army(carrier, body); carrier.HitPointsCurrent = 0; army.RefreshTransport();
            Assert.That(body.MoveMax, Is.EqualTo(2));
            carrier.HitPointsCurrent = 3; army.RefreshTransport(); Assert.That(body.MoveMax, Is.EqualTo(5));
            carrier.Abilities.Clear(); army.RefreshTransport(); Assert.That(body.MoveMax, Is.EqualTo(2));
        }
        [Test]
        public void ProjectionMatchesCommitAndDoesNotWriteLiveBindings()
        {
            UnitData carrier = Unit(5, true), body = Unit(2, infantry: true);
            var roster = new[] { carrier, body };
            Assert.That(ArmyData.ComputeMaxMovement(roster), Is.EqualTo(5));
            Assert.That(ArmyData.ComputeCurrentMovement(roster), Is.EqualTo(5));
            Assert.That(body.MoveMax, Is.EqualTo(2)); Assert.That(body.TransportCarrier, Is.Null);
            Assert.That(Army(roster).MaxMovement, Is.EqualTo(5));
        }
        [Test]
        public void MultipleCarriersUseDistinctTargetsWithoutStacking()
        {
            UnitData a = Unit(5, true), b = Unit(4, true), first = Unit(2, infantry: true), second = Unit(2, infantry: true);
            ArmyData army = Army(a, b, first, second);
            Assert.That(first.TransportCarrier, Is.Not.SameAs(second.TransportCarrier));
            Assert.That(new[] { first.MoveMax, second.MoveMax }, Is.EquivalentTo(new[] { 4, 5 }));
            Assert.That(army.MaxMovement, Is.EqualTo(4));
        }
        [Test]
        public void MovingBothCarrierAndPassengerCannotBeClearedByLateSourceRefresh()
        {
            UnitData carrier = Unit(5, true), body = Unit(2, infantry: true);
            ArmyData source = Army(carrier, body), destination = Army();
            source.Members.Clear(); destination.Members.AddRange(new[] { carrier, body });
            destination.RefreshTransport(); source.RefreshTransport();
            Assert.That(body.TransportCarrier, Is.SameAs(carrier)); Assert.That(destination.MaxMovement, Is.EqualTo(5));
        }
        [Test]
        public void HousekeepingProjectionStripsDonorBonusWhenTransportDoesNotMove()
        {
            UnitData carrier = Unit(5, true), body = Unit(2, infantry: true);
            Army(carrier, body);
            var projected = new Game.Ai.V2.ReorgUnit
            {
                Key = 1, RuntimeIdentity = body.RuntimeId, MoveMax = body.MoveMax, NativeMoveMax = body.PermanentMoveMax,
                MoveCurrent = body.MoveCurrent, MovementSpent = body.MovementSpent,
                TransportCarrierIdentity = carrier.RuntimeId, TypeTags = body.TypeTags.ToList(),
                CombatProfile = Game.Combat.WorthIt.FromLiveUnit(body),
            };
            var movement = Game.Ai.V2.ReorgViability.Movement(new[] { projected });
            Assert.That(movement.maximum, Is.EqualTo(2)); Assert.That(movement.current, Is.EqualTo(2));
            Assert.That(body.MoveMax, Is.EqualTo(5));
        }
        [Test]
        public void FasterInfantryAndNonInfantryDoNotConsumeTransportSlot()
        {
            UnitData carrier = Unit(4, true), faster = Unit(5, infantry: true), vehicle = Unit(2), slow = Unit(3, infantry: true);
            Army(carrier, faster, vehicle, slow);
            Assert.That(faster.TransportCarrier, Is.Null); Assert.That(vehicle.TransportCarrier, Is.Null);
            Assert.That(slow.TransportCarrier, Is.SameAs(carrier));
        }
        [Test]
        public void PermanentMoveUpgradeDoesNotBakeTransportOrRestoreMovement()
        {
            UnitData carrier = Unit(5, true), body = Unit(2, infantry: true);
            ArmyData army = Army(carrier, body); body.MoveCurrent -= 1;
            var grant = new EquipmentGrant(); grant.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.MoveMax, amount = 1 });
            EquipmentSystem.Apply(grant, body); army.RefreshTransport();
            Assert.That(body.PermanentMoveMax, Is.EqualTo(3)); Assert.That(body.MoveMax, Is.EqualTo(5));
            Assert.That(body.MoveCurrent, Is.EqualTo(4));
            army.Members.Remove(carrier); army.RefreshTransport();
            Assert.That(body.MoveMax, Is.EqualTo(3)); Assert.That(body.MoveCurrent, Is.EqualTo(2));
        }

        [Test]
        public void TransportReceivedByAnotherCarrierDoesNotChainItsBonus()
        {
            UnitData fast = Unit(7, true), slower = Unit(4, true, infantry: true), body = Unit(2, infantry: true);
            ArmyData army = Army(fast, slower);
            army.Members.Add(body); army.RefreshTransport();
            Assert.That(slower.TransportCarrier, Is.SameAs(fast));
            Assert.That(body.TransportCarrier, Is.SameAs(slower));
            Assert.That(body.MoveMax, Is.EqualTo(4));
        }

        [Test]
        public void NewFasterCarrierPreservesTheExistingPair()
        {
            UnitData old = Unit(4, true), body = Unit(2, infantry: true), added = Unit(7, true);
            ArmyData army = Army(old, body);
            army.Members.Add(added); army.RefreshTransport();
            Assert.That(body.TransportCarrier, Is.SameAs(old));
            Assert.That(body.MoveMax, Is.EqualTo(4));
        }

        [Test]
        public void PermanentRecipientUpgradeFreesTransportForAnotherInfantry()
        {
            UnitData carrier = Unit(5, true), first = Unit(2, infantry: true), second = Unit(3, infantry: true);
            ArmyData army = Army(carrier, first);
            army.Members.Add(second); army.RefreshTransport();
            first.MoveMax = 6; army.RefreshTransport();
            Assert.That(first.TransportCarrier, Is.Null);
            Assert.That(second.TransportCarrier, Is.SameAs(carrier));
            Assert.That(first.MoveMax, Is.EqualTo(6));
        }

        [Test]
        public void FogMemoryFreezesObservedMovementWithoutGrantingASecondBonus()
        {
            UnitData carrier = Unit(5, true), first = Unit(2, infantry: true), second = Unit(2, infantry: true);
            ArmyData army = Army(carrier, first, second);
            var method = typeof(HumanVisualMemory).GetMethod("SnapshotArmy",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var snapshot = (ArmyData)method.Invoke(null, new object[] { army, army.Hex, null });
            Assert.That(snapshot.MaxMovement, Is.EqualTo(2));
            Assert.That(snapshot.CurrentMovement, Is.EqualTo(2));
            army.Members.Remove(second); army.RefreshTransport();
            carrier.MoveMax = 8; army.RefreshTransport();
            Assert.That(snapshot.MaxMovement, Is.EqualTo(2));
            Assert.That(snapshot.Members.Single(u => u.HasAbility(UnitAbilities.Transport)).MoveMax, Is.EqualTo(5));
        }
    }
}
#endif
