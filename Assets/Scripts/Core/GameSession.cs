using System.Collections.Generic;
using Game.Map;
using Game.Players;
using Game.Terrain;

namespace Game.Core
{
    // Carries the configured players (and now map size/biome) from the main menu's setup panel
    // into the Game scene. Plain static holder — simplest way to pass data across a scene load
    // without needing a DontDestroyOnLoad object.
    public static class GameSession
    {
        public static string MatchId { get; private set; }
        public static bool RewardsEligible { get; private set; }
        public static string SetupError;
        public static Game.Campaign.CampaignMatchContext CampaignContext { get; private set; }
        public static Game.Campaign.CompletedMatchResult? FinalResult { get; private set; }
        public static void SetCampaignContext(Game.Campaign.CampaignMatchContext context)
        { CampaignContext = context; MatchId = context.MatchId; FinalResult = null; }
        public static void CompleteMatch(Game.Campaign.CompletedMatchResult result)
        { if (!FinalResult.HasValue && result.MatchId == MatchId) FinalResult = result; }

        public static bool TryPrepareMatch(List<PlayerSetupData> players, GameConfig config, out string error)
        {
            error = null;
            CampaignContext = null; FinalResult = null;
            if (players.FindAll(p => p.IsHuman).Count > 1)
            { error = "Only one local human profile is supported."; return false; }
            if (!players.Exists(p => p.IsHuman))
            {
                foreach (var player in players) { player.MatchLoadout = null; player.BlueprintQuota = null; }
                MatchId = System.Guid.NewGuid().ToString("N"); RewardsEligible = false;
                return true;
            }
            if (!Game.Progression.ProgressionContext.Initialize(config))
            { error = Game.Progression.ProgressionContext.Error; return false; }
            var collection = Game.Progression.ProgressionContext.Collection;
            Game.Progression.SavedDeck selectedHumanDeck = null;
            var snapshots = new Dictionary<PlayerSetupData, Game.Cards.MatchLoadout>();
            foreach (var player in players)
            {
                if (!player.IsHuman) continue;
                var deck = string.IsNullOrEmpty(player.SelectedDeckId) ? collection.DefaultDeck(player.Faction)
                    : collection.Snapshot.savedDecks.Find(d => d.deckId == player.SelectedDeckId && d.faction == player.Faction);
                if (deck == null) { error = "Select a saved deck for " + player.Faction; return false; }
                selectedHumanDeck = deck;
                try { snapshots[player] = new Game.Cards.MatchLoadout(deck, collection.Rules, collection.Owned); }
                catch (System.Exception ex) { error = ex.Message; return false; }
            }
            if (!collection.SelectDeck(selectedHumanDeck, out error)) return false;
            foreach (var player in players)
            {
                player.MatchLoadout = snapshots.TryGetValue(player, out var snapshot) ? snapshot : null;
                player.BlueprintQuota = player.MatchLoadout == null ? null : new Game.Cards.BlueprintQuota(player.MatchLoadout);
            }
            MatchId = System.Guid.NewGuid().ToString("N");
            RewardsEligible = players.Exists(p => p.IsHuman);
            return true;
        }
        public static void EndRewardEligibility() => RewardsEligible = false;
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCollectionSession() { MatchId = null; RewardsEligible = false; SetupError = null; CampaignContext = null; FinalResult = null; }

        public static List<PlayerSetupData> Players { get; set; } = new List<PlayerSetupData>();

        // Null until the pre-game setup panel actually sets them (GameSetupController.
        // OnStartGameClicked) — nullable rather than defaulting outright so HexMapGenerator/
        // FogOfWarController can tell "no session choice made" (editor-time generation, a scene
        // opened directly) apart from an explicit choice, and fall back to MapGenerationSettings'
        // own design-time radius/Arid default instead.
        public static MapSize? SelectedMapSize { get; set; }
        public static Biome? SelectedBiome { get; set; }

        // fallbackRadius is MapGenerationSettings.radius — its own design-time default.
        public static int ResolveMapRadius(int fallbackRadius) =>
            SelectedMapSize.HasValue ? (int)SelectedMapSize.Value : fallbackRadius;

        public static Biome ResolveBiome() => SelectedBiome ?? Biome.Arid;

        // The one local human player, if any — every AI/Neutral slot is never IsHuman. Was
        // reimplemented independently as `GameSession.Players?.Find(p => p != null &&
        // p.IsHuman)` in several unrelated files; centralized here since they're all reading the
        // exact same list, not just coincidentally-similar code.
        public static PlayerSetupData FindHumanPlayer() => Players?.Find(p => p != null && p.IsHuman);

        // The human's PlayerRoot (action points/resources) — null if there's no human player yet
        // (e.g. before setup finishes) or no PlayerRoot registered for them yet.
        public static PlayerRoot FindHumanRoot()
        {
            PlayerSetupData human = FindHumanPlayer();
            return human != null ? PlayerRootRegistry.FindFor(human) : null;
        }
    }
}

