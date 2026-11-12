#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using Game.Campaign;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;

// Regional weather (spec 8.4): a fixed profile per region from its own zone stream, snapshotted
// from the TARGET into every operation, schema 3 with obsolete 1-2, and the player-facing line.
public sealed class CampaignWeatherTests
{
    private string directory;
    [SetUp] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "campaign-weather-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }
    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static CampaignState Planet(int seed = 37, CampaignGenerationSettings settings = null) =>
        new CampaignMapGenerator().Generate(seed, Faction.Ashen, settings ?? new CampaignGenerationSettings { RegionCount = 24 });
    private CampaignController Controller(CampaignState s) => new CampaignController(s, new CampaignStore(directory));
    private static CampaignState HumanTurn(CampaignState s) { s.CurrentTurnIndex = s.TurnOrder.IndexOf(s.HumanFaction); return s; }
    private static string AllButWeather(CampaignState s) => string.Join("|", s.Regions.OrderBy(r => r.RegionId).Select(r =>
        $"{r.RegionId}:{r.Name}:{r.OwnerFaction}:{r.Biome}:{r.Specialization}:{r.Richness}:{string.Join(",", r.NeighborIds)}:"
        + string.Join(";", r.PolygonVertices.Select(v => v.x.ToString("R") + "," + v.y.ToString("R")))))
        + $"#{s.PlanetName}#{s.BackgroundId}#{string.Join(",", s.TurnOrder)}#{s.RandomSequence}";
    private static string Weathers(CampaignState s) => string.Join(",", s.Regions.OrderBy(r => r.RegionId).Select(r => (int)r.Weather));

    [Test]
    public void SameSeed_SameWeather_AllThreeProfilesPresent()
    {
        foreach (int seed in new[] { 1, 37, -5, 2026 })
        {
            CampaignState a = Planet(seed), b = Planet(seed);
            Assert.That(Weathers(a), Is.EqualTo(Weathers(b)));
            Assert.That(a.Regions.Select(r => r.Weather).Distinct(), Is.EquivalentTo(new[]
                { MatchWeatherProfile.Clear, MatchWeatherProfile.OneSandstorm, MatchWeatherProfile.TwoSandstorms }), "seed " + seed);
            Assert.DoesNotThrow(() => CampaignStore.Validate(a));
        }
        var small = Planet(3, new CampaignGenerationSettings { RegionCount = 3 });
        Assert.That(small.Regions.Select(r => r.Weather).Distinct().Count(), Is.EqualTo(3));
    }

    [Test]
    public void WeatherSettings_DoNotMoveAnyOtherProperty()
    {
        CampaignState baseline = Planet(77);
        CampaignState other = Planet(77, new CampaignGenerationSettings { RegionCount = 24, WeatherZoneCount = 11 });
        Assert.That(AllButWeather(other), Is.EqualTo(AllButWeather(baseline)));
        Assert.Throws<ArgumentException>(() => Planet(1, new CampaignGenerationSettings { WeatherZoneCount = 2 }));
    }

    [Test]
    public void EveryProfileAppearsInBothBiomes()
    {
        var seen = new System.Collections.Generic.HashSet<(Biome, MatchWeatherProfile)>();
        for (int seed = 0; seed < 20; seed++)
            foreach (RegionState r in Planet(seed).Regions) seen.Add((r.Biome, r.Weather));
        foreach (Biome biome in new[] { Biome.Arid, Biome.Desert })
            foreach (MatchWeatherProfile p in new[] { MatchWeatherProfile.Clear, MatchWeatherProfile.OneSandstorm, MatchWeatherProfile.TwoSandstorms })
                Assert.That(seen, Has.Member((biome, p)), $"{biome}/{p}");
    }

