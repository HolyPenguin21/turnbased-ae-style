#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiEquipmentPriorityTests
    {
        private PlayerSetupData player;
        private CardDefinition infantry;

        [SetUp] public void Setup()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); MissionIntentRegistry.Clear();
            DevelopmentInvestmentGate.Clear(); StrategicTempoBudget.ClearAll();
            player = new PlayerSetupData();
            infantry = new CardDefinition { authoredKey = "test.infantry", displayName = "Infantry",
                cardType = CardType.Unit, attack = 5, defenseRating = 2, hitPoints = 8, moveMax = 3 };
        }
        [TearDown] public void Cleanup()
        { ArmyRegistry.Clear(); BuildingRegistry.Clear(); MissionIntentRegistry.Clear();
            DevelopmentInvestmentGate.Clear(); StrategicTempoBudget.ClearAll(); }

        private UnitData Body(int attack) => new UnitData { Owner = player, OriginatingCard = infantry,
            Attack = attack, Defense = 2, HitPointsCurrent = 8, HitPointsMax = 8, MoveMax = 3, MoveCurrent = 3 };
        private ArmyData Army(params UnitData[] members)
        {
            var a = new ArmyData { Owner = player }; a.Members.AddRange(members); ArmyRegistry.Register(a); return a;
        }
        private WorldSnapshot Snap(params CardData[] hand) => new WorldSnapshot {
            Observer = player, Intents = MissionIntentRegistry.GetOrCreate(player),
            Self = new SelfSnapshot { Hand = hand, Armies = ArmyRegistry.AllForOwner(player)
                .Select(a => WorldAnalysis.ToArmySnapshot(a, player, true, 0, null)).ToList() } };
        private MissionIntent Preparation(int hostId, int copies)
        {
            var attack = new AttackIntent { PrimaryArmyId = hostId, Preparation = true,
                Phase = AttackMissionPhase.Gather, TargetRoster = new System.Collections.Generic.List<StrikeRosterSlot>(),
                Target = AttackTargetRef.For(new HexCoord(4, 0), new PlayerSetupData(), AttackTargetKind.Base) };
            attack.TargetRoster.AddRange(Enumerable.Range(0, copies)
                .Select(_ => new StrikeRosterSlot(infantry.authoredKey, false, 10f, ForceSource.Map)));
            var intent = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Objective = attack, IntentKey = MissionIntentKey.ForAttack(attack.Target) };
            MissionIntentRegistry.GetOrCreate(player).Put(intent); return intent;
        }

        [Test]
        public void PreparationCountsSpecificCopiesAndPrefersTheAlreadyAssignedHost()
        {
            var onHost = Body(1); var strong = Body(8); var spare = Body(4);
            var host = Army(onHost); Army(strong); Army(spare);
            var intent = Preparation(host.Id, 2); var snap = Snap(new CardData(infantry));
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(snap, null, onHost), Is.SameAs(intent));
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(snap, null, strong), Is.SameAs(intent));
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(snap, null, spare), Is.Null);
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(snap, snap.Self.Hand.Single(), null), Is.Null);
            snap.Self.MobilizationHeld = true; snap.Self.StrikeRoster = intent.Attack.TargetRoster;
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(snap, null, onHost), Is.True);
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(snap, null, strong), Is.True);
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(snap, null, spare), Is.False,
                "the mobilized peak must count its already assigned copy, not request another full set");
        }

        [Test]
        public void OnlyTheMissingNumberOfHandCopiesReceivesPreparationPurpose()
        {
            var host = Army(Body(2)); var first = new CardData(infantry); var extra = new CardData(infantry);
            var intent = Preparation(host.Id, 2); var snap = Snap(first, extra);
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(snap, first, null), Is.SameAs(intent));
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(snap, extra, null), Is.Null);
            intent.Status = IntentStatus.Suspended;
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(snap, first, null), Is.Null);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void OnlyOptionalPositiveEquipmentGainYieldsToMissileCompetition(bool missile, bool required)
        {
            var gain = new EquipmentUpgradeValuation.EquipmentDelta(4f, -2f, "signed");
            var calibrated = EquipmentUpgradeValuation.CalibrateRecipientGain(gain, required, missile);
            Assert.That(calibrated.Tactical, Is.EqualTo(-2f), "losses must not be hidden by a discount");
            Assert.That(calibrated.Combat, missile && !required ? Is.LessThan(4f) : Is.EqualTo(4f));
            var veto = new EquipmentUpgradeValuation.EquipmentDelta(-1.5f, 0f, "reject");
            Assert.That(EquipmentUpgradeValuation.CalibrateRecipientGain(veto, required, missile).Total,
                Is.EqualTo(veto.Total));
        }

        [Test]
        public void ExistingScoutDutiesAreRequiredEquipmentRecipients()
        {
            var scout = Body(3); var army = Army(scout);
            var intent = new MissionIntent { Kind = MissionKind.Scout, PreferredMoverArmyId = army.Id,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.DisruptStructure, FocusHex = new HexCoord(3, 0) },
                Status = IntentStatus.Active };
            intent.IntentKey = MissionIntentKey.For(intent); MissionIntentRegistry.GetOrCreate(player).Put(intent);
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(Snap(), null, scout), Is.True);
        }

        [Test]
        public void HeldGatherSupportRetainsPurposeAndOtherOperationsDoNotBorrowIt()
        {
            var support = Body(3); var army = Army(support);
            var intent = Preparation(Army(Body(1)).Id, 1);
            intent.Attack.GatherSupportArmyIds.Add(army.Id);
            Assert.That(EquipmentUpgradeValuation.EquipmentPurpose(Snap(), null, support), Is.SameAs(intent));
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(Snap(), null, support), Is.True);
        }

        [Test]
        public void DefenceDutyProtectsTheLastGarrisonMemberAndThreatenedSecondaryBase()
        {
            var defender = Body(3); var garrison = Army(defender); garrison.IsGarrison = true;
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(Snap(), null, defender), Is.True);
            garrison.IsGarrison = false; garrison.Hex = new HexCoord(7, 0);
            var snap = Snap(); snap.Self.BaseHexes = new[] { default(HexCoord), garrison.Hex };
            snap.Threat = new ThreatModel { Threats = new[] { new AssetThreatSnapshot {
                Asset = new StrategicAssetSnapshot { Hex = garrison.Hex, Kind = AssetKind.Base }, CanDamage = true,
                EnemyEta = 0, Contact = new EnemyContactSnapshot { Position = new HexCoord(8, 0),
                    Army = new ArmySnapshot { ArmyId = 9, Owner = new PlayerSetupData() } } } } };
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(snap, null, defender), Is.True);
            snap.Threat.Threats = Array.Empty<AssetThreatSnapshot>();
            Assert.That(EquipmentUpgradeValuation.IsRequiredEquipmentRecipient(snap, null, defender), Is.False);
        }

        [Test]
        public void RecipientVerdictKeyChangesWhenTheTurnStartInvestmentWindowIsObserved()
        {
            var snap = Snap(); snap.Development = new DevelopmentReadiness {
                InvestmentSurplusByType = new Game.Economy.ResourceBundle {
                    Human = 100, Energy = 100, Materials = 100, Tech = 100 } };
            for (int turn = 1; turn < AiConfigV2.devInvestmentSurplusTurns; turn++)
            { snap.TurnNumber = turn; DevelopmentInvestmentGate.Observe(player, snap); }
            snap.TurnNumber = AiConfigV2.devInvestmentSurplusTurns;
            string before = DevelopmentAdmission.RecipientVerdictKey(snap, Array.Empty<MissionIntent>(), player, null);
            DevelopmentInvestmentGate.Observe(player, snap);
            Assert.That(DevelopmentAdmission.RecipientVerdictKey(snap, Array.Empty<MissionIntent>(), player, null),
                Is.Not.EqualTo(before), "warm-up before Observe must not lend its closed-window verdict to settled enumeration");
        }

        [Test]
        public void PriorityFingerprintSeesAttemptsAndFailedSourceWithoutHandOrBudgetChanges()
        {
            var snap = Snap(); var reservation = new MaterializationReservation();
            using var memo = new EquipmentUpgradeValuation.EquipmentTargetMemo(reservation);
            string first = EquipmentUpgradeValuation.RecipientPriorityFacts(snap);
            reservation.GenerationAttemptsUsed++;
            string attempted = EquipmentUpgradeValuation.RecipientPriorityFacts(snap);
            Assert.That(attempted, Is.Not.EqualTo(first));
            reservation.TriedGeneratorCards.Add("same-source");
            Assert.That(EquipmentUpgradeValuation.RecipientPriorityFacts(snap), Is.Not.EqualTo(attempted));
        }

        [Test]
        public void PriorityFingerprintChangesWithRosterMultiplicityAndStatus()
        {
            var host = Army(Body(2)); var intent = Preparation(host.Id, 1); var snap = Snap();
            string first = EquipmentUpgradeValuation.RecipientPriorityFacts(snap);
            intent.Attack.TargetRoster.Add(intent.Attack.TargetRoster[0]);
            string second = EquipmentUpgradeValuation.RecipientPriorityFacts(snap);
            Assert.That(second, Is.Not.EqualTo(first));
            intent.Status = IntentStatus.Suspended;
            Assert.That(EquipmentUpgradeValuation.RecipientPriorityFacts(snap), Is.Not.EqualTo(second));
        }
    }
}
#endif
