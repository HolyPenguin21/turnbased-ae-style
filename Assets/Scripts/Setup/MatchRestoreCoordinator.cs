using System;
using Game.Core;
using Game.Map;
using Game.Turns;

namespace Game.Setup
{
    public static class MatchRestoreCoordinator
    {
        public static void RestorePending(GameConfig config, HexMap map, GameTurnController turns, HexSelectionController selection)
        {
            var source = GameSession.PendingRestore ?? throw new InvalidOperationException("No saved match is prepared.");
            if (map == null || turns == null || selection == null) throw new ArgumentException("Saved match scene dependencies are missing.");
            GameSession.ValidateRestore(source, config);
            using (MatchRestoreScope.Begin())
            {
                source.Identity.Reserve();
                source.RestoreWorld(map);
                source.RestoreKnowledge();
                source.RestorePresentation(selection);
                selection.ValidateRememberedVisualCoverage();
                // Generation ran before restored bonuses/events existed. Rebuild their derived
                // markers now, with complete knowledge, without publishing gameplay events.
                map.GetComponent<MapResourceDisplay>()?.RefreshAll();
                map.GetComponent<MapEventDisplay>()?.RefreshAll();
            }
            // One presentation bind after complete world+knowledge. This resumes a settled
            // turn rather than performing any of the new-game / new-round side effects.
            turns.ResumeGame(source.Turn);
            GameSession.CompleteRestore(source);
        }
    }
}
