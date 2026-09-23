using System;
using Game.Ai.V2;
using Game.HexGrid;

namespace AttackTacticalSim
{
    // ATK stage 4 acceptance harness. Drives the REAL production rules:
    //   AttackTacticalOpportunity.RouteEconomicsAllowDetour (§15)
    //   AttackTacticalOpportunity.SignificanceFloor         (§12)
    //   AttackTacticalOpportunity.Prefer                    (§18)
    // No Unity world is needed: all three are pure functions over integers/floats and one value
    // struct, which is exactly why the route arithmetic was factored out of the world-dependent
    // eligibility sweep in the first place.
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            // ---- §15 route economics ----------------------------------------------------------
            Route_OnTheWay_IsAllowed();
            Route_DetourThatCostsAnExtraTurn_IsRejected();
            Route_ContactConsumingTheWholeTurn_IsRejected();
            Route_ContactEqualToBudget_IsRejectedStrictly();
            Route_NoAffordableNextStepOutOfContact_IsRejected();
            Route_UnreachableLeg_IsRejected();
            Route_FreeDetourOnTheDirectLine_IsAllowed();

            // ---- §12 significance -------------------------------------------------------------
            Significance_UsesShareOfOwnStrengthWhenItExceedsTheFloor();
            Significance_NeverDropsBelowTheAbsoluteFloor();

            // ---- §18 preference ---------------------------------------------------------------
            Prefer_StrongestSignificantArmyWins();
            Prefer_EqualStrength_SmallestDetourWins();
            Prefer_EqualStrengthAndDetour_SmallestContactCostWins();
            Prefer_EqualStrengthDetourAndContact_SmallestOnwardWins();
            Prefer_FullTie_FallsBackToStableArmyId();
            Prefer_AnythingBeatsNone_AndNoneNeverBeatsACandidate();

