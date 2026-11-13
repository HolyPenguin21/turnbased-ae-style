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

            Assert.That(ObservationVantageSelector.UsesVantage(snap, refresh), Is.True);
            Assert.That(ObservationVantageSelector.UsesVantage(snap, explore), Is.False);
            HexCoord from = ReconAssignmentPlanner.ResolveExecutionHex(snap, scout, refresh);
            Assert.That(from, Is.Not.EqualTo(Site));
            Assert.That(HexGridMath.Distance(from, Site), Is.LessThanOrEqualTo(scout.EffectiveVisionRadius));
        }

        [Test]
        public void OrdinaryRefresh_StillExecutesOnItsFocus()
        {
            WorldSnapshot snap = Scenario(out ArmySnapshot scout);
            var refresh = new ScoutMissionTarget { Kind = ScoutTargetKind.Refresh, FocusHex = new HexCoord(3, 0) };

            Assert.That(ObservationVantageSelector.UsesVantage(snap, refresh), Is.False);
            Assert.That(ReconAssignmentPlanner.ResolveExecutionHex(snap, scout, refresh),
                Is.EqualTo(new HexCoord(3, 0)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExploreScore_OperationalNeedOutweighsTheSameCellWithoutTheNeed(bool ruins)
        {
            WorldSnapshot snap = Scenario(out _);
            ReconIntelSnapshotRegistry.Clear();
            snap.MapKnowledge.UnvisitedRuinsHexes = ruins
                ? new HashSet<HexCoord> { Site } : new HashSet<HexCoord>();
            var cost = new ScoutCostEstimate { EtaTurns = 2, ActivationApNow = 1f };
            TaskScore needed = ReconObjectiveEvaluator.BuildExploreScore(snap, Site, 0.5f, 6, cost, 0f);
            snap.Intents = new MissionIntentState();
            TaskScore ordinary = ReconObjectiveEvaluator.BuildExploreScore(snap, Site, 0.5f, 6, cost, 0f);
            Assert.That(needed.StrategicRelevance, Is.EqualTo(TaskScoreEvaluator.StrategicRelevance(1f)));
            Assert.That(ordinary.StrategicRelevance, Is.LessThan(needed.StrategicRelevance));
            Assert.That(needed.CardPrice, Is.EqualTo(ordinary.CardPrice));
            Assert.That(needed.InfoGain, Is.EqualTo(ordinary.InfoGain));
            Assert.That(needed.OwnTerritoryProximity, Is.EqualTo(ordinary.OwnTerritoryProximity));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NeverObservedAttackNeed_UsesFullRelevanceWithoutAddingTheRuinsBonus(bool ruins)
        {
            WorldSnapshot snap = Scenario(out _);
            ReconIntelSnapshotRegistry.Clear();
            snap.MapKnowledge.VisibleArrivalBlockedHexes.Clear();
            snap.MapKnowledge.UnvisitedRuinsHexes = ruins
                ? new HashSet<HexCoord> { Site } : new HashSet<HexCoord>();
            ReconObjective objective = ReconObjectiveEvaluator.ExploreAt(snap, Site);
            Assert.That(objective, Is.Not.Null);
            Assert.That(objective.TaskScore.StrategicRelevance,
                Is.EqualTo(TaskScoreEvaluator.StrategicRelevance(1f)));
            Assert.That(ReconObjectiveEvaluator.Enumerate(snap).Any(o =>
                o.Kind == ReconObjectiveKind.Explore && o.FocusHex.Equals(Site)
                    && o.TaskScore.StrategicRelevance == objective.TaskScore.StrategicRelevance), Is.True);
            MissionIntentRegistry.Clear();
            snap.Intents = MissionIntentRegistry.GetOrCreate(snap.Observer);
            ReconObjective ordinary = ReconObjectiveEvaluator.ExploreAt(snap, Site);
            Assert.That(ordinary.TaskScore.StrategicRelevance,
                Is.EqualTo(TaskScoreEvaluator.StrategicRelevance(ruins ? AiConfigV2.reconRuinsRelevance : 0f)),
                "after the need ends the same cell reverts to ordinary exploration");
        }

        // Match 2026-11-09 P1 (Brynn T12–T15) — the never-seen enemy citadel's Explore had InfoGain
        // 7.9 (its ring was seen recently, ExploreObservationFreshnessFactor) and lost to home
        // Refreshes. An observation need that was never seen is maximally unknown: full InfoGain.
        [Test]
        public void NeverObservedAttackNeed_GetsFullInfoGainAlthoughItsRingWasSeen()
        {
            WorldSnapshot snap = Scenario(out _);
            ReconIntelSnapshotRegistry.Clear();
            snap.MapKnowledge.VisibleArrivalBlockedHexes.Clear();
            var ring = HexGridMath.Neighbors(Site).ToDictionary(h => h, _ => snap.TurnNumber);
            ReconIntelSnapshotRegistry.Capture(snap.Observer, snap.TurnNumber, snap.KnowledgeVersion, ring);

            ReconObjective needed = ReconObjectiveEvaluator.ExploreAt(snap, Site);
            Assert.That(needed, Is.Not.Null);
            Assert.That(needed.TaskScore.InfoGain, Is.EqualTo(TaskScoreEvaluator.InfoGain(1f)));

            MissionIntentRegistry.Clear();
            snap.Intents = MissionIntentRegistry.GetOrCreate(snap.Observer);
            ReconObjective ordinary = ReconObjectiveEvaluator.ExploreAt(snap, Site);
            Assert.That(ordinary.TaskScore.InfoGain, Is.LessThan(needed.TaskScore.InfoGain),
                "the same cell without the need keeps the freshness discount");
            Assert.That(needed.BaseValue, Is.GreaterThan(ordinary.BaseValue));
        }

        // Match 2026-11-09 P1 (Brynn T14, line 12956) — scout #15 two hexes from the citadel was
        // retired as the stalest surplus lane. A lane on a live observation need is never surplus.
        [Test]
        public void SurplusTrim_KeepsTheLaneOnAnObservationNeed()
        {
            WorldSnapshot snap = Scenario(out _);
            Assert.That(AttackObjectiveEvaluator.ObservationNeeds(snap), Does.Contain(Site));
            snap.Self.Armies = new List<ArmySnapshot>
            {
                new ArmySnapshot { ArmyId = 15, Owner = snap.Observer, Hex = new HexCoord(4, 0),
                    IsSoloRecce = true, MemberCount = 1, MaxMovement = 2, CurrentMovement = 2 },
                new ArmySnapshot { ArmyId = 17, Owner = snap.Observer, Hex = new HexCoord(1, 0),
                    IsSoloRecce = true, MemberCount = 1, MaxMovement = 2, CurrentMovement = 2 },
            };
            MissionIntent Lane(ScoutTargetKind kind, HexCoord focus, int mover, int lastProgress)
            {
                var lane = new MissionIntent
                {
                    Kind = MissionKind.Scout, Status = IntentStatus.Active, Funding = CommitmentTier.None,
                    Objective = new ScoutIntent { Kind = kind, FocusHex = focus },
                    PreferredMoverArmyId = mover, LastProgressTurn = lastProgress, CreatedTurn = 1,
                };
                lane.IntentKey = MissionIntentKey.For(lane);
                return lane;
            }
            MissionIntent need = Lane(ScoutTargetKind.Explore, Site, 15, lastProgress: 5);
            MissionIntent home = Lane(ScoutTargetKind.Refresh, new HexCoord(1, 1), 17, lastProgress: 9);
            var state = new MissionIntentState();
            state.Put(need); state.Put(home);
            var active = new List<MissionIntent> { need, home };
            var objectives = new List<ReconObjective>
            {
                new ReconObjective { Kind = ReconObjectiveKind.Refresh, FocusHex = new HexCoord(1, 1), BaseValue = 12f },
            };

            ReconContinuity.TrimSurplusReconLanes(snap.Observer, active, state, snap, objectives);

            Assert.That(active, Is.EqualTo(new[] { need }),
                "desired 1: the home Refresh is shed, the citadel lane keeps its scout");
        }

        // Match 2026-11-13 P1 (Korrin T29) — the observation of a LIVE Attack's target keeps
        // Attack's priority (the DisruptAt precedent): Refresh(1,-6) scored 7.1 and lost scout #21
        // one hex away to its routine Refresh lane at 12.3.
        [Test]
        public void LiveAttackObservationNeed_CarriesAttackReadiness()
        {
            WorldSnapshot snap = Scenario(out _);
            ReconObjective need = ReconObjectiveEvaluator.RefreshAt(snap, Site);
            Assert.That(need, Is.Not.Null);
            Assert.That(need.TaskScore[TaskSlot.AttackReadiness], Is.GreaterThan(0f));

            MissionIntentRegistry.Clear();
            snap.Intents = MissionIntentRegistry.GetOrCreate(snap.Observer);
            ReconObjective plain = ReconObjectiveEvaluator.RefreshAt(snap, Site);
            Assert.That(plain == null || plain.TaskScore[TaskSlot.AttackReadiness] == 0f, Is.True,
                "without a live Attack the same Refresh carries no Attack priority");
        }

        // Match 2026-11-13 P1 (Korrin T30) — #21 one step from the need watched another hex; the
        // 2026-11-09 guard compared lane FocusHex only, so its lane was trimmed and the need went to
        // far #24. The scout best placed for a need no lane looks at keeps its lane.
        [Test]
        public void SurplusTrim_KeepsTheLaneOfTheScoutBestPlacedForAnUnservedNeed()
        {
            WorldSnapshot snap = Scenario(out _);
            snap.Self.Armies = new List<ArmySnapshot>
            {
                new ArmySnapshot { ArmyId = 21, Owner = snap.Observer, Hex = new HexCoord(5, 0),
                    IsSoloRecce = true, MemberCount = 1, MaxMovement = 2, CurrentMovement = 2,
                    EffectiveVisionRadius = 1 },
                new ArmySnapshot { ArmyId = 24, Owner = snap.Observer, Hex = new HexCoord(0, 1),
                    IsSoloRecce = true, MemberCount = 1, MaxMovement = 2, CurrentMovement = 2,
                    EffectiveVisionRadius = 1 },
            };
            MissionIntent Lane(HexCoord focus, int mover, int lastProgress)
            {
                var lane = new MissionIntent
                {
                    Kind = MissionKind.Scout, Status = IntentStatus.Active, Funding = CommitmentTier.None,
                    Objective = new ScoutIntent { Kind = ScoutTargetKind.Refresh, FocusHex = focus },
                    PreferredMoverArmyId = mover, LastProgressTurn = lastProgress, CreatedTurn = 1,
                };
                lane.IntentKey = MissionIntentKey.For(lane);
                return lane;
            }
            MissionIntent near = Lane(new HexCoord(4, 2), 21, lastProgress: 5);
            MissionIntent far = Lane(new HexCoord(0, 2), 24, lastProgress: 9);
            var state = new MissionIntentState();
            state.Put(near); state.Put(far);
            var active = new List<MissionIntent> { near, far };
            var objectives = new List<ReconObjective>
            {
                new ReconObjective { Kind = ReconObjectiveKind.Refresh, FocusHex = new HexCoord(0, 2), BaseValue = 12f },
            };

            ReconContinuity.TrimSurplusReconLanes(snap.Observer, active, state, snap, objectives);

            Assert.That(active, Is.EqualTo(new[] { near }),
                "#21 is the best ETA for the unserved need: its (older) lane stays, the far one goes");
        }

        [Test]
        public void StaleTargetCanRequestObservationBeforeAnAttackIntentExists()
        {
            WorldSnapshot snap = Scenario(out _);
            var store = new MissionIntentState();
            var recon = new MissionIntent
            {
                Kind = MissionKind.Scout, Status = IntentStatus.Active, PreferredMoverArmyId = 10,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Refresh, FocusHex = Site },
            };
            recon.IntentKey = MissionIntentKey.For(recon); store.Put(recon);
            snap.Intents = store;
            snap.Self.AttackPeak = 100f;
            snap.Self.Armies = new[] { new ArmySnapshot
            {
                ArmyId = 81, Owner = snap.Observer, Hex = default, IsStructuralRaidActor = true,
                EffectiveArmyPower = 100f, MemberCount = 1, MaxMovement = 3, CurrentMovement = 3,
                Members = new[] { new Game.Combat.WorthIt.DefenderProfile(10f, false, attack: 20f, hitPoints: 20f) },
            } };
            Assert.That(AttackObjectiveEvaluator.ObservationNeeds(snap).ToArray(), Is.EqualTo(new[] { Site }));
            ReconIntelSnapshotRegistry.Clear();
            ReconIntelSnapshotRegistry.Capture(snap.Observer, snap.TurnNumber, snap.KnowledgeVersion,
                new Dictionary<HexCoord, int> { { Site, snap.TurnNumber } });
            Assert.That(AttackObjectiveEvaluator.ObservationNeeds(snap), Is.Empty);
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
                Intents = MissionIntentRegistry.GetOrCreate(player),
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
