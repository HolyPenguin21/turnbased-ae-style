using System;
using Game.Map;
using System.Collections.Generic;
using Game.Core;
using Game.Players;
using Game.Progression;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Campaign
{
    public sealed class CampaignMatchContext
    {
        public string CampaignId, OperationId, MatchId, SelectedHumanDeckId;
        public Faction AttackerFaction, DefenderFaction, HumanFaction;
        public int SourceRegionId, TargetRegionId;
        // The target region's weather and the operation's weather seed this battle started with.
        public MatchWeatherProfile Weather;
        public long WeatherSeed;
        public MatchWeatherConditions WeatherConditions => new MatchWeatherConditions(Weather, WeatherSeed);
    }
    // Authoritative whole-match result, produced only at GameTurnController's game-over boundary.
    public readonly struct CompletedMatchResult
    {
        public readonly string MatchId;
        public readonly Faction WinnerFaction, LoserFaction;
        public readonly bool Draw;
        public CompletedMatchResult(string matchId, Faction winner, Faction loser, bool draw)
        { MatchId = matchId; WinnerFaction = winner; LoserFaction = loser; Draw = draw; }
    }
    public static partial class CampaignMatchBridge
    {
        public static CampaignController Controller { get; private set; }
        public static string Notice { get; private set; }
        // A manual battle may commit in Game before CampaignUI exists. Keep one live receipt across
        // that scene handoff, consume it once, and never reconstruct it from saved battle history.
        private static CampaignRegionOwnershipChange? pendingOwnershipChange;
        public static CampaignStore Store => new CampaignStore(Application.persistentDataPath);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { SetController(null); pendingOwnershipChange = null; Notice = null; }
        private static void SetController(CampaignController controller)
        {
            if (Controller != null) Controller.RegionOwnershipChanged -= RememberOwnershipChange;
            Controller = controller;
            if (Controller != null) Controller.RegionOwnershipChanged += RememberOwnershipChange;
        }
        private static void RememberOwnershipChange(CampaignRegionOwnershipChange change) => pendingOwnershipChange = change;
        public static bool TryTakeOwnershipChange(out CampaignRegionOwnershipChange change)
        {
            var pending = pendingOwnershipChange;
            pendingOwnershipChange = null;
            change = pending.GetValueOrDefault();
            return pending.HasValue && change.Matches(Controller?.View);
        }
        public static bool Load(out string error)
        {
            error = null; SetController(null);
            try
            {
                var store = Store;
                var s = store.Load(out string notice); Notice = notice;
                SetController(s == null ? null : new CampaignController(s, store));
                if (pendingOwnershipChange.HasValue && !pendingOwnershipChange.Value.Matches(s)) pendingOwnershipChange = null;
                return s != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(ex);
                error = ex is NotSupportedException ? "Update the game to load this campaign."
                    : "Could not load the campaign. Try again.";
                return false;
            }
        }
        public static void Create(Faction human)
        {
            var state = new CampaignMapGenerator().Generate(unchecked((int)DateTime.UtcNow.Ticks), human);
            var store = Store;
            store.Save(state); pendingOwnershipChange = null; SetController(new CampaignController(state, store));
        }
        public static bool PrepareMatch(IProgressionContent config, string deckId, out string error)
        {
            error = null;
            try
            {
                if (Controller == null) { error = "Campaign is not loaded."; return false; }
                var s = Controller.View; var op = s.PendingOperation;
                if (op == null || !op.Manual || op.TestAutoResolve || op.ResultRecorded)
                { error = "No battle is ready to play."; return false; }
                if (!ProgressionContext.Initialize(config)) { error = ProgressionContext.Error; return false; }
                var collection = ProgressionContext.Collection;
                var deck = collection.GetDeck(deckId, s.HumanFaction);
                if (deck == null) { error = "Select a saved deck of your faction."; return false; }
                var validation = collection.Rules.Validate(deck, collection.Owned);
                if (!validation.IsValid) { error = "Deck needs changes. Open Collection & Decks."; return false; }
                var enemy = op.AttackerFaction == s.HumanFaction ? op.DefenderFaction : op.AttackerFaction;
                var players = new List<PlayerSetupData>();
                foreach (var faction in new[] { op.AttackerFaction, op.DefenderFaction })
                    players.Add(new PlayerSetupData { Nickname = faction.ToString(), Faction = faction, ColorIndex = players.Count, IsHuman = faction == s.HumanFaction, SelectedDeckId = faction == s.HumanFaction ? deckId : null });
                var enemyPlayer = players.Find(p => !p.IsHuman);
                string savedEnemyName = enemy == op.AttackerFaction ? op.AttackerDeckName : op.DefenderDeckName;
                enemyPlayer.AiDeckName = CampaignDeck.AiSource(enemy, collection.Rules, op.RandomSeed, savedEnemyName)?.deckName;
                if (!GameSession.TryPrepareMatch(players, config, out error)) return false;
                // A retry reuses the saved MatchId, only a fresh tactical world is created.
                Controller.RegisterMatch(deckId, deck.name, GameSession.Players.Find(p => !p.IsHuman).AiDeckName ?? collection.Rules.Starting.GetDeck(enemy)?.deckName ?? "Standard " + enemy);
                GameSession.SetCampaignContext(new CampaignMatchContext { CampaignId = s.CampaignId, OperationId = op.OperationId,
                    MatchId = op.MatchId, AttackerFaction = op.AttackerFaction, DefenderFaction = op.DefenderFaction, HumanFaction = s.HumanFaction,
                    SourceRegionId = op.SourceRegionId, TargetRegionId = op.TargetRegionId, SelectedHumanDeckId = deckId,
                    Weather = op.Weather, WeatherSeed = op.WeatherConditions.Seed });
                var target = s.FindRegion(op.TargetRegionId);
                if (target.Weather != op.Weather) throw new InvalidOperationException("Battle weather does not match its region.");
                // After TryPrepareMatch: its CommitNewLaunch clears every previous map condition.
                GameSession.SelectedMapSize = null;
                GameSession.SelectedBiome = target.Biome;
                GameSession.SelectedResourceConditions = target.ResourceConditions;
                GameSession.SelectedWeather = op.WeatherConditions;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(ex);
                error = "Could not prepare the battle. Try again.";
                return false;
            }
        }
        internal static CampaignController RequireResultController()
        {
            if (Controller == null && !Load(out string error))
                throw new InvalidOperationException(error ?? "No saved campaign.");
            return Controller;
        }
        public static string ReturnScene(string matchId) => !string.IsNullOrWhiteSpace(matchId)
            && (Controller?.View.PendingOperation?.MatchId == matchId || GameSession.CampaignContext?.MatchId == matchId) ? SceneNames.Campaign : SceneNames.MainMenu;
    }
}

