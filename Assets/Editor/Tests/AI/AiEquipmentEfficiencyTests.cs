#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using NUnit.Framework;

namespace Game.EditorTests
{
    internal static class AiEquipmentTestMath
    {
        // The signed utility of a plain +N Attack on a host against the catalog prior (no enemy known),
        // in EquipmentDelta plumbing units (card value / persistence).
        internal static float IntrinsicAttack(int amount, int attack = 1, int defense = 2, int hp = 8,
            int range = 1, int initiative = 0)
        {
            var before = new EfficiencyStats(attack, defense, hp, range, 3, initiative, 0, 0);
            var after = new EfficiencyStats(attack + amount, defense, hp, range, 3, initiative, 0, 0);
            return EquipmentEfficiency.Utility(before, new string[0], after, new string[0], new EfficiencyContext()).Combat
                / AiConfigV2.equipmentUpgradePersistence;
        }
    }

    // The bonus-weight table of docs/ai-v2-equipment-efficiency-table.md, example by example.
    public class AiEquipmentEfficiencyTests
    {
        private static EfficiencyStats S(int a, int d, int hp, int r, int move = 3, int ini = 1, int ap = 1, int fate = 0) =>
            new EfficiencyStats(a, d, hp, r, move, ini, ap, fate);

        private static readonly EfficiencyStats Scout = S(2, 1, 4, 2);
        private static readonly EfficiencyStats MedTank = S(5, 4, 6, 2);
        private static readonly EfficiencyStats ArtTank = S(7, 3, 3, 4, ini: 2);
        private static readonly EfficiencyStats Wrecker = S(6, 4, 12, 1);
        private static readonly string[] None = new string[0];

        private static EfficiencyBreakdown D(EfficiencyStats before, EfficiencyStats after,
            string[] beforeAb = null, string[] afterAb = null, EfficiencyContext ctx = null) =>
            EquipmentEfficiency.Delta(before, beforeAb ?? None, after, afterAb ?? beforeAb ?? None, ctx);

        [Test]
        public void BaseEfficiency_MatchesTheDesignTable()
        {
            Assert.That(EquipmentEfficiency.Base(Scout), Is.EqualTo(8.5f));
            Assert.That(EquipmentEfficiency.Base(MedTank), Is.EqualTo(14f));
            Assert.That(EquipmentEfficiency.Base(ArtTank), Is.EqualTo(18f));
            Assert.That(EquipmentEfficiency.Base(Wrecker), Is.EqualTo(16f));
        }

        [Test]
        public void RangeCountsOnlyUpToTheCap()
        {
            Assert.That(D(MedTank, S(5, 4, 6, 3)).Total, Is.EqualTo(2f), "+1 Range on a tank");
            Assert.That(D(ArtTank, S(7, 3, 3, 5, ini: 2)).Total, Is.Zero, "range above the cap adds nothing");
        }

        [Test]
        public void OverrideWeaponsHelpWeakHostsAndHurtLongRangeOnes()
        {
            Assert.That(D(Scout, S(5, 1, 4, 2)).Total, Is.EqualTo(3f), "Heavy MG on a scout");
            Assert.That(D(Scout, S(6, 1, 4, 1)).Total, Is.EqualTo(2f), "Shotgun (A6, R1) on a scout");
            Assert.That(D(ArtTank, S(9, 3, 3, 2, ini: 2)).Total, Is.EqualTo(-2f), "Plasma Cannon costs artillery its range");
        }

