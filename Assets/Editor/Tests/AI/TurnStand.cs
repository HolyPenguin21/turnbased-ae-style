#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  TURN STAND: the whole AI turn (AiTurnController.RunTurn -> Pipeline.RunTurn) on a small map,
    //  built from the AI players' authored decks: own Citadel, garrison, field army and scout, enemies
    //  seen now, facts pending before the turn loop runs. A module under cleanup adds its own scenarios
    //  here (TurnScenario + its setup + its expectation).
    //
    //  Two modes over the same cases:
    //    - decisions (TurnStandTests, EditMode): armies have no scene controller, so no move is
    //      executed and no card is deployed; every decision and card arbitration of the turn, the
    //      Phase B rounds and the end of the turn run. ~0.4 s per case.
    //    - execution (TurnStandPlayTests, one PlayMode test over every case): every stand army has an
    //      ArmyController and the turn has a HexSelectionController, so moves, garrison extractions,
    //      assemblies and card plays really happen; an army the turn creates gets its marker the next
    //      frame. Limit: no battle screen, so a contact starts no fight.
    //  Every turn is checked against the turn's boundaries: no exception, AP never below zero, no
    //  reservation or claim invariant violation and no [ERROR] check line, no reservation left after
    //  the turn, no unit lost, the turn session closed; execution also fails a turn that does not
    //  finish. An expectation case also checks what its turn must show. The state after the turn and
    //  the turn's control lines go to a digest (TURN_STAND_OUT, default Logs/TurnStand; execution
    //  digests in its "play" folder) compared between revisions. TURN_STAND_CASES (a regex over
    //  "<scenario>.<deck>") narrows the execution run.
    // ===========================================================================================
    public enum TurnScenario
    {
        Quiet,           // own forces only
        Contact,         // a weak enemy army seen now, 3 hexes away
        Threat,          // a strong enemy army seen now, 2 hexes away
        PendingContact,  // a weak enemy army whose discovery is pending when the turn starts
        PendingHand,     // a hand opportunity pending when the turn starts
        // Expectation cases (each states what the turn must show, besides the boundaries):
        HostAdjacent,       // a strong army next to the Citadel: an ActiveDefence objective is accepted
        TwoContacts,        // a weak army east, a middle one south-west: both are known contacts
        NoFieldArmyThreat,  // only the garrison and a scout against a strong army: the garrison asks for power
        ZeroApThreat,       // the strong army with 0 AP: nothing spends AP, AP stays 0
        RichThreat,         // the strong army with 40 of every resource: boundaries only
        FullHand,           // the hand at its capacity: Phase B never draws while it is full
        PendingContactFar,  // a far weak army whose discovery is pending: the loop takes the Contact fact
        GarrisonOnly,       // the garrison alone, no enemy: no defence objective, no mission step moves AP
        // Provisioning cases (actor binding: garrison extraction, same-hex assembly, sortie):
        GarrisonRecce,      // the only Recce stands in the garrison beside an empty shell: Recon may extract it
        DonorOnHost,        // a second army on the field army's hex, a weak enemy seen: the assault may assemble
        GarrisonSortie,     // no field army, a weak enemy next to the Citadel, a strong garrison: a sortie
        GarrisonBuilder,    // the hero in the garrison, a resource hex in sight: Economy may extract a builder
    }

    // One stand turn: the world of a case, its run and its checks. Shared by both modes.
    internal sealed class TurnStandCase : IDisposable
    {
        internal const string CatalogPath = "Assets/Cards/StartingDeckCatalog.asset";
        private const string GameConfigPath = "Assets/Config/GameConfig.asset";
        private const string ResearchProductionPath = "Assets/Cards/ResearchProductionCatalog.asset";
        private const string GameScenePath = "Assets/Scenes/Game.unity";
        internal const int TurnNumber = 1;

        internal readonly List<string> Lines = new List<string>();
        private readonly List<Object> _objects = new List<Object>();
        private readonly bool _play;
        private HexSelectionController _selection;

        internal readonly TurnScenario Scenario;
        internal readonly string OwnDeck, EnemyDeck;
        internal PlayerSetupData Me, Foe;
        internal PlayerRoot Root;
        internal AiHandData Hand;
        internal AiTurnContext Ctx;
        internal string Ignored;
        private HashSet<UnitData> _unitsBefore;

        internal TurnStandCase(string ownDeck, string enemyDeck, TurnScenario scenario, bool play)
        {
            OwnDeck = ownDeck; EnemyDeck = enemyDeck; Scenario = scenario; _play = play;
            Game.Setup.MatchStateInventory.ResetForNewMatch();
            AiDebugLog.LogToUnityConsole = true;
            Application.logMessageReceived += Capture;
        }

        public void Dispose()
        {
            Application.logMessageReceived -= Capture;
            AiDebugLog.LogToUnityConsole = false;
            foreach (Object o in _objects)
                if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            Game.Setup.MatchStateInventory.ResetForNewMatch();
            Game.Core.GameSession.Players = new List<PlayerSetupData>();
        }

        private void Capture(string message, string stack, LogType type)
        {
            if (message != null && message.Contains("[AI]"))
                Lines.Add(message);
            if (type == LogType.Exception)
                Lines.Add("EXCEPTION " + message);
        }

        internal static IEnumerable<(string Own, string Enemy, TurnScenario Scenario)> All()
        {
            foreach ((string own, string enemy) in HousekeepingStand.DeckPairs)
                foreach (TurnScenario s in Enum.GetValues(typeof(TurnScenario)))
                    yield return (own, enemy, s);
        }

        internal string Name => $"{Scenario}.{OwnDeck.Replace(' ', '_')}";

        // ---- world ------------------------------------------------------------------------------
        // Builds the case. Returns false with Ignored set when the decks cannot stage it.
        internal bool Build()
        {
            TurnScenario scenario = Scenario;
            StartingDeckCatalog catalog = UnityEditor.AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>(CatalogPath);
            StartingDeck own = catalog.aiDecks.First(d => d != null && d.deckName == OwnDeck);
            StartingDeck foeDeck = catalog.aiDecks.First(d => d != null && d.deckName == EnemyDeck);
            UnityEngine.Random.InitState(OwnDeck.GetHashCode() ^ (int)scenario * 7919);

            var mapObject = new GameObject("turn stand map");
            _objects.Add(mapObject);
            HexMap map = mapObject.AddComponent<HexMap>();
            var hexes = new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry>();
            const int radius = 6;
            for (int q = -radius; q <= radius; q++)
                for (int r = -radius; r <= radius; r++)
                    if (HexGridMath.Distance(new HexCoord(0, 0), new HexCoord(q, r)) <= radius)
                        hexes[new HexCoord(q, r)] = new Game.Terrain.TerrainTypeEntry
                            { terrainName = "Plains", moveCost = 1 };
            map.SetData(radius, 1f, hexes);
            // The match's own world configuration (vision rules, map observation, stealth terrain,
            // AI memory bound to the map), as a launched match runs it.
            var config = UnityEditor.AssetDatabase.LoadAssetAtPath<Game.Core.GameConfig>(GameConfigPath);
            Game.Setup.MatchSceneAssembly.ConfigureWorld(config, map);
            if (_play)
            {
                var selectionObject = new GameObject("turn stand selection");
                _objects.Add(selectionObject);
                _selection = selectionObject.AddComponent<HexSelectionController>();
                _selection.enabled = false;
                typeof(HexSelectionController).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(_selection, map);
            }

            Me = new PlayerSetupData { Nickname = "Stand", ColorIndex = 0, Faction = own.faction,
                AiDeckName = own.deckName, IsHuman = false, CitadelHexQ = 0, CitadelHexR = 0 };
            Foe = new PlayerSetupData { Nickname = "Foe", ColorIndex = 1, Faction = foeDeck.faction,
                AiDeckName = foeDeck.deckName, IsHuman = false, CitadelHexQ = 6, CitadelHexR = 0 };
            Game.Core.GameSession.Players = new List<PlayerSetupData> { Me, Foe };
            Root = NewRoot(Me);
            NewRoot(Foe);
            Citadel(catalog, Me, new HexCoord(0, 0));
            Citadel(catalog, Foe, new HexCoord(6, 0));

            List<CardDefinition> ownCards = Cards(catalog, own);
            List<CardDefinition> foeCards = Cards(catalog, foeDeck);
            List<CardDefinition> bodies = GroundBodies(ownCards, Me);
            CardDefinition hero = ownCards.Where(c => c.cardType == CardType.Hero)
                .OrderByDescending(HeroRoleEvaluator.CombatLeadershipScore)
                .ThenBy(c => c.displayName, StringComparer.Ordinal).FirstOrDefault();
            if (bodies.Count == 0 || hero == null)
            {
                Ignored = $"{OwnDeck}: no ground body or hero";
                return false;
            }
            CardDefinition recce = ownCards.FirstOrDefault(c => c.cardType == CardType.Unit && !c.isAviation
                && AbilityParams.UnitHasAnyRecce(UnitData.FromDefinition(c, Me)));
            if (scenario == TurnScenario.GarrisonRecce && recce == null)
            {
                Ignored = $"{OwnDeck}: no ground Recce";
                return false;
            }

            bool fieldArmy = scenario != TurnScenario.NoFieldArmyThreat && scenario != TurnScenario.GarrisonOnly
                && scenario != TurnScenario.GarrisonSortie && scenario != TurnScenario.GarrisonBuilder;
            switch (scenario)
            {
                case TurnScenario.GarrisonRecce:
                    Army(Me, new HexCoord(0, 0), true, bodies[0], recce);
                    break;
                case TurnScenario.GarrisonSortie:
                    Army(Me, new HexCoord(0, 0), true, bodies[0], bodies[bodies.Count - 1],
                        bodies[bodies.Count - 1], bodies[bodies.Count - 1]);
                    break;
                case TurnScenario.GarrisonBuilder:
                    Army(Me, new HexCoord(0, 0), true, bodies[0], hero);
                    break;
                default:
                    Army(Me, new HexCoord(0, 0), true, bodies[0]);
                    break;
            }
            if (fieldArmy)
                Army(Me, new HexCoord(1, 0), false, hero, bodies[bodies.Count / 2], bodies[bodies.Count - 1]);
            if (recce != null && scenario != TurnScenario.GarrisonOnly && scenario != TurnScenario.GarrisonRecce)
                Army(Me, new HexCoord(0, 1), false, recce);
            if (scenario == TurnScenario.GarrisonRecce)
                Army(Me, new HexCoord(0, 0), false);
            if (scenario == TurnScenario.DonorOnHost)
                Army(Me, new HexCoord(1, 0), false, bodies[0], bodies[bodies.Count / 2]);
            if (scenario == TurnScenario.GarrisonBuilder)
                HexResourceBonusRegistry.Set(new HexCoord(2, -1), new ResourceYields { materials = 3 });
            if (scenario == TurnScenario.ZeroApThreat)
                Root.ActionPoints = 0;
            if (scenario == TurnScenario.RichThreat)
                foreach (ResourceType type in new[] { ResourceType.Human, ResourceType.Energy,
                             ResourceType.Materials, ResourceType.Tech })
                    Root.AddResource(type, 34);

            // The context AiTurnContext.From builds in a match: the scene hand's draw price, start
            // size and capacity, the game config and the Research/Production catalog.
            SceneHand sceneHand = SceneHand.Read();
            Ctx = new AiTurnContext
            {
                Map = map, StartingDeckCatalog = catalog, StartingHandSize = sceneHand.StartingSize,
                DrawApCost = sceneHand.DrawApCost, HandCapacity = sceneHand.Capacity,
                GameConfig = config, TurnNumber = TurnNumber, StepDelay = 0f, HexSelection = _selection,
                ResearchProductionCatalog = UnityEditor.AssetDatabase
                    .LoadAssetAtPath<ResearchProductionCatalog>(ResearchProductionPath),
            };
            Hand = AiHandRegistry.GetOrCreate(Me, catalog, Ctx.StartingHandSize, Ctx.HandCapacity);
            Hand.SetCurrentTurn(TurnNumber);
            if (scenario == TurnScenario.FullHand)
                while (Hand.HasFreeSlot && Hand.HasCardsLeftToDraw)
                    Hand.DrawOne();

            List<CardDefinition> foeBodies = GroundBodies(foeCards, Foe);
            if (scenario >= TurnScenario.HostAdjacent)
            {
                if (!PlaceExpectationEnemies(foeBodies))
                    return false;
            }
            else if (scenario != TurnScenario.Quiet && scenario != TurnScenario.PendingHand)
            {
                if (foeBodies.Count == 0)
                {
                    Ignored = $"{EnemyDeck}: no ground body";
                    return false;
                }
                ArmyData enemy = scenario == TurnScenario.Threat
                    ? Army(Foe, new HexCoord(3, 0), false,
                        Enumerable.Repeat(foeBodies[foeBodies.Count - 1], 4).ToArray())
                    : Army(Foe, new HexCoord(4, 0), false, foeBodies[0], foeBodies[0]);
                AiMapMemory.ObserveForTest(Me, enemy);
                if (scenario == TurnScenario.PendingContact)
                    StrategicInterruptRegistry.MarkDiscovery(Me, TurnNumber, new[] { enemy.Id });
            }
            if (scenario == TurnScenario.PendingHand)
                StrategicInterruptRegistry.MarkHandOpportunity(Me, TurnNumber);
            if (scenario == TurnScenario.GarrisonBuilder)
                AiMapMemory.RefreshVisibleForTest(Me);

            _unitsBefore = OwnUnits(Me);
            return true;
        }

        // The enemies of the expectation cases, all seen now (AiMapMemory observation).
        private bool PlaceExpectationEnemies(List<CardDefinition> foeBodies)
        {
            TurnScenario scenario = Scenario;
            if (scenario == TurnScenario.FullHand || scenario == TurnScenario.GarrisonOnly
                || scenario == TurnScenario.GarrisonRecce || scenario == TurnScenario.GarrisonBuilder)
                return true;
            if (foeBodies.Count == 0)
            {
                Ignored = "enemy deck: no ground body";
                return false;
            }
            CardDefinition weak = foeBodies[0], mid = foeBodies[foeBodies.Count / 2],
                strong = foeBodies[foeBodies.Count - 1];
            var seen = new List<ArmyData>();
            switch (scenario)
            {
                case TurnScenario.HostAdjacent:
                    seen.Add(Army(Foe, new HexCoord(1, -1), false, strong, strong, strong, strong));
                    break;
                case TurnScenario.TwoContacts:
                    seen.Add(Army(Foe, new HexCoord(4, 0), false, weak, weak));
                    seen.Add(Army(Foe, new HexCoord(-3, 2), false, mid, mid, mid));
                    break;
                case TurnScenario.NoFieldArmyThreat:
                case TurnScenario.ZeroApThreat:
                case TurnScenario.RichThreat:
                    seen.Add(Army(Foe, new HexCoord(3, 0), false, strong, strong, strong, strong));
                    break;
                case TurnScenario.PendingContactFar:
                    seen.Add(Army(Foe, new HexCoord(-3, -3), false, weak, weak));
                    break;
                case TurnScenario.DonorOnHost:
                    seen.Add(Army(Foe, new HexCoord(3, 0), false, weak, weak));
                    break;
                case TurnScenario.GarrisonSortie:
                    seen.Add(Army(Foe, new HexCoord(1, -1), false, weak));
                    break;
            }
            foreach (ArmyData enemy in seen)
                AiMapMemory.ObserveForTest(Me, enemy);
            if (scenario == TurnScenario.PendingContactFar)
                StrategicInterruptRegistry.MarkDiscovery(Me, TurnNumber, seen.Select(a => a.Id));
            return true;
        }

        // The values the match's CardHandUI hands AiTurnContext.From: its serialized draw price and
        // capacity in the Game scene, and its start size constant.
        private readonly struct SceneHand
        {
            internal readonly int DrawApCost, Capacity, StartingSize;
            private SceneHand(int draw, int capacity, int start) { DrawApCost = draw; Capacity = capacity; StartingSize = start; }

            internal static SceneHand Read()
            {
                string scriptGuid = UnityEditor.AssetDatabase.FindAssets("CardHandUI t:MonoScript")
                    .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith("/CardHandUI.cs", StringComparison.Ordinal))
                    .Select(UnityEditor.AssetDatabase.AssetPathToGUID).Single();
                string[] lines = File.ReadAllLines(GameScenePath);
                int at = Array.FindIndex(lines, l => l.Contains("guid: " + scriptGuid));
                Assert.That(at, Is.GreaterThanOrEqualTo(0), "the Game scene has no CardHandUI");
                int Field(string name)
                {
                    for (int i = at; i < lines.Length && !lines[i].StartsWith("---", StringComparison.Ordinal); i++)
                        if (lines[i].TrimStart().StartsWith(name + ":", StringComparison.Ordinal))
                            return int.Parse(lines[i].Split(':')[1].Trim());
                    throw new InvalidOperationException("CardHandUI." + name + " not serialized in the Game scene");
                }
                int start = (int)typeof(Game.UI.CardHandUI).GetField("startingHandSize",
                    BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
                return new SceneHand(Field("drawApCost"), Field("maxHandSize"), start);
            }
        }

        private PlayerRoot NewRoot(PlayerSetupData player)
        {
            PlayerRoot root = PlayerRoot.Create(player, "turn stand " + player.Nickname);
            _objects.Add(root.gameObject);
            root.ActionPoints = 10;
            foreach (ResourceType type in new[] { ResourceType.Human, ResourceType.Energy,
                         ResourceType.Materials, ResourceType.Tech })
                root.AddResource(type, 6);
            PlayerRootRegistry.Register(player, root);
            return root;
        }

        private static void Citadel(StartingDeckCatalog catalog, PlayerSetupData owner, HexCoord hex)
        {
            CardDefinition baseCard = catalog.GetCatalog(owner.Faction)?.ForType(CardType.Base).FirstOrDefault();
            BuildingData building = BuildingData.FromDefinition(baseCard, hex, owner)
                ?? new BuildingData { Hex = hex, Owner = owner, IsBase = true, Defense = 1 };
            building.IsStartingCitadel = true;
            BuildingRegistry.Register(hex, building);
        }

        private static List<CardDefinition> Cards(StartingDeckCatalog catalog, StartingDeck deck) =>
            deck.cards.Where(e => e != null && e.count > 0).Select(e => catalog.ResolveCard(e.cardKey))
                .Where(c => c != null).Distinct().OrderBy(c => c.displayName, StringComparer.Ordinal).ToList();

        private static List<CardDefinition> GroundBodies(IEnumerable<CardDefinition> cards, PlayerSetupData owner) =>
            cards.Where(c => c.cardType == CardType.Unit && !c.isAviation && c.airfieldCapacity <= 0)
                .Where(c => AiArmyRoles.IsGroundBattleBody(UnitData.FromDefinition(c, owner)))
                .OrderBy(c => AiPower.ToPowerUnit(UnitData.FromDefinition(c, owner)).BasePower)
                .ThenBy(c => c.displayName, StringComparer.Ordinal).ToList();

        // In execution mode every stand army gets a scene controller with instant steps.
        private ArmyData Army(PlayerSetupData owner, HexCoord hex, bool garrison, params CardDefinition[] cards)
        {
            var army = new ArmyData { Owner = owner, Hex = hex, IsGarrison = garrison };
            foreach (CardDefinition c in cards)
                army.AddMemberSorted(UnitData.FromDefinition(c, owner));
            if (_play)
                AttachController(army);
            ArmyRegistry.Register(army);
            return army;
        }

        // Execution: an army the turn creates gets its marker, as HexSelectionController.CreateArmyMarker
        // gives one in a match (the stand has no marker prefab).
        internal void AttachMarkers()
        {
            foreach (ArmyData army in ArmyRegistry.AllForOwner(Me).Where(a => a != null && a.Controller == null
                         && !a.IsPrison).ToList())
                AttachController(army);
        }

        private void AttachController(ArmyData army)
        {
            var marker = new GameObject($"turn stand army #{army.Id}");
            _objects.Add(marker);
            ArmyController controller = marker.AddComponent<ArmyController>();
            typeof(ArmyController).GetField("stepDuration", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, 0f);
            controller.SetData(army);
            army.Controller = controller;
        }

        private static HashSet<UnitData> OwnUnits(PlayerSetupData player) =>
            new HashSet<UnitData>(ArmyRegistry.AllForOwner(player).Where(a => a != null)
                .SelectMany(a => a.Members).Where(u => u != null));

        // ---- run --------------------------------------------------------------------------------
        // Decisions: the turn drained synchronously (no controller, so no wait ever blocks).
        internal static void Drain(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is IEnumerator nested) stack.Push(nested);
            }
        }

        // Execution: the turn advanced until it waits for a frame (a null, WaitForSeconds, a
        // CustomYieldInstruction still waiting), so the scene's own coroutines (movement) can run.
        // An exception ends the turn and is kept in Failure.
        internal sealed class FrameStepper
        {
            private readonly Stack<IEnumerator> _stack = new Stack<IEnumerator>();
            internal Exception Failure;
            internal FrameStepper(IEnumerator routine) => _stack.Push(routine);
            internal bool Done => _stack.Count == 0;

            internal void Tick()
            {
                while (_stack.Count > 0)
                {
                    IEnumerator top = _stack.Peek();
                    if (top is CustomYieldInstruction wait)
                    {
                        if (wait.keepWaiting) return;
                        _stack.Pop();
                        continue;
                    }
                    bool more;
                    try { more = top.MoveNext(); }
                    catch (Exception e) { Failure = e; _stack.Clear(); return; }
                    if (!more) { _stack.Pop(); continue; }
                    if (top.Current is IEnumerator nested) { _stack.Push(nested); continue; }
                    return;
                }
            }
        }

        // ---- checks and digest ------------------------------------------------------------------
        internal List<string> Problems(Exception failure, bool unfinished)
        {
            var problems = new List<string>();
            if (failure != null) problems.Add("exception: " + failure);
            if (unfinished) problems.Add("the turn did not finish");
            if (Root.ActionPoints < 0) problems.Add($"AP below zero: {Root.ActionPoints}");
            problems.AddRange(Lines.Where(l => l.Contains("[AI][V2][INVARIANT]")
                || l.Contains("[Invariant] ERROR") || l.Contains("][ERROR]") || l.StartsWith("EXCEPTION")));
            if (StrategicResourceReservationLedger.HasAny(Me, TurnNumber))
                problems.Add("a reservation survived the turn");
            if (!unfinished && AiTurnSession.PeekActive(Me) != null)
                problems.Add("the turn session is still active");
            List<UnitData> lost = _unitsBefore.Except(OwnUnits(Me)).ToList();
            if (lost.Count > 0)
                problems.Add("units lost: " + string.Join(", ", lost.Select(u => u.Name)));
            problems.AddRange(Expectations());
            return problems;
        }

        // What each expectation case must show in the turn's own lines (beyond the boundaries).
        private IEnumerable<string> Expectations()
        {
            bool Any(params string[] parts) => Lines.Any(l => parts.All(l.Contains));
            switch (Scenario)
            {
                case TurnScenario.HostAdjacent:
                    if (!Any("[ActiveDefence][Objective] decision=ACCEPT"))
                        yield return "expected an accepted ActiveDefence objective against the army next to the Citadel";
                    break;
                case TurnScenario.TwoContacts:
                    if (!Any("threat: contacts 2"))
                        yield return "expected both enemy armies as known contacts";
                    break;
                case TurnScenario.NoFieldArmyThreat:
                    if (!Any("decision=CREATE", "held_base_garrison_below_floor"))
                        yield return "expected the garrison to ask for field power against the strong army";
                    break;
                case TurnScenario.ZeroApThreat:
                    if (Root.ActionPoints != 0)
                        yield return $"AP changed from 0 to {Root.ActionPoints}";
                    foreach (string l in Lines.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"spent ap [1-9]")))
                        yield return "AP spent with 0 AP: " + l;
                    break;
                // A draw line reports the hand after the draw ("hand N/M ... drawn K"): the hand
                // before it (N - K) must have had a free slot.
                case TurnScenario.FullHand:
                    foreach (string l in Lines.Where(l => l.Contains("WINNER Draw")))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(l, @"hand (\d+)/(\d+).*drawn (\d+)");
                        if (!m.Success || int.Parse(m.Groups[1].Value) - int.Parse(m.Groups[3].Value)
                                >= int.Parse(m.Groups[2].Value))
                            yield return "drew into a full hand: " + l;
                    }
                    break;
                case TurnScenario.PendingContactFar:
                    if (!Lines.Any(l => l.Contains("Triggers=") && l.Contains("Contact")))
                        yield return "the pending Contact fact was never taken by the loop";
                    break;
                case TurnScenario.GarrisonOnly:
                    if (Any("[ActiveDefence][Objective] decision=ACCEPT"))
                        yield return "an ActiveDefence objective without any enemy";
                    break;
            }
        }

        // The state after the turn and the turn's control lines; timings and trace ids are left out.
        internal string Digest(Exception failure)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"case {Scenario} {OwnDeck} vs {EnemyDeck}");
            sb.AppendLine($"failure {(failure == null ? "-" : failure.GetType().Name + ": " + failure.Message)}");
            sb.AppendLine($"ap {Root.ActionPoints} H{Root.GetResource(ResourceType.Human)} "
                + $"E{Root.GetResource(ResourceType.Energy)} M{Root.GetResource(ResourceType.Materials)} "
                + $"T{Root.GetResource(ResourceType.Tech)}");
            sb.AppendLine("hand " + string.Join(", ", Hand.Hand.Select(c => c?.Definition?.displayName ?? "?")
                .OrderBy(n => n, StringComparer.Ordinal)));
            foreach (ArmyData a in ArmyRegistry.AllForOwner(Me).Where(a => a != null).OrderBy(a => a.Id))
                sb.AppendLine($"army #{a.Id} ({a.Hex.Q},{a.Hex.R}){(a.IsGarrison ? " garrison" : "")} "
                    + $"act={(a.HasActivatedThisTurn ? 1 : 0)} ["
                    + string.Join(", ", a.Members.Where(u => u != null).Select(u => u.Name)
                        .OrderBy(n => n, StringComparer.Ordinal)) + "]");
            foreach (MissionIntent i in MissionIntentRegistry.GetOrCreate(Me).All
                         .OrderBy(i => i.IntentKey.ToString(), StringComparer.Ordinal))
                sb.AppendLine($"intent {i.IntentKey} {i.Status} mover={i.PreferredMoverArmyId?.ToString() ?? "-"}");
            string[] markers =
            {
                "[AI][V2][INVARIANT]", "[Invariant] ERROR",
                "strat.B/tempo - END", "[AI][V2][Loop] step=", "admission pass opened",
                "][ERROR]", "end-of-turn facts",
            };
            foreach (string m in markers)
                sb.AppendLine($"lines '{m}' {Lines.Count(l => l.Contains(m))}");
            foreach (string l in Lines.Where(l => l.Contains("[INVARIANT]") || l.Contains("ERROR")
                         || l.StartsWith("EXCEPTION")))
                sb.AppendLine("! " + l);
            return sb.ToString();
        }

        internal void WriteDigest(string digest, string subfolder)
        {
            string dir = Environment.GetEnvironmentVariable("TURN_STAND_OUT");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "TurnStand");
            if (subfolder != null)
                dir = Path.Combine(dir, subfolder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Name + ".txt"), digest);
        }
    }

    public sealed class TurnStandTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach ((string own, string enemy, TurnScenario s) in TurnStandCase.All())
                yield return new TestCaseData(own, enemy, s)
                    .SetName($"TurnStand.{s}.{own.Replace(' ', '_')}_vs_{enemy.Replace(' ', '_')}");
        }

        [TestCaseSource(nameof(Cases))]
        public void Turn(string ownDeck, string enemyDeck, TurnScenario scenario)
        {
            using var stand = new TurnStandCase(ownDeck, enemyDeck, scenario, play: false);
            if (!stand.Build())
                Assert.Ignore(stand.Ignored);
            Exception failure = null;
            try { TurnStandCase.Drain(AiTurnController.RunTurn(stand.Me, stand.Ctx, null)); }
            catch (Exception e) { failure = e; }

            string digest = stand.Digest(failure);
            stand.WriteDigest(digest, null);
            Assert.That(stand.Problems(failure, unfinished: false), Is.Empty, digest);
        }
    }

