using System;

namespace Game.Ai
{
    // What an AI/Neutral player is busy with right now, as a general kind only — no army, place
    // or outcome. GameTurnController shows its phrase under "Current turn: X" on the other
    // player's turn popup, so the human sees the turn is alive (owner, 2026-11-11). Producers
    // report at the moment the activity starts; nothing reads it back for decisions.
    public enum AiActivity
    {
        Planning,
        Developing,
        ExploringWorld,
        AirOperations,
        Fighting,
        HuntingHero,
        ExploringEvent,
    }

    public static class AiActivityFeed
    {
        public static event Action<AiActivity> Reported;

        public static void Report(AiActivity activity) => Reported?.Invoke(activity);

        public static string Phrase(AiActivity activity) => activity switch
        {
            AiActivity.Planning => "Planning...",
            AiActivity.Developing => "Developing...",
            AiActivity.ExploringWorld => "Exploring the world...",
            AiActivity.AirOperations => "Air operations...",
            AiActivity.Fighting => "Fighting a battle...",
            AiActivity.HuntingHero => "Hunting a hero...",
            AiActivity.ExploringEvent => "Exploring an event...",
            _ => null,
        };
    }
}