        [Test]
        public void DefenseAndHpBonusesAreWorthMoreOnAFrailHost()
        {
            Assert.That(D(Scout, S(2, 2, 4, 2)).Total, Is.EqualTo(0.8f).Within(1e-4f));
            Assert.That(D(Wrecker, S(6, 5, 12, 1)).Total, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(D(Scout, S(2, 1, 6, 2)).Total, Is.EqualTo(1.6f).Within(1e-4f), "+2 HP on a scout");
        }

        [Test]
        public void Regeneration_GrowsWithTheCarriersDurability()
        {
            string[] regen = { UnitAbilities.Regeneration };
            Assert.That(D(Scout, Scout, None, regen).Total, Is.EqualTo(2.5f));
            Assert.That(D(MedTank, MedTank, None, regen).Total, Is.EqualTo(5f));
            Assert.That(D(Wrecker, Wrecker, None, regen).Total, Is.EqualTo(8f));
        }

        [Test]
        public void AbilityValuesScaleWithHostAttackAndKnownShares()
        {
            string[] crit = { UnitAbilities.CriticalDamage }, splash = { UnitAbilities.Splash },
                hyper = { UnitAbilities.Hyperkinetic }, shock = { UnitAbilities.ShockAttack };
            Assert.That(D(ArtTank, ArtTank, None, crit).Total, Is.EqualTo(3.5f));
            Assert.That(D(ArtTank, ArtTank, None, splash).Total, Is.EqualTo(3.5f), "one neighbour by default");
            Assert.That(D(ArtTank, ArtTank, None, splash, new EfficiencyContext { SplashTargets = 2f }).Total,
                Is.EqualTo(7f));
            Assert.That(D(MedTank, MedTank, None, hyper).Total, Is.EqualTo(1.25f), "default share 0.5");
            Assert.That(D(MedTank, MedTank, None, hyper, new EfficiencyContext { ArmoredShare = 1f }).Total,
                Is.EqualTo(2.5f));
            Assert.That(D(MedTank, MedTank, None, hyper, new EfficiencyContext { ArmoredShare = 0f }).Total, Is.Zero);
            Assert.That(D(ArtTank, ArtTank, None, shock).Total, Is.EqualTo(3.5f), "0.25 x A x Initiative");
            Assert.That(D(MedTank, MedTank, None, shock).Total, Is.EqualTo(1.25f));
        }

        [Test]
        public void ScorcherUsesOnlyTheNeighboursSplashLeaves()
        {
            string[] splash = { UnitAbilities.Splash };
            string[] both = { UnitAbilities.Splash, UnitAbilities.Scorcher };
            var three = new EfficiencyContext { SecondaryNeighbors = 2f, BioShare = 1f };
            var four = new EfficiencyContext { SecondaryNeighbors = 3f, BioShare = 1f };
            Assert.That(D(MedTank, MedTank, splash, both, three).Total, Is.Zero,
                "Splash already takes both neighbours");
            Assert.That(D(MedTank, MedTank, splash, both, four).Total, Is.GreaterThan(0f));
        }

        [Test]
        public void StealthIsWorthMoreToAScoutAndSaturates()
        {
            string[] stealth = { UnitAbilities.Stealth4 };
            var usable = new EfficiencyContext { StealthUsable = true };
            Assert.That(D(Scout, Scout, None, stealth, usable).Total, Is.EqualTo(1f));
            Assert.That(D(Scout, Scout, None, stealth).Total, Is.EqualTo(0.25f), "an unused host keeps only a share");
            Assert.That(D(Scout, S(8, 1, 4, 2), None, stealth, usable).Total, Is.EqualTo(6f + 4f),
                "Plasma Gun on a stealth scout: +6 stats, +4 hidden strike");
            var crowded = new EfficiencyContext { StealthUsable = true, Carriers = f => f == "Stealth" ? 3 : 0 };
            Assert.That(D(Scout, Scout, None, stealth, crowded).Total, Is.EqualTo(0.25f), "1 / (1 + 3 carriers)");
            Assert.That(D(Scout, Scout, stealth, stealth, crowded).Total, Is.Zero,
                "an ability the host already has adds nothing");
        }

        [Test]
        public void FlatBonuses_MoveInitiativeAndActivationAp()
        {
            Assert.That(D(MedTank, S(5, 4, 6, 2, move: 4)).Total, Is.EqualTo(0.15f * 14f).Within(1e-4f));
            Assert.That(D(MedTank, S(5, 4, 6, 2, move: 4), ctx: new EfficiencyContext { OtherSpeedMin = 2 }).Total,
                Is.Zero, "a faster body does not speed up an army held back by a slower member");
            Assert.That(D(MedTank, S(5, 4, 6, 2, ini: 2)).Total, Is.EqualTo(1f).Within(1e-4f), "0.2 x A x Initiative");
            Assert.That(D(MedTank, S(5, 4, 6, 2, ap: 0)).Total, Is.EqualTo(AiConfigV2.equipActivationApValue));
            Assert.That(D(Scout, Scout, None, new[] { UnitAbilities.RapidReaction }).Total,
                Is.EqualTo(AiConfigV2.equipRapidReactionValue));
        }

        [Test]
        public void HeroFate_ScalesWithTheArmyAndTheOperatorRole()
        {
            var hero = S(0, 0, 6, 0, fate: 4);
            var heroAfter = S(0, 0, 6, 0, fate: 5);
            Assert.That(D(hero, heroAfter, ctx: new EfficiencyContext { IsHero = true }).Total,
                Is.EqualTo(2f).Within(1e-4f));
            Assert.That(D(hero, heroAfter, ctx: new EfficiencyContext { IsHero = true, ArmyAttack = 6f }).Total,
                Is.EqualTo(3f).Within(1e-4f));
            Assert.That(D(hero, heroAfter, ctx: new EfficiencyContext { IsHero = true, IsFacilityOperator = true }).Total,
                Is.EqualTo(2f + AiConfigV2.equipOperatorFateGain).Within(1e-4f));
            Assert.That(D(Scout, S(2, 1, 4, 2, fate: 1)).Total, Is.Zero, "Fate of an ordinary unit is not used");
        }

        [Test]
        public void MissionContextScalesTheMatchingGroup()
        {
            var attack = new EfficiencyContext();
            EquipmentEfficiency.ApplyMission(attack, MissionKind.Attack, 0f);
            Assert.That(D(Scout, S(5, 1, 4, 2), ctx: attack).Total,
                Is.EqualTo(3f * AiConfigV2.equipAttackOffenseMult).Within(1e-4f));
            var withHex = new EfficiencyContext();
            EquipmentEfficiency.ApplyMission(withHex, MissionKind.Attack, 3f);
            Assert.That(D(Scout, S(5, 1, 4, 2), ctx: withHex).Total,
                Is.EqualTo(3f * AiConfigV2.equipAttackOffenseMult
                    * (1f + 3f * AiConfigV2.equipHexDefenseOffensePerPoint)).Within(1e-4f),
                "an Attack bonus has to cover the target hex defence");
            var defence = new EfficiencyContext();
            EquipmentEfficiency.ApplyMission(defence, MissionKind.ActiveDefence, 0f);
            Assert.That(D(Scout, S(2, 2, 4, 2), ctx: defence).Total,
                Is.EqualTo(0.8f * AiConfigV2.equipActiveDefenceDefenseMult).Within(1e-4f));
        }

        [Test]
        public void OwnUtility_DoesNotDependOnDeckPlusHandSize()
        {
            // Deck + hand 35 / 24 / 12 / 0: the pair, host and context are the same, so the item's own
            // utility is the same. (Supply no longer scales it; only alternatives and gates may differ.)
            var host = new CardData(new CardDefinition
            {
                cardType = CardType.Unit, attack = 2, defenseRating = 1, hitPoints = 4,
            });
            var item = new CardDefinition { cardType = CardType.Equipment };
            item.equipment = new EquipmentGrant();
            float baseline = StrategicCardEvaluator.EquipmentDeltaParts(item, host.Definition).Total;
            foreach (int cards in new[] { 35, 24, 12, 0 })
            {
                var snap = new WorldSnapshot { Self = new SelfSnapshot {
                    Deck = new CardDefinition[cards], Hand = new CardData[0] } };
                Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(item, host, snap).Total,
                    Is.EqualTo(baseline).Within(1e-5f), $"cards left {cards}");
            }
        }
    }
}
#endif
