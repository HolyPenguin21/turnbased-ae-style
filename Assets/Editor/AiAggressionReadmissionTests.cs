#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // T03 — Aggression re-enters the typed strategic loop only when an input of its demand changed.
    public class AiAggressionReadmissionTests
    {
        private static ArmySnapshot Field() => new ArmySnapshot
        {
            ArmyId = 5, Hex = new HexCoord(1, 1), MemberCount = 3, EffectiveArmyPower = 20f,
            IsStructuralRaidActor = true, CurrentMovement = 3,
        };

        private static ArmySnapshot Scout() => new ArmySnapshot
        {
            ArmyId = 6, Hex = new HexCoord(3, 3), MemberCount = 1, EffectiveArmyPower = 2f,
            IsSoloRecce = true, CurrentMovement = 4,
        };

        private static WorldSnapshot Snapshot(ArmySnapshot field, ArmySnapshot scout,
            float peak = 60f) => new WorldSnapshot
        {
            TurnNumber = 7,
            Self = new SelfSnapshot
            {
                BaseHexes = new[] { new HexCoord(0, 0) },
                Armies = new[] { field, scout },
                TotalMilitaryPotential = peak,
            },
        };

        private static string Key(WorldSnapshot snap, PlayerSetupData player, int hand = 3) =>
            Pipeline.AggressionAdmissionFingerprint(snap, player, hand);

        [Test]
        public void ScoutStep_DoesNotReadmitAggression()
        {
            var player = new PlayerSetupData();
            ArmySnapshot scout = Scout();
            WorldSnapshot snap = Snapshot(Field(), scout);
            string before = Key(snap, player);
            scout.Hex = new HexCoord(4, 3);
            scout.CurrentMovement = 3;
            Assert.That(Key(snap, player), Is.EqualTo(before));
        }

        [Test]
        public void FieldArmyMoveDamageOrSpentMovement_ReadmitsAggression()
        {
            var player = new PlayerSetupData();
            ArmySnapshot field = Field();
            WorldSnapshot snap = Snapshot(field, Scout());
            string before = Key(snap, player);
            field.Hex = new HexCoord(2, 1);
            string moved = Key(snap, player);
            Assert.That(moved, Is.Not.EqualTo(before));
            field.EffectiveArmyPower = 14f;
            string damaged = Key(snap, player);
            Assert.That(damaged, Is.Not.EqualTo(moved));
            field.CurrentMovement = 0;
            Assert.That(Key(snap, player), Is.Not.EqualTo(damaged));
        }

        [Test]
        public void PeakOrHandChange_ReadmitsAggression()
        {
            var player = new PlayerSetupData();
            string before = Key(Snapshot(Field(), Scout()), player);
            Assert.That(Key(Snapshot(Field(), Scout(), peak: 75f), player), Is.Not.EqualTo(before));
            Assert.That(Key(Snapshot(Field(), Scout()), player, hand: 4), Is.Not.EqualTo(before));
        }
    }
}
#endif
