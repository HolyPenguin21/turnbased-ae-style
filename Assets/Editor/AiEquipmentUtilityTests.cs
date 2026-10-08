#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    // The signed utility U (EquipmentEfficiency.Utility): meaningful before/after cases, not a copy of the constants.
    public class AiEquipmentUtilityTests
    {
        private static readonly string[] None = new string[0];

        private static EfficiencyStats S(int a, int d, int hp, int r, int move = 3, int ini = 1, int ap = 1, int fate = 0) =>
            new EfficiencyStats(a, d, hp, r, move, ini, ap, fate);

        // Iron Concord reference infantry.
        private static readonly EfficiencyStats Scout = S(2, 1, 4, 2);
        private static readonly EfficiencyStats Medium = S(3, 2, 4, 2);
        private static readonly EfficiencyStats Heavy = S(4, 3, 4, 2);

        private static WorthIt.DefenderProfile Enemy(int d, int a, int hp, int ini = 1,
            params UnitTypeTag[] tags) => new WorthIt.DefenderProfile(d, false, tags, a, hp, ini);

        private static EfficiencyContext Ctx(params WorthIt.DefenderProfile[] targets) =>
            new EfficiencyContext { Targets = targets, HostTags = new[] { UnitTypeTag.Infantry } };

        private static UtilityBreakdown U(EfficiencyStats before, EfficiencyStats after, EfficiencyContext ctx = null,
            string[] beforeAb = null, string[] afterAb = null) =>
            EquipmentEfficiency.Utility(before, beforeAb ?? None, after, afterAb ?? beforeAb ?? None,
                ctx ?? new EfficiencyContext());

        [Test]
        public void ChangeThatDoesNotChangeTheHost_IsExactlyZero()
        {
            Assert.That(U(Medium, Medium).Total, Is.Zero);
            Assert.That(U(Medium, S(3, 2, 4, 2, ap: 1)).Total, Is.Zero);
        }

        [Test]
        public void AttackOverride_HelpsAWeakHostMoreThanAStrongOne()
        {
            var heavyMg = U(Heavy, S(5, 3, 4, 2), afterAb: None, beforeAb: new[] { UnitAbilities.Berserk }).Combat;
            float scout = U(Scout, S(5, 1, 4, 2)).Combat, medium = U(Medium, S(5, 2, 4, 2)).Combat;
            Assert.That(scout, Is.GreaterThan(medium));
            Assert.That(medium, Is.GreaterThan(heavyMg), "+1 Attack with Berserk lost is worth less than +2 on a plain body");
        }

        [Test]
        public void SameMeanDefense_DifferentDistributions_GiveDifferentRifleValue()
        {
            // Mean Defense 2 either way; the +1 Attack is worth more against the spread.
            float flat = U(Medium, S(4, 2, 4, 2), Ctx(Enemy(2, 3, 4), Enemy(2, 3, 4))).Combat;
            float spread = U(Medium, S(4, 2, 4, 2), Ctx(Enemy(0, 3, 4), Enemy(4, 3, 4))).Combat;
            Assert.That(spread, Is.Not.EqualTo(flat).Within(1e-3f));
        }

        [Test]
        public void LosingRangeAndAbility_CanBeNegative()
        {
            var armoured = Ctx(Enemy(2, 4, 6, 1, UnitTypeTag.Armored));
            var at = S(4, 2, 4, 2);
            float twinSmg = U(at, S(5, 2, 4, 1), armoured,
                new[] { UnitAbilities.Hyperkinetic }, new[] { UnitAbilities.CriticalDamage }).Combat;
            Assert.That(twinSmg, Is.LessThan(0f));
        }

        [Test]
        public void Defense_ImprovesSurvivalWithoutFrailtyAmplifier()
        {
            var ctx = Ctx(Enemy(2, 4, 4));
            float scout = U(Scout, S(2, 2, 4, 2), ctx).Combat, heavy = U(Heavy, S(4, 4, 4, 2), ctx).Combat;
            Assert.That(scout, Is.GreaterThan(0f));
            Assert.That(heavy, Is.GreaterThan(0f));
            Assert.That(U(Medium, S(3, 2, 6, 2), ctx).Combat, Is.GreaterThan(0f), "+HP keeps attacks alive");
        }

        [Test]
        public void Regeneration_HealsOnlyAWoundedSurvivor()
        {
            var regen = new[] { UnitAbilities.Regeneration };
            var enemy = Enemy(2, 3, 6);
            var full = Ctx(enemy);
            var wounded = Ctx(enemy); wounded.HpSpent = 2;
            var lethal = Ctx(Enemy(0, 40, 6)); lethal.HpSpent = 2;
            float fullHp = U(Medium, Medium, full, None, regen).Combat;
            float hurt = U(Medium, Medium, wounded, None, regen).Combat;
            float dead = U(Medium, Medium, lethal, None, regen).Combat;
            Assert.That(hurt, Is.GreaterThan(fullHp));
            Assert.That(dead, Is.LessThan(hurt), "no heal for a carrier killed before the tick");
            Assert.That(dead, Is.GreaterThanOrEqualTo(0f).Or.Zero, "no resurrection / overheal");
        }

        [Test]
        public void Range_UsesKnownDistanceAndOriginalReach()
        {
            var enemy = Enemy(2, 3, 4);
            var d3 = Ctx(enemy); d3.KnownDistance = 3;
            var d1 = Ctx(enemy); d1.KnownDistance = 1;
            Assert.That(U(Medium, S(3, 2, 4, 3), d3).Combat, Is.GreaterThan(0f), "range 2->3 reaches a distance-3 enemy");
            Assert.That(U(Medium, S(3, 2, 4, 3), d1).Combat, Is.Zero, "a target already in reach gains nothing");
            float r12 = U(S(3, 2, 4, 1), S(3, 2, 4, 2), Ctx(enemy)).Combat;
            float r34 = U(S(3, 2, 4, 3), S(3, 2, 4, 4), Ctx(enemy)).Combat;
            Assert.That(r12, Is.Not.EqualTo(r34).Within(1e-3f));
        }

        [Test]
        public void Initiative_PaysOnlyWhenTheOrderChanges()
        {
            var enemy = Enemy(2, 3, 4, ini: 2);
            Assert.That(U(Medium, S(3, 2, 4, 2, ini: 3), Ctx(enemy)).Combat, Is.GreaterThan(0f), "tie broken in our favour");
            var slow = Ctx(Enemy(2, 3, 4, ini: 1));
            Assert.That(U(S(3, 2, 4, 2, ini: 3), S(3, 2, 4, 2, ini: 4), slow).Combat, Is.Zero, "already first: no change");
        }

        [Test]
        public void ActivationAp_IsPaidOnce_AndFreeUnitsGainNothing()
        {
            Assert.That(U(Medium, S(3, 2, 4, 2, ap: 0)).Ap, Is.EqualTo(0.225f).Within(1e-5f));
            Assert.That(U(S(3, 2, 4, 2, ap: 0), S(3, 2, 4, 2, ap: 0)).Ap, Is.Zero);
            Assert.That(U(Medium, Medium, null, None, new[] { UnitAbilities.RapidReaction }).Ap, Is.EqualTo(0.225f).Within(1e-5f));
            Assert.That(U(S(3, 2, 4, 2, ap: 0), S(3, 2, 4, 2, ap: 0), null, None, new[] { UnitAbilities.RapidReaction }).Ap,
                Is.Zero, "no second saving for an already free unit");
            Assert.That(U(S(3, 2, 4, 2, ap: 0), S(3, 2, 4, 2, ap: 1)).Ap, Is.LessThan(0f), "a higher cost is a signed loss");
        }

        [Test]
        public void ArmySpeed_IsTheSlowestMember()
        {
            var amongThrees = new EfficiencyContext { OtherSpeedMin = 3 };
            Assert.That(U(S(3, 2, 4, 2, move: 2), S(3, 2, 4, 2, move: 3), amongThrees).Move, Is.GreaterThan(0f));
            Assert.That(U(S(3, 2, 4, 2, move: 3), S(3, 2, 4, 2, move: 4), amongThrees).Move, Is.Zero);
        }

        [Test]
        public void KnownRoute_ReplacesTheProxy_AndIsNotCountedTwice()
        {
            // Army of three: this host (AP 1) + others paying 3 AP, slowest other member Move 3, route 6.
            var route = new EfficiencyContext { OtherSpeedMin = 3, RouteLength = 6, OtherArmyActivationAp = 3f };
            var speed = U(S(3, 2, 4, 2, move: 2), S(3, 2, 4, 2, move: 3), route);
            Assert.That(speed.Move, Is.EqualTo(0.6f).Within(1e-5f), "ceil(6/2)-ceil(6/3)=1 step x army AP 4 x 0.15");
            // Speed and AP together: one change of the total cost (4 AP x 3 steps -> 3 AP x 2 steps = 6 AP).
            var both = U(S(3, 2, 4, 2, move: 2), S(3, 2, 4, 2, move: 3, ap: 0), route);
            Assert.That(both.Move + both.Ap, Is.EqualTo(0.15f * 6f).Within(1e-4f));
            // AP alone on a route: every step is cheaper by the saved AP.
            var apOnly = U(S(3, 2, 4, 2, move: 3), S(3, 2, 4, 2, move: 3, ap: 0), route);
            Assert.That(apOnly.Move, Is.Zero);
            Assert.That(apOnly.Ap, Is.EqualTo(0.15f * 2f).Within(1e-4f), "2 route steps x 1 AP; the load beyond the route is 0");
        }

        [Test]
        public void Recce_SaturatesInTheLocalArmyOnly()
        {
            var recce = new[] { UnitAbilities.R1S4 };
            float first = U(Medium, Medium, null, None, recce).Vision;
            var sameArmy = new EfficiencyContext { OtherRecceRadius = 1 };
            float second = U(Medium, Medium, sameArmy, None, recce).Vision;
            float otherArmy = U(Medium, Medium, new EfficiencyContext(), None, recce).Vision;
            Assert.That(first, Is.EqualTo(0.16f * 0.5f).Within(1e-5f));
            Assert.That(second, Is.Zero);
            Assert.That(otherArmy, Is.EqualTo(first).Within(1e-6f));
        }

        [Test]
        public void Detection_ReadsSpotAgainstHide()
        {
            Assert.That(EquipmentEfficiency.DetectProbability(4, 4), Is.EqualTo(0.36328125f).Within(1e-6f));
            Assert.That(EquipmentEfficiency.DetectProbability(0, 4), Is.Zero);
            var relevant = new EfficiencyContext { DetectionRelevance = 1f };
            float r1s4 = U(Medium, Medium, relevant, None, new[] { UnitAbilities.R1S4 }).Detection;
            float r1s0 = U(Medium, Medium, relevant, None, new[] { UnitAbilities.R1S0 }).Detection;
            Assert.That(r1s4, Is.GreaterThan(r1s0));
            Assert.That(U(Medium, Medium, new EfficiencyContext(), None, new[] { UnitAbilities.R1S4 }).Detection,
                Is.Zero, "no known hidden target: no invented detection value");
        }

        [Test]
        public void Stealth_IsAnOptionPricedOnce()
        {
            var stealth = new[] { UnitAbilities.Stealth4 };
            var scout = new EfficiencyContext { StealthUsable = true };
            Assert.That(U(Medium, Medium, scout, None, stealth).Stealth, Is.EqualTo(0.165f).Within(1e-5f));
            Assert.That(U(Medium, Medium, new EfficiencyContext(), None, stealth).Stealth, Is.EqualTo(0.04125f).Within(1e-5f));
            Assert.That(U(Medium, Medium, scout, stealth, stealth).Stealth, Is.Zero, "the same Stealth again adds nothing");
            Assert.That(U(Medium, Medium, scout, None, stealth).Combat, Is.Zero, "no hidden first strike on the ground");
        }

        [Test]
        public void AntiAir_NeedsALegalReaction_AndASecondCarrierStillAdds()
        {
            var wasp = Enemy(4, 8, 6, 2, UnitTypeTag.Aircraft);
            var aa = new[] { UnitAbilities.AntiAir };
            Assert.That(U(Heavy, Heavy, Ctx(Enemy(2, 3, 4)), None, aa).AntiAir, Is.Zero, "no air target: no legal reaction");
            var one = Ctx(wasp);
            float first = U(Heavy, Heavy, one, None, aa).AntiAir;
            var second = Ctx(wasp); second.OtherAntiAirCarriers = 1;
            float next = U(Heavy, Heavy, second, None, aa).AntiAir;
            Assert.That(first, Is.GreaterThan(0f));
            Assert.That(next, Is.GreaterThan(0f).And.LessThan(first));
        }

        [Test]
        public void OperatorFate_NeedsWitnessedOutputs()
        {
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), new EfficiencyContext { IsHero = true, ArmyAttack = 0f }).Fate,
                Is.Zero, "no order: no guaranteed future output");
            var ordered = new EfficiencyContext { IsHero = true, ArmyAttack = 0f,
                OperatorOutputs = new[] { (0.5f, 0.15f, 0.64f, 0.91f) } };
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), ordered).Fate,
                Is.EqualTo((0.91f - 0.64f) * (0.5f - 0.15f)).Within(1e-5f));
        }

        [Test]
        public void MissionMultipliers_ActOnTheirOwnShare()
        {
            var enemy = Enemy(2, 3, 4);
            var plain = Ctx(enemy);
            var attack = Ctx(enemy); attack.OffenseMult = 2f; attack.DefenseMult = 1f;
            float rifle = U(Medium, S(4, 2, 4, 2), plain).Combat, rifleMission = U(Medium, S(4, 2, 4, 2), attack).Combat;
            Assert.That(rifleMission, Is.EqualTo(2f * rifle).Within(1e-4f), "pure +Attack is all offence");
            float shield = U(Medium, S(3, 3, 4, 2), plain).Combat, shieldMission = U(Medium, S(3, 3, 4, 2), attack).Combat;
            Assert.That(shieldMission, Is.EqualTo(shield).Within(1e-4f), "pure +Defense is all defence");
            var regen = new[] { UnitAbilities.Regeneration };
            var wounded = Ctx(enemy); wounded.HpSpent = 2;
            var woundedMission = Ctx(enemy); woundedMission.HpSpent = 2; woundedMission.OffenseMult = 2f;
            Assert.That(U(Medium, Medium, woundedMission, None, regen).Combat,
                Is.EqualTo(U(Medium, Medium, wounded, None, regen).Combat).Within(1e-4f), "Regeneration is a defensive share");
        }

        [Test]
        public void HeroDoesNotBecomeAFighter()
        {
            var hero = new EfficiencyContext { IsHero = true, ArmyAttack = 0f, Targets = new[] { Enemy(1, 3, 4) } };
            Assert.That(U(S(0, 0, 6, 0), S(5, 0, 6, 2), hero).Combat, Is.Zero);
        }
    }
}
#endif
