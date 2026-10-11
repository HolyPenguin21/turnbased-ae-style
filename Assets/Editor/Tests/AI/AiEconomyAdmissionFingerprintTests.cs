#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Level 3: the Economy admission key (moved out of Pipeline.RunTurn into EconomyAdmission).
    // Each input is changed on its own: a relevant one must re-admit Economy, an irrelevant one
    // (a scout stepping its waypoint) must not.
    public class AiEconomyAdmissionFingerprintTests
    {
        private PlayerSetupData _player;
        private AiTurnContext _ctx;

        [SetUp]
        public void SetUp()
        {
            _player = new PlayerSetupData { Nickname = "economy-key" };
            _ctx = new AiTurnContext { TurnNumber = 4 };
        }

        private static ArmySnapshot Army(int id, int members = 2, bool hero = false, int q = 0,
            int movement = 5, bool duty = false) => new ArmySnapshot
        {
            ArmyId = id, MemberCount = members, HasHero = hero, Hex = new HexCoord(q, 0),
            CurrentMovement = movement, ActivationApCost = 1, OperatorDutyBlocksDeparture = duty,
        };

        private static WorldSnapshot Snap(params ArmySnapshot[] armies) =>
            new WorldSnapshot { Self = new SelfSnapshot { Armies = armies } };

        private string Key(WorldSnapshot snap, IReadOnlyList<MissionIntent> intents = null, string res = "3,3,3,3") =>
            EconomyAdmission.Fingerprint(DesireAxis.Economy, snap, intents, null, null, _player, _ctx, res);

        private static MissionIntent EconomyIntent(int builder, int? mover = null) => new MissionIntent
        {
            Kind = MissionKind.Economy, Status = IntentStatus.Active, PreferredMoverArmyId = mover,
            Objective = new EconomyIntent { BuilderArmyId = builder },
        };

        [Test]
        public void TheSameWorldGivesTheSameKey() =>
            Assert.AreEqual(Key(Snap(Army(1), Army(2))), Key(Snap(Army(1), Army(2))));

        [Test]
        public void AnOperatorDutyChangeReadmitsEconomy() =>
            Assert.AreNotEqual(Key(Snap(Army(1))), Key(Snap(Army(1, duty: true))));

        [Test]
        public void AnArmyGainingAHeroOrChangingSizeReadmitsEconomy()
        {
            string baseKey = Key(Snap(Army(1)));
            Assert.AreNotEqual(baseKey, Key(Snap(Army(1, hero: true))));
            Assert.AreNotEqual(baseKey, Key(Snap(Army(1, members: 3))));
            Assert.AreNotEqual(baseKey, Key(Snap(Army(1), Army(2))), "a newly formed army");
        }

        [Test]
        public void AScoutSteppingItsWaypointDoesNotReadmitEconomy() =>
            Assert.AreEqual(Key(Snap(Army(1), Army(9, q: 0, movement: 5))),
                Key(Snap(Army(1), Army(9, q: 1, movement: 4))));

        [Test]
        public void AnArmyAnEconomyIntentHoldsIsPositioned()
        {
            var intents = new List<MissionIntent> { EconomyIntent(builder: 2) };
            Assert.AreNotEqual(Key(Snap(Army(2, q: 0)), intents), Key(Snap(Army(2, q: 1)), intents),
                "the committed builder arriving or leaving is an Economy input");
        }

        [Test]
        public void ReleasingADonorChangesTheActorClaims()
        {
            var held = new List<MissionIntent> { EconomyIntent(builder: 2, mover: 3) };
            var released = new List<MissionIntent> { EconomyIntent(builder: 2, mover: null) };
            Assert.AreNotEqual(Key(Snap(Army(2), Army(3)), held), Key(Snap(Army(2), Army(3)), released));
        }

        [Test]
        public void ABaseOpportunityAppearingOrMovingReadmitsEconomy()
        {
            WorldSnapshot none = Snap(Army(1));
            WorldSnapshot one = Snap(Army(1));
            one.Economy = new EconomyStanding
            { BaseOpportunities = new[] { new EconomyBaseOpportunity { Hex = new HexCoord(2, 1) } } };
            WorldSnapshot moved = Snap(Army(1));
            moved.Economy = new EconomyStanding
            { BaseOpportunities = new[] { new EconomyBaseOpportunity { Hex = new HexCoord(3, 1) } } };
            Assert.AreNotEqual(Key(none), Key(one));
            Assert.AreNotEqual(Key(one), Key(moved));
            Assert.AreEqual(Key(one), Key(one));
        }

        [Test]
        public void AnEconomyIntentChangingStatusReadmitsEconomy()
        {
            MissionIntent active = EconomyIntent(builder: 2);
            MissionIntent suspended = EconomyIntent(builder: 2);
            suspended.Status = IntentStatus.Suspended;
            Assert.AreNotEqual(Key(Snap(Army(2)), new List<MissionIntent> { active }),
                Key(Snap(Army(2)), new List<MissionIntent> { suspended }),
                "claims and the owners list both carry the status");
        }

        [Test]
        public void ThePhysicalStockIsAnInput() =>
            Assert.AreNotEqual(Key(Snap(Army(1)), res: "3,3,3,3"), Key(Snap(Army(1)), res: "3,3,3,2"));

        [Test]
        public void TheDispatcherRoutesEachAxisToItsOwnerAndSharesNoKey()
        {
            WorldSnapshot snap = Snap(Army(1));
            string economy = StrategicAdmissionFingerprints.For(DesireAxis.Economy, snap, null, null, null, _player, _ctx);
            string development = StrategicAdmissionFingerprints.For(DesireAxis.Development, snap, null, null, null, _player, _ctx);
            string aggression = StrategicAdmissionFingerprints.For(DesireAxis.Aggression, snap, null, null, null, _player, _ctx);
            Assert.That(economy, Does.StartWith("axis=Economy|"));
            Assert.That(development, Does.StartWith("axis=Development|"));
            Assert.That(aggression, Does.StartWith("axis=Aggression|"));
        }
    }
}
#endif
