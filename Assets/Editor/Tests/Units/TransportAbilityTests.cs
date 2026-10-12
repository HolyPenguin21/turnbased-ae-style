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
            ArmyData army = Army(slower, body);
            army.Members.Add(fast); army.RefreshTransport();
            Assert.That(slower.TransportCarrier, Is.SameAs(fast));
            Assert.That(body.TransportCarrier, Is.SameAs(slower));
            Assert.That(body.MoveMax, Is.EqualTo(4));
        }

        [Test]
        public void CarrierServesTheSlowestEligibleInfantry()
        {
            UnitData carrier = Unit(4, true), slowest = Unit(2, infantry: true), slow = Unit(3, infantry: true);
            ArmyData army = Army(carrier, slow, slowest);
            Assert.That(slowest.TransportCarrier, Is.SameAs(carrier));
            Assert.That(slow.TransportCarrier, Is.Null);
            Assert.That(army.MaxMovement, Is.EqualTo(3));
        }

        [Test]
        public void KeptPairYieldsToANewStrictlySlowerInfantry()
        {
            UnitData carrier = Unit(5, true), slow = Unit(3, infantry: true), slowest = Unit(2, infantry: true);
            ArmyData army = Army(carrier, slow);
            Assert.That(slow.TransportCarrier, Is.SameAs(carrier));
            army.Members.Add(slowest); army.RefreshTransport();
            Assert.That(slowest.TransportCarrier, Is.SameAs(carrier));
            Assert.That(slow.TransportCarrier, Is.Null);
            Assert.That(army.MaxMovement, Is.EqualTo(3));
            Assert.That(ArmyData.ComputeMaxMovement(army.Members.ToList()), Is.EqualTo(3));
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

        // Audit 2026-10-12: per-body AI facts select subsets that leave without their carrier.
        [Test]
        public void AiSnapshotRecordsEachBodysOwnSpeedWithoutTheCarriersSupport()
        {
            var owner = new Game.Players.PlayerSetupData();
            UnitData carrier = Unit(5, true), body = Unit(2, infantry: true);
            ArmyData army = Army(carrier, body); army.Owner = owner;
            body.MoveCurrent -= 1;
            try
            {
                var facts = Game.Ai.V2.WorldAnalysis.ToArmySnapshot(army, owner, true, 0);
                int i = facts.NonHeroRuntimeIds.ToList().IndexOf(body.RuntimeId);
                Assert.That(body.MoveMax, Is.EqualTo(5));
                Assert.That(facts.MaxMovement, Is.EqualTo(5));
                Assert.That(facts.NonHeroMoveMax[i], Is.EqualTo(2));
                Assert.That(facts.NonHeroCurrentMovement[i], Is.EqualTo(1));
            }
            finally { Game.Ai.V2.MissionIntentRegistry.Clear(); }
        }

        private static int _reorgKey = 7000;
        private static Game.Ai.V2.ReorgUnit ReorgBody(int move, int native, bool transport = false,
            bool infantry = false, int carrierId = -1, float hp = 5f)
        {
            int id = ++_reorgKey;
            var tags = new System.Collections.Generic.List<UnitTypeTag> { infantry ? UnitTypeTag.Infantry : UnitTypeTag.Vehicle };
            var abilities = new System.Collections.Generic.List<string>();
            if (transport) abilities.Add(UnitAbilities.Transport);
            return new Game.Ai.V2.ReorgUnit
            {
                Key = id, RuntimeIdentity = id, TransportCarrierIdentity = carrierId, Power = 8f,
                IsGroundCombatant = true, IsGroundBattleBody = true,
                MoveCurrent = move, MoveMax = move, NativeMoveMax = native, TypeTags = tags,
                CombatProfile = new Game.Combat.WorthIt.DefenderProfile(3f, false, tags, 3f, hp, 3, abilities, 5f),
            };
        }

        private static Game.Ai.V2.ReorgUnit ReorgLead(bool committed = false) => new Game.Ai.V2.ReorgUnit
        {
            Key = ++_reorgKey, RuntimeIdentity = _reorgKey, IsHero = true, CommandRating = 6, IsCommitted = committed,
            IsGroundCombatant = true, HeroRole = Game.Ai.V2.HeroOperationalRole.CombatLeader,
            MoveCurrent = 4, MoveMax = 4, NativeMoveMax = 4,
        };

        // Control case (no Transport): the garrison floor takes the most wounded body. With
        // Transport that body is the carrier, and taking it would slow the field army 4 -> 2.
        // The committed lead keeps the field army from folding whole into the garrison.
        [TestCase(true)]
        [TestCase(false)]
        public void HousekeepingDoesNotTakeACarrierWhoseDepartureSlowsItsFieldArmy(bool transport)
        {
            var carrier = ReorgBody(4, 4, transport: transport, hp: 1f);
            var passenger = ReorgBody(transport ? 4 : 2, 2, infantry: true,
                carrierId: transport ? carrier.RuntimeIdentity : -1);
            var garrison = new Game.Ai.V2.ReorgContainer
            {
                ArmyId = 1, Role = Game.Ai.V2.ReorgPhysicalRole.Garrison, IsGarrison = true,
                CanReceive = true, CanDonate = true, CanChangeComposition = true,
                CanReorderCommander = true, SingletonExempt = true, GarrisonNonHeroFloor = 1,
                Units = new System.Collections.Generic.List<Game.Ai.V2.ReorgUnit>(),
            };
            var field = new Game.Ai.V2.ReorgContainer
            {
                ArmyId = 2, Role = Game.Ai.V2.ReorgPhysicalRole.NormalFieldArmy,
                CanReceive = true, CanDonate = true, CanChangeComposition = true, CanReorderCommander = true,
                Units = new System.Collections.Generic.List<Game.Ai.V2.ReorgUnit>
                    { ReorgLead(committed: true), carrier, passenger, ReorgBody(4, 4), ReorgBody(4, 4) },
            };
            // A second viable field formation makes the hex worth a planning pass; it cannot donate.
            var other = new Game.Ai.V2.ReorgContainer
            {
                ArmyId = 3, Role = Game.Ai.V2.ReorgPhysicalRole.NormalFieldArmy,
                CanReceive = true, CanDonate = false, CanChangeComposition = true, CanReorderCommander = true,
                Units = new System.Collections.Generic.List<Game.Ai.V2.ReorgUnit> { ReorgLead(), ReorgBody(4, 4), ReorgBody(4, 4) },
            };
            var plan = Game.Ai.V2.ArmyReorganizationPlanner.Plan(new Game.Ai.V2.LocalForceGroup
            {
                Containers = new System.Collections.Generic.List<Game.Ai.V2.ReorgContainer> { garrison, field, other },
            });
            bool carrierTaken = plan.Transfers.Any(t => t.UnitKey == carrier.Key && t.FromArmyId == 2);
            Assert.That(carrierTaken, Is.EqualTo(!transport), plan.DebugSummary());
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
