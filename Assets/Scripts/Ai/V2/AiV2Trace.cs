using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI V2 TRACE  (debuggability pass — correlation IDs + runtime invariant diagnostics)
    // ===========================================================================================
    //  NOT a second log file. Every line here is formatted and handed to the existing
    //  AiDebugLog.Write (persistent Logs/AiDebug.log, auto-tagged with the calling
    //  Script.Method:Line — the caller info is forwarded through, so a [CHECK] line still points
    //  at the pipeline method that raised it, not at this file). Two jobs:
    //
    //   1. CORRELATION IDS. A readable, deterministic id for
    //        · every planning/execution scope  — T{turn}-P{colorIndex}-M / -R1 / -R2
    //        · every AxisDemand                 — {scope}-D01, D02, …   (rides on AxisDemand.TraceId)
    //        · every MissionProposal attempt    — {scope}-M01, M02, …   (rides on MissionProposal.AttemptId)
    //      The attempt id travels ON the proposal, so it follows the pipeline through FundedEntry
    //      → ProvisionedMission → ExecutionResult → MissionOutcomeLedger → MissionTurnOutcome →
    //      MissionContinuity with no extra plumbing. grep one attempt id => the whole lifecycle.
    //      P{colorIndex} is always present so several AI players never share a scope id; the
    //      spec's shorter "T12-M" examples are the single-AI reading of the same scheme.
    //
    //   2. RUNTIME INVARIANT DIAGNOSTICS — [AI][V2][{corr}][CHECK][OK|WARN|ERROR] lines on the
    //      pipeline boundaries. ERROR = a broken INTERNAL contract (AP desync, a claim over its
    //      funded envelope, a Phase-A ledger/physical mismatch, a failed-infra rollback leak, a
    //      duplicate StableMissionKey across two logical attempts, an execution with no
    //      registered proposal). An ordinary gameplay outcome (NoMoverExists, InsufficientBudget,
    //      TargetSatisfied, …) is NEVER a [CHECK][ERROR].
    //
    //  Fail-safe: nothing here mutates game state, the snapshot, or the order of AI decisions, and
    //  a formatting / logging failure can never take down the AI turn (same contract as
    //  AiDebugLog — every write is wrapped).
    // ===========================================================================================

    // One planning/execution scope (Main, or a bounded reaction round) for one AI player on one
    // turn. Hands out the monotonic per-scope demand / mission-attempt ids.
    public sealed class V2TraceScope
    {
        public readonly string Id;
        private int _demandSeq;
        private int _missionSeq;

        internal V2TraceScope(string id) { Id = string.IsNullOrEmpty(id) ? "T?-P?-?" : id; }

        public string NextDemandId() =>
            $"{Id}-D{(++_demandSeq).ToString("00", CultureInfo.InvariantCulture)}";

        public string NextMissionAttemptId() =>
            $"{Id}-M{(++_missionSeq).ToString("00", CultureInfo.InvariantCulture)}";
    }

    // A physical resource total, captured for the [STATE] control lines (spec §2.7) and the
    // failed-infrastructure rollback check (spec §2.4). Integer rounded — these are control
    // totals, not an accounting formula.
    public readonly struct V2ResourceStamp
    {
        public readonly int Ap, Human, Energy, Materials, Tech;
        public readonly bool Valid;

        public V2ResourceStamp(int ap, int human, int energy, int materials, int tech)
        {
            Ap = ap; Human = human; Energy = energy; Materials = materials; Tech = tech; Valid = true;
        }

        public bool SameAs(V2ResourceStamp o) =>
            Ap == o.Ap && Human == o.Human && Energy == o.Energy
            && Materials == o.Materials && Tech == o.Tech;

        public string Transition(V2ResourceStamp end) =>
            $"AP {Ap}→{end.Ap} H {Human}→{end.Human} E {Energy}→{end.Energy} "
            + $"M {Materials}→{end.Materials} T {Tech}→{end.Tech}";
    }

    // Independent snapshot of the CONTROLLED game state a failed infrastructure op must not have
    // touched (spec §2.4). Deliberately NOT derived from InfraFulfillResult / StateChanged — the
    // whole point is to check the system with evidence it did not produce itself. Owned-building
    // count + filled facility-slot count catch a leaked Base / extraction facility; the army
    // movement sum catches a hero whose move was spent by a half-applied build.
    public readonly struct V2InfraWorldStamp
    {
        public readonly bool Valid;
        public readonly int OwnedBuildings;
        public readonly int FilledFacilitySlots;
        public readonly int ArmyMovementSum;
        public readonly V2ResourceStamp Resources;

        public V2InfraWorldStamp(int ownedBuildings, int filledSlots, int armyMovementSum, V2ResourceStamp res)
        {
            Valid = true;
            OwnedBuildings = ownedBuildings;
            FilledFacilitySlots = filledSlots;
            ArmyMovementSum = armyMovementSum;
            Resources = res;
        }

        public bool SameAs(V2InfraWorldStamp o) =>
            OwnedBuildings == o.OwnedBuildings && FilledFacilitySlots == o.FilledFacilitySlots
            && ArmyMovementSum == o.ArmyMovementSum && Resources.SameAs(o.Resources);

        public string Diff(V2InfraWorldStamp end) =>
            $"{Resources.Transition(end.Resources)} buildings {OwnedBuildings}→{end.OwnedBuildings} "
            + $"facilitySlots {FilledFacilitySlots}→{end.FilledFacilitySlots} "
            + $"armyMove {ArmyMovementSum}→{end.ArmyMovementSum}";
    }

    public static class AiV2Trace
    {
        private static readonly Dictionary<PlayerSetupData, V2TraceScope> Scopes =
            new Dictionary<PlayerSetupData, V2TraceScope>();

        // Wired next to the other V2 per-player registry resets in CitadelSetupController.
        public static void Clear() => Scopes.Clear();

        private static string PlayerTag(PlayerSetupData p) =>
            p == null ? "P?" : "P" + p.ColorIndex.ToString(CultureInfo.InvariantCulture);

        public static V2TraceScope BeginMain(PlayerSetupData player, int turn)
        {
            var scope = new V2TraceScope($"T{turn}-{PlayerTag(player)}-M");
            if (player != null) Scopes[player] = scope;
            return scope;
        }

        // roundZeroBased 0 -> "R1", 1 -> "R2" (StrategicReactionPass numbers its rounds from 0).
        public static V2TraceScope BeginReaction(PlayerSetupData player, int turn, int roundZeroBased)
        {
            var scope = new V2TraceScope($"T{turn}-{PlayerTag(player)}-R{roundZeroBased + 1}");
            if (player != null) Scopes[player] = scope;
            return scope;
        }

        public static V2TraceScope CurrentScope(PlayerSetupData player) =>
            player != null && Scopes.TryGetValue(player, out V2TraceScope s) ? s : null;

        public static string CurrentId(PlayerSetupData player) => CurrentScope(player)?.Id ?? "T?-P?-?";

        // -----------------------------------------------------------------------------------------
        //  Demand -> Mission causal link  (spec §1.6)
        // -----------------------------------------------------------------------------------------
        //  CONSERVATIVE and best-effort. A demand is linked ONLY when a mission's own target hex
        //  exactly matches that demand's TargetHex for a compatible capability/kind — i.e. the
        //  capability shortage it reported was blocking this exact operation. Never inferred from
        //  the shared axis. The link is a SET, not a 1:1: a raid can be gated by both a Hero and a
        //  FieldCombatPower shortage, and mislabelling it "D01 → Raid" (dropping D02) is worse
        //  than "[D01,D02]". Every unmatched mission stays at "none". Demand and Mission are still
        //  enumerated independently — this only annotates a link that already exists.
        public static void CorrelateDemandsToMissions(IReadOnlyList<AxisDemand> demands,
            IReadOnlyList<MissionProposal> missions)
        {
            if (demands == null || missions == null) return;
            foreach (MissionProposal m in missions)
            {
                if (m == null) continue;
                m.CauseDemandTraceIds.Clear();
                if (!TryMissionFocus(m, out HexCoord focus))
                    continue;
                foreach (AxisDemand d in demands)
                {
                    if (d == null || string.IsNullOrEmpty(d.TraceId) || d.TargetHex == null)
                        continue;
                    if (!DemandFitsMissionKind(d, m))
                        continue;
                    if (d.TargetHex.Value.Equals(focus) && !m.CauseDemandTraceIds.Contains(d.TraceId))
                        m.CauseDemandTraceIds.Add(d.TraceId);
                }
            }
        }

        private static bool TryMissionFocus(MissionProposal m, out HexCoord focus)
        {
            focus = default;
            if (m == null) return false;
            if (m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget st) { focus = st.FocusHex; return true; }
            if (m.Kind == MissionKind.Raid && m.Target is RaidMissionTarget rt) { focus = rt.LastKnownHex; return true; }
            return false;
        }

        private static bool DemandFitsMissionKind(AxisDemand d, MissionProposal m)
        {
            switch (m.Kind)
            {
                case MissionKind.Scout: return d.Capability == CapabilityKind.ScoutCapability;
                case MissionKind.Raid:
                    return d.Capability == CapabilityKind.FieldCombatPower
                        || d.Capability == CapabilityKind.Hero;
                default: return false;
            }
        }

        // -----------------------------------------------------------------------------------------
        //  Resource stamps + the end-of-scope [STATE] control line  (spec §2.7)
        // -----------------------------------------------------------------------------------------
        public static V2ResourceStamp Stamp(PlayerRoot root)
        {
            if (root == null) return default;
            return new V2ResourceStamp(
                root.ActionPoints,
                Res(root, ResourceType.Human),
                Res(root, ResourceType.Energy),
                Res(root, ResourceType.Materials),
                Res(root, ResourceType.Tech));
        }

        private static int Res(PlayerRoot root, ResourceType t) => Mathf.RoundToInt(root.GetResource(t));

        // Independent controlled-state snapshot for the failed-infrastructure rollback check.
        public static V2InfraWorldStamp InfraStamp(PlayerSetupData player, PlayerRoot root)
        {
            if (player == null) return default;
            int buildings = 0, slots = 0, movement = 0;
            foreach (BuildingData b in BuildingRegistry.AllBuildings())
            {
                if (b == null || b.Owner != player) continue;
                buildings++;
                if (b.FacilitySlots != null)
                    for (int i = 0; i < b.FacilitySlots.Length; i++)
                        if (b.FacilitySlots[i] != null) slots++;
            }
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
                if (a != null) movement += Mathf.Max(0, a.CurrentMovement);
            return new V2InfraWorldStamp(buildings, slots, movement, Stamp(root));
        }

        public static void LogState(string scopeId, V2ResourceStamp start, V2ResourceStamp end,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0)
        {
            if (!start.Valid || !end.Valid) return;
            Write($"[AI][V2][{scopeId}][STATE] {start.Transition(end)}", cf, cm, cl);
        }

        // -----------------------------------------------------------------------------------------
        //  Invariant checks. `corrId` is the tightest available id — a MissionAttemptId, a
        //  DemandTraceId, or (failing both) a scope id.
        // -----------------------------------------------------------------------------------------
        private const float Eps = 0.05f;

        public static void CheckOk(string corrId, string tag, string detail,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0) =>
            Check("OK", corrId, tag, detail, cf, cm, cl);

        public static void CheckWarn(string corrId, string tag, string detail,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0) =>
            Check("WARN", corrId, tag, detail, cf, cm, cl);

        public static void CheckError(string corrId, string tag, string detail,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0) =>
            Check("ERROR", corrId, tag, detail, cf, cm, cl);

        // §2.1 — the real AP delta at execution must equal ExecutionResult.ApSpent.
        public static void CheckExecutionAp(string attemptId, int apBefore, int apAfter, float reportedSpent,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0)
        {
            float physicalDelta = apBefore - apAfter;
            string detail = $"before={apBefore} after={apAfter} "
                + $"physicalDelta={Num(physicalDelta)} reported={Num(reportedSpent)}";
            if (Mathf.Abs(physicalDelta - reportedSpent) > Eps)
                Check("ERROR", attemptId, "ExecutionAPMismatch", detail, cf, cm, cl);
            else
                Check("OK", attemptId, "ExecutionAP", detail, cf, cm, cl);
        }

        // §2.2 — a provisioned claim must land inside its funded envelope.
        public static void CheckProvisionEnvelope(string attemptId, float claimedAp, float grantedAp,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0)
        {
            string detail = $"claimed={Num(claimedAp)} granted={Num(grantedAp)}";
            if (claimedAp < -Eps || claimedAp > grantedAp + Eps)
                Check("ERROR", attemptId, "ProvisionClaimExceedsEnvelope", detail, cf, cm, cl);
            else
                Check("OK", attemptId, "ProvisionEnvelope", detail, cf, cm, cl);
        }

        // §2.3 — a committed Phase-A action: three INDEPENDENTLY sourced facts must agree —
        //  physicalDelta (root AP before/after), reportedSpend (the action's own ApSpent), and
        //  axisDebit (the REAL AxisBudgetLedger.Balance drop for the requesting axis, measured by
        //  the caller around ledger.Debit and BEFORE any discrete follow-up borrow). Catches a
        //  missing Debit, a Debit to the wrong axis, or a Debit of the wrong amount.
        //  AxisBudgetLedger owns Phase-A entitlement/spend only; this is NOT compared against any
        //  later mission execution spend (that is ResourceAllocator / _lockedClaims territory).
        public static void CheckPhaseAAp(string demandTraceId, DesireAxis axis,
            float physicalDelta, float reportedSpend, float axisDebit,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0)
        {
            string detail = $"physicalDelta={Num(physicalDelta)} reported={Num(reportedSpend)} "
                + $"axisDebit={Num(axisDebit)} axis={DesireAxes.Abbrev(axis)}";
            if (Mathf.Abs(physicalDelta - reportedSpend) > Eps || Mathf.Abs(reportedSpend - axisDebit) > Eps)
                Check("ERROR", demandTraceId, "PhaseAApMismatch", detail, cf, cm, cl);
            else
                Check("OK", demandTraceId, "PhaseAApAccounting", detail, cf, cm, cl);
        }

        // §2.4 — a FAILED infrastructure op must have left the controlled game state untouched.
        //  The `before`/`after` stamps are the INDEPENDENT evidence (building count, filled
        //  facility slots, army movement, resources); `stateChanged` is only OR'd in as an extra
        //  trigger, never trusted as proof of a clean rollback on its own.
        public static void CheckInfrastructureRollback(string demandTraceId, bool built, bool stateChanged,
            V2InfraWorldStamp before, V2InfraWorldStamp after,
            [CallerFilePath] string cf = "", [CallerMemberName] string cm = "", [CallerLineNumber] int cl = 0)
        {
            if (built) return;                              // success path is not this invariant
            if (!before.Valid || !after.Valid) return;
            string detail = $"{before.Diff(after)} stateChanged={(stateChanged ? 1 : 0)}";
            if (!before.SameAs(after) || stateChanged)
                Check("ERROR", demandTraceId, "InfrastructureRollbackLeak", detail, cf, cm, cl);
            else
                Check("OK", demandTraceId, "InfrastructureRollbackClean", detail, cf, cm, cl);
        }

        // -----------------------------------------------------------------------------------------
        private static void Check(string level, string corrId, string tag, string detail,
            string cf, string cm, int cl)
        {
            string id = string.IsNullOrEmpty(corrId) ? "" : $"[{corrId}]";
            string body = string.IsNullOrEmpty(detail) ? tag : tag + " " + detail;
            Write($"[AI][V2]{id}[CHECK][{level}] {body}", cf, cm, cl);
        }

        private static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static void Write(string line, string cf, string cm, int cl)
        {
            try { AiDebugLog.Write(line, cf, cm, cl); }
            catch { /* a logging failure must never break the AI turn */ }
        }
    }
}
