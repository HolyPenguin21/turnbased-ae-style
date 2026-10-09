#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Ai;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
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

        private static WorthIt.DefenderProfile EnemyR(int range, int d, int a, int hp, int ini = 1) =>
            new WorthIt.DefenderProfile(d, false, null, a, hp, ini, range: range);

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
        public void OperatorFate_IsValuedPerPoint_AndWitnessedOutputsReplaceIt()
        {
            var op = new[] { UnitAbilities.Researcher };
            var ctx = new EfficiencyContext { IsHero = true, ArmyAttack = 0f };
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), ctx, op).Fate,
                Is.EqualTo(AiConfigV2.equipOperatorFateValue).Within(1e-5f), "operator: a plain value per Fate point");
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), ctx, None).Fate,
                Is.Zero, "a hero that operates nothing gains no operator value");
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 2), ctx, op).Fate,
                Is.LessThan(0f), "losing Fate is a signed loss");
            var ordered = new EfficiencyContext { IsHero = true, ArmyAttack = 0f,
                OperatorOutputs = new[] { (0.5f, 0.15f, 0.64f, 0.91f) } };
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), ordered, op).Fate,
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
        public void AviationHost_IsPricedAsAnAirStrike_NotAGroundContact()
        {
            var wasp = S(8, 4, 6, 2, move: 10, ini: 2);
            var ctx = Ctx(Enemy(2, 3, 4));
            float plusOne = EquipmentEfficiency.AviationOffenseDelta(wasp, None, S(9, 4, 6, 2, move: 10, ini: 2), None, ctx);
            Assert.That(plusOne, Is.GreaterThan(0f));
            Assert.That(EquipmentEfficiency.AviationOffenseDelta(wasp, None, wasp, None, ctx), Is.Zero);
            Assert.That(EquipmentEfficiency.AviationOffenseDelta(wasp, None, S(8, 4, 6, 4, move: 10, ini: 2), None, ctx),
                Is.Zero, "flying range does not change a sortie's exchange");
            Assert.That(EquipmentEfficiency.AviationOffenseDelta(wasp, None, S(7, 4, 6, 2, move: 10, ini: 2), None, ctx),
                Is.LessThan(0f), "a lost Attack point is a signed loss");
        }

        [Test]
        public void EnemyAnswersOnlyInsideItsOwnRange()
        {
            var shortEnemy = EnemyR(1, 2, 4, 4);
            var d2 = Ctx(shortEnemy); d2.KnownDistance = 2;
            var d1 = Ctx(shortEnemy); d1.KnownDistance = 1;
            // +Defense: at distance 2 a range-1 enemy cannot answer a range-2 host, so armour buys nothing...
            Assert.That(U(Medium, S(3, 3, 4, 2), d2).Combat, Is.Zero);
            // ...while at distance 1 it answers and the armour pays.
            Assert.That(U(Medium, S(3, 3, 4, 2), d1).Combat, Is.GreaterThan(0f));
            // An enemy of unknown Range keeps the old behaviour: it answers everywhere.
            var unknown = Ctx(Enemy(2, 4, 4)); unknown.KnownDistance = 2;
            Assert.That(U(Medium, S(3, 3, 4, 2), unknown).Combat, Is.GreaterThan(0f));
        }

        [Test]
        public void OutRangingTheEnemyIsWorthMoreThanTradingBlows()
        {
            var shortEnemy = EnemyR(1, 2, 4, 4);
            var d2 = Ctx(shortEnemy); d2.KnownDistance = 2;
            var unknownCtx = Ctx(Enemy(2, 4, 4)); unknownCtx.KnownDistance = 2;
            // +1 Attack for a range-2 host: against a short-ranged enemy at distance 2 every hit is free.
            float freeHits = U(Medium, S(4, 2, 4, 2), d2).Combat;
            float traded = U(Medium, S(4, 2, 4, 2), unknownCtx).Combat;
            Assert.That(freeHits, Is.GreaterThan(0f));
            Assert.That(freeHits, Is.Not.EqualTo(traded).Within(1e-4f));
        }

        [Test]
        public void HeroFateIsValuedByRole()
        {
            var operatorAb = new[] { UnitAbilities.Researcher };
            float perPoint = AiConfigV2.equipHeroFateFactor * 4f * AiConfigV2.equipCardValuePerE;   // ArmyAttack 4
            EfficiencyContext Hero(bool commandsField) => new EfficiencyContext
                { IsHero = true, ArmyAttack = 4f, CommandsFieldArmy = commandsField };
            // A garrison operator: operator value only, no battle-commander value.
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), Hero(false), operatorAb).Fate,
                Is.EqualTo(AiConfigV2.equipOperatorFateValue).Within(1e-5f));
            // An operator that really leads a field army with fighters holds both roles.
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), Hero(true), operatorAb).Fate,
                Is.EqualTo(AiConfigV2.equipOperatorFateValue + perPoint).Within(1e-5f));
            // An ordinary hero is a commander only.
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), Hero(false), None).Fate,
                Is.EqualTo(perPoint).Within(1e-5f));
            Assert.That(U(S(0, 0, 6, 0, fate: 3), S(0, 0, 6, 0, fate: 4), Hero(true), None).Fate,
                Is.EqualTo(perPoint).Within(1e-5f));
        }

        [Test]
        public void ShockAndKillShareOneProbabilityMass()
        {
            // A8 against D2 / HP4: a kill is part of the hit, Shock cancels the answer on ANY damage.
            float expected = Game.Combat.BattleSimulationKernel.ExpectedExchangeDamage(8, 2, new[] { UnitAbilities.ShockAttack },
                new UnitTypeTag[0], None, 4, out float hit);
            float below = Game.Combat.BattleSimulationKernel.ExpectedExchangeDamage(8, 2, new[] { UnitAbilities.ShockAttack },
                new UnitTypeTag[0], None, 3, out _);
            float kill = expected - below;
            Assert.That(kill, Is.GreaterThan(0f).And.LessThanOrEqualTo(hit + 1e-6f));
            EquipmentEfficiency.AnswerWeights(hit, kill, true, out float unhurt, out float hurt);
            Assert.That(unhurt + hurt, Is.EqualTo(1f - hit).Within(1e-6f), "with Shock only the unhit answer remains");
            float doubleCounted = (1f - kill) * (1f - hit);
            Assert.That(doubleCounted, Is.LessThan(unhurt - 0.01f), "the old product removed the killed mass twice");
            EquipmentEfficiency.AnswerWeights(hit, kill, false, out unhurt, out hurt);
            Assert.That(unhurt + hurt, Is.EqualTo(1f - kill).Within(1e-6f), "without Shock only the dead stop answering");
        }

        [Test]
        public void SurvivingEnemyBerserkAnswersWithTheRaisedAttack()
        {
            var plain = Ctx(Enemy(1, 3, 6));
            var berserk = new EfficiencyContext { Targets = new[] { new WorthIt.DefenderProfile(1, false, null, 3, 6, 1,
                new[] { UnitAbilities.Berserk }) }, HostTags = new[] { UnitTypeTag.Infantry } };
            // Armour is worth more against an enemy that hits harder after being hit.
            float vsPlain = U(Medium, S(3, 3, 4, 2), plain).Combat;
            float vsBerserk = U(Medium, S(3, 3, 4, 2), berserk).Combat;
            Assert.That(vsBerserk, Is.GreaterThan(vsPlain));
        }

        [Test]
        public void ScorcherAndSplashJudgeTheRealNeighbour()
        {
            WorthIt.DefenderProfile tank = new WorthIt.DefenderProfile(2, false, new[] { UnitTypeTag.Vehicle }, 3, 6, 1);
            WorthIt.DefenderProfile bio = new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Bio }, 3, 4, 1);
            WorthIt.DefenderProfile steel = new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Vehicle }, 3, 4, 1);
            EfficiencyContext Battle(WorthIt.DefenderProfile neighbour) => new EfficiencyContext
            {
                Targets = new[] { tank }, HostTags = new[] { UnitTypeTag.Infantry },
                Battles = new List<IReadOnlyList<WorthIt.DefenderProfile>> { new[] { tank, neighbour } },
            };
            var scorcher = new[] { UnitAbilities.Scorcher };
            float withBio = U(S(5, 2, 4, 2), S(5, 2, 4, 2), Battle(bio), None, scorcher).Combat;
            float withSteel = U(S(5, 2, 4, 2), S(5, 2, 4, 2), Battle(steel), None, scorcher).Combat;
            Assert.That(withBio, Is.GreaterThan(0f), "a Bio neighbour of a vehicle is a valid Scorcher recipient");
            Assert.That(withSteel, Is.Zero, "a non-Bio neighbour is not, whatever the primary is");
            float splashSteel = U(S(5, 2, 4, 2), S(5, 2, 4, 2), Battle(steel), None, new[] { UnitAbilities.Splash }).Combat;
            Assert.That(splashSteel, Is.GreaterThan(0f), "Splash hits any neighbour");
        }

        [Test]
        public void UnknownNeighboursAreEstimatedFromTheKnownComposition()
        {
            var bioPool = new[]
            {
                new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Bio }, 3, 4, 1),
                new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Bio }, 3, 4, 1),
            };
            var steelPool = new[]
            {
                new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Vehicle }, 3, 4, 1),
                new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Vehicle }, 3, 4, 1),
            };
            var scorcher = new[] { UnitAbilities.Scorcher };
            EfficiencyContext Ctxt(WorthIt.DefenderProfile[] pool) => new EfficiencyContext
                { Targets = pool, HostTags = new[] { UnitTypeTag.Infantry }, SecondaryNeighbors = 1f };
            float bio = U(S(5, 2, 4, 2), S(5, 2, 4, 2), Ctxt(bioPool), None, scorcher).Combat;
            float steel = U(S(5, 2, 4, 2), S(5, 2, 4, 2), Ctxt(steelPool), None, scorcher).Combat;
            Assert.That(bio, Is.GreaterThan(steel));
            Assert.That(steel, Is.Zero);
        }

        [Test]
        public void AntiAirShotNeedsALegalReaction()
        {
            var aa = new[] { UnitAbilities.AntiAir };
            var wasp = new WorthIt.DefenderProfile(4, false, new[] { UnitTypeTag.Aircraft }, 8, 6, 2);
            EfficiencyContext Contact(int distance = 1, bool sees = true, bool used = false, int earlier = 0,
                bool hidden = false, int radius = 1)
            {
                var c = Ctx(wasp);
                c.AirContacts = new[] { new AirContact(wasp, distance, sees, used, earlier) };
                c.HostHidden = hidden; c.AntiAirRadius = radius;
                return c;
            }
            float legal = U(Heavy, Heavy, Contact(), None, aa).AntiAir;
            Assert.That(legal, Is.GreaterThan(0f));
            Assert.That(U(Heavy, Heavy, Contact(distance: 3), None, aa).AntiAir, Is.Zero, "out of the AA radius");
            Assert.That(U(Heavy, Heavy, Contact(distance: 3, radius: 3), None, aa).AntiAir, Is.GreaterThan(0f),
                "the carrier own radius stat decides");
            Assert.That(U(Heavy, Heavy, Contact(sees: false), None, aa).AntiAir, Is.Zero, "its owner does not see the hex");
            Assert.That(U(Heavy, Heavy, Contact(hidden: true), None, aa).AntiAir, Is.Zero, "a hidden unit takes no shot");
            Assert.That(U(Heavy, Heavy, Contact(used: true), None, aa).AntiAir, Is.Zero, "the reaction was already used");
            float second = U(Heavy, Heavy, Contact(earlier: 1), None, aa).AntiAir;
            Assert.That(second, Is.GreaterThan(0f).And.LessThan(legal), "a carrier that fires first takes its share");
            // No concrete contact: only a labelled reserve proxy from the known air composition.
            var composition = Ctx(wasp);
            Assert.That(U(Heavy, Heavy, composition, None, aa).AntiAir, Is.GreaterThan(0f));
            Assert.That(U(Heavy, Heavy, Ctx(Enemy(2, 3, 4)), None, aa).AntiAir, Is.Zero);
        }

        [Test]
        public void SnapshotFreeAndReferenceWorldShareOneFormula()
        {
            var host = new CardDefinition { cardType = CardType.Unit, attack = 2, defenseRating = 1, hitPoints = 4, range = 2 };
            var item = new CardDefinition { cardType = CardType.Equipment, equipment = new EquipmentGrant() };
            item.equipment.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.Attack, amount = 3 });
            var free = StrategicCardEvaluator.EquipmentDeltaParts(item, host);
            var emptyWorld = StrategicCardEvaluator.EquipmentDeltaParts(item, new CardData(host), new WorldSnapshot());
            Assert.That(free.Combat, Is.GreaterThan(0f));
            Assert.That(free.Total, Is.EqualTo(emptyWorld.Total).Within(1e-5f), "no second, linear formula for the reserve");
            var big = new CardDefinition { cardType = CardType.Equipment, equipment = new EquipmentGrant() };
            big.equipment.statChanges.Add(new EquipmentStatChange { stat = EquipmentStat.Attack, amount = 40 });
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(big, host).Combat,
                Is.GreaterThan(free.Combat), "no fixed clamp on the result");
        }

        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void AssignedScout_UsesItsRouteForRapidReactionInEitherIndependentSlot(AttachmentSlot slot)
        {
            var host = new UnitData { MoveMax = 3, ActivationApCost = 1 };
            var army = new ArmyData { Hex = new HexCoord(0, 0) };
            army.Members.Add(host);
            var actor = new ArmySnapshot { ArmyId = army.Id, Hex = army.Hex,
                IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3,
                MaxMovement = 3, ActivationApCost = 1 };
            var snap = new WorldSnapshot { Self = new SelfSnapshot { Armies = new[] { actor } } };
            var purpose = new MissionIntent { Kind = MissionKind.Scout, PreferredMoverArmyId = army.Id,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(7, 0) } };
            var ctx = new EfficiencyContext { IsHero = true };
            StrategicCardEvaluator.ApplyKnownRouteAndCoverage(ctx, snap, army, host, null, purpose);
            var body = new CardDefinition { cardType = CardType.Hero, moveMax = 3, activationApCost = 1 };
            var item = new CardDefinition { cardType = CardType.Equipment, attachmentSlot = slot,
                equipment = new EquipmentGrant() };
            item.equipment.addAbilities.Add(UnitAbilities.RapidReaction);
            PredictedEquipmentState projected = EquipmentSystem.Project(body, null, null, item);
            Assert.That(ctx.RouteLength, Is.EqualTo(7));
            Assert.That(U(S(0, 1, 4, 1), S(0, 1, 4, 1), ctx,
                None, new List<string>(projected.Abilities).ToArray()).Ap,
                Is.EqualTo(ActionPrice.ToCardScore(3f)).Within(1e-5f),
                "three route activations replace the unassigned 1.5-activation proxy, without requiring the other slot");
        }

        [Test]
        public void AssignedScout_StealthReadsHonestDetectorsWithoutInventingHiddenTargets()
        {
            var host = new UnitData(); var army = new ArmyData { Hex = new HexCoord(0, 0) };
            army.Members.Add(host);
            HexCoord focus = new HexCoord(3, 0);
            var sightings = new List<AiMapMemory.KnownEnemySighting>();
            for (int i = 0; i < 8; i++) sightings.Add(new AiMapMemory.KnownEnemySighting(
                focus, new PlayerSetupData(), "detector", 1, 1, 1, null, armyId: i));
            var snap = new WorldSnapshot { Known = new KnownSnapshot { EnemySightings = sightings },
                Self = new SelfSnapshot { Armies = new[] { new ArmySnapshot { ArmyId = army.Id,
                    Hex = army.Hex, IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3, MaxMovement = 3 } } } };
            var purpose = new MissionIntent { Kind = MissionKind.Scout, PreferredMoverArmyId = army.Id,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = focus } };
            var ctx = new EfficiencyContext { IsHero = true, StealthUsable = true };
            StrategicCardEvaluator.ApplyKnownRouteAndCoverage(ctx, snap, army, host, null, purpose);
            Assert.That(ctx.StealthRisk, Is.EqualTo(ScoutRiskModel.DetectorRisk(snap, focus)));
            Assert.That(ctx.DetectionRelevance, Is.Zero, "enemy detectors are not evidence of a hidden target");
            var stealth = new[] { "Stealth4" };
            Assert.That(U(Medium, Medium, ctx, None, stealth).Stealth,
                Is.GreaterThan(U(Medium, Medium, new EfficiencyContext { IsHero = true, StealthUsable = true }, None, stealth).Stealth));
            snap.Known = new KnownSnapshot();
            var unknown = new EfficiencyContext { IsHero = true, StealthUsable = true };
            StrategicCardEvaluator.ApplyKnownRouteAndCoverage(unknown, snap, army, host, null, purpose);
            Assert.That(U(Medium, Medium, unknown, None, stealth).Stealth,
                Is.EqualTo(U(Medium, Medium, new EfficiencyContext { IsHero = true, StealthUsable = true }, None, stealth).Stealth));
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
