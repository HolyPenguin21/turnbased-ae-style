#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiStagingReturnTests
    {
        private static readonly PlayerSetupData Me = new PlayerSetupData();
        private static readonly PlayerSetupData Foe = new PlayerSetupData();

        private static WorldSnapshot Snap(float fist, float peak)
        {
            var enemy = new AiMapMemory.KnownBuilding(new HexCoord(6, 0), Foe, true, null, null, 0, true, 0f, 3);
            return new WorldSnapshot
            {
                Observer = Me,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(-4, 5),
                    BaseHexes = new List<HexCoord> { new HexCoord(-4, 5), new HexCoord(3, 0) },
                    FistPower = fist, AttackPeak = peak,
                },
                Known = new KnownSnapshot { Buildings = new List<AiMapMemory.KnownBuilding> { enemy } },
            };
        }

        [Test]
        public void StagingBaseIsTheOwnBaseNearestAKnownEnemyBase()
            => Assert.That(AiReturnBasePolicy.StagingBase(Snap(40f, 100f), Me), Is.EqualTo(new HexCoord(3, 0)));

        [Test]
        public void NoStagingBaseWhileTheFistIsTooWeak()
            => Assert.That(AiReturnBasePolicy.StagingBase(Snap(10f, 100f), Me), Is.Null);

        [Test]
        public void RaidReturnPrefersStagingButOtherLegsKeepTheirOwnRule()
        {
            WorldSnapshot snap = Snap(40f, 100f);
            Assert.That(AiReturnBasePolicy.SelectReturnBase(snap, Me, null, preferStaging: true),
                Is.EqualTo(new HexCoord(3, 0)));
            Assert.That(AiReturnBasePolicy.SelectReturnBase(snap, Me, null),
                Is.EqualTo(new HexCoord(-4, 5)), "tie on every rule: starting Citadel first");
        }
    }
}
#endif

