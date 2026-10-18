#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiMobileRepairTests
    {
        private static readonly HexCoord Field = new HexCoord(4, 3);
        private WorldSnapshot Snapshot(bool healthy = false, bool prisoner = false, bool initialized = true)
        {
            var owner = new PlayerSetupData();
            var current = new WorthIt.DefenderProfile(1, false, attack: 4,
                hitPoints: healthy ? 6 : 2, maxHitPoints: 6);
            var full = new WorthIt.DefenderProfile(1, false, attack: 4, hitPoints: 6, maxHitPoints: 6);
            return new WorldSnapshot { Observer = owner, Self = new SelfSnapshot {
                ActionPoints = 3, Stockpile = new ResourceBundle { Materials = 2 },
                BaseHexes = Array.Empty<HexCoord>(), Armies = new[] {
                    new ArmySnapshot { ArmyId = 1, Owner = owner, Hex = Field, RecoveryMembers = new[] {
                        new RaidRecoveryMemberSnapshot(12, 0, false, false, true, false, 1,
                            current, full, initialized, new ResourceVector(0, 0, 0, 2, 0), prisoner) } } } } };
        }
        [Test] public void RegistryClassifiesRepairAsPersistentSupportWithoutBattlePowerInflation()
        {
            var effects = StrategicEffectRegistry.Resolve(new[] { UnitAbilities.Repair }, 0);
            Assert.That(effects.Count, Is.EqualTo(1));
            Assert.That(effects[0].Context, Is.EqualTo(StrategicEffectContext.MobileRepair));
            Assert.That(effects[0].Timing, Is.EqualTo(EffectTiming.Persistent));
            Assert.That(StrategicEffectRegistry.HasArmySupportEffect(new[] { UnitAbilities.Repair }, 0), Is.True);
            var plain = new Game.Units.UnitData { Attack = 3, HitPointsCurrent = 4, HitPointsMax = 4 };
            float before = AiPower.StatLinePower(plain); plain.Abilities.Add(UnitAbilities.Repair);
            Assert.That(AiPower.StatLinePower(plain), Is.EqualTo(before));
        }
        [Test] public void ServiceUsesWoundedUnitsInEveryOwnArmyOnTheDeploymentHex()
        {
            var snap = Snapshot(); var plan = new MaterializationPlan {
                BaseCardInHand = new CardData(new CardDefinition { cardType = CardType.Unit }),
                Deploy = new PlacementOption(Field, DeploymentKind.NewArmy, null) };
            Assert.That(new EffectEvaluationContext(snap, plan).MobileRepairService,
                Is.EqualTo(2f / 3f).Within(0.0001));
            snap.Self.Armies[0].Hex = new HexCoord(9, 9);
            Assert.That(EffectEvaluationContext.RepairServiceAt(snap, Field), Is.Zero);
        }
        [Test] public void AircraftCannotEarnGroundMechanicServiceValue()
        {
            var snap = Snapshot(); var plan = new MaterializationPlan {
                BaseCardInHand = new CardData(new CardDefinition { cardType = CardType.Unit, isAviation = true }),
                Deploy = new PlacementOption(Field, DeploymentKind.NewArmy, null) };
            Assert.That(new EffectEvaluationContext(snap, plan).MobileRepairService, Is.Zero);
        }
        [TestCase("base")][TestCase("otherMechanic")][TestCase("foreign")][TestCase("prison")]
        [TestCase("resources")][TestCase("ap")][TestCase("reserved")]
        public void RedundantOrUnusableRepairDoesNotEarnServiceValue(string reason)
        {
            var snap = Snapshot(); var army = snap.Self.Armies[0];
            switch (reason) {
                case "base": snap.Self.BaseHexes = new[] { Field }; break;
                case "otherMechanic": army.RepairSourceCount = 1; break;
                case "foreign": army.Owner = new PlayerSetupData(); break;
                case "prison": army.IsPrison = true; break;
                case "resources": snap.Self.Stockpile = default; break;
                case "ap": snap.Self.ActionPoints = 0; break;
                case "reserved": snap.Economy = new EconomyStanding { SpendableStockpile = default }; break;
            }
            Assert.That(EffectEvaluationContext.RepairServiceAt(snap, Field), Is.Zero);
        }
        [TestCase(true, false, true)][TestCase(false, true, true)][TestCase(false, false, false)]
        public void HealthyPrisonerAndUninitializedTargetsContributeNothing(bool healthy, bool prisoner, bool initialized)
        {
            Assert.That(EffectEvaluationContext.RepairServiceAt(Snapshot(healthy, prisoner, initialized), Field), Is.Zero);
        }
        [Test] public void AttachmentReplacementExcludesOnlyItsOwnSource()
        {
            var snap = Snapshot(); snap.Self.Armies[0].RepairSourceCount = 1;
            Assert.That(EffectEvaluationContext.RepairServiceAt(snap, Field, 1), Is.GreaterThan(0));
            snap.Self.Armies[0].RepairSourceCount = 2;
            Assert.That(EffectEvaluationContext.RepairServiceAt(snap, Field, 1), Is.Zero);
        }
        [Test] public void SourceCapabilityChangesPublishActorAndCapabilityInvalidations()
        {
            var owner = new PlayerSetupData(); using var session = AiTurnSession.Begin(owner, null, null, null, 3);
            WorldSnapshot Read(int count) => new WorldSnapshot { Observer = owner, Self = new SelfSnapshot {
                Armies = new[] { new ArmySnapshot { ArmyId = 8, Hex = Field, RepairSourceCount = count } } } };
            WorldAnalysis.PublishStepObservationDelta(owner, 3,
                new WorldAnalysis.StepObservationStamp(Read(1), default, null),
                new WorldAnalysis.StepObservationStamp(Read(0), default, null), null);
            Assert.That(session.PendingInvalidations.Reasons.HasFlag(StrategicInvalidationReason.Actor), Is.True);
            Assert.That(session.PendingInvalidations.Reasons.HasFlag(StrategicInvalidationReason.Capability), Is.True);
            Assert.That(session.PendingInvalidations.ActorIds, Does.Contain(8));
        }
    }
}
#endif
