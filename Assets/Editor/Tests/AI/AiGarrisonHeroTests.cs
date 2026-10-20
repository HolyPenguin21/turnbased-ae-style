#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-09-30 (user decisions) — garrison heroes (Support type tag), the preparation host's
    // capacity hero, the two mobilization starts and one live Attack operation per player.
    public class AiGarrisonHeroTests
    {
        private static readonly PlayerSetupData Us =
            new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
        private static readonly HexCoord Staging = new HexCoord(4, 1);

        [SetUp]
        public void SetUp() => ArmyRegistry.Clear();

        [TearDown]
        public void TearDown() => ArmyRegistry.Clear();

        private static UnitData Body(string name, int attack) => new UnitData
        {
            Name = name, Owner = Us, Attack = attack, Defense = 2,
            HitPointsMax = 6, HitPointsCurrent = 6, Initiative = 2, MoveMax = 3, MoveCurrent = 3,
        };

        private static UnitData Hero(string name, int command, bool support = false)
        {
            var hero = new UnitData
            {
                Name = name, Owner = Us, IsHero = true, CommandRating = command,
                HitPointsMax = 6, HitPointsCurrent = 6, Fate = 3, FateMax = 3, MoveMax = 3, MoveCurrent = 3,
            };
            hero.TypeTags.Add(UnitTypeTag.Hero);
            if (support)
                hero.TypeTags.Add(UnitTypeTag.Support);
            return hero;
        }

        private static ArmyData Army(HexCoord hex, params UnitData[] members)
        {
            var army = new ArmyData { Owner = Us, Hex = hex };
            army.Members.AddRange(members);
            ArmyRegistry.Register(army);
            return army;
        }

        private static WorldSnapshot Snap() => new WorldSnapshot
        {
            Observer = Us,
            TurnNumber = 20,
            Self = new SelfSnapshot { Armies = new List<ArmySnapshot>(), BaseHexes = new List<HexCoord> { Staging } },
        };

        private static WorldSnapshot DepartureSnapshot()
        {
            WorldSnapshot snap = Snap();
            snap.Self.Armies = ArmyRegistry.AllForOwner(Us).Select(a => new ArmySnapshot
            {
                ArmyId = a.Id, Owner = Us, Hex = a.Hex, IsGarrison = a.IsGarrison,
                IsStructuralRaidActor = AiArmyRoles.IsStructuralGroundCombatActor(a),
                MemberCount = a.Members.Count, HasHero = a.Commander != null,
                HeroCount = a.Members.Count(u => u.IsHero), Capacity = a.Capacity,
                CurrentMovement = a.CurrentMovement, MaxMovement = a.MaxMovement,
                ActivationApCost = a.ActivationApCost, HasActivatedThisTurn = a.HasActivatedThisTurn,
                EffectiveArmyPower = AiPower.EffectiveArmyPower(a.Members),
                Commander = WorthIt.SideCommander.Of(a.Members),
                Members = a.Members.Where(u => u.IsGroundCombatant).Select(WorthIt.FromLiveUnit).ToList(),
            }).ToList();
            return snap;
        }

        private static ArmyData DepartureHome()
        {
            ArmyData home = Army(Staging, Hero("Home", 9, support: true), Body("Guard", 12));
            home.IsGarrison = true;
            return home;
        }

        private static IReadOnlyList<WorthIt.DefendingArmy> WeakOpposition()
        {
            UnitData enemy = Body("Enemy", 1);
            enemy.Defense = 0; enemy.HitPointsCurrent = enemy.HitPointsMax = 2;
            enemy.Initiative = 0; enemy.IsSummoned = true;
            return new[] { new WorthIt.DefendingArmy(new[] { WorthIt.FromLiveUnit(enemy) }, default) };
        }

        // Not a decisive fight for the departure host alone: the 80% power floor is not waived by
        // opportunity (AttackForceReadiness.IsOpportunity, 2026-10-16), so the floor itself decides.
        private static IReadOnlyList<WorthIt.DefendingArmy> ContestedOpposition(int count, int attack) =>
            new[] { new WorthIt.DefendingArmy(Enumerable.Range(0, count)
                .Select(i => WorthIt.FromLiveUnit(Body("Enemy" + i, attack))).ToList(), default) };

        [TestCase(false)]
        [TestCase(true)]
        public void DepartureProjection_PricesOneCommanderAndReturnsSecondaryHeroes(bool supportTagged)
        {
            ArmyData home = DepartureHome();
            UnitData mara = Hero("Mara", 8), edda = Hero("Edda", 8), sol = Hero("Sol", 7);
            mara.Abilities.Add(UnitAbilities.ProduceEnergy); edda.Abilities.Add(UnitAbilities.ProduceTech);
            if (supportTagged)
            {
                mara.TypeTags.Add(UnitTypeTag.Support);
                edda.TypeTags.Add(UnitTypeTag.Support);
            }
            sol.Abilities.Add(UnitAbilities.RapidReaction); sol.ActivationApCost = 0;
            UnitData body = Body("Fighter", 20); body.IsSummoned = true;
            ArmyData host = Army(Staging, mara, edda, sol, body);
            WorldSnapshot snap = DepartureSnapshot();
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                snap, WeakOpposition(), host.Id, 0.8f);
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.Commander, Is.SameAs(sol));
            Assert.That(plan.ReturnHeroes, Is.EquivalentTo(new[] { mara, edda }));
            Assert.That(plan.HeroReturnArmyId, Is.EqualTo(home.Id));
            Assert.That(GroundCombatAssemblyPlanner.ProjectedRoster(host, plan), Is.EqualTo(new[] { sol, body }));
            Assert.That(GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan), Is.EqualTo(1));
            Assert.That(host.Members.Count, Is.EqualTo(4), "planning is read-only");
            Assert.That(home.Members.Count, Is.EqualTo(2));
        }

        [Test]
        public void DepartureProjection_DoesNotPromiseHeroReturnWithoutRoomAtHome()
        {
            ArmyData home = DepartureHome();
            while (home.Members.Count < home.Capacity) home.Members.Add(Body("Guard", 12));
            UnitData body = Body("Fighter", 20); body.IsSummoned = true;
            ArmyData host = Army(Staging, Hero("Lead", 8), Hero("Extra", 7), body);
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                DepartureSnapshot(), WeakOpposition(), host.Id, 0.8f);
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.ReturnHeroes, Is.Empty);
            Assert.That(GroundCombatAssemblyPlanner.ProjectedRoster(host, plan).Count, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LoneHeroIsOnlyAnAssemblyHostWhenALegalEscortExists(bool escortAvailable)
        {
            UnitData hero = Hero("Commander", 7); hero.ActivationApCost = 0;
            ArmyData host = Army(Staging, hero);
            if (escortAvailable)
            {
                UnitData first = Body("Escort", 20), second = Body("Guard", 20);
                first.IsSummoned = second.IsSummoned = true;
                Army(Staging, first, second);
            }
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(DepartureSnapshot(),
                new GroundCombatAssemblyRequest { Opposition = WeakOpposition(), WinChanceGate = 0.8f });
            if (!escortAvailable) { Assert.That(plan.Feasible, Is.False); return; }
            // A ready donor wins by the existing precedence; pin to exercise the solo host's
            // assembled alternative, without making it a ready actor by itself.
            plan = GroundCombatAssemblyPlanner.Plan(DepartureSnapshot(), new GroundCombatAssemblyRequest
            {
                Opposition = WeakOpposition(), WinChanceGate = 0.8f,
                PreferredPrimaryArmyId = host.Id, PinToPreferred = true,
            });
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.NeedsAssembly, Is.True);
            Assert.That(plan.Transfers.Count, Is.EqualTo(1));
            Assert.That(GroundCombatAssemblyPlanner.ProjectedRoster(host, plan).Count, Is.EqualTo(2));
        }

        [Test]
        public void SoloHeroWithoutMovementOrClaimedByAnotherTask_IsNotAnAssemblyHost()
        {
            UnitData hero = Hero("Commander", 7);
            ArmyData host = Army(Staging, hero);
            Army(Staging, Body("Escort", 20), Body("Guard", 20));
            var request = new GroundCombatAssemblyRequest { Opposition = WeakOpposition(), WinChanceGate = 0.8f,
                PreferredPrimaryArmyId = host.Id, PinToPreferred = true, ExcludedArmyIds = new HashSet<int> { host.Id } };
            Assert.That(GroundCombatAssemblyPlanner.Plan(DepartureSnapshot(), request).Feasible, Is.False);
            request.ExcludedArmyIds.Clear(); hero.MoveCurrent = 0;
            Assert.That(GroundCombatAssemblyPlanner.Plan(DepartureSnapshot(), request).Feasible, Is.False);
        }

        [Test]
        public void NecessaryPassiveHealingHero_IsKeptByTheWholeRosterCombatGate()
        {
            DepartureHome();
            UnitData leader = Hero("Lead", 7), healer = Hero("Healer", 1);
            healer.Abilities.Add(UnitAbilities.Heal);
            UnitData body = Body("Bio", 4), enemy = Body("Enemy", 4);
            body.TypeTags.Add(UnitTypeTag.Bio);
            body.HitPointsCurrent = 1;
            body.IsSummoned = enemy.IsSummoned = true;
            ArmyData host = Army(Staging, leader, healer, body);
            var opposition = new[] { new WorthIt.DefendingArmy(new[] { WorthIt.FromLiveUnit(enemy) }, default) };
            float plain = WorthIt.EstimateSequential(new[] { WorthIt.FromLiveUnit(leader), WorthIt.FromLiveUnit(body) },
                WorthIt.SideCommander.Of(leader), opposition, 0f).WinChance;
            float healed = WorthIt.EstimateSequential(host.Members.Select(WorthIt.FromLiveUnit).ToList(),
                WorthIt.SideCommander.Of(host.Members), opposition, 0f).WinChance;
            Assert.That(healed, Is.GreaterThan(plain), "fixture must witness real passive benefit");
            float gate = (plain + healed) / 2f;
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                DepartureSnapshot(), opposition, host.Id, gate);
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.ReturnHeroes, Has.No.Member(healer));
            Assert.That(GroundCombatAssemblyPlanner.ProjectedRoster(host, plan), Does.Contain(healer));
        }

        [Test]
        public void DepartureReturnAndCommanderPromotion_RollBackTogether()
        {
            ArmyData home = DepartureHome();
            UnitData old = Hero("Old", 8), keep = Hero("Keep", 7), extra = Hero("Extra", 8);
            ArmyData host = Army(Staging, old, keep, extra, Body("Fighter", 20));
            var returned = new[] { old, extra };
            Assert.That(GroundCombatAssemblyPlanner.CanApplyHeroReturn(host, home, keep, returned), Is.True);
            var applied = new List<GroundCombatAssemblyTransfer>();
            var baseline = GroundCombatAssemblyTransaction.Capture(Us, host, applied);
            host.TryReorderCommander(keep, out _);
            Assert.That(ArmyActions.TransferMembersAtomic(returned, host, home, null, out _), Is.True);
            Assert.That(GroundCombatAssemblyTransaction.Rollback(Us, host, applied,
                new AiTurnContext(), "raid", baseline, home, returned), Is.True);
            Assert.That(host.Commander, Is.SameAs(old));
            Assert.That(host.Members, Does.Contain(extra));
            Assert.That(home.Members.Count, Is.EqualTo(2));
            Assert.That(GroundCombatAssemblyTransaction.RemainingApplied(host, applied, baseline, returned), Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void HeroReturn_CapabilityIgnoresTodaysActivationButStillRequiresRoom(bool requireReadyThisTurn)
        {
            ArmyData home = DepartureHome();
            home.MarkActivated();
            UnitData keep = Hero("Keep", 7), extra = Hero("Extra", 8);
            extra.ActivationApCost = 1;
            ArmyData host = Army(Staging, keep, extra, Body("Fighter", 20));
            Assert.That(GroundCombatAssemblyPlanner.CanApplyHeroReturn(host, home, keep,
                new[] { extra }, requireReadyThisTurn), Is.EqualTo(!requireReadyThisTurn));
            while (home.Members.Count < home.Capacity) home.Members.Add(Body("Guard", 12));
            Assert.That(GroundCombatAssemblyPlanner.CanApplyHeroReturn(host, home, keep,
                new[] { extra }, requireReadyThisTurn), Is.False, "future capability must still fit both rosters");
            Assert.That(host.Members, Does.Contain(extra));
        }

        [Test]
        public void UsefulSecondaryTransportHero_IsNotRemovedForACheaperActivation()
        {
            DepartureHome();
            UnitData lead = Hero("Lead", 8), transport = Hero("Transport", 1);
            lead.MoveMax = lead.MoveCurrent = transport.MoveMax = transport.MoveCurrent = 4;
            transport.Abilities.Add(UnitAbilities.Transport);
            UnitData infantry = Body("Infantry", 20);
            infantry.TypeTags.Add(UnitTypeTag.Infantry);
            infantry.MoveMax = infantry.MoveCurrent = 2;
            ArmyData host = Army(Staging, lead, transport, infantry);
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                DepartureSnapshot(), WeakOpposition(), host.Id, 0.8f);
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.ReturnHeroes, Has.No.Member(transport));
            Assert.That(ArmyData.ComputeMaxMovement(GroundCombatAssemblyPlanner.ProjectedRoster(host, plan)),
                Is.EqualTo(4));
        }

        [TestCase(null, false)]
        [TestCase(UnitAbilities.Heal, true)]
        [TestCase(UnitAbilities.Transport, true)]
        public void CommandHandover_ReturnsOrdinaryOldCommandButKeepsArmySupport(string ability, bool keepOld)
        {
            UnitData old = Hero("Old", 4), lead = Hero("New", 8);
            if (ability != null) old.Abilities.Add(ability);
            ArmyData host = Army(Staging, old, Body("Fighter", 5));
            ArmyData support = Army(Staging, lead, Body("Fresh", 20), Body("Guard", 2));
            HandoffPlan plan = GroundCombatReinforcement.CommandHandover(
                host, support, WeakOpposition(), 0f, null);
            Assert.That(plan, Is.Not.Null);
            var final = host.Members.Except(plan.Displaced).Concat(plan.Incoming).ToList();
            Assert.That(final.Contains(old), Is.EqualTo(keepOld));
            Assert.That(ArmyActions.CanExchangeMembers(plan.Incoming, support, host, plan.Promote,
                plan.Displaced, out _, requireChargeNow: false), Is.True);
            if (!keepOld)
            {
                Assert.That(final.Count(u => u.IsHero), Is.EqualTo(1));
                Assert.That(GroundCombatReinforcement.ProjectedHandoffApCost(plan.Incoming,
                    plan.Displaced, host, support, supportWalks: true), Is.EqualTo(old.ActivationApCost));
            }
            Assert.That(host.Commander, Is.SameAs(old), "planning never mutates command");
        }

        [Test]
        public void CommandHandover_KeepsOldHeroWhenTheSupportCannotReceiveIt()
        {
            UnitData old = Hero("Old", 4), lead = Hero("New", 8);
            ArmyData host = Army(Staging, old, Body("Fighter", 5));
            ArmyData support = Army(Staging, lead, Hero("Small reserve commander", 1), Body("Fresh", 20));
            var plan = GroundCombatReinforcement.CommandHandover(host, support, WeakOpposition(), 0f, null);
            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.Displaced, Has.No.Member(old));
            Assert.That(ArmyActions.CanExchangeMembers(plan.Incoming, support, host, plan.Promote,
                plan.Displaced, out _, requireChargeNow: false), Is.True);
        }

        [Test]
        public void CommandHandover_DoesNotReplaceABetterExistingCommander()
        {
            UnitData old = Hero("Strong lead", 8), incoming = Hero("Weak lead", 3);
            ArmyData host = Army(Staging, old, Body("Fighter", 5));
            ArmyData support = Army(Staging, incoming, Body("Fresh", 20), Body("Guard", 2));
            var plan = GroundCombatReinforcement.PlanAttackHandoff(host, support,
                WeakOpposition(), 0f, requireChargeNow: false, out string why);
            Assert.That(plan, Is.Not.Null, why);
            Assert.That(plan.Promote, Is.Null, "a body delivery must not invent a weaker command upgrade");
            Assert.That(plan.Displaced, Has.No.Member(old));
        }

        [Test]
        public void CommandHandover_ExecutionReturnsOldCommanderInTheSameAtomicExchange()
        {
            UnitData old = Hero("Old", 4), lead = Hero("New", 8);
            ArmyData host = Army(Staging, old, Body("Fighter", 5));
            ArmyData support = Army(Staging, lead, Body("Fresh", 20), Body("Guard", 2));
            bool applied = GroundCombatReinforcementTransaction.ApplyReinforcementHandoff(Us,
                new AiTurnContext(), null, support, host, out _, out bool exchanged, out _,
                out string why, WeakOpposition());
            Assert.That(applied, Is.True, why);
            Assert.That(exchanged, Is.True);
            Assert.That(host.Commander, Is.SameAs(lead));
            Assert.That(host.Members.Count(u => u.IsHero), Is.EqualTo(1));
            Assert.That(support.Members, Does.Contain(old));
            Assert.That(ArmyData.RosterFits(support.Members, false), Is.True);
        }

        // An emptied army is disbanded only at the end of its owner's turn, so a reinforcement
        // hands over every body that fits — the support is not kept alive with one of them.
        [Test]
        public void ReinforcementHandoff_MayEmptyTheSupport()
        {
            UnitData lead = Hero("Lead", 6);
            ArmyData host = Army(Staging, lead, Body("Fighter", 5));
            ArmyData support = Army(Staging, Body("Fresh", 20), Body("Guard", 8));
            Assert.That(GroundCombatReinforcement.SparableSupportBodies(support), Has.Count.EqualTo(2));
            bool applied = GroundCombatReinforcementTransaction.ApplyReinforcementHandoff(Us,
                new AiTurnContext(), null, support, host, out int moved, out _, out _, out string why);
            Assert.That(applied, Is.True, why);
            Assert.That(moved, Is.EqualTo(2));
            Assert.That(support.Members, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AttackDeparture_OptimizesFreshRosterAndPreservesCommittedRoster(bool committed)
        {
            DepartureHome();
            UnitData lead = Hero("Lead", 8), extra = Hero("Extra", 4);
            ArmyData host = Army(Staging, lead, extra, Body("Fighter", 20));
            var plan = GroundCombatAssemblyPlanner.Plan(DepartureSnapshot(), new GroundCombatAssemblyRequest
            {
                Opposition = WeakOpposition(), WinChanceGate = GroundCombatAdmissionPolicy.AttackWinChanceGate,
                PreferredPrimaryArmyId = host.Id, PinToPreferred = true, PreserveDepartureRoster = committed,
            });
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.ReturnHeroes.Contains(extra), Is.EqualTo(!committed));
            Assert.That(host.Members, Does.Contain(extra), "projection is read-only");
        }

        [TestCase(UnitAbilities.Heal)]
        [TestCase(UnitAbilities.Drill)]
        [TestCase(UnitAbilities.Transport)]
        public void AttackZeroGate_DoesNotTreatAnArmySupportHeroAsWaste(string ability)
        {
            DepartureHome();
            UnitData lead = Hero("Lead", 8), support = Hero("Support", 4);
            support.Abilities.Add(ability);
            ArmyData host = Army(Staging, lead, support, Body("Fighter", 20));
            // An Attack start always carries its power floor (minimumArmyPower > 0).
            var plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(DepartureSnapshot(),
                WeakOpposition(), host.Id, GroundCombatAdmissionPolicy.AttackWinChanceGate,
                minimumArmyPower: 0.01f);
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.ReturnHeroes, Has.No.Member(support));
        }

        [Test]
        public void AttackDeparture_StillRequiresTheStrictPowerThreshold()
        {
            DepartureHome();
            ArmyData host = Army(Staging, Hero("Lead", 8), Hero("Extra", 4), Body("Fighter", 20));
            var plan = GroundCombatAssemblyPlanner.Plan(DepartureSnapshot(), new GroundCombatAssemblyRequest
            {
                Opposition = ContestedOpposition(3, 12), WinChanceGate = GroundCombatAdmissionPolicy.AttackWinChanceGate,
                PreferredPrimaryArmyId = host.Id, PinToPreferred = true, AllowSameHexAssembly = false,
                MinimumArmyPower = AiPower.EffectiveArmyPower(host.Members),
            });
            Assert.That(plan.Feasible, Is.False, "equality does not satisfy Attack's strict power bar");
        }

        [TestCase(false, 0.8f)]
        [TestCase(true, 0.8f)]
        [TestCase(false, 0f)]
        [TestCase(true, 0f)]
        public void PartlyFullHome_PermitsPartialReturnsBeforeChoosingSupportFallback(bool needsAssembly, float gate)
        {
            ArmyData home = DepartureHome();
            while (home.Members.Count < home.Capacity - 1) home.Members.Add(Body("Guard", 12));
            UnitData mara = Hero("Mara", 4, support: true), edda = Hero("Edda", 4, support: true);
            UnitData sol = Hero("Sol", 4); sol.ActivationApCost = 0;
            ArmyData host = Army(Staging, mara, edda, sol, Body("Fighter", 3));
            if (needsAssembly) Army(Staging, Body("Fresh", 20), Body("Reserve", 20));
            var plan = GroundCombatAssemblyPlanner.Plan(DepartureSnapshot(), new GroundCombatAssemblyRequest
            {
                Opposition = needsAssembly ? ContestedOpposition(2, 6) : WeakOpposition(), WinChanceGate = gate,
                PreferredPrimaryArmyId = host.Id, PinToPreferred = true,
                MinimumArmyPower = needsAssembly ? AiPower.EffectiveArmyPower(host.Members) + 0.01f : 0f,
            });
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.Commander, Is.SameAs(sol));
            Assert.That(plan.ReturnHeroes.Count, Is.EqualTo(1));
            Assert.That(plan.NeedsAssembly, Is.EqualTo(needsAssembly));
            Assert.That(GroundCombatAssemblyPlanner.ProjectedRoster(host, plan).Count, Is.LessThanOrEqualTo(sol.CommandRating));
        }

        // ---- army support carriers under any commander (2026-10-15 follow-up) ------------------

        private static UnitData BioBody(string name)
        {
            UnitData u = Body(name, 4);
            u.TypeTags.Add(UnitTypeTag.Bio);
            return u;
        }

        private static IReadOnlyList<WorthIt.DefendingArmy> EvenOpposition()
        {
            var enemy = Enumerable.Range(0, 3).Select(i => WorthIt.FromLiveUnit(Body("Enemy" + i, 4))).ToList();
            return new[] { new WorthIt.DefendingArmy(enemy, default) };
        }

        // Two otherwise identical heroes: the carrier's Heal acts whoever leads, so the fight cannot
        // separate them and the current commander stays. Crediting the effect only to the carrier
        // as commander made it win the command contest by its own passive.
        [Test]
        public void BestCommander_CreditsASecondaryCarrierUnderEveryCandidate()
        {
            UnitData lead = Hero("Lead", 6), carrier = Hero("Carrier", 6);
            carrier.Abilities.Add(UnitAbilities.Heal);
            var members = new List<UnitData> { lead, carrier, BioBody("A"), BioBody("B"), BioBody("C") };
            Assert.That(HeroRoleEvaluator.BestCommanderFor(members, false, EvenOpposition(), 0f),
                Is.SameAs(lead));
        }

        [Test]
        public void SideCommanderWithSupport_AddsOnlyLivingSupportHeroes()
        {
            var plain = WorthIt.FromLiveUnit(Hero("Plain", 3));
            UnitData medic = Hero("Medic", 3); medic.Abilities.Add(UnitAbilities.Heal);
            UnitData fallen = Hero("Fallen", 3); fallen.Abilities.Add(UnitAbilities.Drill);
            fallen.HitPointsCurrent = 0;
            var lead = WorthIt.SideCommander.Of(Hero("Lead", 6));
            var with = lead.WithSupport(new[] { plain, WorthIt.FromLiveUnit(medic), WorthIt.FromLiveUnit(fallen),
                WorthIt.FromLiveUnit(Body("Body", 4)) });
            Assert.That(with.SupportHeroes.Count, Is.EqualTo(1));
            Assert.That(with.Initiative, Is.EqualTo(lead.Initiative));
            Assert.That(lead.WithSupport(new[] { plain }), Is.EqualTo(lead));
            Assert.That(default(WorthIt.SideCommander).WithSupport(new[] { WorthIt.FromLiveUnit(medic) }).Present,
                Is.False);
        }

        // A kept support carrier takes a body slot: the frozen roster names only positions the
        // host can actually hold, an ordinary extra hero (released by Housekeeping) does not count.
        [TestCase(UnitAbilities.Heal, 3)]
        [TestCase(UnitAbilities.Transport, 3)]
        [TestCase(null, 4)]
        public void PreparationRoster_LeavesTheSlotOfARetainedSupportCarrier(string ability, int capacity)
        {
            UnitData lead = Hero("Lead", 4), second = Hero("Second", 2);
            if (ability != null) second.Abilities.Add(ability);
            ArmyData host = Army(Staging, lead, second, Body("Fighter", 5));
            Assert.That(StrikeRoster.HostRosterCapacity(host), Is.EqualTo(capacity));
            var pool = Enumerable.Range(0, 6).Select(i => Body("Pool" + i, 5 + i))
                .Select(u => new StrikeRosterCandidate(new StrikeRosterSlot(u.Name, false, 1f, ForceSource.Map),
                    AiPower.ToPowerUnit(u))).ToList();
            List<StrikeRosterSlot> roster = StrikeRoster.ComposeUnder(pool, StrikeRoster.CommanderCandidate(lead),
                StrikeRoster.HostRosterCapacity(host), out _);
            Assert.That(roster.Count(s => !s.IsHero), Is.EqualTo(capacity - 1));
        }

        // A handoff on the rendezvous moves no one: it costs its own charges, never the
        // support's activation (which only a transit step spends).
        [TestCase(false, 0)]
        [TestCase(true, 2)]
        public void ReinforcementHandoffStep_CostsOnlyItsCharges(bool primaryActivated, int expected)
        {
            ArmyData primary = Army(Staging, Hero("Lead", 6), Body("Fighter", 5));
            UnitData fresh = Body("Fresh", 20); fresh.ActivationApCost = 2;
            ArmyData support = Army(Staging, Hero("Escort", 4), fresh, Body("Guard", 2));
            if (primaryActivated) primary.MarkActivated();
            var plan = new HandoffPlan(new[] { fresh }, null, null, "test");
            Assert.That(GroundCombatLegChecks.ReinforcementStepAp(support, primary, plan), Is.EqualTo(expected));
            Assert.That(GroundCombatLegChecks.ReinforcementStepAp(support, primary, null),
                Is.EqualTo(support.ActivationApCost), "a transit step activates the support");
        }

        // ---- the assembly rollback (2026-10-15 follow-up) -------------------------------------

        private static GroundCombatAssemblyTransfer Move(UnitData unit, ArmyData donor, ArmyData host)
        {
            Assert.That(ArmyActions.TransferMember(unit, donor, host, null, out string why), Is.True, why);
            return new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = unit };
        }

        [Test]
        public void AssemblyRollback_LeavesNoActivationCoverageAndRestoresTheDonorCommander()
        {
            ArmyData host = Army(Staging, Body("Fighter", 5));
            UnitData first = Hero("First", 4), second = Hero("Second", 4);
            UnitData x = Body("X", 6);
            ArmyData donor = Army(Staging, first, second, x, Body("Y", 6));
            var baseline = GroundCombatAssemblyTransaction.Capture(Us, host, new[] {
                new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = first } });
            var applied = new List<GroundCombatAssemblyTransfer> { Move(first, donor, host), Move(x, donor, host) };
            Assert.That(donor.Commander, Is.SameAs(second));

            Assert.That(GroundCombatAssemblyTransaction.Rollback(Us, host, applied, new AiTurnContext(), "test",
                baseline), Is.True);
            Assert.That(donor.Commander, Is.SameAs(first));
            Assert.That(host.HasActivationCoverageFor(x), Is.False);
            Assert.That(GroundCombatAssemblyTransaction.RemainingApplied(host, applied, baseline), Is.Zero);
            host.MarkActivated();
            Assert.That(host.RequiresActivationCharge(x), Is.True, "a rolled-back join paid nothing");
        }

        [Test]
        public void AssemblyRollback_ReportsATransportPairingItCouldNotRestore()
        {
            ArmyData host = Army(Staging, Hero("Lead", 6));
            UnitData carrier = Body("Carrier", 4); carrier.MoveMax = 4; carrier.Abilities.Add(UnitAbilities.Transport);
            UnitData u = Body("U", 4), v = Body("V", 4);
            u.TypeTags.Add(UnitTypeTag.Infantry); v.TypeTags.Add(UnitTypeTag.Infantry);
            u.MoveMax = v.MoveMax = 2;
            ArmyData donor = Army(Staging, Hero("Donor lead", 6), carrier, u, v);
            donor.RefreshTransport();
            UnitData riding = u.TransportCarrier != null ? u : v;
            Assert.That(riding.TransportCarrier, Is.SameAs(carrier));
            var transfers = new[] { new GroundCombatAssemblyTransfer { DonorArmyId = donor.Id, Unit = riding } };
            var baseline = GroundCombatAssemblyTransaction.Capture(Us, host, transfers);
            var applied = new List<GroundCombatAssemblyTransfer> { Move(riding, donor, host) };

            GroundCombatAssemblyTransaction.Rollback(Us, host, applied, new AiTurnContext(), "test", baseline);
            Assert.That(donor.Members, Does.Contain(riding));
            Assert.That(riding.TransportCarrier, Is.Null, "the canonical refresh keeps the carrier's new pair");
            Assert.That(GroundCombatAssemblyTransaction.RemainingApplied(host, applied, baseline),
                Is.GreaterThan(0), "a changed pairing is a remaining world change");
        }

        // ---- the Support tag on the catalog heroes -------------------------------------------

        // Every hero that grants AP, researches or assembles is authored as a garrison hero; a new
        // card of that kind without the tag fails here.
        [Test]
        public void CatalogHeroes_ThatGrantApResearchOrAssemble_CarryTheSupportTag()
        {
            string cards = new[] { "Assets/Cards", "src/Assets/Cards" }
                .Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
            Assert.That(cards, Is.Not.Null, "Assets/Cards not found from " + Directory.GetCurrentDirectory());
            var roles = new[] { UnitAbilities.ApBonus, UnitAbilities.Researcher, UnitAbilities.Assembler };
            int checkedHeroes = 0;
            foreach (string file in Directory.GetFiles(cards, "CardCatalog_*.asset", SearchOption.AllDirectories))
                foreach (string block in Regex.Split(File.ReadAllText(file), @"\n  - id: ").Skip(1))
                {
                    List<int> tags = TypeTags(block);
                    if (!tags.Contains((int)UnitTypeTag.Hero))
                        continue;
                    List<string> abilities = Regex.Match(block, @"grantedAbilities:(.*?)\n    \w",
                            RegexOptions.Singleline).Groups[1].Value
                        .Split('\n').Select(l => l.Trim().TrimStart('-').Trim()).ToList();
                    if (!abilities.Intersect(roles).Any())
                        continue;
                    checkedHeroes++;
                    string name = Regex.Match(block, @"displayName: (.*)").Groups[1].Value.Trim();
                    Assert.That(tags, Does.Contain((int)UnitTypeTag.Support), $"{name} in {Path.GetFileName(file)}");
                }
            Assert.That(checkedHeroes, Is.GreaterThan(0));
        }

        private static List<int> TypeTags(string block)
        {
            string hex = Regex.Match(block, @"unitTypeTags: ?(\S*)").Groups[1].Value;
            var result = new List<int>();
            for (int i = 0; i + 8 <= hex.Length; i += 8)
                result.Add(System.BitConverter.ToInt32(Enumerable.Range(0, 4)
                    .Select(b => System.Convert.ToByte(hex.Substring(i + 2 * b, 2), 16)).ToArray(), 0));
            return result;
        }

        [Test]
        public void GarrisonHero_IsAHeroWithTheSupportTag()
        {
            Assert.That(AiArmyRoles.IsGarrisonHero(Hero("Iri", 8, support: true)), Is.True);
            Assert.That(AiArmyRoles.IsGarrisonHero(Hero("Elena", 7)), Is.False);
            var supportBody = Body("Medic", 2);
            supportBody.TypeTags.Add(UnitTypeTag.Support);
            Assert.That(AiArmyRoles.IsGarrisonHero(supportBody), Is.False);
        }

        // ---- the preparation host takes a hero for its capacity ------------------------------

        // Rurik T19/T20: a heroless host at 2/2 could never grow to the Attack threshold while a
        // hero stood on its hex. The hero adds no power, but its slots are the progress.
        [Test]
        public void FullHeroLessHost_TakesALoneHeroForItsCapacity()
        {
            ArmyData host = Army(Staging, Body("Ash Drifter", 5), Body("Ash Drifter", 5));
            ArmyData lone = Army(Staging, Hero("Elena", 7));
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanPreparationAssembly(
                Snap(), host, new HashSet<int>(), System.Array.Empty<WorthIt.DefendingArmy>(), 0f);
            Assert.That(plan.Feasible, Is.True, plan.Reason);
            Assert.That(plan.Transfers.Select(t => (t.DonorArmyId, t.Unit.Name)),
                Is.EquivalentTo(new[] { (lone.Id, "Elena") }));
            Assert.That(plan.UsesGarrisonHero, Is.False);
        }

        // Outside a preparation a lone-hero army is never emptied (the raid transaction boundary).
        [Test]
        public void LoneHeroArmy_IsADonorForAPreparationOnly()
        {
            ArmyData host = Army(Staging, Body("Ash Drifter", 5));
            Army(Staging, Hero("Elena", 7));
            Assert.That(GroundCombatDonorPolicy.PickAttachableHero(Us, host, null,
                System.Array.Empty<WorthIt.DefendingArmy>(), 0f, preparation: false).hero, Is.Null);
            Assert.That(GroundCombatDonorPolicy.PickAttachableHero(Us, host, null,
                System.Array.Empty<WorthIt.DefendingArmy>(), 0f, preparation: true).hero?.Name,
                Is.EqualTo("Elena"));
        }

        // A garrison hero is a fallback: any other legal hero goes first, even a smaller one.
        [Test]
        public void GarrisonHero_IsOnlyTheFallbackCommander()
        {
            ArmyData host = Army(Staging, Body("Ash Drifter", 5), Body("Ash Drifter", 5));
            Army(Staging, Hero("Iri", 8, support: true), Body("Guard", 3));
            Army(Staging, Hero("Hank", 6), Body("Crawler", 3));
            Assert.That(GroundCombatDonorPolicy.PickAttachableHero(Us, host, null,
                System.Array.Empty<WorthIt.DefendingArmy>(), 0f, preparation: true).hero?.Name,
                Is.EqualTo("Hank"));
        }

        [Test]
        public void GarrisonHeroFallback_IsPricedAsAMoverCost()
        {
            Assert.That(ActionPrice.GarrisonHeroFallback(false), Is.Zero);
            Assert.That(ActionPrice.GarrisonHeroFallback(true), Is.EqualTo(
                ActionPrice.ToTaskScore(AiConfigV2.garrisonHeroFallbackApEquivalent)));
        }

        // ---- the two mobilization starts -----------------------------------------------------

        [Test]
        public void Mobilization_OpensOnTheDeckShareOrOnTheFieldStrikeForce()
        {
            var self = new SelfSnapshot
            {
                DeployedPower = 50f, AvailablePower = 100f,
                TotalMilitaryPotential = 60f, FieldStrikePotential = 40f,
            };
            Assert.That(AttackForceReadiness.MobilizationOpen(self), Is.False, "50% share, 40 of 48");
            self.FieldStrikePotential = 48.1f;
            Assert.That(AttackForceReadiness.MobilizationOpen(self), Is.True, "the field can form the fist");
            self.FieldStrikePotential = 40f;
            self.DeployedPower = 75f;
            Assert.That(AttackForceReadiness.MobilizationOpen(self), Is.True, "three quarters deployed");
        }

        // Busy field armies count (they come back); lone scouts, aviation and the garrison's
        // mandatory defence do not.
        [Test]
        public void FieldStrikePotential_CountsBusyArmiesButNotScoutsAirOrTheGarrisonFloor()
        {
            var raid = Army(new HexCoord(0, 0), Hero("Hank", 6), Body("Tank", 8), Body("Crawler", 4));
            var scout = new UnitData { Name = "Hooded", Owner = Us, Attack = 3, Defense = 1,
                HitPointsMax = 4, HitPointsCurrent = 4, Initiative = 3 };
            scout.Abilities.Add("r1s4");
            Army(new HexCoord(2, 2), scout);
            float withRaid = WorldAnalysis.FieldStrikePotential(Us, ArmyRegistry.AllForOwner(Us), 100f,
                MissionIntentRegistry.GetOrCreate(Us).All);
            Assert.That(withRaid, Is.EqualTo(AiPower.EffectiveArmyPower(raid.Members)).Within(1e-3f));
        }

        // 2026-10-01 (user decision) — both Attack force measures read one army set: an army on
        // a Raid counts (it comes back), an army a Scout mission holds does not, on either side.
        [Test]
        public void AttackForceMeasures_CountRaidArmies_LeaveOutScoutMissionArmies_OnBothSides()
        {
            try
            {
                var raid = Army(new HexCoord(0, 0), Hero("Hank", 6), Body("Tank", 8), Body("Crawler", 4));
                var scouting = Army(new HexCoord(3, 3), Hero("Vera", 4), Body("Rifle", 7));
                MissionIntentRegistry.GetOrCreate(Us).Put(new MissionIntent
                {
                    Kind = MissionKind.Raid, Status = IntentStatus.Active,
                    Objective = new RaidIntent { PrimaryArmyId = raid.Id },
                });
                MissionIntentRegistry.GetOrCreate(Us).Put(new MissionIntent
                {
                    Kind = MissionKind.Scout, Status = IntentStatus.Active,
                    Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore },
                    PreferredMoverArmyId = scouting.Id,
                });

                float field = WorldAnalysis.FieldStrikePotential(Us, ArmyRegistry.AllForOwner(Us), 100f,
                MissionIntentRegistry.GetOrCreate(Us).All);
                float peak = AttackForcePool.Build(Us, new SelfSnapshot { AvailablePower = 100f },
                    MissionIntentRegistry.GetOrCreate(Us).All).Peak;

                Assert.That(field, Is.EqualTo(AiPower.EffectiveArmyPower(raid.Members)).Within(1e-3f));
                Assert.That(peak, Is.EqualTo(field).Within(1e-3f));
            }
            finally
            {
                MissionIntentRegistry.Clear();
            }
        }

        // ---- one live Attack operation -------------------------------------------------------

        [Test]
        public void LiveAttackOperation_IsAGatherAssaultOrReinforcement_NotAReturnLeg()
        {
            MissionIntent Intent(AttackMissionPhase phase) => new MissionIntent
            {
                Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Objective = new AttackIntent { Phase = phase },
            };
            Assert.That(AggressionMissionLayer.LiveAttackOperation(new[] { Intent(AttackMissionPhase.RecoveryReturn) }),
                Is.Null);
            Assert.That(AggressionMissionLayer.LiveAttackOperation(new[] { Intent(AttackMissionPhase.Gather) }),
                Is.Not.Null);
        }

        // ---- Housekeeping keeps garrison heroes in the garrison ------------------------------

        private static int _key = 1000;

        private static ReorgUnit RBody(float power) => new ReorgUnit
        {
            Key = _key++, Power = power, IsGroundCombatant = true, IsGroundBattleBody = true,
            MoveCurrent = 3, MoveMax = 3,
        };

        private static ReorgUnit RHero(int command, bool support) => new ReorgUnit
        {
            Key = _key++, IsHero = true, CommandRating = command, IsGroundCombatant = false,
            HeroRole = HeroOperationalRole.CombatLeader, MoveCurrent = 3, MoveMax = 3,
            TypeTags = support ? new[] { UnitTypeTag.Hero, UnitTypeTag.Support } : new[] { UnitTypeTag.Hero },
        };

        private static ReorgContainer RGarrison(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.Garrison, IsGarrison = true,
            CanReceive = true, CanDonate = true, CanChangeComposition = true,
            CanReorderCommander = true, SingletonExempt = true, Units = units.ToList(),
        };

        private static ReorgContainer RFree(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.NormalFieldArmy,
            CanReceive = true, CanDonate = true, CanChangeComposition = true,
            CanReorderCommander = true, Units = units.ToList(),
        };

        private static ReorganizationPlan RPlan(params ReorgContainer[] containers) =>
            ArmyReorganizationPlanner.Plan(new LocalForceGroup { Containers = containers.ToList() });

        [Test]
        public void GarrisonHero_InAFieldArmy_ReturnsToTheLocalGarrison()
        {
            ReorgUnit iri = RHero(8, support: true);
            ReorgContainer garrison = RGarrison(1, RBody(6f), RBody(6f));
            ReorgContainer field = RFree(2, iri, RBody(5f), RBody(5f));
            ReorganizationPlan plan = RPlan(garrison, field);
            Assert.That(plan.ExpectedMembership[1], Does.Contain(iri.Key), plan.DebugSummary());
        }

        [Test]
        public void GarrisonHero_NeverLeavesTheGarrisonToLeadAField()
        {
            ReorgUnit iri = RHero(8, support: true);
            ReorgContainer garrison = RGarrison(1, iri, RBody(6f), RBody(6f));
            ReorgContainer field = RFree(2, RBody(5f), RBody(5f));
            ReorganizationPlan plan = RPlan(garrison, field);
            Assert.That(plan.ExpectedMembership[1], Does.Contain(iri.Key), plan.DebugSummary());
        }

        // 2026-10-15 (user decision) — playtest: Aldric and Dorian stood as lone free heroes on
        // Draven's Base (-1,3) beside its garrison while Sentinels were one turn away. A lone hero
        // stays a bench in peace; under a near threat it shelters in the local garrison.
        [TestCase(false)]
        [TestCase(true)]
        public void LoneFreeHero_SheltersInTheLocalGarrisonOnlyUnderANearThreat(bool threatened)
        {
            ReorgUnit aldric = RHero(4, support: false);
            ReorgUnit tank = RBody(9f), medic = RBody(3f);
            ReorgContainer garrison = RGarrison(1, tank, medic);
            ReorgContainer lone = RFree(2, aldric);
            var group = new LocalForceGroup { Containers = new List<ReorgContainer> { garrison, lone } };
            if (threatened)
                group.ThreatBenchmarks.Add(new ReorgThreatBenchmark
                {
                    ArmyId = 11, EtaToGroup = 1, EtaToNearestBase = 1,
                    Members = new[] { new Game.Combat.WorthIt.DefenderProfile(3f, false, attack: 5f, hitPoints: 5f) },
                    TargetableUnitKeys = new HashSet<int> { tank.Key, medic.Key },
                });

            ReorganizationPlan plan = ArmyReorganizationPlanner.Plan(group);

            Assert.That(plan.ExpectedMembership[1].Contains(aldric.Key), Is.EqualTo(threatened),
                plan.DebugSummary());
        }
    }
}
#endif


