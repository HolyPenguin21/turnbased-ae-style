using System;
using System.Linq;
using Game.Campaign;
using Game.Setup;

namespace Game.Core
{
    public static partial class GameSession
    {
        public static IMatchRestoreSource PendingRestore { get; private set; }

        public static bool TryPrepareRestore(IMatchRestoreSource source, GameConfig config, out string error)
        {
            try
            {
                ValidateRestore(source, config);
                // Commit only after the entire detached input is accepted. No reset, deck
                // validation against today's profile, new MatchId or reward transaction here.
                Players = source.Players.ToList();
                MatchId = source.MatchId;
                RewardsEligible = source.RewardsEligible;
                CampaignContext = source.CampaignContext;
                FinalResult = null;
                SetupError = null;
                ClearMapConditions();
                PendingRestore = source;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not prepare the saved match: " + ex.Message;
                return false;
            }
        }

        internal static void ValidateRestore(IMatchRestoreSource source, GameConfig config)
        {
            if (source == null || config == null) throw new ArgumentException("Missing saved match or game configuration.");
            if (string.IsNullOrWhiteSpace(source.MatchId) || source.Map == null || source.Turn == null || source.Identity == null
                || source.Players == null || source.Players.Count == 0 || source.Players.Any(p => p == null || p.IsNeutral))
                throw new ArgumentException("Incomplete saved match.");
            var humans = source.Players.Where(p => p.IsHuman).ToArray();
            if (humans.Length != 1 || humans[0].IsEliminated || humans[0].Hand == null)
                throw new ArgumentException("A saved match requires one living human and its restored hand.");
            source.Identity.Validate();
            source.Turn.Validate(source.Players.Select(p => p.PlayerId), humans[0].PlayerId);
            if (source.CampaignContext != null && source.CampaignContext.MatchId != source.MatchId)
                throw new ArgumentException("Campaign and match identities differ.");
            Game.Map.HexMapGenerator.ValidateLayout(source.Map, config);
            if (!CampaignMatchBridge.CanRestoreContext(source.CampaignContext, out string error))
                throw new ArgumentException(error);
            source.Validate(config);
        }

        internal static void CompleteRestore(IMatchRestoreSource source)
        {
            if (!ReferenceEquals(source, PendingRestore)) throw new InvalidOperationException("Saved match request changed during loading.");
            PendingRestore = null;
        }
    }
}
