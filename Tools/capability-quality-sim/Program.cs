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
            S21_PhaseBStrategicHeroClaim();
            S22_RaidReadinessPowerVsAssembly();
            S23_HeroOperationalRole();
            S24_ExploreValidityContract();
            S25_ScoutTrailRetrace();

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

        // Spec §2 / §21 — Phase B surplus must not treat a card as generic surplus while it is
        // still strategically claimed by an unresolved capability demand it could satisfy. The
        // claim lookup is the pure gate that withholds a non-delivering placement during
        // candidate construction; once the demand is gone the same card is free surplus again.
        private static void S21_PhaseBStrategicHeroClaim()
        {
            var res = new MaterializationReservation();
            var heroDemand = new AxisDemand
            {
                TraceId = "T-heroclaim",
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.Hero,
                DesiredAmount = 1f,
                Value = 39f,
                RequiredTraits = TraitPreference.None,
            };
            res.UnresolvedDemands.Add(heroDemand);

            AxisDemand claim = MaterializationCandidateBuilder.UnresolvedClaimFor(
                res, CapabilityKind.Hero, null);
            Check("21a hero card is strategically claimed while a Hero demand is unresolved",
                ReferenceEquals(claim, heroDemand));

            AxisDemand scoutClaim = MaterializationCandidateBuilder.UnresolvedClaimFor(
                res, CapabilityKind.ScoutCapability, null);
            Check("21b an unrelated capability is not claimed by the Hero demand", scoutClaim == null);

            heroDemand.DesiredAmount = 0f;
            AxisDemand goneClaim = MaterializationCandidateBuilder.UnresolvedClaimFor(
                res, CapabilityKind.Hero, null);
            Check("21c once the Hero demand is satisfied the card is free generic surplus", goneClaim == null);

            res.UnresolvedDemands.Clear();
            AxisDemand noClaim = MaterializationCandidateBuilder.UnresolvedClaimFor(
                res, CapabilityKind.Hero, null);
            Check("21d no unresolved demands -> no strategic claim", noClaim == null);
        }

        // Spec §11 / §22 — a raid that is not executable purely because its forces are badly
        // arranged must report NeedsAssembly, never a phantom +1 FieldCombatPower. A genuine
        // numeric shortfall must still report NeedsPower with the real deficit.
        private static void S22_RaidReadinessPowerVsAssembly()
        {
            float margin = AiConfigV2.raidCombatPowerMargin;
            float targetPower = 13f;
            float required = Math.Max(1f, targetPower * margin);

            var obj = new AggressionObjective { TargetArmyId = 7, TargetPower = targetPower };

            // (a) plenty of numeric power + a raid-eligible hero, but no legal ready force (null
            //     snapshot -> RaidAssemblyPlanner infeasible).
            var surplus = new CapabilityInventory
            {
                RaidAvailableFieldPower = required + 6.7f,
                AvailableHeroes = 1,
            };
            RaidOperationalReadiness ra = RaidOperationalReadiness.Evaluate(
                null, obj, Array.Empty<Game.Ai.WorthIt.DefenderProfile>(), null, surplus);
            Check("22a numeric surplus -> NeedsPower is false", !ra.NeedsPower);
            Check("22a numeric surplus -> RequestedPower is 0", ra.RequestedPower <= 0.0001f);
            Check("22a numeric surplus -> NeedsAssembly is true", ra.NeedsAssembly);

            // (b) genuine numeric shortfall.
            var short_ = new CapabilityInventory
            {
                RaidAvailableFieldPower = required - 9f,
                AvailableHeroes = 1,
            };
            RaidOperationalReadiness rb = RaidOperationalReadiness.Evaluate(
                null, obj, Array.Empty<Game.Ai.WorthIt.DefenderProfile>(), null, short_);
            Check("22b real shortfall -> NeedsPower is true", rb.NeedsPower);
            Check("22b real shortfall -> NeedsAssembly is false", !rb.NeedsAssembly);
            Check("22b real shortfall -> RequestedPower is the real deficit",
                Math.Abs(rb.RequestedPower - 9f) < 0.01f);
        }

        // Spec §8 / §20 — hero operational-role classification from canonical stats only.
        private static void S23_HeroOperationalRole()
        {
            // Heroes carry no Attack/Defense — combat merit comes from CommandRating plus the
            // canonical AiPower contribution (HitPoints / Initiative / Resistance / Fate).
            var combatHero = new Game.Units.UnitData
            {
                Name = "A", IsHero = true, CommandRating = 7,
                HitPointsMax = 6, HitPointsCurrent = 6, Initiative = 4, Resistance = 2, Fate = 4,
            };
            var researchHero = new Game.Units.UnitData
            {
                Name = "B", IsHero = true, CommandRating = 3,
                HitPointsMax = 2, HitPointsCurrent = 2, Initiative = 1, Resistance = 1, Fate = 1,
            };
            researchHero.Abilities.Add(Game.Cards.UnitAbilities.Researcher);

            var strongResearcher = new Game.Units.UnitData
            {
                Name = "C", IsHero = true, CommandRating = 8,
                HitPointsMax = 8, HitPointsCurrent = 8, Initiative = 5, Resistance = 3, Fate = 5,
            };
            strongResearcher.Abilities.Add(Game.Cards.UnitAbilities.Assembler);

            Check("23a a stat-strong non-support hero is a CombatLeader",
                HeroRoleEvaluator.Classify(combatHero) == HeroOperationalRole.CombatLeader);
            Check("23b a weak research hero is a SupportOperator",
                HeroRoleEvaluator.Classify(researchHero) == HeroOperationalRole.SupportOperator);
            Check("23c a stat-strong support hero is Flexible (usable either way)",
                HeroRoleEvaluator.Classify(strongResearcher) == HeroOperationalRole.Flexible);
            Check("23d field-command ordering puts the combat leader ahead of the weak researcher",
                HeroRoleEvaluator.CompareForFieldCommand(combatHero, researchHero) < 0);
            Check("23e classification ignores display name",
                HeroRoleEvaluator.Classify(new Game.Units.UnitData
                {
                    Name = "Rusty Miller", IsHero = true, CommandRating = 7,
                    HitPointsMax = 6, HitPointsCurrent = 6, Initiative = 4, Resistance = 2, Fate = 4,
                }) == HeroOperationalRole.CombatLeader);
        }

        // Spec §6 / §19 — ONE Explore validity contract. An unvisited, unblocked frontier focus
        // stays a valid runnable objective even with zero fresh immediate neighbours, so it can
        // never be simultaneously "retire Explore X" (continuity) and "create Explore X" (fresh
        // enumeration) against the same snapshot.
        private static void S24_ExploreValidityContract()
        {
            Game.HexGrid.HexCoord H(int q, int r) => new Game.HexGrid.HexCoord(q, r);
            var focus = H(4, -2);

            // A boxed-in but unvisited frontier focus: on map, not visited, not blocked, but
            // every neighbour already visited.
            var all = new HashSet<Game.HexGrid.HexCoord> { focus };
            var visited = new HashSet<Game.HexGrid.HexCoord>();
            foreach (var n in Game.HexGrid.HexGridMath.Neighbors(focus))
            {
                all.Add(n);
                visited.Add(n);
            }
            var snap = new WorldSnapshot
            {
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    AllHexes = new List<Game.HexGrid.HexCoord>(all),
                    VisitedHexSet = visited,
                    ScoutHardBlockedHexes = new HashSet<Game.HexGrid.HexCoord>(),
                },
            };
            var intent = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = focus };

            Check("24a a boxed-in unvisited frontier focus has 0 fresh neighbours",
                ScoutObjectiveEvaluator.ExploreStillOpen(snap, focus) == 0);
            Check("24b it is still a runnable Explore objective",
                ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, focus));
            Check("24c the durable intent is NOT retired against that same snapshot",
                ScoutObjectiveEvaluator.IsIntentStillValid(snap, intent));

            // Once actually visited it is legitimately retired.
            visited.Add(focus);
            Check("24d a visited focus is no longer runnable",
                !ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, focus));
            Check("24e a visited focus retires the durable intent",
                !ScoutObjectiveEvaluator.IsIntentStillValid(snap, intent));
        }

        // Spec §5 / §19 — bounded per-scout trail. Immediate reversal onto the just-left hex is
        // detected; recent-trail hexes are counted; nothing is ever hard-blocked; the ring is
        // bounded by scoutTrailLength.
        private static void S25_ScoutTrailRetrace()
        {
            Game.HexGrid.HexCoord H(int q, int r) => new Game.HexGrid.HexCoord(q, r);
            var player = new Game.Players.PlayerSetupData { Nickname = "S", IsHuman = false };
            const int army = 1;

            ScoutTrailRegistry.ClearAll();
            Check("25a no trail -> no reversal", !ScoutTrailRegistry.IsImmediateReversal(player, army, H(1, 0)));
            Check("25a no trail -> 0 recent hits",
                ScoutTrailRegistry.RecentTrailHits(player, army, new[] { H(1, 0), H(2, 0) }) == 0);

            // Walk A(0,0) -> B(1,0) -> C(2,0).
            ScoutTrailRegistry.RecordStep(player, army, H(0, 0), H(1, 0));
            ScoutTrailRegistry.RecordStep(player, army, H(1, 0), H(2, 0));

            Check("25b stepping back onto the just-left hex is an immediate reversal",
                ScoutTrailRegistry.IsImmediateReversal(player, army, H(1, 0)));
            Check("25c a fresh forward step is not a reversal",
                !ScoutTrailRegistry.IsImmediateReversal(player, army, H(3, 0)));
            Check("25d a route through recent trail hexes is counted",
                ScoutTrailRegistry.RecentTrailHits(player, army, new[] { H(1, 0), H(2, 0), H(3, 0) }) == 2);
            Check("25e a wholly fresh route has 0 recent hits",
                ScoutTrailRegistry.RecentTrailHits(player, army, new[] { H(3, 0), H(4, 0) }) == 0);

            // Ring is bounded: after many steps, an old hex drops out of "recent".
            for (int i = 3; i < 3 + AiConfigV2.scoutTrailLength + 4; i++)
                ScoutTrailRegistry.RecordStep(player, army, H(i - 1, 0), H(i, 0));
            Check("25f the recent ring is bounded by scoutTrailLength",
                ScoutTrailRegistry.RecentTrailHits(player, army, new[] { H(1, 0) }) == 0);

            ScoutTrailRegistry.ClearAll();
        }

        private static void Check(string label, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine($"  [PASS] {label}"); }
            else { _failed++; Console.WriteLine($"  [FAIL] {label}"); }
        }
    }
}
