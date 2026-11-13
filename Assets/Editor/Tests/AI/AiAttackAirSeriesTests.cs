#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-13 (owner, match Sable vs Korrin) — an Attack flies up to
    // AiConfigV2.attackAirSupportMaxWings wings at its site. One free Rusty Vulture used to fill the
    // only slot, so no missile or bomb was ever produced or bound for the operation. The slots are
    // filled best damage first and each wing is priced on what the wings before it leave
    // (GroundCombatAirSupport.Series) — the one series model Continuity's binding and the march
    // forecast (AttackSupportProjection) both read.
    public class AiAttackAirSeriesTests
    {
        private static readonly HexCoord Target = new HexCoord(4, 0);
        private PlayerSetupData _owner;

        [SetUp]
        public void SetUp() => _owner = new PlayerSetupData();

        private static WorthIt.DefenderProfile Profile(float attack = 0f, float defense = 0f, float hp = 4f) =>
            new WorthIt.DefenderProfile(defense, false, attack: attack, hitPoints: hp, maxHitPoints: hp);

        private ArmySnapshot Wing(int id, float attack) => new ArmySnapshot
        {
            ArmyId = id, Owner = _owner, Hex = new HexCoord(0, 0), IsAir = true, MemberCount = 1,
            CurrentMovement = 6, MaxMovement = 6, SafeUnlandedEndsRemaining = 1,
            RecoveryMembers = new[] { new RaidRecoveryMemberSnapshot(id * 10, 0, false, true, false, false, 1,
                Profile(attack: attack), Profile(attack: attack), false, ResourceVector.Zero) },
        };

        private WorldSnapshot Snapshot(params ArmySnapshot[] wings) => new WorldSnapshot
        {
            Observer = _owner,
            Self = new SelfSnapshot { Armies = wings, BaseHexes = new[] { new HexCoord(0, 0) } },
        };

        private static IReadOnlyList<WorthIt.DefendingArmy> Garrison(int bodies, float hp, float defense = 1f) =>
            new[] { new WorthIt.DefendingArmy(Enumerable.Range(0, bodies)
                .Select(_ => Profile(attack: 2f, defense: defense, hp: hp)).ToList(), default, armyId: 41) };

        private static float Hp(IReadOnlyList<WorthIt.DefendingArmy> package) =>
            WorthIt.UnitsOf(package).Sum(u => u.HitPoints);

        private static List<AirSupportOption> Series(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, int maxWings, params int[] bound) =>
            GroundCombatAirSupport.Series(snap, opposition, Target, AirStrikePolicy.Standard, null, 0f,
                bound, null, maxWings);

        [Test]
        public void Series_FillsTheSlotsBestDamageFirst_EachWingOnWhatTheEarlierLeave()
        {
            WorldSnapshot snap = Snapshot(Wing(5, attack: 2f), Wing(6, attack: 9f), Wing(7, attack: 5f));
            IReadOnlyList<WorthIt.DefendingArmy> garrison = Garrison(6, hp: 8f);

            List<AirSupportOption> series = Series(snap, garrison, maxWings: 2);

            Assert.That(series.Select(o => o.WingArmyId), Is.EqualTo(new[] { 6, 7 }),
                "the strongest wing first, the light frame keeps no slot from it");
            Assert.That(Hp(series[1].OppositionAfter), Is.LessThan(Hp(series[0].OppositionAfter)),
                "the second wing strikes the survivors of the first: the package carries both strikes");
            Assert.That(Hp(series[0].OppositionAfter), Is.LessThan(Hp(garrison)));
        }

        [Test]
        public void Series_BoundWingsComeFirst_AndTheLimitHolds()
        {
            WorldSnapshot snap = Snapshot(Wing(5, attack: 2f), Wing(6, attack: 9f), Wing(7, attack: 5f));

            List<AirSupportOption> series = Series(snap, Garrison(6, hp: 8f), maxWings: 2, 5);

            Assert.That(series.Select(o => o.WingArmyId), Is.EqualTo(new[] { 5, 6 }),
                "the bound wing keeps its slot; the free slot goes to the strongest free wing");
            Assert.That(Series(snap, Garrison(6, hp: 8f), maxWings: 3).Count, Is.EqualTo(3));
        }

        [Test]
        public void Series_NothingLeftToHurt_BindsNoFurtherWing()
        {
            WorldSnapshot snap = Snapshot(Wing(6, attack: 9f), Wing(7, attack: 5f));

            List<AirSupportOption> series = Series(snap, Garrison(1, hp: 1f, defense: 0f), maxWings: 3);

            Assert.That(series.Count, Is.EqualTo(1), "a wing with nothing left to strike is not flown");
            Assert.That(series[0].WingArmyId, Is.EqualTo(6));
            Assert.That(WorthIt.UnitsOf(series[0].OppositionAfter), Is.Empty);
        }

        [Test]
        public void Series_UnknownRoster_FliesOneBlindSortieOnly()
        {
            WorldSnapshot snap = Snapshot(Wing(6, attack: 9f), Wing(7, attack: 5f));

            List<AirSupportOption> series = Series(snap, System.Array.Empty<WorthIt.DefendingArmy>(), maxWings: 3);

            Assert.That(series.Count, Is.EqualTo(1));
            Assert.That(series[0].RosterKnown, Is.False);
        }

        [Test]
        public void AttackWings_AreAllHeld_AndCountedInSeriesOrder()
        {
            var intent = new MissionIntent { Objective = new AttackIntent { AirSupportWings =
                { new AttackAirWing { ArmyId = 3 }, new AttackAirWing { ArmyId = 4 } } } };
            Assert.That(GroundCombatLegs.HeldAirSupportArmyIds(intent), Is.EqualTo(new[] { 3, 4 }));

            var forecast = new AttackSupportForecast(Garrison(1, 4f), 0f, Garrison(1, 2f), 0f,
                new[] { new CountedAirStrike(5, 1, true), new CountedAirStrike(6, 2, false) }, null);
            Assert.That(forecast.CountsSupport, Is.True);
            Assert.That(forecast.Describe(), Is.EqualTo("support=air#5(bound)@eta1+air#6@eta2"));
            Assert.That(new AttackSupportForecast(Garrison(1, 4f), 0f, Garrison(1, 4f), 0f, null, null)
                .Describe(), Is.EqualTo("support=none"));
        }

        // The observed-air path: the next wing's estimate runs on the survivors' rosters.
        [Test]
        public void AfterStrikeTargets_KeepsSurvivorsWoundsAndWipedArmies()
        {
            var roster = new AviationCombatEstimator.DefendingAirArmy(41,
                new[] { Profile(attack: 2f, hp: 6f), Profile(attack: 2f, hp: 6f) }, new[] { 0, 0 });
            var wiped = new AviationCombatEstimator.DefendingAirArmy(42, new[] { Profile(hp: 1f) }, new[] { 0 });
            var targets = new[]
            {
                new Game.Ai.AiMapMemory.KnownAirSighting(Target, _owner, 3, true, roster),
                new Game.Ai.AiMapMemory.KnownAirSighting(Target, _owner, 3, true, wiped),
            };
            var estimate = new AviationCombatEstimator.AirStrikeEstimate(0f, 2f,
                new[] { Profile(attack: 2f, hp: 2.5f) }, 9.5f, survivorSourceIndices: new[] { 1 },
                expectedCurrentFatesAfter: new[] { 0 });

            List<Game.Ai.AiMapMemory.KnownAirSighting> after = GroundCombatAirSupport.AfterStrikeTargets(targets, estimate);

            Assert.That(after.Select(t => t.ArmyId), Is.EqualTo(new[] { 41, 42 }));
            Assert.That(after[0].Roster.Units.Single().HitPoints, Is.EqualTo(2.5f));
            Assert.That(after[1].Roster.Units, Is.Empty, "a wiped army stays, so the ground package drops it");
            Assert.That(after[0].Hex, Is.EqualTo(Target));
        }
    }
}
#endif
