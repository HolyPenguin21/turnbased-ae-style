#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  REACTION STAND: the whole AI turn (AiTurnController.RunTurn -> Pipeline.RunTurn) on a small
    //  map, built from the AI players' authored decks, with facts that arrive in the turn (a contact,
    //  a hand change) pending before the turn loop runs. Armies have no scene controller, so no move
    //  is executed: what runs is every decision and every card play of the turn, the Phase B rounds
    //  and the end of the turn.
    //  Every turn is checked against the turn's boundaries: no exception, AP never below zero, no
    //  reservation or claim invariant violation and no [ERROR] check line, no reservation left after
    //  the turn, no unit lost, the turn session closed. The state after the turn and the turn's control lines go to a
    //  digest (REACTION_STAND_OUT, default Logs/ReactionStand) compared between revisions.
    // ===========================================================================================
    public enum ReactionScenario
    {
        Quiet,           // own forces only
        Contact,         // a weak enemy army seen now, 3 hexes away
        Threat,          // a strong enemy army seen now, 2 hexes away
        PendingContact,  // a weak enemy army whose discovery is pending when the turn starts
        PendingHand,     // a hand opportunity pending when the turn starts
    }

    public sealed class ReactionStandTests
    {
        private const string CatalogPath = "Assets/Cards/StartingDeckCatalog.asset";
        private const int TurnNumber = 1;

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach ((string own, string enemy) in HousekeepingStand.DeckPairs)
                foreach (ReactionScenario s in Enum.GetValues(typeof(ReactionScenario)))
                    yield return new TestCaseData(own, enemy, s)
                        .SetName($"ReactionStand.{s}.{own.Replace(' ', '_')}_vs_{enemy.Replace(' ', '_')}");
        }

        private readonly List<string> _lines = new List<string>();
        private readonly List<Object> _objects = new List<Object>();

        private void Capture(string message, string stack, LogType type)
        {
            if (message != null && message.Contains("[AI]"))
                _lines.Add(message);
            if (type == LogType.Exception)
                _lines.Add("EXCEPTION " + message);
        }

        [SetUp]
        public void SetUp()
        {
            Game.Setup.MatchStateInventory.ResetForNewMatch();
            _lines.Clear();
            AiDebugLog.LogToUnityConsole = true;
            Application.logMessageReceived += Capture;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Capture;
            AiDebugLog.LogToUnityConsole = false;
            foreach (Object o in _objects)
                if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            Game.Setup.MatchStateInventory.ResetForNewMatch();
            Game.Core.GameSession.Players = new List<PlayerSetupData>();
        }

        [TestCaseSource(nameof(Cases))]
        public void Turn(string ownDeck, string enemyDeck, ReactionScenario scenario)
        {
            StartingDeckCatalog catalog = UnityEditor.AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>(CatalogPath);
            StartingDeck own = catalog.aiDecks.First(d => d != null && d.deckName == ownDeck);
            StartingDeck foeDeck = catalog.aiDecks.First(d => d != null && d.deckName == enemyDeck);
            UnityEngine.Random.InitState(ownDeck.GetHashCode() ^ (int)scenario * 7919);

            var mapObject = new GameObject("reaction stand map");
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

            var me = new PlayerSetupData { Nickname = "Stand", ColorIndex = 0, Faction = own.faction,
                AiDeckName = own.deckName, IsHuman = false, CitadelHexQ = 0, CitadelHexR = 0 };
            var foe = new PlayerSetupData { Nickname = "Foe", ColorIndex = 1, Faction = foeDeck.faction,
                AiDeckName = foeDeck.deckName, IsHuman = false, CitadelHexQ = 6, CitadelHexR = 0 };
            Game.Core.GameSession.Players = new List<PlayerSetupData> { me, foe };
            PlayerRoot root = Root(me);
            Root(foe);
            Citadel(catalog, me, new HexCoord(0, 0));
            Citadel(catalog, foe, new HexCoord(6, 0));
            AiMapMemory.EnsureSubscribed();

            List<CardDefinition> ownCards = Cards(catalog, own);
            List<CardDefinition> foeCards = Cards(catalog, foeDeck);
            List<CardDefinition> bodies = GroundBodies(ownCards, me);
            CardDefinition hero = ownCards.Where(c => c.cardType == CardType.Hero)
                .OrderByDescending(HeroRoleEvaluator.CombatLeadershipScore)
                .ThenBy(c => c.displayName, StringComparer.Ordinal).FirstOrDefault();
            if (bodies.Count == 0 || hero == null)
                Assert.Ignore($"{ownDeck}: no ground body or hero");

            Army(me, new HexCoord(0, 0), true, bodies[0]);
            Army(me, new HexCoord(1, 0), false, hero, bodies[bodies.Count / 2], bodies[bodies.Count - 1]);
            CardDefinition recce = ownCards.FirstOrDefault(c => c.cardType == CardType.Unit && !c.isAviation
                && AbilityParams.UnitHasAnyRecce(UnitData.FromDefinition(c, me)));
            if (recce != null)
                Army(me, new HexCoord(0, 1), false, recce);

            var ctx = new AiTurnContext
            {
                Map = map, StartingDeckCatalog = catalog, StartingHandSize = 5, HandCapacity = 10,
                TurnNumber = TurnNumber, StepDelay = 0f,
            };
            AiHandData hand = AiHandRegistry.GetOrCreate(me, catalog, ctx.StartingHandSize, ctx.HandCapacity);
            hand.SetCurrentTurn(TurnNumber);

            List<CardDefinition> foeBodies = GroundBodies(foeCards, foe);
            if (scenario != ReactionScenario.Quiet && scenario != ReactionScenario.PendingHand)
            {
                if (foeBodies.Count == 0)
                    Assert.Ignore($"{enemyDeck}: no ground body");
                ArmyData enemy = scenario == ReactionScenario.Threat
                    ? Army(foe, new HexCoord(3, 0), false,
                        Enumerable.Repeat(foeBodies[foeBodies.Count - 1], 4).ToArray())
                    : Army(foe, new HexCoord(4, 0), false, foeBodies[0], foeBodies[0]);
                AiMapMemory.ObserveForTest(me, enemy);
                if (scenario == ReactionScenario.PendingContact)
                    StrategicInterruptRegistry.MarkDiscovery(me, TurnNumber, new[] { enemy.Id });
            }
            if (scenario == ReactionScenario.PendingHand)
                StrategicInterruptRegistry.MarkHandOpportunity(me, TurnNumber);

            HashSet<UnitData> unitsBefore = OwnUnits(me);
            Exception failure = null;
            try { Drain(AiTurnController.RunTurn(me, ctx, null)); }
            catch (Exception e) { failure = e; }

            string digest = Digest(me, root, hand, scenario, ownDeck, enemyDeck, failure);
            string dir = Environment.GetEnvironmentVariable("REACTION_STAND_OUT");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "ReactionStand");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{scenario}.{ownDeck.Replace(' ', '_')}.txt"), digest);

            var problems = new List<string>();
            if (failure != null) problems.Add("exception: " + failure);
            if (root.ActionPoints < 0) problems.Add($"AP below zero: {root.ActionPoints}");
            problems.AddRange(_lines.Where(l => l.Contains("[AI][V2][INVARIANT]")
                || l.Contains("[Invariant] ERROR") || l.Contains("][ERROR]") || l.StartsWith("EXCEPTION")));
            if (StrategicResourceReservationLedger.HasAny(me, TurnNumber))
                problems.Add("a reservation survived the turn");
            if (AiTurnSession.PeekActive(me) != null)
                problems.Add("the turn session is still active");
            List<UnitData> lost = unitsBefore.Except(OwnUnits(me)).ToList();
            if (lost.Count > 0)
                problems.Add("units lost: " + string.Join(", ", lost.Select(u => u.Name)));
            Assert.That(problems, Is.Empty, digest);
        }

        // ---- world ------------------------------------------------------------------------------
        private PlayerRoot Root(PlayerSetupData player)
        {
            PlayerRoot root = PlayerRoot.Create(player, "reaction stand " + player.Nickname);
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

        private static ArmyData Army(PlayerSetupData owner, HexCoord hex, bool garrison, params CardDefinition[] cards)
        {
            var army = new ArmyData { Owner = owner, Hex = hex, IsGarrison = garrison };
            foreach (CardDefinition c in cards)
                army.AddMemberSorted(UnitData.FromDefinition(c, owner));
            ArmyRegistry.Register(army);
            return army;
        }

        private static HashSet<UnitData> OwnUnits(PlayerSetupData player) =>
            new HashSet<UnitData>(ArmyRegistry.AllForOwner(player).Where(a => a != null)
                .SelectMany(a => a.Members).Where(u => u != null));

        private static void Drain(IEnumerator routine)
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

        // ---- digest -----------------------------------------------------------------------------
        // The state after the turn and the turn's control lines; timings and trace ids are left out.
        private string Digest(PlayerSetupData me, PlayerRoot root, AiHandData hand, ReactionScenario scenario,
            string ownDeck, string enemyDeck, Exception failure)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"case {scenario} {ownDeck} vs {enemyDeck}");
            sb.AppendLine($"failure {(failure == null ? "-" : failure.GetType().Name + ": " + failure.Message)}");
            sb.AppendLine($"ap {root.ActionPoints} H{root.GetResource(ResourceType.Human)} "
                + $"E{root.GetResource(ResourceType.Energy)} M{root.GetResource(ResourceType.Materials)} "
                + $"T{root.GetResource(ResourceType.Tech)}");
            sb.AppendLine("hand " + string.Join(", ", hand.Hand.Select(c => c?.Definition?.displayName ?? "?")
                .OrderBy(n => n, StringComparer.Ordinal)));
            foreach (ArmyData a in ArmyRegistry.AllForOwner(me).Where(a => a != null).OrderBy(a => a.Id))
                sb.AppendLine($"army #{a.Id} ({a.Hex.Q},{a.Hex.R}){(a.IsGarrison ? " garrison" : "")} "
                    + $"act={(a.HasActivatedThisTurn ? 1 : 0)} ["
                    + string.Join(", ", a.Members.Where(u => u != null).Select(u => u.Name)
                        .OrderBy(n => n, StringComparer.Ordinal)) + "]");
            foreach (MissionIntent i in MissionIntentRegistry.GetOrCreate(me).All
                         .OrderBy(i => i.IntentKey.ToString(), StringComparer.Ordinal))
                sb.AppendLine($"intent {i.IntentKey} {i.Status} mover={i.PreferredMoverArmyId?.ToString() ?? "-"}");
            string[] markers =
            {
                "strat.B - reaction feasible", "pending invalidation but no FEASIBLE", "reaction - BEGIN",
                "reaction - END", "end-of-turn tempo re-run", "[AI][V2][INVARIANT]", "[Invariant] ERROR",
                "strat.B/tempo - END", "[AI][V2][Loop] step=", "admission pass opened",
                "][ERROR]", "end-of-turn facts",
            };
            foreach (string m in markers)
                sb.AppendLine($"lines '{m}' {_lines.Count(l => l.Contains(m))}");
            foreach (string l in _lines.Where(l => l.Contains("[INVARIANT]") || l.Contains("ERROR")
                         || l.StartsWith("EXCEPTION")))
                sb.AppendLine("! " + l);
            return sb.ToString();
        }
    }
}
#endif
