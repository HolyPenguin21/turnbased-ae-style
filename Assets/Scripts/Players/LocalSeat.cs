using System;
using System.Collections.Generic;

namespace Game.Players
{
    // Whose PRIVATE interface (hand, resources, panels, decisions) this screen shows. One answer
    // for the whole UI instead of "the first human in the list".
    //   single player: always the one human;
    //   hotseat: the human after their handoff was confirmed, null while the curtain is up,
    //            unchanged through an AI turn (the last human keeps watching);
    //   AI observer: null;
    //   MP (future): fixed on the local player.
    // Game rules never read this (they use PlayerSetupData.Control / IsHuman). VisionSystem.
    // CurrentViewer stays separate ("whose picture of the world is drawn").
    public static class LocalSeat
    {
        // Until the turn flow seats someone explicitly (hotseat), the seat is the match's only
        // local human — exactly the old "the human" lookup, so a one-human match never has to
        // manage it. With several local humans and no explicit seat the answer is null: nobody's
        // private UI is shown (fail closed).
        public static PlayerSetupData Current => _managed ? _current : SoleLocalHuman();
        private static PlayerSetupData _current;
        private static bool _managed;
        public static bool IsManaged => _managed;

        // The last player that held the seat; survives the curtain (Current == null) so a
        // handoff can name who is giving the screen back and AI turns know whose fog to keep.
        public static PlayerSetupData Last { get; private set; }

        public static event Action Changed;

        public static bool Is(PlayerSetupData player) => player != null && ReferenceEquals(Current, player);

        internal static void Set(PlayerSetupData player)
        {
            if (player != null && player.Control != PlayerControl.LocalHuman)
                throw new ArgumentException("Only a local human can hold the screen.", nameof(player));
            if (player != null) Last = player;
            var before = Current;
            _managed = true;
            _current = player;
            if (!ReferenceEquals(before, player)) Changed?.Invoke();
        }

        // New match / restore / exit: back to the implicit one-human seat.
        internal static void Reset()
        {
            var before = Current;
            Last = null;
            _current = null;
            _managed = false;
            if (!ReferenceEquals(before, Current)) Changed?.Invoke();
        }

        private static PlayerSetupData SoleLocalHuman()
        {
            PlayerSetupData found = null;
            var players = Game.Core.GameSession.Players;
            if (players == null) return null;
            foreach (var p in players)
            {
                if (p == null || p.Control != PlayerControl.LocalHuman) continue;
                if (found != null) return null;
                found = p;
            }
            return found;
        }

        // Count of local humans that can still take the screen — more than one means hotseat.
        public static int LocalHumanCount(IEnumerable<PlayerSetupData> players)
        {
            int n = 0;
            if (players != null)
                foreach (var p in players)
                    if (p != null && p.Control == PlayerControl.LocalHuman) n++;
            return n;
        }

        public static bool IsHotseat(IEnumerable<PlayerSetupData> players) => LocalHumanCount(players) > 1;
    }
}
