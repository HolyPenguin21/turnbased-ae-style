#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Ai;
using Game.Cards;
using Game.Core;
using Game.Players;
using Game.Progression;
using Game.Saves;
using Game.Turns;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Hotseat / MP preparation (Docs/Hotseat_MP_Prep_Spec.md): pure model rules, no engine.
    public sealed class HotseatModelTests
    {
        private List<PlayerSetupData> previousPlayers;

        [SetUp] public void SetUp() { previousPlayers = GameSession.Players; LocalSeat.Reset(); AiHandRegistry.Clear(); }
        [TearDown] public void TearDown() { GameSession.Players = previousPlayers; LocalSeat.Reset(); AiHandRegistry.Clear(); }

        private static PlayerSetupData Human(string id, Faction faction = Faction.IronConcord)
            => new PlayerSetupData { PlayerId = id, Nickname = id, IsHuman = true, Faction = faction };
        private static PlayerSetupData Ai(string id, Faction faction = Faction.Ashen)
            => new PlayerSetupData { PlayerId = id, Nickname = id, Faction = faction };

        // --- PlayerControl -------------------------------------------------------------

        [Test] public void IsHumanIsDerivedFromControlAndSetterKeepsOldMeaning()
        {
            var p = new PlayerSetupData();
            Assert.That(p.Control, Is.EqualTo(PlayerControl.Ai));
            Assert.That(p.IsHuman, Is.False);
            p.IsHuman = true;
            Assert.That(p.Control, Is.EqualTo(PlayerControl.LocalHuman));
            p.Control = PlayerControl.RemoteHuman;
            Assert.That(p.IsHuman, Is.True, "A remote person is still a human for every game rule.");
            Assert.That(p.IsLocalHuman, Is.False);
            Assert.That(p.CopySetupDraft().Control, Is.EqualTo(PlayerControl.RemoteHuman));
            p.IsHuman = false;
            Assert.That(p.Control, Is.EqualTo(PlayerControl.Ai));
        }

        // --- LocalSeat -----------------------------------------------------------------

        [Test] public void OneHumanIsSeatedImplicitly()
        {
            var h = Human("h");
            GameSession.Players = new List<PlayerSetupData> { Ai("a"), h };
            Assert.That(LocalSeat.Current, Is.SameAs(h));
            Assert.That(LocalSeat.IsManaged, Is.False);
            Assert.That(LocalSeat.IsHotseat(GameSession.Players), Is.False);
        }

        [Test] public void TwoHumansWithoutAnExplicitSeatShowNobody()
        {
            GameSession.Players = new List<PlayerSetupData> { Human("a"), Human("b"), Ai("c") };
            Assert.That(LocalSeat.Current, Is.Null, "No implicit first human in hotseat: fail closed.");
            Assert.That(LocalSeat.IsHotseat(GameSession.Players), Is.True);
        }

        [Test] public void ExplicitSeatRaisesChangedAndRemembersLast()
        {
            var a = Human("a"); var b = Human("b");
            GameSession.Players = new List<PlayerSetupData> { a, b };
            int changes = 0;
            Action onChanged = () => changes++;
            LocalSeat.Changed += onChanged;
            try
            {
                LocalSeat.Set(a);
                LocalSeat.Set(a);
                LocalSeat.Set(null);
                Assert.That(LocalSeat.Current, Is.Null);
                Assert.That(LocalSeat.Last, Is.SameAs(a));
                LocalSeat.Set(b);
                Assert.That(LocalSeat.Is(b), Is.True);
                Assert.That(changes, Is.EqualTo(3));
                Assert.Throws<ArgumentException>(() => LocalSeat.Set(Ai("x")));
                LocalSeat.Reset();
                Assert.That(LocalSeat.IsManaged, Is.False);
                Assert.That(LocalSeat.Last, Is.Null);
            }
            finally { LocalSeat.Changed -= onChanged; }
        }

        // --- PlayerDecisionRouter ------------------------------------------------------

        [Test] public void RouterRoutesByControlAndSeat()
        {
            var a = Human("a"); var b = Human("b"); var ai = Ai("c");
            Assert.That(PlayerDecisionRouter.Route(ai, a), Is.EqualTo(DecisionRoute.Ai));
            Assert.That(PlayerDecisionRouter.Route(null, a), Is.EqualTo(DecisionRoute.Ai));
            Assert.That(PlayerDecisionRouter.Route(a, a), Is.EqualTo(DecisionRoute.Immediate));
            Assert.That(PlayerDecisionRouter.Route(b, a), Is.EqualTo(DecisionRoute.Handoff));
            Assert.That(PlayerDecisionRouter.Route(b, null), Is.EqualTo(DecisionRoute.Handoff), "Curtain up: the owner must still take the screen.");
            var remote = Human("r"); remote.Control = PlayerControl.RemoteHuman;
            Assert.Throws<NotSupportedException>(() => PlayerDecisionRouter.Route(remote, a));
        }

        [Test] public void EncounterOfAnotherHumanIsHandedOverButAnOpenHumanFightIsNot()
        {
            var a = Human("a"); var b = Human("b"); var ai = Ai("c");
            Assert.That(PlayerDecisionRouter.EncounterHandoffTarget(new[] { ai, b }, a), Is.SameAs(b), "AI attacks b while a holds the screen.");
            Assert.That(PlayerDecisionRouter.EncounterHandoffTarget(new[] { a, b }, a), Is.Null, "D3: human-vs-human with the seat is open.");
            Assert.That(PlayerDecisionRouter.EncounterHandoffTarget(new[] { b, a }, a), Is.Null);
            Assert.That(PlayerDecisionRouter.EncounterHandoffTarget(new[] { ai, Ai("d") }, a), Is.Null);
            Assert.That(PlayerDecisionRouter.EncounterHandoffTarget(new[] { ai, a }, a), Is.Null, "Single player: the human is the seat.");
            var remote = Human("r"); remote.Control = PlayerControl.RemoteHuman;
            Assert.Throws<NotSupportedException>(() => PlayerDecisionRouter.EncounterHandoffTarget(new[] { ai, remote }, a));
        }

        [Test] public void SinglePlayerDecisionIsImmediateThroughTheImplicitSeat()
        {
            var h = Human("h");
            GameSession.Players = new List<PlayerSetupData> { h, Ai("a") };
            Assert.That(PlayerDecisionRouter.Route(h), Is.EqualTo(DecisionRoute.Immediate));
        }

        // --- MatchAccountOutcome (owner decision D2) -----------------------------------

        [Test] public void SingleHumanEliminationIsAnImmediateDefeat()
        {
            var h = Human("h", Faction.IronConcord); var ai = Ai("a");
            var rule = new MatchAccountOutcome();
            h.IsEliminated = true;
            var result = rule.OnEliminated("m", h, new[] { h, ai });
            Assert.That(result.HasValue, Is.True);
            Assert.That(result.Value.Outcome, Is.EqualTo(MatchOutcome.Defeat));
            Assert.That(result.Value.Faction, Is.EqualTo(Faction.IronConcord));
            Assert.That(rule.OnMatchFinished("m", new[] { ai }).HasValue, Is.False, "An AI win after the defeat publishes nothing.");
        }

        [Test] public void FirstOfTwoHumansFallingPublishesNothingTheLastIsTheDefeat()
        {
            var a = Human("a", Faction.IronConcord); var b = Human("b", Faction.Ashen); var ai = Ai("c");
            var all = new[] { a, b, ai };
            var rule = new MatchAccountOutcome();
            a.IsEliminated = true;
            Assert.That(rule.OnEliminated("m", a, all).HasValue, Is.False, "The match must go on for the other human.");
            b.IsEliminated = true;
            var defeat = rule.OnEliminated("m", b, all);
            Assert.That(defeat.Value.Outcome, Is.EqualTo(MatchOutcome.Defeat));
            Assert.That(defeat.Value.Player, Is.SameAs(b));
            Assert.That(defeat.Value.Faction, Is.EqualTo(Faction.Ashen), "Defeat uses the last fallen human's faction.");
            Assert.That(rule.OnMatchFinished("m", new[] { ai }).HasValue, Is.False);
        }

        [Test] public void HumanSoleSurvivorIsTheVictoryEvenAfterAnotherHumanFell()
        {
            var a = Human("a", Faction.IronConcord); var b = Human("b", Faction.Ashen);
            var rule = new MatchAccountOutcome();
            a.IsEliminated = true;
            Assert.That(rule.OnEliminated("m", a, new[] { a, b }).HasValue, Is.False);
            var win = rule.OnMatchFinished("m", new[] { b });
            Assert.That(win.Value.Outcome, Is.EqualTo(MatchOutcome.Victory));
            Assert.That(win.Value.Faction, Is.EqualTo(Faction.Ashen));
            Assert.That(rule.OnMatchFinished("m", new[] { b }).HasValue, Is.False, "One reward per match.");
        }

        [Test] public void AiEliminationAndDrawPublishNothing()
        {
            var h = Human("h"); var ai = Ai("a");
            var rule = new MatchAccountOutcome();
            ai.IsEliminated = true;
            Assert.That(rule.OnEliminated("m", ai, new[] { h, ai }).HasValue, Is.False);
            Assert.That(rule.OnMatchFinished("m", Array.Empty<PlayerSetupData>()).HasValue, Is.False);
            Assert.That(rule.OnMatchFinished("m", new[] { ai }).HasValue, Is.False);
        }

        // --- Saved turn: any living human ----------------------------------------------

        private static GameTurnState Turn(string[] order, int index, params string[] ids)
        {
            var completed = new List<PlayerTurnCount>();
            foreach (var id in ids) completed.Add(new PlayerTurnCount { PlayerId = id, CompletedTurns = 1 });
            return new GameTurnState { Round = 2, PlayerOrder = order, CurrentPlayerIndex = index, Completed = completed.ToArray(),
                GreetingsShown = Array.Empty<string>(), PendingMessages = Array.Empty<PlayerTurnMessages>() };
        }

        [Test] public void SavedTurnMayBelongToTheSecondHuman()
        {
            var a = Human("a"); var b = Human("b"); var c = Ai("c");
            var state = Turn(new[] { "a", "c", "b" }, 2, "a", "b", "c");
            Assert.That(state.ValidateHumanTurn(new[] { a, b, c }), Is.SameAs(b));
        }

        [Test] public void SavedTurnOfAnAiOrAnEliminatedHumanIsRejected()
        {
            var a = Human("a"); var b = Human("b"); var c = Ai("c");
            Assert.Throws<ArgumentException>(() => Turn(new[] { "a", "c", "b" }, 1, "a", "b", "c").ValidateHumanTurn(new[] { a, b, c }));
            b.IsEliminated = true;
            Assert.Throws<ArgumentException>(() => Turn(new[] { "a", "c", "b" }, 2, "a", "b", "c").ValidateHumanTurn(new[] { a, b, c }));
            Assert.Throws<ArgumentException>(() => Turn(new[] { "a", "c", "b" }, 7, "a", "b", "c").ValidateHumanTurn(new[] { a, b, c }));
        }

        [Test] public void SlotSummaryNamesTheCurrentHumanAndMarksHotseat()
        {
            var a = Human("Anna"); var b = Human("Boris");
            Assert.That(MatchSaveCodec.SlotSummary(4, b, new[] { a, b, Ai("c") }), Is.EqualTo("Round 4 · Boris · Hotseat"));
            Assert.That(MatchSaveCodec.SlotSummary(4, a, new[] { a, Ai("c") }), Is.EqualTo("Round 4 · Anna"));
        }

        // --- Hands by owner (R4) -------------------------------------------------------

        [Test] public void MatchHandsReadTheOwnersHandNeverAnotherOne()
        {
            var a = Human("a"); var b = Human("b"); var ai = Ai("c");
            a.Hand = new PlayerHandData(null, 0); b.Hand = new PlayerHandData(null, 0);
            Assert.That(MatchHands.Peek(b), Is.SameAs(b.Hand));
            Assert.That(MatchHands.Peek(a), Is.SameAs(a.Hand));
            Assert.That(MatchHands.Peek(ai), Is.Null, "Peek never deals an AI hand.");
            var aiHand = new AiHandData(null, Faction.None, 0);
            AiHandRegistry.RegisterRestored(ai, aiHand);
            Assert.That(MatchHands.Peek(ai), Is.SameAs(aiHand));
            Assert.That(MatchHands.GetOrCreate(b, null, 0), Is.SameAs(b.Hand), "A human hand is never created here.");
        }
    }
}
#endif
