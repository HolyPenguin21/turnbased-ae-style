using System;
using Game.EditorTests;

static class Program
{
    static int Main()
    {
        var tests = new AiPersistencePreparationTests();
        var cases = new (string Name, Action Run)[]
        {
            (nameof(tests.PublicInitiativeHistoryKeepsOrderAndWindowAcrossPlayerRebinding), tests.PublicInitiativeHistoryKeepsOrderAndWindowAcrossPlayerRebinding),
            (nameof(tests.InvalidPlayerBindingFailsBeforeOverwritingHistoricalBaseline), tests.InvalidPlayerBindingFailsBeforeOverwritingHistoricalBaseline),
            (nameof(tests.PreparationFailureStillBlocksExactlyTheFollowingTurn), tests.PreparationFailureStillBlocksExactlyTheFollowingTurn),
            (nameof(tests.AllocatorCooldownRestoresOriginalReasonAndExpiry), tests.AllocatorCooldownRestoresOriginalReasonAndExpiry),
            (nameof(tests.MobilizationRestoresHoldWithoutRestoringOutgoingTurnStamps), tests.MobilizationRestoresHoldWithoutRestoringOutgoingTurnStamps),
            (nameof(tests.RetiredReconSortieIdentityIsNotReusedByFutureCoverage), tests.RetiredReconSortieIdentityIsNotReusedByFutureCoverage),
            (nameof(tests.MapRestoreKeepsStaleObservationAndRebindsItsForeignOwner), tests.MapRestoreKeepsStaleObservationAndRebindsItsForeignOwner),
            (nameof(tests.MissionRestoreKeepsClocksAndPrivateEconomyDevelopmentEvidence), tests.MissionRestoreKeepsClocksAndPrivateEconomyDevelopmentEvidence),
        };
        int failed = 0;
        foreach (var test in cases)
        {
            try { test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + error); }
            finally { tests.ClearOwners(); }
        }
        Console.WriteLine($"AI preparation: {cases.Length - failed}/{cases.Length} passed (managed owners; no native Unity execution)");
        var core = new MatchRestorePreparationTests();
        int corePassed = 0;
        var coreCases = new (string Name, Action Run)[]
        {
            ("InvalidRestorePreservesSession", core.InvalidPreparedRestoreDoesNotReplaceTheCurrentSession),
            ("SavePolicyPendingOperations", core.SavePolicyAllowsSettledTurnButRejectsEachPendingOperation),
            ("CampaignValid", () => core.CampaignRestoreRequiresTheSameUnfinishedManualOperation("valid", true)),
            ("CampaignMismatch", () => core.CampaignRestoreRequiresTheSameUnfinishedManualOperation("differentCampaign", false)),
            ("MatchMismatch", () => core.CampaignRestoreRequiresTheSameUnfinishedManualOperation("differentMatch", false)),
            ("DeckMismatch", () => core.CampaignRestoreRequiresTheSameUnfinishedManualOperation("differentDeck", false)),
            ("ResultAlreadyRecorded", () => core.CampaignRestoreRequiresTheSameUnfinishedManualOperation("recordedResult", false)),
            ("OwnershipAlreadyApplied", () => core.CampaignRestoreRequiresTheSameUnfinishedManualOperation("ownershipApplied", false)),
            ("AutomaticOperation", () => core.CampaignRestoreRequiresTheSameUnfinishedManualOperation("automatic", false)),
        };
        foreach (var test in coreCases)
        {
            try { test.Run(); corePassed++; Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + error); }
        }
        Console.WriteLine($"Core policy/campaign preparation: {corePassed}/{coreCases.Length} passed (native ResumeGame scene test requires Unity)");
        return failed == 0 ? 0 : 1;
    }
}