#if UNITY_6000_3_OR_NEWER
    // The execution mode: one PlayMode test over every case (one domain reload for the whole run).
    public sealed class TurnStandPlayTests
    {
        // A turn that needs more frames than this is reported as not finished.
        private const int MaxFramesPerTurn = 20000;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Game.Core.GameSession.Players = new List<PlayerSetupData>();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator TurnsExecute()
        {
            string filter = Environment.GetEnvironmentVariable("TURN_STAND_CASES");
            var report = new List<string>();
            int ran = 0;
            foreach ((string own, string enemy, TurnScenario scenario) in TurnStandCase.All())
            {
                string name = $"{scenario}.{own.Replace(' ', '_')}";
                if (!string.IsNullOrEmpty(filter) && !System.Text.RegularExpressions.Regex.IsMatch(name, filter))
                    continue;
                var stand = new TurnStandCase(own, enemy, scenario, play: true);
                try
                {
                    if (!stand.Build())
                        continue;
                    ran++;
                    var stepper = new TurnStandCase.FrameStepper(AiTurnController.RunTurn(stand.Me, stand.Ctx, null));
                    int frames = 0;
                    while (true)
                    {
                        stand.AttachMarkers();
                        stepper.Tick();
                        if (stepper.Done || ++frames >= MaxFramesPerTurn)
                            break;
                        yield return null;
                    }
                    // Movement presentation settles on the following frame.
                    yield return null;
                    string digest = stand.Digest(stepper.Failure);
                    stand.WriteDigest(digest, "play");
                    List<string> problems = stand.Problems(stepper.Failure, unfinished: !stepper.Done);
                    if (problems.Count > 0)
                        report.Add($"{name}: " + string.Join(" | ", problems));
                }
                finally
                {
                    stand.Dispose();
                }
            }
            TestContext.WriteLine($"turn stand execution: {ran} cases, {report.Count} with problems");
            Assert.That(report, Is.Empty, string.Join("\n", report));
        }
    }
#endif
}
#endif
