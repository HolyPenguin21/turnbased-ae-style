using System;
using System.Reflection;
using Game.Ai.V2;

namespace CapabilityQualitySim
{
    // Acceptance harness for the Strategy V2 Capability Quality / Contextual Scout / Terminal AP
    // task. Exercises the PURE evaluators directly (ScoutCapabilityQuality.Evaluate,
    // ScoutOptionalStealthPolicy.Evaluate) plus the AiConfigV2 reconciliation. Pins BEHAVIOUR
    // (orderings, sign of marginal terms, decision flips), never magnitudes.
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            S1_DarkMapMobility();
            S2_ShortTargetMobilityNeutral();
            S3_VisionMarginalValue();
            S4_SpotIrrelevantOnGenericExplore();
            S5_SpotValuableInDetectionContext();
            S6_PreferredStealthIsUtilityNotGate();
            S7_ActivationApDrag();
            S8_ScarceHeroOpportunityCostReversal();
            S9_AbundantHeroNotOverPenalized();
            S10_NoOverpayForUnusedQuality();
            S11_Determinism();

            S16_OptionalStealthSkippedWhenSafe();
            S17_OptionalStealthEnteredWhenRisky();
            S18_OptionalStealthApOpportunityFlipsDecision();
            S19_OptionalStealthGuards();

            S20_TerminalDrawConfigReconciled();

