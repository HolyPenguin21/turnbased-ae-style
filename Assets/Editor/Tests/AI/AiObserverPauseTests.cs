#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using Game.Ai;
using Game.Turns;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiObserverPauseTests
    {
        [Test]
        public void ActionBoundary_WithConfiguredGate_InvokesGate()
        {
            bool entered = false;

            IEnumerator Gate()
            {
                entered = true;
                yield break;
            }

            var ctx = new AiTurnContext
            {
                ObserverActionBoundaryPause = Gate,
            };

            IEnumerator outer = ctx.WaitAtObserverActionBoundary();
            Assert.That(outer.MoveNext(), Is.True);
            Assert.That(entered, Is.False);

            IEnumerator inner = outer.Current as IEnumerator;
            Assert.That(inner, Is.Not.Null);
            while (inner.MoveNext()) { }

            Assert.That(entered, Is.True);
            Assert.That(outer.MoveNext(), Is.False);
        }

        [Test]
        public void OpenGameMenu_HoldsObserverMatchAtActionBoundaryUntilClosed()
        {
            var controller = (GameTurnController)FormatterServices.GetUninitializedObject(typeof(GameTurnController));
            typeof(GameTurnController).GetField("_aiObserverMatch", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, true);
            MethodInfo boundary = typeof(GameTurnController).GetMethod("PauseAtAiActionBoundary",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(((IEnumerator)boundary.Invoke(controller, null)).MoveNext(), Is.False);

            controller.SetAiObserverMenuHold(true);
            var held = (IEnumerator)boundary.Invoke(controller, null);
            Assert.That(held.MoveNext(), Is.True);
            Assert.That(held.MoveNext(), Is.True);
            Assert.That(controller.IsAiObserverPaused, Is.True);

            controller.SetAiObserverMenuHold(false);
            Assert.That(held.MoveNext(), Is.False);
            Assert.That(controller.IsAiObserverPaused, Is.False);
        }

        [Test]
        public void ActionBoundary_WithoutConfiguredGate_CompletesImmediately()
        {
            var ctx = new AiTurnContext();

            Assert.That(ctx.WaitAtObserverActionBoundary().MoveNext(), Is.False);
        }
    }
}
#endif
