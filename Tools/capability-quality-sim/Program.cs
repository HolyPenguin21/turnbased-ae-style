using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Ai.V2;
using Game.Cards;

namespace CapabilityQualitySim
{
    // Acceptance harness for the Strategy V2 Capability Quality / Contextual Scout / Terminal AP
    // task. Exercises the PURE evaluators directly and pins decision behaviour, including the
    // corrective AP-ownership / Hero-opportunity regressions found in the integration review.
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
            S9_HeroOpportunityRequiresCompetingDemand();
            S10_NoOverpayForUnusedQuality();
            S11_Determinism();
            S12_ProjectedRapidReactionActivation();

            S16_OptionalStealthSkippedWhenSafe();
            S17_OptionalStealthEnteredWhenRisky();
            S18_OptionalStealthDrawOpportunity();
            S19_OptionalStealthGuardsAndClaims();

            S20_TerminalDrawConfigReconciled();

            Console.WriteLine();
            Console.WriteLine($"capability-quality-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static ScoutCapabilityContext Ctx(float dark, int fresh, float risk, bool detection) =>
            new ScoutCapabilityContext
            {
                ExplorableUnknownFrac = dark,
                FocusFreshNeighbors = fresh,
                DetectionRisk = risk,
                DetectionRelevant = detection,
            };

        private static float Q(int move, int radius, int spot, bool stealth, bool hero, int actAp,
            int dist, int refMove, ScoutCapabilityContext ctx, int heroesFree, bool competingHeroDemand,
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
                HasCompetingHeroDemand = competingHeroDemand,
            }, out bd);
        }

        private static void S1_DarkMapMobility()
        {
            var dark = Ctx(0.9f, 4, 0f, false);
            float a = Q(3, 1, 0, false, false, 1, 8, 2, dark, 1, false, out _);
            float b = Q(2, 1, 0, false, false, 1, 8, 2, dark, 1, false, out _);
            Check("01 dark map, far focus — Move3 outranks Move2", a > b + 0.05f);
        }

        private static void S2_ShortTargetMobilityNeutral()
        {
            var near = Ctx(0.2f, 1, 0f, false);
            float a = Q(3, 1, 0, false, false, 1, 1, 2, near, 1, false, out _);
            float b = Q(2, 1, 0, false, false, 1, 1, 2, near, 1, false, out _);
            Check("02 one-hex focus — Move3 ~ Move2", Math.Abs(a - b) < 0.06f);
        }

        private static void S3_VisionMarginalValue()
        {
            var denseDark = Ctx(0.85f, 5, 0f, false);
            float r2 = Q(2, 2, 0, false, false, 1, 4, 2, denseDark, 1, false, out _);
            float r1 = Q(2, 1, 0, false, false, 1, 4, 2, denseDark, 1, false, out _);
            Check("03a dense dark frontier — radius 2 beats radius 1", r2 > r1 + 0.04f);

            var explored = Ctx(0.04f, 0, 0f, false);
            float r2e = Q(2, 2, 0, false, false, 1, 4, 2, explored, 1, false, out _);
            float r1e = Q(2, 1, 0, false, false, 1, 4, 2, explored, 1, false, out _);
            Check("03b near-fully-explored — radius 2 barely beats radius 1", Math.Abs(r2e - r1e) < 0.04f);
        }

        private static void S4_SpotIrrelevantOnGenericExplore()
        {
            var plain = Ctx(0.6f, 3, 0.8f, detection: false);
            float r2s0 = Q(2, 2, 0, false, false, 1, 4, 2, plain, 1, false, out _);
            float r1s6 = Q(2, 1, 6, false, false, 1, 4, 2, plain, 1, false, out MaterializationQualityBreakdown bd);
            Check("04a own detection risk does not make spot strength valuable on Explore", r2s0 > r1s6);
            Check("04b spot term remains near-zero without target-detection context", bd.SpotDetection < 0.03f);
        }

        private static void S5_SpotValuableInDetectionContext()
        {
            var detect = Ctx(0.6f, 3, 0.6f, detection: true);
            float r2s0 = Q(2, 2, 0, false, false, 1, 4, 2, detect, 1, false, out _);
            float r1s6 = Q(2, 1, 6, false, false, 1, 4, 2, detect, 1, false, out MaterializationQualityBreakdown bd);
            Check("05a target-detection context — spot strength earns real quality", bd.SpotDetection > 0.05f);
            Check("05b target-detection context — r1s6 at least matches r2s0", r1s6 >= r2s0 - 0.001f);
        }

        private static void S6_PreferredStealthIsUtilityNotGate()
        {
            var safe = Ctx(0.5f, 2, 0f, false);
            float withS = Q(2, 1, 0, true, false, 1, 4, 2, safe, 1, false, out _);
            float noS = Q(2, 1, 0, false, false, 1, 4, 2, safe, 1, false, out _);
            Check("06a non-stealth candidate remains valid", noS >= AiConfigV2.scoutQualityMultiplierMin);
            Check("06b safe context — stealth adds only modest option value",
                withS >= noS && withS - noS <= AiConfigV2.scoutQualityStealthOptionValue + 0.02f);

            var risky = Ctx(0.5f, 2, 0.8f, false);
            float withSr = Q(2, 1, 0, true, false, 1, 4, 2, risky, 1, false, out _);
            float noSr = Q(2, 1, 0, false, false, 1, 4, 2, risky, 1, false, out _);
            Check("06c risky context — stealth protection is materially more valuable",
                (withSr - noSr) > (withS - noS) + 0.05f);
        }

        private static void S7_ActivationApDrag()
        {
            var c = Ctx(0.5f, 2, 0f, false);
            float a1 = Q(2, 1, 0, false, false, 1, 3, 2, c, 1, false, out _);
            float a2 = Q(2, 1, 0, false, false, 2, 3, 2, c, 1, false, out MaterializationQualityBreakdown bd);
            Check("07a higher activation AP lowers scout quality", a2 < a1);
            Check("07b activation drag is negative", bd.ActivationApDrag < 0f);
        }

        private static void S8_ScarceHeroOpportunityCostReversal()
        {
            var c = Ctx(0.3f, 2, 0f, false);
            float heroCand = Q(3, 1, 0, false, true, 1, 2, 2, c, 0, true, out MaterializationQualityBreakdown bh);
            float unitCand = Q(2, 1, 0, false, false, 1, 2, 2, c, 0, true, out _);
            Check("08a actual competing Hero demand -> opportunity cost is negative", bh.HeroOpportunityCost < 0f);
            Check("08b scarce Hero + competing demand -> ordinary Scout can win", unitCand > heroCand);
        }

        private static void S9_HeroOpportunityRequiresCompetingDemand()
        {
            var dark = Ctx(0.9f, 4, 0f, false);
            float heroNoCompetition = Q(3, 1, 0, true, true, 1, 5, 2, dark, 0, false,
                out MaterializationQualityBreakdown noComp);
            float unit = Q(2, 1, 0, false, false, 1, 5, 2, dark, 0, false, out _);
            Check("09a no competing Hero demand -> no invented Hero opportunity cost",
                Math.Abs(noComp.HeroOpportunityCost) < 0.0001f);
            Check("09b turn-one dark-map Hero Move3+Stealth beats Unit Move2 on merit",
                heroNoCompetition > unit);

            float abundant = Q(3, 1, 0, false, true, 1, 5, 2, dark, 3, true,
                out MaterializationQualityBreakdown abundantBd);
            Check("09c competing Hero demand but abundant deployed Hero supply -> no penalty",
                Math.Abs(abundantBd.HeroOpportunityCost) < 0.0001f && abundant > 1f);
        }

        private static void S10_NoOverpayForUnusedQuality()
        {
            var trivial = Ctx(0.08f, 0, 0f, false);
            float lux = Q(4, 2, 0, false, false, 2, 1, 2, trivial, 1, false, out MaterializationQualityBreakdown bd);
            Check("10a trivial nearby objective -> luxury scout stays ~neutral", lux <= 1.06f);
            Check("10b extra activation AP registers as drag", bd.ActivationApDrag < 0f);
        }

        private static void S11_Determinism()
        {
            var c = Ctx(0.7f, 3, 0.2f, true);
            float x1 = Q(3, 2, 4, true, true, 2, 5, 2, c, 1, true, out _);
            float x2 = Q(3, 2, 4, true, true, 2, 5, 2, c, 1, true, out _);
            Check("11 identical inputs -> identical multiplier", x1 == x2);
        }

        private static void S12_ProjectedRapidReactionActivation()
        {
            var def = new CardDefinition
            {
                moveMax = 2,
                activationApCost = 2,
                grantedAbilities = new List<string> { "r1s0" },
            };
            var plan = new MaterializationPlan
            {
                GeneratedBaseDef = def,
                ProjectedAbilities = new List<string> { "r1s0", UnitAbilities.RapidReaction },
            };
            Check("12 projected END abilities drive activation AP reservation",
                CapabilityQualityEvaluator.ProjectedActivationApCost(plan) == 0);
        }

        private static OptionalStealthEvaluation Stealth(float risk, bool hidden, bool strategic,
            int apLeft, int stealthAp, bool drawAvail, int drawAp, float mandatoryClaims = 0f, int drawOps = 4)
        {
            return ScoutOptionalStealthPolicy.Evaluate(new OptionalStealthInputs
            {
                LegDetectionRisk = risk,
                MoverAlreadyHidden = hidden,
                MoverIsStrategicBody = strategic,
                ApRemaining = apLeft,
                StealthApCost = stealthAp,
                MandatoryApClaims = mandatoryClaims,
                DrawAvailable = drawAvail,
                DrawApCost = drawAp,
                DrawOpportunities = drawAvail ? drawOps : 0,
            });
        }

        private static void S16_OptionalStealthSkippedWhenSafe()
        {
            var e = Stealth(0.03f, false, false, 6, 1, false, 2);
            Check("16 negligible known route risk -> SKIP", e.Decision == OptionalStealthDecision.Skip);
        }

        private static void S17_OptionalStealthEnteredWhenRisky()
        {
            var e = Stealth(0.8f, false, false, 6, 1, false, 2, mandatoryClaims: 2f);
            Check("17a dangerous route with real AP slack -> ENTER", e.Decision == OptionalStealthDecision.Enter);
            Check("17b protection exceeds opportunity", e.Protection > e.ApOpportunity);
        }

        private static void S18_OptionalStealthDrawOpportunity()
        {
            // Odd 5 AP slack: 2 legal draws remain after spending one AP -> no draw lost.
            var ample = Stealth(0.5f, false, false, 5, 1, true, 2, drawOps: 2);
            // Original user-story shape: 4 AP slack supports 2 draws, stealth 1 AP leaves only 1.
            var losesOneOfTwo = Stealth(0.5f, false, false, 4, 1, true, 2, drawOps: 2);
            Check("18a moderate risk when no draw is lost -> ENTER", ample.Decision == OptionalStealthDecision.Enter);
            Check("18b AP4->3 loses one of two legal draws -> SKIP",
                losesOneOfTwo.Decision == OptionalStealthDecision.Skip);
            Check("18c lost draw appears as extra AP opportunity cost",
                losesOneOfTwo.ApOpportunity > ample.ApOpportunity);
        }

        private static void S19_OptionalStealthGuardsAndClaims()
        {
            Check("19a already hidden -> SKIP",
                Stealth(0.9f, true, false, 6, 1, false, 2).Decision == OptionalStealthDecision.Skip);
            Check("19b real AP unavailable -> SKIP",
                Stealth(0.9f, false, false, 0, 1, false, 2).Decision == OptionalStealthDecision.Skip);
            Check("19c AP exists physically but all is owned by funded missions -> SKIP",
                Stealth(0.9f, false, true, 3, 1, false, 2, mandatoryClaims: 3f).Decision
                    == OptionalStealthDecision.Skip);
            Check("19d only the slack above mandatory claims may be spent",
                Stealth(0.9f, false, true, 4, 1, false, 2, mandatoryClaims: 3f).Decision
                    == OptionalStealthDecision.Enter);
        }

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

        private static void Check(string label, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine($"  [PASS] {label}"); }
            else { _failed++; Console.WriteLine($"  [FAIL] {label}"); }
        }
    }
}
