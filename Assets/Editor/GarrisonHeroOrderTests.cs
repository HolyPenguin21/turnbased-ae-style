#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using System.Reflection;
using Game.Ai.V2;
using Game.Cards;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Garrison hero order: the garrison keeps heroes first by descending CommandRating, so the
    // first hero (Commander / Capacity owner) is always the best one; preview and commit agree.
    public sealed class GarrisonHeroOrderTests
    {
        private static UnitData Hero(string n, int cr) => new UnitData { Name = n, IsHero = true, CommandRating = cr };
        private static UnitData Body(string n) => new UnitData { Name = n };

        private static ArmyData Garrison(params UnitData[] members)
        {
            var g = ArmyData.CreateVisualSnapshot();
            g.IsGarrison = true;
            g.Members.AddRange(members);
            return g;
        }

        private static string Names(ArmyData a) => string.Join(",", a.Members.Select(m => m.Name));

        [Test]
        public void HigherHeroLeadsGarrisonAndRaisesCapacityAtOnce()
        {
            ArmyData g = Garrison(Hero("H3", 3), Body("U"));
            g.AddMemberSorted(Hero("H8", 8));
            Assert.That(Names(g), Is.EqualTo("H8,H3,U"));
            Assert.That(g.Commander.Name, Is.EqualTo("H8"));
            Assert.That(g.Capacity, Is.EqualTo(8));
        }

        [Test]
        public void FullGarrisonAdmitsAnExpandingHero()
        {
            ArmyData g = Garrison(Hero("H3", 3), Body("U1"), Body("U2"));
            Assert.That(g.CanFitAdditionalCard(new CardDefinition { cardType = CardType.Hero, commandRating = 8 }), Is.True);
            Assert.That(g.CanFitAdditionalCard(new CardDefinition { cardType = CardType.Hero, commandRating = 3 }), Is.False);
            Assert.That(g.CanFitAdditionalCard(new CardDefinition { cardType = CardType.Unit }), Is.False);
        }

        [Test]
        public void TailHeroesAreSortedAndEqualRatingsKeepOrder()
        {
            ArmyData g = Garrison(Hero("H8", 8), Hero("H3", 3));
            g.AddMemberSorted(Hero("H6", 6));
            Assert.That(Names(g), Is.EqualTo("H8,H6,H3"));

            ArmyData e = Garrison(Hero("A", 6), Hero("B", 6));
            e.AddMemberSorted(Hero("C", 6));
            Assert.That(Names(e), Is.EqualTo("A,B,C"));
        }

        [Test]
        public void FieldArmyKeepsInsertionOrderAndCommander()
        {
            var f = ArmyData.CreateVisualSnapshot();
            f.Members.Add(Hero("H3", 3));
            f.AddMemberSorted(Hero("H8", 8));
            Assert.That(Names(f), Is.EqualTo("H3,H8"));
            Assert.That(f.Commander.Name, Is.EqualTo("H3"));
        }

        [Test]
        public void NormalizeGarrisonOrderIsNoOpWhenCanonicalAndFixesManualOrder()
        {
            ArmyData g = Garrison(Hero("H8", 8), Hero("H3", 3), Body("U"));
            Assert.That(g.NormalizeGarrisonOrder(), Is.False);
            g.Members.Reverse();
            Assert.That(g.NormalizeGarrisonOrder(), Is.True);
            Assert.That(Names(g), Is.EqualTo("H8,H3,U"));
        }

        [Test]
        public void FirstHeroReplacesGarrisonBaseEvenWhenLower()
        {
            ArmyData g = Garrison(Body("U1"), Body("U2"), Body("U3"), Body("U4"));
            Assert.That(g.CanFitAdditionalCard(new CardDefinition { cardType = CardType.Hero, commandRating = 3 }), Is.False);
            ArmyData h = Garrison(Hero("H0", 0));
            Assert.That(h.Capacity, Is.EqualTo(0));
            Assert.That(h.CanFitAdditionalCard(new CardDefinition { cardType = CardType.Unit }), Is.False);
        }

        [Test]
        public void BatchPreviewJudgesNormalizedFinalRoster()
        {
            ArmyData field = ArmyData.CreateVisualSnapshot();
            UnitData h8 = Hero("H8", 8);
            field.Members.Add(h8);
            field.Members.Add(Body("F"));
            ArmyData g = Garrison(Hero("H3", 3), Body("U1"), Body("U2"));
            Assert.That(ArmyActions.CanTransferMembers(new[] { h8 }, field, g, out string why), Is.True, why);
        }

        [Test]
        public void LeavingOnlySufficientCommanderIsRefused()
        {
            UnitData h8 = Hero("H8", 8);
            ArmyData g = Garrison(h8, Hero("H3", 3), Body("U1"), Body("U2"), Body("U3"), Body("U4"));
            Assert.That(g.CanLeaveWithoutOvercrowding(h8), Is.False);
        }

        private static MaterializationPlan HeroPlan(string key, int cr) => new MaterializationPlan
        {
            Kind = MaterializationChainKind.GenerateDeploy,
            GeneratedBaseDef = new CardDefinition { cardType = CardType.Hero, commandRating = cr },
            StableKey = key,
        };

        private static MaterializationPlan UnitPlan(string key) => new MaterializationPlan
        {
            Kind = MaterializationChainKind.GenerateDeploy,
            GeneratedBaseDef = new CardDefinition { cardType = CardType.Unit },
            StableKey = key,
        };

        [Test]
        public void ProjectionCountsEveryExistingHero()
        {
            ArmyData field = ArmyData.CreateVisualSnapshot();
            field.Members.AddRange(new[] { Hero("A", 4), Hero("B", 4), Body("x"), Body("y") });
            Assert.That(field.Capacity, Is.EqualTo(4));
            var plan = UnitPlan("u");
            plan.Deploy = new PlacementOption(default, DeploymentKind.ExistingArmy, field);
            object state = NewState();
            Seed(state, ProjectedPhysicalState.RecipientKey(plan), field);
            Assert.That(CanAdd(state, plan), Is.False);
        }

        [Test]
        public void GarrisonProjectionRollbackRestoresCapacity()
        {
            ArmyData g = Garrison(Hero("H3", 3), Body("U1"), Body("U2"));
            var h8 = HeroPlan("h8", 8);
            h8.Deploy = new PlacementOption(default, DeploymentKind.Garrison, g);
            var u = UnitPlan("u");
            u.Deploy = new PlacementOption(default, DeploymentKind.Garrison, g);
            object state = NewState();
            Seed(state, ProjectedPhysicalState.RecipientKey(h8), g);
            Assert.That(CanAdd(state, u), Is.False);           // 3 members already at cap 3
            object token = Add(state, h8);
            Assert.That(CanAdd(state, u), Is.True);            // cap 8
            Remove(state, token);
            Assert.That(CanAdd(state, u), Is.False);           // back to cap 3
        }

        private static readonly Type StateType = typeof(MaterializationPlan).Assembly
            .GetType("Game.Ai.V2.ProjectedPhysicalState", throwOnError: true);
        private static object NewState() => Activator.CreateInstance(StateType, nonPublic: true);
        private static MethodInfo M(string n) => StateType.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance);
        private static void Seed(object s, string key, ArmyData a) => M("SeedRecipient").Invoke(s, new object[] { key, a });
        private static bool CanAdd(object s, MaterializationPlan p) => (bool)M("CanAdd").Invoke(s, new object[] { p });
        private static object Add(object s, MaterializationPlan p) => M("Add").Invoke(s, new object[] { p });
        private static void Remove(object s, object token) => M("Remove").Invoke(s, new[] { token });
    }
}
#endif