    [Test]
    public void Operation_TakesTheTargetsWeather_NeverTheSources()
    {
        var s = HumanTurn(Planet());
        var pair = CampaignRules.LegalAttacks(s).First();
        s.Regions.Find(r => r.RegionId == pair.Source).Weather = MatchWeatherProfile.TwoSandstorms;
        s.Regions.Find(r => r.RegionId == pair.Target).Weather = MatchWeatherProfile.Clear;
        var c = Controller(s);
        int sequence = c.View.RandomSequence;
        c.BeginAttack(pair.Source, pair.Target, true);
        var op = c.Snapshot.PendingOperation;
        Assert.That(op.Weather, Is.EqualTo(MatchWeatherProfile.Clear));
        Assert.That(op.WeatherConditions, Is.EqualTo(op.WeatherConditions), "reading the weather twice draws nothing");
        Assert.That(op.WeatherConditions.Seed, Is.EqualTo(MatchWeatherConditions.DeriveSeed(op.RandomSeed)));
        Assert.That(c.View.RandomSequence, Is.EqualTo(sequence + 1), "only the operation's own seed draw");
        Assert.That(c.Snapshot.PendingOperation.Weather, Is.EqualTo(MatchWeatherProfile.Clear), "survives Copy");
    }

    [Test]
    public void EnemyAttackOnAHumanRegion_UsesTheDefendedRegionsWeather()
    {
        var s = Planet();
        foreach (Faction ai in s.TurnOrder.Where(f => f != s.HumanFaction))
        {
            s.CurrentTurnIndex = s.TurnOrder.IndexOf(ai);
            var pair = CampaignRules.LegalAttacks(s).FirstOrDefault(p =>
                s.Regions.Find(r => r.RegionId == p.Target).OwnerFaction == s.HumanFaction);
            if (pair.Equals(default((int Source, int Target)))) continue;
            s.Regions.Find(r => r.RegionId == pair.Target).Weather = MatchWeatherProfile.TwoSandstorms;
            s.Regions.Find(r => r.RegionId == pair.Source).Weather = MatchWeatherProfile.Clear;
            var c = Controller(s);
            c.BeginAttack(pair.Source, pair.Target, true);
            Assert.That(c.Snapshot.PendingOperation.Weather, Is.EqualTo(MatchWeatherProfile.TwoSandstorms));
            return;
        }
        Assert.Inconclusive("No AI faction borders the human on this planet.");
    }

    private static CampaignDeck Deck()
    {
        var hero = new CardDefinition { authoredKey = "hero", cardType = CardType.Hero, commandRating = 5, fate = 3, initiative = 3 };
        var unit = new CardDefinition { authoredKey = "unit", cardType = CardType.Unit, attack = 3, defenseRating = 3, hitPoints = 3, range = 2 };
        return new CampaignDeck("test", new[] { hero, unit, unit, unit, unit });
    }

    [Test]
    public void CaptureAndAutoResolve_NeverChangeTheRegionsWeather()
    {
        var s = HumanTurn(Planet());
        var pair = CampaignRules.LegalAttacks(s).First();
        var before = Weathers(s);
        var c = Controller(s);
        c.BeginAttack(pair.Source, pair.Target, true);
        var op = c.Snapshot.PendingOperation;
        c.ResolveFast(Deck(), Deck(), new CampaignBattleResolver());
        Assert.That(Weathers(c.Snapshot), Is.EqualTo(before), "the coarse auto-resolve model never touches weather");
        Assert.That(c.Snapshot.PendingOperation?.Weather ?? op.Weather, Is.EqualTo(op.Weather));
    }

