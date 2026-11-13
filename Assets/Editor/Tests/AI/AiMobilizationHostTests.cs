#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-13 (P3, match Korrin vs Brynn) — Korrin T25-T31 held its mobilization on
    // `host_is_pinned_stronghold_defender`: the host was the strongest army (#30, 36.4) pinned on
    // Base (0,-1) while Cinderfall #37 (36.2) already stood on the staging Base (-1,-3).
    public class AiMobilizationHostTests
    {
        private static readonly PlayerSetupData Us = new PlayerSetupData { Nickname = "Us" };
        private static readonly HexCoord Front = new HexCoord(0, -1);
        private static readonly HexCoord Staging = new HexCoord(-1, -3);

        private static ArmySnapshot Field(int id, HexCoord hex, float power) => new ArmySnapshot
        {
            ArmyId = id, Owner = Us, Hex = hex, EffectiveArmyPower = power,
            IsStructuralRaidActor = true, CurrentMovement = 3, MaxMovement = 3, MemberCount = 4,
        };

        private static WorldSnapshot Snap(int? enemyEtaToFront, params ArmySnapshot[] armies)
        {
            var threats = new List<AssetThreatSnapshot>();
            if (enemyEtaToFront.HasValue)
                threats.Add(new AssetThreatSnapshot
                {
                    Asset = new StrategicAssetSnapshot { Hex = Front, Kind = AssetKind.Base },
                    Contact = new EnemyContactSnapshot
                    {
                        Army = new ArmySnapshot { ArmyId = 24, Owner = new PlayerSetupData() },
                        Position = new HexCoord(1, -2),
                    },
                    CanDamage = true, EnemyEta = enemyEtaToFront,
                });
            return new WorldSnapshot
            {
                Observer = Us,
                Self = new SelfSnapshot { Armies = armies, BaseHexes = new[] { Front, Staging } },
                Threat = new ThreatModel { Threats = threats },
            };
        }

        [Test]
        public void PinnedDefenderIsNotTheHost_TheArmyOnTheStagingBaseIs()
        {
            ArmySnapshot pinned = Field(30, Front, 36.4f);
            ArmySnapshot cinderfall = Field(37, Staging, 36.2f);
            WorldSnapshot snap = Snap(1, pinned, cinderfall);
            Assert.That(ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, pinned), Is.True);

            ArmySnapshot host = AggressionMissionLayer.PreparationFieldHost(snap, new HashSet<int>(), Staging);
            Assert.That(host?.ArmyId, Is.EqualTo(37));
        }

        [Test]
        public void WithoutAThreat_TheStrongestArmyStillHosts()
        {
            WorldSnapshot snap = Snap(null, Field(30, Front, 36.4f), Field(37, Staging, 36.2f));
            Assert.That(AggressionMissionLayer.PreparationFieldHost(snap, new HashSet<int>(), Staging)?.ArmyId,
                Is.EqualTo(30));
        }

        [Test]
        public void APinnedArmyAlreadyOnTheStagingBase_MayHost()
        {
            ArmySnapshot onStaging = Field(30, Front, 36.4f);
            WorldSnapshot snap = Snap(1, onStaging);
            Assert.That(AggressionMissionLayer.PreparationFieldHost(snap, new HashSet<int>(), Front)?.ArmyId,
                Is.EqualTo(30), "it gathers where it stands: no walk off the stronghold");
            Assert.That(AggressionMissionLayer.PreparationFieldHost(snap, new HashSet<int>(), Staging),
                Is.Null, "pinned away from the staging Base: no host");
        }
    }
}
#endif
