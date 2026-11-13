#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-13 (owner decision, match Korrin vs Brynn P1) — a structure-defended enemy Base /
    // Citadel is assaulted with Disruption, and the delivery a fist entering this turn backs
    // ignores detectors (game rule: a fully hidden army starts no contact and holds nobody even
    // when detected — BattleInitiator.CanInitiateContact / IsHeldByContact).
    public sealed class AiAssaultDisruptionGateTests
    {
        private static readonly HexCoord Site = new HexCoord(3, 0);
        private PlayerSetupData _player;
        private PlayerSetupData _enemy;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiMapMemory.Clear();
            _player = new PlayerSetupData();
            _enemy = new PlayerSetupData();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiMapMemory.Clear();
        }

        private AttackTargetRef Target => AttackTargetRef.For(Site, _enemy, AttackTargetKind.Base);

        [Test]
        public void StructureDefendedBase_NeedsACarrierOnTheHexOrInTheFist()
        {
            ArmySnapshot carrier = Scout(2, true);
            WorldSnapshot snap = Snapshot(carrier);
            ArmySnapshot fist = Fist(snap);

            Assert.That(AttackObjectiveEvaluator.HasAssaultDisruption(snap, Target, fist, out string why), Is.False);
            Assert.That(why, Is.EqualTo("awaiting_disruption_carrier"));

            carrier.Hex = Site;
            Assert.That(AttackObjectiveEvaluator.HasAssaultDisruption(snap, Target, fist, out _), Is.True,
                "the carrier stands on the target");

            carrier.Hex = new HexCoord(1, 0);
            fist.MembersWithHeroes = new[] { Profile(true) };
            Assert.That(AttackObjectiveEvaluator.HasAssaultDisruption(snap, Target, fist, out _), Is.True,
                "the fist carries its own");
        }

        [Test]
        public void NoCarrierInThePool_FallsBackToTheObservationRule()
        {
            WorldSnapshot snap = Snapshot();
            snap.Self.PoolCards = Array.Empty<(CardDefinition, CardDefinition, CardDefinition, bool)>();
            Assert.That(AttackObjectiveEvaluator.DisruptionCarrierInPool(snap), Is.False);
            Assert.That(AttackObjectiveEvaluator.HasAssaultDisruption(snap, Target, Fist(snap), out _), Is.True);

            var card = new CardDefinition { cardType = CardType.Unit };
            card.grantedAbilities.Add(UnitAbilities.Disruption);
            snap.Self.PoolCards = new[] { (card, (CardDefinition)null, (CardDefinition)null, false) };
            Assert.That(AttackObjectiveEvaluator.DisruptionCarrierInPool(snap), Is.True, "a carrier in the deck");
            Assert.That(AttackObjectiveEvaluator.HasAssaultDisruption(snap, Target, Fist(snap), out _), Is.False);
        }

        [Test]
        public void NoStructureDefence_NoDisruptionNeeded()
        {
            WorldSnapshot snap = Snapshot(Scout(2, true));
            snap.Known.Buildings = new[] { new AiMapMemory.KnownBuilding(Site, _enemy, true,
                Array.Empty<string>(), isBase: true, defense: 0f) };
            Assert.That(AttackObjectiveEvaluator.HasAssaultDisruption(snap, Target, Fist(snap), out _), Is.True);
        }

        // Korrin T29: Cinderfall walked next to the Base on stale intel and stood there three turns.
        // The step to distance 1 waits until the carrier on its leg arrives no later than the fist.
        [Test]
        public void ApproachWaitsForACarrierThatArrivesNoLaterThanTheFist()
        {
            ArmySnapshot carrier = Scout(2, true);
            WorldSnapshot snap = Snapshot(carrier);
            ArmySnapshot fist = Fist(snap);
            Assert.That(AttackMarchPolicy.MayApproachAssault(snap, fist, Target, out string why), Is.False);
            Assert.That(why, Is.EqualTo("awaiting_disruption_carrier_before_approach"));

            MissionIntentRegistry.GetOrCreate(_player).Put(DisruptLeg(2));
            Assert.That(AttackMarchPolicy.MayApproachAssault(snap, fist, Target, out _), Is.True,
                "the counted carrier reaches the target as soon as the fist");
        }

        // The game rule (owner): the hidden carrier enters even under a detector when the fist
        // enters behind it this very turn. Without a ready fist the detector rule stays.
        [Test]
        public void DeliveryBackedByAFistEnteringThisTurn_IgnoresDetectors()
        {
            ArmySnapshot carrier = Scout(2, true);
            WorldSnapshot snap = Snapshot(carrier);
            snap.Known.EnemySightings = Enumerable.Range(0, (int)Math.Ceiling(AiConfigV2.scoutDetectionRiskNorm))
                .Select(id => new AiMapMemory.KnownEnemySighting(Site, _enemy, "detector", 1, 1f, 1f,
                    new[] { Profile(false) }, armyId: 90 + id)).ToList();
            Assert.That(ScoutRiskModel.DetectorRisk(snap, Site), Is.EqualTo(1f));
            Assert.That(ReconAssignmentPlanner.CanDeliverDisruptionThisTurn(snap, carrier, Site), Is.False,
                "no live Attack: the detector still refuses the delivery");

            MissionIntentRegistry.GetOrCreate(_player).Put(LiveAssault());
            Assert.That(AttackSupportProjection.DetectorIgnoredForDelivery(snap, Site), Is.True);
            Assert.That(ReconAssignmentPlanner.CanDeliverDisruptionThisTurn(snap, carrier, Site), Is.True);

            MissionIntentRegistry.GetOrCreate(_player).Put(DisruptLeg(2));
            Assert.That(AttackSupportProjection.CountedCarrier(snap, Site, armyEta: 0), Is.EqualTo(2),
                "counted for the fist that enters this turn");
            Assert.That(AttackSupportProjection.CountedCarrier(snap, Site, armyEta: 1), Is.Null,
                "a fist a turn away gets no detector exception");

            Fist(snap).CurrentMovement = 0;
            Assert.That(AttackSupportProjection.DetectorIgnoredForDelivery(snap, Site), Is.False,
                "a fist that cannot enter this turn backs no detector-blind delivery");
        }

        private MissionIntent LiveAssault()
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Objective = new AttackIntent { Target = Target, Phase = AttackMissionPhase.Assault, PrimaryArmyId = 10 },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        private static MissionIntent DisruptLeg(int mover) => new MissionIntent
        {
            Kind = MissionKind.Scout, Status = IntentStatus.Active,
            Objective = new ScoutIntent { Kind = ScoutTargetKind.DisruptStructure, FocusHex = Site, RequiresStealth = true },
            PreferredMoverArmyId = mover,
            IntentKey = MissionIntentKey.ForScout(ScoutTargetKind.DisruptStructure, Site),
        };

        private static ArmySnapshot Fist(WorldSnapshot snap) => snap.Self.Armies.First(a => a.ArmyId == 10);

        private ArmySnapshot Scout(int id, bool carrier) => new ArmySnapshot
        {
            ArmyId = id, Owner = _player, Hex = new HexCoord(1, 0), IsSoloRecce = true,
            MemberCount = 1, Members = new[] { Profile(carrier) }, MembersWithHeroes = new[] { Profile(carrier) },
            CurrentMovement = 3, MaxMovement = 3, IsHidden = true, CanEnterStealth = true,
            StealthLevel = 4, ActivationApCost = 1, EffectiveVisionRadius = 1,
        };

        private static WorthIt.DefenderProfile Profile(bool carrier) => new WorthIt.DefenderProfile(
            1f, false, attack: 1f, hitPoints: 4f,
            abilities: carrier ? new[] { UnitAbilities.Disruption, "r1s0", "Stealth4" }
                : new[] { "r1s0", "Stealth4" });

        private WorldSnapshot Snapshot(params ArmySnapshot[] scouts)
        {
            var armies = scouts.ToList();
            armies.Add(new ArmySnapshot { ArmyId = 10, Owner = _player, Hex = new HexCoord(1, 1),
                MemberCount = 3, MaxMovement = 3, CurrentMovement = 3, EffectiveArmyPower = 40f,
                IsStructuralRaidActor = true });
            return new WorldSnapshot {
                Observer = _player, TurnNumber = 10, Intents = MissionIntentRegistry.GetOrCreate(_player),
                Self = new SelfSnapshot { Armies = armies,
                    AttackPeak = 40f, BaseHexes = new[] { new HexCoord(0, 0) }, Citadel = new HexCoord(0, 0) },
                Known = new KnownSnapshot { Buildings = new[] { new AiMapMemory.KnownBuilding(Site, _enemy,
                    true, Array.Empty<string>(), isBase: true, defense: 10f) } },
                MapKnowledge = new MapKnowledgeSnapshot {
                    AllHexes = HexGridMath.HexesInRange(default, 5).ToList(),
                    VisitedHexSet = new HashSet<HexCoord> { Site },
                    VisibleArrivalBlockedHexes = new HashSet<HexCoord> { Site },
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                },
            };
        }
    }
}
#endif
