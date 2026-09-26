#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Recon audit B2 — a live Attack's observation need on a KNOWN hostile site is served by Recon
    // as an ordinary Refresh, looked at from a vantage next to the site. The site itself is always a
    // visible-arrival block (its garrison would fight, an undefended one would be taken over), which
    // used to drop every such need before it became an objective.
    public class AiReconAttackObservationTests
    {
        private static readonly HexCoord Site = new HexCoord(6, 0);

        [TearDown]
        public void ClearState()
        {
            MissionIntentRegistry.Clear();
            ReconIntelSnapshotRegistry.Clear();
        }

        [Test]
        public void AttackNeedOnAKnownHostileSite_BecomesARefreshObjective()
        {
            WorldSnapshot snap = Scenario(out _);

            ReconObjective need = ReconObjectiveEvaluator.RefreshAt(snap, Site);

            Assert.That(need, Is.Not.Null);
            Assert.That(need.Kind, Is.EqualTo(ReconObjectiveKind.Refresh));
            Assert.That(need.FocusHex, Is.EqualTo(Site));
            Assert.That(ReconObjectiveEvaluator.Enumerate(snap)
                .Any(o => o.Kind == ReconObjectiveKind.Refresh && o.FocusHex.Equals(Site)), Is.True);
        }

        [Test]
        public void AttackNeedRefreshIntent_StaysValid()
        {
            WorldSnapshot snap = Scenario(out _);
            var intent = new ScoutIntent { Kind = ScoutTargetKind.Refresh, FocusHex = Site };

            Assert.That(ScoutObjectiveEvaluator.IsIntentStillValid(snap, intent), Is.True);
        }

        [Test]
        public void VantageRefresh_ExecutesFromAHexWithinVisionOfTheSite()
        {
            WorldSnapshot snap = Scenario(out ArmySnapshot scout);
            var refresh = new ScoutMissionTarget { Kind = ScoutTargetKind.Refresh, FocusHex = Site };
            var explore = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = Site };

            Assert.That(SurveilVantageSelector.UsesVantage(snap, refresh), Is.True);
            Assert.That(SurveilVantageSelector.UsesVantage(snap, explore), Is.False);
            HexCoord from = ReconAssignmentPlanner.ResolveExecutionHex(snap, scout, refresh);
            Assert.That(from, Is.Not.EqualTo(Site));
            Assert.That(HexGridMath.Distance(from, Site), Is.LessThanOrEqualTo(scout.EffectiveVisionRadius));
        }

        [Test]
        public void OrdinaryRefresh_StillExecutesOnItsFocus()
        {
            WorldSnapshot snap = Scenario(out ArmySnapshot scout);
            var refresh = new ScoutMissionTarget { Kind = ScoutTargetKind.Refresh, FocusHex = new HexCoord(3, 0) };

            Assert.That(SurveilVantageSelector.UsesVantage(snap, refresh), Is.False);
            Assert.That(ReconAssignmentPlanner.ResolveExecutionHex(snap, scout, refresh),
                Is.EqualTo(new HexCoord(3, 0)));
        }

        private static WorldSnapshot Scenario(out ArmySnapshot scout)
        {
            var player = new PlayerSetupData { Nickname = "Recon audit", ColorIndex = 1 };
            var enemy = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 3 };
            const int turn = 10;

            var attack = new MissionIntent
            {
                Kind = MissionKind.Attack,
                Status = IntentStatus.Active,
                Objective = new AttackIntent
                {
                    Target = AttackTargetRef.For(Site, enemy, AttackTargetKind.Citadel),
                    Phase = AttackMissionPhase.Assault,
                },
            };
            attack.IntentKey = MissionIntentKey.For(attack);
            MissionIntentRegistry.GetOrCreate(player).Put(attack);
            // Last looked at on turn 7: three turns old, older than attackIntelMaxAgeTurns.
            ReconIntelSnapshotRegistry.Capture(player, turn, knowledgeVersion: 0,
                new Dictionary<HexCoord, int> { { Site, 7 } });

            var hexes = new HashSet<HexCoord>();
            for (int q = -1; q <= 8; q++)
                for (int r = -2; r <= 2; r++)
                    hexes.Add(new HexCoord(q, r));

            scout = new ArmySnapshot
            {
                ArmyId = 10, Owner = player, Hex = new HexCoord(2, 0), IsSoloRecce = true,
                MemberCount = 1, CurrentMovement = 3, MaxMovement = 3, ActivationApCost = 1,
                EffectiveVisionRadius = 1,
            };
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Observer = player,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot> { scout },
                },
                Known = new KnownSnapshot
                {
                    Buildings = new List<AiMapMemory.KnownBuilding>
                    {
                        new AiMapMemory.KnownBuilding(Site, enemy, isStartingCitadel: true,
                            facilityAbilities: new List<string>()),
                    },
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    AllHexes = hexes.ToList(),
                    VisitedHexSet = new HashSet<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                    VisibleArrivalBlockedHexes = new HashSet<HexCoord> { Site },
                },
            };
        }
    }
}
#endif
