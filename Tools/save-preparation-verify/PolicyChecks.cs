using System;
using Game.Setup;
static class PolicyChecks
{
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    static int Main()
    {
        var ready = new MatchSaveConditions { HasMatch = true, HumanTurn = true, TurnConfirmed = true };
        Check(MatchSaveEligibility.UnavailableReason(ready) == null, "settled human turn allowed");
        foreach (var field in typeof(MatchSaveConditions).GetFields())
        {
            var boxed = (object)ready;
            field.SetValue(boxed, !(bool)field.GetValue(boxed));
            Check(MatchSaveEligibility.UnavailableReason((MatchSaveConditions)boxed) != null, field.Name + " blocks");
        }
        Check(!MatchRestoreScope.IsRestoring, "initial scope");
        using (MatchRestoreScope.Begin())
        {
            Check(MatchRestoreScope.IsRestoring, "outer scope");
            using (MatchRestoreScope.Begin()) Check(MatchRestoreScope.IsRestoring, "inner scope");
            Check(MatchRestoreScope.IsRestoring, "outer survives inner");
        }
        Check(!MatchRestoreScope.IsRestoring, "scope disposed");
        try { using (MatchRestoreScope.Begin()) throw new Exception("expected"); } catch { }
        Check(!MatchRestoreScope.IsRestoring, "exception cleanup");
        Console.WriteLine("PASS: capture blockers, settled turn, nested/exception-safe restore scope");
        return 0;
    }
}
