using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;

namespace HousekeepingSim
{
    // Acceptance harness for Strategy V2 build-order step 8C — HousekeepingManager / Army &
    // Garrison Reorganization. Drives the PURE ArmyReorganizationPlanner with scripted
    // LocalForceGroup projections. Pins BEHAVIOUR (which structures are consolidated, which
    // invariants hold), not magnitudes.
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
            Scenario10_HealthyHexNoOp();
            Scenario11_EmptiedSourceStaysAShell();
            Scenario12_SoloRecceExempt();

            Console.WriteLine();
            Console.WriteLine($"housekeeping-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 00 config ----
        private static void Scenario00_ConfigInvariants()
        {
            Check("00 viability floor is positive", AiConfigV2.housekeepingViabilityPowerFloor > 0f);
            Check("00 min containers for a group is >= 2", AiConfigV2.housekeepingMinContainersForGroup >= 2);
            Check("00 plan iteration bound is finite and > 0", AiConfigV2.housekeepingMaxPlanIterationsPerHex > 0);
            Check("00 housekeeping AP reserve is 0 (free same-hex transfers)", AiConfigV2.housekeepingApReserve == 0f);
        }

        // ------------------------------------------------ 01 combine two singletons ----
        private static void Scenario01_CombineTwoSingletons()
        {
            var a = Field(1, U(4, range: 1));
            var b = Field(2, U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));

            Check("01 a plan is produced", !plan.IsEmpty);
            Check("01 exactly one transfer", plan.Transfers.Count == 1);
            int twoUnit = plan.ExpectedMembership.Count(kv => kv.Value.Count == 2);
            int empty = plan.ExpectedMembership.Count(kv => kv.Value.Count == 0);
            Check("01 one container ends with 2 members", twoUnit == 1);
            Check("01 the other ends an empty shell (still listed)", empty == 1 && plan.ExpectedMembership.Count == 2);
        }

        // ------------------------------------------------------------ 02 determinism ----
        private static void Scenario02_Deterministic()
        {
            List<(int, int, int)> Run()
            {
                _nextKey = 0;
                var a = Field(1, U(4, range: 1));
                var b = Field(2, U(4, range: 2));
                var c = Field(3, U(3, range: 1));
                return ArmyReorganizationPlanner.Plan(Group(a, b, c)).Transfers
                    .Select(t => (t.UnitKey, t.FromArmyId, t.ToArmyId)).ToList();
            }

            var first = Run();
            var second = Run();
            Check("02 identical input state -> identical plan", first.SequenceEqual(second));
        }

        // ------------------------------------------ 03 absorb singleton into viable ----
        private static void Scenario03_AbsorbSingletonIntoViable()
        {
            var weak = Field(5, U(4, range: 1));
            var viable = Field(2, Hero(6, cr: 5), U(4, range: 2)); // pow 10, cap 5
            var plan = ArmyReorganizationPlanner.Plan(Group(weak, viable));

            Check("03 a plan is produced", !plan.IsEmpty);
            Check("03 singleton folds into the viable army", plan.ExpectedMembership[2].Count == 3);
            Check("03 singleton container emptied", plan.ExpectedMembership[5].Count == 0);
        }

        // ---------------------------------------------- 04 lone hero not auto-exempt ----
        private static void Scenario04_LoneHeroNotExempt()
        {
            var loneHero = Field(7, Hero(5, cr: 2));                 // count 1, not a "singleton" shape, non-viable
            var viable = Field(2, Hero(5, cr: 5), U(4, range: 2));   // cap 5, room
            var plan = ArmyReorganizationPlanner.Plan(Group(loneHero, viable));

            Check("04 lone hero is reorganised, not left alone", !plan.IsEmpty);
            Check("04 lone hero folds into the viable army", plan.ExpectedMembership[7].Count == 0);
        }

