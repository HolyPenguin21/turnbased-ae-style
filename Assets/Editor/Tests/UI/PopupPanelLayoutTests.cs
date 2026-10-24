#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.EditorTests
{
    public class PopupPanelLayoutTests
    {
        private GameObject root;
        private PopupPanelUI popup;
        private RectTransform panel, textRect, buttonRect;
        private TextMeshProUGUI text;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("popup", typeof(RectTransform));
            panel = (RectTransform)root.transform;
            panel.sizeDelta = new Vector2(431f, 145f);
            popup = root.AddComponent<PopupPanelUI>();
            textRect = (RectTransform)new GameObject("text", typeof(RectTransform)).transform;
            textRect.SetParent(panel, false);
            textRect.anchorMin = new Vector2(0.05f, 0.34f);
            textRect.anchorMax = new Vector2(0.95f, 0.59f);
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.Top;
            buttonRect = (RectTransform)new GameObject("button", typeof(RectTransform)).transform;
            buttonRect.SetParent(panel, false);
            buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0.5f, 0f);
            buttonRect.pivot = new Vector2(0.5f, 0f);
            buttonRect.sizeDelta = new Vector2(140f, 30f);
            buttonRect.anchoredPosition = new Vector2(0f, 20f);
            Set("panelRoot", root);
            Set("infoText", text);
            Set("confirmButton", buttonRect.gameObject.AddComponent<Button>());
            root.SetActive(false);
        }

        private void Set(string field, object value) => typeof(PopupPanelUI)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(popup, value);

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void OtherTurnStretchesToInspectorAnchorsAndRestoresAuthoredLayout()
        {
            Vector2 min = textRect.anchorMin, max = textRect.anchorMax;
            Vector2 offsetMin = textRect.offsetMin, offsetMax = textRect.offsetMax;
            Set("noButtonTextAnchorMin", new Vector2(0.1f, 0.2f));
            Set("noButtonTextAnchorMax", new Vector2(0.9f, 0.7f));
            popup.ShowForOther(null);
            Assert.That(buttonRect.gameObject.activeSelf, Is.False);
            Assert.That(text.verticalAlignment, Is.EqualTo(VerticalAlignmentOptions.Middle));
            AssertVector(textRect.anchorMin, new Vector2(0.1f, 0.2f));
            AssertVector(textRect.anchorMax, new Vector2(0.9f, 0.7f));
            AssertVector(textRect.offsetMin, Vector2.zero);
            AssertVector(textRect.offsetMax, Vector2.zero);

            popup.ShowForHuman(null, null);
            AssertAuthored(min, max, offsetMin, offsetMax);
            popup.ShowForOther(null);
            popup.ShowHint("A message that needs confirmation.");
            AssertAuthored(min, max, offsetMin, offsetMax);
        }

        [Test]
        public void OtherTurnAreaFollowsPanelResize()
        {
            Set("noButtonTextAnchorMin", new Vector2(0.1f, 0.2f));
            Set("noButtonTextAnchorMax", new Vector2(0.9f, 0.7f));
            popup.ShowForOther(null);
            panel.sizeDelta = new Vector2(500f, 200f);
            popup.Hide();
            popup.ShowForOther(null);
            Assert.That(textRect.rect.height, Is.EqualTo(200f * 0.5f).Within(0.001f));
            Assert.That(textRect.rect.width, Is.EqualTo(500f * 0.8f).Within(0.001f));
        }

        private void AssertAuthored(Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            Assert.That(buttonRect.gameObject.activeSelf, Is.True);
            AssertVector(textRect.anchorMin, min);
            AssertVector(textRect.anchorMax, max);
            AssertVector(textRect.offsetMin, offsetMin);
            AssertVector(textRect.offsetMax, offsetMax);
            Assert.That(text.verticalAlignment, Is.EqualTo(VerticalAlignmentOptions.Top));
        }

        private static void AssertVector(Vector2 actual, Vector2 expected) =>
            Assert.That(Vector2.Distance(actual, expected), Is.LessThan(0.001f));
    }
}
#endif
