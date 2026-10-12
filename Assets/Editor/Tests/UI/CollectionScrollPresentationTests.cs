#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class CollectionScrollPresentationTests
{
    private Scene scene;
    private bool closeScene;
    private GameObject root;
    private TMP_Dropdown dropdown;

    [SetUp]
    public void SetUp()
    {
        scene = SceneManager.GetSceneByPath("Assets/Scenes/MainMenu.unity");
        closeScene = !scene.isLoaded;
        if (closeScene) scene = EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity", OpenSceneMode.Additive);
        var collection = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Transform>(true))
            .First(t => t.name == "CollectionWindow");
        var source = collection.Find("Filters/Faction").GetComponent<TMP_Dropdown>();
        root = new GameObject("Dropdown test canvas", typeof(RectTransform), typeof(Canvas));
        root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        ((RectTransform)root.transform).sizeDelta = new Vector2(1280, 720);
        dropdown = Object.Instantiate(source, root.transform);
        dropdown.gameObject.SetActive(true);
        var rect = (RectTransform)dropdown.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, 1);
        rect.anchoredPosition = new Vector2(0, -40);
        dropdown.alphaFadeSpeed = 0;
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(root);
        if (closeScene) EditorSceneManager.CloseScene(scene, true);
    }

    private void Options(int count)
    {
        dropdown.ClearOptions();
        dropdown.AddOptions(Enumerable.Range(0, count).Select(i => "Deck " + i).ToList());
        CollectionUIElements.ConfigureDropdownScroll(dropdown);
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(6)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(25)]
    public void RealTmpPopupShowsTenCompleteRowsBeforeItNeedsScrolling(int count)
    {
        Options(count);
        dropdown.Show();
        Canvas.ForceUpdateCanvases();
        var popup = dropdown.transform.Find("Dropdown List").GetComponent<ScrollRect>();
        Assert.That(popup.content.rect.height, Is.EqualTo(count * 32 + 16).Within(.01f));
        Assert.That(popup.viewport.rect.height, Is.EqualTo(Mathf.Min(count, 10) * 32 + 16).Within(.01f));
        Assert.That(popup.vertical, Is.EqualTo(count > 10));
        Assert.That(popup.verticalScrollbar.gameObject.activeSelf, Is.EqualTo(count > 10));
        if (count <= 10)
        {
            var corners = new Vector3[4];
            foreach (var toggle in popup.content.GetComponentsInChildren<Toggle>())
            {
                ((RectTransform)toggle.transform).GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    float y = popup.viewport.InverseTransformPoint(corner).y;
                    Assert.That(y, Is.InRange(popup.viewport.rect.yMin - .01f, popup.viewport.rect.yMax + .01f));
                }
            }
        }
    }

    [Test]
    public void RemovingDecksHidesTrackAndRestoresFullTextWidthWithoutChangingSelectionOrListeners()
    {
        Options(11);
        dropdown.SetValueWithoutNotify(9);
        int callbacks = 0;
        dropdown.onValueChanged.AddListener(_ => callbacks++);
        var popup = dropdown.template.GetComponent<ScrollRect>();
        float narrowWidth = popup.viewport.rect.width;
        dropdown.options.RemoveAt(10);
        CollectionUIElements.ConfigureDropdownScroll(dropdown);
        Assert.That(popup.verticalScrollbar.gameObject.activeSelf, Is.False);
        Assert.That(popup.vertical, Is.False);
        Assert.That(popup.viewport.rect.width, Is.EqualTo(narrowWidth + 14).Within(.01f));
        Assert.That(dropdown.value, Is.EqualTo(9));
        Assert.That(callbacks, Is.Zero);
        dropdown.value = 8;
        Assert.That(callbacks, Is.EqualTo(1));
    }

    [Test]
    public void EveryAuthoredCollectionScrollbarHasSeparateSlicedTrackAndHandleSprites()
    {
        var collection = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Transform>(true))
            .First(t => t.name == "CollectionWindow");
        var bars = collection.GetComponentsInChildren<Scrollbar>(true);
        Assert.That(bars.Length, Is.EqualTo(6));
        foreach (var bar in bars)
        {
            var track = bar.GetComponent<Image>();
            var handle = bar.handleRect.GetComponent<Image>();
            Assert.That(track.sprite.name, Is.EqualTo("ScrollbarTrack"));
            Assert.That(handle.sprite.name, Is.EqualTo("ScrollbarHandle"));
            Assert.That(track.type, Is.EqualTo(Image.Type.Sliced));
            Assert.That(handle.type, Is.EqualTo(Image.Type.Sliced));
            Assert.That(bar.targetGraphic, Is.SameAs(handle));
        }
    }
}
#endif
