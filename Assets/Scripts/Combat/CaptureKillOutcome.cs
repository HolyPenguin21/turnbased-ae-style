namespace Game.Combat
{
    // Result of a "Capture Kill Challenge" (loosely after the manual, pg. 24) — resolved by
    // BattleAttackPopupUI.BeginCaptureKill/ResolveCaptureKill using the same dice mechanic as any
    // other Challenge (see ChallengeResolver), just with its own win condition instead of Ground
    // Combat's straight damage subtraction. Purely the rolled successes decide it — per the
    // user's own call, NOT the manual's own separate "capture threshold" (comparing successes
    // against the target's full original dice-pool size), which let a clean win still resolve as
    // a kill for reasons invisible on the dice themselves:
    //   Escaped  — the hunter's successes were LESS than the target's own successes.
    //   Killed   — the hunter's successes EQUALED the target's (a bare, even win).
    //   Captured — the hunter's successes EXCEEDED the target's (a clean win).
    public enum CaptureKillOutcome
    {
        Escaped,
        Killed,
        Captured
    }
}
