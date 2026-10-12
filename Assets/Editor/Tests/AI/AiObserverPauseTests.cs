#if UNITY_INCLUDE_TESTS
using System.Collections;
using Game.Ai;
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
        public void ActionBoundary_WithoutConfiguredGate_CompletesImmediately()
        {
            var ctx = new AiTurnContext();

            Assert.That(ctx.WaitAtObserverActionBoundary().MoveNext(), Is.False);
        }
    }
}
#endif
