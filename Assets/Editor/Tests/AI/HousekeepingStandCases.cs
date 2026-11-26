#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // The Housekeeping stand's cases (HousekeepingStand). Each case runs on every deck pair; a
    // deck that lacks a role the case needs ignores the case. Every turn is also held to the
    // stand's standing rules (hex hold, garrison floor, contracts, AP, stability).
    internal sealed class StandCase
    {
        public readonly string Id;
        public readonly string Text;
        public readonly StandSite Site;
        public readonly Action<HousekeepingStand> Run;

        public StandCase(string id, string text, Action<HousekeepingStand> run, StandSite site = StandSite.Citadel)
        {
            Id = id; Text = text; Run = run; Site = site;
        }
    }

    internal static class HousekeepingStandCases
    {
        internal static readonly List<StandCase> All = new List<StandCase>();

        private static void Case(string id, string text, Action<HousekeepingStand> run,
            StandSite site = StandSite.Citadel) => All.Add(new StandCase(id, text, run, site));

        internal static StandCase Find(string id) => All.First(c => c.Id == id);

        // Cases whose expectation contradicts the code as it stands and is not settled yet. A
        // failing pending case reports Inconclusive with its note instead of failing the gate;
        // the entry is removed once the code is fixed. (Q1 peace fold and Q2 garrison commander:
        // decided by the owner 2026-11-25 and fixed - no longer pending.)
        internal static readonly Dictionary<string, string> Pending = new Dictionary<string, string>
        {
            // (E12, 2026-11-25: the "different reading" was the planner's stale threat rows - fixed.)
        };

        static HousekeepingStandCases()
        {
            PeaceSorting();
            ReturnsAndEnds();
            ActiveTasks();
            PreparationHost();
            Threats();
            Constraints();
            OwnerScenarios();
        }

        // ---- A. Sorting on a quiet hex -------------------------------------------------------
        private static void PeaceSorting()
        {
            // 2026-09-30 (owner) - a garrison hero (Support tag) belongs to the local garrison.
            Case("A01_SupportHeroInField_ReturnsToGarrison",
                "garrison 2 strong; a Support hero leads a field pair -> the Support hero goes home", s =>
            {
                UnitData sup = s.Hero(HeroPick.Support);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(sup, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectInGarrison(sup);
            });

            Case("A02_SupportHeroInGarrison_NeverLeadsAField",
                "garrison Support hero + 2 strong; a heroless field pair -> the Support hero stays home", s =>
            {
                UnitData sup = s.Hero(HeroPick.Support);
                s.Garrison(sup, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectInGarrison(sup);
            });

            Case("A03_LoneCombatHero_LeadsHerolessPair",
                "a lone combat hero and a heroless pair -> the hero leads the pair, no new army", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat);
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(hero);
                s.Field(mid, s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectCommands(hero, mid);
                s.ExpectNoArmyTakenIntoUse();
            });

            Case("A04_FreeFieldArmy_ReleasesSecondaryHero",
                "a free field army with two combat heroes and a fighter -> one hero leaves, the fighter stays", s =>
            {
                UnitData strong = s.Body(BodyPick.Strong);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                ArmyData field = s.Field(s.Hero(HeroPick.Combat), s.Hero(HeroPick.Combat2), strong);
                s.EndTurn();
                s.ExpectHeroes(field, 1);
                s.ExpectIn(strong, field);
            });

            // 2026-11-25 (owner, stand A23: "if the army does not gather 7 fighters, the hero with the
            // active skill is the better commander") — a free army seats only the bodies it has: a
            // bigger Command seats nobody more, the commander's Fate and initiative decide.
            Case("A05_FewBodies_TheBetterFighterCommands",
                "a free field army of two heroes and one body -> the hero with the better Fate/initiative commands", s =>
            {
                UnitData small = s.Hero(HeroPick.SmallCommand);
                UnitData big = s.Hero(HeroPick.BigCommand);
                if (big.CommandRating <= small.CommandRating)
                    Assert.Ignore("no command difference between the deck's heroes");
                if (small.Fate == big.Fate && small.Initiative == big.Initiative)
                    Assert.Ignore("the deck's heroes fight alike");
                UnitData mid = s.Body(BodyPick.Mid);
                // A one-body garrison has nothing to give: the army seats its one body only.
                s.Garrison(s.Body(BodyPick.Strong));
                s.Field(small, big, mid);
                s.EndTurn();
                bool smallBetter = small.Fate > big.Fate || small.Fate == big.Fate && small.Initiative > big.Initiative;
                s.ExpectCommands(smallBetter ? small : big, mid, "capacity beyond the seated bodies decides nothing");
            });

            Case("A05b_BodiesNeedTheCommand_BiggerCommandLeads",
                "the same army with more bodies than the smaller command seats -> the bigger commands", s =>
            {
                UnitData small = s.Hero(HeroPick.SmallCommand);
                UnitData big = s.Hero(HeroPick.BigCommand);
                if (big.CommandRating <= small.CommandRating)
                    Assert.Ignore("no command difference between the deck's heroes");
                UnitData[] bodies = s.Bodies(BodyPick.Mid, Math.Min(big.CommandRating - 2, small.CommandRating - 1));
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(new[] { small, big }.Concat(bodies).ToArray());
                s.EndTurn();
                s.ExpectCommands(big, bodies[0], "the smaller command cannot seat them all");
            });

            Case("A06_SingletonBody_FoldsIntoGarrison",
                "garrison 1 strong; a lone weak body in the field -> it joins the garrison", s =>
            {
                UnitData weak = s.Body(BodyPick.Weak);
                s.Garrison(s.Body(BodyPick.Strong));
                s.Field(weak);
                s.EndTurn();
                s.ExpectInGarrison(weak);
            });

            Case("A07_TwoSingletons_FullGarrison_Merge",
                "a full heroless garrison and two lone bodies -> the two stand together", s =>
            {
                UnitData a = s.Body(BodyPick.Mid), b = s.Body(BodyPick.Mid);
                s.Garrison(s.Bodies(BodyPick.Strong, 4));
                s.Field(a);
                s.Field(b);
                s.EndTurn();
                s.ExpectTogether(a, b);
            });

            // 2026-11-26 (owner) — the led field army is the hex's fist: it takes the garrison's
            // bodies above the garrison's own share; the garrison keeps a body.
            Case("A08_Fist_TakesTheGarrisonSurplus",
                "garrison Support hero + strong + mid, a led field army, an empty army -> the fist takes the "
                + "garrison's surplus, the empty army stays empty", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                ArmyData fist = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData shell = s.Shell();
                s.EndTurn();
                s.ExpectStrengthened(fist, "the fist takes the garrison's surplus");
                s.ExpectEmpty(shell);
            });

            Case("A09_EmptyArmy_NotSeededInPeace",
                "garrison 2 strong, a led field army with 3 bodies, an empty army -> the empty army stays empty", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid), s.Body(BodyPick.Weak));
                ArmyData shell = s.Shell();
                s.EndTurn();
                s.ExpectEmpty(shell);
            });

            // 2026-10-15 (owner, Draven Base (-1,3)) - a lone hero is a bench in peace (E04: it shelters under a near threat).
            Case("A10_LoneHero_StaysABenchInPeace",
                "garrison strong + weak; a lone combat hero -> it stays out of the garrison", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Weak));
                s.Field(hero);
                s.EndTurn();
                s.ExpectNotInGarrison(hero);
            });

            // Three real hero benches are never folded just to reduce non-viable containers.
            Case("A11_ThreeHeroBenches_NotFolded",
                "three lone heroes beside the garrison -> they stay three benches", s =>
            {
                UnitData a = s.Hero(HeroPick.Combat), b = s.Hero(HeroPick.Combat2), c = s.Hero(HeroPick.SmallCommand);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(a);
                s.Field(b);
                s.Field(c);
                s.EndTurn();
                s.ExpectAlone(a);
                s.ExpectAlone(b);
                s.ExpectAlone(c);
            });

            Case("A12_CombatHeroInGarrison_LeadsHerolessField",
                "a combat hero sits in the garrison, a heroless field pair beside it -> the hero leads the pair", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat);
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(hero, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(mid, s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectCommands(hero, mid);
            });

            Case("A13_SupportLedField_TakesBenchedCombatHero",
                "a Support hero leads a field pair, a lone combat hero beside -> combat hero leads, Support steps off", s =>
            {
                UnitData sup = s.Hero(HeroPick.Support);
                UnitData hero = s.Hero(HeroPick.Combat);
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(sup, mid, s.Body(BodyPick.Mid));
                s.Field(hero);
                s.EndTurn();
                s.ExpectCommands(hero, mid);
                // 2026-11-18 (owner) - the garrison or a hero-only bench on the hex.
                s.ExpectNoSupportHeroInFightingField();
            });

            Case("A14_GarrisonHeroOrder_HighestCommandFirst",
                "a garrison listed with its weaker Support hero first -> the stronger one commands", s =>
            {
                UnitData big = s.Hero(HeroPick.Support), small = s.Hero(HeroPick.Support2);
                if (big.CommandRating <= small.CommandRating)
                    Assert.Ignore("no command difference between the Support heroes");
                ArmyData garrison = s.GarrisonRaw(small, big, s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
                s.ExpectIn(big, garrison);
                if (garrison.Commander != big)
                    s.Failures.Add($"T1: expected {s.Label(big)} to command the garrison, got {s.Label(garrison.Commander)}");
            });

            Case("A15_IdleScout_IsAnOrdinarySingleton",
                "a lone scout with no recon work -> folded, not left alone", s =>
            {
                UnitData recce = s.Body(BodyPick.Recce);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(recce);
                s.EndTurn();
                s.ExpectNotAlone(recce);
            });

            Case("A16_ScoutWithWork_StaysAlone",
                "a lone scout with recon work -> untouched", s =>
            {
                UnitData recce = s.Body(BodyPick.Recce);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData scout = s.Field(recce);
                s.ScoutWork(scout);
                s.EndTurn();
                s.ExpectUnchanged(scout);
            });

            Case("A17_SoloCollector_StaysAlone",
                "a lone collector parked on the hex -> untouched", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData collector = s.Field(s.Body(BodyPick.Collector));
                s.EndTurn();
                s.ExpectUnchanged(collector);
            });

            Case("A18_AirWing_Untouched",
                "an aircraft wing beside a loose layout -> the wing is never repackaged", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Weak));
                ArmyData air = s.AirWing(s.Body(BodyPick.Air));
                s.EndTurn();
                s.ExpectUnchanged(air);
            });

            Case("A19_FullHeroArmy_LooseBodyGoesHome",
                "a hero army at full capacity and a loose body -> the body joins the garrison", s =>
            {
                UnitData hero = s.Hero(HeroPick.SmallCommand);
                if (hero.CommandRating < 2)
                    Assert.Ignore("hero command too small");
                UnitData loose = s.Body(BodyPick.Weak);
                s.Garrison(s.Body(BodyPick.Strong));
                s.Field(new[] { hero }.Concat(s.Bodies(BodyPick.Mid, hero.CommandRating - 1)).ToArray());
                s.Field(loose);
                s.EndTurn();
                s.ExpectInGarrison(loose);
            });

            Case("A20_FullGarrison_LoneBodyWithNowhereToGo",
                "a full heroless garrison and one lone body -> the body stays as it is", s =>
            {
                UnitData weak = s.Body(BodyPick.Weak);
                s.Garrison(s.Bodies(BodyPick.Strong, 4));
                s.Field(weak);
                s.EndTurn();
                s.ExpectAlone(weak);
            });

            Case("A21_TwoBenches_OneLeadsThePair",
                "two lone combat heroes and a heroless pair -> exactly one hero leads the pair", s =>
            {
                UnitData a = s.Hero(HeroPick.Combat), b = s.Hero(HeroPick.Combat2);
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(a);
                s.Field(b);
                s.Field(mid, s.Body(BodyPick.Mid));
                s.EndTurn();
                ArmyData pair = s.ArmyOf(mid);
                s.ExpectHeroes(pair, 1);
            });

            Case("A22_HeroArmyWithRoom_TakesTheLoneBody",
                "a full garrison, a hero army with room, a lone body -> the body joins the hero army", s =>
            {
                UnitData strong = s.Body(BodyPick.Strong), mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Bodies(BodyPick.Strong, 4));
                s.Field(s.Hero(HeroPick.BigCommand), strong);
                s.Field(mid);
                s.EndTurn();
                s.ExpectTogether(mid, strong);
            });

            Case("A23_ArmySupportSecondaryHero_StaysWithItsArmy",
                "a free field army with a Heal/Drill/Transport second hero -> that hero stays", s =>
            {
                UnitData support = s.Hero(HeroPick.ArmySupport);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData field = s.Field(s.Hero(HeroPick.Combat), support, s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectIn(support, field);
            });

            Case("A24_GarrisonOnly_NothingToDo",
                "only a garrison on the hex -> no operation", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectNoOperations();
            });
        }

        // ---- B. Armies coming back, tasks ending ---------------------------------------------
        private static void ReturnsAndEnds()
        {
            Case("B01_RaidReturned_BecomesTheFist",
                "garrison, the raid army back home (raid over), an empty army -> the army is the hex's fist and "
                + "takes the garrison's surplus", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData shell = s.Shell();
                s.EndTurn();
                s.ExpectStrengthened(raid, "the fist takes the garrison's surplus");
                s.ExpectEmpty(shell);
            });

            Case("B02_RaidReturnLeg_GivesNothingAway",
                "the raid army passes through on its return leg to another base, a lone body beside -> the raid keeps its members", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat), mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(hero, mid);
                s.Raid(raid, RaidMissionPhase.Return);
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
                s.ExpectIn(hero, raid);
                s.ExpectIn(mid, raid);
            });

            Case("B03_RaidOver_SecondaryHeroReleased",
                "the raid is over, its army has two combat heroes -> one leaves", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.Combat), s.Hero(HeroPick.Combat2), s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectHeroes(army, 1);
            });

            Case("B04_RaidReturnLeg_KeepsSecondaryHero",
                "the same army passing through on its return leg -> it keeps both heroes", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.Combat), s.Hero(HeroPick.Combat2), s.Body(BodyPick.Mid));
                s.Raid(army, RaidMissionPhase.Return);
                s.EndTurn();
                s.ExpectUnchanged(army);
            });

            Case("B05_AttackRecoveryReturn_Arrived",
                "an Attack army passing through on its recovery return -> it gives nothing away", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat), mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData army = s.Field(hero, mid, s.Body(BodyPick.Mid));
                s.Attack(army, AttackMissionPhase.RecoveryReturn);
                s.EndTurn();
                s.ExpectIn(hero, army);
                s.ExpectIn(mid, army);
            });

            Case("B06_AttackEnds_ArmyBecomesFree",
                "T1 recovery return keeps both heroes; T2 the Attack is over -> the second hero leaves", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.Combat), s.Hero(HeroPick.Combat2), s.Body(BodyPick.Mid));
                MissionIntent attack = s.Attack(army, AttackMissionPhase.RecoveryReturn);
                s.EndTurn("recovery return");
                s.ExpectUnchanged(army);
                s.EndTask(attack);
                s.EndTurn("attack over");
                s.ExpectHeroes(army, 1);
            });

            Case("B07_DefenceReturn_Arrived",
                "an ActiveDefence army passing through on its way home -> it gives nothing away", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat), mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData army = s.Field(hero, mid);
                s.Defence(army, ActiveDefencePhase.Return);
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
                s.ExpectIn(hero, army);
                s.ExpectIn(mid, army);
            });

            Case("B08_ReturningBuilder_Untouched",
                "a builder walking home with its escort -> untouched, a lone body does not join it", s =>
            {
                UnitData loose = s.Body(BodyPick.Weak);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData builder = s.Field(s.Hero(HeroPick.SmallCommand), s.Body(BodyPick.Mid));
                s.Economy(builder, EconomyTaskKind.ReturnBuilder);
                s.Field(loose);
                s.EndTurn();
                s.ExpectUnchanged(builder);
                s.ExpectNotIn(loose, builder);
            });

            Case("B09_BuilderDone_ReleasedNextTurn",
                "T1 builder claimed; T2 its task is over -> an ordinary army again (standing rules)", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData builder = s.Field(s.Hero(HeroPick.SmallCommand), s.Body(BodyPick.Weak));
                MissionIntent task = s.Economy(builder, EconomyTaskKind.ReturnBuilder);
                s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EndTurn("builder claimed");
                s.ExpectUnchanged(builder);
                s.EndTask(task);
                s.EndTurn("builder free");
            });

            Case("B10_CollectorAtWork_Untouched",
                "a mobile collector claimed by Economy -> untouched", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData collector = s.Field(s.Body(BodyPick.Collector));
                s.Economy(collector, EconomyTaskKind.MobileCollection);
                s.EndTurn();
                s.ExpectUnchanged(collector);
            });

            Case("B11_ScoutWorkEnds_FoldedNextTurn",
                "T1 a scout with work stays alone; T2 its work is over -> folded", s =>
            {
                UnitData recce = s.Body(BodyPick.Recce);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData scout = s.Field(recce);
                s.ScoutWork(scout);
                s.EndTurn("scout at work");
                s.ExpectUnchanged(scout);
                ReconPatrolStateRegistry.ClearAll();
                s.EndTurn("work over");
                s.ExpectNotAlone(recce);
            });

            Case("B12_DevelopmentHero_ClaimedEvenUnderThreat",
                "a lone hero claimed by Development, a near enemy -> it does not shelter", s =>
            {
                UnitData hero = s.Hero(HeroPick.SmallCommand);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData dev = s.Field(hero);
                s.Development(dev, hero);
                s.EnemyAtEta(1, EnemyForce.Raiders);
                s.EndTurn();
                s.ExpectUnchanged(dev);
            });

            Case("B13_LeaseExpires_FoldedNextTurn",
                "T1 a leased fresh body stays; T2 the lease is gone -> folded", s =>
            {
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData fresh = s.Field(mid);
                s.Lease(fresh);
                s.EndTurn("leased");
                s.ExpectUnchanged(fresh);
                s.EndTurn("lease gone");
                s.ExpectNotAlone(mid);
            });

            Case("B14_RaidReturned_WithWoundedBodies",
                "the raid army comes home wounded (raid over) -> standing rules only", s =>
            {
                UnitData a = s.Body(BodyPick.Mid), b = s.Body(BodyPick.Mid);
                HousekeepingStand.Wound(a);
                HousekeepingStand.Wound(b);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Hero(HeroPick.Combat), a, b);
                s.Shell();
                s.EndTurn();
            });

            // 2026-11-25 (owner, stand B02: "why does the strong unit stay alone?") - a return leg
            // standing on its destination is home: Housekeeping packages it with the hex. The
            // garrison's last body still stays (one body always stays, 2026-09-30).
            Case("B15_RaidReturnArrived_JoinsTheHex",
                "the raid army has arrived home (Return leg on its destination), a lone body beside -> one formation", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat), weak = s.Body(BodyPick.Weak);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(hero, s.Body(BodyPick.Mid));
                s.Raid(raid, RaidMissionPhase.Return, home: true);
                s.Field(weak);
                s.EndTurn();
                if (s.Last.Before[raid.Id].Contract != null)
                    s.Failures.Add($"T1: expected the arrived raid army free, got [{s.Last.Before[raid.Id].Contract.Label}]");
                s.ExpectNotAlone(weak);
            });

            Case("B16_AttackRecoveryArrived_SecondHeroReleased",
                "an Attack army has arrived at its recovery base with two heroes -> it is free, one hero leaves", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.Combat), s.Hero(HeroPick.Combat2), s.Body(BodyPick.Mid),
                    s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Attack(army, AttackMissionPhase.RecoveryReturn, home: true);
                s.EndTurn();
                s.ExpectHeroes(army, 1);
            });

            Case("B17_DefenceReturnArrived_JoinsTheHex",
                "an ActiveDefence army has arrived home, a lone body beside -> it is free, the body is not left alone", s =>
            {
                UnitData weak = s.Body(BodyPick.Weak);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Mid));
                s.Defence(army, ActiveDefencePhase.Return, home: true);
                s.Field(weak);
                s.EndTurn();
                if (s.Last.Before[army.Id].Contract != null)
                    s.Failures.Add($"T1: expected the arrived army free, got [{s.Last.Before[army.Id].Contract.Label}]");
                s.ExpectNotAlone(weak);
            });

            Case("B18_BuilderArrivedHome_Free",
                "a builder has arrived home (ReturnBuilder on its destination) -> it is free for the hex", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData builder = s.Field(s.Hero(HeroPick.SmallCommand), s.Body(BodyPick.Mid));
                s.Economy(builder, EconomyTaskKind.ReturnBuilder, home: true);
                s.EndTurn();
                if (s.Last.Before[builder.Id].Contract != null)
                    s.Failures.Add($"T1: expected the arrived builder free, got [{s.Last.Before[builder.Id].Contract.Label}]");
            });

            Case("B19_SupportReturnArrived_PrimaryStillClaimed",
                "a raid support has walked home while its primary stands on the hex -> the support is free, the primary is not", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData primary = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong));
                ArmyData support = s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Raid(primary, RaidMissionPhase.SupportReturn, support, home: true);
                s.EndTurn();
                if (s.Last.Before[support.Id].Contract != null)
                    s.Failures.Add($"T1: expected the arrived support free, got [{s.Last.Before[support.Id].Contract.Label}]");
                if (s.Last.Before[primary.Id].Contract == null)
                    s.Failures.Add("T1: expected the raid primary still claimed");
            });
        }

        // ---- C. Armies with a live task on the hex -------------------------------------------
        private static void ActiveTasks()
        {
            // T05 - a claimed operation protects its function: it never donates, it may receive.
            Case("C01_MissionArmy_NeverDonates_EvenToTheGarrisonFloor",
                "a garrison with no body, a raid army beside -> the raid gives nothing", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support));
                ArmyData raid = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Raid(raid, RaidMissionPhase.Assault);
                s.EndTurn();
                s.ExpectUnchanged(raid);
            });

            Case("C02_RaidPrimary_MayTakeAFreeBody",
                "a raid army with room and a lone fast body -> the raid keeps its own (standing rules)", s =>
            {
                UnitData hero = s.Hero(HeroPick.BigCommand), mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(hero, mid);
                s.Raid(raid, RaidMissionPhase.Assault);
                s.Field(s.Body(BodyPick.Fast));
                s.EndTurn();
                s.ExpectIn(hero, raid);
                s.ExpectIn(mid, raid);
            });

            // T05 - a route-bound operation is never slowed by an inbound member.
            Case("C03_RaidPrimary_RejectsASlowerBody",
                "a raid army of fast bodies and a lone slow body -> the slow body does not join", s =>
            {
                UnitData slow = s.Body(BodyPick.Slow);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Fast));
                s.Raid(raid, RaidMissionPhase.Assault);
                s.Field(slow);
                s.EndTurn();
                s.ExpectNotIn(slow, raid);
            });

            Case("C04_AttackGather_MayTakeAFreeBody",
                "an Attack army gathering (not a preparation) with room and a lone body -> it keeps its own", s =>
            {
                UnitData hero = s.Hero(HeroPick.BigCommand), mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData army = s.Field(hero, mid);
                s.Attack(army, AttackMissionPhase.Gather);
                s.Field(s.Body(BodyPick.Fast));
                s.EndTurn();
                s.ExpectIn(hero, army);
                s.ExpectIn(mid, army);
            });

            Case("C05_AssaultStarted_ReceivesNothing",
                "an Attack army whose assault has started and a lone body -> nothing joins", s =>
            {
                UnitData loose = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Mid));
                s.Attack(army, AttackMissionPhase.Assault, assaultStarted: true);
                s.Field(loose);
                s.EndTurn();
                s.ExpectUnchanged(army);
            });

            Case("C06_AttackGatherSupport_Untouched",
                "an Attack primary and the support army it still expects -> the support is untouched", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData primary = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong));
                ArmyData support = s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Attack(primary, AttackMissionPhase.Gather, gatherSupports: new[] { support });
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
                s.ExpectUnchanged(support);
            });

            Case("C07_RaidReinforcementConvoy_Untouched",
                "a raid primary and its reinforcement convoy -> the convoy is untouched", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData primary = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong));
                ArmyData convoy = s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Raid(primary, RaidMissionPhase.Reinforcement, convoy);
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
                s.ExpectUnchanged(convoy);
            });

            Case("C08_DefenceIntercept_NeverDonates",
                "a garrison with no body, an intercepting army beside -> it gives nothing", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support));
                UnitData mid = s.Body(BodyPick.Mid);
                ArmyData army = s.Field(s.Hero(HeroPick.Combat), mid, s.Body(BodyPick.Mid));
                s.Defence(army, ActiveDefencePhase.Intercept, s.EnemyAtEta(3, EnemyForce.Raiders));
                s.EndTurn();
                s.ExpectIn(mid, army);
            });

            Case("C09_GarrisonSortie_FullyProtected",
                "a sortie detachment out of the garrison and a lone body -> the detachment is untouched", s =>
            {
                ArmyData garrison = s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData detachment = s.Field(s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                s.Sortie(detachment, garrison, s.EnemyAtEta(3, EnemyForce.Raiders));
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
                s.ExpectUnchanged(detachment);
            });

            Case("C10_BuilderOnItsWay_Untouched",
                "a builder with a hero claimed by Economy and a lone body -> untouched", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData builder = s.Field(s.Hero(HeroPick.SmallCommand), s.Body(BodyPick.Weak));
                s.Economy(builder, EconomyTaskKind.BuildExtraction);
                s.Field(s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectUnchanged(builder);
            });

            Case("C11_LeasedArmy_ReceivesButNeverGives",
                "a garrison with no body, a leased fresh army and a lone body -> the leased army keeps its own", s =>
            {
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Hero(HeroPick.Support));
                ArmyData leased = s.Field(mid, s.Body(BodyPick.Mid));
                s.Lease(leased);
                s.EndTurn();
                s.ExpectIn(mid, leased);
            });

            Case("C12_TwoClaims_KeepOnlyWhatBothAllow",
                "an army that is a raid primary and an Attack support at once -> nothing joins or leaves", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData primary = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong));
                ArmyData both = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Mid));
                s.Raid(both, RaidMissionPhase.Assault);
                s.Attack(primary, AttackMissionPhase.Gather, gatherSupports: new[] { both });
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
                s.ExpectUnchanged(both);
            });

            Case("C13_MissionReceiver_NeverGivesAHero",
                "a raid army with two heroes and a heroless pair beside -> the raid keeps both heroes", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(s.Hero(HeroPick.Combat), s.Hero(HeroPick.Combat2), s.Body(BodyPick.Mid));
                s.Raid(raid, RaidMissionPhase.Assault);
                s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectHeroes(raid, 2);
            });

            Case("C14_ClaimedSingleton_NotFolded",
                "a one-body raid army beside a garrison with room -> not folded", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(s.Body(BodyPick.Mid));
                s.Raid(raid, RaidMissionPhase.Assault);
                s.EndTurn();
                s.ExpectUnchanged(raid);
            });

            Case("C15_MovingOperation_PromotesTheBetterCommander",
                "a raid army led by the smaller-command hero -> the bigger one commands, nobody moves", s =>
            {
                UnitData small = s.Hero(HeroPick.SmallCommand), big = s.Hero(HeroPick.BigCommand);
                if (big.CommandRating <= small.CommandRating)
                    Assert.Ignore("no command difference between the deck's heroes");
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(small, big, mid);
                s.Raid(raid, RaidMissionPhase.Assault);
                s.EndTurn();
                s.ExpectUnchanged(raid);
                s.ExpectCommands(big, mid);
            });

            // 2026-11-25 (owner: "the garrison secures the hex adequately and gives everything else
            // to the tasks") — a raid about to leave takes the garrison's strongest surplus.
            Case("C18_GarrisonSurplus_JoinsTheRaid",
                "a raid army in its assault on the hex, a garrison above its floor -> the strongest spare body joins "
                + "the raid, the garrison keeps its floor", s =>
            {
                UnitData strong = s.Body(BodyPick.Strong);
                ArmyData garrison = s.Garrison(strong, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData raid = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Mid));
                s.Raid(raid, RaidMissionPhase.Assault);
                int raidMove = raid.CurrentMovement;
                s.EndTurn();
                // A spare body exists when the garrison keeps its floor (and a body) without the
                // strongest one; the raid's route speed (KeepsMovement) may refuse a slow one.
                StandArmy g = s.Last.Before[garrison.Id];
                List<UnitData> rest = g.Members.Where(u => u != strong && AiArmyRoles.IsGroundBattleBody(u)).ToList();
                bool spare = rest.Count > 0 && AiPower.EffectiveArmyPower(rest) >= s.Last.GarrisonFloor;
                if (spare && strong.MoveMax >= raidMove)
                    s.ExpectIn(strong, raid, "the strongest spare body goes first");
                else
                    s.Notes.Add($"T1: no spare fast-enough strong body (floor {s.Last.GarrisonFloor:0.#}, raid mp {raidMove})");
            });

            Case("C16_FullyProtected_KeepsItsCommander",
                "an Attack army in its assault led by the smaller-command hero -> no reorder", s =>
            {
                UnitData small = s.Hero(HeroPick.SmallCommand), big = s.Hero(HeroPick.BigCommand);
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData army = s.Field(small, big, mid);
                s.Attack(army, AttackMissionPhase.Assault, assaultStarted: true);
                s.EndTurn();
                s.ExpectCommands(small, mid);
            });

            Case("C17_TaskStarts_ArmyStopsBeingADonor",
                "T1 a free army with two heroes releases one; T2 a raid claims it -> it keeps what it has", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.Combat), s.Hero(HeroPick.Combat2), s.Body(BodyPick.Mid),
                    s.Body(BodyPick.Mid));
                s.EndTurn("free");
                s.ExpectHeroes(army, 1);
                s.Raid(army, RaidMissionPhase.Assault);
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn("raid claims it");
            });
        }

        // ---- D. The Attack preparation host gathering on the hex -----------------------------
        private static void PreparationHost()
        {
            Case("D01_HeroOnlyHost_TakesTheFreeBody",
                "a hero-only preparation host wanting middle bodies and a lone one -> it joins the host", s =>
            {
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand));
                s.Preparation(host, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(mid);
                s.EndTurn();
                s.ExpectIn(mid, host);
            });

            // ATK-F03 (Vashti T13) - a hero enters a claimed receiver only as its better commander.
            Case("D02_Host_DoesNotTakeAHeroForAFighterSlot",
                "a led preparation host and a lone combat hero -> the hero does not join", s =>
            {
                UnitData lone = s.Hero(HeroPick.Combat2);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData host = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Mid));
                s.Preparation(host, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(lone);
                s.EndTurn();
                s.ExpectNotIn(lone, host);
            });

            Case("D03_HerolessHost_TakesACapacityHero",
                "a full heroless preparation host and a lone big-command hero -> the hero leads it", s =>
            {
                UnitData big = s.Hero(HeroPick.BigCommand);
                if (big.CommandRating <= 2)
                    Assert.Ignore("no hero raises the field capacity");
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData host = s.Field(mid, s.Body(BodyPick.Mid));
                s.Preparation(host, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(big);
                s.EndTurn();
                s.ExpectCommands(big, mid);
            });

            // ATK-F03 (T17-T20) - the damaged host sheds the heroes that are neither commander nor best leader.
            Case("D04_DamagedHost_ReleasesExcessHeroes",
                "a preparation host with three heroes and one body -> it keeps one hero", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand), s.Hero(HeroPick.Combat),
                    s.Hero(HeroPick.SmallCommand), s.Body(BodyPick.Weak));
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.EndTurn();
                s.ExpectHeroes(host, 1);
            });

            Case("D05_Host_KeepsItsArmySupportHero",
                "a preparation host with a Heal/Drill/Transport second hero -> that hero stays", s =>
            {
                UnitData support = s.Hero(HeroPick.ArmySupport);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData host = s.Field(s.Hero(HeroPick.Combat), support, s.Body(BodyPick.Mid));
                s.Preparation(host, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectIn(support, host);
            });

            // 2026-10-01 (owner) - a full host swaps a body its roster does not need for a missing position.
            Case("D06_Host_TakesItsMissingPositions_FromGarrisonSpares",
                "a host with room and a weak body it does not want, strong spares in the garrison -> the strong ones join", s =>
            {
                UnitData weak = s.Body(BodyPick.Weak);
                UnitData strong = s.Body(BodyPick.Strong);
                s.Garrison(strong, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand), weak);
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.EndTurn();
                int wanted = host.Members.Count(u => u.OriginatingCard == strong.OriginatingCard);
                if (wanted < 2)
                    s.Failures.Add($"T1: expected both missing positions filled from the garrison spares, got {wanted}");
            });

            // 2026-11-25 (owner) — the defence of the hex is the night fold's: by day the host keeps
            // its task, at night the hex fights as one formation, in the morning the host is back.
            Case("D07_Host_UnderSiege_KeepsItsTask_NightJoinsTheHex",
                "a host with a weak non-roster body, a one-body garrison, a company 2 turns away -> the host keeps "
                + "its task, the night joins the hex", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Weak));
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.EnemyAtEta(2, EnemyForce.Company);
                s.EndTurn();
                if (s.Last.Before[host.Id].Contract == null)
                    s.Failures.Add("T1: expected the host to keep its task by day");
                s.ExpectNightFormations(1, "one hex, one fight");
            });

            Case("D08_Host_KeepsTheBody_WithoutASource",
                "the same host with no wanted body anywhere on the hex -> it keeps the weak one", s =>
            {
                UnitData weak = s.Body(BodyPick.Weak);
                s.Garrison(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand), weak);
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.EndTurn();
                s.ExpectIn(weak, host);
            });

            // 2026-11-25 (owner) - "all armies are merged as usual; even without a chance the game
            // must stay interesting for the human": the night fold joins the hex into one fight.
            Case("D09_Host_UnderSiege_HexDefendedAsOne",
                "a host of strong bodies, a Support-led garrison with room, a strong enemy next door -> one formation "
                + "at night", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData host = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn();
                if (s.Last.Before[host.Id].Contract == null)
                    s.Failures.Add("T1: expected the host to keep its task by day");
                s.ExpectNightFormations(1, "one hex, one fight");
            });

            Case("D10_Host_TakesTheWalkInPair",
                "a host wanting middle bodies and a heroless walk-in pair -> the host takes them", s =>
            {
                UnitData mid = s.Body(BodyPick.Mid);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Strong));
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(mid, s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectIn(mid, host);
            });

            Case("D11_HostShell_StandingRules",
                "an empty army claimed as the preparation host beside a heroless pair -> standing rules", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData host = s.Shell();
                s.Preparation(host, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EndTurn();
            });

            Case("D12_Host_DoesNotStripTheGarrisonFloor",
                "a host wanting strong bodies, a garrison of exactly two strong -> the garrison keeps a body", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand));
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.EndTurn();
            });
        }

        // ---- E. Known enemies --------------------------------------------------------------------
        private static void Threats()
        {
            // 2026-11-20 (owner, Halden vs Orlan T24) - packing the fist raised only the second line, never the hold.
            Case("E01_FarThreat_HexHeld_FistNotEmptied",
                "a Support-led garrison and a strong free fist, weak raiders 5 turns away -> the fist keeps its bodies", s =>
            {
                UnitData[] fist = s.Bodies(BodyPick.Strong, 3);
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData army = s.Field(new[] { s.Hero(HeroPick.Combat) }.Concat(fist).ToArray());
                s.EnemyAtEta(5, EnemyForce.Raiders);
                s.EndTurn();
                foreach (UnitData u in fist)
                    s.ExpectIn(u, army, "the hex holds anyway");
            });

            Case("E02_NearStrongThreat_SupportHeroesOffTheFightingArmies",
                "garrison two Support heroes + 3 bodies, a field army, an empty army, a strong enemy next door", s =>
            {
                UnitData sup = s.Hero(HeroPick.Support);
                s.Garrison(sup, s.Hero(HeroPick.Support2), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Shell();
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn();
                s.ExpectNoSupportHeroInFightingField();
            });

            Case("E03_NearThreat_HeroArmyTakesTheHerolessPairs",
                "a full garrison, a hero army with room and two heroless pairs, a company next door -> folded when that raises the hold", s =>
            {
                UnitData big = s.Hero(HeroPick.BigCommand);
                if (big.CommandRating < 7)
                    Assert.Ignore("no hero with room for both pairs");
                UnitData w = s.Body(BodyPick.Weak);
                s.Garrison(s.Bodies(BodyPick.Mid, 4));
                s.Field(big, s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(w, s.Body(BodyPick.Weak));
                s.Field(s.Body(BodyPick.Weak), s.Body(BodyPick.Weak));
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
                if (s.Last.HoldAfter.Values.Any(h => h > s.Last.HoldBefore.Values.Min() + 0.12f))
                    s.ExpectCommands(big, w, "one formation holds the hex better than three");
            });

            // 2026-10-15 (owner) - under a near threat a lone hero shelters in the local garrison.
            // 2026-11-26 (owner) — the hardest army of the hex fights first: beside a garrison with
            // fighters a lone hero is not the one the enemy meets, so it keeps its place.
            Case("E04_LoneHero_BesideFighters_StaysPut",
                "garrison strong + weak, a lone combat hero, a company next door -> the garrison takes the blow, "
                + "the hero stays where it is", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Weak));
                ArmyData alone = s.Field(hero);
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
                s.ExpectIn(hero, alone, "the garrison fights first");
            });

            Case("E04b_LoneHero_NoFighterOnTheHex_Shelters",
                "a garrison of a Support hero only, a lone combat hero, a company next door -> the hero shelters", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat);
                s.Garrison(s.Hero(HeroPick.Support));
                s.Field(hero);
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
                s.ExpectInGarrison(hero, "nobody else can take the blow");
            });

            // A lone hero beyond the shelter horizon joins the garrison only as its commander when
            // that does not lower the hold (it may raise it: capacity, initiative) - standing rules.
            Case("E05_LoneHero_BeyondTheHorizon_StandingRules",
                "the same with the company 3 turns away -> standing rules", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Weak));
                s.Field(s.Hero(HeroPick.Combat));
                s.EnemyAtEta(3, EnemyForce.Company);
                s.EndTurn();
            });

            Case("E06_LoneHero_ShelterAtTheHorizon",
                "a Support-only garrison, the company 2 turns away -> the hero shelters", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat);
                s.Garrison(s.Hero(HeroPick.Support));
                s.Field(hero);
                s.EnemyAtEta(2, EnemyForce.Company);
                s.EndTurn();
                s.ExpectInGarrison(hero);
            });

            Case("E07_LoneHero_FullHerolessGarrison_StandingRules",
                "a full heroless garrison, a lone hero, a company next door -> standing rules", s =>
            {
                s.Garrison(s.Bodies(BodyPick.Strong, 4));
                s.Field(s.Hero(HeroPick.Combat));
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
            });

            Case("E08_NearWeakThreat_NothingPacked",
                "a Support-led garrison, a strong field army, two weak raiders next door -> the field army keeps its bodies", s =>
            {
                UnitData a = s.Body(BodyPick.Strong), b = s.Body(BodyPick.Strong);
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData field = s.Field(s.Hero(HeroPick.Combat), a, b);
                s.EnemyAtEta(1, EnemyForce.Raiders);
                s.EndTurn();
                s.ExpectIn(a, field, "the hex holds anyway");
                s.ExpectIn(b, field, "the hex holds anyway");
            });

            Case("E09_DisruptionCarrier_StandingRules",
                "a strong enemy with a Disruption carrier next door -> the hex hold (without the building bonus) does not drop", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Weak), s.Body(BodyPick.Weak));
                ArmyData enemy = s.EnemyAtEta(1, EnemyForce.Host);
                s.GiveDisruption(enemy);
                s.EndTurn();
            });

            Case("E10_Base_LoneHero_BesideFighters_StaysPut",
                "on a secondary Base: a lone hero beside a garrison with fighters, a company next door -> it stays", s =>
            {
                UnitData hero = s.Hero(HeroPick.Combat);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Weak));
                ArmyData alone = s.Field(hero);
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
                s.ExpectIn(hero, alone, "the garrison fights first");
            }, StandSite.Base);

            Case("E11_Base_FarThreat_FistNotEmptied",
                "on a secondary Base: a small garrison, a strong free army, raiders 5 turns away -> the army keeps its bodies", s =>
            {
                UnitData[] fist = s.Bodies(BodyPick.Strong, 2);
                s.Garrison(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData army = s.Field(new[] { s.Hero(HeroPick.Combat) }.Concat(fist).ToArray());
                s.EnemyAtEta(5, EnemyForce.Raiders);
                s.EndTurn();
                foreach (UnitData u in fist)
                    s.ExpectIn(u, army);
            }, StandSite.Base);

            Case("E12_TwoThreats_StandingRules",
                "raiders next door and a company two turns away -> the hold against both does not drop", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Weak));
                s.EnemyAtEta(1, EnemyForce.Raiders);
                s.EnemyAtEta(2, EnemyForce.Company);
                s.EndTurn();
            });

            // 2026-11-25 (owner) — "the human must get the most interesting fight: the garrison and
            // the army are joined, in the garrison or in the army, it does not matter".
            Case("E13_Threat_RaidArmyJoinsTheGarrisonAtNight",
                "a weak garrison, a raid army, a strong enemy next door -> one fight at night, the raid leaves "
                + "with its own army in the morning", s =>
            {
                UnitData weak = s.Body(BodyPick.Weak);
                s.Garrison(s.Hero(HeroPick.Support), weak);
                UnitData strong = s.Body(BodyPick.Strong);
                ArmyData raid = s.Field(s.Hero(HeroPick.Combat), strong, s.Body(BodyPick.Strong));
                s.Raid(raid, RaidMissionPhase.Assault);
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn();
                s.ExpectNightTogether(weak, strong, "one fight");
                s.ExpectInGarrison(weak, "the morning restores the garrison");
            });

            Case("E14_Threat_AirWingUntouched",
                "an aircraft wing on the hex, a strong enemy next door -> the wing is untouched", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData air = s.AirWing(s.Body(BodyPick.Air));
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn();
                s.ExpectUnchanged(air);
            });

            Case("E15_HopelessThreat_StillOneFight",
                "a garrison and a led field army against a full enemy host next door -> merged into one fight", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Weak));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn();
                s.ExpectNightFormations(1, "a lost hex is still defended as one hard fight");
            });

            Case("E16_Threat_IdleScoutShelters",
                "a lone scout with no work, a company next door -> it is not left alone", s =>
            {
                UnitData recce = s.Body(BodyPick.Recce);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(recce);
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
                s.ExpectNotAlone(recce);
            });

            Case("E17_Threat_ScoutWithWorkUntouched",
                "a lone scout with work, a company next door -> untouched", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData scout = s.Field(s.Body(BodyPick.Recce));
                s.ScoutWork(scout);
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
                s.ExpectUnchanged(scout);
            });

            Case("E18_ThreatLeaves_NoUnpacking",
                "T1 a company next door; T2 it is gone -> nothing packed for it is unpacked", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Weak), s.Body(BodyPick.Weak));
                s.Shell();
                ArmyData enemy = s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn("enemy next door");
                s.RemoveEnemy(enemy);
                s.EndTurn("enemy gone");
                foreach (StandArmy b in s.Turns[0].After.Values)
                    foreach (UnitData u in b.Members)
                    {
                        StandArmy t0 = s.Turns[0].Before.Values.First(a => a.Members.Contains(u));
                        if (t0.Id != b.Id && s.ArmyOf(u)?.Id == t0.Id)
                            s.Failures.Add($"T2: {s.Label(u)} packed for the threat went back to #{t0.Id}");
                    }
            });

            Case("E19_ThreatArrives_AfterAQuietTurn",
                "T1 quiet; T2 a strong enemy appears next door -> the hold does not drop", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Weak), s.Body(BodyPick.Weak));
                s.Shell();
                s.EndTurn("quiet");
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn("enemy next door");
            });

            Case("E20_Threat_RaidReturnLegOnTheHex",
                "a raid army on its return leg, a heroless pair, a strong enemy next door -> standing rules", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid));
                ArmyData raid = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Strong));
                s.Raid(raid, RaidMissionPhase.Return);
                s.Field(s.Body(BodyPick.Weak), s.Body(BodyPick.Weak));
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn();
            });

            Case("E21_Threat_PreparationHostAndFreeArmy",
                "a preparation host, a free field army and a Support-led garrison, a company next door -> standing rules", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Strong));
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn();
            });

            Case("E22_FarThreat_WalkInPair_StandingRules",
                "a heroless walk-in pair and a fist beside the garrison, raiders 5 turns away -> standing rules", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Weak), s.Body(BodyPick.Weak));
                s.EnemyAtEta(5, EnemyForce.Raiders);
                s.EndTurn();
            });
        }

        // ---- F. Unit and army constraints ----------------------------------------------------
        private static void Constraints()
        {
            Case("F01_ActivatedArmy_NoApSpent",
                "a field army already activated this turn and a lone body -> Housekeeping spends no AP", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData army = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Mid));
                army.MarkActivated();
                s.Field(s.Body(BodyPick.Weak));
                s.EndTurn();
            });

            Case("F02_SpentBody_NeverSlowsAMovingOperation",
                "a raid army with full movement and a lone body that has spent its move -> it does not join the raid", s =>
            {
                UnitData spent = s.Body(BodyPick.Mid);
                HousekeepingStand.SpendMovement(spent);
                s.Garrison(s.Body(BodyPick.Strong));
                ArmyData raid = s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Mid));
                s.Raid(raid, RaidMissionPhase.Assault);
                s.Field(spent);
                s.EndTurn();
                s.ExpectNotIn(spent, raid);
            });

            Case("F03_SpentBody_StillGoesHome",
                "a lone body that has spent its move and a garrison with room -> it joins the garrison", s =>
            {
                UnitData spent = s.Body(BodyPick.Weak);
                HousekeepingStand.SpendMovement(spent);
                s.Garrison(s.Body(BodyPick.Strong));
                s.Field(spent);
                s.EndTurn();
                s.ExpectInGarrison(spent);
            });

            Case("F04_CommanderKeepsTheCapacity",
                "a field army filled to its big commander's capacity with a small hero behind -> the big one keeps command", s =>
            {
                UnitData big = s.Hero(HeroPick.BigCommand), small = s.Hero(HeroPick.SmallCommand);
                if (big.CommandRating <= small.CommandRating + 1 || big.CommandRating < 4)
                    Assert.Ignore("no command gap to fill");
                UnitData[] bodies = s.Bodies(BodyPick.Mid, big.CommandRating - 2);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                s.Field(new[] { big, small }.Concat(bodies).ToArray());
                s.EndTurn();
                s.ExpectCommands(big, bodies[0]);
            });

            Case("F05_Prison_Untouched",
                "a prison on the hex beside a loose layout -> untouched", s =>
            {
                s.Garrison(s.Body(BodyPick.Strong));
                s.Field(s.Body(BodyPick.Weak));
                ArmyData prison = s.Prison();
                s.EndTurn();
                s.ExpectUnchanged(prison);
            });

            // 2026-11-25 (owner) — "the attack army takes the healthy and the stronger units the garrison
            // holds above its floor; for the night the host and the garrison are one".
            Case("F06_WoundedHost_TakesTheGarrisonSurplus",
                "a preparation host of wounded bodies, healthy spares in the garrison -> the host takes the "
                + "surplus by day, one formation at night", s =>
            {
                UnitData a = s.Body(BodyPick.Strong), b = s.Body(BodyPick.Mid);
                HousekeepingStand.Wound(a);
                HousekeepingStand.Wound(b);
                s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                ArmyData host = s.Field(s.Hero(HeroPick.BigCommand), a, b);
                s.Preparation(host, s.Body(BodyPick.Strong), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectStrengthened(host, "the garrison's surplus joins the task");
                s.ExpectNightFormations(1, "one hex, one fight");
            });

            Case("F07_FullEverywhere_StandingRules",
                "a full heroless garrison and a full led field army -> standing rules", s =>
            {
                UnitData hero = s.Hero(HeroPick.SmallCommand);
                s.Garrison(s.Bodies(BodyPick.Mid, 4));
                s.Field(new[] { hero }.Concat(s.Bodies(BodyPick.Strong, Math.Max(1, hero.CommandRating - 1))).ToArray());
                s.EndTurn();
            });
        }

        // ---- S. The owner's scenarios -------------------------------------------------------------
        private static void OwnerScenarios()
        {
            Case("S01_GarrisonArmyEmptyArmy_RaidBack",
                "garrison, an army, an empty army; a raid army comes back and the turn ends -> no new army, nothing lost", s =>
            {
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Strong), s.Body(BodyPick.Mid));
                s.Field(s.Hero(HeroPick.BigCommand), s.Body(BodyPick.Strong), s.Body(BodyPick.Strong));
                ArmyData shell = s.Shell();
                ArmyData raid = s.Field(s.Hero(HeroPick.Combat), s.Body(BodyPick.Mid), s.Body(BodyPick.Weak));
                MissionIntent task = s.Raid(raid, RaidMissionPhase.Return);
                s.EndTurn("raid on its way back");
                s.EndTask(task);
                s.EndTurn("raid over");
                s.ExpectEmpty(shell);
            });

            Case("S02_SupportHeroesApart_CombatHeroLeads",
                "garrison and one army holding two Support heroes, a combat hero and the bodies -> Support heroes "
                + "apart, the combat hero leads the bodies, no new army", s =>
            {
                UnitData sup = s.Hero(HeroPick.Support), sup2 = s.Hero(HeroPick.Support2);
                UnitData hero = s.Hero(HeroPick.Combat);
                UnitData strong = s.Body(BodyPick.Strong);
                s.Garrison(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(sup, sup2, hero, strong, s.Body(BodyPick.Strong));
                ArmyData shell = s.Shell();
                s.EndTurn();
                s.ExpectCommands(hero, strong);
                s.ExpectNoSupportHeroInFightingField();
                s.ExpectEmpty(shell, "no new army when the garrison can take them");
            });

            Case("S03_SupportHeroesApart_BiggestCommandLeads",
                "the same with the biggest-command field hero -> it leads the bodies", s =>
            {
                UnitData sup = s.Hero(HeroPick.Support);
                UnitData big = s.Hero(HeroPick.BigCommand);
                UnitData strong = s.Body(BodyPick.Strong);
                s.Garrison(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                s.Field(sup, big, strong, s.Body(BodyPick.Mid));
                s.EndTurn();
                s.ExpectCommands(big, strong);
                s.ExpectNoSupportHeroInFightingField();
            });

            Case("S04_FiveTurns_Barracks",
                "T1 peace; T2 far raiders + walk-in pair; T3 the fist becomes the preparation host; T4 strong enemy next "
                + "door; T5 the enemy is gone", s =>
            {
                UnitData nadia = s.Hero(HeroPick.Combat);
                UnitData[] fistBodies = s.Bodies(BodyPick.Strong, 2);
                s.Garrison(s.Hero(HeroPick.Support), s.Body(BodyPick.Mid), s.Body(BodyPick.Mid), s.Body(BodyPick.Weak));
                ArmyData fist = s.Field(new[] { nadia }.Concat(fistBodies).ToArray());
                s.EndTurn("peace");
                s.ExpectCommands(nadia, fistBodies[0], "the fist is back in the morning");
                s.Field(s.Body(BodyPick.Mid), s.Body(BodyPick.Mid));
                ArmyData raiders = s.EnemyAtEta(5, EnemyForce.Raiders);
                float fistPower = s.Last.After[fist.Id].Power;
                s.EndTurn("walk-in, far raiders");
                if (s.Last.After[fist.Id].Power + 0.01f < fistPower)
                    s.Failures.Add($"T2: expected the fist not weakened by a far threat {fistPower:0.#} -> "
                        + $"{s.Last.After[fist.Id].Power:0.#}");
                s.Preparation(fist, s.Body(BodyPick.Strong), s.Body(BodyPick.Strong), s.Body(BodyPick.Mid),
                    s.Body(BodyPick.Mid));
                s.EndTurn("gate open");
                s.RemoveEnemy(raiders);
                s.EnemyAtEta(1, EnemyForce.Host);
                s.EndTurn("strong enemy next door");
                s.EndTurn("aftermath");
            });

            // 2026-11-25 (owner: "first sort for the defence, then release for the tasks next turn")
            // — three turns of a garrison, a raid about to leave and a free army: every night the hex
            // is one fight, every morning the raid has its own army back, nothing churns by day.
            Case("S05_ThreeNights_TasksLeaveWithTheirArmies",
                "garrison, a raid in its assault, a free led army; three turns, a company next door from T2 -> one "
                + "fight each night, the day layout each morning", s =>
            {
                UnitData raidLead = s.Hero(HeroPick.Combat);
                UnitData raidBody = s.Body(BodyPick.Mid);
                ArmyData garrison = s.Garrison(s.Body(BodyPick.Strong), s.Body(BodyPick.Mid), s.Body(BodyPick.Weak));
                ArmyData raid = s.Field(raidLead, raidBody, s.Body(BodyPick.Mid));
                s.Raid(raid, RaidMissionPhase.Assault);
                ArmyData free = s.Field(s.Hero(HeroPick.Combat2), s.Body(BodyPick.Mid));
                s.EndTurn("peace");
                s.ExpectCommands(raidLead, raidBody, "the raid leaves with its commander");
                s.EnemyAtEta(1, EnemyForce.Company);
                s.EndTurn("company next door");
                s.ExpectCommands(raidLead, raidBody, "the raid leaves with its commander");
                s.EndTurn("still there");
                s.ExpectNoOperations("the day layout holds once formed");
                s.ExpectCommands(raidLead, raidBody, "the raid leaves with its commander");
            });
        }
    }

    // The stand's test entry: every case on every deck pair.
    public sealed class HousekeepingStandTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (StandCase c in HousekeepingStandCases.All)
                foreach ((string own, string enemy) in HousekeepingStand.DeckPairs)
                    yield return new TestCaseData(c.Id, own, enemy).SetName($"{c.Id} [{own}]");
        }

        // Every hero card of the catalog through the game's own role rules (HeroRoleEvaluator):
        // what Housekeeping and the commander evaluation actually see. A report, not a gate.
        [Test, Explicit("report")]
        public void HeroRoleReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("faction | hero | CR | move | fate | init | power | leadership | Support tag | vocation | role");
            var owner = new Game.Players.PlayerSetupData();
            foreach (Game.Cards.FactionCardCatalog catalog in HousekeepingStand.Catalog.catalogs.Where(c => c != null))
                foreach (Game.Cards.CardDefinition c in catalog.ForType(Game.Cards.CardType.Hero)
                             .OrderBy(c => c.displayName, StringComparer.Ordinal))
                {
                    UnitData u = UnitData.FromDefinition(c, owner);
                    sb.AppendLine($"{catalog.faction} | {c.displayName} | {u.CommandRating} | {u.MoveMax} | {u.Fate} | "
                        + $"{u.Initiative} | {AiPower.ToPowerUnit(u).BasePower:0.#} | "
                        + $"{HeroRoleEvaluator.CombatLeadershipScore(u):0.##} | {(Game.Ai.AiArmyRoles.IsGarrisonHero(u) ? "yes" : "-")} | "
                        + $"{(HeroRoleEvaluator.HasSupportVocation(u) ? "yes" : "-")} | {HeroRoleEvaluator.Classify(u)}");
                }
            sb.AppendLine($"a vocation hero is SupportOperator below {AiConfigV2.heroRoleFlexibleCombatFloor}; "
                + $"leadership = (CR x {AiConfigV2.heroRoleCommandWeight} + power x {AiConfigV2.heroRoleCombatContributionWeight}) "
                + $"x min(1, move / {AiConfigV2.mobileCombatMoveMax})");
            TestContext.WriteLine(sb.ToString());
            TestContext.Progress.WriteLine(sb.ToString());
        }

        [TestCaseSource(nameof(Cases))]
        public void Case(string id, string ownDeck, string enemyDeck)
        {
            StandCase c = HousekeepingStandCases.Find(id);
            using (var stand = new HousekeepingStand(ownDeck, enemyDeck, c.Site) { CaseName = id, CaseText = c.Text })
            {
                c.Run(stand);
                string report = stand.Report();
                TestContext.WriteLine(report);
                TestContext.WriteLine($"housekeeping {stand.HousekeepingTime.ElapsedMilliseconds} ms");
                if (stand.Failures.Count > 0 && HousekeepingStandCases.Pending.TryGetValue(id, out string question))
                    Assert.Inconclusive($"pending owner decision - {question}\n{report}");
                if (stand.Failures.Count > 0)
                    Assert.Fail(report);
            }
        }
    }
}
#endif
