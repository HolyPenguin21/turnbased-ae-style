using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // Lifecycle diagnostics split out of AiTurnSession: the one-line settled-step transition and the
    // end-of-turn invariant checks. Read-only over the stores it is handed; it mutates nothing, so it
    // cannot change AI behaviour. AiTurnSession keeps the lifecycle (begin/settle/audit trigger/dispose).
    internal static class LifecycleAudit
    {
        // The durable intent as the lifecycle line sees it: identity kept so a rekey is visible.
        internal readonly struct IntentTrace
        {
            internal readonly MissionIntent Intent;
            internal readonly string State;
            internal readonly int Stall;
            internal readonly int ActorCount;
            internal readonly int ResourceRows;
            internal IntentTrace(MissionIntent intent, string state, int stall, int actors, int resources)
            { Intent = intent; State = state; Stall = stall; ActorCount = actors; ResourceRows = resources; }
        }

        internal static IntentTrace Trace(MissionIntentState state, MissionLeaseBook leases, MissionIntentKey key)
        {
            state.TryGet(key, out MissionIntent intent);
            return new IntentTrace(intent,
                intent == null ? "none" : intent.Status + (intent.Status == IntentStatus.Suspended
                    ? "/" + intent.Suspended : ""),
                intent?.StallTurns ?? 0, leases.ActorsFor(key).Count, leases.ResourcesFor(key).Count);
        }

        // The single lifecycle-transition line, one per settled step, showing the whole chain:
        //   exec   = what Execution/Provisioning/Allocation reported,
        //   norm   = how the result boundary (ledger) normalized it,
        //   domain = what Continuity decided for the durable intent,
        //   claims = the lease effect. Domain continuity keeps its own detail lines.
        internal static string LogTransition(MissionIntentState state, MissionLeaseBook leases,
            MissionStepResult result, IntentTrace before)
        {
            MissionIntentKey key = result.IntentKey;
            var dirty = StrategicInvalidationReason.None;
            foreach (WorldDelta delta in result.WorldDeltas) dirty |= delta.DirtyFacts;
            string source = result.StopReason.HasValue ? "execution"
                : result.ProvisionFailureKindValue.HasValue ? "provisioning"
                : result.AllocationDeferReason.HasValue ? "deferral" : "none";
            // The step may have created the durable intent, kept it, rekeyed it or retired it.
            bool alive = before.Intent != null
                ? state.All.Contains(before.Intent)
                : state.TryGet(key, out _);
            IntentTrace after = alive
                ? Trace(state, leases, before.Intent != null ? before.Intent.IntentKey : key) : default;
            string fate = before.Intent == null ? (alive ? "created" : "none")
                : !alive ? "retired"
                : before.Intent.IntentKey.Equals(key) ? "kept" : "rekeyed:" + before.Intent.IntentKey;
            string newState = alive ? after.State : (before.Intent == null ? "none" : "retired");
            string payloads = (result.GetPayload<ReconStepPayload>() != null ? "recon," : "")
                + (result.GetPayload<RaidStepPayload>() != null ? "raid," : "")
                + (result.GetPayload<AttackStepPayload>() != null ? "attack," : "")
                + (result.GetPayload<ActiveDefenceStepPayload>() != null ? "defence," : "")
                + (result.GetPayload<EconomyStepPayload>() != null ? "economy," : "")
                + (result.GetPayload<DevelopmentStepPayload>() != null ? "development," : "")
                + (result.GetPayload<GroundCombatStepPayload>() != null ? "ground," : "");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string line = ($"[AI][V2][Lifecycle] operation={key} kind={key.Kind}"
                + $" | exec stop={result.StopReason?.ToString() ?? "-"}"
                + $" prov={result.ProvisionFailureKindValue?.ToString() ?? "-"}"
                + $" defer={result.AllocationDeferReason?.ToString() ?? "-"}"
                + $" progress={(result.MadeProgress ? 1 : 0)} moved={result.StepsMoved}"
                + $" ap={result.ApSpent.ToString("0.##", inv)} rev={result.StateVersionAfter}"
                + $" sat={(result.ObjectiveSatisfied ? 1 : 0)}/{(result.ObjectiveSatisfiedExternally ? 1 : 0)}"
                + $" mover={(result.MoverArmyId.HasValue ? result.MoverArmyId.Value.ToString() : "-")}"
                + $" payload=[{payloads.TrimEnd(',')}]"
                + $" | norm src={source} result={result.Disposition}"
                + $" | domain intent={fate} old={before.State} new={newState}"
                + $" stall={before.Stall}>{(alive ? after.Stall : 0)}"
                + $" | claims actors={before.ActorCount}>{leases.ActorsFor(key).Count}"
                + $" [{string.Join(",", leases.ActorsFor(key))}]"
                + $" resources={before.ResourceRows}>{leases.ResourcesFor(key).Count}"
                + $" dirty={dirty}");
            // A repeated identical transition for the same operation (a failed attempt retried by
            // later cycles) is written once.
            AiDebugLog.WriteDeduped("lifecycle:" + key, line);
            return line;
        }

        internal static void CollectViolations(WorldSnapshot snapshot, IReadOnlyList<ReconObjective> objectives,
            List<string> violations, ref int checkedOperations, MissionIntentState persistentState,
            MissionLeaseBook leases, IEnumerable<MissionIntent> lastActorIntents,
            IEnumerable<int> progressReceipts, int revisionAtBegin, int commitsAtBegin)
        {
            int bumps = WorldDeltaLifecycle.Current - revisionAtBegin;
            int commits = WorldDeltaLifecycle.CommitEvents - commitsAtBegin;
            // 1. Every operation that owns actor claims still has a durable intent (else a retired
            //    operation leaked its claim), and every tracked operation's claims equal the
            //    detached derivation of the same intent.
            foreach (MissionIntentKey op in leases.Operations())
                if (!persistentState.TryGet(op, out _))
                    violations.Add($"claim owner {op} has no durable intent; actors=[{string.Join(",", leases.ActorsFor(op))}]");
            if (snapshot?.Self?.Armies != null)
                foreach (MissionIntent intent in lastActorIntents)
                {
                    if (intent == null || !persistentState.TryGet(intent.IntentKey, out MissionIntent live)
                        || !ReferenceEquals(live, intent))
                        continue;
                    checkedOperations++;
                    var derived = new HashSet<int>(MissionActorPolicy.Build(new[] { intent }, snapshot,
                        objectives).ClaimedArmyIdSet);
                    var table = new HashSet<int>(leases.ActorsFor(intent.IntentKey));
                    if (!derived.SetEquals(table))
                        violations.Add($"claim table != derived view for {intent.IntentKey}: "
                            + $"table=[{string.Join(",", table.OrderBy(x => x))}] "
                            + $"derived=[{string.Join(",", derived.OrderBy(x => x))}]");
                }

            // 2. Revision: advances equal commit events, no transaction is left open, and every
            //    step that claimed progress carries a receipt from this turn that is not in the future.
            if (bumps != commits)
                violations.Add($"revision advanced {bumps} time(s) but {commits} mutation(s) committed");
            if (WorldDeltaLifecycle.TransactionOpen)
                violations.Add("a world mutation transaction is still open at turn end");
            foreach (int receipt in progressReceipts)
            {
                if (receipt <= revisionAtBegin)
                    violations.Add($"a step reported progress with receipt {receipt}, which does not advance the "
                        + $"turn-start revision {revisionAtBegin} (missed stamp)");
                else if (receipt > WorldDeltaLifecycle.Current)
                    violations.Add($"a step carries receipt {receipt} newer than the current revision "
                        + $"{WorldDeltaLifecycle.Current}");
            }
        }
    }
}
