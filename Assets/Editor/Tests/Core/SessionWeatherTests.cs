#if UNITY_INCLUDE_TESTS
using Game.Core;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // GameSession carries a campaign battle's weather into the match and never leaks it into the
    // next one (spec 8.4, CP9): explicit Clear stays Clear; no choice = the standalone default.
    public sealed class SessionWeatherTests
    {
        private MatchWeatherConditions? _before;
        [SetUp] public void SetUp() => _before = GameSession.SelectedWeather;
        [TearDown] public void TearDown() => GameSession.SelectedWeather = _before;

        [Test]
        public void TwoThenClearThenStandalone_NothingLeaks()
        {
            GameSession.SelectedWeather = new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, 5);
            Assert.That(GameSession.ResolveWeather(), Is.EqualTo(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, 5)));
            GameSession.ClearMapConditions();
            GameSession.SelectedWeather = new MatchWeatherConditions(MatchWeatherProfile.Clear, 6);
            Assert.That(GameSession.ResolveWeather().Profile, Is.EqualTo(MatchWeatherProfile.Clear),
                "an explicit Clear is never replaced by the standalone default");
            GameSession.ClearMapConditions();
            Assert.That(GameSession.SelectedWeather, Is.Null);
            Assert.That(GameSession.ResolveWeather().Profile, Is.EqualTo(MatchWeatherConditions.StandaloneDefault));
        }

        [Test]
        public void OperationSeed_IsDerivedWithItsOwnSalt_AndStable()
        {
            Assert.That(MatchWeatherConditions.DeriveSeed(1234), Is.EqualTo(MatchWeatherConditions.DeriveSeed(1234)));
            Assert.That(MatchWeatherConditions.DeriveSeed(1234), Is.Not.EqualTo(MatchWeatherConditions.DeriveSeed(1235)));
            Assert.That(MatchWeatherConditions.DeriveSeed(1234), Is.Not.EqualTo(1234L));
        }
    }
}
#endif
