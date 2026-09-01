using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;

namespace HousekeepingSim
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static int _nextKey;

        private static int Main()
        {
            Scenario00_ConfigInvariants();
            Scenario01_CombineTwoSingletons();
            Scenario02_Deterministic();
            Scenario03_AbsorbSingletonIntoViable();
            Scenario04_LoneHeroNotExempt();
            Scenario05_DepositSingletonIntoGarrison();
            Scenario06_GarrisonFloorBlocksDonation();
            Scenario07_SeedFromViableDonor();
            Scenario08_DonorNeverDrivenBelowViability();
            Scenario09_AllProtectedNoOp();
            Scenario10_BalancedHealthyHexNoOp();
            Scenario11_EmptiedSourceStaysAShell();
            Scenario12_SoloRecceExempt();
            Scenario13_ActivatedDestinationWouldSpendAp_Blocked();
            Scenario14_ActivatedDestinationZeroApUnit_Allowed();
            Scenario15_HealthyFullArmiesCanImproveCompositionBySwap();
            Scenario16_FirstHeroCapacityOrderMirrorsGameplay();
            Scenario17_NoUnitTouchedTwiceInOnePlan();
            Scenario18_GarrisonPromotesHighestCapacityHeroToCommander();
            Scenario19_CommanderReorderRaisesModelledCapacity();
            Scenario20_CommanderTieBreakIsDeterministic();
            Scenario21_LeasedSingletonNeverFolded();
            Scenario22_AssignsCombatLeaderToHerolessTwoBodyArmy();
            Scenario23_PrefersCombatHeroOverSupportHero();
            Scenario24_SupportHeroStaysWhenBetterLeaderExists();
            Scenario25_GarrisonSecurityNotBrokenToFormArmy();

            Console.WriteLine();
            Console.WriteLine($"housekeeping-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Scenario00_ConfigInvariants()
        {
            Check("00 viability floor is positive", AiConfigV2.housekeepingViabilityPowerFloor > 0f);
            Check("00 min containers for a group is >= 2", AiConfigV2.housekeepingMinContainersForGroup >= 2);
            Check("00 plan iteration bound is finite and > 0", AiConfigV2.housekeepingMaxPlanIterationsPerHex > 0);
            Check("00 housekeeping AP reserve is 0", AiConfigV2.housekeepingApReserve == 0f);
        }

        private static void Scenario01_CombineTwoSingletons()
        {
            var a = Field(1, U(4, range: 1));
            var b = Field(2, U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));

            Check("01 a plan is produced", !plan.IsEmpty);
            Check("01 exactly one operation", plan.Transfers.Count == 1);
            int twoUnit = plan.ExpectedMembership.Count(kv => kv.Value.Count == 2);
            int empty = plan.ExpectedMembership.Count(kv => kv.Value.Count == 0);
            Check("01 one container ends with 2 members", twoUnit == 1);
            Check("01 the other ends an empty shell (still listed)", empty == 1 && plan.ExpectedMembership.Count == 2);
        }

        private static void Scenario02_Deterministic()
        {
            List<(int, int, int, int)> Run()
            {
                _nextKey = 0;
                var a = Field(1, U(4, range: 1));
                var b = Field(2, U(4, range: 2));
                var c = Field(3, U(3, range: 1));
                return ArmyReorganizationPlanner.Plan(Group(a, b, c)).Transfers
                    .Select(t => (t.UnitKey, t.FromArmyId, t.ToArmyId, t.SwapUnitKey)).ToList();
            }

            var first = Run();
            var second = Run();
            Check("02 identical input state -> identical plan", first.SequenceEqual(second));
        }

        private static void Scenario03_AbsorbSingletonIntoViable()
        {
            var weak = Field(5, U(4, range: 1));
            var viable = Field(2, Hero(6, cr: 5), U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(weak, viable));

            Check("03 a plan is produced", !plan.IsEmpty);
            Check("03 singleton folds into the viable army", plan.ExpectedMembership[2].Count == 3);
            Check("03 singleton container emptied", plan.ExpectedMembership[5].Count == 0);
        }

        private static void Scenario04_LoneHeroNotExempt()
        {
            var loneHero = Field(7, Hero(5, cr: 2));
            var viable = Field(2, Hero(5, cr: 5), U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(loneHero, viable));

            Check("04 lone hero is reorganised, not left alone", !plan.IsEmpty);
            Check("04 lone hero folds into the viable army", plan.ExpectedMembership[7].Count == 0);
        }

        private static void Scenario05_DepositSingletonIntoGarrison()
        {
            var garr = Garrison(1, floor: 2, U(2), U(2));
            var single = Field(4, U(4, range: 1));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, single));

            Check("05 a plan is produced", !plan.IsEmpty);
            Check("05 singleton unit ends up in the garrison", plan.ExpectedMembership[1].Count == 3);
            Check("05 field singleton emptied", plan.ExpectedMembership[4].Count == 0);
        }

        private static void Scenario06_GarrisonFloorBlocksDonation()
        {
            var garr = Garrison(1, floor: 2, Hero(6, cr: 4), Hero(5, cr: 4), U(2), U(2));
            var weak = Field(9, U(2, range: 1), U(2, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, weak));
            Check("06 no illegal garrison raid — degraded state left as-is", plan.IsEmpty);
        }

        private static void Scenario07_SeedFromViableDonor()
        {
            var weak = Field(3, U(2, range: 1), U(2, range: 2));
            var donor = Field(2, Hero(6, cr: 6), U(5), U(5), U(5));
            var plan = ArmyReorganizationPlanner.Plan(Group(weak, donor));

            Check("07 a plan is produced", !plan.IsEmpty);
            var weakEnd = plan.ExpectedMembership[3];
            Check("07 non-viable weak army resolved (absorbed or made viable)", weakEnd.Count == 0 || weakEnd.Count >= 3);
            Check("07 donor keeps a viable roster", plan.ExpectedMembership[2].Count >= 2);
        }

        private static void Scenario08_DonorNeverDrivenBelowViability()
        {
            var weak = Field(3, U(2, range: 1));
            var donor = Field(2, U(4, range: 1), U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(weak, donor));
            Check("08 no fix that breaks the only viable army — left as-is", plan.IsEmpty);
        }

        private static void Scenario09_AllProtectedNoOp()
        {
            var a = Protected(1, U(4));
            var b = Protected(2, U(4));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));
            Check("09 all-protected group -> no-op plan", plan.IsEmpty);
        }

        private static void Scenario10_BalancedHealthyHexNoOp()
        {
            var a = Field(1, Hero(6, cr: 4), U(4, range: 2));
            var b = Field(2, Hero(6, cr: 4), U(4, range: 1));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));
            Check("10 healthy hex with no strict improvement -> no-op", plan.IsEmpty);
        }

        private static void Scenario11_EmptiedSourceStaysAShell()
        {
            var a = Field(1, U(4, range: 1));
            var b = Field(2, U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));
            int fromId = plan.Transfers[0].FromArmyId;
            Check("11 emptied source still in ExpectedMembership", plan.ExpectedMembership.ContainsKey(fromId));
            Check("11 emptied source has zero members (not deleted)", plan.ExpectedMembership[fromId].Count == 0);
        }

        private static void Scenario12_SoloRecceExempt()
        {
            var recce = SoloRecce(1, U(3, range: 1, recce: true));
            var viable = Field(2, Hero(6, cr: 5), U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(recce, viable));
            Check("12 canonical solo recce left intact", plan.IsEmpty);
        }

        private static void Scenario13_ActivatedDestinationWouldSpendAp_Blocked()
        {
            var single = Field(1, U(9, activationApCost: 1));
            var fullViable = Field(2, Hero(8, cr: 2), U(8, range: 2));
            fullViable.HasActivatedThisTurn = true;
            var plan = ArmyReorganizationPlanner.Plan(Group(single, fullViable));
            Check("13 non-zero AP transfer into activated destination is not planned", plan.IsEmpty);
        }

        private static void Scenario14_ActivatedDestinationZeroApUnit_Allowed()
        {
            var single = Field(1, U(9, activationApCost: 0));
            var viableWithRoom = Field(2, Hero(8, cr: 5), U(8, range: 2));
            viableWithRoom.HasActivatedThisTurn = true;
            var plan = ArmyReorganizationPlanner.Plan(Group(single, viableWithRoom));
            Check("14 zero-AP unit may enter activated destination", !plan.IsEmpty);
            Check("14 zero-AP singleton is absorbed", plan.ExpectedMembership[1].Count == 0);
        }

        private static void Scenario15_HealthyFullArmiesCanImproveCompositionBySwap()
        {
            var a = Field(1,
                Hero(10, cr: 3),
                U(8, range: 1, tag: UnitTypeTag.Armored),
                U(8, range: 1, tag: UnitTypeTag.Armored));
            var b = Field(2,
                Hero(10, cr: 3),
                U(8, range: 2, tag: UnitTypeTag.Mechanical),
                U(8, range: 2, tag: UnitTypeTag.Mechanical));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));
            Check("15 healthy full armies are considered for composition improvement", !plan.IsEmpty);
            Check("15 full/full composition improvement uses direct swap", plan.Transfers.Any(t => t.IsSwap));
        }

        private static void Scenario16_FirstHeroCapacityOrderMirrorsGameplay()
        {
            var firstHero = Hero(4, cr: 4);
            var secondHero = Hero(9, cr: 2);
            var roster = new List<ReorgUnit> { firstHero, secondHero, U(3), U(3) };
            Check("16 current capacity uses the canonical first hero", ReorgViability.Capacity(roster, false) == 4);
            Check("16 removing first hero exposes second hero's lower capacity and is rejected",
                !ReorgViability.CanLeaveWithoutOvercrowding(roster, firstHero, false));
        }

        private static void Scenario17_NoUnitTouchedTwiceInOnePlan()
        {
            var a = Field(1, U(4, range: 1));
            var b = Field(2, U(4, range: 2));
            var c = Field(3, Hero(8, cr: 5), U(5, range: 1), U(5, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b, c));
            var touched = new List<int>();
            foreach (PlannedTransfer t in plan.Transfers)
            {
                touched.Add(t.UnitKey);
                if (t.IsSwap)
                    touched.Add(t.SwapUnitKey);
            }
            Check("17 planner never schedules the same unit twice", touched.Count == touched.Distinct().Count());
        }

        // §7 — a garrison holding two heroes, the weaker one first, is reordered so the
        // highest-CommandRating hero becomes commander. Zero-AP, membership preserved.
        private static void Scenario18_GarrisonPromotesHighestCapacityHeroToCommander()
        {
            _nextKey = 0;
            var weak = Hero(3, cr: 2);
            var strong = Hero(3, cr: 6);
            var garr = Garrison(1, floor: 0, weak, strong, U(3), U(3));
            var field = Field(2, Hero(4, cr: 3), U(8));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));

            Check("18 a commander reorder is planned", plan.Transfers.Count == 1 && plan.Transfers[0].IsReorder);
            Check("18 reorder targets the garrison", plan.Transfers[0].FromArmyId == 1);
            Check("18 the promoted hero is the higher-CommandRating one", plan.Transfers[0].UnitKey == strong.Key);
            Check("18 expected roster now leads with the strong hero",
                plan.ExpectedMembership[1].Count > 0 && plan.ExpectedMembership[1][0] == strong.Key);
            Check("18 membership is unchanged (nothing added/removed)",
                plan.ExpectedMembership[1].Count == 4
                && plan.ExpectedMembership[1].OrderBy(k => k).SequenceEqual(
                    new[] { weak.Key, strong.Key }.Concat(
                        plan.ExpectedMembership[1].Where(k => k != weak.Key && k != strong.Key)).OrderBy(k => k)));
        }

        // §7 — the reorder uses the canonical ArmyData.ComputeCapacity semantics mirrored by
        // ReorgViability.Capacity; after it, the modelled capacity is the strong hero's rating.
        private static void Scenario19_CommanderReorderRaisesModelledCapacity()
        {
            _nextKey = 0;
            var weak = Hero(3, cr: 2);
            var strong = Hero(3, cr: 7);
            var before = new List<ReorgUnit> { weak, strong, U(3), U(3) };
            Check("19 capacity before reorder is the weak hero's rating", ReorgViability.Capacity(before, true) == 2);
            var after = new List<ReorgUnit> { strong, weak, U(3), U(3) };
            Check("19 capacity after reorder is the strong hero's rating", ReorgViability.Capacity(after, true) == 7);

            var garr = Garrison(1, floor: 0, weak, strong, U(3), U(3));
            var field = Field(2, Hero(4, cr: 3), U(8));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));
            var reordered = plan.ExpectedMembership[1]
                .Select(k => new[] { weak, strong }.FirstOrDefault(h => h.Key == k) ?? U0(k)).ToList();
            Check("19 planned order yields the canonical higher capacity",
                ReorgViability.Capacity(reordered, true) == 7);
        }

        // §7 — equal capacity heroes trigger no reorder (and no churn); identical input yields
        // an identical plan across repeated runs.
        private static void Scenario20_CommanderTieBreakIsDeterministic()
        {
            (int count, int firstKey) Run()
            {
                _nextKey = 0;
                var h1 = Hero(3, cr: 4);
                var h2 = Hero(3, cr: 4);
                var garr = Garrison(1, floor: 0, h1, h2, U(3), U(3));
                var field = Field(2, Hero(4, cr: 3), U(8));
                var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));
                return (plan.Transfers.Count, plan.Transfers.Count > 0 ? plan.Transfers[0].UnitKey : -1);
            }
            var a = Run();
            var b = Run();
            Check("20 equal-CR heroes produce no reorder", a.count == 0);
            Check("20 repeated runs are identical", a == b);

            (int, int, bool) Run3()
            {
                _nextKey = 0;
                var mid = Hero(3, cr: 3);
                var top = Hero(3, cr: 8);
                var low = Hero(3, cr: 2);
                var garr = Garrison(1, floor: 0, mid, top, low, U(3), U(3), U(3));
                var field = Field(2, Hero(4, cr: 3), U(8));
                var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));
                return (plan.Transfers.Count, plan.Transfers.Count > 0 ? plan.Transfers[0].UnitKey : -1,
                    plan.Transfers.Count > 0 && plan.Transfers[0].IsReorder);
            }
            var c1 = Run3();
            var c2 = Run3();
            Check("20 three-hero best pick is deterministic", c1 == c2 && c1.Item3);
        }

        // §7/§10 — a strategically-leased singleton (ProtectedMissionArmy) is never folded away,
        // even next to a viable field army that could absorb it.
        private static void Scenario21_LeasedSingletonNeverFolded()
        {
            _nextKey = 0;
            var leased = Protected(1, U(5));
            var viable = Field(2, U(4), U(4));
            var spare = Field(3, U(4), U(4));
            var plan = ArmyReorganizationPlanner.Plan(Group(leased, viable, spare));
            bool touchesLeased = plan.Transfers.Any(t => t.FromArmyId == 1 || t.ToArmyId == 1);
            Check("21 leased singleton container is never touched", !touchesLeased);
            Check("21 leased singleton keeps its member",
                !plan.ExpectedMembership.ContainsKey(1) || plan.ExpectedMembership[1].Count == 1);
        }

        // §9 — a heroless viable two-body field army next to a garrison with a spare combat hero
        // gets that hero (via a hero-for-body swap, since a heroless 2-body army is at capacity).
        private static void Scenario22_AssignsCombatLeaderToHerolessTwoBodyArmy()
        {
            _nextKey = 0;
            var leader = Hero(3, cr: 5, HeroOperationalRole.CombatLeader);
            var garr = Garrison(1, floor: 1, leader, U(3), U(3));
            var field = Field(2, U(6), U(6));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));

            bool heroReachesField = plan.ExpectedMembership.TryGetValue(2, out var f)
                && f.Contains(leader.Key);
            Check("22 the benched combat hero ends up leading the field formation", heroReachesField);
            Check("22 the field formation is no longer heroless", heroReachesField);
            Check("22 garrison keeps its non-hero security floor",
                plan.ExpectedMembership[1].Count(k => k != leader.Key) >= 2 - 0 /* U keys */
                && plan.ExpectedMembership[1].Count >= 2);
        }

        // §9/§8 — with both a combat hero and a support hero benched, the combat hero is the one
        // sent to the field formation.
        private static void Scenario23_PrefersCombatHeroOverSupportHero()
        {
            _nextKey = 0;
            var support = Hero(2, cr: 6, HeroOperationalRole.SupportOperator);
            var leader = Hero(3, cr: 4, HeroOperationalRole.CombatLeader);
            var garr = Garrison(1, floor: 1, support, leader, U(3), U(3));
            var field = Field(2, U(6), U(6));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));

            bool leaderToField = plan.ExpectedMembership.TryGetValue(2, out var f) && f.Contains(leader.Key);
            bool supportStays = plan.ExpectedMembership[1].Contains(support.Key);
            Check("23 the combat leader goes to the field formation", leaderToField);
            Check("23 the support hero is preserved in the garrison", supportStays);
        }

        // §9/§8 — a lone support hero is NOT dragged into a field formation just because one is
        // heroless; support heroes stay for base/research/production duty.
        private static void Scenario24_SupportHeroStaysWhenBetterLeaderExists()
        {
            _nextKey = 0;
            var support = Hero(2, cr: 6, HeroOperationalRole.SupportOperator);
            var garr = Garrison(1, floor: 1, support, U(3), U(3));
            var field = Field(2, U(6), U(6));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));
            bool supportMoved = plan.ExpectedMembership.TryGetValue(2, out var f) && f.Contains(support.Key);
            Check("24 a lone support hero is not pulled into the field formation", !supportMoved);
        }

        // §9 — the hero-for-formation move never drops a garrison below its non-hero security floor.
        private static void Scenario25_GarrisonSecurityNotBrokenToFormArmy()
        {
            _nextKey = 0;
            var leader = Hero(3, cr: 5, HeroOperationalRole.CombatLeader);
            // floor 2, exactly 2 non-hero members -> a hero-out/body-in swap is fine (headcount
            // preserved), but a plain hero donation that also needed a body later must not breach it.
            var garr = Garrison(1, floor: 2, leader, U(3), U(3));
            var field = Field(2, U(6), U(6));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, field));
            int garrNonHeroEnd = plan.ExpectedMembership[1].Count(k => k != leader.Key);
            Check("25 garrison never falls below its non-hero floor", garrNonHeroEnd >= 2);
        }

        private static ReorgUnit U0(int key) => new ReorgUnit
        {
            Key = key, IsHero = false, CommandRating = 0, Power = 3, Range = 1,
            TypeTags = new[] { UnitTypeTag.Infantry },
        };

        private static ReorgUnit U(float power, int range = 1, bool recce = false,
            UnitTypeTag tag = UnitTypeTag.Infantry, int activationApCost = 0) => new ReorgUnit
        {
            Key = _nextKey++, IsHero = false, CommandRating = 0, Power = power, Range = range,
            TypeTags = new[] { tag }, ActivationApCost = activationApCost, HasRecce = recce,
        };

        private static ReorgUnit Hero(float power, int cr,
            HeroOperationalRole role = HeroOperationalRole.CombatLeader) => new ReorgUnit
        {
            Key = _nextKey++, IsHero = true, CommandRating = cr, Power = power, Range = 1,
            TypeTags = new[] { UnitTypeTag.Hero }, ActivationApCost = 0,
            HeroRole = role, HeroCombatLeadership = cr + power,
        };

        private static ReorgContainer Field(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.NormalFieldArmy, IsGarrison = false,
            Units = units.ToList(), CanDonate = true, CanReceive = true, CanChangeComposition = true,
            SingletonExempt = false, GarrisonNonHeroFloor = 0,
        };

        private static ReorgContainer Garrison(int id, int floor, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.Garrison, IsGarrison = true,
            Units = units.ToList(), CanDonate = true, CanReceive = true, CanChangeComposition = true,
            SingletonExempt = true, GarrisonNonHeroFloor = floor,
        };

        private static ReorgContainer Protected(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.ProtectedMissionArmy, IsGarrison = false,
            Units = units.ToList(), CanDonate = false, CanReceive = false, CanChangeComposition = false,
            SingletonExempt = true, GarrisonNonHeroFloor = 0,
        };

        private static ReorgContainer SoloRecce(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.SoloRecce, IsGarrison = false,
            Units = units.ToList(), CanDonate = false, CanReceive = false, CanChangeComposition = false,
            SingletonExempt = true, GarrisonNonHeroFloor = 0,
        };

        private static LocalForceGroup Group(params ReorgContainer[] containers) => new LocalForceGroup
        {
            Q = 0, R = 0, Containers = containers.OrderBy(c => c.ArmyId).ToList(),
        };

        private static void Check(string name, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
            else { _failed++; Console.WriteLine($"  FAIL  {name}"); }
        }
    }
}
