using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace StealthSim
{
    // Model-layer acceptance harness for parameterized Recce + individual Stealth.
    // Exercises the real game types (no MonoBehaviour/coroutine/scene code). Scenarios that
    // can only be driven end-to-end through a coroutine/UI (auto-stealth AI move, the exact
    // AP charge in the modal) are verified here at the decision-gate level instead — noted
    // inline and in README.md.
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        // Deterministic stand-in for the hidden challenge's dice (StealthSystem.ChallengeRoller
        // seam) so scenarios can pin exact spot/hide success counts without UnityEngine.Random.
        private static int _stubSpotSuccesses;
        private static int _stubHideSuccesses;
        private static int _challengeRolls;

        private static readonly Dictionary<PlayerSetupData, long> CompletedTurns = new Dictionary<PlayerSetupData, long>();
        private static readonly Dictionary<HexCoord, int> TerrainCost = new Dictionary<HexCoord, int>();

        private static int Main()
        {
            StealthSystem.ChallengeRoller = (spot, hide) =>
            {
                _challengeRolls++;
                return new ChallengeResult(Trues(_stubSpotSuccesses), Trues(_stubHideSuccesses));
            };
            StealthSystem.CompletedTurnsProvider = p => p != null && CompletedTurns.TryGetValue(p, out long n) ? n : 0L;
            StealthSystem.TerrainMoveCostProvider = h => TerrainCost.TryGetValue(h, out int c) ? c : 1;
            VisionSystem.Configure(null); // armyVisionRadius 0, no building — the design's base case

            Scenario01_NoLegacyRecce();
            Scenario02_R1s0RadiusButNoAdjacentDetect();
            Scenario03_SpotPools();
            Scenario04_OrdinarySourceOneDieOwnHexOnly();
            Scenario05_HideDiceByTerrain();
            Scenario06_TieKeepsStealth();
            Scenario07_ObserversTakeMaxNotSum();
            Scenario08_CoLocatedOrdinaryEnemyNoAutoReveal();
            Scenario09_ReadApisRunNoChallenge();
            Scenario10_OneChallengePerPairPerEvent();
            Scenario11_HiddenOnlyArmyIsInert();
            Scenario12_MixedArmyRoster();
            Scenario13_HiddenUnitCannotHoldOrTakeBuilding();
            Scenario14_AviationIgnoresHiddenUndetected();
            Scenario15_DetectionWindow();
            Scenario16_OwnerGetsNoSignal();
            Scenario17_DirectedActionLiftsStealth();
            Scenario18_AiMemoryStaysHonest();
            Scenario19_AiSoloScoutStealthGate();
            Scenario20_EnterCostsGatedExitFree();
            Scenario21_DetectedOnOwnTurnLastsThroughNextTurn();

            Console.WriteLine();
            Console.WriteLine($"stealth-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- scenarios ----

        private static void Scenario01_NoLegacyRecce()
        {
            bool noConst = typeof(UnitAbilities).GetField("Recce", BindingFlags.Public | BindingFlags.Static) == null;
            bool notInAll = !UnitAbilities.All.Contains("Recce");
            bool noCatalogRadius = typeof(UnitAbilityCatalog).GetField("recceRadius") == null
                                   && typeof(UnitAbilityCatalog).GetField("recceStrength") == null;
            bool noArmyFlag = typeof(ArmyData).GetProperty("HasRecce") == null
                              && typeof(ArmyData).GetField("HasRecce") == null;
            bool newTags = UnitAbilities.All.Contains("r1s0") && UnitAbilities.All.Contains("r1s4")
                           && UnitAbilities.All.Contains("r1s5") && UnitAbilities.All.Contains("r1s6")
                           && UnitAbilities.All.Contains("Stealth4");
            Check("01 old Recce fully gone; r1sX/Stealth4 present",
                noConst && notInAll && noCatalogRadius && noArmyFlag && newTags);
        }

        private static void Scenario02_R1s0RadiusButNoAdjacentDetect()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            var scout = Army("scout", At(0, 0), p1, Unit("s", "r1s0"));
            ArmyRegistry.Register(scout);
            VisionSystem.RecomputeFor(p1);

            bool seesOwnHex = VisionSystem.IsVisible(p1, At(0, 0));
            bool seesNeighbour = VisionSystem.IsVisible(p1, At(1, 0)); // r1s0 -> radius 0+1
            bool radiusIsOne = AbilityParams.GetBestRecceRadius(scout) == 1;

            // A Stealth4 unit one hex away: r1s0's spot strength is 0, so no detection next door.
            var hider = Unit("h", "Stealth4");
            var hArmy = Army("h", At(1, 0), p2, hider);
            ArmyRegistry.Register(hArmy);
            StealthSystem.EnterStealth(hider);
            int adjacentPool = StealthSystem.SpotPoolAgainst(p1, At(1, 0));

            Check("02 r1s0 -> vision radius 1, but 0 spot pool on an adjacent hex",
                seesOwnHex && seesNeighbour && radiusIsOne && adjacentPool == 0);
        }

        private static void Scenario03_SpotPools()
        {
            Reset();
            var p = Player("P");
            foreach ((string tag, int expect) in new[] { ("r1s4", 4), ("r1s5", 5), ("r1s6", 6) })
            {
                var a = Army("obs", At(0, 0), p, Unit("o", tag));
                ArmyRegistry.Register(a);
                int inHex = StealthSystem.SpotPoolAgainst(p, At(0, 0));       // max(1, S) == S
                int adjacent = StealthSystem.SpotPoolAgainst(p, At(1, 0));    // r1sX reaches 1 hex: S
                Check($"03 {tag} -> spot pool {expect} in-hex and adjacent", inHex == expect && adjacent == expect);
                ArmyRegistry.Unregister(a);
            }
        }

        private static void Scenario04_OrdinarySourceOneDieOwnHexOnly()
        {
            Reset();
            var p = Player("P");
            var a = Army("plain", At(0, 0), p, Unit("u")); // no Recce tag
            ArmyRegistry.Register(a);
            Check("04 ordinary source: 1 die own hex, 0 adjacent",
                StealthSystem.SpotPoolAgainst(p, At(0, 0)) == 1
                && StealthSystem.SpotPoolAgainst(p, At(1, 0)) == 0);
        }

        private static void Scenario05_HideDiceByTerrain()
        {
            Reset();
            var u = Unit("s", "Stealth4");
            TerrainCost[At(0, 0)] = 1;
            TerrainCost[At(0, 1)] = 2;
            TerrainCost[At(0, 2)] = 3;
            Check("05 Stealth4 hide dice on cost 1/2/3 -> 4/5/6",
                StealthSystem.HideDiceFor(u, At(0, 0)) == 4
                && StealthSystem.HideDiceFor(u, At(0, 1)) == 5
                && StealthSystem.HideDiceFor(u, At(0, 2)) == 6);
        }

        private static void Scenario06_TieKeepsStealth()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            var hider = Unit("h", "Stealth4");
            ArmyRegistry.Register(Army("h", At(0, 0), p2, hider));
            ArmyRegistry.Register(Army("o", At(0, 0), p1, Unit("o", "r1s5")));
            StealthSystem.EnterStealth(hider);

            _stubSpotSuccesses = 3; _stubHideSuccesses = 3;
            bool tie = !StealthSystem.ResolveDetection(hider, p1, At(0, 0));
            _stubSpotSuccesses = 4; _stubHideSuccesses = 3;
            bool win = StealthSystem.ResolveDetection(hider, p1, At(0, 0));
            Check("06 equal successes -> no detection; strictly more -> detection", tie && win);
        }

        private static void Scenario07_ObserversTakeMaxNotSum()
        {
            Reset();
            var p = Player("P");
            ArmyRegistry.Register(Army("a4", At(1, 0), p, Unit("a", "r1s4")));
            ArmyRegistry.Register(Army("a6", At(-1, 0), p, Unit("b", "r1s6")));
            int pool = StealthSystem.SpotPoolAgainst(p, At(0, 0)); // both adjacent to (0,0)
            Check("07 two Recce sources -> max spot pool (6), never summed (10)", pool == 6);
        }

        private static void Scenario08_CoLocatedOrdinaryEnemyNoAutoReveal()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            var hider = Unit("h", "Stealth4");
            ArmyRegistry.Register(Army("h", At(0, 0), p2, hider));
            ArmyRegistry.Register(Army("plain", At(0, 0), p1, Unit("u"))); // ordinary, same hex
            StealthSystem.EnterStealth(hider);

            int pool = StealthSystem.SpotPoolAgainst(p1, At(0, 0)); // exactly 1, no co-location bonus
            _stubSpotSuccesses = 1; _stubHideSuccesses = 4;
            StealthSystem.ResolveDetection(hider, p1, At(0, 0));
            bool stillHidden = StealthSystem.IsHiddenFrom(hider, p1);
            Check("08 co-located ordinary enemy: 1 die only, no auto-reveal", pool == 1 && stillHidden);
        }

        private static void Scenario09_ReadApisRunNoChallenge()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            GameSession.Players = new List<PlayerSetupData> { p1, p2 };
            var hider = Unit("h", "Stealth4");
            ArmyRegistry.Register(Army("h", At(0, 0), p2, hider));
            var obs = Army("o", At(0, 0), p1, Unit("o", "r1s5"));
            ArmyRegistry.Register(obs);
            StealthSystem.EnterStealth(hider);
            VisionSystem.RecomputeFor(p1);

            _challengeRolls = 0;
            _ = StealthSystem.IsHiddenFrom(hider, p1);
            _ = StealthSystem.SpotPoolAgainst(p1, At(0, 0));
            _ = StealthSystem.TargetableMembersFor(obs, p1).ToList();
            _ = BattleInitiator.FindEnemyAt(At(0, 0), p1);
            Check("09 inspecting/menu-style reads never trigger a challenge", _challengeRolls == 0);
        }

        private static void Scenario10_OneChallengePerPairPerEvent()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            GameSession.Players = new List<PlayerSetupData> { p1, p2 };
            var hider = Unit("h", "Stealth4");
            var movedArmy = Army("h", At(0, 0), p2, hider);
            ArmyRegistry.Register(movedArmy);
            // TWO observer sources for p1, both able to challenge the same hex.
            ArmyRegistry.Register(Army("o1", At(0, 0), p1, Unit("o1", "r1s5")));
            ArmyRegistry.Register(Army("o2", At(1, 0), p1, Unit("o2", "r1s6")));
            StealthSystem.EnterStealth(hider);
            VisionSystem.RecomputeFor(p1);
            _stubSpotSuccesses = 0; _stubHideSuccesses = 9; // never actually detects

            _challengeRolls = 0;
            StealthSystem.RunChecksForArrival(movedArmy, At(0, 0));
            int afterArrival = _challengeRolls;
            StealthSystem.RunChecksForNewVisionSource(p1);
            int afterCard = _challengeRolls;
            StealthSystem.RunChecksAfterHiddenUnitAction(hider, At(0, 0), p2);
            int afterAction = _challengeRolls;

            Check("10 arrival / new-vision / hidden-action each roll exactly one challenge per pair",
                afterArrival == 1 && afterCard == 2 && afterAction == 3);
        }

        private static void Scenario11_HiddenOnlyArmyIsInert()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            var hider = Unit("h", "Stealth4");
            var hidden = Army("h", At(0, 0), p2, hider);
            ArmyRegistry.Register(hidden);
            StealthSystem.EnterStealth(hider);

            var mover = Army("m", At(0, 0), p1, Unit("m"));
            bool noContactTarget = BattleInitiator.FindEnemyAt(At(0, 0), p1) == null;
            bool fullyHidden = StealthSystem.ArmyFullyHiddenFrom(hidden, p1);
            bool hidderCannotInitiate = !BattleInitiator.CanInitiateContact(hidden);
            bool moverStillCanInitiate = BattleInitiator.CanInitiateContact(mover);
            Check("11 hidden-only army: no contact target, inert, doesn't block the mover",
                noContactTarget && fullyHidden && hidderCannotInitiate && moverStillCanInitiate);
        }

        private static void Scenario12_MixedArmyRoster()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            var visible = Unit("v");
            var hidden = Unit("h", "Stealth4");
            var mixed = Army("mixed", At(0, 0), p2, visible, hidden);
            ArmyRegistry.Register(mixed);
            StealthSystem.EnterStealth(hidden);

            // On the MAP: enemy sees only the visible member; the mixed army is still a normal
            // contact target through it.
            var mapRoster = StealthSystem.TargetableMembersFor(mixed, p1).ToList();
            bool mapShowsOnlyVisible = mapRoster.Count == 1 && mapRoster[0] == visible;
            bool engageable = BattleInitiator.IsEngageable(mixed, p1);
            bool foundForContact = BattleInitiator.FindEnemyAt(At(0, 0), p1) == mixed;

            // Joining the battle reveals every hidden member — the full roster fights.
            StealthSystem.RevealArmy(mixed);
            var battleRoster = StealthSystem.TargetableMembersFor(mixed, p1).ToList();
            bool battleShowsAll = battleRoster.Count == 2 && !hidden.IsHidden;

            Check("12 mixed army: map shows visible member only; battle initiation reveals the whole roster",
                mapShowsOnlyVisible && engageable && foundForContact && battleShowsAll);
        }

        private static void Scenario13_HiddenUnitCannotHoldOrTakeBuilding()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            var building = new BuildingData { Name = "Base", Hex = At(0, 0), Owner = p1, IsBase = true };
            BuildingRegistry.Register(At(0, 0), building);

            // p1's only defender on the hex is hidden from p2 -> doesn't hold the base.
            var hiddenDefender = Unit("d", "Stealth4");
            ArmyRegistry.Register(Army("def", At(0, 0), p1, hiddenDefender));
            StealthSystem.EnterStealth(hiddenDefender);
            var visibleAttacker = Army("atk", At(0, 0), p2, Unit("a"));
            ArmyRegistry.Register(visibleAttacker);
            BuildingRegistry.CaptureOrDestroyIfUndefended(At(0, 0), p2, null, visibleAttacker);
            bool captured = building.Owner == p2;

            // Reverse: a fully-hidden mover cannot capture as an invisible attacker.
            Reset();
            var q1 = Player("Q1");
            var q2 = Player("Q2");
            var b2 = new BuildingData { Name = "Base2", Hex = At(5, 5), Owner = q1, IsBase = true };
            BuildingRegistry.Register(At(5, 5), b2);
            var hiddenMover = Unit("hm", "Stealth4");
            var hiddenArmy = Army("hm", At(5, 5), q2, hiddenMover);
            ArmyRegistry.Register(hiddenArmy);
            StealthSystem.EnterStealth(hiddenMover);
            BuildingRegistry.CaptureOrDestroyIfUndefended(At(5, 5), q2, null, hiddenArmy);
            bool notCaptured = b2.Owner == q1;

            Check("13 hidden unit neither holds a base nor captures one", captured && notCaptured);
        }

        private static void Scenario14_AviationIgnoresHiddenUndetected()
        {
            Reset();
            var p1 = Player("P1");
            var p2 = Player("P2");
            var hider = Unit("h", "Stealth4");
            var hiddenArmy = Army("h", At(0, 0), p2, hider);
            ArmyRegistry.Register(hiddenArmy);
            StealthSystem.EnterStealth(hider);

            bool noneBeforeDetect = AviationCombatPresenter.FindAirStrikeTargetsAt(At(0, 0), p1).Count == 0;
            StealthSystem.MarkDetected(hider, p1);
            bool oneAfterDetect = AviationCombatPresenter.FindAirStrikeTargetsAt(At(0, 0), p1).Contains(hiddenArmy);
            Check("14 air strike target scan ignores a hidden-undetected unit, sees a detected one",
                noneBeforeDetect && oneAfterDetect);
        }

        private static void Scenario15_DetectionWindow()
        {
            Reset();
            var a = Player("A"); // the observer
            var owner = Player("O");
            var unit = Unit("x", "Stealth4");
            ArmyRegistry.Register(Army("x", At(0, 0), owner, unit));
            StealthSystem.EnterStealth(unit);

            CompletedTurns[a] = 4;             // A has finished 4 of its own turns
            StealthSystem.MarkDetected(unit, a);
            bool visibleNow = !StealthSystem.IsHiddenFrom(unit, a);      // count 4 <= snapshot 4
            CompletedTurns[a] = 5;             // A's next turn has now ended
            bool hiddenAgain = StealthSystem.IsHiddenFrom(unit, a);     // count 5 > snapshot 4

            StealthSystem.MarkDetected(unit, a); // re-detect at 5
            bool visibleAgain = !StealthSystem.IsHiddenFrom(unit, a);
            Check("15 detection lasts through the observer's next turn end, then lapses",
                visibleNow && hiddenAgain && visibleAgain);
        }

        private static void Scenario16_OwnerGetsNoSignal()
        {
            Reset();
            var enemy = Player("E");
            var owner = Player("O");
            var unit = Unit("Scout-7", "Stealth4");
            ArmyRegistry.Register(Army("x", At(3, 2), owner, unit));
            ArmyRegistry.Register(Army("o", At(3, 2), enemy, Unit("o", "r1s6")));
            StealthSystem.EnterStealth(unit);
            _stubSpotSuccesses = 5; _stubHideSuccesses = 0;
            bool rolled = StealthSystem.ResolveDetection(unit, enemy, At(3, 2));

            bool ownerUnaffected = !StealthSystem.IsHiddenFrom(unit, owner); // owner just sees own unit, as always
            bool ownerNoNotice = StealthSystem.TakeDetectionNotices(owner).Count == 0;
            bool debugOff = !StealthSystem.DebugLog;
            bool noReverseLookup = typeof(StealthSystem).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .All(m => !m.Name.Contains("WhoDetected") && !m.Name.Contains("Detectors"));

            // The detector — and only the detector — gets a turn-start notice naming the
            // unit and its (col,row); it drains on read (not re-announced).
            var detectorNotices = StealthSystem.TakeDetectionNotices(enemy);
            (int col, int row) = At(3, 2).ToOffset();
            bool detectorNoticed = detectorNotices.Count == 1
                && detectorNotices[0].Contains("Scout-7") && detectorNotices[0].Contains($"({col}, {row})");
            bool drained = StealthSystem.TakeDetectionNotices(enemy).Count == 0;

            Check("16 owner told nothing; detector alone gets one turn-start notice (drains on read)",
                rolled && ownerUnaffected && ownerNoNotice && debugOff && noReverseLookup
                && detectorNoticed && drained);
        }

        private static void Scenario17_DirectedActionLiftsStealth()
        {
            Reset();
            var attacker = Player("A");
            var owner = Player("O");
            var target = Unit("t", "Stealth4");
            var army = Army("t", At(0, 0), owner, target);
            ArmyRegistry.Register(army);
            StealthSystem.EnterStealth(target);
            StealthSystem.MarkDetected(target, attacker);

            bool targetableWhileDetected = BattleInitiator.FindEnemyAt(At(0, 0), attacker) == army;
            StealthSystem.ExitStealth(target); // what the directed-action code paths call
            bool revealedToAll = !target.IsHidden
                                 && !StealthSystem.IsHiddenFrom(target, attacker)
                                 && !StealthSystem.IsDetectedBy(target, attacker); // table cleared
            Check("17 a detected hidden unit is a valid target; the directed action lifts its stealth",
                targetableWhileDetected && revealedToAll);
        }

        private static void Scenario18_AiMemoryStaysHonest()
        {
            Reset();
            AiMapMemory.Clear();
            AiMapMemory.EnsureSubscribed();
            var ai = Player("AI");
            var foe = Player("Foe");
            GameSession.Players = new List<PlayerSetupData> { ai, foe };

            var hider = Unit("h", "Stealth4");
            var hiddenArmy = Army("h", At(0, 0), foe, hider);
            ArmyRegistry.Register(hiddenArmy);
            StealthSystem.EnterStealth(hider);
            // Give the AI vision of the hex.
            ArmyRegistry.Register(Army("scout", At(0, 0), ai, Unit("s", "r1s5")));
            VisionSystem.RecomputeFor(ai);

            bool notRemembered = AiMapMemory.KnownEnemySightingAt(ai, At(0, 0)) == null;
            StealthSystem.MarkDetected(hider, ai); // fires StealthChanged -> AiMapMemory re-snapshots
            bool rememberedOnceVisible = AiMapMemory.KnownEnemySightingAt(ai, At(0, 0)) != null;
            Check("18 AI memory never records a hidden-undetected enemy; records a detected one",
                notRemembered && rememberedOnceVisible);
        }

        private static void Scenario19_AiSoloScoutStealthGate()
        {
            Reset();
            var p = Player("P");
            var scoutUnit = Unit("s", "r1s5", "Stealth4");
            var solo = Army("solo", At(0, 0), p, scoutUnit);
            ArmyRegistry.Register(solo);

            bool isSoloRecce = AiArmyRoles.IsSoloRecce(solo);
            bool canStealth = StealthSystem.CanEnterStealth(scoutUnit);
            // The AiTurnController.MoveArmyRoutine gate: solo recce + can-enter-stealth + has 1 AP
            // -> stealth before first move. AP + coroutine live outside this harness; the gate
            // itself is what this asserts.
            Check("19 AI solo scout with Stealth4 satisfies the pre-move auto-stealth gate",
                isSoloRecce && canStealth);
        }

        private static void Scenario20_EnterCostsGatedExitFree()
        {
            Reset();
            var p = Player("P");
            var u = Unit("u", "Stealth4");
            ArmyRegistry.Register(Army("a", At(0, 0), p, u));

            bool canEnterOnce = StealthSystem.CanEnterStealth(u);
            StealthSystem.EnterStealth(u);
            bool cannotEnterAgain = !StealthSystem.CanEnterStealth(u); // already hidden
            StealthSystem.ExitStealth(u);                             // free primitive, no gate
            bool exitedClean = !u.IsHidden;
            var plain = Unit("plain");
            bool plainCannotStealth = !StealthSystem.CanEnterStealth(plain); // no Stealth tag
            Check("20 enter is gated (Stealth4, not already hidden); exit is an unconditional free primitive",
                canEnterOnce && cannotEnterAgain && exitedClean && plainCannotStealth);
        }

        private static void Scenario21_DetectedOnOwnTurnLastsThroughNextTurn()
        {
            Reset();
            var a = Player("A"); // the observer
            var owner = Player("O");
            var unit = Unit("x", "Stealth4");
            ArmyRegistry.Register(Army("x", At(0, 0), owner, unit));
            StealthSystem.EnterStealth(unit);

            // A scores the detection DURING its own turn — A's completed-turn count is not
            // bumped until that turn ends, so a bare snapshot (== 4) would lapse the instant
            // the count reaches 5 at this turn's end. The design says it must survive through
            // the end of A's NEXT turn (count 6).
            StealthSystem.ObserverTakingTurnProvider = p => p == a;
            CompletedTurns[a] = 4;
            StealthSystem.MarkDetected(unit, a);
            bool visibleNow = !StealthSystem.IsHiddenFrom(unit, a);      // count 4 <= expiry 5

            StealthSystem.ObserverTakingTurnProvider = _ => false;       // A's turn ended
            CompletedTurns[a] = 5;
            bool stillVisibleNextTurn = !StealthSystem.IsHiddenFrom(unit, a); // count 5 <= expiry 5
            CompletedTurns[a] = 6;                                       // A's NEXT turn now ended
            bool hiddenAgain = StealthSystem.IsHiddenFrom(unit, a);      // count 6 > expiry 5

            StealthSystem.ObserverTakingTurnProvider = _ => false;
            Check("21 detected on the observer's own turn -> lasts through the end of their NEXT turn",
                visibleNow && stillVisibleNextTurn && hiddenAgain);
        }

        // ---------------------------------------------------------------- helpers ----

        private static void Reset()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
            CompletedTurns.Clear();
            TerrainCost.Clear();
            GameSession.Players = new List<PlayerSetupData>();
            StealthSystem.ObserverTakingTurnProvider = _ => false;
            _stubSpotSuccesses = 0;
            _stubHideSuccesses = 0;
            _challengeRolls = 0;
        }

        private static PlayerSetupData Player(string name) => new PlayerSetupData { Nickname = name, IsHuman = false };

        private static UnitData Unit(string name, params string[] abilities)
        {
            var u = new UnitData { Name = name, Defense = 1, Attack = 1, HitPointsCurrent = 1, HitPointsMax = 1 };
            foreach (string a in abilities)
                u.Abilities.Add(a);
            return u;
        }

        private static ArmyData Army(string name, HexCoord hex, PlayerSetupData owner, params UnitData[] members)
        {
            var a = new ArmyData { Name = name, Hex = hex, Owner = owner };
            foreach (UnitData m in members)
            {
                m.Owner = owner;
                a.Members.Add(m);
            }
            return a;
        }

        private static HexCoord At(int q, int r) => new HexCoord(q, r);

        private static bool[] Trues(int n)
        {
            var arr = new bool[Math.Max(0, n)];
            for (int i = 0; i < arr.Length; i++) arr[i] = true;
            return arr;
        }

        private static void Check(string name, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine($"PASS  {name}"); }
            else { _failed++; Console.WriteLine($"FAIL  {name}"); }
        }
    }
}
