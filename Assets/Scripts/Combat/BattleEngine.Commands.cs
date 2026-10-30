using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    public enum BattleCommandPhase { Deployment, RoundReady, Acting, AwaitingAttack, AwaitingCounterattack, AwaitingAdvance, RetreatReady, Completed }

    public sealed class BattleAttackRequest
    {
        internal BattleEngine Engine;
        internal bool IsExplosion;
        public UnitData Attacker { get; internal set; }
        public UnitData Target { get; internal set; }
        public bool IsCounterattack { get; internal set; }
    }

    // Projection/receipt for one admitted action. The accepted roll and collateral selection
    // belong to this request; redrawing the result never makes a second random choice.
    public sealed class BattleAttackPlan
    {
        internal BattleAttackRequest Request;
        internal BattleChallengeRollResult Roll;
        internal UnitData AttackerHero, DefenderHero;
        internal List<(UnitData Victim, string Skill)> Secondary;
        internal bool Applied;
        public UnitData Attacker => Request.Attacker;
        public UnitData Target => Request.Target;
        public bool IsCounterattack => Request.IsCounterattack;
        public UnitData Victim { get; internal set; }
        public UnitData InterceptedBy { get; internal set; }
        public BattleRules.GroundAttackOutcome Outcome { get; internal set; }
        public int Damage { get; internal set; }
        public IReadOnlyList<string> AppliedAbilities { get; internal set; }
        public IReadOnlyList<string> Effects { get; internal set; }
    }

    public sealed partial class BattleEngine
    {
        public BattleCommandPhase Phase { get; private set; } = BattleCommandPhase.Deployment;
        public UnitData CurrentActor => _actingUnit;
        public AbilityMagnitudes Magnitudes => _rules.Magnitudes;
        public BattleRulesSnapshot Rules => _rules;
        internal bool[] RollChallengeTokens(int count) => BattleSimulationKernel.RollTokens(count, _random);
        private readonly BattleRulesSnapshot _rules;
        private readonly HashSet<ArmyData> _summonedArmies = new HashSet<ArmyData>();
        private UnitData _actingUnit, _pendingCounter, _counterTarget;
        private BattleAttackRequest _attackRequest;
        private BattleAttackPlan _activeAttackPlan;
        private ExplosionAttack _activeExplosion;
        private BattleCaptureKillSequence _captureSequence;
        private bool _battleCompleted, _retreatCompleted;
        private BattleRetreatApplication _retreatResult;

        private bool CanAct(UnitData unit) => !_state.FinalizationApplied && Phase == BattleCommandPhase.Acting
            && unit != null && unit == _actingUnit && unit.HitPointsCurrent > 0 && unit.IsGroundCombatant
            && !unit.IsPrisoner && _state.OwningArmy(unit) != null && _state.OwningArmy(unit) != _state.RetreatingArmy;

        public bool TryPass(UnitData actor)
        {
            if (!CanAct(actor)) return false;
            Phase = BattleCommandPhase.AwaitingAdvance;
            return true;
        }

        public bool TryBeginAttack(UnitData actor, UnitData target, bool isCounterattack = false)
            => TryBeginAttack(actor, target, out _, isCounterattack);

        public bool TryBeginAttack(UnitData actor, UnitData target, out BattleAttackRequest request, bool isCounterattack = false)
        {
            request = null;
            if (_state.FinalizationApplied) return false;
            if (isCounterattack)
            {
                if (Phase != BattleCommandPhase.AwaitingCounterattack || actor != _pendingCounter || target != _counterTarget
                    || actor == null || target == null || actor.HitPointsCurrent <= 0 || target.HitPointsCurrent <= 0) return false;
                _pendingCounter = _counterTarget = null;
            }
            else if (!CanGroundAttack(actor, target)) return false;
            _attackRequest = request = new BattleAttackRequest { Engine = this, Attacker = actor, Target = target,
                IsCounterattack = isCounterattack, IsExplosion = ExplosionAttack.TargetLimit(actor.Abilities) > 0 };
            _activeAttackPlan = null; _activeExplosion = null;
            _plannedSecondaryTargets = null;
            _plannedSecondaryAttacker = _plannedSecondaryVictim = null;
            Phase = BattleCommandPhase.AwaitingAttack;
            if (!isCounterattack) CommitGroundAttack(actor);
            return true;
        }

        public ExplosionAttack CreateExplosionAttack(UnitData actor)
        {
            if (Phase != BattleCommandPhase.AwaitingAttack || _attackRequest?.Attacker != actor
                || !_attackRequest.IsExplosion || _activeAttackPlan != null) return null;
            return _activeExplosion ?? (_activeExplosion = new ExplosionAttack(this, actor, _state.BattleHex, _random, _state.Attacker, _state.Defender));
        }
        internal bool OwnsExplosion(ExplosionAttack attack) => Phase == BattleCommandPhase.AwaitingAttack
            && !_state.FinalizationApplied && ReferenceEquals(attack, _activeExplosion);
        public bool CompleteExplosionAction(UnitData actor)
        {
            if (Phase != BattleCommandPhase.AwaitingAttack || _attackRequest?.Attacker != actor
                || _activeExplosion == null || !_activeExplosion.IsCompleted) return false;
            Phase = BattleCommandPhase.AwaitingAdvance;
            return true;
        }

        public BattleAttackPlan PrepareGroundAttack(BattleAttackRequest request, BattleChallengeRollResult roll,
            UnitData attackerHero = null, UnitData defenderHero = null)
            => PrepareGroundAttackCore(request, roll, attackerHero, defenderHero, null);

        public BattleAttackPlan PrepareGroundAttack(UnitData attacker, UnitData defender, BattleChallengeRollResult roll,
            UnitData attackerHero = null, UnitData defenderHero = null, bool isCounterattack = false)
        {
            BattleAttackRequest request = AdmitOrGetAttack(attacker, defender, isCounterattack);
            return PrepareGroundAttack(request, roll, attackerHero, defenderHero);
        }
        private BattleAttackRequest AdmitOrGetAttack(UnitData attacker, UnitData defender, bool isCounterattack)
        {
            if (Phase == BattleCommandPhase.AwaitingAttack)
                return _attackRequest != null && _attackRequest.Attacker == attacker && _attackRequest.Target == defender
                    && _attackRequest.IsCounterattack == isCounterattack ? _attackRequest : null;
            return TryBeginAttack(attacker, defender, out var request, isCounterattack) ? request : null;
        }

        private BattleAttackPlan PrepareGroundAttackCore(BattleAttackRequest request, BattleChallengeRollResult roll,
            UnitData attackerHero, UnitData defenderHero, int? fixedDamage)
        {
            if (request == null || request.Engine != this || request != _attackRequest
                || request.IsExplosion || Phase != BattleCommandPhase.AwaitingAttack || _activeExplosion != null) return null;
            if (_activeAttackPlan != null) return _activeAttackPlan;
            roll = new BattleChallengeRollResult((bool[])(roll.AttackerTokens ?? Array.Empty<bool>()).Clone(),
                (bool[])(roll.DefenderTokens ?? Array.Empty<bool>()).Clone(), roll.AttackerFateRemaining,
                roll.DefenderFateRemaining, roll.AttackerModifier, roll.DefenderModifier);
            var attacker = request.Attacker; var defender = request.Target;
            var outcome = fixedDamage.HasValue
                ? new BattleRules.GroundAttackOutcome(fixedDamage.Value, 0, fixedDamage.Value, fixedDamage.Value, Array.Empty<string>())
                : BattleRules.ResolveGroundAttack(roll.AttackerTokens, roll.DefenderTokens, attacker, defender,
                    _magnitudes, roll.AttackerModifier, roll.DefenderModifier);
            var guard = !request.IsCounterattack && outcome.Damage > 0 ? FindInterceptingBodyguard(attacker, defender) : null;
            var victim = guard ?? defender;
            int damage = outcome.Damage;
            var abilities = new List<string>(outcome.AppliedAbilities);
            if (guard != null)
            {
                damage = ChallengeResult.ApplyAbilityModifiers(outcome.RawDamage, attacker, guard, _magnitudes, out abilities);
                abilities.Add(UnitAbilities.Bodyguard);
            }
            var effects = PreviewGroundHitEffects(attacker, victim, damage, request.IsCounterattack);
            return _activeAttackPlan = new BattleAttackPlan { Request = request, Roll = roll, AttackerHero = attackerHero,
                DefenderHero = defenderHero, Victim = victim, InterceptedBy = guard, Outcome = outcome, Damage = damage,
                Secondary = _plannedSecondaryTargets != null ? new List<(UnitData, string)>(_plannedSecondaryTargets) : new List<(UnitData, string)>(),
                AppliedAbilities = abilities.AsReadOnly(), Effects = new List<string>(effects).AsReadOnly() };
        }

        public BattleAttackApplication ApplyAttackPlan(BattleAttackPlan plan)
        {
            if (plan == null || plan != _activeAttackPlan || plan.Request != _attackRequest || plan.Applied
                || _state.FinalizationApplied || Phase != BattleCommandPhase.AwaitingAttack) return new BattleAttackApplication();
            plan.Applied = true;
            using var mutation = VisionSystem.BeginMutation();
            ApplyFateState(plan.AttackerHero, plan.DefenderHero, plan.Roll);
            if (plan.InterceptedBy != null) _reactions.BodyguardsUsed.Add(plan.InterceptedBy);
            _plannedSecondaryAttacker = plan.Attacker; _plannedSecondaryVictim = plan.Victim;
            _plannedSecondaryDamage = plan.Damage; _plannedSecondaryTargets = plan.Secondary;
            var result = ApplyGroundHit(plan.Attacker, plan.Victim, plan.Damage);
            result.Outcome = plan.Outcome; result.InterceptedBy = plan.InterceptedBy;
            bool mayCounter = !plan.IsCounterattack && !result.DefenderDied && _state.OwningArmy(plan.Victim) != _state.RetreatingArmy;
            result.ShockTriggered |= mayCounter && BattleSimulationKernel.SuppressesTurn(result.Damage, plan.Attacker.Abilities)
                && BattleRules.CanCounterattack(_state.Grid, plan.Victim, plan.Attacker, plan.Victim.HitPointsCurrent, 0);
            if (mayCounter && BattleRules.CanCounterattack(_state.Grid, plan.Victim, plan.Attacker, plan.Victim.HitPointsCurrent, result.Damage))
                result.Counterattacker = plan.Victim;
            ExplosionAttack.CompleteLauncher(plan.Attacker, this);
            if (plan.Attacker.HitPointsCurrent <= 0) result.Counterattacker = null;
            if (result.Counterattacker != null)
            {
                _pendingCounter = result.Counterattacker; _counterTarget = plan.Attacker;
                Phase = BattleCommandPhase.AwaitingCounterattack;
            }
            else Phase = BattleCommandPhase.AwaitingAdvance;
            return result;
        }

        // Even a caller that never asked EvaluatePostAction must resolve the losing heroes.
        internal bool CanFinalize
        {
            get
            {
                if (Phase == BattleCommandPhase.Completed) return true;
                if (Phase == BattleCommandPhase.AwaitingCounterattack || Phase == BattleCommandPhase.AwaitingAttack) return false;
                var status = EvaluateBattleEnd();
                return status.Ended && !CreateCaptureKillSequence(status.AttackerAlive, status.DefenderAlive).HasPending;
            }
        }
    }
}
