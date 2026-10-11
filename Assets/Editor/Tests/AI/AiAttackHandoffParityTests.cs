#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ATK-F04 — one physical handoff answer for the Attack gather projection, Provisioning's leg
    // check and the executed transfer (GroundCombatReinforcement.PlanAttackHandoff).
    public class AiAttackHandoffParityTests
    {
        private static readonly PlayerSetupData Us =
            new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly HexCoord Host = new HexCoord(5, 0);
        private static readonly HexCoord Far = new HexCoord(-2, -2);

        [SetUp]
        public void SetUp() => ArmyRegistry.Clear();

        [TearDown]
        public void TearDown() => ArmyRegistry.Clear();

        private static UnitData Body(string name, int attack, int hp) => new UnitData
        {
            Name = name, Owner = Us, Attack = attack, Defense = 2,
            HitPointsMax = hp, HitPointsCurrent = hp, Initiative = 2,
        };

        private static UnitData Hero(string name, int command) => new UnitData
        {
            Name = name, Owner = Us, IsHero = true, CommandRating = command,
            HitPointsMax = 6, HitPointsCurrent = 6, Fate = 3, FateMax = 3,
        };

        private static ArmyData Army(HexCoord hex, params UnitData[] members)
        {
            var army = new ArmyData { Owner = Us, Hex = hex };
            foreach (UnitData m in members)
                army.Members.Add(m);
            return army;
        }

        private static IReadOnlyList<WorthIt.DefendingArmy> Site() => new[]
        {
            new WorthIt.DefendingArmy(new[] { new WorthIt.DefenderProfile(2f, false, null, 3f, 8f, 2) },
                default(WorthIt.SideCommander)),
        };

        // Kryll T12: the host already acted this turn and today's AP is gone, while the support is
        // still walking. The arrival handoff strengthens the host; only its charge is today's.
        [Test]
        public void ArrivalHandoff_DoesNotDependOnTodaysActivationCharge()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            host.MarkActivated();
            ArmyData support = Army(Far, Body("Medium Tank", 8, 12));

            HandoffPlan projected = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: false, out string why);
            Assert.That(projected, Is.Not.Null, why);
            Assert.That(projected.Incoming.Select(u => u.Name), Is.EquivalentTo(new[] { "Medium Tank" }));
            Assert.That(GroundCombatReinforcement.ImprovesAttackForce(host, support, Site(), 0f, out _),
                Is.True, "the leg check is the same composition answer");
        }

        // The rendezvous step itself still pays today's charge (player root lookup: Unity-only).
        [Test]
        public void RendezvousHandoff_StillNeedsTodaysActivationCharge()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            host.MarkActivated();
            ArmyData support = Army(Host, Body("Medium Tank", 8, 12));
            HandoffPlan now = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: true, out string nowWhy);
            Assert.That(now, Is.Null, "no player root pays the newcomer's activation today");
            Assert.That(nowWhy, Does.Contain("action points"));
        }

        // Kryll T18/T20: a support [hero, body] whose hero would take command but adds no power —
        // the bodies-only transfer that does raise the host is the plan, and the gather projection
        // (SupportImprovesPrimary on live armies) answers the same.
        [Test]
        public void NonStrengtheningHeroHandover_FallsBackToTheUsefulBody()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            ArmyData support = Army(Host, Hero("Aldric", 8), Body("Ash Drifter", 5, 6));
            float before = AiPower.EffectiveArmyPower(host.Members);

            HandoffPlan plan = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: false, out string why);
            Assert.That(plan, Is.Not.Null, why);
            var after = host.Members.Except(plan.Displaced).Concat(plan.Incoming).ToList();
            Assert.That(AiPower.EffectiveArmyPower(after), Is.GreaterThan(before),
                "a handoff that does not strengthen the fist is never the plan");
            Assert.That(plan.Incoming.Any(u => u.Name == "Ash Drifter"), Is.True);

        }

        // The gather projection on live armies (registry lookup: Unity-only) answers the same.
        [Test]
        public void GatherProjection_MatchesTheLivePlan()
        {
            ArmyData host = Army(Host, Hero("Kessa", 7), Body("Infantry", 3, 4), Body("Crawler", 3, 5));
            ArmyData useful = Army(Host, Hero("Aldric", 8), Body("Ash Drifter", 5, 6));
            ArmyData useless = Army(Host, Body("Weak", 1, 2));
            ArmyData full = Army(Host, Body("A", 6, 8), Body("B", 6, 8));
            foreach (ArmyData a in new[] { host, useful, useless, full })
                ArmyRegistry.Register(a);
            foreach ((ArmyData primary, ArmyData support) in new[] { (host, useful), (full, useless) })
                Assert.That(GroundCombatAssemblyPlanner.SupportImprovesPrimary(Snapshot(primary),
                        Snapshot(support), Site(), allowCommandHandover: true, allowCompleteTransfer: true),
                    Is.EqualTo(GroundCombatReinforcement.PlanAttackHandoff(primary, support, Site(), 0f,
                        requireChargeNow: false, out _) != null));
        }

        [Test]
        public void FullHost_WithoutAStrongerBody_HasNoPlanAndAnExactReason()
        {
            ArmyData host = Army(Host, Body("A", 6, 8), Body("B", 6, 8));
            ArmyData support = Army(Host, Body("Weak", 1, 2));

            HandoffPlan plan = GroundCombatReinforcement.PlanAttackHandoff(host, support, Site(),
                0f, requireChargeNow: false, out string why);
            Assert.That(plan, Is.Null);
            Assert.That(why, Does.Contain("full"));
        }

        private static ArmySnapshot Snapshot(ArmyData a) => new ArmySnapshot
        {
            ArmyId = a.Id, Owner = Us, Hex = a.Hex, IsStructuralRaidActor = true,
            MemberCount = a.Members.Count, Capacity = a.Capacity, MaxMovement = 3, CurrentMovement = 3,
            Members = a.Members.Where(u => !u.IsHero).Select(WorthIt.FromLiveUnit).ToList(),
            EffectiveArmyPower = AiPower.EffectiveArmyPower(a.Members),
        };
    }
}
#endif
