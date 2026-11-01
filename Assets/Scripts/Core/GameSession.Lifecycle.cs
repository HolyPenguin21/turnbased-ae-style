using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Players;
using Game.Setup;
namespace Game.Core
{
    public enum MatchSessionStage { Inactive, Prepared, InitializingWorld, BindingPresentation, Running, Exiting }
    public sealed class MatchLaunchRequest
    {
        public IReadOnlyList<PlayerSetupData> Players { get; }
        internal MatchLaunchRequest(List<PlayerSetupData> players) => Players = players.AsReadOnly();
    }
    public static partial class GameSession
    {
        public static MatchSessionStage Stage { get; private set; }
        public static MatchLaunchRequest PreparedLaunch { get; private set; }
        public static long Generation { get; private set; }
        private static int? initializedScene;
        public static bool IsCurrent(long generation) => Generation == generation && Stage != MatchSessionStage.Exiting;
        private static void CommitNewLaunch(List<PlayerSetupData> players, bool rewardsEligible)
        {
            PendingRestore = null; RestoreNotice = null; CampaignContext = null; FinalResult = null; SetupError = null;
            ClearMapConditions(); Players = players; PreparedLaunch = new MatchLaunchRequest(players);
            MatchId = Guid.NewGuid().ToString("N"); RewardsEligible = rewardsEligible; Stage = MatchSessionStage.Prepared;
        }
        // Generator and setup share this entry; repeated component activation does not reset
        // a world. A newly prepared request can reconstruct even in the same native scene.
        public static void InitializeWorld(int sceneHandle)
        {
            if (initializedScene == sceneHandle && Stage != MatchSessionStage.Prepared) return;
            if (Stage == MatchSessionStage.Running || Stage == MatchSessionStage.Exiting
                || Stage == MatchSessionStage.BindingPresentation || Stage == MatchSessionStage.InitializingWorld) ClearMatch();
            Generation++; initializedScene = sceneHandle; Stage = MatchSessionStage.InitializingWorld;
            MatchStateInventory.ResetForNewMatch();
        }
        public static void InitializeNewMatchHands(StartingDeckCatalog catalog, int startingSize = 7, int capacity = 10)
        {
            if (PendingRestore != null) throw new InvalidOperationException("A restore must retain its saved hands.");
            foreach (var player in Players.Where(p => p.IsHuman))
            {
                if (player.Hand != null) continue;
                var rules = new DeckRules(catalog, Game.Progression.ProgressionContext.Collection?.Rules.Research);
                if (player.MatchLoadout == null || player.MatchLoadout.Faction != player.Faction
                    || !player.MatchLoadout.TryBuildPool(rules, out var pool, out _))
                    throw new InvalidOperationException("Cannot load the selected deck. Select a saved deck in Collection & Decks.");
                player.Hand = new PlayerHandData(pool, startingSize, capacity);
            }
        }
        public static void BeginPresentationBinding() => Stage = MatchSessionStage.BindingPresentation;
        public static void MarkRunning() { PreparedLaunch = null; Stage = MatchSessionStage.Running; }
        public static void BeginExit()
        {
            if (Stage == MatchSessionStage.Exiting) return;
            Stage = MatchSessionStage.Exiting; Generation++;
        }
    }
}