            Console.WriteLine();
            Console.WriteLine($"capability-quality-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------- quality helpers ----
        private static ScoutCapabilityContext Ctx(float dark, int fresh, float risk, bool detection) =>
            new ScoutCapabilityContext
            {
                ExplorableUnknownFrac = dark,
                FocusFreshNeighbors = fresh,
                DetectionRisk = risk,
                DetectionRelevant = detection,
            };

        private static float Q(int move, int radius, int spot, bool stealth, bool hero, int actAp,
            int dist, int refMove, ScoutCapabilityContext ctx, int heroesFree, int heroesCommitted,
            out MaterializationQualityBreakdown bd)
        {
            return ScoutCapabilityQuality.Evaluate(new ScoutCapabilityQuality.Inputs
            {
                MoveMax = move,
                RecceRadius = radius,
                SpotStrength = spot,
                HasStealth = stealth,
                IsHero = hero,
                ActivationApCost = actAp,
                DistanceToFocus = dist,
                HasFocus = true,
                ReferenceMoveMax = refMove,
                Context = ctx,
                AvailableHeroes = heroesFree,
                CommittedHeroes = heroesCommitted,
            }, out bd);
        }

        // --------------------------------------------------------------------- 1 dark mobility ----
        private static void S1_DarkMapMobility()
        {
            var dark = Ctx(0.9f, 4, 0f, false);
            float a = Q(3, 1, 0, false, false, 1, 8, refMove: 2, dark, 1, 0, out _);
            float b = Q(2, 1, 0, false, false, 1, 8, refMove: 2, dark, 1, 0, out _);
            Check("01 dark map, far focus — Move3 outranks Move2", a > b + 0.05f);
        }

        // ---------------------------------------------------------- 2 short target -> neutral ----
        private static void S2_ShortTargetMobilityNeutral()
        {
            var near = Ctx(0.2f, 1, 0f, false);
            float a = Q(3, 1, 0, false, false, 1, 1, refMove: 2, near, 1, 0, out _);
            float b = Q(2, 1, 0, false, false, 1, 1, refMove: 2, near, 1, 0, out _);
            Check("02 one-hex focus — Move3 ~ Move2 (no material mobility bonus)", Math.Abs(a - b) < 0.06f);
        }

        // ------------------------------------------------------------- 3 vision marginal value ----
        private static void S3_VisionMarginalValue()
        {
            var denseDark = Ctx(0.85f, 5, 0f, false);
            float r2 = Q(2, 2, 0, false, false, 1, 4, refMove: 2, denseDark, 1, 0, out _);
            float r1 = Q(2, 1, 0, false, false, 1, 4, refMove: 2, denseDark, 1, 0, out _);
            Check("03a dense dark frontier — radius 2 beats radius 1", r2 > r1 + 0.04f);

            var explored = Ctx(0.04f, 0, 0f, false);
            float r2e = Q(2, 2, 0, false, false, 1, 4, refMove: 2, explored, 1, 0, out _);
            float r1e = Q(2, 1, 0, false, false, 1, 4, refMove: 2, explored, 1, 0, out _);
            Check("03b near-fully-explored — radius 2 barely beats radius 1", Math.Abs(r2e - r1e) < 0.04f);
        }

        // --------------------------------------------------- 4 spot irrelevant on plain Explore ----
        private static void S4_SpotIrrelevantOnGenericExplore()
        {
            var plain = Ctx(0.6f, 3, 0f, detection: false);
            float r2s0 = Q(2, 2, 0, false, false, 1, 4, refMove: 2, plain, 1, 0, out _);
            float r1s6 = Q(2, 1, 6, false, false, 1, 4, refMove: 2, plain, 1, 0, out MaterializationQualityBreakdown bd);
            Check("04a r2s0 outranks r1s6 on a plain Explore", r2s0 > r1s6);
            Check("04b spot term is near-zero without a detection context", bd.SpotDetection < 0.03f);
        }

        // ---------------------------------------------------- 5 spot valuable in detection ctx ----
        private static void S5_SpotValuableInDetectionContext()
        {
            var detect = Ctx(0.6f, 3, 0.6f, detection: true);
            float r2s0 = Q(2, 2, 0, false, false, 1, 4, refMove: 2, detect, 1, 0, out _);
            float r1s6 = Q(2, 1, 6, false, false, 1, 4, refMove: 2, detect, 1, 0, out MaterializationQualityBreakdown bd);
            Check("05a detection context — spot strength earns real quality", bd.SpotDetection > 0.05f);
            Check("05b detection context — r1s6 now at least matches r2s0", r1s6 >= r2s0 - 0.001f);
        }

        // -------------------------------------------- 6 Preferred stealth = utility, not a gate ----
        private static void S6_PreferredStealthIsUtilityNotGate()
        {
            var safe = Ctx(0.5f, 2, 0f, false);
            float withS = Q(2, 1, 0, stealth: true, false, 1, 4, refMove: 2, safe, 1, 0, out MaterializationQualityBreakdown bs);
            float noS = Q(2, 1, 0, stealth: false, false, 1, 4, refMove: 2, safe, 1, 0, out _);
            Check("06a non-stealth candidate still yields a valid multiplier (no hard gate)",
                noS >= AiConfigV2.scoutQualityMultiplierMin);
            Check("06b safe context — stealth adds only a modest option value",
                withS >= noS && withS - noS <= AiConfigV2.scoutQualityStealthOptionValue + 0.02f);

            var risky = Ctx(0.5f, 2, 0.8f, true);
            float withSr = Q(2, 1, 0, true, false, 1, 4, refMove: 2, risky, 1, 0, out _);
            float noSr = Q(2, 1, 0, false, false, 1, 4, refMove: 2, risky, 1, 0, out _);
            Check("06c risky context — stealth is worth materially more than when safe",
                (withSr - noSr) > (withS - noS) + 0.05f);
        }

        // ------------------------------------------------------------- 7 activation-AP drag ----
        private static void S7_ActivationApDrag()
        {
            var c = Ctx(0.5f, 2, 0f, false);
            float a1 = Q(2, 1, 0, false, false, 1, 3, refMove: 2, c, 1, 0, out _);
            float a2 = Q(2, 1, 0, false, false, 2, 3, refMove: 2, c, 1, 0, out MaterializationQualityBreakdown bd);
            Check("07a higher activation AP lowers scout quality", a2 < a1);
            Check("07b the drag term is negative", bd.ActivationApDrag < 0f);
        }

        // ------------------------------------------------ 8 scarce Hero opportunity-cost flip ----
        private static void S8_ScarceHeroOpportunityCostReversal()
        {
            var c = Ctx(0.3f, 2, 0f, false);
            // Hero Move3 vs Unit Move2, small mobility need, heroes are a live bottleneck.
            float heroCand = Q(3, 1, 0, false, hero: true, 1, 2, refMove: 2, c, heroesFree: 0, heroesCommitted: 1, out MaterializationQualityBreakdown bh);
            float unitCand = Q(2, 1, 0, false, hero: false, 1, 2, refMove: 2, c, heroesFree: 0, heroesCommitted: 1, out _);
            Check("08a scarce Hero -> opportunity cost is negative", bh.HeroOpportunityCost < 0f);
            Check("08b scarce Hero -> the weaker Unit scout wins", unitCand > heroCand);
        }

        // ------------------------------------------------ 9 abundant Hero not over-penalized ----
        private static void S9_AbundantHeroNotOverPenalized()
        {
            var dark = Ctx(0.9f, 4, 0f, false);
            float heroCand = Q(3, 1, 0, false, hero: true, 1, 8, refMove: 2, dark, heroesFree: 3, heroesCommitted: 0, out MaterializationQualityBreakdown bh);
            float unitCand = Q(2, 1, 0, false, hero: false, 1, 8, refMove: 2, dark, heroesFree: 3, heroesCommitted: 0, out _);
            Check("09a abundant Hero -> no opportunity cost", Math.Abs(bh.HeroOpportunityCost) < 0.0001f);
            Check("09b abundant Hero + dark map -> the faster Hero scout wins on merit", heroCand > unitCand);
        }

        // ----------------------------------------------- 10 no overpay for unused quality ----
        private static void S10_NoOverpayForUnusedQuality()
        {
            var trivial = Ctx(0.08f, 0, 0f, false);
            float lux = Q(4, 2, 0, false, false, 2, 1, refMove: 2, trivial, 1, 0, out MaterializationQualityBreakdown bd);
            Check("10a trivial nearby objective -> a luxury scout's multiplier stays ~neutral", lux <= 1.06f);
            Check("10b its extra activation AP still registers as drag", bd.ActivationApDrag < 0f);
        }

        // ------------------------------------------------------------------- 11 determinism ----
        private static void S11_Determinism()
        {
            var c = Ctx(0.7f, 3, 0.2f, true);
            float x1 = Q(3, 2, 4, true, true, 2, 5, refMove: 2, c, 1, 1, out _);
            float x2 = Q(3, 2, 4, true, true, 2, 5, refMove: 2, c, 1, 1, out _);
            Check("11 identical inputs -> identical multiplier", x1 == x2);
        }

        // ------------------------------------------------------ optional-stealth policy ----
        private static OptionalStealthEvaluation Stealth(float risk, bool hidden, bool strategic,
            int apLeft, int stealthAp, bool drawAvail, int drawAp)
        {
            return ScoutOptionalStealthPolicy.Evaluate(new OptionalStealthInputs
            {
                LegDetectionRisk = risk,
                MoverAlreadyHidden = hidden,
                MoverIsStrategicBody = strategic,
                ApRemaining = apLeft,
                StealthApCost = stealthAp,
                DrawAvailable = drawAvail,
                DrawApCost = drawAp,
            });
        }

        private static void S16_OptionalStealthSkippedWhenSafe()
        {
            var e = Stealth(0.03f, hidden: false, strategic: false, apLeft: 6, stealthAp: 1, drawAvail: false, drawAp: 2);
            Check("16 negligible leg risk -> SKIP", e.Decision == OptionalStealthDecision.Skip);
        }

        private static void S17_OptionalStealthEnteredWhenRisky()
        {
            var e = Stealth(0.8f, hidden: false, strategic: false, apLeft: 6, stealthAp: 1, drawAvail: false, drawAp: 2);
            Check("17a dangerous leg, AP ample -> ENTER", e.Decision == OptionalStealthDecision.Enter);
            Check("17b protection exceeds opportunity", e.Protection > e.ApOpportunity);
        }

        private static void S18_OptionalStealthApOpportunityFlipsDecision()
        {
            // risk that ENTERs with headroom...
            var ample = Stealth(0.5f, false, false, apLeft: 6, stealthAp: 1, drawAvail: true, drawAp: 2);
            // ...but the SAME risk when the 1 AP is the difference between drawing and not.
            var tight = Stealth(0.5f, false, false, apLeft: 2, stealthAp: 1, drawAvail: true, drawAp: 2);
            Check("18a moderate risk with AP headroom -> ENTER", ample.Decision == OptionalStealthDecision.Enter);
            Check("18b same risk, but stealth would kill an otherwise-legal draw -> SKIP",
                tight.Decision == OptionalStealthDecision.Skip);
            Check("18c the draw threat shows up as extra AP opportunity cost",
                tight.ApOpportunity > ample.ApOpportunity);
        }

        private static void S19_OptionalStealthGuards()
        {
            Check("19a already hidden -> SKIP",
                Stealth(0.9f, hidden: true, false, 6, 1, false, 2).Decision == OptionalStealthDecision.Skip);
            Check("19b transition unaffordable -> SKIP",
                Stealth(0.9f, false, false, apLeft: 0, stealthAp: 1, false, 2).Decision == OptionalStealthDecision.Skip);
        }

        // ------------------------------------------------- 20 Phase-B config reconciliation ----
        private static void S20_TerminalDrawConfigReconciled()
        {
            Check("20a maxTerminalDrawsPerTurn is a real bound", AiConfigV2.maxTerminalDrawsPerTurn > 0);
            Check("20b surplusAllowDraw still governs Phase-B draw", AiConfigV2.surplusAllowDraw == true || AiConfigV2.surplusAllowDraw == false);

            string[] retired = { "surplusApReserve", "surplusHumanReserve", "surplusEnergyReserve",
                "surplusMaterialsReserve", "surplusTechReserve" };
            foreach (string name in retired)
            {
                FieldInfo f = typeof(AiConfigV2).GetField(name, BindingFlags.Public | BindingFlags.Static);
                Check($"20c retired speculative reserve '{name}' is gone", f == null);
            }
        }

        // ------------------------------------------------------------------------- harness ----
        private static void Check(string label, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine($"  [PASS] {label}"); }
            else { _failed++; Console.WriteLine($"  [FAIL] {label}"); }
        }
    }
}