        // --------------------------------------- 05 deposit a singleton into garrison ----
        private static void Scenario05_DepositSingletonIntoGarrison()
        {
            var garr = Garrison(1, floor: 2, U(2), U(2));   // 2 non-hero, cap 4, room for 2
            var single = Field(4, U(4, range: 1));
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, single));

            Check("05 a plan is produced", !plan.IsEmpty);
            Check("05 singleton unit ends up in the garrison", plan.ExpectedMembership[1].Count == 3);
            Check("05 field singleton emptied", plan.ExpectedMembership[4].Count == 0);
        }

        // ----------------------------------- 06 garrison floor blocks a donation out ----
        private static void Scenario06_GarrisonFloorBlocksDonation()
        {
            // Garrison FULL and exactly at floor via capacity; weak field army; no other donor.
            var garr = Garrison(1, floor: 2, U(2), U(2), U(2), U(2)); // 4/4, full
            var weak = Field(9, U(2, range: 1), U(2, range: 2));      // pow 4 < 6, non-viable
            var plan = ArmyReorganizationPlanner.Plan(Group(garr, weak));

            // The only structural fix would need the garrison to give up a defender it can't
            // spare AND the garrison has no room to absorb the weak army — leave it.
            Check("06 no illegal garrison raid — degraded state left as-is", plan.IsEmpty);
        }

        // ------------------------------------------------ 07 seed from a viable donor ----
        private static void Scenario07_SeedFromViableDonor()
        {
            var weak = Field(3, U(2, range: 1), U(2, range: 2));                       // pow 4, non-viable
            var donor = Field(2, Hero(6, cr: 6), U(5), U(5), U(5));                    // pow 21, cap 6
            var plan = ArmyReorganizationPlanner.Plan(Group(weak, donor));

            Check("07 a plan is produced", !plan.IsEmpty);
            // The non-viable formation is resolved either way: absorbed whole (preferred) or
            // topped up past the viability floor. It must NOT be left a non-viable occupied army.
            var weakEnd = plan.ExpectedMembership[3];
            // Absorbed whole (preferred) -> empty; or seeded past the viability floor (+1 pow-5
            // donor unit onto pow-4 clears it). Either way: no longer a non-viable occupied army.
            Check("07 non-viable weak army resolved (absorbed or made viable)",
                weakEnd.Count == 0 || weakEnd.Count >= 3);
            Check("07 donor keeps a viable roster", plan.ExpectedMembership[2].Count >= 2);
        }

        // -------------------------------- 08 donor never driven below viability floor ----
        private static void Scenario08_DonorNeverDrivenBelowViability()
        {
            var weak = Field(3, U(2, range: 1));                          // singleton
            var donor = Field(2, U(3, range: 1), U(3, range: 2));         // pow 6, viable but FULL (cap 2)
            var plan = ArmyReorganizationPlanner.Plan(Group(weak, donor));

            // Fold fails (donor full); seed would drop the donor to a single unit (non-viable).
            Check("08 no fix that breaks the only viable army — left as-is", plan.IsEmpty);
        }

        // ----------------------------------------------- 09 all containers protected ----
        private static void Scenario09_AllProtectedNoOp()
        {
            var a = Protected(1, U(4));
            var b = Protected(2, U(4));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));
            Check("09 all-protected group -> no-op plan", plan.IsEmpty);
        }

        // ------------------------------------------------------ 10 healthy hex no-op ----
        private static void Scenario10_HealthyHexNoOp()
        {
            var a = Field(1, Hero(6, cr: 4), U(4, range: 2)); // pow 10, viable
            var b = Field(2, Hero(6, cr: 4), U(4, range: 1)); // pow 10, viable
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));
            Check("10 two viable armies -> nothing to do", plan.IsEmpty);
        }

        // -------------------------------- 11 emptied source is preserved as a shell ----
        private static void Scenario11_EmptiedSourceStaysAShell()
        {
            var a = Field(1, U(4, range: 1));
            var b = Field(2, U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(a, b));
            int fromId = plan.Transfers[0].FromArmyId;
            Check("11 emptied source still in ExpectedMembership", plan.ExpectedMembership.ContainsKey(fromId));
            Check("11 emptied source has zero members (not deleted)", plan.ExpectedMembership[fromId].Count == 0);
        }

        // --------------------------------------------------- 12 solo recce is exempt ----
        private static void Scenario12_SoloRecceExempt()
        {
            var recce = SoloRecce(1, U(3, range: 1, recce: true));
            var viable = Field(2, Hero(6, cr: 5), U(4, range: 2));
            var plan = ArmyReorganizationPlanner.Plan(Group(recce, viable));
            Check("12 canonical solo recce left intact", plan.IsEmpty);
        }

        // ============================================================ builders ====

        private static ReorgUnit U(float power, int range = 1, bool recce = false) => new ReorgUnit
        {
            Key = _nextKey++, IsHero = false, CommandRating = 0, Power = power, Range = range, HasRecce = recce,
        };

        private static ReorgUnit Hero(float power, int cr) => new ReorgUnit
        {
            Key = _nextKey++, IsHero = true, CommandRating = cr, Power = power, Range = 1,
        };

        private static ReorgContainer Field(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.NormalFieldArmy, IsGarrison = false,
            Units = units.ToList(),
            CanDonate = true, CanReceive = true, CanChangeComposition = true,
            SingletonExempt = false, GarrisonNonHeroFloor = 0,
        };

        private static ReorgContainer Garrison(int id, int floor, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.Garrison, IsGarrison = true,
            Units = units.ToList(),
            CanDonate = true, CanReceive = true, CanChangeComposition = true,
            SingletonExempt = true, GarrisonNonHeroFloor = floor,
        };

        private static ReorgContainer Protected(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.ProtectedMissionArmy, IsGarrison = false,
            Units = units.ToList(),
            CanDonate = false, CanReceive = false, CanChangeComposition = false,
            SingletonExempt = true, GarrisonNonHeroFloor = 0,
        };

        private static ReorgContainer SoloRecce(int id, params ReorgUnit[] units) => new ReorgContainer
        {
            ArmyId = id, Role = ReorgPhysicalRole.SoloRecce, IsGarrison = false,
            Units = units.ToList(),
            CanDonate = false, CanReceive = false, CanChangeComposition = false,
            SingletonExempt = true, GarrisonNonHeroFloor = 0,
        };

        private static LocalForceGroup Group(params ReorgContainer[] containers) => new LocalForceGroup
        {
            Q = 0, R = 0, Containers = containers.OrderBy(c => c.ArmyId).ToList(),
        };

        // ============================================================ asserts ====

        private static void Check(string name, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
            else { _failed++; Console.WriteLine($"  FAIL  {name}"); }
        }
    }
}
