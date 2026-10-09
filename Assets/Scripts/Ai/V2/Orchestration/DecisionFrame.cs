using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // The world-dependent services a decision frame calls. Production binds them to the real owners
    // (WorldAnalysis, StrategyLayer, the objective evaluators, MissionContinuityLayer, AiTurnSession,
    // DemandLayer, CombatOpportunityAnalyzer); they are a seam so the order of the refreshes and the
    // freshness protocol can be driven by tests without a world. No knowledge is added.
    internal sealed class FrameServices
    {
        internal Func<WorldSnapshot, WorldSnapshot> RefreshKnowledge;
        internal Func<WorldSnapshot, WorldAnalysis.StepObservationStamp, ExecutionResult, WorldSnapshot> ObserveSettled;
        internal Func<WorldSnapshot, IEnumerator> WarmEstimates;
        internal Func<WorldSnapshot, List<ReconObjective>> EnumerateRecon;
        internal Action<WorldSnapshot> RefreshAggressionFacts;
        internal Func<WorldSnapshot, List<RaidObjective>> EnumerateAggression;
        internal Func<WorldSnapshot, List<ReconObjective>, List<RaidObjective>, bool, List<MissionIntent>> ResolveActive;
        internal Func<List<MissionIntent>, WorldSnapshot, List<ReconObjective>, ActorCommitments> RefreshActors;
        internal Func<WorldSnapshot, List<ReconObjective>, ActorCommitments> RefreshPersistentActors;
        internal Func<WorldSnapshot, List<ReconObjective>, List<RaidObjective>, List<MissionIntent>,
            ActorCommitments, ISet<DesireAxis>, List<AxisDemand>> GenerateDemands;

        internal static FrameServices Production(AiTurnSession session, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, DesireBreakdown breakdown) => new FrameServices
        {
            RefreshKnowledge = snap => WorldAnalysis.RefreshStrategicKnowledge(snap, player, root, hand, ctx),
            ObserveSettled = (snap, stamp, result) =>
                WorldAnalysis.ObserveSettled(snap, player, root, hand, ctx, stamp, result),
            WarmEstimates = snap => CombatOpportunityAnalyzer.WarmEstimates(snap),
            EnumerateRecon = snap => ReconObjectiveEvaluator.Enumerate(snap),
            RefreshAggressionFacts = snap => StrategyLayer.RefreshAggressionOperationalFacts(snap, breakdown),
            EnumerateAggression = snap => RaidObjectiveEvaluator.Enumerate(snap, breakdown.OpportunityReport),
            // The first resolution of the turn passes the turn context; every later one does not.
            ResolveActive = (snap, recon, aggression, withContext) => withContext
                ? MissionContinuityLayer.ResolveActive(player, snap, recon, aggression, ctx)
                : MissionContinuityLayer.ResolveActive(player, snap, recon, aggression),
            RefreshActors = (intents, snap, recon) => session.RefreshActors(intents, snap, recon),
            RefreshPersistentActors = (snap, recon) =>
                session.RefreshActors(session.PersistentState.All, snap, recon),
            GenerateDemands = (snap, recon, aggression, intents, commitments, axes) =>
                DemandLayer.Generate(snap, breakdown, recon, aggression, intents, commitments, player, ctx,
                    root, axes),
        };
    }

    // ===========================================================================================
    //  THE DECISION FRAME of one AI turn: the settled snapshot and what is derived from it for the
    //  decisions of the turn (Recon / Aggression objectives, durable intents, actor claims, demands),
    //  plus the protocol that says when the derived part is current.
    //
    //  Only this type assigns those references; the work bodies of RunTurn read them and name the
    //  moment they are in (the operations below). The recipe of each moment is the one the bodies
    //  ran inline before, in the same order:
    //    · a refresh is NEVER skipped because a revision did not move: ResolveActive retires dead
    //      intents and re-keys others, RefreshActors rewrites the lease projection - neither is a
    //      pure read, and the world revision does not cover them;
    //    · WarmEstimates always runs before the operational refresh that consumes the estimates;
    //    · the ownership credit is granted only where a refresh already made the derived part
    //      current (initial changed Phase A, a changed re-admission, a changed cold residual) and
    //      is consumed by the next admission.
    //  The frame owns consistency of the view, not policy: it holds no bank, no leases, no facts
    //  registry; intents and leases stay mutable at their owners.
    // ===========================================================================================
    internal sealed class DecisionFrame
    {
        private readonly FrameServices _services;
        private readonly AiTurnContext _ctx;
        private readonly PlayerSetupData _player;
        private readonly PlayerRoot _root;
        private readonly AiHandData _hand;
        private bool _ownershipCredit;

        internal WorldSnapshot Snapshot { get; private set; }
        internal List<ReconObjective> Recon { get; private set; }
        internal List<RaidObjective> Aggression { get; private set; }
        internal List<MissionIntent> Intents { get; private set; }
        internal ActorCommitments Commitments { get; private set; }
        internal List<AxisDemand> Demands { get; private set; }
        // The ownership view Phase B and Housekeeping consume (persistent state of every intent).
        internal ActorCommitments PostCommitments { get; private set; }

        internal DecisionFrame(WorldSnapshot snapshot, AiTurnContext ctx, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, FrameServices services)
        {
            Snapshot = snapshot;
            _ctx = ctx;
            _player = player;
            _root = root;
            _hand = hand;
            _services = services;
        }

        internal static DecisionFrame Begin(WorldSnapshot snapshot, AiTurnSession session,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            DesireBreakdown breakdown) =>
            new DecisionFrame(snapshot, ctx, player, root, hand,
                FrameServices.Production(session, player, root, hand, ctx, breakdown));

        // ---- the start of the turn: separate named inputs (logs of the turn sit between them) ----

        // The ONE objective enumeration of the turn, shared by DemandLayer and the mission planners.
        internal void EnumerateObjectives()
        {
            Recon = _services.EnumerateRecon(Snapshot);
            Aggression = _services.EnumerateAggression(Snapshot);
        }

        // Mission Continuity resolves the durable intents FIRST (the only resolution that gets the
        // turn context), then the actor-claim view is built from them.
        internal void ResolveInitialOwnership()
        {
            Intents = _services.ResolveActive(Snapshot, Recon, Aggression, true);
            Commitments = _services.RefreshActors(Intents, Snapshot, Recon);
        }

        internal List<AxisDemand> GenerateDemands(ISet<DesireAxis> axes) =>
            _services.GenerateDemands(Snapshot, Recon, Aggression, Intents, Commitments, axes);

        // Demand families persist across settled admissions: the whole set is rebuilt only after the
        // first Phase A changed the world and after a changed cold residual.
        internal void RebuildDemands(ISet<DesireAxis> axes) => Demands = GenerateDemands(axes);

        // ---- the operational refresh ----

        // Warm the combat estimates for the current snapshot, then rebuild Recon / Aggression /
        // intents / actor claims from it, in that order. Does not refresh the snapshot itself.
        internal IEnumerator RefreshOperationalDecision()
        {
            yield return _services.WarmEstimates(Snapshot);
            Recon = _services.EnumerateRecon(Snapshot);
            // Rebuild the operational Aggression facts from this settled snapshot before
            // re-enumerating objectives, so a neutral destroyed by the previous step is gone.
            _services.RefreshAggressionFacts(Snapshot);
            Aggression = _services.EnumerateAggression(Snapshot);
            Intents = _services.ResolveActive(Snapshot, Recon, Aggression, false);
            Commitments = _services.RefreshActors(Intents, Snapshot, Recon);
        }

        // The first Phase A changed the settled world: refresh knowledge and the operational
        // decision (the caller regenerates the demands of every axis).
        internal IEnumerator AcceptChangedPhaseA()
        {
            Snapshot = _services.RefreshKnowledge(Snapshot);
            yield return RefreshOperationalDecision();
        }

        // A re-admission (Phase A owner) changed the world: the same refresh, and the derived part
        // is now current for the next admission, which must not refresh it again.
        internal IEnumerator AcceptChangedReentry()
        {
            Snapshot = _services.RefreshKnowledge(Snapshot);
            yield return RefreshOperationalDecision();
            _ownershipCredit = true;
        }

        // The initial credit: the first Phase A already refreshed the derived part when it changed.
        internal void StartWithCredit(bool phaseAChangedTheWorld) => _ownershipCredit = phaseAChangedTheWorld;

        // The start of an admission: every admission reads a settled world. Without a credit the
        // strategic knowledge and the operational decision are refreshed; the credit is spent.
        internal IEnumerator PrepareAdmission()
        {
            if (!_ownershipCredit)
            {
                Snapshot = _services.RefreshKnowledge(Snapshot);
                yield return RefreshOperationalDecision();
            }
            _ownershipCredit = false;
        }

        // ---- after a mutation ----

        // A settled work step: Capture -> (action) -> publish the observation delta (WorldAnalysis).
        internal void ObserveSettled(WorldAnalysis.StepObservationStamp before, ExecutionResult result) =>
            Snapshot = _services.ObserveSettled(Snapshot, before, result);

        // The re-admission demand families replace the dirty ones; the others are untouched.
        internal void ReplaceDemandFamilies(ISet<DesireAxis> dirtyAxes, IEnumerable<AxisDemand> regenerated) =>
            Demands = Demands.Where(d => d != null && !dirtyAxes.Contains(d.RequestingAxis))
                .Concat(regenerated).ToList();

        // A wing was formed: knowledge, Recon objectives, intents and claims - the partial recipe of
        // that moment (no Aggression refresh, no estimate warm-up, no turn context).
        internal void RefreshAfterFormation()
        {
            Snapshot = _services.RefreshKnowledge(Snapshot);
            Recon = _services.EnumerateRecon(Snapshot);
            Intents = _services.ResolveActive(Snapshot, Recon, Aggression, false);
            Commitments = _services.RefreshActors(Intents, Snapshot, Recon);
        }

        // ---- Phase B ----

        // Each Phase B round consumes a coherent strategic snapshot, refreshed first: knowledge,
        // Recon objectives and the ownership of every persistent intent (no ResolveActive, no warm-up).
        internal void PrepareTempoOwnership()
        {
            Snapshot = _services.RefreshKnowledge(Snapshot);
            Recon = _services.EnumerateRecon(Snapshot);
            PostCommitments = _services.RefreshPersistentActors(Snapshot, Recon);
        }

        // ---- the cold residual ----

        internal IEnumerator PrepareColdResidual()
        {
            Snapshot = _services.RefreshKnowledge(Snapshot);
            yield return RefreshOperationalDecision();
        }

        // The cold residual changed the world: observe it, refresh the decision, rebuild every
        // demand family, and credit the next admission.
        internal IEnumerator AcceptChangedCold(WorldAnalysis.StepObservationStamp before, ISet<DesireAxis> axes)
        {
            ObserveSettled(before, null);
            yield return RefreshOperationalDecision();
            RebuildDemands(axes);
            _ownershipCredit = true;
        }

        // ---- the end of the turn ----

        // Cold Phase A and the following typed admissions may have created or re-bound actors after
        // management captured its view: Housekeeping must see the latest canonical ownership.
        internal void RefreshFinalOwnership() =>
            PostCommitments = _services.RefreshPersistentActors(Snapshot, Recon);

        internal void AcceptHousekeeping() => Snapshot = _services.RefreshKnowledge(Snapshot);
    }
}
