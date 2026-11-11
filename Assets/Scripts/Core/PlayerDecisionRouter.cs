using System;
using Game.Players;

namespace Game.Core
{
    public enum DecisionRoute
    {
        // The AI policy decides (SuggestedFire, ChooseAiTactic, ...).
        Ai,
        // The owner already holds this screen: show the decision UI right away.
        Immediate,
        // Another local human holds the screen: curtain to the owner, decide, curtain back.
        Handoff,
    }

    // Single point for "player X must decide D while it may not be X's screen". Pure routing
    // here; the curtain itself is presentation (GameTurnController registers it as IDecisionHandoff).
    // RemoteHuman is the networking extension point and is not supported yet.
    public static class PlayerDecisionRouter
    {
        public static DecisionRoute Route(PlayerSetupData owner) => Route(owner, LocalSeat.Current);

        public static DecisionRoute Route(PlayerSetupData owner, PlayerSetupData seat)
        {
            if (owner == null || owner.Control == PlayerControl.Ai) return DecisionRoute.Ai;
            if (owner.Control == PlayerControl.RemoteHuman)
                throw new NotSupportedException("Decisions of a remote player need the network layer.");
            // No seat at all (curtain up, or a screen that never seated anyone): the owner must
            // still take the screen first, otherwise the decision would show on an open map.
            return ReferenceEquals(owner, seat) ? DecisionRoute.Immediate : DecisionRoute.Handoff;
        }

        // An interactive encounter (battle, challenge) between these owners: the local human who
        // must hold the screen first, or null when the seat already takes part (owner decision
        // D3: a human-vs-human fight is open, both sides on one screen) or no human takes part.
        public static PlayerSetupData EncounterHandoffTarget(System.Collections.Generic.IEnumerable<PlayerSetupData> owners)
            => EncounterHandoffTarget(owners, LocalSeat.Current);

        public static PlayerSetupData EncounterHandoffTarget(System.Collections.Generic.IEnumerable<PlayerSetupData> owners, PlayerSetupData seat)
        {
            PlayerSetupData first = null;
            if (owners == null) return null;
            foreach (var owner in owners)
            {
                if (owner == null || !owner.IsHuman) continue;
                if (seat != null && ReferenceEquals(owner, seat)) return null;
                if (owner.Control == PlayerControl.RemoteHuman)
                    throw new NotSupportedException("Decisions of a remote player need the network layer.");
                if (first == null) first = owner;
            }
            return first;
        }

        // Presentation hook, set by the turn controller of the running match.
        public static IDecisionHandoff Handoff { get; set; }

        // Coroutine helper: take the screen for `owner` when the route says so. `returnTo`
        // receives the player to give the screen back to afterwards (null = nothing to return).
        public static System.Collections.IEnumerator TakeScreenFor(PlayerSetupData owner, string reason, Action<PlayerSetupData> returnTo)
        {
            if (owner == null || Handoff == null || Route(owner) != DecisionRoute.Handoff) yield break;
            PlayerSetupData previous = null;
            yield return Handoff.HandTo(owner, reason, p => previous = p);
            if (!ReferenceEquals(previous, owner)) returnTo?.Invoke(previous);
        }

        public static System.Collections.IEnumerator GiveScreenBack(PlayerSetupData player)
        {
            if (player == null || Handoff == null) yield break;
            yield return Handoff.ReturnTo(player);
        }
    }

    public interface IDecisionHandoff
    {
        // Covers the screen, asks `owner` to take it, seats them. Completes when confirmed.
        // Returns the player to hand the screen back to (may be null).
        System.Collections.IEnumerator HandTo(PlayerSetupData owner, string reason, Action<PlayerSetupData> previousSeat);
        // Covers the screen and gives it back to `player` (no-op when null or already seated).
        System.Collections.IEnumerator ReturnTo(PlayerSetupData player);
    }
}
