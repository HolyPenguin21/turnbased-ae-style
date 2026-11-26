#if UNITY_INCLUDE_TESTS
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.EditorTests
{
    public sealed class ModalFocusStackTests
    {
        [Test]
        public void NestedWindowOwnsFocusAndClosingRestoresTheSelectedControl()
        {
            var previousEvents = EventSystem.current;
            var fixture = new GameObject("Modal focus fixture");
            var events = fixture.AddComponent<EventSystem>();
            EventSystem.current = events;
            var opener = ButtonUnder(fixture.transform, "Open");
            var window = new GameObject("Window", typeof(RectTransform));
            window.transform.SetParent(fixture.transform, false);
            var parentFocus = ButtonUnder(window.transform, "Default");
            var nestedOpener = ButtonUnder(window.transform, "Nested opener");
            var childWindow = new GameObject("Nested window", typeof(RectTransform));
            childWindow.transform.SetParent(window.transform, false);
            var childFocus = ButtonUnder(childWindow.transform, "Close");
            var owner = window.AddComponent<CanvasScaler>();

            try
            {
                events.SetSelectedGameObject(opener.gameObject);
                UIFocusUtility.SetModal(owner, window, ModalBlock.Viewer, parentFocus);
                Assert.That(events.currentSelectedGameObject, Is.SameAs(parentFocus.gameObject));
                Assert.That(UIFocusUtility.Blocks(ModalBlock.Map), Is.True);
                Assert.That(UIFocusUtility.Blocks(ModalBlock.Hand), Is.False);

                events.SetSelectedGameObject(nestedOpener.gameObject);
                uint generation = UIFocusUtility.InputGeneration;
                UIFocusUtility.SetNestedModal(owner, childWindow, childFocus);
                Assert.That(UIFocusUtility.InputGeneration, Is.Not.EqualTo(generation));
                Assert.That(events.currentSelectedGameObject, Is.SameAs(childFocus.gameObject));
                Assert.That(nestedOpener.IsInteractable(), Is.False);
                Assert.That(UIFocusUtility.Blocks(ModalBlock.Hand), Is.True);

                UIFocusUtility.SetNestedModal(owner, null);
                Assert.That(nestedOpener.IsInteractable(), Is.True);
                Assert.That(events.currentSelectedGameObject, Is.SameAs(nestedOpener.gameObject));
                Assert.That(UIFocusUtility.CanHandleInput(owner), Is.False, "A close cannot fall through in the same frame.");

                UIFocusUtility.SetModal(owner, null);
                Assert.That(events.currentSelectedGameObject, Is.SameAs(opener.gameObject));
            }
            finally
            {
                UIFocusUtility.SetNestedModal(owner, null);
                UIFocusUtility.SetModal(owner, null);
                Object.DestroyImmediate(fixture);
                if (previousEvents != null) EventSystem.current = previousEvents;
            }
        }

        private static Button ButtonUnder(Transform parent, string name)
        {
            var button = new GameObject(name, typeof(RectTransform), typeof(Button)).GetComponent<Button>();
            button.transform.SetParent(parent, false);
            return button;
        }
    }
}
#endif
