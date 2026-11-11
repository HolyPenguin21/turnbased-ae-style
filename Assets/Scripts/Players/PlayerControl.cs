namespace Game.Players
{
    // Who drives a player. RemoteHuman is never created yet (no networking); every fork on
    // control must still treat it as "a person who is not at this screen" (MP preparation).
    public enum PlayerControl
    {
        Ai = 0,
        LocalHuman = 1,
        RemoteHuman = 2,
    }
}
