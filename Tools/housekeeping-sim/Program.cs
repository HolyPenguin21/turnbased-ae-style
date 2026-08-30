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

        private static ReorgUnit U(float power, int range = 1, bool recce = false,
            UnitTypeTag tag = UnitTypeTag.Infantry, int activationApCost = 0) => new ReorgUnit
        {
            Key = _nextKey++, IsHero = false, CommandRating = 0, Power = power, Range = range,
            TypeTags = new[] { tag }, ActivationApCost = activationApCost, HasRecce = recce,
        };

        private static ReorgUnit Hero(float power, int cr) => new ReorgUnit
        {
            Key = _nextKey++, IsHero = true, CommandRating = cr, Power = power, Range = 1,
            TypeTags = new[] { UnitTypeTag.Hero }, ActivationApCost = 0,
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
