#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public class MatchGreetingTests
    {
        private GameObject _host;
        private GameTurnController _turns;
        private EventRewardPopupUI _popup;
        private TMP_Text _title, _summary;
        private Image _portrait, _logo;
        private Button _okButton, _endTurnButton;
        private PlayerSetupData _human;
        private List<PlayerSetupData> _previousPlayers;
        private StartingDeckCatalog _decks;

        [SetUp]
        public void SetUp()
        {
            _previousPlayers = GameSession.Players;
            _human = new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord };
            GameSession.Players = new List<PlayerSetupData> { _human,
                new PlayerSetupData { Faction = Faction.Vessels } };
            _decks = AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset");
            _host = new GameObject("Greeting test");
            _host.SetActive(false); // Avoid unrelated turn-controller OnEnable subscriptions.
            _turns = _host.AddComponent<GameTurnController>();
            var panel = Child("Popup");
            panel.SetActive(false);
            _popup = panel.AddComponent<EventRewardPopupUI>();
            _title = Child("Title", panel.transform).AddComponent<TextMeshProUGUI>();
            _title.text = "Reward";
            _summary = Child("Summary", panel.transform).AddComponent<TextMeshProUGUI>();
            _portrait = Child("Portrait", panel.transform).AddComponent<Image>();
            _logo = Child("Logo", panel.transform).AddComponent<Image>();
            _okButton = Child("OK", panel.transform).AddComponent<Button>();
            _endTurnButton = Child("End Turn").AddComponent<Button>();
            Set(_popup, "panelRoot", panel);
            Set(_popup, "titleText", _title);
            Set(_popup, "summaryText", _summary);
            Set(_popup, "unitImage", _portrait);
            Set(_popup, "rewardCardImage", _logo);
            Set(_popup, "okButton", _okButton);
            Set(_turns, "eventRewardPopup", _popup);
            Set(_turns, "endTurnButton", _endTurnButton);
            Set(_turns, "startingDeckCatalog", _decks);
            Set(_turns, "<CurrentPlayer>k__BackingField", _human);
            _popup.VisibilityChanged += () => Call(_turns, "RecomputeBlockedState");
        }

        [TearDown]
        public void TearDown()
        {
            GameSession.Players = _previousPlayers;
            Object.DestroyImmediate(_host);
        }

        [TestCase("IronConcord", Faction.IronConcord)]
        [TestCase("TheAshen", Faction.Ashen)]
        [TestCase("TheVessels", Faction.Vessels)]
        public void AuthoredFactions_HaveTwentyUniqueGreetingsAndSpeakerArt(string folder, Faction faction)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FactionCardCatalog>(
                $"Assets/Cards/{folder}/CardCatalog_{folder}.asset");
            Assert.That(catalog.faction, Is.EqualTo(faction));
            Assert.That(catalog.matchGreetings.Count, Is.EqualTo(20));
            Assert.That(catalog.matchGreetings.Distinct().Count(), Is.EqualTo(20));
            Assert.That(catalog.matchGreetings.All(m => !string.IsNullOrWhiteSpace(m)), Is.True);
            Assert.That(catalog.logo, Is.Not.Null);
            Assert.That(catalog.ForType(CardType.Hero).Any(c => c.detailArt != null || c.art != null), Is.True);
        }

        [Test]
        public void FirstConfirmation_WaitsForGreetingBeforeUnlockingAndDoesNotRepeat()
        {
            var randomBefore = Random.state;
            var flow = (IEnumerator)Call(_turns, "ResolveTurnStartThenConfirm", null, _human);
            try
            {
                Assert.That(flow.MoveNext(), Is.True);
                Assert.That(_popup.IsShowing, Is.True);
                Assert.That(_turns.TurnConfirmed, Is.False);
                Assert.That(_endTurnButton.interactable, Is.False);
                Call(_turns, "RecomputeBlockedState");
                Assert.That(_turns.InputBlocked, Is.True);
                Assert.That(_turns.CardDraggingBlocked, Is.True);
                var opponent = _decks.GetCatalog(Faction.Vessels);
                Assert.That(_title.text, Is.EqualTo("Message"));
                Assert.That(opponent.matchGreetings, Does.Contain(_summary.text));
                Assert.That(_logo.sprite, Is.SameAs(opponent.logo));
                Assert.That(opponent.ForType(CardType.Hero).Any(c =>
                    _portrait.sprite == (c.detailArt != null ? c.detailArt : c.art)), Is.True);
                Assert.That(Random.state, Is.EqualTo(randomBefore), "Cosmetic choices must not change gameplay rolls.");
                _okButton.onClick.Invoke();
                Assert.That(_popup.IsShowing, Is.True, "The confirming keypress cannot dismiss the new popup.");
                Set(_popup, "_shownFrame", -1);
                _okButton.onClick.Invoke();
                Assert.That(flow.MoveNext(), Is.False);
                Assert.That(_turns.TurnConfirmed, Is.True);
                Assert.That(_turns.InputBlocked, Is.False);
                Assert.That(_endTurnButton.interactable, Is.True);
                Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.False);
            }
            finally { (flow as System.IDisposable)?.Dispose(); }
        }

        [Test]
        public void DuplicateConfirmation_IsIgnoredWhilePendingAndAfterCompletion()
        {
            Set(_turns, "_turnConfirmationInProgress", true);
            Call(_turns, "OnTurnConfirmed");
            Assert.That(_popup.IsShowing, Is.False);
            Set(_turns, "_turnConfirmationInProgress", false);
            Set(_turns, "<TurnConfirmed>k__BackingField", true);
            Call(_turns, "OnTurnConfirmed");
            Assert.That(_popup.IsShowing, Is.False);
        }

        [Test]
        public void LaterPersonalTurn_DoesNotShowGreeting()
        {
            var completed = (Dictionary<PlayerSetupData, long>)typeof(GameTurnController)
                .GetField("_completedTurns", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_turns);
            completed[_human] = 1;
            Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.False);
        }

        [Test]
        public void AiAndMissingActiveOpponents_DoNotShowGreeting()
        {
            Assert.That(Call(_turns, "TryShowMatchGreeting", GameSession.Players[1]), Is.False);
            GameSession.Players[1].IsEliminated = true;
            GameSession.Players.Add(new PlayerSetupData { IsNeutral = true });
            GameSession.Players.Add(null);
            Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.False);
        }

        [Test]
        public void MirrorMatch_UsesOtherPlayerEvenWhenFactionMatches()
        {
            GameSession.Players[1].Faction = _human.Faction;
            Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.True);
            Assert.That(_logo.sprite, Is.SameAs(_decks.GetCatalog(_human.Faction).logo));
        }

        [Test]
        public void PendingTurnNotice_WaitsUntilGreetingCloses()
        {
            var panel = Child("Turn notice");
            panel.SetActive(false);
            var notice = panel.AddComponent<PopupPanelUI>();
            Set(notice, "panelRoot", panel);
            var text = Child("Notice text", panel.transform).AddComponent<TextMeshProUGUI>();
            Set(notice, "infoText", text);
            Set(_turns, "popupPanel", notice);
            notice.VisibilityChanged += () => Call(_turns, "RecomputeBlockedState");
            var queue = (Queue<string>)typeof(GameTurnController)
                .GetField("_aviationMessageQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_turns);
            var flow = (IEnumerator)Call(_turns, "ResolveTurnStartThenConfirm", null, _human);
            Assert.That(flow.MoveNext(), Is.True);
            queue.Enqueue("Detection report");
            queue.Enqueue("Aircraft report");
            Call(_turns, "ShowNextAviationMessage");
            Assert.That(notice.IsShowing, Is.False);
            Assert.That(queue.Count, Is.EqualTo(2));
            _popup.Hide();
            Assert.That(notice.IsShowing, Is.False, "Reports must wait for the complete confirmation sequence.");
            Assert.That(flow.MoveNext(), Is.False);
            Assert.That(notice.IsShowing, Is.True);
            Assert.That(text.text, Is.EqualTo("Detection report"), "CompleteTurnConfirmation must not hide the first report.");
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(_turns.TurnConfirmed, Is.True);
            Assert.That(_turns.InputBlocked, Is.True);
            Assert.That(_endTurnButton.interactable, Is.False);
            notice.Hide();
            Assert.That(text.text, Is.EqualTo("Aircraft report"));
            Assert.That(queue, Is.Empty);
            notice.Hide();
            Assert.That(_turns.InputBlocked, Is.False);
            Assert.That(_endTurnButton.interactable, Is.True);
        }

        [Test]
        public void ProgrammaticHide_CanCloseGreetingImmediately()
        {
            Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.True);
            _popup.Hide();
            Assert.That(_popup.IsShowing, Is.False);
            Assert.That(_turns.InputBlocked, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void InterruptedConfirmation_ClosesGreetingWithoutUnlocking(bool gameOver)
        {
            Set(_turns, "_turnConfirmationInProgress", true);
            var flow = (IEnumerator)Call(_turns, "ResolveTurnStartThenConfirm", null, _human);
            Assert.That(flow.MoveNext(), Is.True);
            if (gameOver)
                Set(_turns, "_gameOver", true);
            else
                Set(_turns, "<CurrentPlayer>k__BackingField", GameSession.Players[1]);
            Assert.That(flow.MoveNext(), Is.False);
            Assert.That(_popup.IsShowing, Is.False);
            Assert.That(_turns.TurnConfirmed, Is.False);
            Assert.That(_endTurnButton.interactable, Is.False);
            Assert.That(typeof(GameTurnController).GetField("_turnConfirmationInProgress",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_turns), Is.False);
        }

        [Test]
        public void IncompleteOpponentCatalog_DoesNotSuppressValidOpponentGreeting()
        {
            var decks = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            var incomplete = ScriptableObject.CreateInstance<FactionCardCatalog>();
            try
            {
                incomplete.faction = Faction.Ashen;
                incomplete.logo = _decks.GetCatalog(Faction.Ashen).logo;
                incomplete.cards = null;
                decks.catalogs.Add(incomplete);
                decks.catalogs.Add(_decks.GetCatalog(Faction.Vessels));
                Set(_turns, "startingDeckCatalog", decks);
                GameSession.Players.Add(new PlayerSetupData { Faction = Faction.Ashen });
                // Select the incomplete second opponent first, independent of the runtime's
                // seeded Random sequence. The previous one-shot lookup must fail this case.
                int seed = 0;
                while (new System.Random(seed).Next(2) != 1) seed++;
                Set(_turns, "_greetingRandom", new System.Random(seed));
                Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.True);
                Assert.That(_logo.sprite, Is.SameAs(_decks.GetCatalog(Faction.Vessels).logo));
            }
            finally
            {
                Object.DestroyImmediate(incomplete);
                Object.DestroyImmediate(decks);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RewardAfterMessage_RestoresTitleArtModeAndRewardSummary(bool authoredPreserveAspect)
        {
            _logo.preserveAspect = authoredPreserveAspect;
            Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.True);
            Assert.That(_logo.preserveAspect, Is.True);
            _popup.Show(new HexEventRegistry.Entry { Definition = new EventDefinition() }, null);
            Assert.That(_title.text, Is.EqualTo("Reward"));
            Assert.That(_summary.text, Is.EqualTo("The army finds nothing of value here."));
            Assert.That(_logo.sprite, Is.Null);
            Assert.That(_logo.gameObject.activeSelf, Is.False);
            Assert.That(_logo.preserveAspect, Is.EqualTo(authoredPreserveAspect));
            _popup.Hide();
            Assert.That(_popup.IsShowing, Is.False);
        }

        [TestCase("reward")]
        [TestCase("exhausted")]
        [TestCase("error")]
        public void LibraryAfterMessage_UsesLibraryResultAndReplacesFactionArt(string result)
        {
            Assert.That(Call(_turns, "TryShowMatchGreeting", _human), Is.True);
            var entry = new HexEventRegistry.Entry
            {
                Definition = new EventDefinition { grantsRandomCollectionCard = true }
            };
            var card = _decks.GetCatalog(Faction.IronConcord).ForType(CardType.Hero)
                .First(c => c.detailArt != null || c.art != null);
            if (result == "reward")
                entry.ResolvedCardRewards.Add((new RewardEntry(), card));
            else if (result == "error")
                entry.CollectionRewardError = "Unable to save test profile.";
            _popup.Show(entry, null);
            Assert.That(_title.text, Is.EqualTo("Reward"));
            Assert.That(_portrait.gameObject.activeSelf, Is.False);
            if (result == "reward")
            {
                Assert.That(_summary.text, Does.Contain(card.displayName).And.Contain("COLLECTION"));
                Assert.That(_logo.sprite, Is.SameAs(card.detailArt != null ? card.detailArt : card.art));
            }
            else
            {
                Assert.That(_logo.sprite, Is.Null);
                Assert.That(_logo.gameObject.activeSelf, Is.False);
                if (result == "error")
                    Assert.That(_summary.text, Is.EqualTo(entry.CollectionRewardError));
                else
                    Assert.That(_summary.text, Does.Contain("maximum number of copies"));
            }
        }

        private GameObject Child(string name, Transform parent = null)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent != null ? parent : _host.transform, false);
            return child;
        }
        private static void Set(object obj, string field, object value) => obj.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static object Call(object obj, string method, params object[] args) => obj.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
    }
}
#endif
