using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;
using Game.Terrain;

namespace Game.Core
{
    // Carries the configured players (and now map size/biome) from the main menu's setup panel
    // into the Game scene. Plain static holder — simplest way to pass data across a scene load
    // without needing a DontDestroyOnLoad object.
    public static partial class GameSession
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
            if (players == null || players.Count == 0 || players.Any(p => p == null || p.IsNeutral))
            { error = "Configure at least one non-neutral player."; return false; }
            players = players.Select(p => p.CopySetupDraft()).ToList();
            // Several humans = local hotseat. The campaign bridge always builds exactly one human.
            // An AI player plays a collection deck only when one was chosen on its setup row
            // (SelectedDeckId); Random and campaign enemies always resolve to authored AI decks.
            if (!players.Exists(p => p.IsHuman || !string.IsNullOrEmpty(p.SelectedDeckId)))
            {
                AssignAiDecks(players, config?.collectionDeckCatalog);
                foreach (var player in players) { player.Tactics.Clear(); player.MatchLoadout = null; player.BlueprintQuota = null; }
                AssignPlayerIdentities(players);
                CommitNewLaunch(players, false);
                return true;
            }
            if (!Game.Progression.ProgressionContext.Initialize(config))
            { error = Game.Progression.ProgressionContext.Error; return false; }
            var collection = Game.Progression.ProgressionContext.Collection;
            Game.Progression.SavedDeck selectedHumanDeck = null;
            var snapshots = new Dictionary<PlayerSetupData, Game.Cards.MatchLoadout>();
            foreach (var player in players)
            {
                if (!player.IsHuman && string.IsNullOrEmpty(player.SelectedDeckId)) continue;
                var deck = player.IsHuman && string.IsNullOrEmpty(player.SelectedDeckId) ? collection.DefaultDeck(player.Faction)
                    : collection.GetDeck(player.SelectedDeckId, player.Faction);
                if (deck == null) { error = "Select a saved deck for " + player.Nickname + "."; return false; }
                // Hotseat: the collection remembers the deck of the first human in setup order,
                // exactly what a single-player start remembers.
                if (player.IsHuman && selectedHumanDeck == null) selectedHumanDeck = deck;
                try { snapshots[player] = new Game.Cards.MatchLoadout(deck, collection.Rules, collection.Owned); }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogWarning(ex);
                    error = "Could not load the deck of " + player.Nickname + ". Check it in Collection & Decks.";
                    return false;
                }
            }
            if (selectedHumanDeck != null && !collection.SelectDeck(selectedHumanDeck, out error)) return false;
            foreach (var player in players)
            {
                player.MatchLoadout = snapshots.TryGetValue(player, out var snapshot) ? snapshot : null;
                player.Tactics.Clear();
                // AI production is gated by AiBlueprints (AssignAiDecks), never by a human quota.
                player.BlueprintQuota = player.IsHuman && player.MatchLoadout != null
                    ? new Game.Cards.BlueprintQuota(player.MatchLoadout) : null;
            }
            // Resolve Random only after validation/persistence succeeds. A rejected start must
            // leave the setup row's Random choice intact instead of pinning the next attempt.
            AssignAiDecks(players, config?.collectionDeckCatalog);
            AssignPlayerIdentities(players);
            CommitNewLaunch(players, players.Exists(p => p.IsHuman));
            return true;
        }
        // Each AI player plays one random AI deck of its faction; a choice already made (shown on
        // the setup row) is kept while it is still an authored deck of that faction.
        public static void AssignAiDecks(List<PlayerSetupData> players, Game.Cards.StartingDeckCatalog catalog)
        {
            foreach (var player in players)
            {
                player.AiGrantedLeaderKey = null;
                if (player.IsHuman || player.IsNeutral) { player.AiDeckName = null; player.AiBlueprints = null; continue; }
                if (player.MatchLoadout != null)
                {
                    // Collection deck chosen for this AI: its blueprints unlock unlimited AI
                    // production, exactly as an authored AI deck's blueprint lists do.
                    player.AiDeckName = null;
                    player.AiBlueprints = new HashSet<string>(System.StringComparer.Ordinal);
                    foreach (var e in player.MatchLoadout.BlueprintEntries())
                        if (e.count > 0 && !string.IsNullOrEmpty(e.cardKey)) player.AiBlueprints.Add(e.cardKey);
                    continue;
                }
                if (catalog == null) continue;
                bool valid = player.AiDeckName != null && System.Linq.Enumerable.Any(
                    catalog.GetAiDecks(player.Faction), d => d.deckName == player.AiDeckName);
                if (!valid) player.AiDeckName = catalog.PickAiDeckName(player.Faction);
                player.AiBlueprints = catalog.BuildAiBlueprintKeys(player.Faction, player.AiDeckName);
            }
        }
        private static void AssignPlayerIdentities(List<PlayerSetupData> players)
        {
            foreach (var player in players)
            {
                player.PlayerId = System.Guid.NewGuid().ToString("N");
                player.Hand = null;
            }
        }

        public static void EndRewardEligibility() => RewardsEligible = false;

        // Tactical session only. The campaign controller, its pending operation and the
        // collection/reward profile have independent persistence and must survive this reset.
        public static void ClearMatch()
        {
            BeginExit();
            PendingRestore = null;
            PreparedLaunch = null;
            Stage = MatchSessionStage.Inactive;
            initializedScene = null;
            RestoreNotice = null;
            Players = new List<PlayerSetupData>();
            LocalSeat.Reset();
            ClearMapConditions();
            MatchId = null;
            RewardsEligible = false;
            SetupError = null;
            CampaignContext = null;
            FinalResult = null;
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCollectionSession() => ClearMatch();

        public static List<PlayerSetupData> Players { get; set; } = new List<PlayerSetupData>();

        // Null until the pre-game setup panel actually sets them (GameSetupController.
        // OnStartGameClicked) — nullable rather than defaulting outright so HexMapGenerator/
        // FogOfWarController can tell "no session choice made" (editor-time generation, a scene
        // opened directly) apart from an explicit choice, and fall back to MapGenerationSettings'
        // own design-time radius/Arid default instead.
        public static MapSize? SelectedMapSize { get; set; }
        public static Biome? SelectedBiome { get; set; }
        public static RegionResourceConditions? SelectedResourceConditions { get; set; }
        // A campaign battle's weather (the target region's profile + the operation's seed). Null =
        // no explicit choice: a standalone match gets the default profile with a fresh seed; an
        // explicit Clear is never replaced by that default.
        public static MatchWeatherConditions? SelectedWeather { get; set; }
        public static void ClearMapConditions()
        { SelectedMapSize = null; SelectedBiome = null; SelectedResourceConditions = null; SelectedWeather = null; }

        // The weather a NEW match starts with (read once by GameTurnController.BeginGame).
        public static MatchWeatherConditions ResolveWeather() =>
            SelectedWeather ?? new MatchWeatherConditions(MatchWeatherConditions.StandaloneDefault,
                unchecked(((long)System.Guid.NewGuid().GetHashCode() << 32) ^ (uint)System.Environment.TickCount));

        // fallbackRadius is MapGenerationSettings.radius — its own design-time default.
        public static int ResolveMapRadius(int fallbackRadius) =>
            SelectedMapSize.HasValue ? (int)SelectedMapSize.Value : fallbackRadius;

        public static Biome ResolveBiome() => SelectedBiome ?? Biome.Arid;

        // Every human (local or remote) in the match, in setup order. "Any human in the match"
        // questions use these; "whose screen is this" questions use Game.Players.LocalSeat.
        public static IEnumerable<PlayerSetupData> HumanPlayers =>
            Players?.Where(p => p != null && p.IsHuman) ?? Enumerable.Empty<PlayerSetupData>();
        public static bool HasHuman => Players?.Exists(p => p != null && p.IsHuman) == true;
        // First human in setup order — only for one-human contexts (campaign: exactly one human).
        public static PlayerSetupData FirstHuman => Players?.Find(p => p != null && p.IsHuman);

        // The seated player's PlayerRoot (action points/resources) — null when nobody holds the
        // screen (curtain, AI observer, before setup) or no root is registered yet.
        public static PlayerRoot SeatRoot()
        {
            PlayerSetupData seat = LocalSeat.Current;
            return seat != null ? PlayerRootRegistry.FindFor(seat) : null;
        }
    }
}