    [Test]
    public void SchemaThree_MissingOrUnknownWeather_IsADataError()
    {
        var s = Planet();
        s.Regions[0].Weather = 0;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(s));
        s.Regions[0].Weather = (MatchWeatherProfile)9;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(s));
    }

    [Test]
    public void OperationWeather_MustMatchItsTarget_AndAPlaceholderStaysEmpty()
    {
        var s = HumanTurn(Planet());
        var pair = CampaignRules.LegalAttacks(s).First();
        var c = Controller(s);
        c.BeginAttack(pair.Source, pair.Target, true);
        var snapshot = c.Snapshot;
        snapshot.PendingOperation.Weather = snapshot.Regions.Find(r => r.RegionId == pair.Target).Weather == MatchWeatherProfile.Clear
            ? MatchWeatherProfile.TwoSandstorms : MatchWeatherProfile.Clear;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(snapshot));
        Assert.That(new CampaignOperation().IsEmptyInlineValue, Is.True);
        Assert.That(new CampaignOperation { Weather = MatchWeatherProfile.Clear }.IsEmptyInlineValue, Is.False,
            "a real profile is never mistaken for Unity's null placeholder");
    }

    private string File1 => Path.Combine(directory, "campaign-v1.json");

    private static string WithSchema(CampaignState s, int version)
    {
        var copy = s.Copy(); copy.SchemaVersion = version;
        return JsonUtility.ToJson(copy);
    }

    [Test]
    public void PrimarySchemaTwo_BackupCurrent_RestoresTheBackup()
    {
        var state = Planet(); var store = new CampaignStore(directory); store.Save(state); store.Save(state);
        string backup = File.ReadAllText(File1 + ".bak");
        File.WriteAllText(File1, WithSchema(state, 2));
        Assert.That(store.Load(out var notice).CampaignId, Is.EqualTo(state.CampaignId));
        Assert.That(notice, Is.Not.Empty);
        Assert.That(File.ReadAllText(File1 + ".bak"), Is.EqualTo(backup), "the current backup is kept");
    }

    [Test]
    public void PrimarySchemaTwo_BackupFuture_KeepsTheFutureFile()
    {
        var state = Planet();
        const string future = "{\"SchemaVersion\":4,\"Regions\":{\"newShape\":true}}";
        File.WriteAllText(File1, WithSchema(state, 2)); File.WriteAllText(File1 + ".bak", future);
        Assert.Throws<NotSupportedException>(() => new CampaignStore(directory).Load(out _));
        Assert.That(File.ReadAllText(File1 + ".bak"), Is.EqualTo(future));
    }

    [Test]
    public void BothSchemaTwo_IsAnObsoleteCampaign_NotCorruption()
    {
        var state = Planet();
        File.WriteAllText(File1, WithSchema(state, 2)); File.WriteAllText(File1 + ".bak", WithSchema(state, 2));
        var store = new CampaignStore(directory);
        Assert.That(store.Load(out var notice), Is.Null);
        Assert.That(notice, Does.Contain("incompatible"));
        Assert.That(store.Exists, Is.False);
    }

    [Test]
    public void WeatherLine_IsThePlayerFacingTextForEveryProfile()
    {
        Assert.That(MatchWeatherConditions.Describe(MatchWeatherProfile.Clear), Is.EqualTo("Weather: Clear — no sandstorms"));
        Assert.That(MatchWeatherConditions.Describe(MatchWeatherProfile.OneSandstorm), Is.EqualTo("Weather: Sandstorms — up to 1"));
        Assert.That(MatchWeatherConditions.Describe(MatchWeatherProfile.TwoSandstorms), Is.EqualTo("Weather: Sandstorms — up to 2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchWeatherConditions.Describe(0));
    }

    [Test]
    public void CampaignUiAndScene_CarryNoGeneratorNotes()
    {
        string ui = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts/Campaign/CampaignUI.cs"));
        string scene = File.ReadAllText(Path.Combine(Application.dataPath, "Scenes/Campaign.unity"));
        foreach (string text in new[] { ui, scene })
        {
            Assert.That(text, Does.Not.Contain("DescribeTarget"));
            Assert.That(text, Does.Not.Contain("Generation target"));
            Assert.That(text, Does.Not.Contain("random remainder"));
        }
        Assert.That(ui, Does.Contain("MatchWeatherConditions.Describe"));
    }
}
#endif
