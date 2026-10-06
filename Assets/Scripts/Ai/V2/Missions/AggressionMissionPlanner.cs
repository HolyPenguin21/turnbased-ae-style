using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Combat;
using Game.Aviation;

namespace Game.Ai.V2
{
    internal static partial class AggressionMissionLayer
    {
        public static List<MissionProposal> Propose(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<RaidObjective> frozenObjectives, AiTurnContext ctx = null,
            IDictionary<MissionIntentKey, string> deferredThisPass = null)
        {
            var proposals = new List<MissionProposal>();
            if (snap?.Self == null || breakdown == null)
            {
                ResourceStarvationRegistry.ReplaceOperationalForecast(snap?.Observer,
                    snap?.TurnNumber ?? 0, "combat-air", ReconAirEnergyPolicy.TaskResourceForecast(snap, proposals));
                return proposals;
            }

            IReadOnlyList<RaidObjective> objectives = frozenObjectives
                ?? RaidObjectiveEvaluator.Enumerate(snap, breakdown.OpportunityReport);

            // Fresh Raid scoring must use the same actor-ownership view Provisioning enforces.
            // Otherwise a busy Economy/Recon/Raid actor can make a fresh Raid look executable and cheap,
            // only to be rejected later by the batch assignment. Incumbents are allowed to keep their
            // own pinned mover below; every other durable actor remains excluded from host/donor selection.
            // Only Active intents contribute a real claim — a retired/cancelled intent's mover is free.
            ISet<int> committed = ActorCommitments.FromIntents(
                activeIntents?.Where(i => i != null && i.Status == IntentStatus.Active),
                snap, null).ClaimedArmyIdSet;
            AppendRaid(snap, breakdown, activeIntents, objectives, committed, proposals, deferredThisPass);
            AppendActiveDefence(snap, activeIntents, committed, proposals, deferredThisPass);
            // ATK §40 — Attack is a peer lane of the same Aggression planner, appended through the
            // same one entry point; it is never orchestrated separately (§81).
            AppendAttack(snap, activeIntents, committed, proposals, ctx, deferredThisPass);
            ResourceStarvationRegistry.ReplaceOperationalForecast(snap?.Observer,
                    snap?.TurnNumber ?? 0, "combat-air", ReconAirEnergyPolicy.TaskResourceForecast(snap, proposals));
            return proposals;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
