using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RESERVATION INVARIANTS  (log-only safety net)
    // ===========================================================================================
    //  The AI turn interleaves decisions and spending: Phase A re-enters after every settled step,
    //  so a hold can be eaten on the N-th re-entry by a path no single diff review traces. These
    //  checks run at settled step boundaries and name the step that broke a rule:
    //
    //    · CommittedHoldUncovered — the claims that must be honoured THIS turn exceed the physical
    //      stock: every TurnResourceBook claim except EconomyDeferred (ledger rows plus the derived
    //      air-recovery and operation-continuation claims). EconomyDeferred is excluded on purpose:
    //      it holds a build's full cost while the stock is still being saved up, so it may
    //      legitimately exceed it. Reported once when a resource BECOMES uncovered, labelled with
    //      the step that did it.
    //    · SpendExceededSpendable — one action with no spend authority (a Phase B tempo action)
    //      spent more of a resource than was spendable for it immediately before it ran.
    //    · DeferredApHold / NonPositiveHold — rows the ledger itself must never keep.
    //    · LeakAtTurnEnd — a row survived the turn (StrategicResourceReservationLedger.AssertClearAtTurnEnd).
    //
    //  Violations are logged as [AI][V2][INVARIANT] and recorded per player and turn. Nothing
    //  throws and no decision reads them.
    // ===========================================================================================
    internal enum ReservationInvariantRule
    {
        CommittedHoldUncovered,
        SpendExceededSpendable,
        DeferredApHold,
        NonPositiveHold,
        LeakAtTurnEnd,
    }

    internal sealed class ReservationInvariantViolation
    {
        public int Turn;
        public ReservationInvariantRule Rule;
        public StrategicReservedResource? Resource;
        public string Label;
        public string Detail;

        public override string ToString() =>
            $"T{Turn} {Rule}{(Resource.HasValue ? " " + Resource.Value : "")} after '{Label}': {Detail}";
    }

    internal static class ReservationInvariants
    {
        internal const float Epsilon = 0.01f;

        private static readonly StrategicReservedResource[] AllResources =
            (StrategicReservedResource[])System.Enum.GetValues(typeof(StrategicReservedResource));

        private sealed class Entry
        {
            public int Turn;
            public int Checks;
            public readonly bool[] Uncovered = new bool[AllResources.Length];
            public readonly List<ReservationInvariantViolation> Violations =
                new List<ReservationInvariantViolation>();
        }

        private static readonly Dictionary<PlayerSetupData, Entry> ByPlayer =
            new Dictionary<PlayerSetupData, Entry>();

        // Match-start reset (CitadelSetupController), alongside the other V2 registries.
        internal static void ClearAll() => ByPlayer.Clear();

        // A claim that must be honoured this turn. A kind added later counts as committed until
        // it is explicitly classified otherwise.
        internal static bool IsCommitted(ResourceClaimKind kind) =>
            kind != ResourceClaimKind.EconomyDeferred;

        // ---- pure rules (vectors are indexed by (int)StrategicReservedResource) ----------------

        internal static float[] CommittedHeld(IEnumerable<ResourceClaim> claims)
        {
            var held = new float[AllResources.Length];
            foreach (ResourceClaim c in claims ?? Enumerable.Empty<ResourceClaim>())
                if (IsCommitted(c.Kind))
                    held[(int)c.Resource] += Mathf.Max(0f, c.Amount);
            return held;
        }

        internal static List<StrategicReservedResource> Uncovered(float[] held, float[] physical)
        {
            var result = new List<StrategicReservedResource>();
            foreach (StrategicReservedResource res in AllResources)
                if (held[(int)res] > physical[(int)res] + Epsilon)
                    result.Add(res);
            return result;
        }

        internal static List<StrategicReservedResource> Overspent(float[] before, float[] after,
            float[] spendableBefore)
        {
            var result = new List<StrategicReservedResource>();
            foreach (StrategicReservedResource res in AllResources)
            {
                int i = (int)res;
                if (before[i] - after[i] > spendableBefore[i] + Epsilon)
                    result.Add(res);
            }
            return result;
        }

        internal static List<(ReservationInvariantRule Rule, StrategicResourceReservation Row)> Structural(
            IEnumerable<StrategicResourceReservation> rows)
        {
            var result = new List<(ReservationInvariantRule, StrategicResourceReservation)>();
            foreach (StrategicResourceReservation r in rows ?? Enumerable.Empty<StrategicResourceReservation>())
            {
                if (r == null)
                    continue;
                if (r.Amount <= 0f)
                    result.Add((ReservationInvariantRule.NonPositiveHold, r));
                else if (r.Reason == StrategicReservationReason.EconomyDeferredBuild
                         && r.Resource == StrategicReservedResource.ActionPoints)
                    result.Add((ReservationInvariantRule.DeferredApHold, r));
            }
            return result;
        }

        // ---- live checks ------------------------------------------------------------------------

        internal static float[] Physical(PlayerRoot root)
        {
            var v = new float[AllResources.Length];
            if (root == null)
                return v;
            foreach (StrategicReservedResource res in AllResources)
                v[(int)res] = TurnResourceBook.Physical(root, res);
            return v;
        }

        // Spendable with no spend authority — what an ordinary action may draw on.
        internal static float[] Spendable(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            var v = new float[AllResources.Length];
            v[(int)StrategicReservedResource.ActionPoints] =
                StrategicSpendability.SpendableAp(player, root, ctx);
            foreach (ResourceType t in ResourceBundle.All)
                v[(int)StrategicResourceReservationLedger.Map(t)] =
                    StrategicSpendability.SpendableAmount(player, root, ctx, t);
            return v;
        }

        // A settled step boundary: the ledger is structurally sound and every committed hold is
        // still covered by the physical stock.
        internal static void CheckBoundary(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, string label)
        {
            if (player == null || root == null || ctx == null)
                return;
            int turn = ctx.TurnNumber;
            Entry e = GetOrReset(player, turn);
            e.Checks++;

            IReadOnlyList<StrategicResourceReservation> rows =
                StrategicResourceReservationLedger.Rows(player, turn);
            foreach ((ReservationInvariantRule rule, StrategicResourceReservation row) in Structural(rows))
                Report(player, turn, rule, row.Resource, label, row.ToString());

            List<ResourceClaim> claims = TurnResourceBook.Claims(player, root, ctx);
            float[] physical = Physical(root);
            float[] held = CommittedHeld(claims);
            List<StrategicReservedResource> uncovered = Uncovered(held, physical);
            foreach (StrategicReservedResource res in AllResources)
            {
                bool now = uncovered.Contains(res);
                bool was = e.Uncovered[(int)res];
                e.Uncovered[(int)res] = now;
                if (!now || was)
                    continue;
                Report(player, turn, ReservationInvariantRule.CommittedHoldUncovered, res, label,
                    $"committed {F(held[(int)res])} > stock {F(physical[(int)res])} (claims ["
                    + string.Join(", ", claims.Where(c => c.Resource == res)) + "])");
            }
        }

        // Physical stock + no-authority spendable, captured immediately before one action.
        internal readonly struct SpendProbe
        {
            public readonly string Label;
            public readonly float[] Before;
            public readonly float[] SpendableBefore;

            public SpendProbe(string label, float[] before, float[] spendableBefore)
            {
                Label = label;
                Before = before;
                SpendableBefore = spendableBefore;
            }
        }

        internal static SpendProbe BeginSpend(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, string label)
        {
            if (player == null || root == null || ctx == null)
                return default;
            return new SpendProbe(label, Physical(root), Spendable(player, root, ctx));
        }

        // The action ran with no spend authority, so it may not have spent into anyone's hold.
        internal static void EndSpend(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            SpendProbe probe)
        {
            if (player == null || root == null || ctx == null || probe.Before == null)
                return;
            int turn = ctx.TurnNumber;
            GetOrReset(player, turn).Checks++;
            float[] after = Physical(root);
            foreach (StrategicReservedResource res in Overspent(probe.Before, after, probe.SpendableBefore))
            {
                int i = (int)res;
                Report(player, turn, ReservationInvariantRule.SpendExceededSpendable, res, probe.Label,
                    $"spent {F(probe.Before[i] - after[i])} > spendable {F(probe.SpendableBefore[i])}"
                    + $" (stock {F(probe.Before[i])} -> {F(after[i])};"
                    + $" {StrategicResourceReservationLedger.DebugLine(player, turn)})");
            }
        }

        internal static void RecordLeakAtTurnEnd(PlayerSetupData player, int turn, string rows) =>
            Report(player, turn, ReservationInvariantRule.LeakAtTurnEnd, null, "turn end", rows);

        internal static IReadOnlyList<ReservationInvariantViolation> Violations(
            PlayerSetupData player, int turn) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e) && e.Turn == turn
                ? e.Violations
                : (IReadOnlyList<ReservationInvariantViolation>)System.Array.Empty<ReservationInvariantViolation>();

        internal static void LogTurnSummary(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return;
            string summary = $"[AI][V2][INVARIANT] turn {turn} {player.Nickname}: checks={e.Checks} "
                + $"violations={e.Violations.Count}"
                + (e.Violations.Count == 0 ? "" : " ["
                    + string.Join(", ", e.Violations.GroupBy(v => v.Rule)
                        .Select(g => $"{g.Key}×{g.Count()}")) + "]");
            if (e.Violations.Count == 0)
                AiDebugLog.WriteVerbose(summary);
            else
                AiDebugLog.Write(summary);
        }

        private static void Report(PlayerSetupData player, int turn, ReservationInvariantRule rule,
            StrategicReservedResource? resource, string label, string detail)
        {
            var v = new ReservationInvariantViolation
            {
                Turn = turn, Rule = rule, Resource = resource, Label = label, Detail = detail,
            };
            GetOrReset(player, turn).Violations.Add(v);
            AiDebugLog.Write($"[AI][V2][INVARIANT] {player?.Nickname}: {v}");
        }

        private static Entry GetOrReset(PlayerSetupData player, int turn)
        {
            if (!ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
            {
                e = new Entry { Turn = turn };
                ByPlayer[player] = e;
            }
            return e;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
