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

        private static readonly float[] UselessHuman = { 0.2f, 1.5f, 1.5f, 1.5f };
        private static readonly float[] NoIncome = { 0f, 0f, 0f, 0f };

        [Test] public void AccessIsWorthNothingWithoutProtectedSurplus()
        {
            float gain = TradeFunding.SimulateConversionGain(new[] { 5f, 5f, 5f, 5f },
                new[] { 10f, 10f, 10f, 10f }, new[] { 1f, 1f, 1f, 1f }, UselessHuman, 2, 1);
            Assert.That(gain, Is.EqualTo(0f));
        }

        [Test] public void UnneededResourceIsConvertedOnlyAboveItsProtection()
        {
            // Human 20, of which 1 is still needed: 19 surplus pays rates 2+3+4+5; 6 does not fit.
            var stock = new[] { 20f, 0f, 0f, 0f };
            float gain = TradeFunding.SimulateConversionGain(stock, new[] { 1f, 10f, 10f, 10f },
                NoIncome, UselessHuman, 2, 1);
            Assert.That(gain, Is.EqualTo(1.1f + 0.9f + 0.7f + 0.5f).Within(1e-4f));
            Assert.That(stock[0], Is.EqualTo(20f), "Simulation must not change the caller's stock.");
        }

        [Test] public void ProgressiveRateStopsUnprofitableConversion()
        {
            float gain = TradeFunding.SimulateConversionGain(new[] { 100f, 0f, 0f, 0f },
                new[] { 0f, 10f, 10f, 10f }, NoIncome, new[] { 0.5f, 1.5f, 1.5f, 1.5f }, 2, 1);
            Assert.That(gain, Is.EqualTo(0.5f).Within(1e-4f), "rate 3 x 0.5 already equals the bought unit");
        }

        [Test] public void ProjectedIncomeAndIdleDecayFeedLaterTurns()
        {
            var income = new[] { 3f, 0f, 0f, 0f };
            var protect = new[] { 0f, 10f, 10f, 10f };
            Assert.That(TradeFunding.SimulateConversionGain(new float[4], protect, income, UselessHuman, 6, 1),
                Is.EqualTo(0f));
            // Turn 0 idle (6->5), turn 1 Human 3 < 5 idle (5->4), turn 2 Human 6 pays 4.
            Assert.That(TradeFunding.SimulateConversionGain(new float[4], protect, income, UselessHuman, 6, 3),
                Is.EqualTo(1.5f - 4 * 0.2f).Within(1e-4f));
        }
    }
}
#endif
