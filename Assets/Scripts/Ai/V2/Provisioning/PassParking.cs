using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  The parking of one admission pass: missions the provisioning of THIS pass found out of the
    //  running for the rest of the turn (ProvisionDisposition.RetryNextTurn - ResourceAllocator,
    //  covers MoverContended and NoExecutableStep alike).
    //
    //  Why it exists: nothing enforced that verdict across settled steps, so the next step's mission
    //  set re-proposed the same losing job and re-ran the full batch solve to reach the identical
    //  rejection. A failure is parked LIVE, at the moment it is seen (never read back from
    //  ProvisioningSession.AssignmentRejections after the step settles - SetAssignment clears and
    //  refills that dictionary on every pass, so by then it holds only the last pass's leftovers).
    //  It is consumed only by the NEXT settled admission's Filter, so the remaining reallocation
    //  passes of the step that parked it still see the full candidate set.
    //
    //  Scope: one admission pass (the loop opens a new PassParking with every pass). The turn-wide
    //  memory is CapabilityPoolExhaustionRegistry (State), which Park also feeds and Filter also
    //  asks; this type owns only the pass set and the filter. It cannot execute, spend or rewrite
    //  intents.
    // ===========================================================================================
    internal sealed class PassParking
    {
        internal const string RetryNextTurnReason = "retry_next_turn_after_provision_failure";

        private readonly HashSet<StableMissionKey> _parkedThisPass = new HashSet<StableMissionKey>();

        // A RetryNextTurn failure was seen: remember it for this pass and for the rest of the turn.
        internal void Park(PlayerSetupData player, MissionProposal mission)
        {
            _parkedThisPass.Add(StableMissionKey.For(mission));
            CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(player, mission);
        }

        internal bool IsParked(StableMissionKey key) => _parkedThisPass.Contains(key);

        // The proposals of the next settled admission without the parked ones. A durable leg
        // (a Raid's fresh-decision return fallback at tier None still belongs to a funded intent)
        // dropped here gets a reason, otherwise BindFunding warns about an unexplained loss.
        // The input list instance is returned unchanged when there is nothing to filter.
        internal PassParkingResult Filter(List<MissionProposal> missions, WorldSnapshot snapshot,
            PlayerSetupData player)
        {
            var deferrals = new Dictionary<MissionIntentKey, string>();
            if (missions == null || missions.Count == 0)
                return new PassParkingResult(missions, deferrals);

            var retained = new List<MissionProposal>(missions.Count);
            foreach (MissionProposal mission in missions)
            {
                // The set is per admission; the registry carries the same verdict to a later
                // admission of this turn while it still provably holds.
                if (mission != null
                    && (_parkedThisPass.Contains(StableMissionKey.For(mission))
                        || CapabilityPoolExhaustionRegistry.ShouldSkipRetried(player, mission, snapshot)))
                {
                    if (mission.FromDurableIntent)
                        deferrals[MissionIntentKey.For(mission)] = RetryNextTurnReason;
                    continue;
                }
                retained.Add(mission);
            }
            return new PassParkingResult(retained, deferrals);
        }
    }

    internal readonly struct PassParkingResult
    {
        internal readonly List<MissionProposal> Retained;
        // Durable legs that were parked, with the reason BindFunding reports for them.
        internal readonly IReadOnlyDictionary<MissionIntentKey, string> Deferrals;

        internal PassParkingResult(List<MissionProposal> retained,
            IReadOnlyDictionary<MissionIntentKey, string> deferrals)
        {
            Retained = retained;
            Deferrals = deferrals;
        }
    }
}
