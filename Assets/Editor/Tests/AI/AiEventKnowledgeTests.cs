#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Hex Event knowledge boundary: the AI learns an event (and its guard) only from its OWN ground
    // army's Explore/Skip encounter (HexEventRegistry.MarkDiscovered), never from fog vision,
    // aviation or another player's encounter, and learns of a completion only by claiming it or by
    // re-observing an event it already knew.
    public class AiEventKnowledgeTests
    {
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly HexCoord Hex = new HexCoord(4, 2);

        private PlayerSetupData _a;
        private PlayerSetupData _b;
        private Dictionary<PlayerSetupData, HashSet<HexCoord>> _visible;

        [SetUp]
        public void SetUp()
        {
            HexEventRegistry.Clear();
            VisionSystem.Clear();
            AiMapMemory.Clear();
            AiMapMemory.EnsureSubscribed();
            _a = new PlayerSetupData { Nickname = "EventA" };
            _b = new PlayerSetupData { Nickname = "EventB" };
            _visible = (Dictionary<PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visible", Static).GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            HexEventRegistry.Clear();
            VisionSystem.Clear();
            AiMapMemory.Clear();
        }

        // ---- the original failure: visibility alone revealed the guard ---------------------------

        [Test]
        public void VisibleButNeverEncountered_EventAndGuardStayUnknown()
        {
            SetGuardedEvent(Hex);
            See(_a, Hex);

            AiMapMemory.RefreshVisibleForTest(_a);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Unknown));
            Assert.That(AiMapMemory.KnownEventGuardStrengthAt(_a, Hex), Is.Null);
            Assert.That(AiMapMemory.KnownEventGuardHexes(_a), Is.Empty);
            KnownSnapshot known = BuildKnown(_a);
            Assert.That(known.EventGuards, Is.Empty);
            Assert.That(known.ActiveEventHexes, Is.Empty);
        }

        [Test]
        public void FirstEncounter_RecordsGuard_AndBumpsKnowledgeOnce_WithoutAnyVisionChange()
        {
            SetGuardedEvent(Hex);
            int version = AiMapMemory.KnowledgeVersionFor(_a);
            long routes = AiMapMemory.RouteMemoryVersionFor(_a);

            HexEventRegistry.MarkDiscovered(Hex, _a);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));
            Assert.That(AiMapMemory.KnownEventGuardStrengthAt(_a, Hex), Is.Not.Null);
            Assert.That(AiMapMemory.KnowledgeVersionFor(_a), Is.GreaterThan(version));
            Assert.That(AiMapMemory.RouteMemoryVersionFor(_a), Is.GreaterThan(routes));

            int afterFirst = AiMapMemory.KnowledgeVersionFor(_a);
            HexEventRegistry.MarkDiscovered(Hex, _a);
            See(_a, Hex);
            AiMapMemory.RefreshVisibleForTest(_a);
            AiMapMemory.RefreshVisibleForTest(_a);

            Assert.That(AiMapMemory.KnowledgeVersionFor(_a), Is.EqualTo(afterFirst),
                "an unchanged known event must not keep invalidating the snapshot");
        }

        [Test]
        public void SkipByOnePlayer_DoesNotTeachTheOther_ButASecondOwnSkipDoes()
        {
            SetGuardedEvent(Hex);
            See(_b, Hex);

            HexEventRegistry.MarkSkipped(Hex, _a);
            AiMapMemory.RefreshVisibleForTest(_b);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));
            Assert.That(AiMapMemory.KnownEventStateAt(_b, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Unknown));
            Assert.That(AiMapMemory.KnownEventGuardHexes(_b), Is.Empty);

            // Skipped is already true globally; B's own encounter must still notify its memory.
            Assert.That(HexEventRegistry.FindAt(Hex).Skipped, Is.True);
            int version = AiMapMemory.KnowledgeVersionFor(_b);
            HexEventRegistry.MarkSkipped(Hex, _b);

            Assert.That(AiMapMemory.KnownEventStateAt(_b, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));
            Assert.That(AiMapMemory.KnownEventGuardStrengthAt(_b, Hex), Is.Not.Null);
            Assert.That(AiMapMemory.KnowledgeVersionFor(_b), Is.GreaterThan(version));
        }

        [Test]
        public void MarkerRule_MemoryDiscoveryAloneDoesNotRevealTheMapMarker()
        {
            SetGuardedEvent(Hex);

            HexEventRegistry.MarkDiscovered(Hex, _a); // Explore path: no Skip, no marker audience

            Assert.That(HexEventRegistry.IsDiscoveredBy(Hex, _a), Is.False);
            Assert.That(HexEventRegistry.IsKnownBy(Hex, _a), Is.True);
        }

        // ---- fog, completion ---------------------------------------------------------------------

        [Test]
        public void KnownEventLeavingVision_KeepsTheLastKnownGuard()
        {
            SetGuardedEvent(Hex);
            See(_a, Hex);
            HexEventRegistry.MarkDiscovered(Hex, _a);
            _visible[_a].Clear();

            AiMapMemory.RefreshVisibleForTest(_a);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));
            Assert.That(AiMapMemory.KnownEventGuardStrengthAt(_a, Hex), Is.Not.Null);
        }

        [Test]
        public void OtherPlayerCompletesOutOfSight_OurMemoryAndMissionStayUntilReObserved()
        {
            SetGuardedEvent(Hex);
            HexEventRegistry.MarkDiscovered(Hex, _a);
            HexEventRegistry.MarkDiscovered(Hex, _b);
            KnownSnapshot before = BuildKnown(_a);
            int version = AiMapMemory.KnowledgeVersionFor(_a);

            HexEventRegistry.MarkConsumed(Hex, _b);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));
            Assert.That(AiMapMemory.KnowledgeVersionFor(_a), Is.EqualTo(version));
            var target = RaidTargetRef.ForEventGuard(Hex);
            Assert.That(RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(_a, target), Is.False);
            Assert.That(RaidObjectiveEvaluator.EventTargetState(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));

            // Re-observing the hex confirms it: guard gone, completed, versions bumped, no repeat.
            See(_a, Hex);
            AiMapMemory.RefreshVisibleForTest(_a);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Completed));
            Assert.That(AiMapMemory.KnownEventGuardStrengthAt(_a, Hex), Is.Null);
            Assert.That(RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(_a, target), Is.True);
            int confirmed = AiMapMemory.KnowledgeVersionFor(_a);
            Assert.That(confirmed, Is.GreaterThan(version));
            AiMapMemory.RefreshVisibleForTest(_a);
            Assert.That(AiMapMemory.KnowledgeVersionFor(_a), Is.EqualTo(confirmed));

            Assert.That(before.EventGuards.Count, Is.EqualTo(1), "the snapshot built earlier is immutable");
        }

        [Test]
        public void OwnCompletion_IsKnownAtOnce_EvenWithoutVision()
        {
            SetGuardedEvent(Hex);
            HexEventRegistry.MarkDiscovered(Hex, _a);

            HexEventRegistry.MarkConsumed(Hex, _a);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Completed));
            Assert.That(AiMapMemory.KnownEventGuardHexes(_a), Is.Empty);
            Assert.That(RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(_a, RaidTargetRef.ForEventGuard(Hex)), Is.True);
        }

        [Test]
        public void CompletionNeverReachesAPlayerThatDidNotKnowTheEvent()
        {
            SetGuardedEvent(Hex);
            See(_a, Hex);
            HexEventRegistry.MarkDiscovered(Hex, _b);

            HexEventRegistry.MarkConsumed(Hex, _b);
            AiMapMemory.RefreshVisibleForTest(_a);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Unknown));
        }

        [Test]
        public void ArrivalAtAnEventAlreadyFinishedElsewhere_ConfirmsCompletion()
        {
            SetGuardedEvent(Hex);
            HexEventRegistry.MarkDiscovered(Hex, _a);
            HexEventRegistry.MarkConsumed(Hex, _b);
            See(_a, Hex); // the walker stands on the hex but no vision recompute reported it

            AiMapMemory.ObserveEventAt(_a, Hex);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Completed));
        }

        // ---- three states ------------------------------------------------------------------------

        [Test]
        public void UnguardedEvent_IsKnownActive_NotUnknownAndNotCompleted()
        {
            HexEventRegistry.Set(Hex, new EventDefinition { rewards = new List<RewardEntry>() },
                guardArmyName: null, resolvedGuardMembers: null, guardOwner: null, resolvedCardRewards: null);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Unknown));
            HexEventRegistry.MarkDiscovered(Hex, _a);

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));
            Assert.That(AiMapMemory.KnownEventGuardHexes(_a), Is.Empty, "no guard, still not 'completed'");
            See(_a, Hex);
            AiMapMemory.RefreshVisibleForTest(_a);
            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Active));
            Assert.That(BuildKnown(_a).ActiveEventHexes, Is.EqualTo(new[] { Hex }));
        }

        // ---- cache / snapshot / lifecycle --------------------------------------------------------

        [Test]
        public void ObservationDelta_SeesGuardRemoval_AndUnguardedStateChanges()
        {
            SetGuardedEvent(Hex);
            HexEventRegistry.MarkDiscovered(Hex, _a);
            var before = new WorldSnapshot { Known = BuildKnown(_a) };
            HexEventRegistry.MarkConsumed(Hex, _a);
            var after = new WorldSnapshot { Known = BuildKnown(_a) };

            var changed = (HashSet<HexCoord>)typeof(WorldAnalysis)
                .GetMethod("ChangedEventHexes", Static).Invoke(null, new object[] { before, after });
            Assert.That(changed, Is.EquivalentTo(new[] { Hex }));

            var same = (HashSet<HexCoord>)typeof(WorldAnalysis)
                .GetMethod("ChangedEventHexes", Static).Invoke(null, new object[] { after, after });
            Assert.That(same, Is.Empty);
        }

        [Test]
        public void RaidIntentValidity_FollowsSnapshotKnowledge_NotTheGlobalRegistry()
        {
            SetGuardedEvent(Hex);
            HexEventRegistry.MarkDiscovered(Hex, _a);
            var snap = new WorldSnapshot { Known = BuildKnown(_a) };
            var intent = new RaidIntent { Target = RaidTargetRef.ForEventGuard(Hex), OperationStarted = true };
            Assert.That(RaidObjectiveEvaluator.IsIntentStillValid(snap, intent), Is.True);

            HexEventRegistry.MarkConsumed(Hex, _b); // someone else, unobserved

            Assert.That(RaidObjectiveEvaluator.IsIntentStillValid(snap, intent), Is.True);
        }

        [Test]
        public void SessionReset_ClearsKnownEvents()
        {
            SetGuardedEvent(Hex);
            HexEventRegistry.MarkDiscovered(Hex, _a);

            AiMapMemory.Clear();

            Assert.That(AiMapMemory.KnownEventStateAt(_a, Hex), Is.EqualTo(AiMapMemory.KnownEventState.Unknown));
            Assert.That(AiMapMemory.KnownEventGuardHexes(_a), Is.Empty);
        }

        // ---- helpers -----------------------------------------------------------------------------

        private void See(PlayerSetupData player, HexCoord hex)
        {
            if (!_visible.TryGetValue(player, out HashSet<HexCoord> set))
                _visible[player] = set = new HashSet<HexCoord>();
            set.Add(hex);
        }

        private static void SetGuardedEvent(HexCoord hex)
        {
            var grunt = new CardDefinition { displayName = "Grunt", attack = 3, defenseRating = 2, hitPoints = 10 };
            HexEventRegistry.Set(hex, new EventDefinition { rewards = new List<RewardEntry>() },
                guardArmyName: "Guard", resolvedGuardMembers: new List<(CardDefinition, int)> { (grunt, 2) },
                guardOwner: new PlayerSetupData { Nickname = "Neutral", IsNeutral = true },
                resolvedCardRewards: null);
        }

        private static KnownSnapshot BuildKnown(PlayerSetupData player) =>
            (KnownSnapshot)typeof(WorldAnalysis).GetMethod("BuildKnown", Static)
                .Invoke(null, new object[] { player, System.Array.Empty<HexCoord>() });
    }
}
#endif
