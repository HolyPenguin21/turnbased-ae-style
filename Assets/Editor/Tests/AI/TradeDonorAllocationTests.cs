#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class TradeDonorAllocationTests
    {
        [TestCase(3, 2, 0, 2)]
        [TestCase(4, 5, 0, 3)]
        [TestCase(6, 5, 0, 3)]
        public void PreservesLargeDonorForLaterRates(int a, int b, int c, int count)
        {
            var capacities = new[] { a, b, c, 0 };
            Assert.That(TradeFunding.TryAllocateDonors(capacities, 2, count, out var allocation), Is.True);
            var remaining = (int[])capacities.Clone();
            for (int i = 0; i < count; i++)
            { remaining[allocation[i]] -= 2 + i; Assert.That(remaining[allocation[i]], Is.GreaterThanOrEqualTo(0)); }
            Assert.That(capacities, Is.EqualTo(new[] { a, b, c, 0 }), "Preview must not change the bank.");
        }
        [Test] public void MissingLiveAccountPreservesFractionalMissionRequirements()
        {
            var raw = new ResourceVector(0f, 0.25f, 0.5f, 0.75f, 0.125f);
            ResourceVector priced = TradeFunding.PricedVector(null, null, new Game.Ai.AiTurnContext(), raw);
            Assert.That(priced.Human, Is.EqualTo(raw.Human)); Assert.That(priced.Energy, Is.EqualTo(raw.Energy));
            Assert.That(priced.Materials, Is.EqualTo(raw.Materials)); Assert.That(priced.Tech, Is.EqualTo(raw.Tech));
        }
        [Test] public void SmallAllocationsMatchExhaustiveOracle()
        {
            bool Oracle(int[] bank, int index)
            {
                if (index < 0) return true;
                for (int i = 0; i < 4; i++)
                {
                    if (bank[i] < 2 + index) continue;
                    bank[i] -= 2 + index;
                    bool fits = Oracle(bank, index - 1);
                    bank[i] += 2 + index;
                    if (fits) return true;
                }
                return false;
            }
            for (int a = 0; a <= 8; a++) for (int b = 0; b <= 8; b++)
                for (int c = 0; c <= 8; c++) for (int count = 1; count <= 4; count++)
                {
                    var bank = new[] { a, b, c, 0 };
                    Assert.That(TradeFunding.TryAllocateDonors(bank, 2, count, out _),
                        Is.EqualTo(Oracle((int[])bank.Clone(), count - 1)), $"{a}/{b}/{c}, count={count}");
                }
        }
    }
}
#endif
