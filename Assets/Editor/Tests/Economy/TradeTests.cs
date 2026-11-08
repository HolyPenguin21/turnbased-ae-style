#if UNITY_INCLUDE_TESTS
using Game.Economy;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class TradeTests
    {
        [Test] public void ProgressivePriceAndPairsShareOneRate()
        {
            var steps = new[] { new TradeStep(ResourceType.Materials, ResourceType.Tech),
                new TradeStep(ResourceType.Human, ResourceType.Energy) };
            Assert.That(TradeActions.Simulate(new[] { 3, 0, 2, 0 }, 2, steps, out var after, out int rate), Is.True);
            Assert.That(after, Is.EqualTo(new[] { 0, 1, 0, 1 })); Assert.That(rate, Is.EqualTo(4));
        }
        [Test] public void ThreeUnitsCostNineAndInputIsUntouched()
        {
            var stock = new[] { 0, 0, 9, 0 };
            var step = new TradeStep(ResourceType.Materials, ResourceType.Tech);
            Assert.That(TradeActions.Price(2, 3), Is.EqualTo(9));
            Assert.That(TradeActions.Simulate(stock, 2, new[] {step, step, step}, out var after, out int rate), Is.True);
            Assert.That(stock[2], Is.EqualTo(9)); Assert.That(after[2], Is.Zero);
            Assert.That(after[3], Is.EqualTo(3)); Assert.That(rate, Is.EqualTo(5));
        }
        [Test] public void InsufficientFinalStepDoesNotReturnPartialState()
        {
            var stock = new[] { 0, 0, 8, 0 };
            var step = new TradeStep(ResourceType.Materials, ResourceType.Tech);
            Assert.That(TradeActions.Simulate(stock, 2, new[] {step,step,step}, out var after, out _), Is.False);
            Assert.That(after, Is.Null); Assert.That(stock[2], Is.EqualTo(8));
        }
        [Test] public void InvalidTypesAndOverflowAreRejected()
        {
            foreach (var step in new[] { new TradeStep(ResourceType.Tech, ResourceType.Tech),
                new TradeStep((ResourceType)99, ResourceType.Human) })
                Assert.That(TradeActions.Simulate(new[] {9,9,9,9}, 2, new[] {step}, out _, out _), Is.False);
            Assert.That(TradeActions.Simulate(new[] {int.MaxValue,0,0,0}, int.MaxValue,
                new[] {new TradeStep(ResourceType.Human,ResourceType.Tech)}, out _, out _), Is.False);
            Assert.That(TradeActions.Simulate(new[] {2,0,0,int.MaxValue}, 2,
                new[] {new TradeStep(ResourceType.Human,ResourceType.Tech)}, out _, out _), Is.False);
        }
    }
}
#endif
