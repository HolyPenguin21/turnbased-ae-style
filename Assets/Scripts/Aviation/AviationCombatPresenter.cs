using System;
using System.Collections;
using System.Collections.Generic;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    // Map/UI adapter for aviation combat. Ordinary ground contact and aviation share
    // ArmyController.MoveAlong's per-step resolution; this adapter only presents requests.
    public class AviationCombatPresenter : MonoBehaviour
    {
        public static event System.Action<ArmyData> OperationCompleted;
        [SerializeField] private BattleAttackPopupUI attackPopup;
        [SerializeField] private AaChoicePopupUI aaChoicePopup;
        [SerializeField] private HexSelectionController hexSelection;

        // Kept for callers that seed the former headless path. Both adapters now use this source.
        public static System.Random HeadlessRng = new System.Random();
        public sealed class AirStrikeResult : AviationCombatResult { }

        private AviationCombatOperation CreateOperation()
        {
            var operation = new AviationCombatOperation(HeadlessRng,
                magnitudes: attackPopup != null ? attackPopup.Magnitudes : AbilityMagnitudes.Default);
            operation.StrikeCompleted += army => OperationCompleted?.Invoke(army);
            return operation;
        }

        private readonly HashSet<PresentationLifetime> _presentations = new HashSet<PresentationLifetime>();

        private void OnDisable()
        {
            foreach (var presentation in new List<PresentationLifetime>(_presentations)) presentation.Cancel();
        }

        public IEnumerator ResolveStep(ArmyData mover, HexCoord hex, ArmyController.StepResolutionOutcome outcome)
            => Present(CreateOperation().ResolveStep(mover, hex, outcome), outcome);
        public IEnumerator ResolveTurnStartAntiAir(PlayerSetupData owner)
        {
            var operation = CreateOperation();
            var presentationOutcome = new ArmyController.StepResolutionOutcome();
            var presentation = Present(operation.ResolveTurnStartAntiAir(owner), presentationOutcome);
            try { while (presentation.MoveNext()) yield return presentation.Current; }
            finally { (presentation as IDisposable)?.Dispose(); }
            // The turn owner must not confirm/start planning while mandatory AA remains. A
            // cancelled presentation falls back to the same operation's headless policy. Recollect
            // eligible pairs: accepted shots/skips stay committed; a cancelled choice is undecided.
            if (presentationOutcome.StopMovement)
                AviationCombatHeadlessAdapter.Run(operation.ResolveTurnStartAntiAir(owner));
        }

        public IEnumerator ResolveAirStrikeAtCurrentHex(ArmyData airArmy, HexCoord hex,
            AirStrikePolicy policy, AirStrikeResult result = null)
            => Present(CreateOperation().ResolveAirStrikeAtCurrentHex(airArmy, hex, policy,
                result ?? new AirStrikeResult()));

        // Unity can stop a nested movement coroutine without disposing it. The movement owner
        // therefore holds an explicit cancellation callback while this operation is suspended.
        private sealed class PresentationLifetime : IDisposable
        {
            private readonly AviationCombatPresenter _owner;
            private readonly IEnumerator<AviationCombatOperation.Request> _iterator;
            private readonly ArmyController.StepResolutionOutcome _outcome;
            public AviationCombatOperation.ChallengeRequest Challenge;
            public AviationCombatOperation.AaDecisionRequest AaDecision;
            public bool IsCancelled { get; private set; }
            private bool _disposed, _finished;
            public void Finish() => _finished = true;
            public PresentationLifetime(AviationCombatPresenter owner,
                IEnumerator<AviationCombatOperation.Request> iterator, ArmyController.StepResolutionOutcome outcome)
            {
                _owner = owner; _iterator = iterator; _outcome = outcome;
                owner._presentations.Add(this);
                if (outcome != null) outcome.CancelResolution = Cancel;
            }
            public void Cancel()
            {
                if (_disposed) return;
                IsCancelled = true;
                if (_outcome != null) { _outcome.StopMovement = true; _outcome.Cancelled = true; }
                Dispose();
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (!_finished)
                {
                    IsCancelled = true;
                    if (_outcome != null) { _outcome.StopMovement = true; _outcome.Cancelled = true; }
                }
                _owner._presentations.Remove(this);
                if (_outcome != null) _outcome.CancelResolution = null;
                AaDecision?.Cancel();
                try { _iterator.Dispose(); }
                finally
                {
                    if (Challenge != null && _owner.attackPopup != null
                        && _owner.attackPopup.PresentsSession(Challenge.Session)) _owner.attackPopup.Hide();
                    if (AaDecision != null && _owner.aaChoicePopup != null) _owner.aaChoicePopup.Hide();
                }
            }
        }

        private IEnumerator Present(IEnumerable<AviationCombatOperation.Request> requests,
            ArmyController.StepResolutionOutcome outcome = null)
        {
            var iterator = requests.GetEnumerator();
            using var lifetime = new PresentationLifetime(this, iterator, outcome);
            AviationCombatOperation.ChallengeRequest previous = null;
            while (!lifetime.IsCancelled && iterator.MoveNext())
            {
                if (previous != null) { hexSelection?.RestackArmiesOn(previous.Hex, null); previous = null; }
                if (iterator.Current is AviationCombatOperation.AaDecisionRequest aa)
                {
                    lifetime.AaDecision = aa;
                    if (aa.Reaction.AaOwner?.IsHuman == true && aaChoicePopup != null)
                    {
                        aaChoicePopup.Show(aa.Reaction.AaUnit, aa.Reaction.AirArmy,
                            onAttack: () => aa.Complete(true), onSkip: () => aa.Complete(false),
                            onCancelled: lifetime.Cancel);
                        yield return new WaitUntil(() => lifetime.IsCancelled || aa.IsCompleted
                            || aaChoicePopup == null || !aaChoicePopup.IsShowing);
                        if (!aa.IsCompleted) { lifetime.Cancel(); yield break; }
                    }
                    else aa.Complete(aa.SuggestedFire);
                    lifetime.AaDecision = null;
                }
                else if (iterator.Current is AviationCombatOperation.ChallengeRequest challenge)
                {
                    lifetime.Challenge = challenge;
                    if (attackPopup != null)
                    {
                        attackPopup.Begin(challenge.Attacker, challenge.AttackerHero, challenge.Defender,
                            challenge.DefenderHero, null, null, challenge.Complete,
                            attackerPoolSize: challenge.Session.AttackerPoolSize,
                            defenderPoolSize: challenge.Session.DefenderPoolSize,
                            tacticsCritical: challenge.TacticsCritical, challengeTitle: challenge.Title,
                            session: challenge.Session);
                        yield return new WaitUntil(() => lifetime.IsCancelled || challenge.IsCompleted
                            || challenge.Session.IsCancelled || attackPopup == null
                            || !attackPopup.PresentsSession(challenge.Session));
                        if (!challenge.IsCompleted) { lifetime.Cancel(); yield break; }
                    }
                    else AviationCombatHeadlessAdapter.Resolve(challenge);
                    previous = challenge;
                    lifetime.Challenge = null;
                }
            }
            lifetime.Finish();
            if (previous != null) hexSelection?.RestackArmiesOn(previous.Hex, null);
        }

        internal static List<(UnitData, ArmyData)> CollectStrikeTargets(List<ArmyData> armies, PlayerSetupData striker)
            => AviationCombatOperation.CollectStrikeTargets(armies, striker);
        public static bool CanExplosionStrikeAt(ArmyData airArmy, HexCoord hex)
            => AviationCombatOperation.CanExplosionStrikeAt(airArmy, hex);
        public static List<ArmyData> FindAirStrikeTargetsAt(HexCoord hex, PlayerSetupData owner)
            => AviationCombatOperation.FindAirStrikeTargetsAt(hex, owner);
        public static List<ArmyData> FindAirStrikeTargetsAt(HexCoord hex, PlayerSetupData owner, int? exactTargetArmyId)
            => AviationCombatOperation.FindAirStrikeTargetsAt(hex, owner, exactTargetArmyId);
    }
}