            if (_failures == 0)
            {
                Console.WriteLine("ALL PASS");
                return 0;
            }
            Console.WriteLine($"{_failures} FAILURE(S)");
            return 1;
        }

        // =======================================================================================
        //  §15
        // =======================================================================================

        // A 2-hex sidestep off a 6-cost march, with movement 4 per turn: via costs 6 as well, so
        // both routes are 2 turns and the detour is genuinely free.
        private static void Route_OnTheWay_IsAllowed() => ExpectRoute(
            nameof(Route_OnTheWay_IsAllowed),
            directCost: 6, contactCost: 2, onwardCost: 4,
            currentMovement: 4, maxMovement: 4, nextStepCost: 1,
            expected: true, expectedReason: null);

        // Same march, but the enemy sits 3 hexes off the line: via = 3 + 6 = 9 > 8, which is a
        // third turn. §15 forbids exactly this.
        private static void Route_DetourThatCostsAnExtraTurn_IsRejected() => ExpectRoute(
            nameof(Route_DetourThatCostsAnExtraTurn_IsRejected),
            directCost: 6, contactCost: 3, onwardCost: 6,
            currentMovement: 4, maxMovement: 4, nextStepCost: 1,
            expected: false, expectedReason: "delays_operation");

        private static void Route_ContactConsumingTheWholeTurn_IsRejected() => ExpectRoute(
            nameof(Route_ContactConsumingTheWholeTurn_IsRejected),
            directCost: 8, contactCost: 5, onwardCost: 3,
            currentMovement: 4, maxMovement: 4, nextStepCost: 1,
            expected: false, expectedReason: "not_reachable_this_turn");

        // The STRICT `<` of §15: contact that exactly empties the movement budget leaves nothing
        // to continue with and must be refused, even though the ETA arithmetic is satisfied.
        private static void Route_ContactEqualToBudget_IsRejectedStrictly() => ExpectRoute(
            nameof(Route_ContactEqualToBudget_IsRejectedStrictly),
            directCost: 8, contactCost: 4, onwardCost: 4,
            currentMovement: 4, maxMovement: 4, nextStepCost: 1,
            expected: false, expectedReason: "not_reachable_this_turn");

        // Contact leaves 1 MP, but the only onward step is a 2-cost hex: a route exists on paper
        // and no legal continuation exists in fact.
        private static void Route_NoAffordableNextStepOutOfContact_IsRejected() => ExpectRoute(
            nameof(Route_NoAffordableNextStepOutOfContact_IsRejected),
            directCost: 8, contactCost: 3, onwardCost: 5,
            currentMovement: 4, maxMovement: 4, nextStepCost: 2,
            expected: false, expectedReason: "no_affordable_next_step");

        private static void Route_UnreachableLeg_IsRejected() => ExpectRoute(
            nameof(Route_UnreachableLeg_IsRejected),
            directCost: 6, contactCost: 2, onwardCost: int.MaxValue,
            currentMovement: 4, maxMovement: 4, nextStepCost: 1,
            expected: false, expectedReason: "unreachable");

        // The enemy standing ON the direct line: via cost equals direct cost exactly.
        private static void Route_FreeDetourOnTheDirectLine_IsAllowed() => ExpectRoute(
            nameof(Route_FreeDetourOnTheDirectLine_IsAllowed),
            directCost: 7, contactCost: 3, onwardCost: 4,
            currentMovement: 5, maxMovement: 4, nextStepCost: 1,
            expected: true, expectedReason: null);

        // =======================================================================================
        //  §12
        // =======================================================================================

        private static void Significance_UsesShareOfOwnStrengthWhenItExceedsTheFloor()
        {
            float floor = AttackTacticalOpportunity.SignificanceFloor(40f);
            float expected = 40f * AiConfigV2.attackTacticalOpportunityMinStrengthShare;
            Check(nameof(Significance_UsesShareOfOwnStrengthWhenItExceedsTheFloor),
                Math.Abs(floor - expected) < 1e-4f && floor > AiConfigV2.attackTacticalOpportunityMinRawStrength,
                $"floor={floor} expected={expected}");
        }

        private static void Significance_NeverDropsBelowTheAbsoluteFloor()
        {
            float floor = AttackTacticalOpportunity.SignificanceFloor(1f);
            Check(nameof(Significance_NeverDropsBelowTheAbsoluteFloor),
                Math.Abs(floor - AiConfigV2.attackTacticalOpportunityMinRawStrength) < 1e-4f,
                $"floor={floor} absoluteFloor={AiConfigV2.attackTacticalOpportunityMinRawStrength}");
        }

        // =======================================================================================
        //  §18
        // =======================================================================================

        private static void Prefer_StrongestSignificantArmyWins()
        {
            // The weak one is cheaper on every tie-break; strength must still decide. This is the
            // one the spec calls out explicitly: "не выбирать самую слабую армию".
            AttackTacticalStrike strong = Strike(id: 9, raw: 20f, detour: 4, contact: 3, onward: 9);
            AttackTacticalStrike weak = Strike(id: 1, raw: 6f, detour: 0, contact: 1, onward: 5);
            Check(nameof(Prefer_StrongestSignificantArmyWins),
                AttackTacticalOpportunity.Prefer(strong, weak)
                && !AttackTacticalOpportunity.Prefer(weak, strong),
                "the stronger candidate must win despite a worse detour");
        }

        private static void Prefer_EqualStrength_SmallestDetourWins()
        {
            AttackTacticalStrike near = Strike(id: 9, raw: 10f, detour: 1, contact: 5, onward: 9);
            AttackTacticalStrike far = Strike(id: 1, raw: 10f, detour: 4, contact: 1, onward: 5);
            Check(nameof(Prefer_EqualStrength_SmallestDetourWins),
                AttackTacticalOpportunity.Prefer(near, far)
                && !AttackTacticalOpportunity.Prefer(far, near),
                "smaller detour must win at equal strength");
        }

        private static void Prefer_EqualStrengthAndDetour_SmallestContactCostWins()
        {
            AttackTacticalStrike close = Strike(id: 9, raw: 10f, detour: 2, contact: 1, onward: 9);
            AttackTacticalStrike distant = Strike(id: 1, raw: 10f, detour: 2, contact: 3, onward: 5);
            Check(nameof(Prefer_EqualStrengthAndDetour_SmallestContactCostWins),
                AttackTacticalOpportunity.Prefer(close, distant)
                && !AttackTacticalOpportunity.Prefer(distant, close),
                "smaller movement cost to contact must win");
        }

        private static void Prefer_EqualStrengthDetourAndContact_SmallestOnwardWins()
        {
            AttackTacticalStrike shortTail = Strike(id: 9, raw: 10f, detour: 2, contact: 2, onward: 4);
            AttackTacticalStrike longTail = Strike(id: 1, raw: 10f, detour: 2, contact: 2, onward: 7);
            Check(nameof(Prefer_EqualStrengthDetourAndContact_SmallestOnwardWins),
                AttackTacticalOpportunity.Prefer(shortTail, longTail)
                && !AttackTacticalOpportunity.Prefer(longTail, shortTail),
                "smaller remaining distance to the main target must win");
        }

        private static void Prefer_FullTie_FallsBackToStableArmyId()
        {
            AttackTacticalStrike low = Strike(id: 3, raw: 10f, detour: 2, contact: 2, onward: 4);
            AttackTacticalStrike high = Strike(id: 8, raw: 10f, detour: 2, contact: 2, onward: 4);
            Check(nameof(Prefer_FullTie_FallsBackToStableArmyId),
                AttackTacticalOpportunity.Prefer(low, high)
                && !AttackTacticalOpportunity.Prefer(high, low),
                "a full tie must resolve deterministically on ArmyId, never on sweep order");
        }

        private static void Prefer_AnythingBeatsNone_AndNoneNeverBeatsACandidate()
        {
            AttackTacticalStrike some = Strike(id: 4, raw: 3f, detour: 9, contact: 9, onward: 9);
            Check(nameof(Prefer_AnythingBeatsNone_AndNoneNeverBeatsACandidate),
                AttackTacticalOpportunity.Prefer(some, AttackTacticalStrike.None)
                && !AttackTacticalOpportunity.Prefer(AttackTacticalStrike.None, some),
                "None is the empty seed, never a competitor");
        }

        // ---- plumbing --------------------------------------------------------------------------

        private static AttackTacticalStrike Strike(int id, float raw, int detour, int contact,
            int onward) =>
            new AttackTacticalStrike(new HexCoord(id, id), id, $"enemy{id}", raw, 0.8f,
                contact, detour, onward);

        private static void ExpectRoute(string name, int directCost, int contactCost, int onwardCost,
            int currentMovement, int maxMovement, int nextStepCost, bool expected,
            string expectedReason)
        {
            bool allowed = AttackTacticalOpportunity.RouteEconomicsAllowDetour(directCost,
                contactCost, onwardCost, currentMovement, maxMovement, nextStepCost,
                out string reason);
            Check(name, allowed == expected && reason == expectedReason,
                $"allowed={allowed} (expected {expected}) reason={reason ?? "<none>"} "
                + $"(expected {expectedReason ?? "<none>"})");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                Console.WriteLine($"PASS  {name}");
                return;
            }
            _failures++;
            Console.WriteLine($"FAIL  {name} — {detail}");
        }
    }
}
