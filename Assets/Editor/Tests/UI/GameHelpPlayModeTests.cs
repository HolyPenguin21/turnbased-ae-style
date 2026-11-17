#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System.Collections;
using System.Linq;
using System.Reflection;
using Game.Players;
using Game.Turns;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // These checks need real layout, scene deserialization and deferred Destroy.
    [Category("Extended")]
    public sealed class GameHelpPlayModeTests
    {
        private GameObject canvasObject, turnObject;
        private GameMenuPanelUI menu;
        private GameHelpPanelUI view;
        private GameTurnController turn;
        private GameHelpCatalog catalog;
        private Sprite sprite;
        private CanvasGroup gameplayGroup;

        private static T Get<T>(object target, string field) =>
            (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
            turnObject = new GameObject("Help test turn controller");
            turnObject.SetActive(false);
            turn = turnObject.AddComponent<GameTurnController>();
            turn.enabled = false;
            turnObject.SetActive(true);
            Set(turn, "<CurrentPlayer>k__BackingField", new PlayerSetupData { IsHuman = true });

            canvasObject = new GameObject("Help test canvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(1280, 720);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var gameplay = new GameObject("Gameplay branch", typeof(RectTransform), typeof(CanvasGroup));
            gameplay.transform.SetParent(canvasObject.transform, false);
            gameplayGroup = gameplay.GetComponent<CanvasGroup>();

            var preview = EditorSceneManager.OpenPreviewScene("Assets/Scenes/Game.unity");
            try
            {
                var source = preview.GetRootGameObjects()
                    .SelectMany(r => r.GetComponentsInChildren<GameMenuPanelUI>(true)).Single();
                menu = Object.Instantiate(source, canvasObject.transform);
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
            view = Get<GameHelpPanelUI>(menu, "helpPanel");
            catalog = Object.Instantiate(Get<GameHelpCatalog>(view, "catalog"));
            Set(view, "catalog", catalog);
            menu.gameObject.SetActive(true);
            Set(menu, "turnController", turn);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (canvasObject != null) Object.Destroy(canvasObject);
            if (turnObject != null) Object.Destroy(turnObject);
            if (catalog != null) Object.Destroy(catalog);
            if (sprite != null) Object.Destroy(sprite);
            yield return null;
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator HelpBlocksGameplayAndRetainsTopicWithoutDuplicatingRows()
        {
            menu.OpenHelp();
            Assert.That(menu.IsShowing, Is.True);
            Assert.That(GameMenuPanelUI.GameplayInputBlocked, Is.True);
            Assert.That(gameplayGroup.interactable, Is.False);
            Assert.That(gameplayGroup.blocksRaycasts, Is.False);
            Assert.That(Get<GameObject>(menu, "blockingRoot").activeSelf, Is.False);
            Assert.That(view.SelectTopic("sandstorms"), Is.True);
            Assert.That(view.SelectTopic("unknown-topic"), Is.False);
            var list = Get<ScrollRect>(view, "topicsScroll");
            int rows = list.content.childCount;
            int closed = 0;
            view.Closed += () => closed++;
            Get<Button>(view, "closeButton").onClick.Invoke();
            Assert.That(menu.IsShowing, Is.False);
            Assert.That(gameplayGroup.interactable, Is.True);
            Assert.That(gameplayGroup.blocksRaycasts, Is.True);
            view.Close();
            Assert.That(closed, Is.EqualTo(1));
            menu.OpenHelp();
            Assert.That(view.SelectedTopicId, Is.EqualTo("sandstorms"));
            Assert.That(list.content.childCount, Is.EqualTo(rows));
            menu.OpenMenu();
            Assert.That(Get<GameObject>(menu, "blockingRoot").activeSelf, Is.False);

            // Disabling an open help root releases the same modal state as its close button.
            view.gameObject.SetActive(false);
            Assert.That(menu.IsShowing, Is.False);
            Assert.That(gameplayGroup.interactable, Is.True);
            yield return null;
            Assert.That(GameMenuPanelUI.GameplayInputBlocked, Is.False);

            // The shared owner must also release the existing observer menu hold.
            Set(turn, "_aiObserverMatch", true);
            menu.OpenHelp();
            Assert.That(Get<bool>(turn, "_aiObserverMenuHold"), Is.True);
            menu.ContinueGame();
            Assert.That(Get<bool>(turn, "_aiObserverMenuHold"), Is.False);
        }

        [UnityTest]
        public IEnumerator TranslationAndOrderedArticleBlocksReflowWithinTheRightViewport()
        {
            var topic = catalog.sections.SelectMany(s => s.topics).First(t => t.id == "sandstorms");
            sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.zero);
            topic.blocks = new[]
            {
                new HelpArticleBlock { id = "heading", kind = HelpBlockKind.Heading,
                    text = new HelpText { key = "test.heading", fallback = "Section heading" } },
                new HelpArticleBlock { id = "intro", text = new HelpText { key = "test.intro", fallback = "Fallback" } },
                new HelpArticleBlock { id = "figure", kind = HelpBlockKind.Image, image = sprite,
                    text = new HelpText { key = "test.caption", fallback = "Figure caption" } },
            };
            menu.OpenHelp();
            view.SelectTopic(topic.id);
            view.SetTextResolver(key => key == topic.title.key ? "Translated title"
                : key == "test.heading" ? "Translated heading"
                : key == "test.intro" ? string.Join("\n", Enumerable.Repeat("A long translated article paragraph.", 100)) : null);
            yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.That(view.SelectedTopicId, Is.EqualTo(topic.id));
            Assert.That(Get<TMP_Text>(view, "articleTitle").text, Is.EqualTo("Translated title"));
            var scroll = Get<ScrollRect>(view, "articleScroll");
            Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
            var activeBlocks = scroll.content.Cast<Transform>().Where(t => t.gameObject.activeSelf).ToArray();
            Assert.That(activeBlocks.Length, Is.EqualTo(5)); // Title, heading, paragraph, image, caption.
            Assert.That(activeBlocks[1].GetComponent<TMP_Text>().text, Is.EqualTo("Translated heading"));
            Assert.That(activeBlocks[1].GetComponent<TMP_Text>().fontStyle,
                Is.EqualTo(Get<TMP_Text>(view, "articleTitle").fontStyle));
            Assert.That(activeBlocks[3].GetComponent<Image>().sprite, Is.SameAs(sprite));
            Assert.That(activeBlocks[4].GetComponent<TMP_Text>().text, Is.EqualTo("Figure caption"));
            Assert.That(Mathf.Abs(scroll.content.anchoredPosition.y), Is.LessThan(1f));
        }
    }
}
#endif
