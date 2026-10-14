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
            host.TryReorderCommander(keep, out _);
            Assert.That(ArmyActions.TransferMembersAtomic(returned, host, home, null, out _), Is.True);
            var applied = new List<GroundCombatAssemblyTransfer>();
            Assert.That(GroundCombatAssemblyTransaction.Rollback(Us, host, applied,
                new AiTurnContext(), "raid", home, returned, old), Is.True);
            Assert.That(host.Commander, Is.SameAs(old));
            Assert.That(host.Members, Does.Contain(extra));
            Assert.That(home.Members.Count, Is.EqualTo(2));
            Assert.That(GroundCombatAssemblyTransaction.RemainingApplied(host, applied, returned, old), Is.Zero);
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
            float withRaid = WorldAnalysis.FieldStrikePotential(Us, ArmyRegistry.AllForOwner(Us), 100f);
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

                float field = WorldAnalysis.FieldStrikePotential(Us, ArmyRegistry.AllForOwner(Us), 100f);
                float peak = AttackForcePool.Build(Us, new SelfSnapshot { AvailablePower = 100f }).Peak;

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
    }
}
#endif


